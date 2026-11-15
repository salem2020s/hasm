using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using LY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.6 inventory costing engine against real PostgreSQL (company migration 0014): FIFO layers and moving weighted average per item and warehouse with exact, explicitly stated figures; negative-stock refusal;
/// precision; transfers that carry the actual cost; idempotency; concurrency; rollback; reversals; company isolation; and the reconciliation invariants. The engine is exercised only through its own transactional API: no
/// document, screen or job posts stock in this phase. Disposable cluster only.
/// </summary>
public abstract class CostingTestBase : StockTestBase
{
    protected InventoryCostingService Cost = null!;
    protected ItemView Fifo = null!, Fifo2 = null!, Avg = null!, Avg2 = null!, KgAvg = null!;
    protected static readonly DateOnly D1 = new(2026, 3, 10), D2 = new(2026, 3, 11), D3 = new(2026, 3, 12);
    private int keys;

    protected async Task PrepareCosting(string currency = "USD")
    {
        await Prepare();
        await Sql($"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('{currency}', true, true, 1, now())");
        Cost = new InventoryCostingService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Inv, Clock);
        var fifoGroup = Ok(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("FIFOG", "x", "Fifo", null, 1)));
        var avgGroup = Ok(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("AVGG", "x", "Avg", null, 2)));
        ItemView Make(string code, ItemGroupView g, UnitView u) => Ok(Inv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput(code, null, code, 1, g.Id, u.Id, null, null, false, false, false, null, [])).GetAwaiter().GetResult());
        Fifo = Make("FIFO1", fifoGroup, Pcs); Fifo2 = Make("FIFO2", fifoGroup, Pcs); Avg = Make("AVG1", avgGroup, Pcs); Avg2 = Make("AVG2", avgGroup, Pcs); KgAvg = Make("KGAVG", avgGroup, Kg);
    }

    protected string Key(string p = "k") => $"{p}-{Interlocked.Increment(ref keys)}";
    protected Task<CostingResult> Receive(ItemView item, decimal qty, decimal amount, WarehouseView? wh = null, DateOnly? date = null, string? key = null) =>
        Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(key ?? Key("r"), date ?? D1, null, [new CostingReceiptLine(item.Id, (wh ?? W1).Id, qty, amount)]));
    protected Task<CostingResult> IssueOf(ItemView item, decimal qty, WarehouseView? wh = null, DateOnly? date = null, string? key = null) =>
        Cost.IssueAsync(Company.Id, Admin.Id, new CostingIssueRequest(key ?? Key("i"), date ?? D1, null, [new CostingIssueLine(item.Id, (wh ?? W1).Id, qty)]));
    protected Task<CostingResult> Move(ItemView item, decimal qty, WarehouseView? from = null, WarehouseView? to = null, DateOnly? date = null, string? key = null) =>
        Cost.TransferAsync(Company.Id, Admin.Id, new CostingTransferRequest(key ?? Key("t"), date ?? D1, null, (from ?? W1).Id, (to ?? W2).Id, [new CostingTransferLine(item.Id, qty)]));
    protected Task<CostingResult> Undo(string key, DateOnly? date = null, string? reversalKey = null) =>
        Cost.ReverseAsync(Company.Id, Admin.Id, new CostingReversalRequest(reversalKey ?? Key("x"), key, date ?? D1, null));
    protected async Task<CostPositionView> Pos(ItemView item, WarehouseView? wh = null) => (await Cost.GetPositionAsync(Company.Id, item.Id, (wh ?? W1).Id))!;
    protected static async Task<CostingException> Refused(Func<Task> action) => await Assert.ThrowsAsync<CostingException>(action);
    protected async Task AssertConsistent() => Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    protected Task<long> Count(string table) => Sql<long>($"SELECT count(*) FROM {table}");
    protected async Task<decimal> CompanyValue() => await Sql<decimal>($"SELECT coalesce(sum({PO.Value}), 0) FROM {PO.Table}");
}

public class InventoryCostingTests : CostingTestBase
{
    // ---------------------------------------------------------------------------------------------------------------- FIFO

