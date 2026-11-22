using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using EG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochGuard;
using EL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochEntryLineage;
using EM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The migration itself (owner safeguards 1 and 2 of X1-A): every function, trigger, constraint, index and column that existed before company migration 0034 is byte-identical after it and no trigger is disabled (N17);
/// a company with an executed epoch survives a dump and a trial restore with an identical catalog and identical data, and the guards work in the restored copy (N18); and a period close racing an execution never
/// leaves an epoch entry in a closed period (N28).
/// </summary>
public class PlanExecutionCatalogTests : PlanExecutionTestBase
{
    [PostgresRoleFact]
    public async Task N17_every_existing_function_trigger_constraint_index_and_column_is_byte_identical_after_the_migration_and_no_trigger_is_disabled()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var plan = all.Single(s => s.Sql.Contains(EG.FnEntry));
        var legacy = await ReadyCompany([.. all.Where(s => s.Version < plan.Version)]);
        var before = await Catalog(legacy.PhysicalDatabaseName);

        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal(all.Where(s => s.Version >= plan.Version).Select(s => s.Version), outcome.Applied);
        var after = await Catalog(legacy.PhysicalDatabaseName);

        // the catalog before the migration is not trivially small, and it holds the named backstops of design 3.7
        Assert.True(before.Count(k => k.Key.StartsWith("function ")) > 150 && before.Count(k => k.Key.StartsWith("trigger ")) > 300 && before.Count(k => k.Key.StartsWith("constraint ")) > 300, $"{before.Count} catalog entries");
        foreach (var key in new[]
                 {
                     $"function {PO.GuardFunction}()", $"function {OP.GuardFunction}()", $"function {EN.GuardFunction}()", $"function {CompanySchema.CostLayer.GuardFunction}()", $"function {CompanySchema.CostConsumption.GuardFunction}()",
                     $"function {CompanySchema.NegativeStockGuard.EmptyOperationFunction}()", $"function {CompanySchema.NegativeStockGuard.PositionStockFunction}()", $"function {CompanySchema.NegativeStockGuard.LastAverageFunction}()",
                     $"trigger {PO.Table}.{PO.GuardRow}", $"trigger {PO.Table}.{PO.GuardTruncate}", $"trigger {EN.Table}.{EN.GuardRow}", $"trigger {EN.Table}.{EN.GuardTruncate}", $"trigger {OP.Table}.{OP.GuardRow}", $"trigger {OP.Table}.{OP.GuardTruncate}",
                     $"trigger {PO.Table}.{CompanySchema.NegativeStockGuard.PositionStockTrigger}", $"trigger {OP.Table}.{CompanySchema.NegativeStockGuard.EmptyOperationTrigger}", $"trigger {PO.Table}.{CompanySchema.NegativeStockGuard.LastAverageTrigger}",
                     $"constraint {OP.Table}.{OP.CheckValues}", $"constraint {EN.Table}.{EN.CheckValues}", $"constraint {PO.Table}.{PO.CheckValues}", $"index {OP.Table}.{OP.UniqueReverses}", $"index {EN.Table}.{EN.UniqueReverses}", $"index {EN.Table}.{EN.UniquePositionSeq}",
                     $"function {CompanySchema.BusinessAudit.GuardFunction}()", $"trigger {CompanySchema.BusinessAudit.Table}.{CompanySchema.BusinessAudit.GuardTrigger}", $"function {FP.GuardFunction}()",
                 })
            Assert.True(before.ContainsKey(key), key);

        // BYTE-IDENTICAL: every entry that existed is still there with exactly the same definition (functions with volatility, definer flag, settings and ACL; triggers with their enabled state and function;
        // constraints with validity and deferrability; index definitions; column types, nullability and defaults)
        foreach (var (key, definition) in before)
        {
            Assert.True(after.TryGetValue(key, out var now), "removed: " + key);
            Assert.True(definition == now, "changed: " + key);
        }
        // no trigger is disabled, before or after, the new ones included
        Assert.DoesNotContain(after, k => k.Key.StartsWith("trigger ") && !k.Value.Contains(" | enabled=O "));
        Assert.DoesNotContain(before, k => k.Key.StartsWith("trigger ") && !k.Value.Contains(" | enabled=O "));

