using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Company-business cryptographic audit against real PostgreSQL: a business change and its authenticated event share ONE transaction
/// in the company database. The single disposable test principal is the table owner here, which lets the tests play an attacker with
/// direct database access (the keyed chain and the external checkpoints are what must detect that). Per-role privilege checks live in
/// <see cref="CompanyRuntimeIsolationTests"/>.
/// </summary>
public class CompanyBusinessAuditTests : CompanyRuntimeTestBase
{
    private const short Posted = 1000;

    private sealed record Setup(CompanyRecord Company, CompanyDatabaseResolver Resolver, CompanyAuditedTransactions Tx, AuditStream Stream);

    private async Task<Setup> Prepare(AuditTrail? trail = null)
    {
        await StartCompanies();
        return await NewCompanyWithLedger(trail);
    }

    private async Task<Setup> NewCompanyWithLedger(AuditTrail? trail = null)
    {
        var c = await ReadyCompany();
        await InCompany(c.PhysicalDatabaseName, "CREATE TABLE zz_ledger (id int PRIMARY KEY, note text)");
        var resolver = SinglePrincipalResolver(c);
        return new Setup(c, resolver, new CompanyAuditedTransactions(resolver, trail ?? Audit, Clock), AuditStream.ForBusiness(c.Id));
    }

    private static Task Post(Setup s, int id, short eventType = Posted, Func<CompanyAuditedWork, Task>? afterAudit = null) =>
        s.Tx.ExecuteAsync(s.Company.Id, async w =>
        {
            await using (var cmd = new NpgsqlCommand("INSERT INTO zz_ledger (id, note) VALUES (@i, @n)", w.Connection, w.Transaction))
            {
                cmd.Parameters.AddWithValue("i", id);
                cmd.Parameters.AddWithValue("n", "entry " + id);
                await cmd.ExecuteNonQueryAsync();
            }
            await w.AppendAuditAsync(new AuditEventData(eventType, SubjectId: Guid.NewGuid(), Detail: "post " + id));
            if (afterAudit is not null) await afterAudit(w);
        });

    private AuditVerifier VerifierFor(Setup s, ICheckpointStore? store = null) =>
        new(Options.BuildCompanyAdminConnectionString(s.Company.PhysicalDatabaseName), AuditKeys, store ?? AuditCheckpoints);

    private async Task<List<AuditRecord>> Events(Setup s)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(s.Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {AuditTableMap.Business.SelectColumns} FROM {B.Table} ORDER BY {B.Seq}", conn);
        var list = new List<AuditRecord>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(AuditStorage.Read(r));
        return list;
    }

    private Task<long> Rows(Setup s) => InCompany<long>(s.Company.PhysicalDatabaseName, "SELECT count(*) FROM zz_ledger");

    private Task Tamper(Setup s, string sql) => InCompany(s.Company.PhysicalDatabaseName,
        $"ALTER TABLE {B.Table} DISABLE TRIGGER {B.GuardTrigger}; {sql}; ALTER TABLE {B.Table} ENABLE TRIGGER {B.GuardTrigger}");

    private static string[] Codes(AuditVerificationReport r) => [.. r.Findings.Select(f => f.Code).Distinct()];

    private static void AssertVerified(AuditVerificationReport r, long events)
    {
        Assert.True(r.Verdict == AuditVerdict.Verified || r.Findings.All(f => f.Severity == FindingSeverity.Note),
            $"expected Verified but was {r.Verdict}: {string.Join(", ", r.Findings.Select(f => $"{f.Code}@{f.Seq}"))}");
        Assert.Equal(events, r.Events);
        Assert.Equal(events, r.VerifiedThroughSeq);
        Assert.Equal(0, r.UnanchoredEvents);
    }

    // ---- atomic commit ---------------------------------------------------------------------

