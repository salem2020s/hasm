using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using FYr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using NC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using SA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustment;
using SDE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentEvent;
using STE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StocktakeEvent;
using JV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Gapless numbering under failure, for every numbered operation outside the commercial track (which has its own tests): stock document approval, stocktake approval, stock adjustment generation and journal posting.
/// Each test proves two things. (1) Next-number reuse after a failure AFTER the issue, without a planted duplicate and without a test seam: the operation is held, after its number was issued, by a SHARE lock on
/// the table it writes next, and its backend is cancelled (pg_cancel_backend); the whole transaction rolls back, the counter does not move, and the retry receives exactly that number. (2) A number collision
/// (the next number is already issued, e.g. after a direct write) is the stable, NOT retryable refusal <c>number_already_issued</c> (409; a warning naming the document type is logged), never an unhandled
/// 23505: nothing changes, the counter does not move, a retry is refused the same way (no number is skipped). Disposable cluster only.
/// </summary>
public class NumberingCollisionTests : StockAdjustmentTestBase
{
    private Task<long> LastValue(string docType) => Sql<long>($"SELECT coalesce(max({NC.LastValue}), 0) FROM {NC.Table} WHERE {NC.DocType} = '{docType}'");
    private Task<long> Issued(string docType) => Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = '{docType}'");

    private async Task Occupy(string docType, string number)
    {
        var year = await Sql<Guid>($"SELECT {FYr.Id} FROM {FYr.Table} LIMIT 1");
        await Sql($"INSERT INTO {NI.Table} ({NI.DocType}, {NI.Number}, {NI.BranchId}, {NI.FiscalYearId}, {NI.Value}, {NI.DocumentId}, {NI.IssuedAtUtc}, {NI.IssuedBy}) " +
                  $"VALUES ('{docType}', '{number}', '{(await Main()).Id}', '{year}', 77, '{Guid.NewGuid()}', now(), '{Admin.Id}')");
    }

