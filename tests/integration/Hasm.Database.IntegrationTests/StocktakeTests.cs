using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using CEn = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using ST = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Stocktake;
using STL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StocktakeLine;
using STE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StocktakeEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8B-1: stocktake sheets and their variance (company migration 0018, system migration 0021) against real PostgreSQL: book snapshot from the costing ledger as of the count date, exact variance in the base
/// unit, life cycle with numbering and audit, separation of duties, concurrency, warehouse-scoped authorization and the database guards. A stocktake changes NO stock figure. Disposable cluster only.
/// </summary>
public abstract class StocktakeTestBase : CostingTestBase
{
    protected StocktakeService Takes = null!;
    protected StocktakeManagementService TakeMgmt = null!;
    protected const string CountDate = "2026-03-11";

    protected async Task PrepareStocktake()
    {
        await PrepareCosting();
        Takes = new StocktakeService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        TakeMgmt = new StocktakeManagementService(Rbac, Docs, Takes);
    }

    protected static StocktakeInput Head(WarehouseView wh, string date = CountDate, params StocktakeLineInput[] lines) => new(wh.Id, date, "STK-REF", null, lines);
    protected static StocktakeLineInput C(ItemView item, UnitView unit, decimal? qty, string? reason = null) => new(item.Id, unit.Id, qty, reason);

    /// <summary>The current sheet with the given counts applied (unit, quantity, reason per item); other lines are kept as they are.</summary>
    protected static StocktakeInput Counted(StocktakeView v, params (ItemView Item, UnitView Unit, decimal? Qty, string? Reason)[] counts) =>
        new(v.WarehouseId, v.CountDate, v.Reference, v.Notes, [.. v.Lines.Select(l => counts.FirstOrDefault(c => c.Item.Id == l.ItemId) is var c && c.Item is not null
            ? new StocktakeLineInput(l.ItemId, c.Unit.Id, c.Qty, c.Reason) : new StocktakeLineInput(l.ItemId, l.UnitId, l.CountedQuantity, l.Reason))]);

    protected async Task<StocktakeView> Draft(WarehouseView? wh = null, params StocktakeLineInput[] lines) => Ok(await Takes.CreateAsync(Admin.Id, Company.Id, Head(wh ?? W1, CountDate, lines)));
    protected async Task<StocktakeView> SubmittedTake(UserRecord? by = null, WarehouseView? wh = null)
    {
        var d = await Draft(wh, C(Fifo, Pcs, 9m, "two damaged"));
        return Ok(await Takes.SubmitAsync((by ?? Admin).Id, Company.Id, d.Id, d.Revision));
    }
    protected async Task<UserRecord> TakeApprover(ScopeKind scope = ScopeKind.Company, Guid? id = null) =>
        (await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeApprove], scope, id)).User;
    protected Task<long> TakeAudit() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1170 AND 1179");
    protected async Task<(long Ops, long Entries)> CostRows() => (await Count(CO.Table), await Count(CEn.Table));
}

