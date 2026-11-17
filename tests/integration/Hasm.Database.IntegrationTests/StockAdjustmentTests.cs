using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using ST = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Stocktake;
using SA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustment;
using SAL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentLine;
using SAP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPosting;
using SAPL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPostingLine;
using SAR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentReversal;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8B-2: the adjustment generated from an approved stocktake, posted through the 3.7B machinery and reversed with the 3.7C rules (company migration 0019) against real PostgreSQL: one adjustment per stocktake
/// against the CURRENT book quantity, exact figures for shortages (engine cost) and surpluses (entered amount), one balanced journal against the dedicated adjustment counter account, the refusal while it is not
/// configured, exact reversal, the stocktake cancel guard, closed periods, warehouse-scoped authorization and the database guards. Disposable cluster only.
/// </summary>
public abstract class StockAdjustmentTestBase : StockReversalTestBase
{
    protected StocktakeService Takes = null!;
    protected StocktakeManagementService TakeMgmt = null!;
    protected StockAdjustmentService Adj = null!;
    protected StockAdjustmentManagementService AdjMgmt = null!;
    protected AccountView AdjAcc = null!;
    protected const string CountDate = "2026-03-11";

    /// <summary>Posting prepared as in 3.7B; with <paramref name="adjustmentAccount"/> a second configuration version names the dedicated adjustment counter account.</summary>
    protected async Task PrepareAdjustments(bool adjustmentAccount = true)
    {
        await PreparePosting();
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Takes = new StocktakeService(Resolver, tx, Clock);
        TakeMgmt = new StocktakeManagementService(Rbac, Docs, Takes);
        Adj = new StockAdjustmentService(Resolver, tx, Cost, Journal, Clock);
        AdjMgmt = new StockAdjustmentManagementService(Rbac, Takes, Adj);
        var chart = new ChartOfAccountsService(Resolver, tx, new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        AdjAcc = Ok(await chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("5201", null, "فروقات الجرد", "Stock adjustments", "expense", null, false, null, null)));
        if (adjustmentAccount) Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id)));
    }

    protected static StocktakeLineInput C(ItemView item, UnitView unit, decimal qty, string? reason = "counted") => new(item.Id, unit.Id, qty, reason);

    /// <summary>A stocktake of <paramref name="wh"/> on the count date with these counts, submitted by the administrator and approved by someone else.</summary>
    protected async Task<StocktakeView> ApprovedTake(WarehouseView? wh = null, params StocktakeLineInput[] lines)
    {
        var d = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput((wh ?? W1).Id, CountDate, "COUNT", null, lines)));
        var s = Ok(await Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        var approver = (await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeApprove], ScopeKind.Company, null)).User;
        return Ok(await Takes.ApproveAsync(approver.Id, Company.Id, d.Id, s.Revision));
    }

    protected Task<ConfigResult<StockAdjustmentView>> Generate(StocktakeView st, UserRecord? by = null) => Adj.GenerateAsync((by ?? Admin).Id, Company.Id, st.Id, st.Revision);

    /// <summary>The amount of each surplus line, by line number.</summary>
    protected static StockPostInput Values(params (int Line, decimal Amount)[] amounts) => new([.. amounts.Select(a => new StockPostLineInput(a.Line, a.Amount))]);

    protected async Task<(long Ops, long Postings, long Journals, long Adjustments)> AdjCounts() =>
        (await Sql<long>($"SELECT count(*) FROM {CO.Table}"), await Sql<long>($"SELECT count(*) FROM {SAP.Table}"), await Sql<long>($"SELECT count(*) FROM {JE.Table}"), await Sql<long>($"SELECT count(*) FROM {SA.Table}"));
}