        // what the migration ADDED to tables that already existed: exactly four triggers (three insert guards and the deferred position check of review M2; a constraint trigger also has a constraint row), plus the internal foreign-key triggers of the new tables' references; no column, constraint or index
        var oldTables = before.Keys.Where(k => k.StartsWith("table ")).Select(k => k["table ".Length..]).ToHashSet();
        var addedToOld = after.Keys.Except(before.Keys).Where(k => !k.StartsWith("function ") && !k.StartsWith("table ") && oldTables.Contains(k.Split(' ')[1].Split('.')[0])).ToList();
        Assert.Equal(new[] { $"constraint {EN.Table}.{EG.TrEntryCommit}", $"trigger {EN.Table}.{EG.TrEntry}", $"trigger {EN.Table}.{EG.TrEntryCommit}", $"trigger {OP.Table}.{EG.TrOperation}", $"trigger {CompanySchema.StockPosting.Table}.{EG.TrPosting}" }.Order(StringComparer.Ordinal),
            addedToOld.Where(k => !k.Contains(".internal:")).Order(StringComparer.Ordinal));
        Assert.All(addedToOld.Where(k => k.Contains(".internal:")), k => Assert.StartsWith("trigger ", k));
        Assert.Equal(9, after.Keys.Count(k => k.StartsWith("table ")) - oldTables.Count);
        // every function the migration added is new by name: none of the earlier names was redefined (CREATE OR REPLACE would have changed a definition above)
        Assert.Equal(21, after.Keys.Except(before.Keys).Count(k => k.StartsWith("function ")));
    }

    /// <summary>The CHECK constraints (all of earlier migrations) whose printed text changes in a dump and restore: PostgreSQL re-parses the expression. None belongs to migration 0034. Pinned by review L4.</summary>
    private static readonly string[] CosmeticCheckDifferences = [
        "constraint t_28e40627.k_d6ad16bf",
        "constraint t_3069b85b.k_d070c05a",
        "constraint t_33fca2e4.k_62b3d49c",
        "constraint t_6eec2227.k_2d8d01ee",
        "constraint t_8176e273.k_101d5d5c",
        "constraint t_91632385.k_5c6c576e",
        "constraint t_9ff18b1b.k_70ff0005",
        "constraint t_a8b39266.k_5d1e394e",
        "constraint t_b6491d4d.k_2e340a81",
        "constraint t_b9231a6b.k_851f3da2",
        "constraint t_c0e5f7b5.k_85514d93",
        "constraint t_f3158869.k_eb8d49c6",
    ];

    [PostgresRoleFact]
    public async Task N18_a_dump_with_an_executed_epoch_restores_with_an_identical_catalog_and_identical_data_and_the_guards_work_in_the_restored_copy()
    {
        await PrepareRuntime();
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var work = Path.Combine(Path.GetTempPath(), "hasm-n18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var tool = new PgBackupTool(Options);
            var dump = Path.Combine(work, "company.dump");
            var locale = await tool.DumpAsync(Company.PhysicalDatabaseName, dump);
            var restored = NewTestName();
            // pg_restore loads the data before it creates the triggers: the epoch rows (whose guards and deferred checks only accept them inside their executing transaction) restore without a false refusal
            await tool.RestoreIntoNewDatabaseAsync(dump, restored, Company.PhysicalDatabaseName, locale);

            var source = await Catalog(Company.PhysicalDatabaseName);
            var copy = await Catalog(restored);
            Assert.Equal(source.Keys, copy.Keys);
            // A CHECK expression is re-parsed by the restore and PostgreSQL prints it differently afterwards (an expanded BETWEEN, parentheses, ARRAY casts). That is true of exactly the pinned CHECKs of earlier migrations
            // below (review L4); every other entry (functions, triggers with their enabled state, keys, foreign keys, indexes, columns) and every CHECK of migration 0034 is compared byte for byte, and for the pinned
            // CHECKs the validity and deferrability must be identical.
            var differing = source.Where(kv => kv.Value != copy[kv.Key]).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(CosmeticCheckDifferences.Order(StringComparer.Ordinal), differing);
            foreach (var key in differing)
            {
                Assert.StartsWith("CHECK ", source[key]);
                Assert.Equal(source[key][source[key].LastIndexOf(" | ", StringComparison.Ordinal)..], copy[key][copy[key].LastIndexOf(" | ", StringComparison.Ordinal)..]);
            }
            Assert.DoesNotContain(copy, k => k.Key.StartsWith("trigger ") && !k.Value.Contains(" | enabled=O "));
            foreach (var table in source.Keys.Where(k => k.StartsWith("table ")).Select(k => k["table ".Length..]))
                Assert.True(await Digest(table) == await Digest(table, database: restored), "data differs after restore: " + table);
            Assert.Equal((1L, 3L, 1L), (await InCompany<long>(restored, $"SELECT count(*) FROM {CX.Table}"), await InCompany<long>(restored, $"SELECT count(*) FROM {EM.Table}"), await InCompany<long>(restored, $"SELECT count(*) FROM {EL.Table}")));

            // the schema gate accepts the restored database (journal, checksums and company identity)
            await using (var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(restored)))
            {
                await conn.OpenAsync();
                await CompanySchemaGate.EnsureCompatibleAsync(conn, Company.Id, MigrationScript.LoadEmbeddedCompany(), allowOverprivileged: true);
            }
            // and the guards are alive there: a back-dated ordinary entry, a member for the restored execution and a change of a lineage row are all refused
            async Task<string?> InRestored(params string[] statements)
            {
                await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(restored));
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();
                try { foreach (var q in statements) { await using var cmd = new NpgsqlCommand(q, conn, tx); await cmd.ExecuteNonQueryAsync(); } await tx.CommitAsync(); return null; }
                catch (PostgresException ex) { return ex.SqlState + ": " + ex.MessageText; }
            }
            var op = Guid.NewGuid();
            RefusedWith("a back-dated cost entry is accepted only inside the execution of an approved compensation plan", await InRestored([Op(op, 1, D2), .. Move(op, Avg.Id, W1.Id, 1, 1, D2, 1m, 10m)]));
            RefusedWith("an epoch member belongs to the execution, written in this transaction", await InRestored(Op(op, 1, D3), Member(op, s.Plan.Execution, 2, null)));
            RefusedWith("compensation plan and epoch records are append-only", await InRestored($"UPDATE {EL.Table} SET {EL.ExecutionId} = {EL.ExecutionId}"));
            RefusedWith("operation_rewritten_by_epoch", await InRestored(Op(op, 4, D3, s.Event)));
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    [PostgresRoleFact]
    public async Task N28_a_period_close_that_starts_during_an_execution_waits_for_it_and_no_later_epoch_entry_enters_the_closed_period()
    {
        await PrepareRuntime();
        var s = await SimpleEpoch();
        var t = await SimpleEpoch(Avg2);
        Task<string?> close = null!;
        // after the first epoch entry the executing transaction holds the period row FOR SHARE (taken by the entry guard itself, not by the test): a close of that period cannot get through
        s.Script.Hooks["storno"] = async () =>
        {
            close = Task.Run(() => RawMessage(PeriodStatus("2026-03-12", 2)));
            await Task.Delay(1500);
            Assert.False(close.IsCompleted);
        };
        Assert.Null(await Run(s.Script));                                                                                       // the execution wins: all of its entries were written while the period was Open
        Assert.Null(await close);                                                                                               // only then does the close go through
        Assert.Equal((short)2, await Sql<short>($"SELECT {FP.Status} FROM {FP.Table} WHERE {FP.StartDate} = DATE '2026-03-01'"));
        // and from then on no epoch entry enters the closed period
        RefusedWith("period_not_open", await Run(t.Script));
        Assert.Equal((1L, 3L, 1L), (await Rows(CX.Table), await Rows(EM.Table), await Rows(EL.Table)));
        // EXECUTOR-LEVEL VARIANT (step 5): the close is the real FiscalCalendarService close (calendar advisory lock, then the period row FOR UPDATE, then the close probes), which after waiting refuses because of
        // the epoch's pending compensation journals; the database part proven here is that it cannot overtake the execution.
    }

    [PostgresRoleFact]
    public async Task N28_an_execution_that_starts_during_a_period_close_waits_for_it_and_is_refused_once_the_period_is_closed()
    {
        await PrepareRuntime();
        var s = await SimpleEpoch();
        var before = (await Rows(CX.Table), await Rows(EM.Table), await Digest(EN.Table));
        await using var closer = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await closer.OpenAsync();
        await using var tx = await closer.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(PeriodStatus("2026-03-12", 2), closer, tx)) await cmd.ExecuteNonQueryAsync();      // the close holds the period row, not yet committed
        var execution = Task.Run(() => Run(s.Script));
        await Task.Delay(2000);
        Assert.False(execution.IsCompleted);                                                                                    // its first epoch entry is waiting for the period row (FOR SHARE)
        await tx.CommitAsync();                                                                                                 // the close wins
        RefusedWith("period_not_open", await execution);
        Assert.Equal(before, (await Rows(CX.Table), await Rows(EM.Table), await Digest(EN.Table)));                             // never an epoch entry in the closed period
    }
}
