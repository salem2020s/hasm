using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemGroup;
using NS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeStockSetting;
using NF = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeStockItemFlag;
using NA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeExceptionApproval;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using NX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeSettlement;
using NR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligationReversal;
using SAP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPosting;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using IAV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingVersion;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8B-3: the negative-stock exception (company migration 0020, system migration 0022) against real PostgreSQL: the split of an issue into the covered engine part and a provisional obligation, settlement by later
/// stock-document receipts oldest first with exact figures and balanced journals, reversals in order, the close probe and reconciliation, guarded changes, the stock-adjustment conditions (R15), authorization and every
/// database invariant attacked directly. Disposable cluster only.
/// </summary>
public abstract class NegativeStockTestBase : StockAdjustmentTestBase
{
    protected NegativeStockService Neg = null!;
    protected NegativeStockManagementService NegMgmt = null!;
    protected AccountView OblAcc = null!, VarAcc = null!;
    protected UserRecord Excepter = null!;
    protected ChartOfAccountsService Chart = null!;

    /// <summary>Posting and adjustments prepared as in 3.7B/3.8B-2; a new configuration version with the obligation account O (and the variance account V unless <paramref name="variance"/> is false), the exception enabled and the
    /// three moving-average items flagged eligible; an exception approver holding the permission company-wide.</summary>
    protected async Task PrepareNegative(bool enable = true, bool variance = true, bool secondApprover = false)
    {
        await PrepareAdjustments();
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Neg = new NegativeStockService(Resolver, tx, Cost, Clock);
        NegMgmt = new NegativeStockManagementService(Rbac, Docs, Neg);
        Chart = new ChartOfAccountsService(Resolver, tx, new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        OblAcc = Ok(await Chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("2301", null, "التزامات مخزون مؤقتة", "Provisional stock obligations", "liability", null, false, null, null)));
        VarAcc = Ok(await Chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("5301", null, "فروقات تسوية", "Settlement variance", "expense", null, false, null, null)));
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 2, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, OblAcc.Id, variance ? VarAcc.Id : null)));
        if (enable) Ok(await Neg.SaveSettingsAsync(Admin.Id, Company.Id, 0, new NegativeStockSettingsInput(true, secondApprover, "pilot")));
        foreach (var item in new[] { Avg, Avg2, KgAvg }) Ok(await Neg.SetItemFlagAsync(Admin.Id, Company.Id, item.Id, new NegativeItemFlagInput(true, null)));
        Excepter = (await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsNegativeException], ScopeKind.Company, null)).User;
    }

    protected async Task<StockPostingView> ReceiveDoc(ItemView item, decimal qty, decimal amount, string date, WarehouseView? wh = null, UnitView? unit = null) =>
        Ok(await Post(await Approved(Receipt(wh ?? W1, date, L(item, unit ?? Pcs, qty))), Amounts(amount)));

    protected async Task<StockPostingView> IssueDoc(ItemView item, decimal qty, string date, WarehouseView? wh = null, UnitView? unit = null) =>
        Ok(await Post(await Approved(Issue(wh ?? W1, date, L(item, unit ?? Pcs, qty)))));

    protected static NegativeApprovalInput Why(params NegativeManualCostInput[] manual) => new("urgent dispatch before the delivery", manual.Length == 0 ? null : manual);

    /// <summary>An approved issue document of <paramref name="qty"/> with its exception approved by <see cref="Excepter"/> (never its creator, the administrator, who posts it).</summary>
    protected async Task<StockDocumentView> Exceptional(ItemView item, decimal qty, string date, WarehouseView? wh = null, UnitView? unit = null, params NegativeManualCostInput[] manual)
    {
        var doc = await Approved(Issue(wh ?? W1, date, L(item, unit ?? Pcs, qty)));
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why(manual)));
        return doc;
    }

    /// <summary>Stock to an empty position with a known last average: a receipt then an ordinary issue of all of it (the position is empty, its last pair is kept).</summary>
    protected async Task Emptied(ItemView item, decimal qty, decimal amount, WarehouseView? wh = null)
    {
        await ReceiveDoc(item, qty, amount, "2026-03-10", wh);
        await IssueDoc(item, qty, "2026-03-11", wh);
    }

    protected Task<IReadOnlyList<NegativeObligationView>> Obligations(bool openOnly = false) => Neg.ListObligationsAsync(Company.Id, null, openOnly);
    protected async Task<NegativeObligationView> ObligationOf(StockDocumentView doc) => (await Obligations()).Single(o => o.DocumentId == doc.Id);
    protected async Task<List<(Guid Account, decimal Debit, decimal Credit)>> Lines(Guid journalId) => [.. (await Journal.GetAsync(Company.Id, journalId))!.Lines.Select(l => (l.AccountId, l.BaseDebit, l.BaseCredit))];
    protected async Task<List<NegativeSettlementView>> SettlementsOf(StockDocumentView receipt) =>
        [.. (await Obligations()).SelectMany(o => o.Settlements).Where(s => s.ReceiptDocumentId == receipt.Id && s.ReversesSettlementId is null)];
    protected async Task<(decimal? Q, decimal? V)> LastAverage(ItemView item, WarehouseView? wh = null)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var q = new NpgsqlCommand($"SELECT {PO.LastAverageQuantity}, {PO.LastAverageValue} FROM {PO.Table} WHERE {PO.ItemId} = '{item.Id}' AND {PO.WarehouseId} = '{(wh ?? W1).Id}'", conn);
        await using var r = await q.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.IsDBNull(0) ? null : r.GetDecimal(0), r.IsDBNull(1) ? null : r.GetDecimal(1));
    }

    /// <summary>Runs statements in one committed transaction as the database owner and returns the PostgreSQL message (null when everything committed): deferred guards answer at commit.</summary>
    protected async Task<string?> RawMessage(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex) { return ex.MessageText; }
    }

    protected async Task<(long Obligations, long Settlements, long Reversals, long Ops, long Journals)> NegCounts() =>
        (await Rows(NO.Table), await Rows(NX.Table), await Rows(NR.Table), await Rows(CO.Table), await Rows(JE.Table));
}

