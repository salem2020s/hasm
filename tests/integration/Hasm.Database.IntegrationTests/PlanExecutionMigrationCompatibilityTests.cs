using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using EG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochGuard;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Owner condition 4 for the re-scoped PR 48: company migration 0036 (the plan-execution database layer) is forward-only and compatible with an EXISTING company database. A company is built at the schema of
/// main's latest migration (every migration before 0036), representative stock and costing data is posted through the real services (FIFO and moving-average receipts and issues, a transfer, a reversal, a closed
/// period), the database is migrated to 0036 by the real migrator, and the test shows that every pre-existing row is byte-identical, the catalog only GAINED objects, and posting, reversal, close and costing still work.
/// The company database at the older schema cannot be served by this backend's schema gate unless the tables of the new migration exist, so those nine tables are created EMPTY as placeholders for the time the data is
/// written and dropped again before the snapshot is taken; nothing writes to them.
/// </summary>
public class PlanExecutionMigrationCompatibilityTests : PlanExecutionTestBase
{
    private static readonly IReadOnlyList<MigrationScript> All = MigrationScript.LoadEmbeddedCompany();
    private static readonly MigrationScript PlanScript = All.Single(s => s.Sql.Contains(EG.FnEntry));
    private readonly List<MigrationScript> live = [.. All.Where(s => s.Version < PlanScript.Version)];
    private readonly List<string> placeholders = [];

    protected override IReadOnlyList<MigrationScript>? CompanyScripts => live;

    protected override async Task OnCompanyProvisioned(CompanyRecord company)
    {
        // the tables this backend's runtime-access check requires that the older schema does not have yet (exactly the tables of the migration under test)
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
        await conn.OpenAsync();
        foreach (var (table, _) in CompanyRuntimePrivileges.Tables)
        {
            await using var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
            exists.Parameters.AddWithValue("t", table);
            if (await exists.ExecuteScalarAsync() is true) continue;
            placeholders.Add(table);
            await using var create = new NpgsqlCommand($"CREATE TABLE {table} (placeholder integer)", conn);
            await create.ExecuteNonQueryAsync();
        }
    }

    private async Task DropPlaceholders()
    {
        foreach (var table in placeholders) await Sql($"DROP TABLE {table}");
        placeholders.Clear();
    }

    private async Task<List<string>> Tables() =>
        [.. (await Sql<string>("SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_tables WHERE schemaname = 'public'")).Split(',')];