    [PostgresFact]
    public async Task A_business_change_and_its_authenticated_event_commit_together_and_verify()
    {
        var s = await Prepare();
        var systemEventsBefore = await Scalar<long>($"SELECT count(*) FROM {Ev.Table}");

        await Post(s, 1);
        await Post(s, 2);

        Assert.Equal(2L, await Rows(s));
        var events = await Events(s);
        Assert.Equal([1L, 2L], events.Select(e => e.Seq));
        Assert.All(events, e =>
        {
            Assert.Equal(s.Stream.Key, e.StreamKey);
            Assert.Equal(s.Company.Id, e.CompanyId);
            Assert.Equal(Posted, e.EventType);
            Assert.Equal(1, e.FormatVersion);
            Assert.Null(e.LegacyRef);
            Assert.Equal(32, e.Mac.Length);
        });
        Assert.Equal(AuditCanonical.GenesisHash(s.Stream.Key), events[0].PrevHash);
        Assert.Equal(events[0].Hash, events[1].PrevHash);
        AssertVerified(await VerifierFor(s).VerifyAsync(s.Stream), 2);
        Assert.Equal(systemEventsBefore, await Scalar<long>($"SELECT count(*) FROM {Ev.Table}"));      // the system audit table is untouched
    }

    // ---- rollback --------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_failure_after_the_audit_append_rolls_back_the_change_and_the_event()
    {
        var s = await Prepare();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(s, 1, afterAudit: _ => throw new InvalidOperationException("business rule failed")));
        Assert.Equal(0L, await Rows(s));
        Assert.Empty(await Events(s));
        Assert.Null(await AuditCheckpoints.ReadLastAsync(s.Stream.Key));                // nothing was anchored for an event that never existed