public class StocktakeTests : StocktakeTestBase
{
    [PostgresFact]
    public async Task The_sheet_takes_the_book_quantity_of_the_warehouse_as_of_the_count_date_and_the_variance_is_exact_in_the_base_unit()
    {
        await PrepareStocktake();
        await Receive(Fifo, 10m, 100m);                                                      // W1, 2026-03-10
        await Receive(Avg, 4m, 40m);
        await Receive(KgAvg, 2.5m, 25m);
        await Receive(Apple, 30m, 300m);
        await Receive(Fifo, 7m, 70m, W2);                                                     // another warehouse: not on this sheet
        await IssueOf(Avg, 1m, date: new DateOnly(2026, 3, 20));                             // after the count date: not in the book quantity
        await Receive(Fifo2, 5m, 50m); await IssueOf(Fifo2, 5m);                          // held, then emptied: zero book, not generated
        var cost = await CostRows();
        var st = await Draft();
        Assert.Empty(st.Lines);
        var gen = Ok(await Takes.GenerateSheetAsync(Admin.Id, Company.Id, st.Id, st.Revision, new StocktakeSheetInput(null)));
        Assert.Equal(["APPLE", "AVG1", "FIFO1", "KGAVG"], gen.Lines.Select(l => l.ItemCode));
        Assert.Equal([30m, 4m, 10m, 2.5m], gen.Lines.Select(l => l.BookQuantity));
        Assert.All(gen.Lines, l => Assert.Equal((null, null, false), (l.CountedQuantity, l.Variance, l.Stale)));
        var counted = Ok(await Takes.UpdateAsync(Admin.Id, Company.Id, st.Id, gen.Revision, Counted(gen,
            (Apple, Box, 2m, "broken pieces"), (Avg, Pcs, 4m, null), (Fifo, Pcs, 12m, "found on the shelf"), (KgAvg, Kg, 2.505m, "scale"))));
        var byCode = counted.Lines.ToDictionary(l => l.ItemCode);
        Assert.Equal((12m, 24m, -6m), (byCode["APPLE"].Factor, byCode["APPLE"].CountedBaseQuantity, byCode["APPLE"].Variance));    // 2 boxes x 12 pieces - 30 = -6, in pieces
        Assert.Equal(0m, byCode["AVG1"].Variance);
        Assert.Equal(2m, byCode["FIFO1"].Variance);
        Assert.Equal(0.005m, byCode["KGAVG"].Variance);                                                                          // exact decimal, never floating point
        // a second generation keeps every count and adds nothing new; chosen items are added with their (zero) book quantity
        var again = Ok(await Takes.GenerateSheetAsync(Admin.Id, Company.Id, st.Id, counted.Revision, new StocktakeSheetInput(null)));
        Assert.Equal(counted.Lines.Select(l => (l.ItemId, l.CountedQuantity, l.Variance)), again.Lines.Select(l => (l.ItemId, l.CountedQuantity, l.Variance)));
        var chosen = Ok(await Takes.GenerateSheetAsync(Admin.Id, Company.Id, st.Id, again.Revision, new StocktakeSheetInput([Fifo2.Id])));
        Assert.Equal(("FIFO2", 0m, (decimal?)null), (chosen.Lines[^1].ItemCode, chosen.Lines[^1].BookQuantity, chosen.Lines[^1].CountedQuantity));
        // a count that does not fit the unit's decimals is refused, never rounded
        Invalid(await Takes.UpdateAsync(Admin.Id, Company.Id, st.Id, chosen.Revision, Counted(chosen, (KgAvg, Kg, 2.5055m, "scale"))), "lines[3].countedQuantity", "quantity_precision_exceeded");
        Assert.Equal(cost, await CostRows());                                                                                    // counting moves no stock
    }