public class NegativeStockTests : NegativeStockTestBase
{
    [PostgresFact]
    public async Task A_mixed_issue_takes_the_covered_part_through_the_engine_and_the_rest_into_one_obligation_with_the_balanced_journal_of_5_1()
    {
        await PrepareNegative();
        await ReceiveDoc(Avg, 3m, 15m, "2026-03-10");                                                       // 3 on hand at an average of 5.00
        var doc = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 10m)));
        var preview = Ok(await NegMgmt.GetExceptionAsync(Excepter.Id, Company.Id, doc.Id));
        var line = Assert.Single(preview.Plan.Lines);
        Assert.Equal((true, 3m, 3m, 7m, 1, 35.00m, 5m), (preview.Plan.Needed, line.OnHand, line.Covered, line.Uncovered, line.Source!.Value, line.ProvisionalTotal!.Value, line.ProvisionalUnitCost!.Value));
        Assert.Equal((3m, 15m), (line.SourceQuantity, line.SourceValue));                                   // the (quantity, value) pair, never a stored unit cost
        Assert.Equal(0L, await Rows(NA.Table));                                                             // a preview writes nothing
        Fails(await Post(doc), ConfigStatus.Conflict, "negative_exception_approval_required");
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why()));
        var p = Ok(await Post(doc));
        Assert.Equal((50m, (int)StockAccountingState.Pending), (p.TotalValue, p.AccountingState));
        Assert.Equal([(1, 10m, 50m)], p.Lines.Select(l => (l.LineNo, l.BaseQuantity, l.Amount)));             // 3 x 5.00 at cost + 7 x 5.00 provisional
        Assert.Equal((0m, 0m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));                       // the position is a true physical stock: never negative
        Assert.Equal(((decimal?)3m, (decimal?)15m), await LastAverage(Avg));                                  // the last average is kept when the position falls to zero
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.OperationId} = '{p.CostOperationId}'"));
        // one balanced entry: Dr K 50.00 / Cr I 15.00 / Cr O 35.00
        Assert.Equal([(CogsAcc.Id, 50m, 0m), (StockAcc.Id, 0m, 15m), (OblAcc.Id, 0m, 35m)], await Lines(p.JournalEntryId!.Value));
        var o = await ObligationOf(doc);
        Assert.Equal((7m, 35m, 7m, 35m, 1, 1, (string?)null, p.JournalEntryId!.Value, OblAcc.Id, VarAcc.Id), (o.OriginalQuantity, o.ProvisionalTotal, o.OpenQuantity, o.OpenProvisional, o.Source, o.Status, o.Justification, o.JournalEntryId, o.ObligationAccountId, o.VarianceAccountId));
        // exactly once: a repeated request returns the posting and creates nothing
        var counts = await NegCounts();
        Assert.True(Ok(await Post(doc)).Replayed);
        Assert.Equal(counts, await NegCounts());
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_fully_uncovered_issue_and_a_fully_matched_receipt_book_5_2_with_positive_and_negative_differences_and_nothing_enters_inventory()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 15m);
        var issue = await Exceptional(Avg, 7m, "2026-03-12");
        var ip = Ok(await Post(issue));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.OperationId} = '{ip.CostOperationId}'"));     // a fully uncovered issue: an issue operation without entries (R16)
        Assert.Equal(2, await Sql<short>($"SELECT {CO.Kind} FROM {CO.Table} WHERE {CO.Id} = '{ip.CostOperationId}'"));
        Assert.Equal([(CogsAcc.Id, 35m, 0m), (OblAcc.Id, 0m, 35m)], await Lines(ip.JournalEntryId!.Value));
        // 5.2: a receipt of 7 at 6.00 settles all 7: Dr O 35.00, Dr V 7.00 / Cr G 42.00; the position is unchanged
        var receipt = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 7m)));
        var rp = Ok(await Post(receipt, Amounts(42m)));
        Assert.Equal((42m, (Guid?)null, (int)StockAccountingState.NotRequired), (rp.TotalValue, rp.JournalEntryId, rp.AccountingState));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.OperationId} = '{rp.CostOperationId}'"));    // a fully matched receipt: a receipt operation without entries
        Assert.Equal((0m, 0m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        var s = Assert.Single(await SettlementsOf(receipt));
        Assert.Equal((7m, 35m, 42m, 7m, "2026-03-13"), (s.MatchedQuantity, s.ProvisionalAmount, s.ActualAmount, s.Difference, s.EffectiveDate));
        Assert.Equal([(OblAcc.Id, 35m, 0m), (VarAcc.Id, 7m, 0m), (GoodsAcc.Id, 0m, 42m)], await Lines(s.JournalEntryId!.Value));
        var sj = (await Journal.GetAsync(Company.Id, s.JournalEntryId!.Value))!;
        Assert.Equal((JournalStatus.Draft, DocumentSource.Automatic, true), (sj.Header.Status, sj.Header.Source, sj.Balanced));
        Assert.StartsWith("stock_negative_settlement:", sj.Header.SourceKey);
        var o = await ObligationOf(issue);
        Assert.Equal((0m, 0m, 2), (o.OpenQuantity, o.OpenProvisional, o.Status));
        // the actual cost below the provisional one: Dr O 35.00 / Cr V 7.00, Cr G 28.00
        await Emptied(Avg2, 3m, 15m, W2);
        Ok(await Post(await Exceptional(Avg2, 7m, "2026-03-12", W2)));
        var cheaper = await Approved(Receipt(W2, "2026-03-13", L(Avg2, Pcs, 7m)));
        Ok(await Post(cheaper, Amounts(28m)));
        var s2 = Assert.Single(await SettlementsOf(cheaper));
        Assert.Equal((35m, 28m, -7m), (s2.ProvisionalAmount, s2.ActualAmount, s2.Difference));
        Assert.Equal([(OblAcc.Id, 35m, 0m), (GoodsAcc.Id, 0m, 28m), (VarAcc.Id, 0m, 7m)], await Lines(s2.JournalEntryId!.Value));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_partial_then_a_larger_receipt_settle_5_3_and_only_the_rest_enters_the_weighted_average_at_its_actual_cost()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 15m);
        var issue = await Exceptional(Avg, 7m, "2026-03-12");
        Ok(await Post(issue));                                                                                // obligation 7 at 35.00
        var r1 = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 4m)));
        Ok(await Post(r1, Amounts(24m)));
        var s1 = Assert.Single(await SettlementsOf(r1));
        Assert.Equal((4m, 20m, 24m, 4m), (s1.MatchedQuantity, s1.ProvisionalAmount, s1.ActualAmount, s1.Difference));
        Assert.Equal([(OblAcc.Id, 20m, 0m), (VarAcc.Id, 4m, 0m), (GoodsAcc.Id, 0m, 24m)], await Lines(s1.JournalEntryId!.Value));
        Assert.Equal((3m, 15m), ((await ObligationOf(issue)).OpenQuantity, (await ObligationOf(issue)).OpenProvisional));
        var r2 = await Approved(Receipt(W1, "2026-03-14", L(Avg, Pcs, 10m)));
        var p2 = Ok(await Post(r2, Amounts(45m)));
        var s2 = Assert.Single(await SettlementsOf(r2));
        Assert.Equal((3m, 15m, 13.50m, -1.50m), (s2.MatchedQuantity, s2.ProvisionalAmount, s2.ActualAmount, s2.Difference));
        Assert.Equal([(OblAcc.Id, 15m, 0m), (GoodsAcc.Id, 0m, 13.50m), (VarAcc.Id, 0m, 1.50m)], await Lines(s2.JournalEntryId!.Value));
        // the remaining 7 units enter inventory normally at 4.50: Dr I 31.50 / Cr G 31.50; the weighted average never sees the provisional cost
        Assert.Equal([(StockAcc.Id, 31.50m, 0m), (GoodsAcc.Id, 0m, 31.50m)], await Lines(p2.JournalEntryId!.Value));
        var pos = await Pos(Avg);
        Assert.Equal((7m, 31.50m, 4.5m), (pos.Quantity, pos.Value, pos.AverageUnitCost));
        Assert.Equal(((decimal?)7m, (decimal?)31.50m), await LastAverage(Avg));
        Assert.Equal(45m, p2.TotalValue);                                                                     // the document's whole entered value
        Assert.Equal((0m, 2), ((await ObligationOf(issue)).OpenQuantity, (await ObligationOf(issue)).Status));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Several_obligations_are_settled_oldest_first_by_date_then_sequence_with_exact_remainders_across_three_partial_matches()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 10m);                                                                          // last average pair (3, 10.00)
        var a = await Exceptional(Avg, 3m, "2026-03-12");
        Ok(await Post(a));
        var b = await Exceptional(Avg, 2m, "2026-03-12");                                                     // the same date: the creation sequence decides
        Ok(await Post(b));
        Assert.Equal((10m, 6.67m), ((await ObligationOf(a)).ProvisionalTotal, (await ObligationOf(b)).ProvisionalTotal));   // 10.00 x 2 / 3 rounded once
        Assert.True((await ObligationOf(a)).Seq < (await ObligationOf(b)).Seq);
        var receipts = new List<StockDocumentView>();
        foreach (var day in new[] { "2026-03-13", "2026-03-14", "2026-03-15" })
        {
            var r = await Approved(Receipt(W1, day, L(Avg, Pcs, 1m)));
            Ok(await Post(r, Amounts(5m)));
            receipts.Add(r);
        }
        // three partial matches of A: 10.00 x 1/3 = 3.33, then 6.67 x 1/2 = 3.34 (half away from zero), then the exact remainder 3.33 on the final match
        var parts = new List<NegativeSettlementView>();
        foreach (var r in receipts) parts.Add(Assert.Single(await SettlementsOf(r)));
        Assert.Equal([3.33m, 3.34m, 3.33m], parts.Select(x => x.ProvisionalAmount));
        Assert.Equal(10m, parts.Sum(x => x.ProvisionalAmount));
        var settledA = (await ObligationOf(a)).Settlements.Select(s => s.Id).ToHashSet();
        Assert.All(parts, x => Assert.Contains(x.Id, settledA));                                            // all three on the OLDEST obligation
        Assert.Equal((0m, 2m), ((await ObligationOf(a)).OpenQuantity, (await ObligationOf(b)).OpenQuantity));   // B untouched until A is settled
        // a receipt of 3 at 9.00 settles the 2 of B (the exact open provisional 6.67) and the actual 6.00; 1 unit at the receipt remainder 3.00 enters the position
        var last = await Approved(Receipt(W1, "2026-03-16", L(Avg, Pcs, 3m)));
        var lp = Ok(await Post(last, Amounts(9m)));
        var sb = Assert.Single(await SettlementsOf(last));
        Assert.Equal((2m, 6.67m, 6m, -0.67m), (sb.MatchedQuantity, sb.ProvisionalAmount, sb.ActualAmount, sb.Difference));
        Assert.Equal((1m, 3m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        Assert.Equal([(StockAcc.Id, 3m, 0m), (GoodsAcc.Id, 0m, 3m)], await Lines(lp.JournalEntryId!.Value));
        Assert.Empty(await Obligations(openOnly: true));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task The_provisional_cost_comes_from_the_last_average_else_the_last_stock_document_receipt_else_a_justified_manual_total_and_is_never_zero()
    {
        await PrepareNegative();
        // source 2: the last average is not valid (zero value after free goods), the latest stock-document receipt with value prices the uncovered quantity
        await ReceiveDoc(KgAvg, 2m, 10m, "2026-03-10", unit: Kg);
        await IssueDoc(KgAvg, 2m, "2026-03-11", unit: Kg);
        await ReceiveDoc(KgAvg, 3m, 0m, "2026-03-11", unit: Kg);
        await IssueDoc(KgAvg, 3m, "2026-03-11", unit: Kg);
        Assert.Equal(((decimal?)3m, (decimal?)0m), await LastAverage(KgAvg));
        var kg = await Exceptional(KgAvg, 5m, "2026-03-12", unit: Kg);
        Ok(await Post(kg));
        var ok = await ObligationOf(kg);
        Assert.Equal((2, 25m), (ok.Source, ok.ProvisionalTotal));                                           // 10.00 x 5 / 2
        Assert.NotNull(await Sql<long?>($"SELECT {NO.SourceEntryId} FROM {NO.Table} WHERE {NO.Id} = '{ok.Id}'"));
        // source 3: nothing to price from; a manual total needs a justification and is never zero
        var none = await Approved(Issue(W1, "2026-03-12", L(Avg2, Pcs, 4m)));
        var refused = await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, none.Id, none.Revision, Why());
        Fails(refused, ConfigStatus.Conflict, "provisional_cost_unavailable");
        Assert.Contains(refused.Fields!, f => f.Field == "lines[0]" && f.Code == "provisional_cost_unavailable");
        Invalid(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, none.Id, none.Revision, Why(new NegativeManualCostInput(1, 20m, " "))), "manualCosts[0].justification", "required");
        Invalid(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, none.Id, none.Revision, Why(new NegativeManualCostInput(1, 0m, "supplier quote"))), "manualCosts[0].amount", "must_be_positive");
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, none.Id, none.Revision, Why(new NegativeManualCostInput(1, 20.001m, "supplier quote"))), ConfigStatus.Conflict, "invalid_amount");
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {NA.Table} WHERE {NA.DocumentId} = '{none.Id}'"));
        var approved = Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, none.Id, none.Revision, Why(new NegativeManualCostInput(1, 20m, "supplier quote 2026-118"))));
        Assert.Equal((3, 20m, "supplier quote 2026-118", 1, 1), (approved.Plan.Lines[0].Source!.Value, approved.Plan.Lines[0].ProvisionalTotal!.Value, approved.Plan.Lines[0].Justification, approved.ApprovalsRequired, approved.ValidApprovals));
        Ok(await Post(none));
        var manual = await ObligationOf(none);
        Assert.Equal((3, 20m, "supplier quote 2026-118"), (manual.Source, manual.ProvisionalTotal, manual.Justification));
        // a manual total is not allowed while a source exists
        await ReceiveDoc(Avg, 2m, 8m, "2026-03-10");
        var avg = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 5m)));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, avg.Id, avg.Revision, Why(new NegativeManualCostInput(1, 20m, "quote"))), ConfigStatus.Conflict, "manual_cost_not_allowed");
        // a provisional total that rounds to zero is refused, never accepted as zero
        await Emptied(Avg, 0m + 1000m, 0.01m, W2);
        var tiny = await Approved(Issue(W2, "2026-03-12", L(Avg, Pcs, 1m)));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, tiny.Id, tiny.Revision, Why()), ConfigStatus.Conflict, "provisional_cost_unavailable");
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task The_exception_is_refused_by_default_and_needs_an_eligible_item_the_enablement_the_permission_and_an_approver_who_is_neither_creator_nor_poster()
    {
        await PrepareNegative(enable: false);
        // eligibility: moving average, no tracking (service and PostgreSQL)
        Fails(await Neg.SetItemFlagAsync(Admin.Id, Company.Id, Fifo.Id, new NegativeItemFlagInput(true, null)), ConfigStatus.Conflict, "negative_exception_item_not_eligible");
        Fails(await Neg.SetItemFlagAsync(Admin.Id, Company.Id, Lot.Id, new NegativeItemFlagInput(true, null)), ConfigStatus.Conflict, "negative_exception_item_not_eligible");
        Assert.StartsWith("negative_exception_item_not_eligible", await RawMessage($"INSERT INTO {NF.Table} ({NF.ItemId}, {NF.Eligible}, {NF.ChangedBy}, {NF.ChangedAtUtc}) VALUES ('{Fifo.Id}', true, gen_random_uuid(), now())"));
        Assert.StartsWith("negative_exception_item_not_eligible", await RawMessage($"INSERT INTO {NF.Table} ({NF.ItemId}, {NF.Eligible}, {NF.ChangedBy}, {NF.ChangedAtUtc}) VALUES ('{Lot.Id}', true, gen_random_uuid(), now())"));
        await Emptied(Avg, 3m, 15m);
        await ReceiveDoc(Fifo, 2m, 20m, "2026-03-10");
        var covered = await Approved(Issue(W2, "2026-03-12", L(Apple, Pcs, 1m)));
        await ReceiveDoc(Apple, 5m, 5m, "2026-03-10", W2);
        // off by default: posting above the stock is the unchanged refusal, approving is refused
        var doc = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 7m)));
        var before = await NegCounts();
        Fails(await Post(doc), ConfigStatus.Conflict, "insufficient_stock");
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why()), ConfigStatus.Conflict, "negative_exception_disabled");
        var on = Ok(await Neg.SaveSettingsAsync(Admin.Id, Company.Id, 0, new NegativeStockSettingsInput(true, false, null)));
        Fails(await Neg.SaveSettingsAsync(Admin.Id, Company.Id, 0, new NegativeStockSettingsInput(false, false, null)), ConfigStatus.PreconditionFailed, "settings_version_conflict");
        Assert.Equal(1, Ok(await Neg.SaveSettingsAsync(Admin.Id, Company.Id, 1, new NegativeStockSettingsInput(true, false, null))).VersionNo);   // unchanged: no new version
        // FIFO and unflagged items keep the default refusal; a transfer and a receipt never use the exception
        var fifo = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 5m)));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, fifo.Id, fifo.Revision, Why()), ConfigStatus.Conflict, "negative_exception_item_not_eligible");
        Fails(await Post(fifo), ConfigStatus.Conflict, "insufficient_stock");
        var transfer = await Approved(Transfer(W1, W2, "2026-03-12", L(Avg, Pcs, 4m)));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, transfer.Id, transfer.Revision, Why()), ConfigStatus.Conflict, "negative_exception_transfer_not_allowed");
        Fails(await Post(transfer), ConfigStatus.Conflict, "insufficient_stock");
        var receipt = await Approved(Receipt(W2, "2026-03-12", L(Avg, Pcs, 4m)));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, receipt.Id, receipt.Revision, Why()), ConfigStatus.Conflict, "negative_exception_issue_only");
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, covered.Id, covered.Revision, Why()), ConfigStatus.Conflict, "negative_exception_not_needed");
        // authorization: the warehouse-scoped permission AND the right to see the document; never the document's creator
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);
        Fails(await NegMgmt.ApproveAsync(viewer.Id, Company.Id, doc.Id, doc.Revision, Why()), ConfigStatus.Forbidden, "forbidden");
        var (elsewhere, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsNegativeException], ScopeKind.Warehouse, W2.Id);
        Fails(await NegMgmt.ApproveAsync(elsewhere.Id, Company.Id, doc.Id, doc.Revision, Why()), ConfigStatus.Forbidden, "forbidden");
        Fails(await NegMgmt.ApproveAsync(elsewhere.Id, Company.Id, Guid.NewGuid(), 1, Why()), ConfigStatus.Forbidden, "forbidden");
        Fails(await NegMgmt.ApproveAsync(Admin.Id, Company.Id, doc.Id, doc.Revision, Why()), ConfigStatus.Conflict, "negative_exception_self_approval");   // the administrator created it
        Assert.NotNull(await RawMessage($"INSERT INTO {NA.Table} ({NA.Id}, {NA.DocumentId}, {NA.DocumentRevision}, {NA.ApproverId}, {NA.ApprovedAtUtc}, {NA.Reason}, {NA.SnapshotHash}, {NA.Snapshot}) VALUES (gen_random_uuid(), '{doc.Id}', {doc.Revision}, '{Admin.Id}', now(), 'x', repeat('a', 64), '{{}}')"));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision + 1, Why()), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, new NegativeApprovalInput(" ", null)), "reason", "required");
        // the poster may not approve their own exception: an approval by the poster alone does not count
        var (posterApprover, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsNegativeException, PermissionCatalog.Codes.StockDocsPost], ScopeKind.Company, null);
        Ok(await NegMgmt.ApproveAsync(posterApprover.Id, Company.Id, doc.Id, doc.Revision, Why()));
        Fails(await Posting.PostAsync(posterApprover.Id, Company.Id, doc.Id, doc.Revision, null), ConfigStatus.Conflict, "negative_exception_self_approval");
        Assert.Equal(before, await NegCounts());
        // a stale snapshot (the stock changed) must be approved again
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why()));
        await ReceiveDoc(Avg, 1m, 5m, "2026-03-11");
        Fails(await Post(doc), ConfigStatus.Conflict, "negative_exception_stale");
        Assert.Equal(0, Ok(await NegMgmt.GetExceptionAsync(Excepter.Id, Company.Id, doc.Id)).ValidApprovals);
        // a company requiring a second distinct approver
        Ok(await Neg.SaveSettingsAsync(Admin.Id, Company.Id, on.VersionNo, new NegativeStockSettingsInput(true, true, "two approvers")));
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why()));
        Fails(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why()), ConfigStatus.Conflict, "already_approved");
        Fails(await Post(doc), ConfigStatus.Conflict, "negative_exception_approval_required");
        var (second, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsNegativeException], ScopeKind.Warehouse, W1.Id);
        Assert.Equal(2, Ok(await NegMgmt.ApproveAsync(second.Id, Company.Id, doc.Id, doc.Revision, Why())).ValidApprovals);
        Ok(await Post(doc));
        Assert.Equal((6m, 30m), ((await ObligationOf(doc)).OriginalQuantity, (await ObligationOf(doc)).ProvisionalTotal));   // 7 - the 1 received, at the live average 5.00
        Fails(await NegMgmt.ApproveAsync(second.Id, Company.Id, doc.Id, doc.Revision, Why()), ConfigStatus.Conflict, "already_posted");
        // switching the item flag or the company off stops new exceptions at once (R18)
        var next = await Approved(Issue(W1, "2026-03-13", L(Avg, Pcs, 2m)));
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, next.Id, next.Revision, Why()));
        Ok(await NegMgmt.ApproveAsync(second.Id, Company.Id, next.Id, next.Revision, Why()));
        Ok(await Neg.SaveSettingsAsync(Admin.Id, Company.Id, 2, new NegativeStockSettingsInput(false, true, "pilot over")));
        Fails(await Post(next), ConfigStatus.Conflict, "insufficient_stock");
        Assert.Equal(3L, await Rows(NS.Table));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Reversals_restore_exact_open_quantities_newest_settlement_first_and_the_issue_only_once_nothing_is_settled()
    {
        await PrepareNegative();
        await ReceiveDoc(Avg, 3m, 15m, "2026-03-10");
        var issue = await Exceptional(Avg, 10m, "2026-03-12");
        var ip = Ok(await Post(issue));                                                                       // 3 covered (15.00), obligation 7 at 35.00
        var r1 = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 4m)));
        Ok(await Post(r1, Amounts(24m)));
        var r2 = await Approved(Receipt(W1, "2026-03-14", L(Avg, Pcs, 3m)));
        Ok(await Post(r2, Amounts(18m)));
        Assert.Equal(2, (await ObligationOf(issue)).Status);
        var before = await NegCounts();
        Fails(await Reverse(r1, "2026-03-20"), ConfigStatus.Conflict, "negative_settlement_newer_exists");
        Fails(await Reverse(issue, "2026-03-20"), ConfigStatus.Conflict, "negative_obligation_settled");
        Assert.Equal(before, await NegCounts());
        // the newest settlement first: the obligation is open again with exactly its 3 units and 15.00, the settlement journal (never posted) is cancelled
        var s2 = Assert.Single(await SettlementsOf(r2));
        Ok(await Reverse(r2, "2026-03-20"));
        Assert.Equal((3m, 15m, 1), ((await ObligationOf(issue)).OpenQuantity, (await ObligationOf(issue)).OpenProvisional, (await ObligationOf(issue)).Status));
        Assert.Equal(JournalStatus.Cancelled, (await Journal.GetAsync(Company.Id, s2.JournalEntryId!.Value))!.Header.Status);
        var negated = (await ObligationOf(issue)).Settlements.Single(s => s.ReversesSettlementId == s2.Id);
        Assert.Equal((-3m, -15m, -18m, -3m), (negated.MatchedQuantity, negated.ProvisionalAmount, negated.ActualAmount, negated.Difference));
        Fails(await Reverse(issue, "2026-03-20"), ConfigStatus.Conflict, "negative_obligation_settled");
        Ok(await Reverse(r1, "2026-03-20"));
        Assert.Equal((7m, 35m), ((await ObligationOf(issue)).OpenQuantity, (await ObligationOf(issue)).OpenProvisional));
        // now the issue: the obligation is reversed (append-only), the covered part comes back exactly, the issue journal (never posted) is cancelled
        Ok(await Reverse(issue, "2026-03-20"));
        var o = await ObligationOf(issue);
        Assert.Equal((3, 0m, 0m), (o.Status, o.OpenQuantity, o.OpenProvisional));
        Assert.Equal((3m, 15m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        Assert.Equal(JournalStatus.Cancelled, (await Journal.GetAsync(Company.Id, ip.JournalEntryId!.Value))!.Header.Status);
        Assert.Equal(1L, await Rows(NR.Table));
        Assert.Empty(await Obligations(openOnly: true));
        // R4: reversing a receipt whose settlement journal was POSTED drafts the exact opposite entry (Cr O 35.00, Cr V 7.00 / Dr G 42.00) and re-opens 7 at 35.00
        await Emptied(Avg2, 3m, 15m);
        var other = await Exceptional(Avg2, 7m, "2026-03-12");
        await PostJournal(Ok(await Post(other)));
        var r3 = await Approved(Receipt(W1, "2026-03-13", L(Avg2, Pcs, 7m)));
        Ok(await Post(r3, Amounts(42m)));
        var s3 = Assert.Single(await SettlementsOf(r3));
        await PostJournalEntry(s3.JournalEntryId!.Value);
        Ok(await Reverse(r3, "2026-04-02"));
        var back = (await ObligationOf(other)).Settlements.Single(s => s.ReversesSettlementId == s3.Id);
        Assert.NotEqual(s3.JournalEntryId, back.JournalEntryId);
        Assert.Equal([(OblAcc.Id, 0m, 35m), (VarAcc.Id, 0m, 7m), (GoodsAcc.Id, 42m, 0m)], await Lines(back.JournalEntryId!.Value));
        Assert.Equal((7m, 35m), ((await ObligationOf(other)).OpenQuantity, (await ObligationOf(other)).OpenProvisional));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_receipt_dated_before_an_open_obligation_and_every_other_inflow_are_refused_while_it_is_open()
    {
        await PrepareNegative();
        await ReceiveDoc(Avg, 3m, 15m, "2026-03-10");
        var earlier = await Approved(Issue(W1, "2026-03-11", L(Avg, Pcs, 3m)));
        Ok(await Post(earlier));                                                                              // a covered issue: emptied the position
        await ReceiveDoc(Avg, 5m, 50m, "2026-03-10", W2);
        Ok(await Post(await Exceptional(Avg, 7m, "2026-03-12")));
        var before = await NegCounts();
        // R13: a receipt dated before the obligation cannot match and may not enter the position
        Fails(await Post(await Approved(Receipt(W1, "2026-03-11", L(Avg, Pcs, 7m))), Amounts(42m)), ConfigStatus.Conflict, "negative_obligation_older");
        // R3 / D4: a transfer in, the reversal of an earlier covered issue, a direct engine receipt
        var transfer = await Approved(Transfer(W2, W1, "2026-03-13", L(Avg, Pcs, 3m)));
        Fails(await Post(transfer), ConfigStatus.Conflict, "negative_obligation_open");
        Fails(await Reverse(earlier, "2026-03-20"), ConfigStatus.Conflict, "negative_obligation_open");
        Assert.Equal("negative_obligation_open", (await Refused(() => Receive(Avg, 1m, 5m, date: new DateOnly(2026, 3, 13)))).Code);
        Assert.Equal(before, await NegCounts());
        Assert.Equal((0m, 0m, 5m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg, W2)).Quantity));
        // once the obligation is settled the same transfer is accepted at its actual cost
        Ok(await Post(await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 7m))), Amounts(42m)));
        Ok(await Post(transfer));
        Assert.Equal((3m, 30m, 2m, 20m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg, W2)).Quantity, (await Pos(Avg, W2)).Value));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task An_open_obligation_blocks_final_close_but_not_soft_close_the_obligation_account_reconciles_and_a_receipt_reversal_cannot_reopen_a_final_closed_period()
    {
        await PrepareNegative();
        await PostJournal(await ReceiveDoc(Avg, 3m, 15m, "2026-01-10"));
        await PostJournal(await IssueDoc(Avg, 3m, "2026-01-11"));
        var issue = await Approved(Issue(W1, "2026-01-12", L(Avg, Pcs, 7m)));
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, issue.Id, issue.Revision, Why()));
        var ip = Ok(await Post(issue));
        var draft = Ok(await NegMgmt.CloseReportAsync(Admin.Id, Company.Id, new DateOnly(2026, 1, 31)));
        Assert.Equal((35m, 0m, false), (draft.ExpectedObligationBalance, draft.LedgerObligationBalance, draft.Reconciled));      // the issue entry is still a draft: O does not reconcile
        await PostJournal(ip);
        var periods = (await Calendar.ListAsync(Company.Id)).Single().Periods;
        FiscalPeriodView Month(int m) => periods.Single(x => x.StartDate == new DateOnly(2026, m, 1));
        var jan = Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, Month(1).Id, Month(1).Revision));      // D7: only a warning at soft close
        var report = Ok(await NegMgmt.CloseReportAsync(Admin.Id, Company.Id, new DateOnly(2026, 1, 31)));
        var open = Assert.Single(report.Open);
        Assert.Equal((issue.Id, 7m, 35m, 35m, 35m, true), (open.DocumentId, open.OpenQuantity, open.OpenProvisional, report.ExpectedObligationBalance, report.LedgerObligationBalance, report.Reconciled));
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, Month(2).Id, Month(2).Revision));
        Fails(await CloseCalendar.FinalCloseAsync(Admin.Id, Company.Id, jan.Id, jan.Revision), ConfigStatus.Conflict, "period_close_blocked");   // N3
        // a receipt in an open period settles the January obligation; the variance lands in March, January is never edited
        var receipt = await Approved(Receipt(W1, "2026-03-02", L(Avg, Pcs, 7m)));
        Ok(await Post(receipt, Amounts(42m)));
        var s = Assert.Single(await SettlementsOf(receipt));
        Assert.Equal("2026-03-02", s.EffectiveDate);
        var janEnd = Ok(await NegMgmt.CloseReportAsync(Admin.Id, Company.Id, new DateOnly(2026, 1, 31)));
        Assert.Equal((0, 35m, 35m, true), (janEnd.Open.Count, janEnd.ExpectedObligationBalance, janEnd.LedgerObligationBalance, janEnd.Reconciled));   // point in time: still 35.00 at the end of January
        var finalJan = Ok(await CloseCalendar.FinalCloseAsync(Admin.Id, Company.Id, jan.Id, jan.Revision));
        Assert.Equal(FiscalPeriodState.FinalClosed, finalJan.State);
        // R9: reversing the receipt would re-open an obligation of a Final Closed period
        Fails(await Reverse(receipt, "2026-03-05"), ConfigStatus.Conflict, "negative_obligation_period_closed");
        // the settlement journal pending counts as a posting problem for March only
        var marchEnd = Ok(await NegMgmt.CloseReportAsync(Admin.Id, Company.Id, new DateOnly(2026, 3, 31)));
        Assert.Equal((0m, 35m, false), (marchEnd.ExpectedObligationBalance, marchEnd.LedgerObligationBalance, marchEnd.Reconciled));
        await PostJournalEntry(s.JournalEntryId!.Value);
        marchEnd = Ok(await NegMgmt.CloseReportAsync(Admin.Id, Company.Id, new DateOnly(2026, 3, 31)));
        Assert.Equal((0m, 0m, 7m, 7m, true), (marchEnd.ExpectedObligationBalance, marchEnd.LedgerObligationBalance, marchEnd.SettlementDifferences, marchEnd.LedgerVariance, marchEnd.Reconciled));
    }

    [PostgresFact]
    public async Task Concurrent_receipts_settle_exactly_once_and_concurrent_postings_create_one_obligation()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 15m);
        var issue = await Exceptional(Avg, 7m, "2026-03-12");
        var posts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Post(issue)));
        Assert.All(posts, r => Assert.True(r.IsOk));
        Assert.Equal(1, posts.Count(r => !r.Value!.Replayed));
        Assert.Equal(1L, await Rows(NO.Table));
        var r1 = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 5m)));
        var r2 = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 5m)));
        var both = await Task.WhenAll(Post(r1, Amounts(30m)), Post(r2, Amounts(30m)));
        Assert.All(both, r => Assert.True(r.IsOk));
        var o = await ObligationOf(issue);
        Assert.Equal((7m, 35m, 0m), (o.Settlements.Sum(s => s.MatchedQuantity), o.Settlements.Sum(s => s.ProvisionalAmount), o.OpenQuantity));      // never more than the obligation
        Assert.Equal(3m, (await Pos(Avg)).Quantity);                                                          // 10 received, 7 settled, 3 entered
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_the_obligation_settlement_and_reversal_invariants_and_every_record_is_append_only()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 15m);
        var issue = await Exceptional(Avg, 7m, "2026-03-12");
        var ip = Ok(await Post(issue));
        var receipt = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 4m)));
        var rp = Ok(await Post(receipt, Amounts(24m)));
        var o = await ObligationOf(issue);
        var s = Assert.Single(o.Settlements);
        var cols = $"{NO.Id}, {NO.ItemId}, {NO.WarehouseId}, {NO.PositionId}, {NO.PostingId}, {NO.LineNo}, {NO.DocumentId}, {NO.IssueOperationId}, {NO.JournalEntryId}, {NO.AccountingVersionId}, {NO.ObligationAccountId}, {NO.VarianceAccountId}, {NO.CounterAccountId}, {NO.EffectiveDate}, {NO.OriginalQuantity}, {NO.ProvisionalTotal}, {NO.SourceCode}, {NO.SourceEntryId}, {NO.SourceQuantity}, {NO.SourceValue}, {NO.Justification}, {NO.ApprovalId}, {NO.CreatedBy}, {NO.CreatedAtUtc}";
        string Copy(string overrides) => $"INSERT INTO {NO.Table} ({cols}) SELECT {overrides} FROM {NO.Table} WHERE {NO.Id} = '{o.Id}'";
        string Same(string except) => string.Join(", ", cols.Split(", ").Select(c => c == NO.Id ? "gen_random_uuid()" : except.Split(';').Select(x => x.Split('=')).FirstOrDefault(x => x[0] == c) is { } kv ? kv[1] : c));
        // obligations: an ineligible item, no posting line, no approval, a manual source without justification, a zero total
        Assert.Contains("eligible", await RawMessage(Copy(Same($"{NO.ItemId}='{Avg2.Id}'::uuid"))) ?? "");
        Assert.Contains("posting line", await RawMessage(Copy(Same($"{NO.LineNo}=99"))) ?? "");
        Assert.NotNull(await RawMessage(Copy(Same($"{NO.LineNo}=99;{NO.SourceCode}=3::smallint;{NO.SourceQuantity}=NULL;{NO.SourceValue}=NULL"))));
        Assert.NotNull(await RawMessage(Copy(Same($"{NO.ProvisionalTotal}=0"))));
        // settlements: more than the obligation, more than the receipt line, a reversal that does not negate, a reversal of a non reversed posting, dated before the obligation
        var sc = $"{NX.Id}, {NX.ObligationId}, {NX.ReceiptPostingId}, {NX.ReceiptLineNo}, {NX.ReceiptOperationId}, {NX.LineQuantity}, {NX.LineAmount}, {NX.MatchedQuantity}, {NX.ProvisionalAmount}, {NX.ActualAmount}, {NX.Difference}, {NX.EffectiveDate}, {NX.ReversesSettlementId}, {NX.ReversalId}, {NX.CreatedBy}, {NX.CreatedAtUtc}";
        string Settle(string q, string p, string a, string reverses = "NULL", string reversal = "NULL", string date = "'2026-03-13'") =>
            $"INSERT INTO {NX.Table} ({sc}) VALUES (gen_random_uuid(), '{o.Id}', '{rp.Id}', 1, '{rp.CostOperationId}', 4, 24, {q}, {p}, {a}, {a} - {p}, {date}, {reverses}, {reversal}, gen_random_uuid(), now())";
        Assert.NotNull(await RawMessage(Settle("1", "5", "6")));                                             // the same receipt line twice (unique) and more than the line
        Assert.Contains("negates exactly", await RawMessage(Settle("-4", "-20", "-23", $"'{s.Id}'", "gen_random_uuid()")) ?? "");
        Assert.NotNull(await RawMessage(Settle("1", "5", "6", date: "'2026-03-11'")));
        Assert.NotNull(await RawMessage($"UPDATE {NX.Table} SET {NX.Difference} = 0, {NX.ActualAmount} = {NX.ProvisionalAmount}"));
        // an obligation with a net settlement cannot be reversed; a reversal must belong to its issue posting
        Assert.NotNull(await RawMessage($"INSERT INTO {NR.Table} ({NR.Id}, {NR.ObligationId}, {NR.StockReversalId}, {NR.ReversalOperationId}, {NR.EffectiveDate}, {NR.CreatedBy}, {NR.CreatedAtUtc}) VALUES (gen_random_uuid(), '{o.Id}', gen_random_uuid(), '{ip.CostOperationId}', '2026-03-20', gen_random_uuid(), now())"));
        // R3 at commit: an open obligation keeps the position empty, even for a hand-written position update
        Assert.Contains("empty cost position", await RawMessage($"UPDATE {PO.Table} SET {PO.Quantity} = 1, {PO.Value} = 1, {PO.LastAverageQuantity} = 1, {PO.LastAverageValue} = 1, {PO.LastSeq} = {PO.LastSeq} + 1 WHERE {PO.ItemId} = '{Avg.Id}' AND {PO.WarehouseId} = '{W1.Id}'") ?? "");
        // the last average is written by entries only
        Assert.NotNull(await RawMessage($"UPDATE {PO.Table} SET {PO.LastAverageQuantity} = 9, {PO.LastSeq} = {PO.LastSeq} + 1 WHERE {PO.ItemId} = '{Avg.Id}' AND {PO.WarehouseId} = '{W1.Id}'"));
        // DB-1: an operation without entries only for the exception (judged at commit)
        Assert.Contains("without entries", await RawMessage($"INSERT INTO {CO.Table} ({CO.Id}, {CO.Key}, {CO.Kind}, {CO.RequestHash}, {CO.EffectiveDate}, {CO.CreatedBy}, {CO.CreatedAtUtc}) VALUES (gen_random_uuid(), 'hack-empty', 2, repeat('a', 64), '2026-03-20', gen_random_uuid(), now())") ?? "");
        // the flag of an item with an open obligation cannot be removed
        Assert.Equal("negative_obligation_open", await RawMessage($"INSERT INTO {NF.Table} ({NF.ItemId}, {NF.Eligible}, {NF.ChangedBy}, {NF.ChangedAtUtc}) VALUES ('{Avg.Id}', false, gen_random_uuid(), now())"));
        // append-only, for everyone
        foreach (var t in new[] { NS.Table, NF.Table, NA.Table, NO.Table, NX.Table, NR.Table })
        {
            if (t != NR.Table) Assert.NotNull(await RawMessage($"DELETE FROM {t}"));                       // (no obligation reversal row exists to delete)
            Assert.NotNull(await RawMessage($"TRUNCATE {t} CASCADE"));
        }
        Assert.NotNull(await RawMessage($"UPDATE {NO.Table} SET {NO.ProvisionalTotal} = 1"));
        Assert.NotNull(await RawMessage($"UPDATE {NS.Table} SET {NS.Enabled} = false"));
        Assert.NotNull(await RawMessage($"UPDATE {NA.Table} SET {NA.Reason} = 'x'"));
        Assert.NotNull(await RawMessage($"UPDATE {NF.Table} SET {NF.Eligible} = false"));
        Assert.Equal((1L, 1L, 0L), (await Rows(NO.Table), await Rows(NX.Table), await Rows(NR.Table)));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Guarded_changes_are_refused_while_an_obligation_is_open_and_allowed_once_it_is_settled()
    {
        await PrepareNegative();
        Ok(await Neg.SetItemFlagAsync(Admin.Id, Company.Id, Apple.Id, new NegativeItemFlagInput(true, "resolves through the company default")));
        await Emptied(Avg, 3m, 15m);
        await Emptied(Apple, 2m, 10m);
        Ok(await Post(await Exceptional(Avg, 7m, "2026-03-12")));
        Ok(await Post(await Exceptional(Apple, 1m, "2026-03-12")));
        // the eligibility flag, the item's tracking, group and activity, the valuation policy of its groups, the company default when it would change the resolution, the warehouse
        Fails(await Neg.SetItemFlagAsync(Admin.Id, Company.Id, Avg.Id, new NegativeItemFlagInput(false, null)), ConfigStatus.Conflict, "negative_obligation_open");
        var avg = (await Inv.GetItemAsync(Company.Id, Avg.Id))!;
        Fails(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, Avg.Id, avg.Revision, false), ConfigStatus.Conflict, "negative_obligation_open");
        Assert.Equal("negative_obligation_open", await RawMessage($"UPDATE {I.Table} SET {I.IsLot} = true, {I.Revision} = {I.Revision} + 1 WHERE {I.Id} = '{Avg.Id}'"));
        Assert.Equal("negative_obligation_open", await RawMessage($"UPDATE {I.Table} SET {I.GroupId} = '{Apple.GroupId}', {I.Revision} = {I.Revision} + 1 WHERE {I.Id} = '{Avg.Id}'"));
        Assert.Equal("negative_obligation_open", await RawMessage($"UPDATE {G.Table} SET {G.Valuation} = 1, {G.Revision} = {G.Revision} + 1 WHERE {G.Id} = '{Avg.GroupId}'"));
        var settings = (await Inv.GetSettingsAsync(Company.Id))!;
        Fails(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, settings.Revision, new InventorySettingsInput(1)), ConfigStatus.Conflict, "negative_obligation_open");   // Apple resolves through the default
        // the warehouse: the service already refuses while it holds documents; PostgreSQL refuses the deactivation itself while an obligation is open
        Assert.Equal("negative_obligation_open", await RawMessage($"UPDATE {W.Table} SET {W.IsActive} = false, {W.Revision} = {W.Revision} + 1, {W.ActivationEpoch} = {W.ActivationEpoch} + 1 WHERE {W.Id} = '{W1.Id}'"));
        Assert.Equal((true, true), ((await Inv.GetItemAsync(Company.Id, Avg.Id))!.IsActive, (await WhDomain.GetAsync(Company.Id, W1.Id))!.IsActive));
        // settled: the same changes are possible again
        Ok(await Post(await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 7m))), Amounts(35m)));
        Ok(await Post(await Approved(Receipt(W1, "2026-03-13", L(Apple, Pcs, 1m))), Amounts(5m)));
        Assert.False(Ok(await Neg.SetItemFlagAsync(Admin.Id, Company.Id, Avg.Id, new NegativeItemFlagInput(false, null))).Eligible);
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, settings.Revision, new InventorySettingsInput(1)));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task The_obligation_and_variance_accounts_are_validated_kept_per_obligation_across_versions_and_take_no_manual_entry()
    {
        await PrepareNegative(variance: false);
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, CogsAcc.Id)), "obligationAccountId", "obligation_account_must_be_liability");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, GoodsAcc.Id)), "obligationAccountId", "obligation_equals_counter_account");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, OblAcc.Id, OblAcc.Id)), "varianceAccountId", "variance_equals_obligation_account");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, OblAcc.Id, GoodsAcc.Id)), "varianceAccountId", "variance_equals_receipt_counter_account");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, OblAcc.Id, StockAcc.Id)), "varianceAccountId", "counter_equals_inventory_account");
        Assert.Equal(3, (await Acc.GetAsync(Company.Id)).Version!.VersionNo);
        // without V the variance goes to the issue counter account K (the default)
        await Emptied(Avg, 3m, 15m);
        var first = await Exceptional(Avg, 7m, "2026-03-12");
        Ok(await Post(first));
        Assert.Equal((OblAcc.Id, CogsAcc.Id), ((await ObligationOf(first)).ObligationAccountId, (await ObligationOf(first)).VarianceAccountId));
        // a new version with another O: the open obligation keeps the O of its issue (R8), a new one takes the new O
        var obl2 = Ok(await Chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("2302", null, "التزامات ٢", "Obligations 2", "liability", null, false, null, null)));
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], "new O", AdjAcc.Id, obl2.Id, VarAcc.Id)));
        await Emptied(Avg2, 2m, 20m);
        var second = await Exceptional(Avg2, 1m, "2026-03-12");
        Ok(await Post(second));
        Assert.Equal((obl2.Id, VarAcc.Id), ((await ObligationOf(second)).ObligationAccountId, (await ObligationOf(second)).VarianceAccountId));
        var r = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 7m), L(Avg2, Pcs, 1m)));
        Ok(await Post(r, Amounts(42m, 12m)));
        var settlements = await SettlementsOf(r);
        Assert.Equal([(OblAcc.Id, 35m, 0m), (CogsAcc.Id, 7m, 0m), (GoodsAcc.Id, 0m, 42m)], await Lines(settlements.Single(x => x.ReceiptLineNo == 1).JournalEntryId!.Value));
        Assert.Equal([(obl2.Id, 10m, 0m), (VarAcc.Id, 2m, 0m), (GoodsAcc.Id, 0m, 12m)], await Lines(settlements.Single(x => x.ReceiptLineNo == 2).JournalEntryId!.Value));
        // R9: the obligation accounts take automatic postings only (a manual entry is refused by PostgreSQL, reported as a stable code)
        var manual = await Journal.CreateDraftAsync(Admin.Id, Company.Id, new JournalInput(new DateOnly(2026, 3, 20), "manual", W1.BranchId,
            [new JournalLineInput(OblAcc.Id, null, null, 10m, null, W1.BranchId, null, null, null), new JournalLineInput(CogsAcc.Id, null, null, null, 10m, W1.BranchId, null, null, null)], null), false);
        Fails(manual, ConfigStatus.Conflict, "account_automatic_only");
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Adjustment_shortages_never_use_the_exception_and_surpluses_and_shortage_reversals_are_refused_naming_the_item_while_an_obligation_is_open()
    {
        await PrepareNegative();
        // (1) a shortage of a flagged item with the exception enabled is costed by the engine at actual cost: no approval, no obligation
        await ReceiveDoc(Avg2, 5m, 50m, "2026-03-10");
        var shortage = Ok(await Generate(await ApprovedTake(W1, C(Avg2, Pcs, 0m))));
        Assert.Equal(-1, shortage.Lines.Single().Direction);
        Ok(await Adj.PostAsync(Admin.Id, Company.Id, shortage.Id, null));
        Assert.Equal((0m, 0m, 0L), ((await Pos(Avg2)).Quantity, (await Pos(Avg2)).Value, await Rows(NO.Table)));
        // an obligation now opens on both items of W1
        Ok(await Post(await Exceptional(Avg2, 3m, "2026-03-12")));
        await Emptied(Avg, 3m, 15m);
        Ok(await Post(await Exceptional(Avg, 2m, "2026-03-12")));
        var before = await NegCounts();
        // (2) taking the shortage back is an inflow: refused, naming the item, nothing written
        var back = await Adj.ReverseAsync(Admin.Id, Company.Id, shortage.Id, new StockReverseInput("2026-03-20", "recount"));
        Fails(back, ConfigStatus.Conflict, "negative_obligation_open");
        Assert.Equal([("lines[0].itemId", "negative_obligation_open")], back.Fields!.Select(f => (f.Field, f.Code)));
        Assert.Null((await Adj.GetAsync(Company.Id, shortage.Id))!.Posting!.Reversal);
        // (2) a surplus is refused, naming its item (line 2 of the adjustment), all or nothing: the FIFO shortage of the same adjustment is not booked either
        await ReceiveDoc(Fifo, 4m, 40m, "2026-03-10");
        var take = await ApprovedTake(W1, C(Fifo, Pcs, 3m), C(Avg, Pcs, 2m));
        var mixed = Ok(await Generate(take));
        before = await NegCounts();
        var refused = await Adj.PostAsync(Admin.Id, Company.Id, mixed.Id, Values((2, 10m)));
        Fails(refused, ConfigStatus.Conflict, "negative_obligation_open");
        Assert.Equal([("lines[1].itemId", "negative_obligation_open")], refused.Fields!.Select(f => (f.Field, f.Code)));
        Assert.Equal(before, await NegCounts());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {SAP.Table} WHERE {SAP.AdjustmentId} = '{mixed.Id}'"));
        Assert.Equal((4m, 0m), ((await Pos(Fifo)).Quantity, (await Pos(Avg)).Quantity));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task An_adjustment_surplus_is_not_a_receipt_it_never_settles_an_obligation_and_never_prices_one_as_source_2()
    {
        await PrepareNegative();
        // the only receipt with value is an adjustment surplus; the last average then loses its value (free goods)
        var surplus = Ok(await Generate(await ApprovedTake(W1, C(Avg, Pcs, 2m))));
        Ok(await Adj.PostAsync(Admin.Id, Company.Id, surplus.Id, Values((1, 20m))));
        await IssueDoc(Avg, 2m, "2026-03-11");
        await ReceiveDoc(Avg, 1m, 0m, "2026-03-11");
        await IssueDoc(Avg, 1m, "2026-03-11");
        var doc = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 3m)));
        var preview = Ok(await NegMgmt.GetExceptionAsync(Excepter.Id, Company.Id, doc.Id));
        Assert.Equal(((int?)null, "provisional_cost_unavailable"), (preview.Plan.Lines[0].Source, preview.Plan.Lines[0].Problem));   // D5 narrowed by R15 (3): stock-document receipts only
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why(new NegativeManualCostInput(1, 30m, "last purchase invoice"))));
        Ok(await Post(doc));
        // a later surplus is refused while the obligation is open, so it can never match it; a stock-document receipt settles it
        var later = Ok(await Generate(await ApprovedTake(W2, C(Avg, Pcs, 1m))));                           // another warehouse: no obligation there, accepted
        Ok(await Adj.PostAsync(Admin.Id, Company.Id, later.Id, Values((1, 10m))));
        Assert.Equal(0L, await Rows(NX.Table));
        Fails(await Adj.PostAsync(Admin.Id, Company.Id, Ok(await Generate(await ApprovedTake(W1, C(Avg, Pcs, 3m, "found")))).Id, Values((1, 30m))), ConfigStatus.Conflict, "negative_obligation_open");
        Assert.Equal(0L, await Rows(NX.Table));
        var receipt = await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 3m)));
        Ok(await Post(receipt, Amounts(33m)));
        Assert.Equal((3m, 30m, 33m), ((await SettlementsOf(receipt)).Single().MatchedQuantity, (await SettlementsOf(receipt)).Single().ProvisionalAmount, (await SettlementsOf(receipt)).Single().ActualAmount));
        await AssertConsistent();
    }
}

