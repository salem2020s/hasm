using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Tests;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemGroup;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using BC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemBarcode;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.4B: inventory master-data imports (units, item groups, items with alternate units, conversions, barcodes and tracking flags) end to end against real PostgreSQL: real CSV and XLSX files, the real
/// adapters, the real inventory domain service, independent approval and the atomic application. Disposable cluster only.
/// </summary>
public class InventoryImportTests : ImportTestBase
{
    private CompanyInventoryService Inv = null!;
    private FailingAdapter Failing = null!;

    private const string UnitHeader = "code,name_ar,name_en,decimals\n";
    private const string GroupHeader = "code,name_ar,name_en,parent_code,valuation\n";
    private const string ItemHeader = "code,name_ar,name_en,type,group_code,base_unit_code,purchase_unit_code,sales_unit_code,alternate_units,barcodes,is_lot,is_serial,has_expiry,notes\n";
    private static JsonObject MapOf(params string[] fields) => new() { ["fields"] = new JsonObject(fields.Select((f, i) => KeyValuePair.Create(f, (JsonNode?)new JsonObject { ["column"] = i }))), ["options"] = new JsonObject { ["decimalSeparator"] = ".", ["thousandsSeparator"] = "," } };
    private static readonly JsonObject UnitMap = MapOf("code", "name_ar", "name_en", "decimals");
    private static readonly JsonObject GroupMap = MapOf("code", "name_ar", "name_en", "parent_code", "valuation");
    private static readonly JsonObject ItemMap = MapOf("code", "name_ar", "name_en", "type", "group_code", "base_unit_code", "purchase_unit_code", "sales_unit_code", "alternate_units", "barcodes", "is_lot", "is_serial", "has_expiry", "notes");

