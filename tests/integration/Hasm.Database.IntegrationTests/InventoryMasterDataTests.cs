using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemGroup;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using IU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemUnit;
using BC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemBarcode;
using S = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventorySettings;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.4 inventory master data against real PostgreSQL (company migration 0012, system migration 0015): units, item groups, items, alternate units, barcodes and the valuation policy,
/// with database-enforced invariants, optimistic concurrency, audit and authorization. No stock movement or accounting entry exists in this phase. Disposable cluster only.
/// </summary>
public abstract class InventoryTestBase : ChartAccessTestBase
{
    protected CompanyInventoryService Inv { get; private set; } = null!;
    protected InventoryManagementService InvMgmt { get; private set; } = null!;
    protected long AuditBase;

    protected async Task PrepareInventory()
    {
        await PrepareRbac();
        Inv = new CompanyInventoryService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        InvMgmt = new InventoryManagementService(Rbac, Inv);
        AuditBase = await Count(Company, Audit_.Table);
    }

    protected static T Ok<T>(ConfigResult<T> r) { Assert.True(r.IsOk, $"{r.Status} {r.Code} {string.Join(",", r.Fields?.Select(f => f.Field + ":" + f.Code) ?? [])}"); return r.Value!; }
    protected static void Fails<T>(ConfigResult<T> r, ConfigStatus status, string code) { Assert.Equal((status, code), (r.Status, r.Code)); }
    protected static void Invalid<T>(ConfigResult<T> r, string field, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, f => f.Field == field && f.Code == code); }

    protected Task<UnitView> Unit(string code, int decimals = 3) => Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput(code, "وحدة", "Unit " + code, decimals)).ContinueWith(t => Ok(t.Result));
    protected Task<ItemGroupView> Group(string code, Guid? parent = null, int? valuation = null) => Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput(code, "مجموعة", "Group " + code, parent, valuation)).ContinueWith(t => Ok(t.Result));
    protected static ItemInput NewItem(Guid group, Guid baseUnit, string? code = null, int type = 1, params (Guid Unit, decimal Factor)[] alt) =>
        new(code, "صنف", "Item", type, group, baseUnit, null, null, false, false, false, null, [.. alt.Select(a => new ItemUnitInput(a.Unit, a.Factor))]);
    protected Task<long> EventCount(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");
    protected Task<long> InventoryAudit() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1120 AND 1139");
    protected Task<string?> Raw(params string[] sql) => InTx(sql);
}