/// <summary>Fixes of the independent review of PR #45 (M1 to M5).</summary>
public class NegativeStockReviewTests : NegativeStockTestBase
{
    [PostgresFact]
    public async Task M1_an_obligation_needs_the_live_moving_average_policy_and_a_concurrent_policy_change_never_leaves_an_open_obligation_under_another_method()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 15m);
        var issue = await Exceptional(Avg, 7m, "2026-03-12");
        Ok(await Post(issue));
        Ok(await Post(await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 7m))), Amounts(42m)));          // settled: the policy may change again
        var o = await ObligationOf(issue);
        Assert.Null(await RawMessage($"UPDATE {G.Table} SET {G.Valuation} = 1, {G.Revision} = {G.Revision} + 1 WHERE {G.Id} = '{Avg.GroupId}'"));
        // the position still says moving average, the flag is still set, but the item no longer RESOLVES to moving average: refused (before the posting-line check)
        var cols = $"{NO.Id}, {NO.ItemId}, {NO.WarehouseId}, {NO.PositionId}, {NO.PostingId}, {NO.LineNo}, {NO.DocumentId}, {NO.IssueOperationId}, {NO.JournalEntryId}, {NO.AccountingVersionId}, {NO.ObligationAccountId}, {NO.VarianceAccountId}, {NO.CounterAccountId}, {NO.EffectiveDate}, {NO.OriginalQuantity}, {NO.ProvisionalTotal}, {NO.SourceCode}, {NO.SourceEntryId}, {NO.SourceQuantity}, {NO.SourceValue}, {NO.Justification}, {NO.ApprovalId}, {NO.CreatedBy}, {NO.CreatedAtUtc}";
        var select = cols.Replace($"{NO.Id},", "gen_random_uuid(),").Replace($"{NO.LineNo},", "99,");
        Assert.Contains("eligible", await RawMessage($"INSERT INTO {NO.Table} ({cols}) SELECT {select} FROM {NO.Table} WHERE {NO.Id} = '{o.Id}'") ?? "");
        Assert.Null(await RawMessage($"UPDATE {G.Table} SET {G.Valuation} = 2, {G.Revision} = {G.Revision} + 1 WHERE {G.Id} = '{Avg.GroupId}'"));
        // a policy change (inventory master lock) racing an exception posting (posting lock): whichever wins, an open obligation never coexists with another resolved method
        var next = await Exceptional(Avg, 2m, "2026-03-15");
        var group = (await Inv.ListGroupsAsync(Company.Id)).Single(g => g.Id == Avg.GroupId);
        var both = await Task.WhenAll(
            Task.Run(async () => (object)await Post(next)),
            Task.Run(async () => (object)await Inv.UpdateGroupAsync(Admin.Id, Company.Id, group.Id, group.Revision, new ItemGroupInput(group.Code, group.NameAr, group.NameEn, group.ParentId, 1))));
        var posted = ((ConfigResult<StockPostingView>)both[0]).IsOk;
        var changed = ((ConfigResult<ItemGroupView>)both[1]).IsOk;
        Assert.True(posted ^ changed, $"posted={posted} changed={changed}");
        var valuation = await Sql<short>($"SELECT {G.Valuation} FROM {G.Table} WHERE {G.Id} = '{Avg.GroupId}'");
        Assert.Equal(posted ? (short)2 : (short)1, valuation);
        Assert.Equal(posted ? 1 : 0, (await Obligations(openOnly: true)).Count);
    }

    [PostgresFact]
    public async Task M2_M4_a_preview_and_an_approval_write_no_cost_position_only_the_posting_creates_it()
    {
        await PrepareNegative();
        var doc = await Approved(Issue(W2, "2026-03-12", L(Avg2, Pcs, 4m)));
        string Positions() => $"SELECT count(*) FROM {PO.Table} WHERE {PO.ItemId} = '{Avg2.Id}' AND {PO.WarehouseId} = '{W2.Id}'";
        var preview = Ok(await NegMgmt.GetExceptionAsync(Excepter.Id, Company.Id, doc.Id));
        Assert.Equal((0m, 4m, "provisional_cost_unavailable"), (preview.Plan.Lines[0].OnHand, preview.Plan.Lines[0].Uncovered, preview.Plan.Lines[0].Problem));
        Assert.Equal(0L, await Sql<long>(Positions()));
        Ok(await NegMgmt.ApproveAsync(Excepter.Id, Company.Id, doc.Id, doc.Revision, Why(new NegativeManualCostInput(1, 40m, "supplier quote"))));
        Assert.Equal(0L, await Sql<long>(Positions()));                                                      // approving without posting leaves no position
        Ok(await Post(doc));                                                                                  // the same snapshot: the posting creates the position (method fixed) and the obligation
        Assert.Equal(1L, await Sql<long>(Positions()));
        Assert.Equal((4m, 40m, 3), ((await ObligationOf(doc)).OriginalQuantity, (await ObligationOf(doc)).ProvisionalTotal, (await ObligationOf(doc)).Source));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task M3_M5_the_receipt_counter_account_may_not_become_the_obligation_or_variance_account_of_an_open_obligation()
    {
        await PrepareNegative();
        await Emptied(Avg, 3m, 15m);
        Ok(await Post(await Exceptional(Avg, 7m, "2026-03-12")));
        // at configuration save (M5)
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 3, new InventoryAccountingInput(StockAcc.Id, VarAcc.Id, CogsAcc.Id, [], null, AdjAcc.Id, OblAcc.Id, null)), "receiptCounterAccountId", "receipt_counter_equals_open_obligation_account");
        // at settlement (M3): a version that names V as the receipt counter (written directly, as a version saved before the obligation could have been) is refused when the receipt would settle
        var v = $"{IAV.Id}, {IAV.VersionNo}, {IAV.CreatedAtUtc}, {IAV.CreatedBy}, {IAV.Note}, {IAV.DefaultAccountId}, {IAV.ReceiptCounterAccountId}, {IAV.IssueCounterAccountId}, {IAV.GroupCount}, {IAV.ContentHash}, {IAV.AdjustmentCounterAccountId}, {IAV.ObligationAccountId}, {IAV.VarianceAccountId}";
        Assert.Null(await RawMessage($"INSERT INTO {IAV.Table} ({v}) SELECT gen_random_uuid(), {IAV.VersionNo} + 1, now(), {IAV.CreatedBy}, 'hack', {IAV.DefaultAccountId}, '{VarAcc.Id}', {IAV.IssueCounterAccountId}, {IAV.GroupCount}, repeat('b', 64), {IAV.AdjustmentCounterAccountId}, {IAV.ObligationAccountId}, {IAV.VarianceAccountId} FROM {IAV.Table} ORDER BY {IAV.VersionNo} DESC LIMIT 1"));
        var before = await NegCounts();
        Fails(await Post(await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 7m))), Amounts(42m)), ConfigStatus.Conflict, "negative_settlement_account_conflict");
        Assert.Equal(before, await NegCounts());
        Assert.Single(await Obligations(openOnly: true));
    }
}