        await Post(s, 1);                                                                // the stream starts cleanly at sequence 1
        Assert.Equal([1L], (await Events(s)).Select(e => e.Seq));
    }

    [PostgresFact]
    public async Task A_mutation_without_an_audit_event_is_refused_and_rolled_back()
    {
        var s = await Prepare();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Tx.ExecuteAsync(s.Company.Id, async w =>
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO zz_ledger VALUES (1, 'unaudited')", w.Connection, w.Transaction);
            await cmd.ExecuteNonQueryAsync();
        }));
        Assert.Equal(0L, await Rows(s));
    }

    [PostgresFact]
    public async Task A_business_statement_failing_after_the_event_leaves_no_gap_in_the_chain()
    {
        var s = await Prepare();
        await Post(s, 1);
        await Assert.ThrowsAsync<PostgresException>(() => Post(s, 1));                  // primary-key violation after the audit append
        Assert.Equal(1L, await Rows(s));
        Assert.Single(await Events(s));
        await Post(s, 2);
        Assert.Equal([1L, 2L], (await Events(s)).Select(e => e.Seq));
        AssertVerified(await VerifierFor(s).VerifyAsync(s.Stream), 2);
    }

    [PostgresFact]
    public async Task When_the_audit_cannot_be_written_the_business_change_is_not_committed()
    {
        var s = await Prepare();
        AuditKeys.Unavailable = true;
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Post(s, 1));
        AuditKeys.Unavailable = false;
        Assert.Equal(0L, await Rows(s));
        Assert.Empty(await Events(s));
        await Post(s, 1);
        Assert.Equal(1L, await Rows(s));
    }

    [PostgresFact]
    public async Task Business_events_use_the_reserved_range_only()
    {
        var s = await Prepare();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Post(s, 1, eventType: AuditEventTypes.MembershipGranted));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Post(s, 1, eventType: 999));
        Assert.Equal(0L, await Rows(s));
        Assert.Empty(await Events(s));
    }

    [PostgresFact]
    public async Task A_checkpoint_failure_after_commit_blocks_the_next_mutation_until_the_head_is_anchored()
    {
        var s = await Prepare();
        await Post(s, 1);
        AuditCheckpoints.FailAppend = true;
        await Post(s, 2);                                                                // commits; its anchor fails (logged, never undone)
        Assert.Equal(2L, await Rows(s));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => Post(s, 3));  // refuses until event 2 is anchored
        Assert.Equal("audit_checkpoint_unavailable", ex.Code);
        Assert.Equal(2L, await Rows(s));

        AuditCheckpoints.FailAppend = false;
        await Post(s, 3);
        AssertVerified(await VerifierFor(s).VerifyAsync(s.Stream), 3);
    }

    // ---- concurrency -----------------------------------------------------------------------

    [PostgresFact]
    public async Task Concurrent_appends_keep_the_chain_gap_free_and_verifiable()
    {
        var s = await Prepare();
        await Post(s, 1);                                                                // anchored baseline

        await Task.WhenAll(Enumerable.Range(2, 12).Select(i => Post(s, i)));

        Assert.Equal(13L, await Rows(s));
        var events = await Events(s);
        Assert.Equal(Enumerable.Range(1, 13).Select(i => (long)i), events.Select(e => e.Seq));
        for (var i = 1; i < events.Count; i++) Assert.Equal(events[i - 1].Hash, events[i].PrevHash);
        AssertVerified(await VerifierFor(s).VerifyAsync(s.Stream), 13);
    }

    // ---- truncation, missing baseline, tampering ---------------------------------------------

    [PostgresFact]
    public async Task Deleting_the_tail_of_the_stream_is_detected_and_blocks_further_appends()
    {
        var s = await Prepare();
        for (var i = 1; i <= 5; i++) await Post(s, i);
        await Tamper(s, $"DELETE FROM {B.Table} WHERE {B.Seq} >= 4");                   // an attacker with database access removes the newest events

        var report = await VerifierFor(s).VerifyAsync(s.Stream);
        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains("tail_truncated", Codes(report));

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Post(s, 6));
        Assert.Equal("audit_stream_truncated", ex.Code);
        Assert.Equal(5L, await Rows(s));                                                 // the refused mutation was not committed
    }

    [PostgresFact]
    public async Task Losing_the_checkpoints_fails_closed_until_a_person_establishes_a_baseline()
    {
        var s = await Prepare();
        for (var i = 1; i <= 3; i++) await Post(s, i);

        var lost = new InMemoryCheckpointStore();                                       // the external evidence was lost or replaced
        var trail = new AuditTrail(new AuditServices(AuditKeys, lost, 0), Clock);
        var tx = new CompanyAuditedTransactions(s.Resolver, trail, Clock);
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => tx.ExecuteAsync(s.Company.Id, w => w.AppendAuditAsync(new AuditEventData(Posted))));
        Assert.Equal("audit_baseline_missing", ex.Code);

        var report = await VerifierFor(s, lost).VerifyAsync(s.Stream);
        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);
        Assert.Contains("no_checkpoints", Codes(report));

        var refused = await Assert.ThrowsAsync<AuditIntegrityException>(() => VerifierFor(s, lost).AnchorHeadAsync(s.Stream, Clock));
        Assert.Equal("audit_baseline_requires_confirmation", refused.Code);
        Assert.Equal(3L, await VerifierFor(s, lost).AnchorHeadAsync(s.Stream, Clock, establishBaseline: true));
        await tx.ExecuteAsync(s.Company.Id, w => w.AppendAuditAsync(new AuditEventData(Posted)));
        Assert.Equal(4, (await Events(s)).Count);
    }

    [PostgresFact]
    public async Task Edited_or_removed_events_are_detected()
    {
        var s = await Prepare();
        for (var i = 1; i <= 4; i++) await Post(s, i);

        await Tamper(s, $"UPDATE {B.Table} SET {B.Detail} = 'rewritten' WHERE {B.Seq} = 2");
        var edited = await VerifierFor(s).VerifyAsync(s.Stream);
        Assert.Equal(AuditVerdict.Failed, edited.Verdict);
        Assert.Contains("hash_mismatch", Codes(edited));
        Assert.Equal(1, edited.VerifiedThroughSeq);

        await Tamper(s, $"DELETE FROM {B.Table} WHERE {B.Seq} = 3");
        var removed = await VerifierFor(s).VerifyAsync(s.Stream);
        Assert.Contains("seq_gap", Codes(removed));
    }

    // ---- append-only and binding enforced by PostgreSQL -------------------------------------------

    [PostgresFact]
    public async Task The_table_refuses_update_delete_and_truncate_even_for_its_owner()
    {
        var s = await Prepare();
        await Post(s, 1);
        foreach (var sql in new[] { $"UPDATE {B.Table} SET {B.Detail} = 'x'", $"DELETE FROM {B.Table}", $"TRUNCATE {B.Table}" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => InCompany(s.Company.PhysicalDatabaseName, sql));
            Assert.Equal("P0001", ex.SqlState);
            Assert.Contains("append-only", ex.MessageText);
        }
        Assert.Single(await Events(s));
    }

    [PostgresFact]
    public async Task The_table_only_accepts_well_formed_events_of_its_own_company_and_stream()
    {
        var s = await Prepare();
        var good = (await PostAndRead(s));
        string Insert(Guid company, string stream, short type) =>
            $"INSERT INTO {B.Table} ({AuditTableMap.Business.SelectColumns.Replace("NULL::bigint, ", "")}) VALUES ('{stream}', 99, gen_random_uuid(), now(), {type}, '{company}', NULL, NULL, NULL, NULL, 1, 'k1', '\\x{Convert.ToHexString(good.Hash)}', '\\x{Convert.ToHexString(good.Hash)}', '\\x{Convert.ToHexString(good.Hash)}')";
        Assert.Equal(1L, await InCompany<long>(s.Company.PhysicalDatabaseName, $"SELECT count(*) FROM {B.Table}"));

        var other = Guid.NewGuid();
        // An event of another company, even with a consistent stream key, is refused by the identity-binding trigger.
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => InCompany(s.Company.PhysicalDatabaseName, Insert(other, "business:" + other.ToString("N"), 1000)));
        Assert.Equal("P0001", foreign.SqlState);
        // A stream key that is not this row's business stream (e.g. the access stream) violates the table's check constraint.
        var wrongStream = await Assert.ThrowsAsync<PostgresException>(() => InCompany(s.Company.PhysicalDatabaseName, Insert(s.Company.Id, "company:" + s.Company.Id.ToString("N"), 1000)));
        Assert.Equal("23514", wrongStream.SqlState);
        // Event types below 1000 are not business events.
        var lowType = await Assert.ThrowsAsync<PostgresException>(() => InCompany(s.Company.PhysicalDatabaseName, Insert(s.Company.Id, s.Stream.Key, 5)));
        Assert.Equal("23514", lowType.SqlState);
        Assert.Single(await Events(s));
    }

    private async Task<AuditRecord> PostAndRead(Setup s)
    {
        await Post(s, 1);
        return (await Events(s)).Single();
    }

    // ---- stream independence ---------------------------------------------------------------

    [PostgresFact]
    public async Task Each_company_has_its_own_independent_stream_in_its_own_database()
    {
        var a = await Prepare();
        var b = await NewCompanyWithLedger();
        await Post(a, 1);
        await Post(a, 2);
        await Post(b, 1);

        Assert.Equal([1L, 2L], (await Events(a)).Select(e => e.Seq));
        Assert.Equal([1L], (await Events(b)).Select(e => e.Seq));
        Assert.NotEqual(a.Stream.Key, b.Stream.Key);
        Assert.Equal(2L, (await AuditCheckpoints.ReadLastAsync(a.Stream.Key))!.Seq);
        Assert.Equal(1L, (await AuditCheckpoints.ReadLastAsync(b.Stream.Key))!.Seq);
        AssertVerified(await VerifierFor(a).VerifyAsync(a.Stream), 2);
        AssertVerified(await VerifierFor(b).VerifyAsync(b.Stream), 1);
        // A verifier pointed at company A's stream name cannot be satisfied from company B's database.
        var crossed = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(b.Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(a.Stream);
        Assert.Equal(0, crossed.Events);
        Assert.Equal(AuditVerdict.Failed, crossed.Verdict);                              // the protected checkpoints of A expect records that are not there
        // Business events never enter the system audit table, and the company access stream is unaffected.
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.StreamKey} LIKE 'business:%'"));
        var access = await new AuditVerifier(Options.BuildSystemConnectionString(admin: true), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForCompany(a.Company.Id));
        Assert.Equal(0, access.Events);
        Assert.DoesNotContain(access.Findings, f => f.Severity == FindingSeverity.Failure);
    }

    [PostgresFact]
    public async Task Existing_system_audit_streams_keep_working_next_to_business_streams()
    {
        var s = await Prepare();
        var user = await NewUser("auditor");                                              // writes into the system stream (identity events)
        await Post(s, 1);
        var system = await new AuditVerifier(Options.BuildSystemConnectionString(admin: true), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.System);
        Assert.True(system.Events >= 1);
        Assert.DoesNotContain(system.Findings, f => f.Severity == FindingSeverity.Failure);
        Assert.Equal(system.Events, system.VerifiedThroughSeq);
        Assert.NotEqual(Guid.Empty, user.Id);
        AssertVerified(await VerifierFor(s).VerifyAsync(s.Stream), 1);
    }
}
