using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using SL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentLine;
using SE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentEvent;
using NC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.5 stock movement documents against real PostgreSQL (company migration 0013, system migration 0016): receipt, issue and transfer from draft to approval with numbering, warehouse-scoped authorization,
/// separation of duties, database-enforced life cycle and immutability, concurrency and audit. These documents change NO stock figure; no quantity, ledger, cost or accounting record exists. Disposable cluster only.
/// </summary>
public abstract class StockTestBase : WarehouseAccessTestBase
{
    protected CompanyInventoryService Inv { get; private set; } = null!;
    protected StockDocumentService Docs { get; private set; } = null!;
    protected StockDocumentManagementService DocMgmt { get; private set; } = null!;
    protected FiscalCalendarService Calendar { get; private set; } = null!;
    protected WarehouseView W1 = null!, W2 = null!;
    protected UnitView Pcs = null!, Box = null!, Kg = null!;
    protected ItemView Apple = null!, Pear = null!, Service = null!, Lot = null!;

    protected async Task Prepare(bool year = true, int maxWarehouses = 50)
    {
        await PrepareWarehouses(maxWarehouses);
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Inv = new CompanyInventoryService(Resolver, tx, Clock);
        Docs = new StockDocumentService(Resolver, tx, Clock);
        DocMgmt = new StockDocumentManagementService(Rbac, Docs);
        Calendar = new FiscalCalendarService(Resolver, tx, new PeriodCloseGuard(), new FinancialHistoryGuard(), Clock);
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        if (year) Ok(await Calendar.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        W1 = await NewWarehouse("W1");
        W2 = maxWarehouses > 1 ? await NewWarehouse("W2") : null!;
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(2)));
        Pcs = Ok(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("PCS", "قطعة", "Piece", 0)));
        Box = Ok(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("BOX", "علبة", "Box", 2)));
        Kg = Ok(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("KG", "كيلو", "Kilogram", 3)));
        var group = Ok(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("G", "مجموعة", "Group", null, null)));
        ItemView Make(string code, int type, UnitView unit, bool lot = false, params (UnitView U, decimal F)[] alt) =>
            Ok(Inv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput(code, null, code, type, group.Id, unit.Id, null, null, lot, false, false, null, [.. alt.Select(a => new ItemUnitInput(a.U.Id, a.F))])).GetAwaiter().GetResult());
        Apple = Make("APPLE", 1, Pcs, false, (Box, 12m));
        Pear = Make("PEAR", 1, Kg);
        Service = Make("SERVICE", 2, Pcs);
        Lot = Make("LOT", 1, Pcs, true);
    }

    protected static T Ok<T>(ConfigResult<T> r) { Assert.True(r.IsOk, $"{r.Status} {r.Code} {string.Join(",", r.Fields?.Select(f => f.Field + ":" + f.Code) ?? [])}"); return r.Value!; }
    protected static void Fails<T>(ConfigResult<T> r, ConfigStatus status, string code) => Assert.Equal((status, code), (r.Status, r.Code));
    protected static void Invalid<T>(ConfigResult<T> r, string field, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, f => f.Field == field && f.Code == code); }

    protected StockLineInput L(ItemView item, UnitView? unit = null, decimal q = 1m, string? notes = null) => new(item.Id, (unit ?? Pcs).Id, q, notes);
    protected StockDocumentInput Receipt(WarehouseView? to = null, string date = "2026-03-10", params StockLineInput[] lines) => new(1, null, (to ?? W1).Id, date, "REF-1", "note", lines.Length == 0 ? [L(Apple)] : lines);
    protected StockDocumentInput Issue(WarehouseView? from = null, string date = "2026-03-10", params StockLineInput[] lines) => new(2, (from ?? W1).Id, null, date, null, null, lines.Length == 0 ? [L(Apple)] : lines);
    protected StockDocumentInput Transfer(WarehouseView? from = null, WarehouseView? to = null, string date = "2026-03-10", params StockLineInput[] lines) => new(3, (from ?? W1).Id, (to ?? W2).Id, date, null, null, lines.Length == 0 ? [L(Apple)] : lines);

    protected async Task<StockDocumentView> Draft(StockDocumentInput? input = null) => Ok(await Docs.CreateAsync(Admin.Id, Company.Id, input ?? Receipt()));
    protected async Task<(UserRecord User, StockDocumentView Doc)> Submitted(UserRecord? by = null, StockDocumentInput? input = null)
    {
        var d = await Draft(input);
        var u = by ?? Admin;
        return (u, Ok(await Docs.SubmitAsync(u.Id, Company.Id, d.Id, d.Revision)));
    }
    protected async Task<UserRecord> Approver() => (await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsApprove], ScopeKind.Company, null)).User;
    protected Task<long> Rows(string table) => Sql<long>($"SELECT count(*) FROM {table}");
    protected Task<string?> Raw(params string[] sql) => InTx(sql);
    protected Task<long> StockEvents() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1140 AND 1146");
}