/// <summary>End-to-end HTTP tests of the negative-stock endpoints (Web and Windows Desktop use exactly these).</summary>
public class NegativeStockApiTests : NegativeStockTestBase
{
    [PostgresFact]
    public async Task Enablement_flags_exception_approval_obligations_and_the_close_report_over_http_with_etags_and_authorization()
    {
        await StartApi();
        await PrepareNegative(enable: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/v1/company/negative-stock/settings")).Status);
        var admin = await SignIn(Admin);
        var settings = await Send(HttpMethod.Get, "/api/v1/company/negative-stock/settings", cookie: admin);
        Assert.Equal((HttpStatusCode.OK, false, 0, true), (settings.Status, settings.Json.GetProperty("settings").GetProperty("enabled").GetBoolean(), settings.Json.GetProperty("settings").GetProperty("versionNo").GetInt32(), settings.Json.GetProperty("canConfigure").GetBoolean()));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Put, "/api/v1/company/negative-stock/settings", new { enabled = true }, admin)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, "/api/v1/company/negative-stock/settings", new { enabled = true }, admin, ifMatch: "\"3\"")).Status);
        var saved = await Send(HttpMethod.Put, "/api/v1/company/negative-stock/settings", new { enabled = true, requireSecondApprover = false, note = "pilot" }, admin, ifMatch: "\"0\"");
        Assert.Equal((HttpStatusCode.OK, true), (saved.Status, saved.Json.GetProperty("enabled").GetBoolean()));
        Assert.Contains("no-store", saved.Headers.CacheControl!.ToString());
        var items = await Send(HttpMethod.Get, "/api/v1/company/negative-stock/items", cookie: admin);
        Assert.Contains(items.Json.GetProperty("items").EnumerateArray(), i => i.GetProperty("itemId").GetGuid() == Avg.Id && i.GetProperty("eligible").GetBoolean());
        var fifo = await Send(HttpMethod.Put, $"/api/v1/company/negative-stock/items/{Fifo.Id}", new { eligible = true }, admin);
        Assert.Equal((HttpStatusCode.Conflict, "negative_exception_item_not_eligible"), (fifo.Status, fifo.Error));
        await Emptied(Avg, 3m, 15m);
        var doc = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 7m)));
        var url = $"/api/v1/company/stock-documents/{doc.Id}/negative-exception";
        var excepter = await SignIn(Excepter);
        var plan = await Send(HttpMethod.Get, url, cookie: excepter);
        Assert.Equal((HttpStatusCode.OK, true, true, 35m), (plan.Status, plan.Json.GetProperty("canApprove").GetBoolean(), plan.Json.GetProperty("exception").GetProperty("plan").GetProperty("needed").GetBoolean(),
            plan.Json.GetProperty("exception").GetProperty("plan").GetProperty("lines")[0].GetProperty("provisionalTotal").GetDecimal()));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, $"{url}/approvals", new { reason = "urgent" }, excepter)).Status);
        var approved = await Send(HttpMethod.Post, $"{url}/approvals", new { reason = "urgent" }, excepter, ifMatch: $"\"{doc.Revision}\"");
        Assert.Equal((HttpStatusCode.OK, 1), (approved.Status, approved.Json.GetProperty("validApprovals").GetInt32()));
        var (outsider, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Warehouse, W2.Id);
        var other = await SignIn(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, url, cookie: other)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/company/negative-stock/settings", cookie: other)).Status);
        var posted = await Send(HttpMethod.Post, $"/api/v1/company/stock-documents/{doc.Id}/posting", null, admin, ifMatch: $"\"{doc.Revision}\"");
        Assert.Equal((HttpStatusCode.OK, 35m), (posted.Status, posted.Json.GetProperty("totalValue").GetDecimal()));
        var list = await Send(HttpMethod.Get, "/api/v1/company/negative-stock/obligations?status=open", cookie: admin);
        var o = list.Json.GetProperty("obligations")[0];
        Assert.Equal((7m, 35m, 1, "AVG1"), (o.GetProperty("openQuantity").GetDecimal(), o.GetProperty("openProvisional").GetDecimal(), o.GetProperty("status").GetInt32(), o.GetProperty("itemCode").GetString()));
        Assert.Equal(0, (await Send(HttpMethod.Get, "/api/v1/company/negative-stock/obligations", cookie: other)).Json.GetProperty("obligations").GetArrayLength());   // W2 only: nothing to see
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/api/v1/company/negative-stock/close-report?periodEnd=31-03-2026", cookie: admin)).Status);
        var report = await Send(HttpMethod.Get, "/api/v1/company/negative-stock/close-report?periodEnd=2026-03-31", cookie: admin);
        Assert.Equal((HttpStatusCode.OK, 1, true, 35m), (report.Status, report.Json.GetProperty("open").GetArrayLength(), report.Json.GetProperty("blocksFinalClose").GetBoolean(), report.Json.GetProperty("expectedObligationBalance").GetDecimal()));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/company/negative-stock/close-report?periodEnd=2026-03-31", cookie: other)).Status);
    }
}