public class StockAdjustmentTests : StockAdjustmentTestBase
{
    [PostgresFact]
    public async Task An_approved_stocktake_generates_one_adjustment_against_the_current_book_quantity_without_moving_stock_and_cannot_be_cancelled_afterwards()
    {
        await PrepareAdjustments();
        await Receive(Fifo, 10m, 100m);                                                           // W1, 2026-03-10
        await Receive(Avg, 4m, 40m);
        await Receive(Apple, 30m, 300m);
        var draft = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput(W1.Id, CountDate, "COUNT", null, [C(Fifo, Pcs, 8m, "two missing"), C(Avg, Pcs, 4m, null), C(Apple, Box, 3m, "found a box")])));
        // follow-up M2 of the 3.8B-1 review: every reloaded line offers the units of its item (base first, with the exact factors), not only its saved unit
        var apple = (await Takes.GetAsync(Company.Id, draft.Id))!.Lines.Single(l => l.ItemId == Apple.Id);
        Assert.Equal([(Pcs.Id, 1m, true), (Box.Id, 12m, false)], apple.Units!.Select(u => (u.Id, u.Factor, u.IsBase)));
        Fails(await Adj.GenerateAsync(Admin.Id, Company.Id, draft.Id, draft.Revision), ConfigStatus.Conflict, "stocktake_not_approved");
        var sub = Ok(await Takes.SubmitAsync(Admin.Id, Company.Id, draft.Id, draft.Revision));
        var st = Ok(await Takes.ApproveAsync((await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeApprove], ScopeKind.Company, null)).User.Id, Company.Id, draft.Id, sub.Revision));
        // a movement on the count date after approval: the sheet only turns stale, the adjustment uses the CURRENT book quantity (Avg: book 5, counted 4)
        await Receive(Avg, 1m, 10m, date: new DateOnly(2026, 3, 11));
        var costBefore = await Sql<long>($"SELECT count(*) FROM {CO.Table}");
        Fails(await Adj.GenerateAsync(Admin.Id, Company.Id, st.Id, null), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Adj.GenerateAsync(Admin.Id, Company.Id, st.Id, st.Revision + 1), ConfigStatus.PreconditionFailed, "revision_conflict");
        var a = Ok(await Generate(st));
        Assert.False(a.Replayed);
        Assert.Equal(("SA-2026-000001", "2026-03-11", W1.Id, st.Id, st.Number), (a.Number, a.AdjustmentDate, a.WarehouseId, a.StocktakeId, a.StocktakeNumber));
        Assert.Equal([("FIFO1", -1, 2m, 8m, 10m), ("AVG1", -1, 1m, 4m, 5m), ("APPLE", 1, 6m, 36m, 30m)], a.Lines.Select(l => (l.ItemCode, l.Direction, l.Quantity, l.CountedQuantity, l.BookQuantity)));
        Assert.All(a.Lines, l => Assert.Equal(Pcs.Id, l.UnitId));                                                           // always the base unit (the apple was counted in boxes)
        Assert.Equal(("two missing", (string?)null), (a.Lines[0].Reason, a.Lines[1].Reason));
        Assert.Null(a.Posting);
        Assert.Equal(costBefore, await Sql<long>($"SELECT count(*) FROM {CO.Table}"));                                        // generating moves no stock and books nothing
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {JE.Table}"));
        // idempotent: the same adjustment, whatever the revision sent, and nothing new written
        var again = Ok(await Adj.GenerateAsync(Admin.Id, Company.Id, st.Id, 999));
        Assert.Equal((a.Id, true), (again.Id, again.Replayed));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SA.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stock_adjustment'"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockAdjustmentAuditEvents.Generated}"));
        var withAdj = (await Takes.GetAsync(Company.Id, st.Id))!;
        Assert.Equal((a.Id, a.Number, true), (withAdj.AdjustmentId, withAdj.AdjustmentNumber, withAdj.Stale));
        // the stocktake that has an adjustment cannot be cancelled: by the service, nor by direct SQL
        Fails(await Takes.CancelAsync(Admin.Id, Company.Id, st.Id, st.Revision, "wrong aisle"), ConfigStatus.Conflict, "adjustment_exists");
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 5, {ST.CancelledBy} = gen_random_uuid(), {ST.CancelledAtUtc} = now(), {ST.CancelReason} = 'x', {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{st.Id}'"));
        Assert.Equal(3, (await Takes.GetAsync(Company.Id, st.Id))!.Status);
        // nothing to adjust: refused, nothing written
        var equal = await ApprovedTake(W2, C(Fifo, Pcs, 0m, null));
        Fails(await Generate(equal), ConfigStatus.Conflict, "no_variance");
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SA.Table}"));
    }

    [PostgresFact]
    public async Task Concurrent_generation_yields_exactly_one_adjustment_and_one_number()
    {
        await PrepareAdjustments();
        await Receive(Fifo, 10m, 100m);
        var st = await ApprovedTake(W1, C(Fifo, Pcs, 7m));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Generate(st)));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Single(results.Select(r => r.Value!.Id).Distinct());
        Assert.Equal(1, results.Count(r => !r.Value!.Replayed));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SA.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SAL.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stock_adjustment'"));
        // and posting it concurrently moves the stock once
        var a = results[0].Value!;
        var posts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Adj.PostAsync(Admin.Id, Company.Id, a.Id, null)));
        Assert.All(posts, r => Assert.True(r.IsOk));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SAP.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {JE.Table}"));
        Assert.Equal((7m, 70m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_mixed_adjustment_posts_once_shortages_at_the_engine_cost_surpluses_at_the_entered_amount_with_one_balanced_journal()
    {
        await PrepareAdjustments();
        await Receive(Fifo, 2m, 30m);                                                             // FIFO layers: 2 x 15.00, then 8 x 12.50
        await Receive(Fifo, 8m, 100m);
        await Receive(Avg, 4m, 40m);                                                              // moving average: 10 units for 106.00 -> 10.60
        await Receive(Avg, 6m, 66m);
        var st = await ApprovedTake(W1, C(Fifo, Pcs, 7m), C(Avg, Pcs, 9m), C(Apple, Box, 0.5m));
        var a = Ok(await Generate(st));
        Assert.Equal([(1, -1, 3m), (2, -1, 1m), (3, 1, 6m)], a.Lines.Select(l => (l.LineNo, l.Direction, l.Quantity)));
        var before = await AdjCounts();
        // the poster values the surpluses only, every one of them, never a shortage
        Invalid(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, null), "lines", "amount_required_for_every_surplus_line");
        Invalid(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, 61.2m), (1, 5m))), "lines[1].lineNo", "amount_not_allowed_for_shortage");
        Invalid(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, -1m))), "lines[0].amount", "negative_amount");
        Invalid(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, 61.2m), (9, 1m))), "lines[1].lineNo", "line_not_found");
        Assert.Equal(before, await AdjCounts());
        var p = Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, 61.20m))));
        Assert.Equal([42.50m, 10.60m, 61.20m], p.Lines.Select(l => l.Amount!.Value));                                       // 2 x 15.00 + 1 x 12.50; 1 x 10.60; as entered
        var post = p.Posting!;
        Assert.Equal((114.30m, (int)StockAccountingState.Pending, "2026-03-11"), (post.TotalValue, post.AccountingState, post.EffectiveDate));
        Assert.NotNull(post.IssueOperationId);
        Assert.NotNull(post.ReceiptOperationId);
        Assert.Equal(StockAdjustmentService.IssueKeyOf(a.Id), await Sql<string>($"SELECT {CO.Key} FROM {CO.Table} WHERE {CO.Id} = '{post.IssueOperationId}'"));
        Assert.Equal(StockAdjustmentService.ReceiptKeyOf(a.Id), await Sql<string>($"SELECT {CO.Key} FROM {CO.Table} WHERE {CO.Id} = '{post.ReceiptOperationId}'"));
        Assert.Equal((7m, 87.50m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        Assert.Equal((9m, 95.40m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        Assert.Equal((6m, 61.20m), ((await Pos(Apple)).Quantity, (await Pos(Apple)).Value));
        // one balanced draft entry, gross: surplus Dr inventory / Cr adjustment account, shortages Dr adjustment account / Cr inventory
        var j = (await Journal.GetAsync(Company.Id, post.JournalEntryId!.Value))!;
        Assert.Equal((JournalStatus.Draft, DocumentSource.Automatic, StockAdjustmentService.JournalKeyOf(a.Id), true), (j.Header.Status, j.Header.Source, j.Header.SourceKey, j.Balanced));
        Assert.Equal((114.30m, 114.30m), (j.BaseDebitTotal, j.BaseCreditTotal));
        Assert.Equal([(StockAcc.Id, 61.20m, 0m), (AdjAcc.Id, 53.10m, 0m), (AdjAcc.Id, 0m, 61.20m), (StockAcc.Id, 0m, 53.10m)], j.Lines.Select(l => (l.AccountId, l.BaseDebit, l.BaseCredit)));
        Assert.Equal((await Acc.GetAsync(Company.Id)).Version!.Id, post.AccountingVersionId);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockAdjustmentAuditEvents.Posted}"));
        var after = await AdjCounts();
        Assert.Equal((before.Ops + 2, before.Postings + 1, before.Journals + 1), (after.Ops, after.Postings, after.Journals));
        // exactly once: the identical retry (with or without the amounts) returns it, other amounts are a conflict, nothing is written twice
        Assert.True(Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, 61.20m)))).Replayed);
        Assert.True(Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, null)).Replayed);
        Fails(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, 70m))), ConfigStatus.Conflict, "already_posted");
        Assert.Equal(after, await AdjCounts());
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Posting_with_value_is_refused_until_the_adjustment_counter_account_is_configured_and_the_refusal_leaves_nothing_behind()
    {
        await PrepareAdjustments(adjustmentAccount: false);
        // the version saved before has no adjustment account; saving it again unchanged is still a no-op (the hash of a configuration without one did not change)
        Assert.Equal(1, Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null))).Version!.VersionNo);
        await Receive(Fifo, 10m, 100m);
        var a = Ok(await Generate(await ApprovedTake(W1, C(Fifo, Pcs, 9m), C(Apple, Pcs, 2m))));
        var before = await AdjCounts();
        Fails(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((2, 8m))), ConfigStatus.Conflict, "adjustment_counter_account_not_configured");
        Fails(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((2, 0m))), ConfigStatus.Conflict, "adjustment_counter_account_not_configured");      // the shortage still has value
        Assert.Equal(before, await AdjCounts());                                                                                // no stock, no cost operation, no journal, no posting
        Assert.Equal((10m, 100m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        // validated like the other counter accounts: never an inventory account
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, StockAcc.Id)), "adjustmentCounterAccountId", "counter_equals_inventory_account");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, Guid.NewGuid())), "adjustmentCounterAccountId", "account_not_found");
        var v2 = Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], "adjustments", AdjAcc.Id)));
        Assert.Equal((2, AdjAcc.Id), (v2.Version!.VersionNo, v2.AdjustmentCounterAccountId));
        Assert.Null((await Acc.GetAsync(Company.Id, 1)).AdjustmentCounterAccountId);                                         // the earlier version is never reinterpreted
        var p = Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((2, 8m)))).Posting!;
        Assert.Equal((v2.Version.Id, 18m), (p.AccountingVersionId, p.TotalValue));                                             // 1 x 10.00 out, 8.00 in
        // an adjustment of no value at all books nothing and needs no configuration
        var free = Ok(await Generate(await ApprovedTake(W2, C(Fifo, Pcs, 3m))));                                             // nothing held in W2: a surplus of 3
        var fp = Ok(await Adj.PostAsync(Admin.Id, Company.Id, free.Id, Values((1, 0m)))).Posting!;
        Assert.Equal(((int)StockAccountingState.NotRequired, (Guid?)null, (Guid?)null), (fp.AccountingState, fp.JournalEntryId, fp.AccountingVersionId));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Reversal_restores_quantity_and_value_exactly_cancels_the_unposted_journal_and_happens_once()
    {
        await PrepareAdjustments();
        await Receive(Fifo, 2m, 30m);
        await Receive(Fifo, 8m, 100m);
        await Receive(Avg, 10m, 106m);
        var fifo0 = await Pos(Fifo); var avg0 = await Pos(Avg);
        var a = Ok(await Generate(await ApprovedTake(W1, C(Fifo, Pcs, 7m), C(Avg, Pcs, 9m), C(Apple, Pcs, 6m))));
        var p = Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, Values((3, 61.20m)))).Posting!;
        Fails(await Adj.ReverseAsync(Admin.Id, Company.Id, Guid.NewGuid(), new StockReverseInput("2026-03-20", "x")), ConfigStatus.NotFound, "adjustment_not_found");
        Invalid(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-03-10", "recount")), "effectiveDate", "before_posting_date");
        Invalid(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-04-02", "recount")), "effectiveDate", "must_stay_in_posting_period");   // the journal is not posted yet
        Invalid(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-03-20", " ")), "reason", "required");
        var r = Ok(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-03-20", "counted the wrong aisle")));
        var rv = r.Posting!.Reversal!;
        Assert.Equal(((int)StockAccountingState.Reversed, 1, p.JournalEntryId), (r.Posting.AccountingState, rv.JournalAction, rv.JournalEntryId));
        Assert.NotNull(rv.IssueOperationId);
        Assert.NotNull(rv.ReceiptOperationId);
        Assert.Equal(JournalStatus.Cancelled, (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!.Header.Status);
        var fifo1 = await Pos(Fifo); var avg1 = await Pos(Avg); var apple1 = await Pos(Apple);
        Assert.Equal((fifo0.Quantity, fifo0.Value, avg0.Quantity, avg0.Value, 0m, 0m), (fifo1.Quantity, fifo1.Value, avg1.Quantity, avg1.Value, apple1.Quantity, apple1.Value));   // exactly as before
        Assert.Equal([15m, 12.5m], fifo1.OpenLayers.Select(l => l.RemainingAmount / l.RemainingQuantity));                // the FIFO layers are restored, not re-priced
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockAdjustmentAuditEvents.Reversed}"));
        // once: the identical retry returns it, another one is refused, nothing more is written
        var ops = await Sql<long>($"SELECT count(*) FROM {CO.Table}");
        Assert.True(Ok(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-03-20", "counted the wrong aisle"))).Replayed);
        Fails(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-03-21", "again")), ConfigStatus.Conflict, "already_reversed");
        Assert.Equal(ops, await Sql<long>($"SELECT count(*) FROM {CO.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SAR.Table}"));
        await AssertConsistent();
        // a not posted adjustment cannot be reversed
        var other = Ok(await Generate(await ApprovedTake(W2, C(Fifo, Pcs, 1m))));
        Fails(await Adj.ReverseAsync(Admin.Id, Company.Id, other.Id, new StockReverseInput("2026-03-20", "x")), ConfigStatus.Conflict, "adjustment_not_posted");
    }

    [PostgresFact]
    public async Task A_closed_period_refuses_generation_and_posting_and_the_valuation_probe_watches_adjustments_until_the_ledger_agrees()
    {
        await PrepareAdjustments();
        // stock received through posted receipts, so the ledger and the costing ledger agree before the count
        var receipt = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        await PostJournal(Ok(await Post(receipt, Amounts(100m))));
        var st = await ApprovedTake(W1, C(Fifo, Pcs, 8m));
        var march = await March();
        var closed = Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        Fails(await Generate(st), ConfigStatus.Conflict, "fiscal_period_soft_closed");
        var reopened = Ok(await CloseCalendar.ReopenAsync(Admin.Id, Company.Id, march.Id, closed.Revision));
        var a = Ok(await Generate(st));
        var again = Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, reopened.Revision));                  // generated but not posted: no stock or ledger effect yet
        var before = await AdjCounts();
        Fails(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, null), ConfigStatus.Conflict, "fiscal_period_soft_closed");
        Assert.Equal(before, await AdjCounts());
        Ok(await CloseCalendar.ReopenAsync(Admin.Id, Company.Id, march.Id, again.Revision));
        var p = Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, null)).Posting!;
        Assert.Equal(20m, p.TotalValue);
        var period = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, period.Id, period.Revision), ConfigStatus.Conflict, "period_close_blocked");   // the adjustment's entry is still a draft
        // an entry cancelled outside the reversal leaves the adjustment FAILED: it blocks the close until the stock is reversed
        var j = (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!;
        Ok(await Journal.CancelAsync(Admin.Id, Company.Id, j.Header.Id, j.Header.Revision));
        Assert.Equal((int)StockAccountingState.Failed, (await Adj.GetAsync(Company.Id, a.Id))!.Posting!.AccountingState);
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, period.Id, period.Revision), ConfigStatus.Conflict, "period_close_blocked");
        Ok(await Adj.ReverseAsync(Admin.Id, Company.Id, a.Id, new StockReverseInput("2026-03-20", "entry cancelled")));
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, period.Id, period.Revision));                              // stock back, ledger never had it: they agree again
    }

    [PostgresFact]
    public async Task Generating_posting_reversing_and_reading_are_warehouse_scoped_and_nothing_is_disclosed()
    {
        await PrepareAdjustments();
        await Receive(Fifo, 10m, 100m);
        var st = await ApprovedTake(W1, C(Fifo, Pcs, 9m));
        var (onlyView, _) = await Member([PermissionCatalog.Codes.StocktakeView], ScopeKind.Warehouse, W1.Id);
        var (otherWh, _) = await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StockDocsCreate, PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsPost, PermissionCatalog.Codes.StockDocsReverse], ScopeKind.Warehouse, W2.Id);
        var (maker, _) = await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StockDocsCreate], ScopeKind.Warehouse, W1.Id);
        Fails(await AdjMgmt.GenerateAsync(onlyView.Id, Company.Id, st.Id, st.Revision), ConfigStatus.Forbidden, "forbidden");        // viewing a count is not creating a movement
        Fails(await AdjMgmt.GenerateAsync(otherWh.Id, Company.Id, st.Id, st.Revision), ConfigStatus.Forbidden, "forbidden");         // a W2 grant does not reach W1
        Fails(await AdjMgmt.GenerateAsync(maker.Id, Company.Id, Guid.NewGuid(), 1), ConfigStatus.Forbidden, "forbidden");            // no existence probing
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {SA.Table}"));
        var caps = await TakeMgmt.CapabilitiesAsync(maker.Id, Company.Id);
        Assert.Equal([W1.Id], caps.Adjust!);
        Assert.Equal((0, 0), (caps.Post!.Count, caps.Reverse!.Count));
        var a = Ok(await AdjMgmt.GenerateAsync(maker.Id, Company.Id, st.Id, st.Revision));
        Fails(await AdjMgmt.PostAsync(maker.Id, Company.Id, a.Id, null), ConfigStatus.Forbidden, "forbidden");                          // creating is not posting
        Fails(await AdjMgmt.PostAsync(otherWh.Id, Company.Id, a.Id, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await AdjMgmt.GetAsync(otherWh.Id, Company.Id, a.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await AdjMgmt.GetAsync(otherWh.Id, Company.Id, Guid.NewGuid()), ConfigStatus.Forbidden, "forbidden");
        var (postOnly, _) = await Member([PermissionCatalog.Codes.StockDocsPost], ScopeKind.Warehouse, W1.Id);
        Fails(await AdjMgmt.PostAsync(postOnly.Id, Company.Id, a.Id, null), ConfigStatus.Forbidden, "forbidden");                       // posting needs the right to see it
        var (poster, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsPost], ScopeKind.Warehouse, W1.Id);
        Assert.Equal(a.Id, Ok(await AdjMgmt.GetAsync(poster.Id, Company.Id, a.Id)).Id);
        Assert.NotNull(Ok(await AdjMgmt.PostAsync(poster.Id, Company.Id, a.Id, null)).Posting);
        Fails(await AdjMgmt.ReverseAsync(poster.Id, Company.Id, a.Id, new StockReverseInput("2026-03-20", "x")), ConfigStatus.Forbidden, "forbidden");   // posting is not reversing
        var (reverser, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsReverse], ScopeKind.Warehouse, W1.Id);
        Assert.NotNull(Ok(await AdjMgmt.ReverseAsync(reverser.Id, Company.Id, a.Id, new StockReverseInput("2026-03-20", "x"))).Posting!.Reversal);
        Assert.Equal((10m, 100m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_one_adjustment_per_approved_stocktake_exact_lines_and_append_only_records()
    {
        await PrepareAdjustments();
        await Receive(Fifo, 10m, 100m);
        var st = await ApprovedTake(W1, C(Fifo, Pcs, 9m));
        var a = Ok(await Generate(st));
        Ok(await Adj.PostAsync(Admin.Id, Company.Id, a.Id, null));
        var year = $"(SELECT {CompanySchema.FiscalYear.Id} FROM {CompanySchema.FiscalYear.Table} LIMIT 1)";
        string Insert(Guid stocktake, Guid warehouse, string date, string number) =>
            $"INSERT INTO {SA.Table} ({SA.Id}, {SA.StocktakeId}, {SA.BranchId}, {SA.WarehouseId}, {SA.AdjustmentDate}, {SA.Number}, {SA.FiscalYearId}, {SA.CreatedBy}, {SA.CreatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), '{stocktake}', '{st.BranchId}', '{warehouse}', '{date}', '{number}', {year}, gen_random_uuid(), now())";
        Assert.NotNull(await Raw(Insert(st.Id, W1.Id, CountDate, "SA-HACK")));                                                    // a second adjustment for the same stocktake
        var draft = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput(W1.Id, CountDate, null, null, [C(Fifo, Pcs, 1m)])));
        Assert.NotNull(await Raw(Insert(draft.Id, W1.Id, CountDate, "SA-H2")));                                                   // a stocktake that is not approved
        var other = await ApprovedTake(W1, C(Fifo, Pcs, 5m));
        Assert.NotNull(await Raw(Insert(other.Id, W2.Id, CountDate, "SA-H3")));                                                   // another warehouse
        Assert.NotNull(await Raw(Insert(other.Id, W1.Id, "2026-03-12", "SA-H4")));                                               // another date
        Assert.NotNull(await Raw(Insert(other.Id, W1.Id, CountDate, "SA-H5")));                                                   // no line (judged at commit)
        // lines: exact arithmetic, never changed, never added after posting
        Assert.NotNull(await Raw($"INSERT INTO {SAL.Table} ({SAL.AdjustmentId}, {SAL.LineNo}, {SAL.ItemId}, {SAL.UnitId}, {SAL.Direction}, {SAL.Quantity}, {SAL.CountedQuantity}, {SAL.BookQuantity}) VALUES ('{a.Id}', 9, '{Avg.Id}', '{Pcs.Id}', 1, 1, 1, 0)"));
        Assert.NotNull(await Raw($"UPDATE {SAL.Table} SET {SAL.Quantity} = 2, {SAL.CountedQuantity} = 8 WHERE {SAL.AdjustmentId} = '{a.Id}'"));
        Assert.NotNull(await Raw($"DELETE FROM {SAL.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {SAL.Table}"));
        var ok = Ok(await Generate(other));
        Assert.NotNull(await Raw($"INSERT INTO {SAL.Table} ({SAL.AdjustmentId}, {SAL.LineNo}, {SAL.ItemId}, {SAL.UnitId}, {SAL.Direction}, {SAL.Quantity}, {SAL.CountedQuantity}, {SAL.BookQuantity}) VALUES ('{ok.Id}', 9, '{Avg.Id}', '{Pcs.Id}', 1, 2, 1, 0)"));   // 2 x +1 <> 1 - 0
        // the adjustment, its posting, posting lines and reversal are append-only
        foreach (var t in new[] { SA.Table, SAP.Table, SAPL.Table, SAR.Table })
        {
            if (t != SAR.Table) Assert.NotNull(await Raw($"DELETE FROM {t}"));                                              // (no reversal row exists to delete)
            Assert.NotNull(await Raw($"TRUNCATE {t}"));
        }
        Assert.NotNull(await Raw($"UPDATE {SA.Table} SET {SA.Number} = 'X'"));
        Assert.NotNull(await Raw($"UPDATE {SAP.Table} SET {SAP.TotalValue} = 0"));
        Assert.NotNull(await Raw($"UPDATE {SAPL.Table} SET {SAPL.Amount} = 0"));
        Assert.NotNull(await Raw($"INSERT INTO {SAP.Table} ({SAP.Id}, {SAP.AdjustmentId}, {SAP.EffectiveDate}, {SAP.PostedAtUtc}, {SAP.PostedBy}, {SAP.TotalValue}) VALUES (gen_random_uuid(), '{ok.Id}', '{CountDate}', now(), gen_random_uuid(), 0)"));   // no costing operation
        Assert.Equal((2L, 1L), (await Sql<long>($"SELECT count(*) FROM {SA.Table}"), await Sql<long>($"SELECT count(*) FROM {SAP.Table}")));
    }
}