    /// <summary>
    /// Runs <paramref name="operation"/> while <paramref name="table"/> is held in SHARE mode (it is the table the operation writes right AFTER issuing its number), waits until the operation's backend waits for that
    /// lock, cancels it, and returns the failure. Bounded polling; no sleep is used as synchronisation.
    /// </summary>
    private async Task<Exception> CancelledAfterIssue(string table, Func<Task> operation)
    {
        await using var holder = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lk = new NpgsqlCommand($"LOCK TABLE {table} IN SHARE MODE", holder, tx)) await lk.ExecuteNonQueryAsync();
        var run = Task.Run(operation);
        var pid = 0;
        for (var i = 0; i < 600 && pid == 0; i++)
        {
            pid = await Sql<int>($"SELECT coalesce(min(pid), 0) FROM pg_locks WHERE NOT granted AND locktype = 'relation' AND relation = '{table}'::regclass");   // the backend waiting for exactly this table
            if (pid == 0) await Task.Delay(50);
        }
        Assert.True(pid != 0, "the operation never reached the table it writes after issuing its number");
        Assert.True(await Sql<bool>($"SELECT pg_cancel_backend({pid})"));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => run);
        await tx.RollbackAsync();
        Assert.Equal("57014", (failure as PostgresException ?? failure.InnerException as PostgresException)?.SqlState);   // query_canceled: the operation failed after its number was issued
        return failure;
    }

    [PostgresFact]
    public async Task Stock_document_approval_reuses_the_number_after_a_failure_and_refuses_a_collision_with_number_already_issued()
    {
        await PrepareAdjustments();
        var approver = await Approver();
        async Task<StockDocumentView> Submitted()
        {
            var d = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1))));
            return Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        }
        var doc = await Submitted();
        await CancelledAfterIssue(SDE.Table, () => Docs.ApproveAsync(approver.Id, Company.Id, doc.Id, doc.Revision));
        Assert.Equal((0L, 0L, (string?)null), (await LastValue(DocumentTypes.StockReceipt), await Issued(DocumentTypes.StockReceipt), (await Docs.GetAsync(Company.Id, doc.Id))!.Number));
        Assert.Equal("SR-2026-000001", Ok(await Docs.ApproveAsync(approver.Id, Company.Id, doc.Id, doc.Revision)).Number);  // the retry gets exactly the number the failed attempt had issued

        var next = await Submitted();
        await Occupy(DocumentTypes.StockReceipt, "SR-2026-000002");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Fails(await Docs.ApproveAsync(approver.Id, Company.Id, next.Id, next.Revision), ConfigStatus.Conflict, NumberingService.NumberAlreadyIssued);
            var after = (await Docs.GetAsync(Company.Id, next.Id))!;
            Assert.Equal((next.Status, next.Revision, (string?)null), (after.Status, after.Revision, after.Number));
            Assert.Equal((1L, 2L), (await LastValue(DocumentTypes.StockReceipt), await Issued(DocumentTypes.StockReceipt)));   // the counter stays at 1; only the first number and the foreign row exist
        }
    }

    [PostgresFact]
    public async Task Stocktake_approval_reuses_the_number_after_a_failure_and_refuses_a_collision_with_number_already_issued()
    {
        await PrepareAdjustments();
        var approver = (await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeApprove], ScopeKind.Company, null)).User;
        async Task<StocktakeView> Submitted()
        {
            var d = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput(W1.Id, CountDate, "COUNT", null, [C(Fifo, Pcs, 1m)])));
            return Ok(await Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        }
        var take = await Submitted();
        await CancelledAfterIssue(STE.Table, () => Takes.ApproveAsync(approver.Id, Company.Id, take.Id, take.Revision));
        Assert.Equal((0L, 0L, (string?)null), (await LastValue(DocumentTypes.Stocktake), await Issued(DocumentTypes.Stocktake), (await Takes.GetAsync(Company.Id, take.Id))!.Number));
        Assert.Equal("STK-2026-000001", Ok(await Takes.ApproveAsync(approver.Id, Company.Id, take.Id, take.Revision)).Number);

        var next = await Submitted();
        await Occupy(DocumentTypes.Stocktake, "STK-2026-000002");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Fails(await Takes.ApproveAsync(approver.Id, Company.Id, next.Id, next.Revision), ConfigStatus.Conflict, NumberingService.NumberAlreadyIssued);
            var after = (await Takes.GetAsync(Company.Id, next.Id))!;
            Assert.Equal((next.Status, next.Revision, (string?)null), (after.Status, after.Revision, after.Number));
            Assert.Equal((1L, 2L), (await LastValue(DocumentTypes.Stocktake), await Issued(DocumentTypes.Stocktake)));
        }
    }

    [PostgresFact]
    public async Task Stock_adjustment_generation_reuses_the_number_after_a_failure_and_refuses_a_collision_with_number_already_issued()
    {
        await PrepareAdjustments();
        var first = await ApprovedTake(W1, C(Fifo, Pcs, 1m));
        await CancelledAfterIssue(SA.Table, () => Generate(first));
        Assert.Equal((0L, 0L, 0L), (await LastValue(DocumentTypes.StockAdjustment), await Issued(DocumentTypes.StockAdjustment), await Sql<long>($"SELECT count(*) FROM {SA.Table}")));
        Assert.Equal("SA-2026-000001", Ok(await Generate((await Takes.GetAsync(Company.Id, first.Id))!)).Number);

        var second = await ApprovedTake(W2, C(Fifo, Pcs, 1m));
        await Occupy(DocumentTypes.StockAdjustment, "SA-2026-000002");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Fails(await Generate(second), ConfigStatus.Conflict, NumberingService.NumberAlreadyIssued);
            Assert.Null((await Takes.GetAsync(Company.Id, second.Id))!.AdjustmentId);
            Assert.Equal((1L, 2L, 1L), (await LastValue(DocumentTypes.StockAdjustment), await Issued(DocumentTypes.StockAdjustment), await Sql<long>($"SELECT count(*) FROM {SA.Table}")));
        }
    }

    [PostgresFact]
    public async Task Journal_posting_reuses_the_number_after_a_failure_and_refuses_a_collision_with_number_already_issued()
    {
        await PrepareAdjustments();
        var poster = await Poster();
        async Task<JournalEntryView> Approved_(string date, decimal amount)
        {
            var p = Ok(await Post(await Approved(Receipt(W1, date, L(Fifo, Pcs, 1))), Amounts(amount)));
            var d = (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!;
            return Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, d.Header.Id, d.Header.Revision));
        }
        var entry = await Approved_("2026-03-10", 10m);
        await CancelledAfterIssue(JV.Table, () => Journal.PostAsync(poster.Id, Company.Id, entry.Header.Id, entry.Header.Revision, false));
        Assert.Equal((0L, 0L, JournalStatus.Approved), (await LastValue(DocumentTypes.JournalEntry), await Issued(DocumentTypes.JournalEntry), (await Journal.GetAsync(Company.Id, entry.Header.Id))!.Header.Status));
        Assert.Equal("JV-2026-000001", Ok(await Journal.PostAsync(poster.Id, Company.Id, entry.Header.Id, entry.Header.Revision, false)).Header.Number);

        var next = await Approved_("2026-03-11", 12m);
        await Occupy(DocumentTypes.JournalEntry, "JV-2026-000002");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Fails(await Journal.PostAsync(poster.Id, Company.Id, next.Header.Id, next.Header.Revision, false), ConfigStatus.Conflict, NumberingService.NumberAlreadyIssued);
            var after = (await Journal.GetAsync(Company.Id, next.Header.Id))!;
            Assert.Equal((JournalStatus.Approved, (string?)null), (after.Header.Status, after.Header.Number));
            Assert.Equal((1L, 2L), (await LastValue(DocumentTypes.JournalEntry), await Issued(DocumentTypes.JournalEntry)));
        }
    }
}