public class StockDocumentTests : StockTestBase
{
    // ---------------------------------------------------------------------------------------------------------------- warehouses

    [PostgresFact]
    public async Task The_trial_limit_of_one_warehouse_is_respected_and_documents_work_with_it()
    {
        await Prepare(maxWarehouses: 1);
        Assert.Equal(ConfigStatus.EntitlementDenied, (await WhDomain.CreateAsync(Admin.Id, Company.Id, Input("W9", (await Main()).Id))).Status);
        var d = await Draft(Receipt());
        Assert.Equal((1, 1), (d.DocType, d.Status));
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, new StockDocumentInput(3, W1.Id, W1.Id, "2026-03-01", null, null, [L(Apple)])), "targetWarehouseId", "same_warehouse");   // a transfer needs two warehouses
    }

    [PostgresFact]
    public async Task A_warehouse_with_open_documents_cannot_be_deactivated_until_they_are_finished_and_a_document_cannot_use_an_inactive_warehouse()
    {
        await Prepare();
        var d = await Draft(Receipt(W2));
        Fails(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, W2.Id, 1), ConfigStatus.Conflict, "warehouse_has_open_documents");
        Ok(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, W1.Id, 1));                                                              // W1 has no documents
        Fails(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1)), ConfigStatus.Conflict, "warehouse_inactive");
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, d.Revision, "not needed"));
        Ok(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, W2.Id, 1));                                                              // a cancelled document no longer blocks
    }

    [PostgresFact]
    public async Task Documents_cannot_reference_warehouses_items_or_units_of_another_company_or_unknown_ids()
    {
        await Prepare();
        var other = await ReadyCompany();
        var foreignWh = Ok(await new CompanyWarehouseService(SinglePrincipalResolver(other), new CompanyAuditedTransactions(SinglePrincipalResolver(other), Audit, Clock), new WarehouseLimitPolicy(5), Clock)
            .CreateAsync(Admin.Id, other.Id, Input("FW", (await Branches(company: other).ListAsync(other.Id)).Single().Id)));
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, new StockDocumentInput(1, null, foreignWh.Id, "2026-03-01", null, null, [L(Apple)])), "targetWarehouseId", "warehouse_not_found");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, new StockDocumentInput(1, null, W1.Id, "2026-03-01", null, null, [new StockLineInput(Guid.NewGuid(), Pcs.Id, 1m, null)])), "lines[0].itemId", "item_not_found");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, new StockDocumentInput(1, null, W1.Id, "2026-03-01", null, null, [new StockLineInput(Apple.Id, Guid.NewGuid(), 1m, null)])), "lines[0].unitId", "unit_not_found");
        Assert.Equal(0L, await Rows(SD.Table));
        Assert.Equal(0L, await InCompany<long>(other.PhysicalDatabaseName, $"SELECT count(*) FROM {SD.Table}"));
    }

    // ---------------------------------------------------------------------------------------------------------------- lifecycle and numbering

    [PostgresFact]
    public async Task A_document_goes_from_draft_to_approved_with_an_official_number_history_and_audit_and_changes_no_stock()
    {
        await Prepare();
        var d = await Draft(Receipt(W1, "2026-03-10", L(Apple, Box, 2m), L(Pear, Kg, 1.5m)));
        Assert.Equal(("", 1, 1L), (d.Number ?? "", d.Status, d.Revision));
        Assert.Equal([(1, 2m, 12m, 24m, "PCS"), (2, 1.5m, 1m, 1.5m, "KG")], d.Lines.Select(l => (l.LineNo, l.Quantity, l.Factor, l.BaseQuantity, l.BaseUnitCode)));   // exact conversion into the base unit
        var events0 = await StockEvents();
        var edited = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, d.Revision, Receipt(W1, "2026-03-11", L(Apple, Pcs, 5m))));
        Assert.Equal((2L, "2026-03-11", 1), (edited.Revision, edited.DocumentDate, edited.Lines.Count));
        var sub = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, edited.Revision));
        Assert.Equal((2, Admin.Id), (sub.Status, sub.SubmittedBy));
        var approver = await Approver();
        var ok = Ok(await Docs.ApproveAsync(approver.Id, Company.Id, d.Id, sub.Revision));
        Assert.Equal((3, "SR-2026-000001", approver.Id), (ok.Status, ok.Number, ok.DecidedBy));
        Assert.NotNull(ok.FiscalYearId);
        Assert.Equal([1, 2, 3, 5], ok.Events.Select(e => e.Kind));                                                                       // created, updated, submitted, approved
        Assert.Equal(events0 + 3, await StockEvents());                                                                                      // created before the baseline, then updated, submitted, approved
        Assert.Equal(4L, await StockEvents());
        Assert.Equal(4L, await Rows(SE.Table));
        // no stock effect of any kind: nothing but the document tables changed, and the ledger and item data are untouched
        Assert.Equal(0L, await Sql<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND (table_name ILIKE '%onhand%' OR table_name ILIKE '%ledger%' OR table_name ILIKE '%layer%' OR table_name ILIKE '%balance%')"));
        Assert.Equal(0L, await Rows(CompanySchema.JournalEntry.Table));
    }

    [PostgresFact]
    public async Task Numbers_are_per_document_type_gapless_never_reused_and_a_cancelled_approved_number_is_void()
    {
        await Prepare();
        var approver = await Approver();
        async Task<StockDocumentView> Approved(StockDocumentInput input)
        {
            var (_, s) = await Submitted(Admin, input);
            return Ok(await Docs.ApproveAsync(approver.Id, Company.Id, s.Id, s.Revision));
        }
        var r1 = await Approved(Receipt()); var r2 = await Approved(Receipt()); var i1 = await Approved(Issue()); var t1 = await Approved(Transfer());
        Assert.Equal(["SR-2026-000001", "SR-2026-000002", "SI-2026-000001", "ST-2026-000001"], [r1.Number!, r2.Number!, i1.Number!, t1.Number!]);
        var cancelled = Ok(await Docs.CancelAsync(Admin.Id, Company.Id, r2.Id, r2.Revision, "entered twice"));
        Assert.Equal(("SR-2026-000002", 5), (cancelled.Number, cancelled.Status));                                                      // the number stays on the cancelled document
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stock_receipt' AND {NI.Number} = 'SR-2026-000002' AND {NI.VoidedAtUtc} IS NOT NULL"));
        var r3 = await Approved(Receipt());
        Assert.Equal("SR-2026-000003", r3.Number);                                                                                      // the voided number is never issued again
        var rejected = await Submitted(Admin, Receipt());
        Ok(await Docs.RejectAsync(approver.Id, Company.Id, rejected.Doc.Id, rejected.Doc.Revision, "wrong quantities"));
        Assert.Equal("SR-2026-000004", (await Approved(Receipt())).Number);                                                             // a rejected document consumed no number
    }

    [PostgresFact]
    public async Task Every_status_transition_is_validated_withdraw_and_reject_work_and_final_states_stay_final()
    {
        await Prepare();
        var approver = await Approver();
        var d = await Draft();
        Fails(await Docs.ApproveAsync(approver.Id, Company.Id, d.Id, d.Revision), ConfigStatus.Conflict, "invalid_status");           // a draft cannot be approved
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        Fails(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, s.Revision), ConfigStatus.Conflict, "invalid_status");
        Fails(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, s.Revision, Receipt()), ConfigStatus.Conflict, "invalid_status");      // a submitted document cannot be edited
        var back = Ok(await Docs.WithdrawAsync(Admin.Id, Company.Id, d.Id, s.Revision));
        Assert.Equal((1, (Guid?)null), (back.Status, back.SubmittedBy));
        Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, back.Revision, Receipt(W1, "2026-03-12")));
        var again = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, back.Revision + 1));
        Invalid(await Docs.RejectAsync(approver.Id, Company.Id, d.Id, again.Revision, " "), "note", "required");
        var rej = Ok(await Docs.RejectAsync(approver.Id, Company.Id, d.Id, again.Revision, "no"));
        Assert.Equal((4, "no", approver.Id), (rej.Status, rej.DecisionNote, rej.DecidedBy));
        foreach (var attempt in new[] { Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, rej.Revision), Docs.ApproveAsync(approver.Id, Company.Id, d.Id, rej.Revision), Docs.CancelAsync(Admin.Id, Company.Id, d.Id, rej.Revision, "x"), Docs.WithdrawAsync(Admin.Id, Company.Id, d.Id, rej.Revision) })
            Fails(await attempt, ConfigStatus.Conflict, "invalid_status");                                                               // a rejected document is final
        Invalid(await Docs.CancelAsync(Admin.Id, Company.Id, (await Draft()).Id, 1, null), "reason", "required");
        Fails(await Docs.CancelAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, "x"), ConfigStatus.NotFound, "document_not_found");
        Fails(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, null), ConfigStatus.PreconditionRequired, "precondition_required");
    }

    [PostgresFact]
    public async Task The_submitter_can_never_approve_or_reject_their_own_document_and_PostgreSQL_enforces_it_too()
    {
        await Prepare();
        var (_, s) = await Submitted();
        Fails(await Docs.ApproveAsync(Admin.Id, Company.Id, s.Id, s.Revision), ConfigStatus.Conflict, "self_approval_not_allowed");
        Fails(await Docs.RejectAsync(Admin.Id, Company.Id, s.Id, s.Revision, "no"), ConfigStatus.Conflict, "self_approval_not_allowed");
        // direct SQL cannot bypass it: the same person as decider is refused by the table itself
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Status} = 4, {SD.DecidedBy} = {SD.SubmittedBy}, {SD.DecidedAtUtc} = now(), {SD.DecisionNote} = 'x', {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{s.Id}'"));
        Assert.Equal(2, (await Docs.GetAsync(Company.Id, s.Id))!.Status);
    }

    // ---------------------------------------------------------------------------------------------------------------- items, units and conversion

    [PostgresFact]
    public async Task Lines_are_checked_against_the_real_items_units_and_tracking_and_conversions_are_exact()
    {
        await Prepare();
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Service))), "lines[0].itemId", "item_not_stockable");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Lot))), "lines[0].itemId", "tracked_item_not_supported_yet");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Pear, Box))), "lines[0].unitId", "unit_not_defined_for_item");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Apple, Box, 0.3m))), "lines[0].quantity", "base_quantity_precision_exceeded");     // 0.3 box = 3.6 pieces
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Apple, Pcs, 1.5m))), "lines[0].quantity", "quantity_precision_exceeded");           // pieces have no decimals
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Pear, Kg, 1.2345m))), "lines[0].quantity", "quantity_precision_exceeded");
        Assert.Equal(3m, Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Apple, Box, 0.25m)))).Lines[0].BaseQuantity);                         // a quarter box of 12 = 3 pieces
        Assert.Equal(0.003m, Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Pear, Kg, 0.003m)))).Lines[0].BaseQuantity);
        var off = Ok(await Inv.SetUnitActiveAsync(Admin.Id, Company.Id, Box.Id, 1, false));
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Apple, Box, 1m))), "lines[0].unitId", "unit_inactive");
        Assert.NotNull(off);
        // a draft saved earlier is checked again when it is submitted: the item may have been deactivated meanwhile
        var d = await Draft(Receipt(W1, "2026-03-10", L(Pear, Kg, 2m)));
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, Pear.Id, 1, false));
        Invalid(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision), "lines[0].itemId", "item_inactive");
        Assert.Equal(1, (await Docs.GetAsync(Company.Id, d.Id))!.Status);
    }

    [PostgresFact]
    public async Task A_transfer_between_branches_is_not_supported_yet_and_a_transfer_within_a_branch_is()
    {
        await Prepare();
        Assert.Equal(2, (await Draft(Transfer())).Lines.Count + 1);
        var b2 = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{b2}', 'B2', 'Second', true, false, 1, 1, now(), now())");
        var w3 = await NewWarehouse("W3", b2);
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Transfer(W1, w3)), "targetWarehouseId", "transfer_cross_branch_not_supported");
        Assert.Equal(b2, (await Draft(Receipt(w3))).BranchId);                                                                          // a receipt takes the branch of its warehouse
    }

    // ---------------------------------------------------------------------------------------------------------------- approval conditions and rollback

    [PostgresFact]
    public async Task Approval_needs_an_open_fiscal_period_and_a_failed_approval_leaves_no_number_and_no_change()
    {
        await Prepare();
        var approver = await Approver();
        var (_, outside) = await Submitted(Admin, Receipt(W1, "2027-02-01"));                                                           // no fiscal year covers 2027
        var counters = await Rows(NC.Table);
        var r = await Docs.ApproveAsync(approver.Id, Company.Id, outside.Id, outside.Revision);
        Assert.False(r.IsOk);
        Assert.Equal(ConfigStatus.Conflict, r.Status);
        var after = (await Docs.GetAsync(Company.Id, outside.Id))!;
        Assert.Equal((2, (string?)null, outside.Revision), (after.Status, after.Number, after.Revision));                              // still submitted, untouched
        Assert.Equal((counters, 0L), (await Rows(NC.Table), await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} LIKE 'stock_%'")));   // nothing was issued
        Assert.Equal(2L, await Rows(SE.Table));                                                                                          // only created and submitted
        var (_, soft) = await Submitted(Admin, Receipt(W1, "2026-04-05"));
        var p = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(x => x.StartDate == new DateOnly(2026, 4, 1));
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, p.Id, p.Revision));
        Assert.False((await Docs.ApproveAsync(approver.Id, Company.Id, soft.Id, soft.Revision)).IsOk);                                  // a period that is not open refuses it
    }

    // ---------------------------------------------------------------------------------------------------------------- database guards

    [PostgresFact]
    public async Task PostgreSQL_protects_documents_lines_and_history_even_from_direct_sql()
    {
        await Prepare();
        var approver = await Approver();
        var draft = await Draft(Receipt(W1, "2026-03-10", L(Apple, Pcs, 4m)));
        var (_, s) = await Submitted(Admin, Receipt());
        var approved = Ok(await Docs.ApproveAsync(approver.Id, Company.Id, s.Id, s.Revision));
        // never deleted or truncated
        Assert.NotNull(await Raw($"DELETE FROM {SD.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {SD.Table}"));
        Assert.NotNull(await Raw($"DELETE FROM {SE.Table}"));
        Assert.NotNull(await Raw($"UPDATE {SE.Table} SET {SE.Note} = 'x'"));
        Assert.NotNull(await Raw($"TRUNCATE {SE.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {SL.Table}"));
        // immutable identity, type, branch; revision +1; number never changes; header and lines frozen outside draft
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.DocType} = 2, {SD.SourceWarehouseId} = {SD.TargetWarehouseId}, {SD.TargetWarehouseId} = NULL, {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Notes} = 'x', {SD.Revision} = {SD.Revision} + 5 WHERE {SD.Id} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Notes} = 'edited after approval', {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Number} = 'SR-HACK', {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {SL.Table} SET {SL.Quantity} = 99, {SL.BaseQuantity} = 99 WHERE {SL.DocumentId} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"DELETE FROM {SL.Table} WHERE {SL.DocumentId} = '{approved.Id}'"));
        Assert.NotNull(await Raw($"INSERT INTO {SL.Table} ({SL.DocumentId}, {SL.LineNo}, {SL.ItemId}, {SL.UnitId}, {SL.Quantity}, {SL.Factor}, {SL.BaseQuantity}) VALUES ('{approved.Id}', 9, '{Apple.Id}', '{Pcs.Id}', 1, 1, 1)"));
        // closed life-cycle graph: no jump from draft to approved, no way back from approved to draft
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Status} = 3, {SD.Number} = 'X', {SD.FiscalYearId} = (SELECT {CompanySchema.FiscalYear.Id} FROM {CompanySchema.FiscalYear.Table} LIMIT 1), {SD.DecidedBy} = gen_random_uuid(), {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Status} = 1, {SD.Number} = NULL, {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{approved.Id}'"));
        // line and header integrity
        Assert.NotNull(await Raw($"UPDATE {SL.Table} SET {SL.Quantity} = 0, {SL.BaseQuantity} = 0 WHERE {SL.DocumentId} = '{draft.Id}'"));
        Assert.NotNull(await Raw($"UPDATE {SL.Table} SET {SL.BaseQuantity} = {SL.BaseQuantity} + 1 WHERE {SL.DocumentId} = '{draft.Id}'"));                      // base must equal quantity x factor
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.SourceWarehouseId} = {SD.TargetWarehouseId}, {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{draft.Id}'"));   // a receipt has no source
        // a document cannot leave draft without lines
        var empty = await Draft(Receipt(W1, "2026-03-10", L(Apple, Pcs, 1m)));
        Assert.NotNull(await Raw($"DELETE FROM {SL.Table} WHERE {SL.DocumentId} = '{empty.Id}'", $"UPDATE {SD.Table} SET {SD.Status} = 2, {SD.SubmittedBy} = gen_random_uuid(), {SD.SubmittedAtUtc} = now(), {SD.Revision} = {SD.Revision} + 1 WHERE {SD.Id} = '{empty.Id}'"));
        // the number is unique per type
        Assert.NotNull(await Raw($"UPDATE {SD.Table} SET {SD.Number} = '{approved.Number}' WHERE {SD.Id} = '{draft.Id}'"));
        Assert.Equal(1, (await Docs.GetAsync(Company.Id, draft.Id))!.Status);
    }

    // ---------------------------------------------------------------------------------------------------------------- authorization

    [PostgresFact]
    public async Task Permissions_are_warehouse_scoped_a_transfer_needs_both_warehouses_and_documents_of_other_warehouses_are_not_disclosed()
    {
        await Prepare();
        var w3 = await NewWarehouse("W3");
        var (creator1, role) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsCreate, PermissionCatalog.Codes.StockDocsUpdate, PermissionCatalog.Codes.StockDocsSubmit], ScopeKind.Warehouse, W1.Id);
        Ok(await DocMgmt.CreateAsync(creator1.Id, Company.Id, Receipt(W1)));
        Fails(await DocMgmt.CreateAsync(creator1.Id, Company.Id, Receipt(W2)), ConfigStatus.Forbidden, "forbidden");                      // a W1 grant does not reach W2
        Fails(await DocMgmt.CreateAsync(creator1.Id, Company.Id, Transfer(W1, W2)), ConfigStatus.Forbidden, "forbidden");               // a transfer needs the permission on BOTH
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, creator1.Id, role, ScopeKind.Warehouse, W2.Id)).IsOk);
        Ok(await DocMgmt.CreateAsync(creator1.Id, Company.Id, Transfer(W1, W2)));
        // visibility: a W3-only viewer sees nothing of W1/W2 documents, not even that they exist
        var (viewer3, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Warehouse, w3.Id);
        var own = await Draft(Receipt(w3));
        var list = Ok(await DocMgmt.ListAsync(viewer3.Id, Company.Id, new StockDocumentFilter(null, null, null, null, null, null, null, 0, 50)));
        Assert.Equal([own.Id], list.Items.Select(i => i.Id));
        var theirs = (await Docs.ListAsync(Company.Id, new StockDocumentFilter(null, null, W1.Id, null, null, null, null, 0, 50))).Items.First();
        Fails(await DocMgmt.GetAsync(viewer3.Id, Company.Id, theirs.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.SubmitAsync(viewer3.Id, Company.Id, theirs.Id, theirs.Revision), ConfigStatus.Forbidden, "forbidden");        // no existence probing by action either
        Fails(await DocMgmt.GetAsync(viewer3.Id, Company.Id, Guid.NewGuid()), ConfigStatus.NotFound, "document_not_found");
        var (nobody, _) = await Member([PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Fails(await DocMgmt.ListAsync(nobody.Id, Company.Id, new StockDocumentFilter(null, null, null, null, null, null, null, 0, 50)), ConfigStatus.Forbidden, "forbidden");
        // a company-wide grant covers every warehouse; capabilities report exactly that
        var caps = await DocMgmt.CapabilitiesAsync(Admin.Id, Company.Id);
        Assert.Equal(3, caps.Approve.Count);
        var c1 = await DocMgmt.CapabilitiesAsync(creator1.Id, Company.Id);
        Assert.Equal(new[] { W1.Id, W2.Id }.Order(), c1.Create.Order());
        Assert.Empty(c1.Approve);
    }

    [PostgresFact]
    public async Task Create_update_submit_approve_and_cancel_are_separate_permissions_and_a_warehouse_deactivation_ends_its_scoped_access()
    {
        await Prepare();
        var (maker, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsCreate], ScopeKind.Warehouse, W1.Id);
        var d = Ok(await DocMgmt.CreateAsync(maker.Id, Company.Id, Receipt(W1)));
        Fails(await DocMgmt.UpdateAsync(maker.Id, Company.Id, d.Id, d.Revision, Receipt(W1, "2026-03-11")), ConfigStatus.Forbidden, "forbidden");     // create is not update
        Fails(await DocMgmt.SubmitAsync(maker.Id, Company.Id, d.Id, d.Revision), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.CancelAsync(maker.Id, Company.Id, d.Id, d.Revision, "x"), ConfigStatus.Forbidden, "forbidden");
        var s = Ok(await DocMgmt.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        var (approver1, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsApprove], ScopeKind.Warehouse, W1.Id);
        Fails(await DocMgmt.CancelAsync(approver1.Id, Company.Id, d.Id, s.Revision, "x"), ConfigStatus.Forbidden, "forbidden");           // approve is not cancel
        Assert.Equal(3, Ok(await DocMgmt.ApproveAsync(approver1.Id, Company.Id, d.Id, s.Revision)).Status);
        var (other2, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsApprove], ScopeKind.Warehouse, W2.Id);
        var second = await Submitted(Admin, Receipt(W1));
        Fails(await DocMgmt.ApproveAsync(other2.Id, Company.Id, second.Doc.Id, second.Doc.Revision), ConfigStatus.Forbidden, "forbidden");  // a W2 approver cannot approve a W1 document
    }

    // ---------------------------------------------------------------------------------------------------------------- concurrency and duplicates

    [PostgresFact]
    public async Task Concurrent_approvals_of_one_document_issue_exactly_one_number_and_repeated_requests_do_nothing_twice()
    {
        await Prepare();
        var (_, s) = await Submitted();
        var approvers = new List<UserRecord>();
        for (var i = 0; i < 6; i++) approvers.Add(await Approver());
        var results = await Task.WhenAll(approvers.Select(a => Docs.ApproveAsync(a.Id, Company.Id, s.Id, s.Revision)));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));                            // the others saw a changed revision
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stock_receipt'"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SE.Table} WHERE {SE.Kind} = 5"));
        // a duplicate request with the stale revision does nothing
        var again = await Docs.ApproveAsync(approvers[0].Id, Company.Id, s.Id, s.Revision);
        Assert.Equal(ConfigStatus.PreconditionFailed, again.Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = 'stock_receipt'"));
        var d = await Draft();
        var submits = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision)));
        Assert.Equal(1, submits.Count(r => r.IsOk));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SE.Table} WHERE {SE.DocumentId} = '{d.Id}' AND {SE.Kind} = 3"));
    }

    [PostgresFact]
    public async Task Concurrent_approvals_of_different_documents_get_unique_gapless_numbers()
    {
        await Prepare();
        var approver = await Approver();
        var docs = new List<StockDocumentView>();
        for (var i = 0; i < 10; i++) docs.Add((await Submitted(Admin, Receipt(i % 2 == 0 ? W1 : W2))).Doc);
        var results = await Task.WhenAll(docs.Select(d => Docs.ApproveAsync(approver.Id, Company.Id, d.Id, d.Revision)));
        Assert.All(results, r => Assert.True(r.IsOk, r.Code));
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"SR-2026-{n:D6}"), results.Select(r => r.Value!.Number!).Order());
    }

    [PostgresFact]
    public async Task A_concurrent_warehouse_deactivation_and_document_creation_never_leave_an_open_document_on_an_inactive_warehouse()
    {
        await Prepare();
        var creates = Enumerable.Range(0, 5).Select(_ => Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W2))).ToList();
        var off = WhDomain.DeactivateAsync(Admin.Id, Company.Id, W2.Id, 1);
        await Task.WhenAll([.. creates, off]);
        var open = await Sql<long>($"SELECT count(*) FROM {SD.Table} d JOIN {CompanySchema.Warehouse.Table} w ON w.{CompanySchema.Warehouse.Id} = d.{SD.TargetWarehouseId} WHERE d.{SD.Status} IN (1, 2, 3) AND NOT w.{CompanySchema.Warehouse.IsActive}");
        Assert.Equal(0L, open);
    }

    // ---------------------------------------------------------------------------------------------------------------- audit

    [PostgresFact]
    public async Task Every_step_writes_one_authenticated_audit_event_with_its_history_row_and_the_audit_chain_stays_intact()
    {
        await Prepare();
        var before = await Rows(Audit_.Table);
        var approver = await Approver();
        var before2 = await Rows(Audit_.Table);
        var d = await Draft();
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        var a = Ok(await Docs.ApproveAsync(approver.Id, Company.Id, d.Id, s.Revision));
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, a.Revision, "cancelled after approval"));
        Assert.Equal("1140,1142,1144,1146", await Sql<string>($"SELECT string_agg({Audit_.EventType}::text, ',' ORDER BY {Audit_.Seq}) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1140 AND 1146"));
        Assert.Equal(4L, await Sql<long>($"SELECT count(*) FROM {SE.Table} e JOIN {Audit_.Table} a ON a.{Audit_.StreamKey} = e.{SE.AuditStream} AND a.{Audit_.Seq} = e.{SE.AuditSeq} WHERE e.{SE.DocumentId} = '{d.Id}'"));   // each history row points at its own audit event
        Assert.True(await Rows(Audit_.Table) >= before2 + 4);
        Assert.NotNull(before);
        var details = await Sql<string>($"SELECT string_agg({Audit_.Detail}, '|' ORDER BY {Audit_.Seq}) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1140 AND 1146");
        Assert.Contains("number=SR-2026-000001", details);
    }
}
