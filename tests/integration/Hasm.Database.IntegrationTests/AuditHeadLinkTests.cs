using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Audit head integrity, slice 1 (option (a) of docs/AUDIT-HEAD-INTEGRITY-PLAN.md; PROVISIONAL migration numbers) on the SYSTEM audit table: every inserted row must continue the chain of its stream (sequence 1 and
/// the genesis hash on an empty stream, otherwise the head's sequence plus one and the head's hash), whoever inserts it and with every trigger enabled. The genuine writer is unaffected; a gap, a replay, an unlinked
/// or random previous hash and a wrong genesis are refused with <c>audit_link_violation</c> and nothing is written; concurrent inserts at one sequence leave exactly one row; the SQL genesis expression equals
/// <c>AuditCanonical.GenesisHash</c> byte for byte for the system, a company access and a business stream; and the stated LIMIT is pinned: a well-linked junk head (bad MAC) is still accepted, after which the
/// writer refuses (<c>audit_head_corrupt</c>), so this guard narrows the shape of damage and does not restore availability by itself.
/// </summary>
public class AuditHeadLinkTests : AuditTestBase
{
    private string Conn => Options.BuildSystemConnectionString(admin: true);
    private static readonly InMemoryAuditKeyRing Attacker = new("k1");                                          // the key id is known, the secret is not

    private static AuditRecord Forge(AuditRecord head, long seq, byte[] prev, string detail = "forged") =>
        AuditCanonical.Seal(head with { Seq = seq, EventId = Guid.NewGuid(), Detail = detail, PrevHash = prev, Hash = [], Mac = [] }, Attacker.GetActive());

    private async Task Refused(AuditRecord forged)
    {
        var before = await EventCount();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertRawEnforced(forged));
        Assert.Equal("P0001", ex.SqlState);
        Assert.Equal("audit_link_violation", ex.MessageText);
        Assert.Equal(before, await EventCount());                                                                // nothing was written
    }

    [PostgresFact]
    public async Task The_genuine_writer_is_unaffected_and_the_guard_refuses_every_row_that_does_not_continue_the_chain()
    {
        var (_, _, stream) = await Scenario();
        var events = await Events(stream);
        AssertVerified(await Verify(stream), events.Count);                                                       // the AuditTrail wrote 6 linked records through the new trigger
        var head = events[^1];

        await Refused(Forge(head, head.Seq + 2, head.Hash));                                                      // a gap
        await Refused(Forge(head, head.Seq, head.Hash));                                                          // a duplicate sequence
        await Refused(Forge(head, 2, events[0].Hash));                                                            // a replay at an old sequence
        await Refused(Forge(head, head.Seq + 1, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))); // a random link
        await Refused(Forge(head, head.Seq + 1, events[^2].Hash));                                                // the link of an older record, not the head's
        await Refused(Forge(head, head.Seq + 1, AuditCanonical.GenesisHash(stream.Key)));                         // the genesis hash on a stream that has a head
        await Refused(Forge(head, 1, AuditCanonical.GenesisHash(stream.Key)));                                    // a second record 1
        var moved = Forge(head, head.Seq + 1, head.Hash) with { Hash = new byte[31] };                           // a well-linked row with a hash of the wrong length: the table's own check refuses it
        await Assert.ThrowsAsync<PostgresException>(() => InsertRawEnforced(moved));

        Assert.Equal(events.Count, (await Events(stream)).Count);
        AssertVerified(await Verify(stream), events.Count);                                                       // the chain is exactly as it was
        // a new genuine event is still appended normally afterwards
        await BackupAudit.WriteAsync(Conn, Audit, BackupAuditEvents.RetentionEvaluated, "backup retention evaluated operator=os:t");
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.StreamKey} = 'system' AND {Ev.EventType} = {BackupAuditEvents.RetentionEvaluated}"));
    }

    [PostgresFact]
    public async Task An_empty_stream_accepts_only_sequence_one_with_the_genesis_hash()
    {
        var (company, _, _) = await Scenario(roles: 0);
        var other = await NewCompany();                                                                          // a company whose access stream is still empty
        var stream = AuditStream.ForCompany(other);
        Assert.Equal(0L, await EventCount(stream));
        var template = (await Events(AuditStream.ForCompany(company)))[0] with { StreamKey = stream.Key, CompanyId = other, ActorUserId = null, TargetUserId = null, SubjectId = null };
        await Refused(Forge(template, 1, new byte[32]));                                                          // a wrong genesis
        await Refused(Forge(template, 1, AuditCanonical.GenesisHash("system")));                                  // another stream's genesis
        await Refused(Forge(template, 2, AuditCanonical.GenesisHash(stream.Key)));                                // not sequence 1
        await InsertRawEnforced(Forge(template, 1, AuditCanonical.GenesisHash(stream.Key)));                       // the right first row passes the guard (its MAC is the verifier's business)
        Assert.Equal(1L, await EventCount(stream));
    }

    [PostgresFact]
    public async Task A_well_linked_junk_head_is_still_accepted_the_stated_limit_and_the_writer_then_refuses()
    {
        await StartAccess();
        await BackupAudit.WriteAsync(Conn, Audit, BackupAuditEvents.RetentionEvaluated, "backup retention evaluated operator=os:t");
        var head = (await Events(AuditStream.System))[^1];
        await InsertRawEnforced(Forge(head, head.Seq + 1, head.Hash, "junk"));                                    // linked to the head, bad MAC: nothing in the database can tell
        var junk = (await Events(AuditStream.System))[^1];
        Assert.Equal(head.Seq + 1, junk.Seq);
        Assert.Equal(RecordCheck.MacInvalid, AuditVerifier.CheckRecord(junk, AuditKeys));
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => BackupAudit.WriteAsync(Conn, Audit, BackupAuditEvents.RetentionEvaluated, "backup retention evaluated operator=os:t"));
        Assert.Equal("audit_head_corrupt", ex.Code);                                                              // the availability gap of the plan remains: this guard does not close it
    }

    [PostgresFact]
    public async Task Concurrent_inserts_at_one_sequence_leave_exactly_one_row()
    {
        var (_, _, stream) = await Scenario(roles: 1);
        var head = (await Events(stream))[^1];
        var before = await EventCount(stream);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            try { await InsertRawEnforced(Forge(head, head.Seq + 1, head.Hash, "racer " + i)); return true; }
            catch (PostgresException) { return false; }
        }));
        Assert.Single(results, ok => ok);
        Assert.Equal(before + 1, await EventCount(stream));
    }

    [PostgresFact]
    public async Task The_sql_genesis_expression_equals_the_canonical_genesis_hash_for_every_stream_kind_and_the_append_only_guard_remains()
    {
        var (company, _, stream) = await Scenario(roles: 0);
        foreach (var key in new[] { AuditStream.SystemKey, stream.Key, AuditStream.ForBusiness(company).Key })
        {
            var sql = await Scalar<byte[]>("SELECT sha256(convert_to('HASM-AUDIT-GENESIS-v1', 'UTF8') || decode('00', 'hex') || convert_to(@s, 'UTF8'))", ("s", key));
            Assert.Equal(AuditCanonical.GenesisHash(key), sql);
        }
        // UPDATE and DELETE are still refused (the first audit migration's guard is untouched), and the table gained exactly the link guard
        var first = (await Events(stream))[0];
        var update = await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 1", ("s", stream.Key)));
        Assert.Equal("audit events are append-only", update.MessageText);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s", ("s", stream.Key)));
        Assert.Equal("audit events are append-only", delete.MessageText);
        Assert.Equal(first.Hash, (await Events(stream))[0].Hash);
        var triggers = await Names($"SELECT tgname FROM pg_trigger WHERE tgrelid = '{Ev.Table}'::regclass AND NOT tgisinternal");
        Assert.Equal(new[] { Ev.GuardTrigger, SystemSchema.AuditHeadLink.TrSystemLink }.Order(StringComparer.Ordinal), triggers.Order(StringComparer.Ordinal));
    }

    private async Task<List<string>> Names(string sql)
    {
        await using var conn = new NpgsqlConnection(Conn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }
}