/// <summary>End-to-end HTTP tests of the adjustment endpoints (Web and Windows Desktop use exactly these).</summary>
public class StockAdjustmentApiTests : StockAdjustmentTestBase
{
    [PostgresFact]
    public async Task An_adjustment_is_generated_posted_and_reversed_over_http_with_the_stocktake_etag_and_warehouse_scope()
    {
        await StartApi();
        await PrepareAdjustments();
        await Receive(Fifo, 10m, 100m);
        var st = await ApprovedTake(W1, C(Fifo, Pcs, 8m), C(Apple, Pcs, 2m));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/api/v1/company/stocktakes/{st.Id}/adjustment", new { })).Status);
        var admin = await SignIn(Admin);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, $"/api/v1/company/stocktakes/{st.Id}/adjustment", null, admin)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"/api/v1/company/stocktakes/{st.Id}/adjustment", null, admin, ifMatch: $"\"{st.Revision + 1}\"")).Status);
        var gen = await Send(HttpMethod.Post, $"/api/v1/company/stocktakes/{st.Id}/adjustment", null, admin, ifMatch: $"\"{st.Revision}\"");
        Assert.Equal((HttpStatusCode.OK, "SA-2026-000001"), (gen.Status, gen.Json.GetProperty("number").GetString()));
        var id = gen.Json.GetProperty("id").GetGuid();
        Assert.Equal((-1, 1), (gen.Json.GetProperty("lines")[0].GetProperty("direction").GetInt32(), gen.Json.GetProperty("lines")[1].GetProperty("direction").GetInt32()));
        var take = await Send(HttpMethod.Get, $"/api/v1/company/stocktakes/{st.Id}", cookie: admin);
        Assert.Equal(id, take.Json.GetProperty("adjustmentId").GetGuid());
        Assert.Equal(2, take.Json.GetProperty("lines")[1].GetProperty("units").GetArrayLength());                               // follow-up M2: the units of every line (Apple: piece and box)
        var caps = await Send(HttpMethod.Get, "/api/v1/company/stocktakes/capabilities", cookie: admin);
        Assert.Equal(2, caps.Json.GetProperty("post").GetArrayLength());
        var bad = await Send(HttpMethod.Post, $"/api/v1/company/stock-adjustments/{id}/posting", new { lines = new[] { new { lineNo = 1, amount = 5m } } }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var posted = await Send(HttpMethod.Post, $"/api/v1/company/stock-adjustments/{id}/posting", new { lines = new[] { new { lineNo = 2, amount = 21m } } }, admin);
        Assert.Equal((HttpStatusCode.OK, 41m, (int)StockAccountingState.Pending), (posted.Status, posted.Json.GetProperty("posting").GetProperty("totalValue").GetDecimal(), posted.Json.GetProperty("posting").GetProperty("accountingState").GetInt32()));
        Assert.Contains("no-store", posted.Headers.CacheControl!.ToString());
        var cancel = await Send(HttpMethod.Post, $"/api/v1/company/stocktakes/{st.Id}/cancel", new { reason = "x" }, admin, ifMatch: $"\"{st.Revision}\"");
        Assert.Equal((HttpStatusCode.Conflict, "adjustment_exists"), (cancel.Status, cancel.Error));
        var (outsider, _) = await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsReverse], ScopeKind.Warehouse, W2.Id);
        var other = await SignIn(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"/api/v1/company/stock-adjustments/{id}", cookie: other)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/company/stock-adjustments/{id}/reversal", new { effectiveDate = "2026-03-20", reason = "x" }, other)).Status);
        var reversed = await Send(HttpMethod.Post, $"/api/v1/company/stock-adjustments/{id}/reversal", new { effectiveDate = "2026-03-20", reason = "recount" }, admin);
        Assert.Equal((HttpStatusCode.OK, (int)StockAccountingState.Reversed), (reversed.Status, reversed.Json.GetProperty("posting").GetProperty("accountingState").GetInt32()));
        Assert.Equal((10m, 100m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
    }
}
