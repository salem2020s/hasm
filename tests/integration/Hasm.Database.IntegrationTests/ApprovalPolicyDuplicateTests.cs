using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using AP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicy;
using AS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicyStage;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using PI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicyIdentity;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// A semantically identical ACTIVE approval policy is refused (owner decision of 2026-11-21; company migration 0033): by the service with 409 <c>approval_policy_duplicate</c> and by PostgreSQL for every writer,
/// under concurrency. Policies that differ in branch, source, minimum amount, a condition or the stages stay allowed. Real PostgreSQL; disposable cluster only.
/// </summary>
public class ApprovalPolicyDuplicateTests : JournalTestBase
{
    private static ApprovalPolicyInput P(string source = "any", decimal min = 0m, int? required = 1, Guid? branch = null, string? jtype = null, bool? rate = null, bool? soft = null, string? closing = null,
        (int Required, Guid? Role)[]? stages = null, string en = "Policy") =>
        new(Doc, source, branch, min, stages is null ? required : null, "سياسة", en, jtype, rate, soft, stages is null ? null : [.. stages.Select(s => new ApprovalStageInput(s.Required, s.Role))], closing);

    private static ApprovalPolicyDetailsInput D(string source = "any", decimal min = 0m, int? required = 1, Guid? branch = null, string en = "Policy") => new(source, branch, min, required, "سياسة", en);

    private static void Duplicate<T>(ConfigResult<T> r) => Assert.Equal((ConfigStatus.Conflict, ApprovalPolicyService.DuplicateCode), (r.Status, r.Code));

    private Task<long> Policies_() => Count(Company, AP.Table);
    private Task<long> AuditRows() => Count(Company, Audit_.Table);