    [PostgresRoleFact]
    public async Task Migration_0036_is_additive_leaves_every_existing_row_untouched_and_posting_reversal_close_and_costing_still_work()
    {
        // ---- the company at the schema of main's latest migration, with representative data written by the real services
        await PrepareNegative();
        Assert.Equal(PlanScript.Version - 1, All.Where(s => live.Contains(s)).Max(s => s.Version));
        Assert.NotEmpty(placeholders);
        Assert.Equal(PlanScript.Version, All.Count(s => s.Version <= PlanScript.Version));       // the migration under test is the last one
        var fifoReceipt = await ReceiveDoc(Fifo, 10m, 100m, "2026-03-10");
        var avgReceipt = await ReceiveDoc(Avg, 10m, 100m, "2026-03-10");
        await PostJournal(fifoReceipt); await PostJournal(avgReceipt);
        await IssueDoc(Fifo, 4m, "2026-03-12");
        await IssueDoc(Avg, 4m, "2026-03-12");
        Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-12", L(Avg, Pcs, 2m)))));
        var reversedDoc = await Approved(Receipt(W1, "2026-03-12", L(Fifo2, Pcs, 5m)));
        Ok(await Post(reversedDoc, Amounts(50m)));
        Ok(await Reverse(reversedDoc, "2026-03-13"));
        // a closed period: January Soft Closed, February Final Closed (nothing is dated there)
        var january = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(p => p.Number == 1);
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, january.Id, january.Revision));
        var february = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(p => p.Number == 2);
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, february.Id, february.Revision));
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, february.Id, soft.Revision));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        await AssertConsistent();

        // ---- the placeholders go; this is exactly main's schema with data
        await DropPlaceholders();
        Assert.DoesNotContain(await Tables(), t => t == CompanySchema.CompensationPlan.Table);
        var before = await Catalog(Company.PhysicalDatabaseName);
        var tables = (await Tables()).Where(t => t != SystemSchema.MigrationJournalTable).ToList();
        var digests = new Dictionary<string, string>();
        foreach (var t in tables) digests[t] = await Digest(t);
        var rowsBefore = new Dictionary<string, long>();
        foreach (var t in tables) rowsBefore[t] = await Sql<long>($"SELECT count(*) FROM {t}");
        Assert.True(rowsBefore[CompanySchema.CostEntry.Table] >= 8 && rowsBefore[CompanySchema.StockPosting.Table] >= 6 && rowsBefore[CompanySchema.JournalEntry.Table] >= 4, "representative data exists");
        var journalBefore = await Sql<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}");

        // ---- the real migrator applies exactly migration 0036
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(), o => o.CompanyId == Company.Id);
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([PlanScript.Version], outcome.Applied);
        live.Add(PlanScript);          // the resolvers of this test now know the new script (they read the list at every schema check)
        Assert.Equal(journalBefore + 1, await Sql<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(), o => o.CompanyId == Company.Id).Status);   // idempotent

        // ---- additive: every earlier catalog entry is byte-identical, no trigger is disabled, the catalog only gained objects
        var after = await Catalog(Company.PhysicalDatabaseName);
        foreach (var (key, definition) in before) Assert.True(after.TryGetValue(key, out var now) && now == definition, "changed or removed: " + key);
        Assert.True(after.Count > before.Count);
        Assert.DoesNotContain(after, k => k.Key.StartsWith("trigger ") && !k.Value.Contains(" | enabled=O "));
        Assert.Equal(before.Keys.Count(k => k.StartsWith("table ")) + 9, after.Keys.Count(k => k.StartsWith("table ")));
        // every pre-existing row is untouched (same text of every row), and the nine new tables are empty
        foreach (var t in tables) Assert.True(digests[t] == await Digest(t), "rows changed: " + t);
        foreach (var t in new[] { CompanySchema.CompensationPlan.Table, CompanySchema.CompensationPlanPosition.Table, CompanySchema.CompensationPlanLine.Table, CompanySchema.CompensationPlanEvent.Table,
            CompanySchema.CompensationPlanApproval.Table, CompanySchema.CompensationPlanExecution.Table, CompanySchema.CompensationPlanExecutionJournal.Table, CompanySchema.EpochMember.Table, CompanySchema.EpochEntryLineage.Table })
            Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {t}"));

        // ---- the application still works on the migrated database: posting (receipt, issue, FIFO and average, a transfer), a reversal, a period close, costing
        await ReceiveDoc(Fifo, 5m, 60m, "2026-03-13");
        await IssueDoc(Avg, 1m, "2026-03-13");
        Ok(await Post(await Approved(Transfer(W1, W2, "2026-03-13", L(Fifo, Pcs, 1m)))));
        var later = await Approved(Receipt(W1, "2026-03-14", L(Avg2, Pcs, 3m)));
        Ok(await Post(later, Amounts(30m)));
        Ok(await Reverse(later, "2026-03-15"));
        var april = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(p => p.Number == 4);
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, april.Id, april.Revision));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        await AssertConsistent();
        // the stock-period and plan guards leave a closed period closed for stock exactly as before (the service answers first, with its own code)
        var januaryDoc = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-01-15", L(Avg, Pcs, 1m))));
        var januarySubmitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, januaryDoc.Id, januaryDoc.Revision));
        var refused = await Docs.ApproveAsync((await Approver()).Id, Company.Id, januaryDoc.Id, januarySubmitted.Revision);
        Assert.Equal((ConfigStatus.Conflict, "fiscal_period_soft_closed"), (refused.Status, refused.Code));

        // ---- and for the per-company runtime role: after the documented grant step an ordinary entry commits and a back-dated one is refused by the new guard
        var granted = await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        Assert.True(granted.LoginVerified);
        var op = Guid.NewGuid();
        Assert.Null(await Rt([Op(op, 1, new DateOnly(2026, 3, 20)), .. Move(op, Avg.Id, W1.Id, 1, 1, new DateOnly(2026, 3, 20), 1m, 10m)]));
        var late = Guid.NewGuid();
        RefusedWith("a back-dated cost entry is accepted only inside the execution of an approved compensation plan", await Rt([Op(late, 1, D2), .. Move(late, Avg.Id, W1.Id, 1, 1, D2, 1m, 10m)]));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }
}