public class InventoryMasterDataTests : InventoryTestBase
{
    [PostgresFact]
    public async Task A_new_company_has_empty_inventory_tables_and_only_the_company_administrator_holds_the_permissions()
    {
        await PrepareInventory();
        foreach (var t in new[] { U.Table, G.Table, I.Table, IU.Table, BC.Table, S.Table }) Assert.Equal(0L, await Count(Company, t));
        Assert.Empty(Ok(await InvMgmt.ListUnitsAsync(Admin.Id, Company.Id)));
        Assert.Equal(new InventorySettingsView(null, 0), Ok(await InvMgmt.GetSettingsAsync(Admin.Id, Company.Id)));
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "units", "itemgroups", "items" }) foreach (var a in new[] { "view", "create", "update", "activate" }) Assert.Contains($"company.{c}.{a}", admin.Permissions);
        foreach (var c in new[] { "company.barcodes.create", "company.barcodes.activate", "company.inventorysettings.view", "company.inventorysettings.configure" }) Assert.Contains(c, admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.unit") || p.StartsWith("company.item") || p.StartsWith("company.barcode") || p.StartsWith("company.inventory"));
    }

    // ---------------------------------------------------------------------------------------------------------------- units

    [PostgresFact]
    public async Task The_unit_and_group_search_pages_by_code_with_a_total_matches_literally_and_keeps_the_permission_of_the_unpaged_lists()
    {
        await PrepareInventory();
        foreach (var (code, name) in new[] { ("U1", "Piece 100%"), ("U2", "Box_large"), ("U3", "BoxXlarge"), ("U4", "Dozen"), ("U5", "Gross") })
            Ok(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput(code, "وحدة", name, 0)));
        static string[] Codes(UnitPage p) => [.. p.Items.Select(u => u.Code)];
        UnitPage Units(string? q, int? offset = null, int? limit = null) => Ok(InvMgmt.SearchUnitsAsync(Admin.Id, Company.Id, q, offset, limit).Result);

        Assert.Equal(["U2", "U3"], Codes(Units("box")));
        Assert.Equal(2L, Units("BOX").Total);                                      // either case, name or code
        Assert.Equal(["U4"], Codes(Units("u4")));
        Assert.Equal(["U1"], Codes(Units("%")));                                   // the wildcards are literal
        Assert.Equal(["U2"], Codes(Units("_")));
        Assert.Equal(["U2"], Codes(Units("box_")));
        Assert.Empty(Units("x'; DROP TABLE t; --").Items);
        Assert.Equal(["U1", "U2"], Codes(Units(null, 0, 2)));
        Assert.Equal(5L, Units(null, 0, 2).Total);
        Assert.Equal(["U5"], Codes(Units(null, 4, 2)));
        Assert.Empty(Units(null, 99, 5).Items);
        Assert.Equal(5L, Units(null, 99, 5).Total);                                // an offset beyond the last row still reports the total
        Assert.Single(Units(null, 0, 0).Items);                                    // a limit below 1 is clamped to 1
        Assert.Equal(5, Units(null, 0, 100000).Items.Count);
        Assert.Equal(5, Units(null).Items.Count);                                  // default page 50
        Assert.Equal(Ok(await InvMgmt.ListUnitsAsync(Admin.Id, Company.Id)).Select(u => u.Code), Enumerable.Range(0, 5).Select(i => Codes(Units(null, i, 1)).Single()));   // pages of one reproduce the unpaged order

        // item groups: the effective valuation still resolves through ancestors that are not on the page
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var root = await Group("G1", null, 2);
        var child = await Group("G2", root.Id);
        await Group("G3", child.Id);
        static string[] GCodes(ItemGroupPage p) => [.. p.Items.Select(g => g.Code)];
        ItemGroupPage Groups(string? q, int? offset = null, int? limit = null) => Ok(InvMgmt.SearchGroupsAsync(Admin.Id, Company.Id, q, offset, limit).Result);
        var found = Groups("group g3");
        Assert.Equal(["G3"], GCodes(found));
        Assert.Equal(1L, found.Total);
        Assert.Equal(2, found.Items.Single().EffectiveValuation);                  // inherited from G1 through G2, neither on the page
        Assert.Equal(["G1", "G2"], GCodes(Groups(null, 0, 2)));
        Assert.Equal(3L, Groups(null, 0, 2).Total);
        Assert.Empty(Groups(null, 50, 5).Items);
        Assert.Equal(3L, Groups(null, 50, 5).Total);

        // the permission is the one of the unpaged lists (view of the entity or of the items); nothing else
        var outsider = await NewUser("searchoutsider");
        Fails(await InvMgmt.SearchUnitsAsync(outsider.Id, Company.Id, "box", null, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.SearchGroupsAsync(outsider.Id, Company.Id, "g", null, null), ConfigStatus.Forbidden, "forbidden");
        var (unitViewer, _) = await Member([PermissionCatalog.Codes.UnitsView], ScopeKind.Company, null);
        Assert.Equal(["U2", "U3"], Codes(Ok(await InvMgmt.SearchUnitsAsync(unitViewer.Id, Company.Id, "box", null, null))));
        Fails(await InvMgmt.SearchGroupsAsync(unitViewer.Id, Company.Id, "g", null, null), ConfigStatus.Forbidden, "forbidden");
        var (itemViewer, _) = await Member([PermissionCatalog.Codes.ItemsView], ScopeKind.Company, null);
        Assert.Equal(2, Ok(await InvMgmt.SearchUnitsAsync(itemViewer.Id, Company.Id, "box", null, null)).Items.Count);
        Assert.Equal(3L, Ok(await InvMgmt.SearchGroupsAsync(itemViewer.Id, Company.Id, null, null, null)).Total);
    }

    [PostgresFact]
    public async Task Units_are_unique_case_insensitively_keep_code_and_decimals_and_are_deactivated_never_deleted()
    {
        await PrepareInventory();
        var kg = await Unit("kg", 3);
        Assert.Equal(("KG", 3, 1L, true), (kg.Code, kg.Decimals, kg.Revision, kg.IsActive));
        Fails(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("Kg", "x", null, 2)), ConfigStatus.Conflict, "unit_code_taken");
        Invalid(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("A B", "x", null, 2)), "code", "invalid_format");
        Invalid(await Inv.UpdateUnitAsync(Admin.Id, Company.Id, kg.Id, 1, new UnitInput("KG", "x", null, 2)), "decimals", "immutable");
        var renamed = Ok(await Inv.UpdateUnitAsync(Admin.Id, Company.Id, kg.Id, 1, new UnitInput("KG", "كيلوغرام", "Kilogram", 3)));
        Assert.Equal((2L, "Kilogram"), (renamed.Revision, renamed.NameEn));
        Fails(await Inv.UpdateUnitAsync(Admin.Id, Company.Id, kg.Id, 1, new UnitInput("KG", "y", null, 3)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Inv.UpdateUnitAsync(Admin.Id, Company.Id, kg.Id, null, new UnitInput("KG", "y", null, 3)), ConfigStatus.PreconditionRequired, "precondition_required");
        Assert.Equal(2L, Ok(await Inv.UpdateUnitAsync(Admin.Id, Company.Id, kg.Id, 2, new UnitInput("KG", "كيلوغرام", "Kilogram", 3))).Revision);   // unchanged: no new revision
        var off = Ok(await Inv.SetUnitActiveAsync(Admin.Id, Company.Id, kg.Id, 2, false));
        Assert.False(off.IsActive);
        Fails(await Inv.SetUnitActiveAsync(Admin.Id, Company.Id, kg.Id, 3, false), ConfigStatus.Conflict, "unit_already_inactive");
        // PostgreSQL enforces the same, even for the owner: no delete, no truncate, code and decimals immutable, revision exactly +1
        Assert.NotNull(await Raw($"DELETE FROM {U.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {U.Table}"));
        Assert.NotNull(await Raw($"UPDATE {U.Table} SET {U.Decimals} = 2, {U.Revision} = {U.Revision} + 1"));
        Assert.NotNull(await Raw($"UPDATE {U.Table} SET {U.Code} = 'X', {U.Revision} = {U.Revision} + 1"));
        Assert.NotNull(await Raw($"UPDATE {U.Table} SET {U.NameEn} = 'x', {U.Revision} = {U.Revision} + 5"));
        Assert.NotNull(await Raw($"INSERT INTO {U.Table} ({U.Code}, {U.NameEn}, {U.Decimals}, {U.CreatedAtUtc}, {U.UpdatedAtUtc}) VALUES ('Z', 'z', 9, now(), now())"));
        Assert.Equal(3L, await InventoryAudit());                                                                                   // created, updated, deactivated; the refused and unchanged calls left no trace
    }

    // ---------------------------------------------------------------------------------------------------------------- groups and valuation

    [PostgresFact]
    public async Task Groups_form_a_tree_without_cycles_limited_to_eight_levels_and_resolve_the_valuation_method_from_the_nearest_decision()
    {
        await PrepareInventory();
        var root = await Group("ROOT");
        var child = await Group("CHILD", root.Id);
        Assert.Null(root.EffectiveValuation);                                                                                       // nobody has decided yet
        var settings = Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(2)));
        Assert.Equal(new InventorySettingsView(2, 1), settings);
        Assert.Equal(2, (await Inv.GetGroupAsync(Company.Id, child.Id))!.EffectiveValuation);                                      // company default: moving average
        var rootFifo = Ok(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, root.Id, 1, new ItemGroupInput("ROOT", "مجموعة", "Group ROOT", null, 1)));
        Assert.Equal(1, (await Inv.GetGroupAsync(Company.Id, child.Id))!.EffectiveValuation);                                      // the child inherits its parent's decision
        var avgChild = Ok(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, child.Id, 1, new ItemGroupInput("CHILD", "مجموعة", "Group CHILD", root.Id, 2)));
        Assert.Equal((2, 2), (avgChild.Valuation, avgChild.EffectiveValuation));
        Assert.Equal(1, rootFifo.EffectiveValuation);
        // cycles and depth
        Invalid(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, root.Id, 2, new ItemGroupInput("ROOT", "مجموعة", "Group ROOT", child.Id, 1)), "parentId", "cycle");
        Invalid(await Inv.UpdateGroupAsync(Admin.Id, Company.Id, root.Id, 2, new ItemGroupInput("ROOT", "مجموعة", "Group ROOT", root.Id, 1)), "parentId", "cycle");
        var deep = child;
        for (var i = 3; i <= 8; i++) deep = await Group("L" + i, deep.Id);                                                           // levels 3..8
        Invalid(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("L9", "x", null, deep.Id, null)), "parentId", "tree_too_deep");
        Assert.NotNull(await Raw($"UPDATE {G.Table} SET {G.ParentId} = (SELECT {G.Id} FROM {G.Table} WHERE {G.Code} = 'L8'), {G.Revision} = {G.Revision} + 1 WHERE {G.Code} = 'ROOT'"));   // the database refuses a cycle too
        Fails(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("root", "x", null, null, null)), ConfigStatus.Conflict, "group_code_taken");
        Invalid(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("BAD", "x", null, Guid.NewGuid(), null)), "parentId", "parent_not_found");
        Invalid(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("BAD", "x", null, null, 7)), "valuation", "invalid_method");
    }

    [PostgresFact]
    public async Task A_group_with_active_children_or_active_items_cannot_be_deactivated_and_an_inactive_parent_blocks_new_children()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var root = await Group("R"); var kid = await Group("K", root.Id);
        var pcs = await Unit("PCS", 0);
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(kid.Id, pcs.Id)));
        Fails(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, root.Id, 1, false), ConfigStatus.Conflict, "group_has_active_children");
        Fails(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, kid.Id, 1, false), ConfigStatus.Conflict, "group_has_active_items");
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, item.Id, 1, false));
        var kidOff = Ok(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, kid.Id, 1, false));
        var rootOff = Ok(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, root.Id, 1, false));
        Fails(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("K2", "x", null, root.Id, null)), ConfigStatus.Conflict, "parent_group_inactive");
        Fails(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, kid.Id, kidOff.Revision, true), ConfigStatus.Conflict, "parent_group_inactive");
        Fails(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, item.Id, 2, true), ConfigStatus.Conflict, "group_inactive");
        Ok(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, root.Id, rootOff.Revision, true));
        Ok(await Inv.SetGroupActiveAsync(Admin.Id, Company.Id, kid.Id, kidOff.Revision, true));
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, item.Id, 2, true));
    }

    [PostgresFact]
    public async Task The_company_default_valuation_is_versioned_audited_and_never_changes_silently()
    {
        await PrepareInventory();
        Fails(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, null, new InventorySettingsInput(1)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 3, new InventorySettingsInput(1)), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(9)), "defaultValuation", "invalid_method");
        Assert.Equal(new InventorySettingsView(1, 1), Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1))));
        Assert.Equal(new InventorySettingsView(1, 1), Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 1, new InventorySettingsInput(1))));       // unchanged: no new revision
        Assert.Equal(new InventorySettingsView(2, 2), Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 1, new InventorySettingsInput(2))));
        Assert.Equal(2L, await EventCount(InventoryAuditEvents.SettingsChanged));
        Assert.NotNull(await Raw($"DELETE FROM {S.Table}"));
        Assert.NotNull(await Raw($"INSERT INTO {S.Table} ({S.Id}, {S.UpdatedAtUtc}) VALUES (2, now())"));                               // a second row is impossible
    }

    // ---------------------------------------------------------------------------------------------------------------- items

    [PostgresFact]
    public async Task An_item_is_created_with_a_generated_code_alternate_units_and_a_resolved_valuation_and_the_inputs_are_checked()
    {
        await PrepareInventory();
        var pcs = await Unit("PCS", 0); var box = await Unit("BOX", 0); var pack = await Unit("PACK", 0);
        var group = await Group("G1");
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id)), "groupId", "valuation_method_unresolved");                  // goods need a decided policy
        Assert.Equal(0L, await Count(Company, I.Table));
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(2)));
        var first = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id, null, 1, (box.Id, 12m), (pack.Id, 0.5m)) with { PurchaseUnitId = box.Id, SalesUnitId = pcs.Id }));
        Assert.Equal(("ITM-000001", 1L, 2, true), (first.Code, first.Revision, first.EffectiveValuation, first.IsActive));
        Assert.Equal([("BOX", 12m), ("PACK", 0.5m)], first.AlternateUnits.Select(a => (a.UnitCode, a.Factor)));
        Assert.Equal((box.Id, pcs.Id), (first.PurchaseUnitId, first.SalesUnitId));
        Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id, "ITM-000002")));                                                    // a user takes the next automatic code
        Assert.Equal("ITM-000003", Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id))).Code);                                // the counter skips it
        Fails(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id, "itm-000001")), ConfigStatus.Conflict, "item_code_taken");
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(Guid.NewGuid(), pcs.Id)), "groupId", "group_not_found");
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, Guid.NewGuid())), "baseUnitId", "unit_not_found");
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id, null, 1, (Guid.NewGuid(), 2m))), "alternateUnits[0].unitId", "unit_not_found");
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id) with { PurchaseUnitId = box.Id }), "purchaseUnitId", "unit_not_defined_for_item");
        Ok(await Inv.SetUnitActiveAsync(Admin.Id, Company.Id, pack.Id, 1, false));
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, pcs.Id, null, 1, (pack.Id, 2m))), "alternateUnits[0].unitId", "unit_inactive");
        Assert.Equal(3L, await Count(Company, I.Table));                                                                                             // every refusal left no row
        Assert.Equal(3L, await EventCount(InventoryAuditEvents.ItemCreated));
    }

    [PostgresFact]
    public async Task A_service_is_never_tracked_never_valued_and_needs_no_valuation_policy()
    {
        await PrepareInventory();
        var hour = await Unit("HR", 2);
        var group = await Group("SRV");
        var service = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, hour.Id, null, 2)));                                       // no company default exists: still allowed
        Assert.Equal((2, (int?)null), (service.ItemType, service.EffectiveValuation));
        Invalid(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(group.Id, hour.Id, null, 2) with { IsLot = true }), "itemType", "service_cannot_be_tracked");
        Assert.NotNull(await Raw($"UPDATE {I.Table} SET {I.IsSerial} = true, {I.Revision} = {I.Revision} + 1"));                                       // the database refuses it too
    }

    [PostgresFact]
    public async Task Updating_an_item_changes_names_group_units_and_flags_with_revisions_and_keeps_code_type_and_base_unit()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var pcs = await Unit("PCS", 0); var box = await Unit("BOX", 0); var crate = await Unit("CRATE", 0);
        var g1 = await Group("G1"); var g2 = await Group("G2", null, 2);
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g1.Id, pcs.Id, null, 1, (box.Id, 6m))));
        var input = NewItem(g2.Id, pcs.Id, item.Code, 1, (box.Id, 6m), (crate.Id, 24m)) with { IsLot = true, HasExpiry = true, Notes = "n", NameEn = "Renamed", SalesUnitId = crate.Id };
        var updated = Ok(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 1, input));
        Assert.Equal((2L, g2.Id, 2, true, true, "Renamed"), (updated.Revision, updated.GroupId, updated.EffectiveValuation, updated.IsLot, updated.HasExpiry, updated.NameEn));
        Assert.Equal(2, updated.AlternateUnits.Count);
        Fails(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 1, input), ConfigStatus.PreconditionFailed, "revision_conflict");
        Assert.Equal(2L, Ok(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 2, input)).Revision);                                              // unchanged: nothing written
        var shrink = Ok(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 2, NewItem(g2.Id, pcs.Id, item.Code, 1, (box.Id, 6m)) with { NameEn = "Renamed", IsLot = true, HasExpiry = true, Notes = "n" }));
        Assert.Equal(1, shrink.AlternateUnits.Count);
        Invalid(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 3, NewItem(g2.Id, box.Id, item.Code)), "baseUnitId", "immutable");
        Invalid(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 3, NewItem(g2.Id, pcs.Id, "OTHER")), "code", "immutable");
        Invalid(await Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, 3, NewItem(g2.Id, pcs.Id, item.Code, 2)), "itemType", "immutable");
        Fails(await Inv.UpdateItemAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, input), ConfigStatus.NotFound, "item_not_found");
        // PostgreSQL: type, code and base unit are immutable, rows are never deleted
        Assert.NotNull(await Raw($"UPDATE {I.Table} SET {I.BaseUnitId} = (SELECT {U.Id} FROM {U.Table} WHERE {U.Code} = 'BOX'), {I.Revision} = {I.Revision} + 1"));
        Assert.NotNull(await Raw($"UPDATE {I.Table} SET {I.Code} = 'ZZ', {I.Revision} = {I.Revision} + 1"));
        Assert.NotNull(await Raw($"UPDATE {I.Table} SET {I.ItemType} = 2, {I.Revision} = {I.Revision} + 1"));
        Assert.NotNull(await Raw($"DELETE FROM {I.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {I.Table}"));
        Assert.Equal(2L, await EventCount(InventoryAuditEvents.ItemUpdated));
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_the_unit_rules_of_an_item_at_commit()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var pcs = await Unit("PCS", 0); var box = await Unit("BOX", 0);
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem((await Group("G")).Id, pcs.Id, null, 1, (box.Id, 6m))));
        Assert.NotNull(await Raw($"INSERT INTO {IU.Table} ({IU.ItemId}, {IU.UnitId}, {IU.Factor}) VALUES ('{item.Id}', '{pcs.Id}', 2)"));                  // the base unit cannot be an alternate unit
        Assert.NotNull(await Raw($"INSERT INTO {IU.Table} ({IU.ItemId}, {IU.UnitId}, {IU.Factor}) SELECT '{item.Id}', {U.Id}, 0 FROM {U.Table} WHERE {U.Code} = 'BOX'"));   // factor must be positive (and the pair is unique)
        Assert.NotNull(await Raw($"UPDATE {I.Table} SET {I.PurchaseUnitId} = gen_random_uuid(), {I.Revision} = {I.Revision} + 1"));                           // unknown unit
        Assert.NotNull(await Raw($"DELETE FROM {IU.Table} WHERE {IU.ItemId} = '{item.Id}'", $"UPDATE {I.Table} SET {I.SalesUnitId} = '{box.Id}', {I.Revision} = {I.Revision} + 1"));   // a sales unit that is not defined
        Assert.Null(await Raw($"UPDATE {I.Table} SET {I.SalesUnitId} = '{box.Id}', {I.Revision} = {I.Revision} + 1"));                                       // defined: fine
        Assert.NotNull(await Raw($"DELETE FROM {IU.Table} WHERE {IU.ItemId} = '{item.Id}'"));                                                                // removing a unit still used as the sales unit
        Assert.Equal(1L, await Count(Company, IU.Table));
    }

    [PostgresFact]
    public async Task Item_listing_filters_by_text_group_with_sub_groups_active_state_type_and_exact_barcode_with_paging()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var pcs = await Unit("PCS", 0);
        var root = await Group("ROOT"); var kid = await Group("KID", root.Id); var other = await Group("OTHER");
        var a = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(root.Id, pcs.Id, "A-1") with { NameEn = "Apple" }));
        var b = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(kid.Id, pcs.Id, "B-1") with { NameEn = "Banana", NameAr = "موز" }));
        Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(other.Id, pcs.Id, "C-1", 2) with { NameEn = "Cleaning service" }));
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, a.Id, 1, false));
        Ok(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, b.Id, new BarcodeInput("4006381333931", null, null)));
        async Task<string[]> Codes(string? search = null, Guid? group = null, bool? active = null, int? type = null, int offset = 0, int limit = 50) =>
            [.. (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter(search, group, active, type, offset, limit))).Items.Select(i => i.Code)];
        Assert.Equal(["A-1", "B-1", "C-1"], await Codes());
        Assert.Equal(["A-1", "B-1"], await Codes(group: root.Id));                                                                                    // includes the sub-group
        Assert.Equal(["B-1"], await Codes(group: kid.Id));
        Assert.Equal(["B-1", "C-1"], await Codes(active: true));
        Assert.Equal(["C-1"], await Codes(type: 2));
        Assert.Equal(["B-1"], await Codes("banan"));
        Assert.Equal(["B-1"], await Codes("موز"));
        Assert.Equal(["B-1"], await Codes("4006381333931"));                                                                                          // exact barcode
        Assert.Empty(await Codes("400638133393"));                                                                                                    // a partial barcode is not a match
        Assert.Empty(await Codes("%"));                                                                                                               // wildcards are literals
        Assert.Equal(["B-1"], await Codes(offset: 1, limit: 1));
        Assert.Equal(3L, (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter(null, null, null, null, 0, 1))).Total);
    }

    // ---------------------------------------------------------------------------------------------------------------- barcodes

    [PostgresFact]
    public async Task Barcodes_are_company_wide_unique_even_when_inactive_belong_to_a_defined_unit_and_are_found_by_exact_lookup()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var pcs = await Unit("PCS", 0); var box = await Unit("BOX", 0);
        var g = await Group("G");
        var one = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id, "ONE", 1, (box.Id, 12m))));
        var two = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id, "TWO")));
        var ean = Ok(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, one.Id, new BarcodeInput(" 4006381333931 ", null, null)));
        Assert.Equal((one.Id, pcs.Id, (int)BarcodeKind.Ean13, true), (ean.ItemId, ean.UnitId, ean.Kind, ean.IsActive));                                 // default unit = the base unit, kind detected
        var carton = Ok(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, one.Id, new BarcodeInput("carton-1", box.Id, null)));
        Assert.Equal((int)BarcodeKind.Internal, carton.Kind);
        Fails(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, two.Id, new BarcodeInput("4006381333931", null, null)), ConfigStatus.Conflict, "barcode_taken");
        Fails(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, two.Id, new BarcodeInput("CARTON-1", null, null)), ConfigStatus.Conflict, "barcode_taken");   // case-insensitive
        Invalid(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, two.Id, new BarcodeInput("X1", box.Id, null)), "unitId", "unit_not_defined_for_item");   // BOX is not defined for item TWO
        Invalid(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, two.Id, new BarcodeInput("4006381333932", null, 1)), "barcode", "invalid_check_digit");
        Fails(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, Guid.NewGuid(), new BarcodeInput("Y1", null, null)), ConfigStatus.NotFound, "item_not_found");
        var off = Ok(await Inv.SetBarcodeActiveAsync(Admin.Id, Company.Id, one.Id, ean.Id, false));
        Assert.False(off.IsActive);
        Fails(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, two.Id, new BarcodeInput("4006381333931", null, null)), ConfigStatus.Conflict, "barcode_taken");   // inactive barcodes keep their value
        Fails(await Inv.SetBarcodeActiveAsync(Admin.Id, Company.Id, two.Id, ean.Id, true), ConfigStatus.NotFound, "barcode_not_found");               // wrong item
        var hit = (await Inv.LookupBarcodeAsync(Company.Id, "carton-1"))!;
        Assert.Equal(("ONE", "BOX", 12m, true), (hit.ItemCode, hit.UnitCode, hit.UnitFactor, hit.Barcode.IsActive));
        Assert.Equal(1m, (await Inv.LookupBarcodeAsync(Company.Id, "4006381333931"))!.UnitFactor);
        Assert.Null(await Inv.LookupBarcodeAsync(Company.Id, "NOPE"));
        Assert.Null(await Inv.LookupBarcodeAsync(Company.Id, "  "));
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, one.Id, 1, false));
        Fails(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, one.Id, new BarcodeInput("Z9", null, null)), ConfigStatus.Conflict, "item_inactive");
        Fails(await Inv.SetBarcodeActiveAsync(Admin.Id, Company.Id, one.Id, ean.Id, true), ConfigStatus.Conflict, "item_inactive");
        // PostgreSQL: only the active flag can change; no delete
        Assert.NotNull(await Raw($"UPDATE {BC.Table} SET {BC.Barcode} = 'CHANGED'"));
        Assert.NotNull(await Raw($"UPDATE {BC.Table} SET {BC.Kind} = 4"));
        Assert.NotNull(await Raw($"DELETE FROM {BC.Table}"));
        Assert.NotNull(await Raw($"INSERT INTO {BC.Table} ({BC.ItemId}, {BC.UnitId}, {BC.Barcode}, {BC.Kind}, {BC.CreatedAtUtc}, {BC.UpdatedAtUtc}) VALUES ('{two.Id}', '{box.Id}', 'RAW-1', 4, now(), now())"));   // an undefined unit
        Assert.NotNull(await Raw($"INSERT INTO {BC.Table} ({BC.ItemId}, {BC.UnitId}, {BC.Barcode}, {BC.Kind}, {BC.CreatedAtUtc}, {BC.UpdatedAtUtc}) VALUES ('{two.Id}', '{pcs.Id}', 'bad space', 4, now(), now())"));
    }

    [PostgresFact]
    public async Task Concurrent_creations_never_duplicate_a_code_a_barcode_or_a_generated_number()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var pcs = await Unit("PCS", 0);
        var g = await Group("G");
        var generated = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id))));
        Assert.All(generated, r => Assert.True(r.IsOk, r.Code));
        Assert.Equal(12, generated.Select(r => r.Value!.Code).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 12).Select(n => InventoryValidation.FormatGeneratedCode(n)).Order(), generated.Select(r => r.Value!.Code).Order());
        var item = generated[0].Value!;
        var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Inv.AddBarcodeAsync(Admin.Id, Company.Id, item.Id, new BarcodeInput("RACE-1", null, null))));
        Assert.Equal(1, racing.Count(r => r.IsOk));
        Assert.All(racing.Where(r => !r.IsOk), r => Assert.Equal("barcode_taken", r.Code));
        var sameCode = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id, "SAME"))));
        Assert.Equal(1, sameCode.Count(r => r.IsOk));
        Assert.All(sameCode.Where(r => !r.IsOk), r => Assert.Equal("item_code_taken", r.Code));
        var stale = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Inv.UpdateItemAsync(Admin.Id, Company.Id, item.Id, item.Revision, NewItem(g.Id, pcs.Id, item.Code) with { NameEn = "N" + i })));
        Assert.Equal(1, stale.Count(r => r.IsOk));                                                                                                    // optimistic concurrency: one winner
        Assert.All(stale.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
    }

    // ---------------------------------------------------------------------------------------------------------------- authorization and audit

    [PostgresFact]
    public async Task Every_operation_is_authorized_company_wide_and_a_viewer_can_read_but_never_change_anything()
    {
        await PrepareInventory();
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var pcs = await Unit("PCS", 0); var g = await Group("G");
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id)));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.ItemsView);
        var (nobody, _) = await Holder(PermissionCatalog.Codes.AccountsView);
        Assert.Single(Ok(await InvMgmt.ListItemsAsync(viewer.Id, Company.Id, new CompanyInventoryService.ItemFilter(null, null, null, null, 0, 10))).Items);
        Assert.Equal(item.Id, Ok(await InvMgmt.GetItemAsync(viewer.Id, Company.Id, item.Id)).Id);
        Assert.Single(Ok(await InvMgmt.ListUnitsAsync(viewer.Id, Company.Id)));                                                                       // the item editor needs the unit list
        Assert.Single(Ok(await InvMgmt.ListGroupsAsync(viewer.Id, Company.Id)));
        Assert.Empty(Ok(await InvMgmt.ListBarcodesAsync(viewer.Id, Company.Id, item.Id)));
        Fails(await InvMgmt.CreateItemAsync(viewer.Id, Company.Id, NewItem(g.Id, pcs.Id)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.UpdateItemAsync(viewer.Id, Company.Id, item.Id, 1, NewItem(g.Id, pcs.Id, item.Code)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.SetItemActiveAsync(viewer.Id, Company.Id, item.Id, 1, false), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.CreateUnitAsync(viewer.Id, Company.Id, new UnitInput("X", "x", null, 0)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.CreateGroupAsync(viewer.Id, Company.Id, new ItemGroupInput("X", "x", null, null, null)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.AddBarcodeAsync(viewer.Id, Company.Id, item.Id, new BarcodeInput("A1", null, null)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.GetSettingsAsync(viewer.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.SaveSettingsAsync(viewer.Id, Company.Id, 1, new InventorySettingsInput(2)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.ListItemsAsync(nobody.Id, Company.Id, new CompanyInventoryService.ItemFilter(null, null, null, null, 0, 10)), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.ListUnitsAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await InvMgmt.LookupBarcodeAsync(nobody.Id, Company.Id, "A1"), ConfigStatus.Forbidden, "forbidden");
        var (creator, _) = await Holder(PermissionCatalog.Codes.ItemsView, PermissionCatalog.Codes.ItemsCreate, PermissionCatalog.Codes.BarcodesCreate);
        var made = Ok(await InvMgmt.CreateItemAsync(creator.Id, Company.Id, NewItem(g.Id, pcs.Id)));
        Ok(await InvMgmt.AddBarcodeAsync(creator.Id, Company.Id, made.Id, new BarcodeInput("A1", null, null)));
        Fails(await InvMgmt.UpdateItemAsync(creator.Id, Company.Id, made.Id, 1, NewItem(g.Id, pcs.Id, made.Code)), ConfigStatus.Forbidden, "forbidden");           // create is not update
        Fails(await InvMgmt.SetBarcodeActiveAsync(creator.Id, Company.Id, made.Id, Guid.NewGuid(), false), ConfigStatus.Forbidden, "forbidden");                    // add is not activate
        var caps = await InvMgmt.CapabilitiesAsync(creator.Id, Company.Id, default);
        Assert.True(caps["itemsCreate"] && caps["itemsView"] && caps["barcodesCreate"]);
        Assert.False(caps["itemsUpdate"] || caps["unitsCreate"] || caps["settingsConfigure"]);
        Assert.True((await InvMgmt.CapabilitiesAsync(Admin.Id, Company.Id, default)).Values.All(v => v));
    }

    [PostgresFact]
    public async Task Every_change_writes_one_authenticated_audit_event_and_creates_no_stock_or_accounting_record()
    {
        await PrepareInventory();
        var pcs = await Unit("PCS", 0);                                                                                                               // 1 unit event
        var g = await Group("G", null, 1);                                                                                                            // 1 group event
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id)));                                                       // 1 item event
        Ok(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, item.Id, new BarcodeInput("B1", null, null)));                                             // 1 barcode event
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, item.Id, 1, false));                                                                    // 1 active event
        Assert.Equal(5L, await InventoryAudit());
        Assert.Equal(5L, await Count(Company, Audit_.Table) - AuditBase);
        var details = await Sql<string>($"SELECT string_agg({Audit_.Detail}, '|' ORDER BY {Audit_.Seq}) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1120 AND 1139");
        Assert.Contains("unit PCS created", details);
        Assert.DoesNotContain("password", details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await Sql<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND (table_name ILIKE '%stock%' OR table_name ILIKE '%movement%' OR table_name ILIKE '%layer%')"));
        Assert.Equal(0L, await Count(Company, CompanySchema.JournalEntry.Table));                                                                      // master data never posts to the ledger
    }
}