    /// <summary>Wraps the real items adapter; when told to, fails AFTER the real adapter has created everything, to prove the whole application rolls back.</summary>
    private sealed class FailingAdapter(IImportAdapter inner) : IImportAdapter
    {
        public bool Fail;
        public string Code => inner.Code; public int Version => inner.Version; public string NameAr => inner.NameAr; public string NameEn => inner.NameEn;
        public string UploadPermission => inner.UploadPermission; public string ViewPermission => inner.ViewPermission;
        public IReadOnlyList<ImportField> Fields => inner.Fields; public IReadOnlyList<string> Resolutions => inner.Resolutions; public bool Atomic => inner.Atomic;
        public Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct) => inner.AnalyzeAsync(context, rows, ct);
        public Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => inner.BeginApplyAsync(work, ct);
        public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
        {
            var result = await inner.ApplyAsync(context, ct);
            if (Fail) throw new ImportApplyException("simulated_failure", "simulated failure after the items were created");
            return result;
        }
    }

    private async Task Prepare(bool defaultValuation = true)
    {
        await PrepareRbac();
        Inv = new CompanyInventoryService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        Failing = new FailingAdapter(new InventoryItemsImportAdapter(Inv));
        BuildImportFacade(adapters: [new InventoryUnitsImportAdapter(Inv), new InventoryGroupsImportAdapter(Inv), Failing]);
        if (defaultValuation) Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(2)));
    }

    private async Task<ImportBatchView> Validate(string adapter, byte[] data, JsonObject map, string name = "data.csv", bool xlsx = false)
    {
        var u = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, adapter, name, data, null));
        var s = Ok(await Imports.StageAsync(Admin.Id, Company.Id, u.Id, u.Revision, xlsx ? new StageInput(0, 1, null, null) : new StageInput(0, 1, "utf-8", null)));
        var m = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, map));
        return Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision));
    }

    private async Task<ImportBatchView> Approve(ImportBatchView validated)
    {
        var sub = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, validated.Id, validated.Revision));
        var (user, _) = await Member([PermissionCatalog.Codes.ItemsView, PermissionCatalog.Codes.UnitsView, PermissionCatalog.Codes.ItemGroupsView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(user.Login, Company.Id));
        return Ok(await Imports.DecideAsync(user.Id, Company.Id, sub.Id, sub.Revision, true, null));
    }

    private async Task<ImportBatchView> ApplyAll(string adapter, byte[] data, JsonObject map)
    {
        var v = await Validate(adapter, data, map);
        Assert.True(v.Plan!["canApply"]!.GetValue<bool>(), v.Plan.ToJsonString());
        var a = await Approve(v);
        return Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
    }

    private Task<ImportBatchView> Units(string rows) => ApplyAll(InventoryUnitsImportAdapter.AdapterCode, Bytes(UnitHeader + rows), UnitMap);
    private Task<ImportBatchView> Groups(string rows) => ApplyAll(InventoryGroupsImportAdapter.AdapterCode, Bytes(GroupHeader + rows), GroupMap);
    private Task<ImportBatchView> ValidateItems(string rows) => Validate(InventoryItemsImportAdapter.AdapterCode, Bytes(ItemHeader + rows), ItemMap);
    private async Task<Dictionary<int, ImportRowView>> RowMap(Guid id) => (await Imports.RowsAsync(Company.Id, id, 0, 500, null))!.Rows.ToDictionary(r => r.RowNo);
    private static string[] Codes(ImportRowView row) => row.Issues is JsonArray a ? [.. a.Select(i => i!["code"]!.GetValue<string>())] : [];
    private async Task<long> Count(string table) => await Sql<long>($"SELECT count(*) FROM {table}");
    private async Task<long> InventoryEvents() => await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} BETWEEN 1120 AND 1139");

    /// <summary>The usual foundation: units PCS/BOX/PACK/KG, a FOOD group (FIFO) with a FRUIT child and a TOOLS group that inherits the company default (moving average).</summary>
    private async Task Foundation()
    {
        Assert.Equal(ImportStatus.Applied, (await Units("PCS,قطعة,Piece,0\nBOX,علبة,Box,0\nPACK,,Pack,0\nKG,كيلوغرام,Kilogram,3\n")).Status);
        Assert.Equal(ImportStatus.Applied, (await Groups("FRUIT,فواكه,Fruit,FOOD,\nFOOD,أغذية,Food,,fifo\nTOOLS,,Tools,,\n")).Status);
    }

    // ---------------------------------------------------------------------------------------------------------------- the whole flow

    [PostgresFact]
    public async Task Units_groups_and_items_are_imported_in_order_through_the_real_domain_path_with_audit_and_exact_values()
    {
        await Prepare();
        var u = await Units("pcs,قطعة,Piece,0\nBOX,علبة,Box,٠\nPACK,,Pack,0\nKG,كيلوغرام,Kilogram,3\n");                                   // lower case and Arabic-Indic digits are normalised
        Assert.Equal((ImportStatus.Applied, 4), (u.Status, u.Result!["summary"]!["created"]!.GetValue<int>()));
        Assert.Equal(["BOX", "KG", "PACK", "PCS"], (await Inv.ListUnitsAsync(Company.Id)).Select(x => x.Code));
        Assert.Equal(3, (await Inv.ListUnitsAsync(Company.Id)).Single(x => x.Code == "KG").Decimals);
        // groups: the child is listed BEFORE its parent
        var g = await Groups("FRUIT,فواكه,Fruit,FOOD,\nFOOD,أغذية,Food,,fifo\nTOOLS,,Tools,,average\n");
        Assert.Equal(3, g.Result!["summary"]!["created"]!.GetValue<int>());
        var groups = (await Inv.ListGroupsAsync(Company.Id)).ToDictionary(x => x.Code);
        Assert.Equal(groups["FOOD"].Id, groups["FRUIT"].ParentId);
        Assert.Equal((1, 1, 2), (groups["FOOD"].Valuation, groups["FRUIT"].EffectiveValuation, groups["TOOLS"].Valuation));    // FRUIT inherits FIFO from FOOD
        var events = await InventoryEvents();

        var rows =
            "0001,تفاح,Apple,goods,FRUIT,PCS,BOX,PCS,BOX=12;PACK=0.5,4006381333931;007654321@BOX,yes,,true,fresh\n" +
            "ITM-2,,Hammer,,TOOLS,PCS,,,,٠٠١٢٣٤٥,,,,\n" +
            "SRV1,صيانة,Maintenance,service,TOOLS,PCS,,,,,,,,\n";
        var v = await ValidateItems(rows);
        Assert.True(v.Plan!.ToJsonString().Length > 0);
        foreach (var r in (await RowMap(v.Id)).Values.Where(r => r.Status != (short)RowOutcome.Valid)) Assert.Fail($"row {r.RowNo}: {r.Issues}");
        Assert.Equal((3, true), (v.Plan!["summary"]!["operations"]!.GetValue<int>(), v.Plan["canApply"]!.GetValue<bool>()));
        Assert.Equal(0L, await Count(I.Table));                                                                                    // the dry run changed nothing
        Assert.Equal(0L, await Count(BC.Table));
        Assert.Equal(events, await InventoryEvents());
        var map = await RowMap(v.Id);
        Assert.All(map.Values, r => Assert.Equal((short)RowOutcome.Valid, r.Status));
        var applied = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, (await Approve(v)).Id, (await Reload(v.Id)).Revision));
        Assert.Equal(ImportStatus.Applied, applied.Status);
        Assert.Equal((3, 2, 3), (applied.Result!["summary"]!["created"]!.GetValue<int>(), applied.Result["summary"]!["alternateUnits"]!.GetValue<int>(), applied.Result["summary"]!["barcodes"]!.GetValue<int>()));

        var items = (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter(null, null, null, null, 0, 50))).Items.ToDictionary(x => x.Code);
        Assert.Equal(["0001", "ITM-2", "SRV1"], items.Keys.Order());                                                               // the leading zeros of the code survive
        var apple = items["0001"];
        Assert.Equal(("تفاح", "Apple", 1, 1), (apple.NameAr, apple.NameEn, apple.ItemType, apple.EffectiveValuation));
        Assert.Equal([("BOX", 12m), ("PACK", 0.5m)], apple.AlternateUnits.Select(a => (a.UnitCode, a.Factor)));
        Assert.True(apple.IsLot && apple.HasExpiry && !apple.IsSerial);
        Assert.Equal("fresh", apple.Notes);
        var units = (await Inv.ListUnitsAsync(Company.Id)).ToDictionary(x => x.Code);
        Assert.Equal((units["BOX"].Id, units["PCS"].Id), (apple.PurchaseUnitId, apple.SalesUnitId));
        Assert.Equal(("TOOLS", 2), ((await Inv.ListGroupsAsync(Company.Id)).Single(x => x.Id == items["ITM-2"].GroupId).Code, items["ITM-2"].EffectiveValuation));
        Assert.Equal((2, (int?)null), (items["SRV1"].ItemType, items["SRV1"].EffectiveValuation));
        var bcs = await Inv.ListBarcodesAsync(Company.Id, apple.Id);
        Assert.Equal(["007654321", "4006381333931"], bcs.Select(b => b.Barcode).Order());                                          // leading zeros of a barcode survive
        Assert.Equal((int)BarcodeKind.Ean13, bcs.Single(b => b.Barcode == "4006381333931").Kind);
        Assert.Equal(units["BOX"].Id, bcs.Single(b => b.Barcode == "007654321").UnitId);                                           // barcode-to-unit mapping
        Assert.Equal(["001234５".Replace('５', '5')], (await Inv.ListBarcodesAsync(Company.Id, items["ITM-2"].Id)).Select(b => b.Barcode));     // Arabic-Indic digits normalised, zeros kept
        Assert.Equal(events + 3 + 3, await InventoryEvents());                                                                     // one audit event per item and per barcode, through the ordinary domain path
        Assert.Equal(0L, await Count(CompanySchema.JournalEntry.Table));                                                           // no stock and no accounting entry
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_real_excel_workbook_is_imported_and_text_barcodes_keep_their_leading_zeros()
    {
        await Prepare();
        await Foundation();
        var xlsx = new XlsxBuilder().Sheet("Items", new object?[][]
        {
            ["code", "name_ar", "name_en", "type", "group_code", "base_unit_code", "purchase_unit_code", "sales_unit_code", "alternate_units", "barcodes", "is_lot", "is_serial", "has_expiry", "notes"],
            ["00100", "موز", "Banana", "مادة", "FRUIT", "KG", null, null, null, "0012345678905", null, null, null, null],
            ["00200", null, "Pen", "goods", "TOOLS", "PCS", null, null, "BOX=10", "000999", "no", "yes", null, "ملاحظة"],
        }).Build();
        var v = await Validate(InventoryItemsImportAdapter.AdapterCode, xlsx, ItemMap, "items.xlsx", xlsx: true);
        Assert.Equal((2, true), (v.Plan!["summary"]!["operations"]!.GetValue<int>(), v.Plan["canApply"]!.GetValue<bool>()));
        var a = await Approve(v);
        Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
        var item = (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter("00100", null, null, null, 0, 5))).Items.Single();
        Assert.Equal("00100", item.Code);
        Assert.Equal("0012345678905", (await Inv.ListBarcodesAsync(Company.Id, item.Id)).Single().Barcode);
        var pen = (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter("00200", null, null, null, 0, 5))).Items.Single();
        Assert.Equal((true, "ملاحظة"), (pen.IsSerial, pen.Notes));
        Assert.Equal("000999", (await Inv.ListBarcodesAsync(Company.Id, pen.Id)).Single().Barcode);
    }

    // ---------------------------------------------------------------------------------------------------------------- validation

    [PostgresFact]
    public async Task Invalid_rows_are_reported_with_stable_codes_block_the_import_and_change_nothing()
    {
        await Prepare();
        await Foundation();
        Ok(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("OFF", "x", null, 0)));
        var off = (await Inv.ListUnitsAsync(Company.Id)).Single(x => x.Code == "OFF");
        Ok(await Inv.SetUnitActiveAsync(Admin.Id, Company.Id, off.Id, 1, false));
        var rows =
            "A1,,Missing group,goods,NOPE,PCS,,,,,,,,\n" +
            "A2,,Missing unit,goods,TOOLS,NOUNIT,,,,,,,,\n" +
            "A3,,Inactive unit,goods,TOOLS,OFF,,,,,,,,\n" +
            "A4,,Equals base,goods,TOOLS,PCS,,,PCS=2,,,,,\n" +
            "A5,,Bad factor,goods,TOOLS,PCS,,,BOX=0.00000000001,,,,,\n" +
            "A6,,Zero factor,goods,TOOLS,PCS,,,BOX=0,,,,,\n" +
            "A7,,Malformed alt,goods,TOOLS,PCS,,,BOX,,,,,\n" +
            "A8,,Duplicate alt,goods,TOOLS,PCS,,,BOX=2;BOX=3,,,,,\n" +
            "A9,,Purchase unit not defined,goods,TOOLS,PCS,BOX,,,,,,,\n" +
            "B1,,Bad check digit,goods,TOOLS,PCS,,,,4006381333932@,,,,\n" +
            "B2,,Barcode unit not defined,goods,TOOLS,PCS,,,,ABC-1@BOX,,,,\n" +
            "B3,,Bad barcode shape,goods,TOOLS,PCS,,,,has space,,,,\n" +
            "B4,,Service tracked,service,TOOLS,PCS,,,,,yes,,,\n" +
            "B5,,,goods,TOOLS,PCS,,,,,,,,\n" +
            "B6,,Bad type,widget,TOOLS,PCS,,,,,,,,\n";
        var v = await ValidateItems(rows);
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());
        var map = await RowMap(v.Id);
        Assert.Contains("group_not_found", Codes(map[2]));
        Assert.Contains("unit_not_found", Codes(map[3]));
        Assert.Contains("unit_inactive", Codes(map[4]));
        Assert.Contains("equals_base_unit", Codes(map[5]));
        Assert.Contains("invalid_factor", Codes(map[6]));
        Assert.Contains("invalid_factor", Codes(map[7]));
        Assert.Contains("invalid_alternate_unit", Codes(map[8]));
        Assert.Contains("duplicate_unit", Codes(map[9]));
        Assert.Contains("unit_not_defined_for_item", Codes(map[10]));
        Assert.Contains("invalid_check_digit", Codes(map[11]));
        Assert.Contains("unit_not_defined_for_item", Codes(map[12]));
        Assert.Contains("invalid_format", Codes(map[13]));
        Assert.Contains("service_cannot_be_tracked", Codes(map[14]));
        Assert.Contains("name_required", Codes(map[15]));
        Assert.NotEmpty(Codes(map[16]));
        Assert.All(map.Values, r => Assert.Equal((short)RowOutcome.Invalid, r.Status));
        Assert.Equal("blocking_rows", (await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, v.Revision)).Code);                      // an invalid file cannot even be submitted
        Assert.Equal(0L, await Count(I.Table));
    }

    [PostgresFact]
    public async Task Goods_need_a_decided_valuation_policy_and_services_do_not()
    {
        await Prepare(defaultValuation: false);
        Assert.Equal(ImportStatus.Applied, (await Units("PCS,,Piece,0\n")).Status);
        Assert.Equal(ImportStatus.Applied, (await Groups("PLAIN,,Plain,,\nDECIDED,,Decided,,fifo\n")).Status);
        var v = await ValidateItems("G1,,Goods,goods,PLAIN,PCS,,,,,,,,\nS1,,Service,service,PLAIN,PCS,,,,,,,,\nG2,,Goods,goods,DECIDED,PCS,,,,,,,,\n");
        var map = await RowMap(v.Id);
        Assert.Contains("valuation_method_unresolved", Codes(map[2]));
        Assert.Equal(((short)RowOutcome.Valid, (short)RowOutcome.Valid), (map[3].Status, map[4].Status));
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));                                         // the company decides: the old plan is stale and a new dry run accepts it
        var again = await ValidateItems("G1,,Goods,goods,PLAIN,PCS,,,,,,,,\n");
        Assert.True(again.Plan!["canApply"]!.GetValue<bool>());
    }

    [PostgresFact]
    public async Task Duplicate_items_and_barcodes_are_detected_inside_the_file_and_against_existing_data()
    {
        await Prepare();
        await Foundation();
        var existing = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput("OLD", null, "Old", 1, (await Inv.ListGroupsAsync(Company.Id)).Single(g => g.Code == "TOOLS").Id,
            (await Inv.ListUnitsAsync(Company.Id)).Single(u => u.Code == "PCS").Id, null, null, false, false, false, null, [])));
        Ok(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, existing.Id, new BarcodeInput("4006381333931", null, null)));
        var rows =
            "N1,,One,goods,TOOLS,PCS,,,,5901234123457,,,,\n" +
            "N2,,Two,goods,TOOLS,PCS,,,,5901234123457,,,,\n" +                                  // the same barcode as row 2 of the file
            "N3,,Three,goods,TOOLS,PCS,,,,4006381333931,,,,\n" +                                // a barcode of an existing item
            "N1,,One,goods,TOOLS,PCS,,,,5901234123457,,,,\n" +                                  // an exact repeat of row 2
            "N4,,Four,goods,TOOLS,PCS,,,,5901234123457;5901234123457,,,,\n";                    // the same barcode twice in one row
        var v = await ValidateItems(rows);
        var map = await RowMap(v.Id);
        Assert.Equal((short)RowOutcome.Valid, map[2].Status);
        Assert.Contains("barcode_duplicate_in_file", Codes(map[3]));
        Assert.Contains("barcode_taken", Codes(map[4]));
        Assert.NotEqual((short)RowOutcome.Valid, map[5].Status);                                                                            // the framework flags a repeated code
        Assert.Contains("duplicate_barcode", Codes(map[6]));                                                                    // the same barcode twice in one row
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------------------------------------------------------- existing records and conflicts

    [PostgresFact]
    public async Task An_existing_code_is_exact_when_identical_or_a_conflict_that_can_only_be_skipped_and_nothing_is_ever_overwritten()
    {
        await Prepare();
        await Foundation();
        Assert.Equal(ImportStatus.Applied, (await ApplyAll(InventoryItemsImportAdapter.AdapterCode, Bytes(ItemHeader + "E1,,Exact,goods,TOOLS,PCS,,,BOX=6,4006381333931,,,,\nE2,,Changing,goods,TOOLS,PCS,,,,,,,,\n"), ItemMap)).Status);
        var before = (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter(null, null, null, null, 0, 50))).Items.ToDictionary(i => i.Code);
        var events = await InventoryEvents();
        var v = await ValidateItems("E1,,Exact,goods,TOOLS,PCS,,,BOX=6,4006381333931,,,,\nE2,,Different name,goods,FOOD,PCS,,,,,,,,\nE3,,New,goods,TOOLS,PCS,,,,,,,,\n");
        var map = await RowMap(v.Id);
        Assert.Equal((short)RowOutcome.Exact, map[2].Status);
        Assert.Equal((short)RowOutcome.Conflict, map[3].Status);
        Assert.Contains("record_exists_with_differences", Codes(map[3]));
        Assert.Equal((short)RowOutcome.Valid, map[4].Status);
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());                                                                               // the conflict must be decided first
        var resolved = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(3, "skip")]));
        var skip = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, resolved.Id, resolved.Revision));
        Assert.True(skip.Plan!["canApply"]!.GetValue<bool>());
        var a = await Approve(skip);
        var applied = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
        Assert.Equal(1, applied.Result!["summary"]!["created"]!.GetValue<int>());
        var after = (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter(null, null, null, null, 0, 50))).Items.ToDictionary(i => i.Code);
        Assert.Equal((before["E2"].Revision, before["E2"].NameEn, before["E2"].GroupId, before["E2"].UpdatedAtUtc), (after["E2"].Revision, after["E2"].NameEn, after["E2"].GroupId, after["E2"].UpdatedAtUtc));                                                                                          // the existing item was not touched
        Assert.Equal((before["E1"].Revision, before["E1"].UpdatedAtUtc), (after["E1"].Revision, after["E1"].UpdatedAtUtc));
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal(events + 1, await InventoryEvents());
    }

    [PostgresFact]
    public async Task Existing_units_and_groups_are_exact_or_skippable_conflicts_and_group_hierarchies_are_validated()
    {
        await Prepare();
        await Foundation();
        var uv = await Validate(InventoryUnitsImportAdapter.AdapterCode, Bytes(UnitHeader + "PCS,قطعة,Piece,0\nKG,كيلو,Kilogram,3\nNEW,,New,2\n"), UnitMap);
        var um = await RowMap(uv.Id);
        Assert.Equal(((short)RowOutcome.Exact, (short)RowOutcome.Conflict, (short)RowOutcome.Valid), (um[2].Status, um[3].Status, um[4].Status));
        Assert.Contains("record_exists_with_differences", Codes(um[3]));
        Assert.Equal(4L, await Count(U.Table));
        var rows =
            "FOOD,أغذية,Food,,fifo\n" +             // exact
            "TOOLS,,Tools,FOOD,\n" +                // exists with a different parent: conflict
            "C1,,Cycle one,C2,\n" +
            "C2,,Cycle two,C1,\n" +
            "ORPHAN,,Orphan,MISSING,\n" +
            "SELF,,Self,SELF,\n" +
            "BADVAL,,Bad,,weird\n" +
            "OK,,Child of existing,FRUIT,average\n";
        var gv = await Validate(InventoryGroupsImportAdapter.AdapterCode, Bytes(GroupHeader + rows), GroupMap);
        var gm = await RowMap(gv.Id);
        Assert.Equal((short)RowOutcome.Exact, gm[2].Status);
        Assert.Equal((short)RowOutcome.Conflict, gm[3].Status);
        Assert.Contains("group_parent_cycle", Codes(gm[4]));
        Assert.Contains("group_parent_cycle", Codes(gm[5]));
        Assert.Contains("group_parent_not_found", Codes(gm[6]));
        Assert.Contains("group_parent_is_self", Codes(gm[7]));
        Assert.NotEmpty(Codes(gm[8]));
        Assert.Equal((short)RowOutcome.Valid, gm[9].Status);
        Assert.False(gv.Plan!["canApply"]!.GetValue<bool>());
    }

    [PostgresFact]
    public async Task A_group_tree_deeper_than_eight_levels_is_refused_by_the_analysis()
    {
        await Prepare();
        var rows = string.Concat(Enumerable.Range(1, 9).Select(i => $"L{i},,Level {i},{(i == 1 ? "" : "L" + (i - 1))},\n"));
        var v = await Validate(InventoryGroupsImportAdapter.AdapterCode, Bytes(GroupHeader + rows), GroupMap);
        var map = await RowMap(v.Id);
        Assert.Contains("group_tree_too_deep", Codes(map[10]));
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------------------------------------------------------- atomicity, staleness, permissions, isolation, files

    [PostgresFact]
    public async Task A_failure_during_application_rolls_everything_back_and_the_same_approved_batch_can_be_retried()
    {
        await Prepare();
        await Foundation();
        var v = await ValidateItems("R1,,One,goods,TOOLS,PCS,,,BOX=2,4006381333931,,,,\nR2,,Two,goods,TOOLS,PCS,,,,5901234123457,,,,\n");
        var approved = await Approve(v);
        var events = await InventoryEvents();
        Failing.Fail = true;
        var failed = await IMg.ApplyAsync(Admin.Id, Company.Id, approved.Id, approved.Revision);
        Assert.Equal((ConfigStatus.Conflict, "simulated_failure"), (failed.Status, failed.Code));
        Assert.Equal((0L, 0L, events), (await Count(I.Table), await Count(BC.Table), await InventoryEvents()));                          // not one item, barcode, alternate unit or audit event remains
        Assert.Equal(ImportStatus.Failed, (await Reload(approved.Id)).Status);
        Failing.Fail = false;
        var f = await Reload(approved.Id);
        var applied = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, f.Id, f.Revision));
        Assert.Equal((ImportStatus.Applied, 2L, 2L), (applied.Status, await Count(I.Table), await Count(BC.Table)));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_plan_goes_stale_when_the_data_it_depends_on_changes_and_nothing_is_applied()
    {
        await Prepare();
        await Foundation();
        var v = await ValidateItems("S1,,One,goods,TOOLS,PCS,,,,4006381333931,,,,\n");
        var a = await Approve(v);
        var tools = (await Inv.ListGroupsAsync(Company.Id)).Single(g => g.Code == "TOOLS");
        var pcs = (await Inv.ListUnitsAsync(Company.Id)).Single(u => u.Code == "PCS");
        var other = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput("OTHER", null, "Other", 1, tools.Id, pcs.Id, null, null, false, false, false, null, [])));
        Ok(await Inv.AddBarcodeAsync(Admin.Id, Company.Id, other.Id, new BarcodeInput("4006381333931", null, null)));                      // somebody takes the barcode after the dry run
        var r = await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision);
        Assert.False(r.IsOk);
        Assert.Equal(1L, await Count(I.Table));                                                                                          // only OTHER exists
    }

    [PostgresFact]
    public async Task Permissions_are_checked_on_the_server_for_upload_view_and_application()
    {
        await Prepare();
        await Foundation();
        var (viewer, _) = await Member([PermissionCatalog.Codes.ItemsView, PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsUpload], ScopeKind.Company, null);
        var denied = await IMg.UploadAsync(viewer.Id, Company.Id, InventoryItemsImportAdapter.AdapterCode, "x.csv", Bytes(ItemHeader), null);
        Assert.False(denied.IsOk);                                                                                                       // no company.items.create: the adapter refuses
        var (creator, _) = await Member([PermissionCatalog.Codes.ItemsCreate, PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsView], ScopeKind.Company, null);
        var ok = await IMg.UploadAsync(creator.Id, Company.Id, InventoryItemsImportAdapter.AdapterCode, "x.csv", Bytes(ItemHeader + "P1,,P,goods,TOOLS,PCS,,,,,,,,\n"), null);
        Assert.True(ok.IsOk, ok.Code);
        var units = await IMg.UploadAsync(creator.Id, Company.Id, InventoryUnitsImportAdapter.AdapterCode, "u.csv", Bytes(UnitHeader), null);
        Assert.False(units.IsOk);                                                                                                        // the items permission does not open the units import
        var v = await ValidateItems("P2,,P,goods,TOOLS,PCS,,,,,,,,\n");
        var a = await Approve(v);
        Assert.False((await IMg.ApplyAsync(creator.Id, Company.Id, a.Id, a.Revision)).IsOk);                                            // creator lacks company.imports.apply
        Assert.Equal(0L, await Count(I.Table));
    }

    [PostgresFact]
    public async Task An_import_in_one_company_never_reaches_another_company()
    {
        await Prepare();
        await Foundation();
        var other = await ReadyCompany();
        var v = await ValidateItems("ISO,,Isolated,goods,TOOLS,PCS,,,,4006381333931,,,,\n");
        var a = await Approve(v);
        Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
        Assert.Equal(1L, await Count(I.Table));
        Assert.Equal(0L, await InCompany<long>(other.PhysicalDatabaseName, $"SELECT count(*) FROM {I.Table}"));
        Assert.Equal(0L, await InCompany<long>(other.PhysicalDatabaseName, $"SELECT count(*) FROM {BC.Table}"));
        Assert.Equal(0L, await InCompany<long>(other.PhysicalDatabaseName, $"SELECT count(*) FROM {U.Table}"));
    }

    [PostgresFact]
    public async Task Malformed_files_and_formula_cells_are_rejected_before_anything_is_staged()
    {
        await Prepare();
        await Foundation();
        // a CSV that is binary garbage, a CSV without rows, and an XLSX that is not a workbook
        var garbage = await Imports.UploadAsync(Admin.Id, Company.Id, InventoryItemsImportAdapter.AdapterCode, "x.csv", [0x00, 0x01, 0xFF, 0xFE, 0x00, 0x00], null);
        if (garbage.IsOk) Assert.False((await Imports.StageAsync(Admin.Id, Company.Id, garbage.Value!.Id, garbage.Value.Revision, new StageInput(0, 1, "utf-8", null))).IsOk);
        var notBook = await Imports.UploadAsync(Admin.Id, Company.Id, InventoryItemsImportAdapter.AdapterCode, "x.xlsx", Bytes("this is not a zip"), null);
        Assert.False(notBook.IsOk && (await Imports.StageAsync(Admin.Id, Company.Id, notBook.Value!.Id, notBook.Value.Revision, new StageInput(0, 1, null, null))).IsOk);
        // a text cell that merely LOOKS like a formula is data, never executed: it imports as that exact text
        var v = await ValidateItems("F1,\"=1+1\",\"=HYPERLINK(\"\"x\"\")\",goods,TOOLS,PCS,,,,,,,,\"@SUM(A1)\"\n");
        Assert.True(v.Plan!["canApply"]!.GetValue<bool>());
        var a = await Approve(v);
        Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
        var item = (await Inv.ListItemsAsync(Company.Id, new CompanyInventoryService.ItemFilter("F1", null, null, null, 0, 5))).Items.Single();
        Assert.Equal(("=1+1", "=HYPERLINK(\"x\")", "@SUM(A1)"), (item.NameAr, item.NameEn, item.Notes));
        Assert.Equal(1L, await Count(I.Table));
    }
}