    [PostgresFact]
    public async Task A_stocktake_is_submitted_only_when_complete_then_approved_by_someone_else_with_a_number_history_and_audit()
    {
        await PrepareStocktake();
        await Receive(Fifo, 10m, 100m);
        await Receive(Avg, 4m, 40m);
        var cost = await CostRows();
        var d = await Draft(W1, C(Fifo, Pcs, null), C(Avg, Pcs, 3m));
        var refused = await Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision);
        Invalid(refused, "lines[0].countedQuantity", "required");                                                              // not counted yet
        Invalid(refused, "lines[1].reason", "required_for_variance");                                                          // a variance needs a reason
        var fixedUp = Ok(await Takes.UpdateAsync(Admin.Id, Company.Id, d.Id, d.Revision, Counted(d, (Fifo, Pcs, 10m, null), (Avg, Pcs, 3m, "one missing"))));
        var sub = Ok(await Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, fixedUp.Revision));
        Assert.Equal((2, Admin.Id), (sub.Status, sub.SubmittedBy));
        Fails(await Takes.UpdateAsync(Admin.Id, Company.Id, d.Id, sub.Revision, Counted(sub)), ConfigStatus.Conflict, "invalid_status");      // frozen from submit on
        Fails(await Takes.ApproveAsync(Admin.Id, Company.Id, d.Id, sub.Revision), ConfigStatus.Conflict, "self_approval_not_allowed");
        var approver = await TakeApprover();
        var ok = Ok(await Takes.ApproveAsync(approver.Id, Company.Id, d.Id, sub.Revision));
        Assert.Equal((3, "STK-2026-000001", approver.Id), (ok.Status, ok.Number, ok.DecidedBy));
        Assert.Equal([1, 2, 3, 5], ok.Events.Select(e => e.Kind));                                                              // created, updated, submitted, approved
        Assert.Equal(4L, await TakeAudit());
        Assert.Equal(4L, await Count(STE.Table));
        Assert.Equal(cost, await CostRows());                                                                                  // approval moves no stock and books nothing
        // withdraw, reject and cancel; a cancelled approved stocktake keeps its number, which is void and never reused
        var (u2, s2) = (Admin, await SubmittedTake());
        var back = Ok(await Takes.WithdrawAsync(Admin.Id, Company.Id, s2.Id, s2.Revision));
        Assert.Equal((1, (Guid?)null), (back.Status, back.SubmittedBy));
        var s3 = Ok(await Takes.SubmitAsync(u2.Id, Company.Id, s2.Id, back.Revision));
        Invalid(await Takes.RejectAsync(approver.Id, Company.Id, s2.Id, s3.Revision, " "), "note", "required");
        Assert.Equal(4, Ok(await Takes.RejectAsync(approver.Id, Company.Id, s2.Id, s3.Revision, "count again")).Status);
        var cancelled = Ok(await Takes.CancelAsync(Admin.Id, Company.Id, ok.Id, ok.Revision, "counted the wrong aisle"));
        Assert.Equal((5, "STK-2026-000001"), (cancelled.Status, cancelled.Number));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stocktake' AND {NI.Number} = 'STK-2026-000001' AND {NI.VoidedAtUtc} IS NOT NULL"));
        var next = await SubmittedTake();
        Assert.Equal("STK-2026-000002", Ok(await Takes.ApproveAsync(approver.Id, Company.Id, next.Id, next.Revision)).Number);
        Fails(await Takes.CancelAsync(Admin.Id, Company.Id, s2.Id, s3.Revision + 1, "x"), ConfigStatus.Conflict, "invalid_status");   // rejected is final
    }

    [PostgresFact]
    public async Task The_snapshot_is_frozen_at_submit_a_later_back_dated_movement_marks_it_stale_and_approval_is_still_allowed()
    {
        await PrepareStocktake();
        await Receive(Fifo, 10m, 100m);
        var d = await Draft(W1, C(Fifo, Pcs, 10m, "checked twice"));
        await Receive(Fifo, 1m, 10m, date: new DateOnly(2026, 3, 11));                        // on the count date, before submit: the draft is stale until saved again
        var draft = (await Takes.GetAsync(Company.Id, d.Id))!;
        Assert.True(draft.Stale);
        Assert.Equal((10m, 11m), (draft.Lines[0].BookQuantity, draft.Lines[0].CurrentBookQuantity));
        var sub = Ok(await Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));                                        // submit retakes the snapshot
        Assert.Equal((11m, -1m, false), (sub.Lines[0].BookQuantity, sub.Lines[0].Variance, sub.Stale));
        await IssueOf(Fifo, 3m, date: new DateOnly(2026, 3, 11));                              // on the count date, after submit
        await Receive(Fifo, 5m, 50m, date: new DateOnly(2026, 3, 12));                         // after the count date: never affects the sheet
        var stale = (await Takes.GetAsync(Company.Id, d.Id))!;
        Assert.Equal((11m, 8m, true, -1m), (stale.Lines[0].BookQuantity, stale.Lines[0].CurrentBookQuantity, stale.Stale, stale.Lines[0].Variance));     // the frozen snapshot is unchanged
        var ok = Ok(await Takes.ApproveAsync((await TakeApprover()).Id, Company.Id, d.Id, sub.Revision));
        Assert.Equal((3, 11m, true), (ok.Status, ok.Lines[0].BookQuantity, ok.Stale));
    }

    [PostgresFact]
    public async Task Concurrent_approvals_and_submits_act_exactly_once()
    {
        await PrepareStocktake();
        var s = await SubmittedTake();
        var approvers = new List<UserRecord>();
        for (var i = 0; i < 6; i++) approvers.Add(await TakeApprover());
        var results = await Task.WhenAll(approvers.Select(a => Takes.ApproveAsync(a.Id, Company.Id, s.Id, s.Revision)));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stocktake'"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {STE.Table} WHERE {STE.StocktakeId} = '{s.Id}' AND {STE.Kind} = 5"));
        var d = await Draft(W1, C(Fifo, Pcs, 0m, "nothing found"));
        var submits = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision)));
        Assert.Equal(1, submits.Count(r => r.IsOk));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {STE.Table} WHERE {STE.StocktakeId} = '{d.Id}' AND {STE.Kind} = 3"));
    }

    [PostgresFact]
    public async Task Permissions_are_warehouse_scoped_separate_per_action_and_nothing_of_another_warehouse_is_disclosed()
    {
        await PrepareStocktake();
        var (counter, _) = await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeCreate, PermissionCatalog.Codes.StocktakeUpdate, PermissionCatalog.Codes.StocktakeSubmit], ScopeKind.Warehouse, W1.Id);
        var mine = Ok(await TakeMgmt.CreateAsync(counter.Id, Company.Id, Head(W1, CountDate, C(Fifo, Pcs, 1m, "x"))));
        Fails(await TakeMgmt.CreateAsync(counter.Id, Company.Id, Head(W2)), ConfigStatus.Forbidden, "forbidden");                        // a W1 grant does not reach W2
        Assert.Equal(new[] { W1.Id }, (await TakeMgmt.CapabilitiesAsync(counter.Id, Company.Id)).Create);
        var sub = Ok(await TakeMgmt.SubmitAsync(counter.Id, Company.Id, mine.Id, mine.Revision));
        Fails(await TakeMgmt.ApproveAsync(counter.Id, Company.Id, mine.Id, sub.Revision), ConfigStatus.Forbidden, "forbidden");          // submit is not approve
        Fails(await TakeMgmt.CancelAsync(counter.Id, Company.Id, mine.Id, sub.Revision, "x"), ConfigStatus.Forbidden, "forbidden");      // nor cancel
        var w2Approver = await TakeApprover(ScopeKind.Warehouse, W2.Id);
        Fails(await TakeMgmt.ApproveAsync(w2Approver.Id, Company.Id, mine.Id, sub.Revision), ConfigStatus.Forbidden, "forbidden");       // a W2 approver cannot approve a W1 count
        // a W2-only viewer sees nothing of W1, not even that it exists; list, count and get are restricted inside the query
        var theirs = await Draft(W2);
        var (viewer2, _) = await Member([PermissionCatalog.Codes.StocktakeView], ScopeKind.Warehouse, W2.Id);
        var list = Ok(await TakeMgmt.ListAsync(viewer2.Id, Company.Id, new StocktakeFilter(null, null, null, null, null, null, 0, 50)));
        Assert.Equal((1L, theirs.Id), (list.Total, list.Items.Single().Id));
        Assert.Equal(0L, Ok(await TakeMgmt.ListAsync(viewer2.Id, Company.Id, new StocktakeFilter(null, W1.Id, null, null, null, null, 0, 50))).Total);
        Fails(await TakeMgmt.GetAsync(viewer2.Id, Company.Id, mine.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await TakeMgmt.SubmitAsync(viewer2.Id, Company.Id, mine.Id, sub.Revision), ConfigStatus.Forbidden, "forbidden");
        Fails(await TakeMgmt.GetAsync(viewer2.Id, Company.Id, Guid.NewGuid()), ConfigStatus.NotFound, "stocktake_not_found");
        var (nobody, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);                              // stock documents do not grant stocktakes
        Fails(await TakeMgmt.ListAsync(nobody.Id, Company.Id, new StocktakeFilter(null, null, null, null, null, null, 0, 50)), ConfigStatus.Forbidden, "forbidden");
        Fails(await TakeMgmt.SearchItemsAsync(nobody.Id, Company.Id, ""), ConfigStatus.Forbidden, "forbidden");
        var w1Approver = await TakeApprover(ScopeKind.Warehouse, W1.Id);
        Assert.Equal(3, Ok(await TakeMgmt.ApproveAsync(w1Approver.Id, Company.Id, mine.Id, sub.Revision)).Status);
        var admin = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!.Single(r => r.PresetKey == "company-administrator");
        Assert.All(new[] { PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeCreate, PermissionCatalog.Codes.StocktakeUpdate, PermissionCatalog.Codes.StocktakeSubmit,
            PermissionCatalog.Codes.StocktakeApprove, PermissionCatalog.Codes.StocktakeCancel }, c => Assert.Contains(c, admin.Permissions));
    }

    [PostgresFact]
    public async Task Lines_are_validated_against_the_real_items_units_and_warehouse_and_the_warehouse_never_changes()
    {
        await PrepareStocktake();
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1, CountDate, C(Service, Pcs, 1m))), "lines[0].itemId", "item_not_stockable");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1, CountDate, C(Lot, Pcs, 1m))), "lines[0].itemId", "tracked_item_not_supported_yet");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1, CountDate, C(Fifo, Pcs, 1m), C(Fifo, Pcs, 2m))), "lines[1].itemId", "duplicate_item");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1, CountDate, C(Fifo, Box, 1m))), "lines[0].unitId", "unit_not_defined_for_item");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1, CountDate, C(Fifo, Pcs, -1m))), "lines[0].countedQuantity", "invalid_quantity");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1, CountDate, new StocktakeLineInput(Guid.NewGuid(), Pcs.Id, 1m, null))), "lines[0].itemId", "item_not_found");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput(Guid.NewGuid(), CountDate, null, null, null)), "warehouseId", "warehouse_not_found");
        Invalid(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput(W1.Id, "11/03/2026", null, null, null)), "countDate", "invalid_date");
        var d = await Draft(W1, C(Fifo, Pcs, 1m));
        Invalid(await Takes.UpdateAsync(Admin.Id, Company.Id, d.Id, d.Revision, Head(W2, CountDate, C(Fifo, Pcs, 1m))), "warehouseId", "immutable");
        Invalid(await Takes.GenerateSheetAsync(Admin.Id, Company.Id, d.Id, d.Revision, new StocktakeSheetInput([Service.Id])), "itemIds", "item_not_countable");
        Fails(await Takes.UpdateAsync(Admin.Id, Company.Id, d.Id, null, Head(W1)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Takes.UpdateAsync(Admin.Id, Company.Id, d.Id, d.Revision + 7, Head(W1)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(1L, await Count(ST.Table));                                                                               // nothing of the refused requests was written
        Ok(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, W2.Id, 1));
        Fails(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W2)), ConfigStatus.Conflict, "warehouse_inactive");
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_the_life_cycle_immutability_exact_variance_separation_of_duties_and_append_only_history()
    {
        await PrepareStocktake();
        var draft = await Draft(W1, C(Fifo, Pcs, 2m, "x"));
        var sub = await SubmittedTake();
        var approved = Ok(await Takes.ApproveAsync((await TakeApprover()).Id, Company.Id, sub.Id, sub.Revision));
        // never deleted or truncated; history append-only
        Assert.NotNull(await Raw($"DELETE FROM {ST.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {ST.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {STL.Table}"));
        Assert.NotNull(await Raw($"DELETE FROM {STE.Table}"));
        Assert.NotNull(await Raw($"UPDATE {STE.Table} SET {STE.Note} = 'x'"));
        Assert.NotNull(await Raw($"TRUNCATE {STE.Table}"));
        // immutable warehouse; revision +1; number never changes; header and lines frozen outside draft
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.WarehouseId} = '{W2.Id}', {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Notes} = 'x', {ST.Revision} = {ST.Revision} + 3 WHERE {ST.Id} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.CountDate} = '2026-03-01', {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Number} = 'STK-HACK', {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {STL.Table} SET {STL.CountedQuantity} = 99, {STL.CountedBaseQuantity} = 99, {STL.Variance} = 99 - {STL.BookQuantity} WHERE {STL.StocktakeId} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"DELETE FROM {STL.Table} WHERE {STL.StocktakeId} = '{approved.Id}'"));
        // closed graph: no jump from draft to approved, no way back from approved
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 3, {ST.Number} = 'X', {ST.FiscalYearId} = (SELECT {CompanySchema.FiscalYear.Id} FROM {CompanySchema.FiscalYear.Table} LIMIT 1), {ST.DecidedBy} = gen_random_uuid(), {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 1, {ST.Number} = NULL, {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{approved.Id}'"));
        // line arithmetic: counted >= 0, base = counted x factor, variance = base - book, exactly
        Assert.NotNull(await Raw($"UPDATE {STL.Table} SET {STL.CountedQuantity} = -1, {STL.CountedBaseQuantity} = -1, {STL.Variance} = -1 - {STL.BookQuantity} WHERE {STL.StocktakeId} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {STL.Table} SET {STL.CountedBaseQuantity} = {STL.CountedBaseQuantity} + 1 WHERE {STL.StocktakeId} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {STL.Table} SET {STL.Variance} = {STL.Variance} + 0.0000000001 WHERE {STL.StocktakeId} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {STL.Table} SET {STL.Variance} = NULL WHERE {STL.StocktakeId} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"INSERT INTO {STL.Table} ({STL.StocktakeId}, {STL.LineNo}, {STL.ItemId}, {STL.UnitId}, {STL.Factor}, {STL.BookQuantity}) VALUES ('{draft.Id}', 9, '{Fifo.Id}', '{Pcs.Id}', 1, 0)"));   // an item appears once
        // separation of duties against direct SQL, and a submitted sheet must be complete (judged at commit)
        var s2 = await SubmittedTake();
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 4, {ST.DecidedBy} = {ST.SubmittedBy}, {ST.DecidedAtUtc} = now(), {ST.DecisionNote} = 'x', {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{s2.Id}'"));
        var open = await Draft(W1, C(Fifo, Pcs, null));
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 2, {ST.SubmittedBy} = gen_random_uuid(), {ST.SubmittedAtUtc} = now(), {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{open.Id}'"));   // uncounted
        var unexplained = await Draft(W1, C(Fifo, Pcs, 4m));
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 2, {ST.SubmittedBy} = gen_random_uuid(), {ST.SubmittedAtUtc} = now(), {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{unexplained.Id}'"));   // variance without reason
        var empty = await Draft(W1);
        Assert.NotNull(await Raw($"UPDATE {ST.Table} SET {ST.Status} = 2, {ST.SubmittedBy} = gen_random_uuid(), {ST.SubmittedAtUtc} = now(), {ST.Revision} = {ST.Revision} + 1 WHERE {ST.Id} = '{empty.Id}'"));   // no line
        Assert.Equal(1, (await Takes.GetAsync(Company.Id, draft.Id))!.Status);
        Assert.Equal(2, (await Takes.GetAsync(Company.Id, s2.Id))!.Status);
        Assert.Equal(3, (await Takes.GetAsync(Company.Id, approved.Id))!.Status);
    }
}

/// <summary>End-to-end HTTP tests of /api/v1/company/stocktakes (Web and Windows Desktop use exactly these endpoints).</summary>
public class StocktakeApiTests : StocktakeTestBase
{
    private const string Base = "/api/v1/company/stocktakes";

    [PostgresFact]
    public async Task A_stocktake_goes_from_sheet_generation_to_approval_over_http_with_etags_and_separation_of_duties()
    {
        await StartApi();
        await PrepareStocktake();
        await Receive(Apple, 30m, 300m);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"{Base}/{Guid.NewGuid()}/approve", new { })).Status);
        var maker = await SignIn(Admin);
        var approver = await SignIn(await TakeApprover());
        var created = await Send(HttpMethod.Post, Base, new { warehouseId = W1.Id, countDate = CountDate, reference = "R1" }, maker);
        Assert.Equal((HttpStatusCode.Created, "\"1\""), (created.Status, Etag(created)));
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, $"{Base}/{id}/generate-sheet", new { }, maker)).Status);
        var sheet = await Send(HttpMethod.Post, $"{Base}/{id}/generate-sheet", new { }, maker, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.OK, "\"2\""), (sheet.Status, Etag(sheet)));
        Assert.Equal(30m, sheet.Json.GetProperty("lines")[0].GetProperty("bookQuantity").GetDecimal());
        var counted = await Send(HttpMethod.Put, $"{Base}/{id}", new { warehouseId = W1.Id, countDate = CountDate, reference = "R1", lines = new[] { new { itemId = Apple.Id, unitId = Box.Id, countedQuantity = 2.5m, reason = "short" } } }, maker, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, counted.Status);
        Assert.Equal((30m, 0m), (counted.Json.GetProperty("lines")[0].GetProperty("countedBaseQuantity").GetDecimal(), counted.Json.GetProperty("lines")[0].GetProperty("variance").GetDecimal()));   // 2.5 boxes x 12
        var bad = await Send(HttpMethod.Put, $"{Base}/{id}", new { warehouseId = W1.Id, countDate = CountDate, lines = new[] { new { itemId = Apple.Id, unitId = Box.Id, countedQuantity = -1m } } }, maker, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, maker, ifMatch: "\"2\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, maker, ifMatch: "\"3\"")).Status);
        var own = await Send(HttpMethod.Post, $"{Base}/{id}/approve", null, maker, ifMatch: "\"4\"");
        Assert.Equal((HttpStatusCode.Conflict, "self_approval_not_allowed"), (own.Status, own.Error));
        var approved = await Send(HttpMethod.Post, $"{Base}/{id}/approve", null, approver, ifMatch: "\"4\"");
        Assert.Equal((HttpStatusCode.OK, "STK-2026-000001", false), (approved.Status, approved.Json.GetProperty("number").GetString(), approved.Json.GetProperty("stale").GetBoolean()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/{id}/approve", null, approver, ifMatch: "\"4\"")).Status);   // a repeated request does nothing twice
        var list = await Send(HttpMethod.Get, $"{Base}?status=3&search=stk-2026", cookie: maker);
        Assert.Equal((1, 1), (list.Json.GetProperty("total").GetInt32(), list.Json.GetProperty("stocktakes")[0].GetProperty("lineCount").GetInt32()));
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        var caps = await Send(HttpMethod.Get, $"{Base}/capabilities", cookie: maker);
        Assert.Equal(2, caps.Json.GetProperty("approve").GetArrayLength());
        var (outsider, _) = await Member([PermissionCatalog.Codes.StocktakeView], ScopeKind.Warehouse, W2.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}", cookie: await SignIn(outsider))).Status);
    }
}