    [PostgresFact]
    public async Task FIFO_consumes_layers_oldest_first_partially_and_completely_with_exact_figures_and_a_trace_of_every_layer()
    {
        await PrepareCosting();
        var r1 = await Receive(Fifo, 10, 100.00m); var r2 = await Receive(Fifo, 10, 200.00m); var r3 = await Receive(Fifo, 10, 300.00m);
        Assert.Equal((10m, 20m, 30m), (r1.Lines[0].UnitCost, r2.Lines[0].UnitCost, r3.Lines[0].UnitCost));
        var p = await Pos(Fifo);
        Assert.Equal((CostMethod.Fifo, 30m, 600m, 3), (p.Method, p.Quantity, p.Value, p.OpenLayers.Count));
        var issue = await IssueOf(Fifo, 25);                                                   // 10 @ 10.00 + 10 @ 20.00 + 5 @ 30.00
        var line = Assert.Single(issue.Lines);
        Assert.Equal((-1, 25m, 450m, 5m, 150m), (line.Direction, line.Quantity, line.Amount, line.QuantityAfter, line.ValueAfter));
        Assert.Equal([(10m, 100m), (10m, 200m), (5m, 150m)], line.Consumptions.Select(c => (c.Quantity, c.Amount)));
        Assert.Equal(p.OpenLayers.Select(l => l.Id), line.Consumptions.Select(c => c.LayerId));      // traceable to the very layers, oldest first
        var after = await Pos(Fifo);
        var open = Assert.Single(after.OpenLayers);
        Assert.Equal((5m, 150m, 10m, 300m), (open.RemainingQuantity, open.RemainingAmount, open.OriginalQuantity, open.OriginalAmount));
        Assert.Equal(-450m, issue.NetValueChange);
        var last = await IssueOf(Fifo, 5);                                                     // the rest of the last layer: its exact remainder
        Assert.Equal(150m, last.Lines[0].Amount);
        var empty = await Pos(Fifo);
        Assert.Equal((0m, 0m, 0), (empty.Quantity, empty.Value, empty.OpenLayers.Count));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_layer_issued_in_thirds_loses_and_invents_nothing_the_last_unit_takes_the_rounding_remainder()
    {
        await PrepareCosting();
        await Receive(Fifo, 3, 100.00m);
        var costs = new List<decimal>();
        for (var i = 0; i < 3; i++) costs.Add((await IssueOf(Fifo, 1)).Lines[0].Amount);
        Assert.Equal([33.33m, 33.34m, 33.33m], costs);
        Assert.Equal(100.00m, costs.Sum());
        Assert.Equal((0m, 0m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Fractional_quantities_in_a_three_decimal_unit_and_three_decimal_currency_stay_exact()
    {
        await PrepareCosting("JOD");                                                               // minor units: 3
        await Receive(KgAvg, 2.5m, 10.001m);
        await Receive(KgAvg, 0.5m, 1.999m);                                                         // 3.000 kg worth 12.000
        var p = await Pos(KgAvg);
        Assert.Equal((3m, 12.000m, 4m), (p.Quantity, p.Value, p.AverageUnitCost));
        var cost = (await IssueOf(KgAvg, 0.001m)).Lines[0];                                         // 12.000 x 0.001 / 3 = 0.004
        Assert.Equal(0.004m, cost.Amount);
        Assert.Equal(11.996m, (await Pos(KgAvg)).Value);
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- moving average

    [PostgresFact]
    public async Task Moving_average_walks_through_receipts_and_issues_with_stated_figures_and_resets_cleanly_at_zero()
    {
        await PrepareCosting();
        await Receive(Avg, 10, 100.00m);
        await Receive(Avg, 20, 260.00m);                                                            // 30 @ avg 12.00
        Assert.Equal((30m, 360m, 12m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg)).AverageUnitCost));
        var i1 = (await IssueOf(Avg, 15)).Lines[0];
        Assert.Equal((180m, 15m, 180m), (i1.Amount, i1.QuantityAfter, i1.ValueAfter));              // 15 x 12.00
        await Receive(Avg, 5, 70.00m);                                                              // 20 @ 250.00 -> avg 12.50
        Assert.Equal((20m, 250m, 12.5m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg)).AverageUnitCost));
        var i2 = (await IssueOf(Avg, 8)).Lines[0];
        Assert.Equal(100m, i2.Amount);                                                              // 8 x 12.50
        var i3 = (await IssueOf(Avg, 12)).Lines[0];                                                 // everything: the exact remainder
        Assert.Equal((150m, 0m, 0m), (i3.Amount, i3.QuantityAfter, i3.ValueAfter));
        Assert.Equal((0m, 0m, 0m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg)).AverageUnitCost));
        await Receive(Avg, 4, 50.00m);                                                              // a fresh start after a zero balance
        Assert.Equal((4m, 50m, 12.5m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg)).AverageUnitCost));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Average_rounding_gives_the_remainder_to_the_last_unit_and_free_goods_are_valued_at_zero()
    {
        await PrepareCosting();
        await Receive(Avg, 3, 100.00m);
        var costs = new[] { (await IssueOf(Avg, 1)).Lines[0].Amount, (await IssueOf(Avg, 1)).Lines[0].Amount, (await IssueOf(Avg, 1)).Lines[0].Amount };
        Assert.Equal([33.33m, 33.34m, 33.33m], costs);
        Assert.Equal(0m, (await Pos(Avg)).Value);
        await Receive(Avg2, 5, 0m);                                                                 // free goods: quantity without value
        Assert.Equal((5m, 0m, 0m), ((await Pos(Avg2)).Quantity, (await Pos(Avg2)).Value, (await Pos(Avg2)).AverageUnitCost));
        Assert.Equal(0m, (await IssueOf(Avg2, 5)).Lines[0].Amount);
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Several_lines_of_one_operation_and_the_same_item_twice_are_applied_in_order()
    {
        await PrepareCosting();
        var r = await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null,
            [new CostingReceiptLine(Avg.Id, W1.Id, 2, 20m), new CostingReceiptLine(Avg.Id, W1.Id, 2, 40m), new CostingReceiptLine(Fifo.Id, W2.Id, 1, 5m)]));
        Assert.Equal([(2m, 20m), (4m, 60m), (1m, 5m)], r.Lines.Select(l => (l.QuantityAfter, l.ValueAfter)));
        Assert.Equal(new[] { (W1.Id, Avg.Id, 4m, 60m), (W2.Id, Fifo.Id, 1m, 5m) }.OrderBy(x => x.Item1).ThenBy(x => x.Item2), r.ToValueLines().Select(l => (l.WarehouseId, l.ItemId, l.QuantityChange, l.ValueChange)));
        Assert.Equal(65m, r.NetValueChange);
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- negative stock, precision, policy

    [PostgresFact]
    public async Task Stock_never_goes_negative_an_issue_above_the_balance_is_refused_and_leaves_no_trace()
    {
        await PrepareCosting();
        await Receive(Fifo, 5, 50m); await Receive(Avg, 5, 50m);
        foreach (var item in new[] { Fifo, Avg })
        {
            var entries = await Count(EN.Table); var ops = await Count(OP.Table); var audit = await Count(Audit_.Table);
            var e = await Refused(() => IssueOf(item, 5.0000000001m));
            Assert.True(e.Code is "insufficient_stock" or "quantity_precision_exceeded");
            e = await Refused(() => IssueOf(item, 6));
            Assert.Equal("insufficient_stock", e.Code);
            Assert.Equal((entries, ops, audit), (await Count(EN.Table), await Count(OP.Table), await Count(Audit_.Table)));
            Assert.Equal((5m, 50m), ((await Pos(item)).Quantity, (await Pos(item)).Value));
        }
        Assert.Equal("insufficient_stock", (await Refused(() => IssueOf(Fifo2, 1))).Code);                         // nothing ever received
        Assert.Equal("insufficient_stock", (await Refused(() => IssueOf(Fifo, 1, W2))).Code);                      // another warehouse has none
        Assert.Equal(5m, (await IssueOf(Fifo, 5)).Lines[0].Quantity - 0m);                                         // exactly the balance is fine
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Amounts_and_quantities_are_validated_for_precision_sign_and_unit_decimals()
    {
        await PrepareCosting("USD");
        Assert.Equal("amount_precision_exceeded", (await Refused(() => Receive(Avg, 1, 1.005m))).Code);          // USD has two decimals: never rounded silently
        Assert.Equal("invalid_amount", (await Refused(() => Receive(Avg, 1, -1m))).Code);
        Assert.Equal("invalid_quantity", (await Refused(() => Receive(Avg, 0, 1m))).Code);
        Assert.Equal("invalid_quantity", (await Refused(() => Receive(Avg, -1, 1m))).Code);
        Assert.Equal("quantity_precision_exceeded", (await Refused(() => Receive(Avg, 1.5m, 1m))).Code);          // pieces have no decimals
        Assert.Equal("quantity_precision_exceeded", (await Refused(() => Receive(KgAvg, 1.0001m, 1m))).Code);     // kilograms have three
        Assert.Equal("invalid_key", (await Refused(() => Receive(Avg, 1, 1m, key: " "))).Code);
        Assert.Equal("invalid_lines", (await Refused(() => Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null, [])))).Code);
        Assert.Equal(0L, await Count(OP.Table));
        Assert.Equal(1.25m, (await Receive(Avg, 1, 1.25m)).Lines[0].Amount);
        Assert.Equal(0.001m, (await Receive(KgAvg, 0.001m, 0.01m)).Lines[0].Quantity);
    }

    [PostgresFact]
    public async Task A_currency_without_a_base_a_service_a_tracked_item_and_unknown_ids_are_refused()
    {
        await Prepare();
        Cost = new InventoryCostingService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Inv, Clock);
        Assert.Equal("base_currency_not_set", (await Refused(() => Receive(Apple, 1, 1m))).Code);
        await Sql($"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('USD', true, true, 1, now())");
        Assert.Equal("item_not_stockable", (await Refused(() => Receive(Service, 1, 1m))).Code);
        Assert.Equal("tracked_item_not_supported_yet", (await Refused(() => Receive(Lot, 1, 1m))).Code);
        Assert.Equal("item_not_found", (await Refused(() => Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null, [new CostingReceiptLine(Guid.NewGuid(), W1.Id, 1, 1m)])))).Code);
        Assert.Equal("warehouse_not_found", (await Refused(() => Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, null, [new CostingReceiptLine(Apple.Id, Guid.NewGuid(), 1, 1m)])))).Code);
        Assert.Equal(0L, await Count(PO.Table));
    }

    [PostgresFact]
    public async Task The_valuation_policy_decides_the_method_and_a_later_policy_change_is_never_applied_silently()
    {
        await PrepareCosting();
        await Receive(Avg, 5, 50m);
        await Receive(Fifo, 5, 50m);
        Assert.Equal((CostMethod.MovingAverage, CostMethod.Fifo), ((await Pos(Avg)).Method, (await Pos(Fifo)).Method));
        // the group of AVG1 is switched to FIFO after the position exists: the engine refuses instead of re-costing
        var g = (await Inv.ListGroupsAsync(Company.Id)).Single(x => x.Code == "AVGG");
        Ok(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, g.Id, g.Revision, new ItemGroupInput("AVGG", "x", "Avg", null, 1)));
        Assert.Equal("valuation_method_changed", (await Refused(() => IssueOf(Avg, 1))).Code);
        Assert.Equal("valuation_method_changed", (await Refused(() => Receive(Avg, 1, 1m))).Code);
        Assert.Equal((5m, 50m, CostMethod.MovingAverage), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg)).Method));      // untouched
        Assert.NotNull(await Raw($"UPDATE {PO.Table} SET {PO.Method} = 1, {PO.LastSeq} = {PO.LastSeq} + 1 WHERE {PO.ItemId} = '{Avg.Id}'"));                        // and PostgreSQL refuses it too
        // an item whose policy nobody decided cannot be costed
        var undecided = Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 1, new InventorySettingsInput(null)));
        Assert.Null(undecided.DefaultValuation);
        Assert.Equal("valuation_method_unresolved", (await Refused(() => Receive(Apple, 1, 1m))).Code);
    }

    [PostgresFact]
    public async Task Back_dated_entries_are_refused_until_the_recalculation_slice_exists()
    {
        await PrepareCosting();
        await Receive(Avg, 5, 50m, date: D2);
        Assert.Equal("backdated_not_supported", (await Refused(() => IssueOf(Avg, 1, date: D1))).Code);
        Assert.Equal("backdated_not_supported", (await Refused(() => Receive(Avg, 1, 1m, date: D1))).Code);
        Assert.Equal(10m, (await IssueOf(Avg, 1, date: D2)).Lines[0].Amount);                       // the same day is fine
        Assert.Equal(10m, (await IssueOf(Avg, 1, date: D3)).Lines[0].Amount);
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- transfers

    [PostgresFact]
    public async Task A_FIFO_transfer_carries_the_actual_layers_so_no_profit_or_loss_arises_and_the_destination_keeps_the_order()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100m); await Receive(Fifo, 10, 200m);                                // layers 10.00 and 20.00 per unit
        var before = await CompanyValue();
        var t = await Move(Fifo, 15);                                                                  // 10 @ 10 + 5 @ 20 = 200.00
        Assert.Equal(0m, t.NetValueChange);
        Assert.Equal([(-1, 15m, 200m, CostEntryKind.TransferOut), (1, 15m, 200m, CostEntryKind.TransferIn)], t.Lines.Select(l => (l.Direction, l.Quantity, l.Amount, l.Kind)));
        Assert.Equal(before, await CompanyValue());                                                    // the company total value is unchanged
        var src = await Pos(Fifo); var dst = await Pos(Fifo, W2);
        Assert.Equal((5m, 100m), (src.Quantity, src.Value));
        Assert.Equal((15m, 200m), (dst.Quantity, dst.Value));
        Assert.Equal([(10m, 100m), (5m, 100m)], dst.OpenLayers.Select(l => (l.RemainingQuantity, l.RemainingAmount)));     // mirrored layers: the actual cost
        Assert.All(dst.OpenLayers, l => Assert.NotNull(l.SourceLayerId));                                                  // traceable to the source layers
        Assert.Equal(140m, (await IssueOf(Fifo, 12, W2)).Lines[0].Amount);                              // 10 @ 10.00 + 2 @ 20.00
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task An_average_transfer_moves_exactly_the_issued_value_even_when_rounding_applies()
    {
        await PrepareCosting();
        await Receive(Avg, 3, 100.00m);
        var before = await CompanyValue();
        var t = await Move(Avg, 1);
        Assert.Equal((33.33m, 33.33m), (t.Lines[0].Amount, t.Lines[1].Amount));
        Assert.Equal(before, await CompanyValue());
        Assert.Equal((2m, 66.67m, 1m, 33.33m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg, W2)).Quantity, (await Pos(Avg, W2)).Value));
        var back = await Move(Avg, 1, W2, W1);                                                         // and back: the average of the source is intact
        Assert.Equal(33.33m, back.Lines[0].Amount);
        Assert.Equal((3m, 100.00m, 0m, 0m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg, W2)).Quantity, (await Pos(Avg, W2)).Value));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Transfer_rules_insufficient_stock_same_warehouse_duplicates_and_atomicity_across_lines()
    {
        await PrepareCosting();
        await Receive(Fifo, 5, 50m); await Receive(Avg, 5, 50m);
        Assert.Equal("insufficient_stock", (await Refused(() => Move(Fifo, 6))).Code);
        Assert.Equal("same_warehouse", (await Refused(() => Move(Fifo, 1, W1, W1))).Code);
        Assert.Equal("duplicate_item_line", (await Refused(() => Cost.TransferAsync(Company.Id, Admin.Id, new CostingTransferRequest(Key(), D1, null, W1.Id, W2.Id, [new CostingTransferLine(Fifo.Id, 1), new CostingTransferLine(Fifo.Id, 1)])))).Code);
        // the second line fails: the first line's movement must not remain
        var e = await Refused(() => Cost.TransferAsync(Company.Id, Admin.Id, new CostingTransferRequest(Key(), D1, null, W1.Id, W2.Id, [new CostingTransferLine(Avg.Id, 2), new CostingTransferLine(Fifo.Id, 99)])));
        Assert.Equal("insufficient_stock", e.Code);
        Assert.Equal((5m, 50m, 5m, 50m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        Assert.Null(await Cost.GetPositionAsync(Company.Id, Avg.Id, W2.Id));                                // not even the empty destination position survived the rollback
        Assert.Equal(2L, await Count(OP.Table));
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- idempotency, audit, rollback

    [PostgresFact]
    public async Task A_repeated_request_returns_the_original_result_and_changes_nothing_and_a_reused_key_with_other_content_is_refused()
    {
        await PrepareCosting();
        var k = Key("idem");
        var first = await Receive(Avg, 4, 40m, key: k);
        var entries = await Count(EN.Table); var audit = await Count(Audit_.Table); var ops = await Count(OP.Table);
        var again = await Receive(Avg, 4, 40m, key: k);
        Assert.True(again.Replayed);
        Assert.False(first.Replayed);
        Assert.Equal((first.OperationId, first.Lines[0].EntryId, first.Lines[0].Amount), (again.OperationId, again.Lines[0].EntryId, again.Lines[0].Amount));
        Assert.Equal((entries, audit, ops), (await Count(EN.Table), await Count(Audit_.Table), await Count(OP.Table)));
        Assert.Equal((4m, 40m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        Assert.Equal("idempotency_key_reused", (await Refused(() => Receive(Avg, 5, 40m, key: k))).Code);          // other quantity
        Assert.Equal("idempotency_key_reused", (await Refused(() => Receive(Avg, 4, 41m, key: k))).Code);          // other amount
        Assert.Equal("idempotency_key_reused", (await Refused(() => IssueOf(Avg, 1, key: k))).Code);               // other kind
        Assert.Equal("idempotency_key_reused", (await Refused(() => Receive(Avg, 4, 40m, date: D2, key: k))).Code); // other date
        var iss = Key("iss");
        var i1 = await IssueOf(Avg, 1, key: iss); var i2 = await IssueOf(Avg, 1, key: iss);
        Assert.True(i2.Replayed);
        Assert.Equal(3m, (await Pos(Avg)).Quantity);                                                                // issued once
    }

    [PostgresFact]
    public async Task Parallel_duplicate_requests_with_one_key_apply_exactly_once()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100m);
        var k = Key("par");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => IssueOf(Fifo, 2, key: k)));
        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(7, results.Count(r => r.Replayed));
        Assert.Single(results.Select(r => r.OperationId).Distinct());
        Assert.Equal((8m, 80m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {OP.Table} WHERE {OP.Key} = '{k}'"));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Every_operation_writes_one_audit_event_and_a_costing_failure_inside_a_larger_transaction_rolls_everything_back()
    {
        await PrepareCosting();
        var a0 = await Count(Audit_.Table);
        await Receive(Avg, 10, 100m); await IssueOf(Avg, 1); await Move(Avg, 1); await Undo((await Sql<string>($"SELECT {OP.Key} FROM {OP.Table} WHERE {OP.Kind} = 2")));
        Assert.Equal("1150,1151,1152,1153", await Sql<string>($"SELECT string_agg({Audit_.EventType}::text, ',' ORDER BY {Audit_.Seq}) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1150 AND 1159"));
        Assert.Equal(a0 + 4, await Count(Audit_.Table));
        // the future posting transaction: costing succeeds, then something else fails: nothing of the costing may remain
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        var before = (await Count(EN.Table), await Count(OP.Table), await Count(LY.Table), await Count(CS.Table), await Count(Audit_.Table));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExecuteAsync(Company.Id, async work =>
        {
            await Cost.IssueAsync(work, Admin.Id, new CostingIssueRequest(Key(), D1, null, [new CostingIssueLine(Avg.Id, W1.Id, 2)]), default);
            await Cost.ReceiveAsync(work, Admin.Id, new CostingReceiptRequest(Key(), D1, null, [new CostingReceiptLine(Fifo.Id, W1.Id, 3, 30m)]), default);
            throw new InvalidOperationException("the accounting side failed");
        }));
        Assert.Equal(before, (await Count(EN.Table), await Count(OP.Table), await Count(LY.Table), await Count(CS.Table), await Count(Audit_.Table)));
        Assert.Null(await Cost.GetPositionAsync(Company.Id, Fifo.Id, W1.Id));
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- concurrency

    [PostgresFact]
    public async Task Concurrent_issues_never_consume_the_same_stock_twice_exactly_the_available_quantity_is_issued()
    {
        await PrepareCosting();
        await Receive(Fifo, 6, 60m); await Receive(Fifo, 4, 80m); await Receive(Avg, 10, 100m);
        foreach (var item in new[] { Fifo, Avg })
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => { try { return (CostingResult?)await IssueOf(item, 1); } catch (CostingException e) when (e.Code == "insufficient_stock") { return null; } }));
            Assert.Equal(10, results.Count(r => r is not null));
            var p = await Pos(item);
            Assert.Equal((0m, 0m, 0), (p.Quantity, p.Value, p.OpenLayers.Count));
            Assert.Equal(item == Fifo ? 140m : 100m, results.Where(r => r is not null).Sum(r => r!.Lines[0].Amount));          // all the cost that ever came in left exactly once
            var seqs = await Sql<string>($"SELECT string_agg({EN.Seq}::text, ',' ORDER BY {EN.Seq}) FROM {EN.Table} WHERE {EN.ItemId} = '{item.Id}'");
            Assert.Equal(string.Join(',', Enumerable.Range(1, item == Fifo ? 12 : 11)), seqs);                                     // gapless, unique sequence per position
        }
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Concurrent_mixed_operations_in_both_directions_are_deadlock_free_and_value_conserving()
    {
        await PrepareCosting();
        await Receive(Avg, 100, 1000m, W1); await Receive(Avg, 100, 1000m, W2); await Receive(Fifo, 100, 500m, W1); await Receive(Fifo, 100, 700m, W2);
        var before = await CompanyValue();
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Move(Avg, 3, W1, W2)); tasks.Add(Move(Avg, 2, W2, W1)); tasks.Add(Move(Fifo, 4, W1, W2)); tasks.Add(Move(Fifo, 5, W2, W1));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(before, await CompanyValue());                                                    // transfers alone create no value
        Assert.Equal((200m, 200m), ((await Pos(Avg)).Quantity + (await Pos(Avg, W2)).Quantity, (await Pos(Fifo)).Quantity + (await Pos(Fifo, W2)).Quantity));
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- reversals and corrections

    [PostgresFact]
    public async Task Reversing_a_FIFO_issue_restores_exactly_the_consumed_layers_and_an_operation_can_be_reversed_only_once()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100m); await Receive(Fifo, 10, 200m);
        var layersBefore = (await Pos(Fifo)).OpenLayers.Select(l => (l.Id, l.RemainingQuantity, l.RemainingAmount)).ToList();
        var k = Key("iss");
        await IssueOf(Fifo, 15, key: k);
        var undo = await Undo(k);
        Assert.Equal(CostOperationKind.Reversal, undo.Kind);
        Assert.Equal([(1, 15m, 200m)], undo.Lines.Select(l => (l.Direction, l.Quantity, l.Amount)));
        Assert.All(undo.Lines[0].Consumptions, c => Assert.True(c.Restores));
        Assert.Equal(layersBefore, (await Pos(Fifo)).OpenLayers.Select(l => (l.Id, l.RemainingQuantity, l.RemainingAmount)).ToList());      // the very same layers, the very same amounts
        Assert.Equal("already_reversed", (await Refused(() => Undo(k))).Code);
        Assert.Equal("reversal_cannot_be_reversed", (await Refused(() => Undo(undo.Key))).Code);
        Assert.Equal("operation_not_found", (await Refused(() => Undo("no-such-key"))).Code);
        var rk = Key("rev");
        var iss2 = Key("iss2");
        await IssueOf(Fifo, 4, key: iss2);
        var u2 = await Undo(iss2, reversalKey: rk);
        Assert.True((await Undo(iss2, reversalKey: rk)).Replayed);                                     // the reversal itself is idempotent
        Assert.Equal(u2.OperationId, (await Undo(iss2, reversalKey: rk)).OperationId);
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Reversing_a_receipt_works_only_while_its_goods_are_untouched()
    {
        await PrepareCosting();
        var k1 = Key("r1"); var k2 = Key("r2");
        await Receive(Fifo, 10, 100m, key: k1);
        await Receive(Fifo, 5, 100m, key: k2);
        await IssueOf(Fifo, 12);                                                                       // consumes layer 1 completely and 2 units of layer 2
        Assert.Equal("reversal_not_possible_goods_consumed", (await Refused(() => Undo(k1))).Code);
        Assert.Equal("reversal_not_possible_goods_consumed", (await Refused(() => Undo(k2))).Code);
        Assert.Equal((3m, 60m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));                // the refusals changed nothing
        var untouched = Key("r3");
        await Receive(Fifo, 2, 30m, key: untouched);
        var u = await Undo(untouched);
        Assert.Equal((-1, 2m, 30m), (u.Lines[0].Direction, u.Lines[0].Quantity, u.Lines[0].Amount));
        Assert.Equal((3m, 60m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        // moving average: only the LAST entry of the position can be taken out again
        var a1 = Key("a1"); var a2 = Key("a2");
        await Receive(Avg, 4, 40m, key: a1); await Receive(Avg, 4, 80m, key: a2);
        Assert.Equal("reversal_not_possible_later_movements", (await Refused(() => Undo(a1))).Code);
        var ua = await Undo(a2);
        Assert.Equal(40m, ua.Lines[0].ValueAfter);
        Assert.Equal(4m, (await Undo(a1)).Lines[0].Amount / 10m);                                       // now a1 is the last entry
        Assert.Equal((0m, 0m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Reversing_a_transfer_undoes_the_destination_first_and_restores_the_source_and_is_refused_once_the_destination_stock_moved()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100m); await Receive(Fifo, 10, 200m);
        var k = Key("tr");
        await Move(Fifo, 15, key: k);
        var before = await CompanyValue();
        var u = await Undo(k);
        Assert.Equal(before, await CompanyValue());
        Assert.Equal([(CostEntryKind.TransferIn, -1), (CostEntryKind.TransferOut, 1)], u.Lines.Select(l => (l.Kind, l.Direction)));
        Assert.Equal((20m, 300m, 0m, 0m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value, (await Pos(Fifo, W2)).Quantity, (await Pos(Fifo, W2)).Value));
        Assert.Equal([(10m, 100m), (10m, 200m)], (await Pos(Fifo)).OpenLayers.Select(l => (l.RemainingQuantity, l.RemainingAmount)));
        var k2 = Key("tr2");
        await Move(Fifo, 5, key: k2);
        await IssueOf(Fifo, 1, W2);                                                                    // the destination stock is touched
        Assert.Equal("reversal_not_possible_goods_consumed", (await Refused(() => Undo(k2))).Code);
        Assert.Equal((15m, 4m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo, W2)).Quantity));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task A_cost_correction_is_a_reversal_followed_by_a_new_operation_and_the_history_stays_complete()
    {
        await PrepareCosting();
        var k = Key("wrong");
        await Receive(Avg, 10, 100m, key: k);                                                           // entered at the wrong value
        await Undo(k);
        await Receive(Avg, 10, 120m);                                                                   // corrected
        Assert.Equal((10m, 120m, 12m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg)).AverageUnitCost));
        Assert.Equal(3L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.ItemId} = '{Avg.Id}'"));       // receipt, reversal, corrected receipt: nothing was edited or deleted
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.ReversesEntryId} IS NOT NULL"));
        await AssertConsistent();
    }

    // ---------------------------------------------------------------------------------------------------------------- reconciliation, isolation, guards, compatibility

    [PostgresFact]
    public async Task A_long_deterministic_scenario_of_every_operation_keeps_every_reconciliation_invariant()
    {
        await PrepareCosting();
        var rng = new Random(20260315);
        var items = new[] { Fifo, Fifo2, Avg, Avg2 };
        var whs = new[] { W1, W2 };
        var applied = new List<string>();
        for (var step = 0; step < 150; step++)
        {
            var item = items[rng.Next(items.Length)]; var wh = whs[rng.Next(2)]; var other = whs[1 - Array.IndexOf(whs, wh)];
            var qty = rng.Next(1, 12);
            var date = D1.AddDays(step / 30);
            try
            {
                switch (rng.Next(5))
                {
                    case 0 or 1: { var r = await Receive(item, qty, qty * (rng.Next(50, 400) / 10m) + rng.Next(0, 100) / 100m, wh, date); applied.Add(r.Key); break; }
                    case 2: { var r = await IssueOf(item, qty, wh, date); applied.Add(r.Key); break; }
                    case 3: { var r = await Move(item, qty, wh, other, date); applied.Add(r.Key); break; }
                    default:
                        if (applied.Count > 0) { var key = applied[rng.Next(applied.Count)]; await Undo(key, date); applied.Remove(key); }
                        break;
                }
            }
            catch (CostingException e) when (e.Code is "insufficient_stock" or "reversal_not_possible_goods_consumed" or "reversal_not_possible_later_movements" or "already_reversed" or "reversal_cannot_be_reversed") { }
        }
        Assert.True(await Count(EN.Table) > 80);                                                       // the scenario really did a lot
        await AssertConsistent();
        foreach (var item in items) foreach (var wh in whs)
        {
            var p = await Cost.GetPositionAsync(Company.Id, item.Id, wh.Id);
            if (p is null) continue;
            Assert.True(p.Quantity >= 0 && p.Value >= 0);
            if (p.Method == CostMethod.Fifo) Assert.True(p.OpenLayers.All(l => l.RemainingQuantity > 0 && l.RemainingQuantity <= l.OriginalQuantity && l.RemainingAmount <= l.OriginalAmount));
        }
    }

    [PostgresFact]
    public async Task Another_company_shares_nothing_the_same_key_is_independent_and_foreign_items_are_unknown()
    {
        await PrepareCosting();
        var other = await ReadyCompany();
        var otherResolver = SinglePrincipalResolver(other);
        var otherTx = new CompanyAuditedTransactions(otherResolver, Audit, Clock);
        var otherCost = new InventoryCostingService(otherResolver, otherTx, new CompanyInventoryService(otherResolver, otherTx, Clock), Clock);
        var k = Key("shared");
        await Receive(Avg, 3, 30m, key: k);
        var e = await Assert.ThrowsAsync<CostingException>(() => otherCost.ReceiveAsync(other.Id, Admin.Id, new CostingReceiptRequest(k, D1, null, [new CostingReceiptLine(Avg.Id, W1.Id, 3, 30m)])));
        Assert.Contains(e.Code, new[] { "base_currency_not_set", "item_not_found" });                  // the other company has neither this item nor this warehouse nor (here) a base currency
        Assert.Equal(0L, await InCompany<long>(other.PhysicalDatabaseName, $"SELECT count(*) FROM {OP.Table}"));
        Assert.Equal(1L, await Count(OP.Table));
    }

    [PostgresFact]
    public async Task PostgreSQL_protects_the_costing_tables_even_from_direct_sql()
    {
        await PrepareCosting();
        await Receive(Fifo, 10, 100m); await IssueOf(Fifo, 4);
        Assert.NotNull(await Raw($"UPDATE {EN.Table} SET {EN.Amount} = 1"));
        Assert.NotNull(await Raw($"DELETE FROM {EN.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {EN.Table}"));
        Assert.NotNull(await Raw($"UPDATE {CS.Table} SET {CS.Amount} = 1"));
        Assert.NotNull(await Raw($"DELETE FROM {CS.Table}"));
        Assert.NotNull(await Raw($"DELETE FROM {LY.Table}"));
        Assert.NotNull(await Raw($"DELETE FROM {PO.Table}"));
        Assert.NotNull(await Raw($"DELETE FROM {OP.Table}"));
        Assert.NotNull(await Raw($"UPDATE {OP.Table} SET {OP.Key} = 'x'"));
        Assert.NotNull(await Raw($"UPDATE {LY.Table} SET {LY.RemainingQuantity} = -1"));                                       // no negative FIFO layer
        Assert.NotNull(await Raw($"UPDATE {LY.Table} SET {LY.RemainingQuantity} = {LY.OriginalQuantity} + 1"));                // never more than received
        Assert.NotNull(await Raw($"UPDATE {LY.Table} SET {LY.OriginalAmount} = 1"));
        Assert.NotNull(await Raw($"UPDATE {PO.Table} SET {PO.Quantity} = -1, {PO.LastSeq} = {PO.LastSeq} + 1"));              // no negative stock
        Assert.NotNull(await Raw($"UPDATE {PO.Table} SET {PO.Quantity} = 0, {PO.LastSeq} = {PO.LastSeq} + 1"));               // a position without quantity cannot keep a value
        Assert.NotNull(await Raw($"UPDATE {PO.Table} SET {PO.Value} = {PO.Value} + 1"));                                       // a position moves only with an entry (sequence +1)
        Assert.NotNull(await Raw($"UPDATE {PO.Table} SET {PO.WarehouseId} = '{W2.Id}'"));
        Assert.Null(await Raw($"UPDATE {OP.Table} SET {OP.ReversedByOperationId} = (SELECT {OP.Id} FROM {OP.Table} LIMIT 1) WHERE false"));
        await AssertConsistent();
    }

    [PostgresFact]
    public async Task Approved_stock_documents_are_never_executed_by_the_costing_engine_and_nothing_links_documents_to_cost_tables()
    {
        await PrepareCosting();
        var approver = await Approver();
        var d = await Draft(Receipt(W1, "2026-03-10", L(Apple, Pcs, 5m)));
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        var approved = Ok(await Docs.ApproveAsync(approver.Id, Company.Id, d.Id, s.Revision));
        Assert.Equal(3, approved.Status);
        foreach (var t in new[] { OP.Table, PO.Table, EN.Table, LY.Table, CS.Table }) Assert.Equal(0L, await Count(t));        // approval is not posting: no cost, no layer, no position
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.table_constraints c JOIN information_schema.constraint_column_usage u ON u.constraint_name = c.constraint_name WHERE c.constraint_type = 'FOREIGN KEY' AND c.table_name IN ('{CompanySchema.StockDocument.Table}', '{CompanySchema.StockDocumentLine.Table}') AND u.table_name IN ('{OP.Table}', '{PO.Table}', '{EN.Table}', '{LY.Table}', '{CS.Table}')"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.table_constraints c JOIN information_schema.constraint_column_usage u ON u.constraint_name = c.constraint_name WHERE c.constraint_type = 'FOREIGN KEY' AND c.table_name IN ('{OP.Table}', '{PO.Table}', '{EN.Table}', '{LY.Table}', '{CS.Table}') AND u.table_name IN ('{CompanySchema.StockDocument.Table}', '{CompanySchema.StockDocumentLine.Table}')"));
        // the engine only acts on an explicit request: a source reference is traceability, never an instruction
        var r = await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest(Key(), D1, new CostingSource("stock_document", approved.Id), [new CostingReceiptLine(Avg.Id, W1.Id, 5, 25m)]));
        Assert.Equal(1L, await Count(OP.Table));
        Assert.Equal(("stock_document", approved.Id), (await Sql<string>($"SELECT {OP.SourceType} FROM {OP.Table}"), await Sql<Guid>($"SELECT {OP.SourceId} FROM {OP.Table}")));
        Assert.Equal(3, (await Docs.GetAsync(Company.Id, approved.Id))!.Status);                       // the document itself is untouched by costing
        Assert.NotNull(r);
    }

    [PostgresFact]
    public async Task The_accounting_contract_gives_signed_value_changes_per_item_and_warehouse_that_net_to_zero_for_transfers()
    {
        await PrepareCosting();
        var receipt = await Receive(Avg, 10, 100m);
        Assert.Equal([new InventoryValueLine(Avg.Id, W1.Id, 10m, 100m)], receipt.ToValueLines());
        var issue = await IssueOf(Avg, 4);
        Assert.Equal([new InventoryValueLine(Avg.Id, W1.Id, -4m, -40m)], issue.ToValueLines());
        var transfer = await Move(Avg, 3);
        Assert.Equal(0m, transfer.NetValueChange);
        Assert.Equal(new[] { -3m, 3m }, transfer.ToValueLines().Select(l => l.QuantityChange).Order());
        Assert.Equal(new[] { -30m, 30m }, transfer.ToValueLines().Select(l => l.ValueChange).Order());
        Assert.Equal(["ToValueLines", "NetValueChange"], new[] { nameof(CostingResult.ToValueLines), nameof(CostingResult.NetValueChange) });
    }
}