    /// <summary>Statements in ONE transaction on the administrative connection; the SQLSTATE and constraint of the failure (at a statement or at COMMIT), or null.</summary>
    private async Task<(string? State, string? Constraint)> Direct(CompanyRecord company, params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return (null, null);
        }
        catch (PostgresException ex) { return (ex.SqlState, ex.ConstraintName); }
    }

    private static string RawPolicy(Guid id, int required = 1, string min = "0", string branch = "NULL", bool active = true, string created = "now()") =>
        $"INSERT INTO {AP.Table} ({AP.Id}, {AP.DocType}, {AP.Source}, {AP.BranchId}, {AP.MinAmount}, {AP.RequiredApprovals}, {AP.NameEn}, {AP.IsActive}, {AP.Revision}, {AP.CreatedAtUtc}, {AP.UpdatedAtUtc}) " +
        $"VALUES ('{id}', 'journal_entry', 3, {branch}, {min}, {required}, 'raw', {(active ? "true" : "false")}, 1, {created}, now())";

    [PostgresFact]
    public async Task An_identical_active_policy_is_refused_with_409_and_nothing_is_written_while_every_genuine_difference_is_accepted()
    {
        await PrepareJournal();
        var role = PresetRoles.JournalApproverId;
        var first = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, P()));
        var (rows, audit) = (await Policies_(), await AuditRows());

        // identical: the same selection and the same approvals; the names are not part of the identity, and a classic "1 approval" is one stage of 1 without a role
        Duplicate(await Policies.CreateAsync(Admin.Id, Company.Id, P()));
        Duplicate(await Policies.CreateAsync(Admin.Id, Company.Id, P(en: "Another name")));
        Duplicate(await Policies.CreateAsync(Admin.Id, Company.Id, P(stages: [(1, null)])));
        Duplicate(await PMg.CreateAsync(Admin.Id, Company.Id, P()));                                                           // the authorized entry point answers the same
        Assert.Equal((rows, audit), (await Policies_(), await AuditRows()));                                                   // no policy, no stage, no audit event from a refusal
        Assert.Equal(0L, await Count(Company, AS.Table));

        // genuinely distinct: each differs from the first in exactly one part, and from each other
        var accepted = new[]
        {
            P(branch: Main),                                    // one branch instead of all branches
            P(branch: B2),                                      // another branch
            P(source: "manual"),                                // source
            P(min: 0.0001m),                                    // minimum amount (the smallest step)
            P(required: 2),                                     // approvals
            P(jtype: "reversal"),                               // journal type condition
            P(rate: true), P(rate: false),                      // a condition that is set differs from one that is not, and true differs from false
            P(soft: true),
            P(closing: "closing"),
            P(stages: [(1, role)]),                             // the same count, bound to a role
            P(stages: [(1, null), (1, null)]),                  // two stages of 1 are not one stage of 2
            P(stages: [(1, role), (1, null)]),
            P(stages: [(1, null), (1, role)]),                  // the ORDER of the stages is part of the identity
        };
        foreach (var input in accepted) Ok(await Policies.CreateAsync(Admin.Id, Company.Id, input));
        foreach (var input in accepted) Duplicate(await Policies.CreateAsync(Admin.Id, Company.Id, input));                   // and each of them is protected in turn
        Assert.Equal(rows + accepted.Length, await Policies_());

        // every policy has exactly one identity, computed by PostgreSQL, and no two active identities are equal
        Assert.Equal(await Policies_(), await Count(Company, PI.Table));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM (SELECT {PI.Fingerprint} FROM {PI.Table} WHERE {PI.IsActive} GROUP BY 1 HAVING count(*) > 1) x"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {PI.Table} i WHERE i.{PI.Fingerprint} <> {PI.FnFingerprint}(i.{PI.PolicyId}) OR i.{PI.PreExisting}"));
        Assert.Equal(first.Id, await Sql<Guid>($"SELECT {PI.PolicyId} FROM {PI.Table} WHERE {PI.Fingerprint} = {PI.FnFingerprint}('{first.Id}')"));
    }

    [PostgresFact]
    public async Task Only_active_policies_collide_and_an_update_or_a_reactivation_into_a_duplicate_is_refused()
    {
        await PrepareJournal();
        var a = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, P()));
        var b = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, P(min: 100m)));

        // UPDATE into the identity of another active policy: refused, the policy keeps its revision and its content
        Duplicate(await Policies.UpdateAsync(Admin.Id, Company.Id, b.Id, b.Revision, D()));
        Duplicate(await PMg.UpdateAsync(Admin.Id, Company.Id, b.Id, b.Revision, D()));
        var still = (await Policies.GetAsync(Company.Id, b.Id))!;
        Assert.Equal((b.Revision, 100m), (still.Revision, still.MinAmount));
        // an update that keeps the identity (a new name) is not a duplicate of itself
        var renamed = Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, a.Id, a.Revision, D(en: "Renamed")));

        // a DEACTIVATED policy holds no identity: the same policy may be created again, or another one moved onto it
        var off = Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, renamed.Id, renamed.Revision));
        var moved = Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, b.Id, b.Revision, D()));
        // ... and then the deactivated one cannot come back as a duplicate of the active one
        Duplicate(await Policies.ReactivateAsync(Admin.Id, Company.Id, off.Id, off.Revision));
        Assert.False((await Policies.GetAsync(Company.Id, off.Id))!.IsActive);
        // any number of INACTIVE identical policies may exist
        var offToo = Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, moved.Id, moved.Revision));
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {PI.Table} WHERE NOT {PI.IsActive} AND {PI.Fingerprint} = {PI.FnFingerprint}('{off.Id}')"));
        // with none active, one of them may be reactivated; the other then may not
        Ok(await Policies.ReactivateAsync(Admin.Id, Company.Id, offToo.Id, offToo.Revision));
        Duplicate(await Policies.ReactivateAsync(Admin.Id, Company.Id, off.Id, off.Revision));
    }

    [PostgresFact]
    public async Task Seven_parallel_identical_creates_have_exactly_one_winner_and_six_409_refusals()
    {
        await PrepareJournal();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 7).Select(i => Task.Run(async () => { await gate.Task; return await Policies.CreateAsync(Admin.Id, Company.Id, P(min: 250m, en: $"Writer {i}")); })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);                                                                                // an exception (a 500) would fail the test here
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), Duplicate);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AP.Table} WHERE {AP.MinAmount} = 250"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {LedgerAuditEvents.ApprovalPolicyCreated}"));
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_a_duplicate_from_a_direct_statement_at_commit_also_between_two_concurrent_writers_and_the_identity_cannot_be_forged()
    {
        await PrepareJournal();
        var a = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, P()));
        var rows = await Policies_();

        // a direct INSERT of an identical active policy passes the statement and fails at COMMIT (the deferred identity trigger), leaving nothing
        Assert.Equal(("23505", PI.UqActive), await Direct(Company, RawPolicy(Guid.NewGuid())));
        Assert.Equal(rows, await Policies_());
        // the same statement for an INACTIVE policy, or for a different amount, commits
        Assert.Equal((null, null), await Direct(Company, RawPolicy(Guid.NewGuid(), active: false)));
        var other = Guid.NewGuid();
        Assert.Equal((null, null), await Direct(Company, RawPolicy(other, min: "7")));
        // a stage written directly for the current revision changes the identity: adding the stage that makes it identical to another policy is refused at commit
        var staged = Guid.NewGuid();
        Assert.Equal((null, null), await Direct(Company, RawPolicy(staged, required: 2, min: "9"), $"INSERT INTO {AS.Table} ({AS.PolicyId}, {AS.PolicyRevision}, {AS.StageNo}, {AS.Required}) VALUES ('{staged}', 1, 1, 1), ('{staged}', 1, 2, 1)"));
        var twin = Guid.NewGuid();
        Assert.Equal(("23505", PI.UqActive), await Direct(Company, RawPolicy(twin, required: 2, min: "9"), $"INSERT INTO {AS.Table} ({AS.PolicyId}, {AS.PolicyRevision}, {AS.StageNo}, {AS.Required}) VALUES ('{twin}', 1, 1, 1), ('{twin}', 1, 2, 1)"));
        // stages added LATER, in a transaction that does not touch the policy row, change the identity too: a classic policy of 2 is not the staged 1 > 1, until someone gives it those stages
        var classic = Guid.NewGuid();
        Assert.Equal((null, null), await Direct(Company, RawPolicy(classic, required: 2, min: "9")));
        Assert.Equal(("23505", PI.UqActive), await Direct(Company, $"INSERT INTO {AS.Table} ({AS.PolicyId}, {AS.PolicyRevision}, {AS.StageNo}, {AS.Required}) VALUES ('{classic}', 1, 1, 1), ('{classic}', 1, 2, 1)"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {AS.Table} WHERE {AS.PolicyId} = '{classic}'"));
        // a direct UPDATE into a duplicate (the revision advances by one, as the policy guard demands) is refused at commit
        Assert.Equal(("23505", PI.UqActive), await Direct(Company, $"UPDATE {AP.Table} SET {AP.MinAmount} = 0, {AP.Revision} = {AP.Revision} + 1 WHERE {AP.Id} = '{other}'"));

        // two concurrent direct writers: both statements succeed (the identity is written at commit), then exactly one COMMIT succeeds and the other is refused by the unique index
        await using (var c1 = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)))
        await using (var c2 = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)))
        {
            await c1.OpenAsync(); await c2.OpenAsync();
            await using var t1 = await c1.BeginTransactionAsync(); await using var t2 = await c2.BeginTransactionAsync();
            await using (var i1 = new NpgsqlCommand(RawPolicy(Guid.NewGuid(), min: "55"), c1, t1)) await i1.ExecuteNonQueryAsync();
            await using (var i2 = new NpgsqlCommand(RawPolicy(Guid.NewGuid(), min: "55"), c2, t2)) await i2.ExecuteNonQueryAsync();
            static async Task<string?> Commit(NpgsqlTransaction t) { try { await t.CommitAsync(); return null; } catch (PostgresException ex) { return ex.SqlState + "/" + ex.ConstraintName; } }
            var commits = await Task.WhenAll(Task.Run(() => Commit(t1)), Task.Run(() => Commit(t2)));
            Assert.Equal([null, "23505/" + PI.UqActive], commits.OrderBy(c => c is null ? 0 : 1));
        }
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AP.Table} WHERE {AP.MinAmount} = 55"));

        // the identity table cannot be used to get around it: a forged fingerprint, activity flag or pre-existing mark is replaced by the truth, and a row cannot be removed
        Assert.Equal((null, null), await Direct(Company, $"UPDATE {PI.Table} SET {PI.Fingerprint} = repeat('a', 64), {PI.IsActive} = false, {PI.PreExisting} = true WHERE {PI.PolicyId} = '{a.Id}'"));
        Assert.Equal((true, false, true), (await Sql<bool>($"SELECT {PI.IsActive} FROM {PI.Table} WHERE {PI.PolicyId} = '{a.Id}'"), await Sql<bool>($"SELECT {PI.PreExisting} FROM {PI.Table} WHERE {PI.PolicyId} = '{a.Id}'"),
            await Sql<bool>($"SELECT {PI.Fingerprint} = {PI.FnFingerprint}({PI.PolicyId}) FROM {PI.Table} WHERE {PI.PolicyId} = '{a.Id}'")));
        Assert.Equal("P0001", (await Direct(Company, $"DELETE FROM {PI.Table} WHERE {PI.PolicyId} = '{a.Id}'")).State);
        Assert.Equal("P0001", (await Direct(Company, $"TRUNCATE {PI.Table}")).State);
        Assert.Equal("P0001", (await Direct(Company, $"UPDATE {PI.Table} SET {PI.PolicyId} = '{other}' WHERE {PI.PolicyId} = '{a.Id}'")).State);
        Duplicate(await Policies.CreateAsync(Admin.Id, Company.Id, P()));                                                      // and the refusal still stands
    }

    [PostgresFact]
    public async Task The_migration_succeeds_on_a_database_with_identical_policies_changes_no_policy_marks_all_but_the_oldest_and_a_marked_one_cannot_be_kept_as_a_duplicate()
    {
        await PrepareJournal();
        var all = MigrationScript.LoadEmbeddedCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 32)]);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{PI.Table}'", legacy));
        // a deployed database: three identical active policies (one staged as a single stage of 1, which means the same), an identical INACTIVE one, and an unrelated policy
        Guid oldest = Guid.NewGuid(), dup1 = Guid.NewGuid(), dup2 = Guid.NewGuid(), inactive = Guid.NewGuid(), unrelated = Guid.NewGuid();
        Assert.Equal((null, null), await Direct(legacy,
            RawPolicy(oldest, created: "'2026-01-01Z'"), RawPolicy(dup1, created: "'2026-01-02Z'"), RawPolicy(dup2, created: "'2026-01-03Z'"), RawPolicy(inactive, active: false, created: "'2025-12-01Z'"), RawPolicy(unrelated, min: "500"),
            $"INSERT INTO {AS.Table} ({AS.PolicyId}, {AS.PolicyRevision}, {AS.StageNo}, {AS.Required}) VALUES ('{dup2}', 1, 1, 1)"));
        var before = await Sql<string>($"SELECT md5(string_agg(p::text, '|' ORDER BY p.{AP.Id})) FROM {AP.Table} p", legacy);

        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(), o => o.CompanyId == legacy.Id);
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([.. all.Where(s => s.Version > 32).Select(s => s.Version)], outcome.Applied);

        // no policy row was changed, deactivated or removed
        Assert.Equal(before, await Sql<string>($"SELECT md5(string_agg(p::text, '|' ORDER BY p.{AP.Id})) FROM {AP.Table} p", legacy));
        Assert.Equal(5L, await Count(legacy, AP.Table));
        Assert.Equal(4L, await Sql<long>($"SELECT count(*) FROM {AP.Table} WHERE {AP.IsActive}", legacy));
        // one identity per policy; the oldest of the identical group holds the identity, the two later ones are the reported count
        Assert.Equal(5L, await Count(legacy, PI.Table));
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {PI.Table} WHERE {PI.PreExisting}", legacy));
        Assert.Equal(new[] { dup1, dup2 }.OrderBy(g => g.ToString()), (await Sql<string>($"SELECT string_agg({PI.PolicyId}::text, ',' ORDER BY {PI.PolicyId}::text) FROM {PI.Table} WHERE {PI.PreExisting}", legacy)).Split(',').Select(Guid.Parse));
        Assert.Equal(1L, await Sql<long>($"SELECT count(DISTINCT {PI.Fingerprint}) FROM {PI.Table} WHERE {PI.PolicyId} IN ('{oldest}', '{dup1}', '{dup2}', '{inactive}')", legacy));

        var resolver = SinglePrincipalResolver(legacy);
        var policies = new ApprovalPolicyService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), Clock);
        // a new identical policy is refused, as on any database
        Duplicate(await policies.CreateAsync(Admin.Id, legacy.Id, P()));
        // a marked duplicate cannot be EDITED and stay a duplicate (a new name keeps its identity): refused, and it keeps its mark and its revision
        var marked = (await policies.GetAsync(legacy.Id, dup1))!;
        Duplicate(await policies.UpdateAsync(Admin.Id, legacy.Id, dup1, marked.Revision, D(en: "Edited")));
        Assert.Equal((1L, true), ((await policies.GetAsync(legacy.Id, dup1))!.Revision, await Sql<bool>($"SELECT {PI.PreExisting} FROM {PI.Table} WHERE {PI.PolicyId} = '{dup1}'", legacy)));
        // the holder of the identity is NOT blocked by the marked rows: it can be renamed
        var holder = (await policies.GetAsync(legacy.Id, oldest))!;
        Ok(await policies.UpdateAsync(Admin.Id, legacy.Id, oldest, holder.Revision, D(en: "Holder renamed")));
        // a marked duplicate can be edited into something distinct: the mark is gone for good
        var distinct = Ok(await policies.UpdateAsync(Admin.Id, legacy.Id, dup1, marked.Revision, D(min: 42m)));
        Assert.False(await Sql<bool>($"SELECT {PI.PreExisting} FROM {PI.Table} WHERE {PI.PolicyId} = '{dup1}'", legacy));
        Duplicate(await policies.UpdateAsync(Admin.Id, legacy.Id, dup1, distinct.Revision, D()));                               // and it cannot go back
        // a marked duplicate can always be deactivated; it then cannot be REACTIVATED into a duplicate
        var second = (await policies.GetAsync(legacy.Id, dup2))!;
        var off = Ok(await policies.DeactivateAsync(Admin.Id, legacy.Id, dup2, second.Revision));
        Assert.False(await Sql<bool>($"SELECT {PI.PreExisting} FROM {PI.Table} WHERE {PI.PolicyId} = '{dup2}'", legacy));
        Duplicate(await policies.ReactivateAsync(Admin.Id, legacy.Id, dup2, off.Revision));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {PI.Table} WHERE {PI.PreExisting}", legacy));
        // the mark cannot be written by anyone after the migration
        Assert.Equal((null, null), await Direct(legacy, $"UPDATE {PI.Table} SET {PI.PreExisting} = true WHERE {PI.PolicyId} = '{unrelated}'"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {PI.Table} WHERE {PI.PreExisting}", legacy));
        // running the migrator again changes nothing
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(), o => o.CompanyId == legacy.Id).Status);
    }

    [PostgresFact]
    public async Task While_a_marked_duplicate_is_active_its_identity_stays_taken_even_after_the_holder_is_deactivated()
    {
        await PrepareJournal();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 32)]);
        Guid oldest = Guid.NewGuid(), dup = Guid.NewGuid();
        Assert.Equal((null, null), await Direct(legacy, RawPolicy(oldest, created: "'2026-01-01Z'"), RawPolicy(dup, created: "'2026-01-02Z'")));
        Assert.Equal(CompanyMigrationStatus.Migrated, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(), o => o.CompanyId == legacy.Id).Status);
        var resolver = SinglePrincipalResolver(legacy);
        var policies = new ApprovalPolicyService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), Clock);
        var holder = (await policies.GetAsync(legacy.Id, oldest))!;
        var off = Ok(await policies.DeactivateAsync(Admin.Id, legacy.Id, oldest, holder.Revision));
        // the marked policy is still active, so neither a new identical policy nor the reactivated holder may join it (service and direct statement)
        Duplicate(await policies.CreateAsync(Admin.Id, legacy.Id, P()));
        Duplicate(await policies.ReactivateAsync(Admin.Id, legacy.Id, oldest, off.Revision));
        Assert.Equal(("23505", PI.UqActive), await Direct(legacy, RawPolicy(Guid.NewGuid())));
        // once the marked one is deactivated the identity is free again
        var marked = (await policies.GetAsync(legacy.Id, dup))!;
        Ok(await policies.DeactivateAsync(Admin.Id, legacy.Id, dup, marked.Revision));
        Ok(await policies.CreateAsync(Admin.Id, legacy.Id, P()));
    }
}
