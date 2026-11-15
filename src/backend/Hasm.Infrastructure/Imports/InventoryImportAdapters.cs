using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemGroup;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using IU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemUnit;
using BC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemBarcode;
using S = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventorySettings;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Inventory master-data imports (Phase 3.4B). Three adapters on the generic import framework, one per kind of data, to be imported in this order because every reference must ALREADY exist when a file is
/// analysed: <b>units</b>, then <b>item groups</b>, then <b>items</b> (with alternate units, conversion factors, barcodes and tracking flags). Nothing here imports stock quantities, values, opening
/// balances, accounting entries or movements.
/// <para><b>What an import can do:</b> CREATE new records or explicitly SKIP a row. It never overwrites, merges, re-codes, moves or deactivates an existing record (the same owner decision as the chart import).
/// A code that already exists is an <i>exact</i> match when everything in the row equals the stored record (nothing to do) and a <i>conflict</i> (resolution: skip) when anything differs.</para>
/// <para><b>Application</b> is atomic: every record is created through the module's own audited domain operations (<see cref="CompanyInventoryService"/> <c>*InWorkAsync</c>) in one transaction, after
/// the same advisory lock every inventory writer takes, so all domain rules, PostgreSQL invariants and audit events of an ordinary creation still apply and nothing partial can remain.</para>
/// Codes and barcodes are text: leading zeros are preserved exactly (they are only trimmed, normalised to upper case and, for digits, from Arabic-Indic to Latin).
/// </summary>
internal static class InventoryImportSupport
{
    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public static async Task<string> HashAsync(StringBuilder sb) => await Task.FromResult(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant());

    public sealed record UnitRow(Guid Id, string Code, string? NameAr, string? NameEn, int Decimals, bool IsActive);
    public sealed record GroupRow(Guid Id, string Code, string? NameAr, string? NameEn, string? ParentCode, int? Valuation, bool IsActive);

    public static async Task<Dictionary<string, UnitRow>> LoadUnitsAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        var map = new Dictionary<string, UnitRow>(StringComparer.Ordinal);
        await using var q = new NpgsqlCommand($"SELECT {U.Id}, {U.Code}, {U.NameAr}, {U.NameEn}, {U.Decimals}, {U.IsActive} FROM {U.Table} ORDER BY {U.Code}", c.Connection, c.Transaction);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(1)] = new UnitRow(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt16(4), r.GetBoolean(5));
        return map;
    }

    public static async Task<Dictionary<string, GroupRow>> LoadGroupsAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        var map = new Dictionary<string, GroupRow>(StringComparer.Ordinal);
        await using var q = new NpgsqlCommand($"SELECT g.{G.Id}, g.{G.Code}, g.{G.NameAr}, g.{G.NameEn}, p.{G.Code}, g.{G.Valuation}, g.{G.IsActive} FROM {G.Table} g LEFT JOIN {G.Table} p ON p.{G.Id} = g.{G.ParentId} ORDER BY g.{G.Code}", c.Connection, c.Transaction);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(1)] = new GroupRow(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt16(5), r.GetBoolean(6));
        return map;
    }

    public static async Task<int?> LoadDefaultValuationAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {S.DefaultValuation} FROM {S.Table}", c.Connection, c.Transaction);
        return await q.ExecuteScalarAsync(ct) is short v ? v : null;
    }

    public static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    public static ImportApplyException Apply(int rowNo, InventoryOperationException ex) =>
        new(ex.Code, $"row {rowNo}: {ex.Code}" + (ex.Fields is { Count: > 0 } f ? " (" + string.Join(',', f.Select(x => $"{x.Field}:{x.Code}")) + ")" : ""));

    public static Task LockAsync(CompanyInventoryService inventory, CompanyAuditedWork work, CancellationToken ct) => inventory.LockForImportAsync(work, ct);
}

// ====================================================================================================================================== units

public sealed class InventoryUnitsImportAdapter(CompanyInventoryService inventory) : IImportAdapter
{
    public const string AdapterCode = "inventory-units";
    public string Code => AdapterCode;
    public int Version => 1;
    public string NameAr => "وحدات القياس";
    public string NameEn => "Units of measure";
    public string UploadPermission => PermissionCatalog.Codes.UnitsCreate;
    public string ViewPermission => PermissionCatalog.Codes.UnitsView;
    public bool Atomic => true;
    public IReadOnlyList<string> Resolutions => [ImportResolutions.Skip];

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "رمز الوحدة", "Unit code", ImportFieldType.Code, Required: true, MaxLength: 20, IsKey: true, UpperCase: true, Aliases: ["unit", "code", "unit code", "الرمز", "رمز الوحدة", "كود الوحدة"]),
        new("name_ar", "الاسم بالعربية", "Arabic name", ImportFieldType.Text, MaxLength: 100, Aliases: ["arabic", "الاسم العربي", "اسم الوحدة"]),
        new("name_en", "الاسم بالإنجليزية", "English name", ImportFieldType.Text, MaxLength: 100, Aliases: ["english", "الاسم الانجليزي", "الاسم الإنجليزي", "unit name"]),
        new("decimals", "الخانات العشرية", "Decimal places", ImportFieldType.Integer, Required: true, Aliases: ["decimals", "precision", "الخانات العشرية", "عدد الخانات العشرية"]),
    ];

    public async Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        var existing = await InventoryImportSupport.LoadUnitsAsync(context, ct);
        var answers = new List<RowAnalysis>();
        int create = 0, exact = 0;
        foreach (var row in rows.OrderBy(r => r.RowNo))
        {
            var input = new UnitInput(row.Get("code"), row.Get("name_ar"), row.Get("name_en"), int.TryParse(row.Get("decimals"), NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : null);
            if (!InventoryValidation.TryNormalizeUnit(input, null, out var d, out var errors))
            {
                answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Invalid, [.. errors.Select(e => new RowIssue(e.Field switch { "nameAr" => "name_ar", "nameEn" => "name_en", var f => f }, e.Code))]));
                continue;
            }
            if (existing.TryGetValue(d.Code, out var e))
            {
                var diffs = new List<string>();
                if (!string.Equals(d.NameAr, e.NameAr, StringComparison.Ordinal)) diffs.Add("name_ar");
                if (!string.Equals(d.NameEn, e.NameEn, StringComparison.Ordinal)) diffs.Add("name_en");
                if (d.Decimals != e.Decimals) diffs.Add("decimals");
                if (diffs.Count == 0) { exact++; answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Exact, Explanation: "exists_identical")); }
                else answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Conflict, [new RowIssue("code", "record_exists_with_differences", string.Join(',', diffs))], [ImportResolutions.Skip], Explanation: "record_exists_with_differences:" + string.Join(',', diffs)));
                continue;
            }
            create++;
            answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Valid, Operation: new JsonObject { ["type"] = "create-unit", ["code"] = d.Code, ["nameAr"] = d.NameAr, ["nameEn"] = d.NameEn, ["decimals"] = d.Decimals }, Explanation: "will_be_created"));
        }
        var sb = new StringBuilder();
        foreach (var e in existing.Values.OrderBy(e => e.Code, StringComparer.Ordinal)) sb.Append(e.Code).Append('|').Append(e.NameAr).Append('|').Append(e.NameEn).Append('|').Append(e.Decimals).Append('|').Append(e.IsActive ? 1 : 0).Append('\n');
        return new AnalysisResult(answers, await InventoryImportSupport.HashAsync(sb), [new("units_to_create", InventoryImportSupport.Num(create)), new("matched_exactly", InventoryImportSupport.Num(exact)), new("already_existing", InventoryImportSupport.Num(existing.Count))]);
    }

    public Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => InventoryImportSupport.LockAsync(inventory, work, ct);

    public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
    {
        var created = 0;
        foreach (var op in context.Operations.OrderBy(o => o.Sequence).ThenBy(o => o.RowNo))
        {
            var o = op.Operation;
            try { await inventory.CreateUnitInWorkAsync(context.Work, context.Actor, new UnitInput(o["code"]!.GetValue<string>(), o["nameAr"]?.GetValue<string>(), o["nameEn"]?.GetValue<string>(), o["decimals"]!.GetValue<int>()), ct); created++; }
            catch (InventoryOperationException ex) { throw InventoryImportSupport.Apply(op.RowNo, ex); }
        }
        return new ImportApplyResult(new JsonObject { ["created"] = created });
    }
}

// ====================================================================================================================================== groups

public sealed class InventoryGroupsImportAdapter(CompanyInventoryService inventory) : IImportAdapter
{
    public const string AdapterCode = "inventory-groups";
    public string Code => AdapterCode;
    public int Version => 1;
    public string NameAr => "مجموعات المواد";
    public string NameEn => "Item groups";
    public string UploadPermission => PermissionCatalog.Codes.ItemGroupsCreate;
    public string ViewPermission => PermissionCatalog.Codes.ItemGroupsView;
    public bool Atomic => true;
    public IReadOnlyList<string> Resolutions => [ImportResolutions.Skip];

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "رمز المجموعة", "Group code", ImportFieldType.Code, Required: true, MaxLength: 20, IsKey: true, UpperCase: true, Aliases: ["group", "code", "group code", "الرمز", "رمز المجموعة", "كود المجموعة"]),
        new("name_ar", "الاسم بالعربية", "Arabic name", ImportFieldType.Text, MaxLength: 200, Aliases: ["arabic", "الاسم العربي", "اسم المجموعة"]),
        new("name_en", "الاسم بالإنجليزية", "English name", ImportFieldType.Text, MaxLength: 200, Aliases: ["english", "الاسم الانجليزي", "الاسم الإنجليزي", "group name"]),
        new("parent_code", "رمز المجموعة الأم", "Parent group code", ImportFieldType.Code, MaxLength: 20, UpperCase: true, Aliases: ["parent", "parent group", "المجموعة الأم", "المجموعة الام", "الأب"]),
        new("valuation", "طريقة التقييم", "Valuation method", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]>
        {
            ["fifo"] = ["first in first out", "الوارد أولاً صادر أولاً", "الوارد اولا صادر اولا"], ["average"] = ["moving average", "weighted average", "avg", "المتوسط", "المتوسط المرجح", "المتوسط المتحرك"], ["inherit"] = ["", "inherited", "وراثة", "موروث"],
        }, Aliases: ["valuation method", "costing", "طريقة التقييم"]),
    ];

    private sealed class Draft(NormalizedRow row)
    {
        public NormalizedRow Row { get; } = row;
        public string Code = "";
        public string? ParentCode, NameAr, NameEn;
        public ValuationMethod? Valuation;
        public List<RowIssue> Issues = [];
        public RowOutcome Outcome = RowOutcome.Valid;
        public int Depth;
        public string? Explanation;
    }

    public async Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        var existing = await InventoryImportSupport.LoadGroupsAsync(context, ct);
        var drafts = new Dictionary<int, Draft>();
        foreach (var row in rows)
        {
            var d = new Draft(row);
            var valuation = row.Get("valuation") switch { "fifo" => 1, "average" => 2, _ => (int?)null };
            if (!InventoryValidation.TryNormalizeGroup(new ItemGroupInput(row.Get("code"), row.Get("name_ar"), row.Get("name_en"), null, valuation), null, out var n, out var errors))
            {
                d.Code = (row.Get("code") ?? "").ToUpperInvariant(); d.Outcome = RowOutcome.Invalid;
                d.Issues.AddRange(errors.Select(e => new RowIssue(e.Field switch { "nameAr" => "name_ar", "nameEn" => "name_en", var f => f }, e.Code)));
            }
            else
            {
                d.Code = n.Code; d.NameAr = n.NameAr; d.NameEn = n.NameEn; d.Valuation = n.Valuation;
                if (row.Get("parent_code") is { Length: > 0 } p)
                {
                    if (p == d.Code) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("parent_code", "group_parent_is_self")); } else d.ParentCode = p;
                }
            }
            drafts[row.RowNo] = d;
        }
        var byCode = new Dictionary<string, Draft>(StringComparer.Ordinal);
        foreach (var d in drafts.Values.OrderBy(d => d.Row.RowNo)) if (d.Code.Length > 0) byCode.TryAdd(d.Code, d);

        // existing codes: exact or conflict
        foreach (var d in drafts.Values.Where(d => d.Outcome == RowOutcome.Valid && existing.ContainsKey(d.Code)))
        {
            var e = existing[d.Code];
            var diffs = new List<string>();
            if (!string.Equals(d.NameAr, e.NameAr, StringComparison.Ordinal)) diffs.Add("name_ar");
            if (!string.Equals(d.NameEn, e.NameEn, StringComparison.Ordinal)) diffs.Add("name_en");
            if (!string.Equals(d.ParentCode, e.ParentCode, StringComparison.Ordinal)) diffs.Add("parent_code");
            if ((d.Valuation is { } v ? (int?)v : null) != e.Valuation) diffs.Add("valuation");
            if (diffs.Count == 0) { d.Outcome = RowOutcome.Exact; d.Explanation = "exists_identical"; }
            else { d.Outcome = RowOutcome.Conflict; d.Issues.Add(new RowIssue("code", "record_exists_with_differences", string.Join(',', diffs))); d.Explanation = "record_exists_with_differences:" + string.Join(',', diffs); }
        }

        // hierarchy of the rows to create: cycles, parents, depth (iterative: a hostile file may nest very deeply)
        var candidates = drafts.Values.Where(d => d.Outcome == RowOutcome.Valid).OrderBy(d => d.Row.RowNo).ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in candidates)
        {
            if (done.Contains(start.Code)) continue;
            var path = new List<Draft>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var cur = start; cur is not null && !done.Contains(cur.Code);)
            {
                if (index.TryGetValue(cur.Code, out var at)) { for (var i = at; i < path.Count; i++) Fail(path[i], "parent_code", "group_parent_cycle"); break; }
                index[cur.Code] = path.Count; path.Add(cur);
                cur = cur.ParentCode is { } pc && !existing.ContainsKey(pc) && byCode.TryGetValue(pc, out var next) ? next : null;
            }
            foreach (var p in path) done.Add(p.Code);
        }
        done.Clear();
        foreach (var d in candidates)
        {
            var stack = new Stack<Draft>();
            for (var cur = d; cur is not null && !done.Contains(cur.Code);)
            {
                stack.Push(cur);
                cur = cur.ParentCode is { } pc && !existing.ContainsKey(pc) && byCode.TryGetValue(pc, out var next) && next.Outcome == RowOutcome.Valid ? next : null;
            }
            while (stack.Count > 0) { var x = stack.Pop(); Resolve(x, byCode, existing); done.Add(x.Code); }
        }
        // a parent row that is invalid, skipped or undecided means the child cannot be created
        bool Skipped(Draft p) => p.Outcome == RowOutcome.Conflict && p.Row.Resolution == ImportResolutions.Skip;
        foreach (var d in candidates.OrderBy(d => d.Depth).ThenBy(d => d.Row.RowNo))
        {
            if (d.Outcome != RowOutcome.Valid || d.ParentCode is null || existing.ContainsKey(d.ParentCode) || !byCode.TryGetValue(d.ParentCode, out var p)) continue;
            if (p.Outcome == RowOutcome.Invalid) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("parent_code", "group_parent_row_invalid", d.ParentCode)); }
            else if (p.Outcome == RowOutcome.Conflict) { var code = Skipped(p) ? "group_parent_row_skipped" : "group_parent_row_unresolved"; d.Outcome = RowOutcome.Conflict; d.Issues.Add(new RowIssue("parent_code", code, d.ParentCode)); d.Explanation = code; }
        }

        var answers = new List<RowAnalysis>();
        int create = 0, exact = 0;
        foreach (var d in drafts.Values.OrderBy(d => d.Row.RowNo))
        {
            switch (d.Outcome)
            {
                case RowOutcome.Valid:
                    create++;
                    answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Valid, Operation: new JsonObject
                    { ["type"] = "create-group", ["code"] = d.Code, ["parentCode"] = d.ParentCode, ["nameAr"] = d.NameAr, ["nameEn"] = d.NameEn, ["valuation"] = d.Valuation is { } v ? (int)v : null }, Explanation: "will_be_created", Sequence: d.Depth));
                    break;
                case RowOutcome.Exact: exact++; answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Exact, Explanation: d.Explanation)); break;
                case RowOutcome.Conflict: answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Conflict, d.Issues, [ImportResolutions.Skip], Explanation: d.Explanation)); break;
                default: answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Invalid, d.Issues)); break;
            }
        }
        var sb = new StringBuilder();
        foreach (var e in existing.Values.OrderBy(e => e.Code, StringComparer.Ordinal)) sb.Append(e.Code).Append('|').Append(e.ParentCode).Append('|').Append(e.NameAr).Append('|').Append(e.NameEn).Append('|').Append(e.Valuation).Append('|').Append(e.IsActive ? 1 : 0).Append('\n');
        return new AnalysisResult(answers, await InventoryImportSupport.HashAsync(sb), [new("item_groups_to_create", InventoryImportSupport.Num(create)), new("matched_exactly", InventoryImportSupport.Num(exact)), new("already_existing", InventoryImportSupport.Num(existing.Count))]);
    }

    private static void Fail(Draft d, string field, string code) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue(field, code, d.ParentCode)); }

    private static void Resolve(Draft d, Dictionary<string, Draft> byCode, Dictionary<string, InventoryImportSupport.GroupRow> existing)
    {
        if (d.Outcome != RowOutcome.Valid) return;
        if (d.ParentCode is null) { d.Depth = 0; return; }
        if (existing.TryGetValue(d.ParentCode, out var e))
        {
            if (!e.IsActive) { Fail(d, "parent_code", "group_parent_inactive"); return; }
            d.Depth = 0;
            return;
        }
        if (!byCode.TryGetValue(d.ParentCode, out var p)) { Fail(d, "parent_code", "group_parent_not_found"); return; }
        d.Depth = p.Depth + 1;
        if (d.Depth + 1 > 8) Fail(d, "parent_code", "group_tree_too_deep");
    }

    public Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => InventoryImportSupport.LockAsync(inventory, work, ct);

    public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var created = 0;
        foreach (var op in context.Operations.OrderBy(o => o.Sequence).ThenBy(o => o.RowNo))
        {
            var o = op.Operation;
            Guid? parentId = null;
            if (o["parentCode"]?.GetValue<string>() is { } pc)
            {
                if (ids.TryGetValue(pc, out var pid)) parentId = pid;
                else parentId = (await inventory.FindGroupIdInWorkAsync(context.Work, pc, ct)) ?? throw new ImportApplyException("parent_missing", $"row {op.RowNo}: the parent group does not exist");
            }
            try
            {
                var g = await inventory.CreateGroupInWorkAsync(context.Work, context.Actor, new ItemGroupInput(o["code"]!.GetValue<string>(), o["nameAr"]?.GetValue<string>(), o["nameEn"]?.GetValue<string>(), parentId, o["valuation"]?.GetValue<int>()), ct);
                ids[g.Code] = g.Id; created++;
            }
            catch (InventoryOperationException ex) { throw InventoryImportSupport.Apply(op.RowNo, ex); }
        }
        return new ImportApplyResult(new JsonObject { ["created"] = created });
    }
}

// ====================================================================================================================================== items

public sealed class InventoryItemsImportAdapter(CompanyInventoryService inventory) : IImportAdapter
{
    public const string AdapterCode = "inventory-items";
    public string Code => AdapterCode;
    public int Version => 1;
    public string NameAr => "المواد والخدمات";
    public string NameEn => "Items and services";
    public string UploadPermission => PermissionCatalog.Codes.ItemsCreate;
    public string ViewPermission => PermissionCatalog.Codes.ItemsView;
    public bool Atomic => true;
    public IReadOnlyList<string> Resolutions => [ImportResolutions.Skip];

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "رمز المادة", "Item code", ImportFieldType.Code, Required: true, MaxLength: 30, IsKey: true, UpperCase: true, Aliases: ["item", "item code", "code", "sku", "الرمز", "رمز المادة", "كود المادة"]),
        new("name_ar", "الاسم بالعربية", "Arabic name", ImportFieldType.Text, MaxLength: 200, Aliases: ["arabic", "الاسم العربي", "اسم المادة", "الوصف"]),
        new("name_en", "الاسم بالإنجليزية", "English name", ImportFieldType.Text, MaxLength: 200, Aliases: ["english", "name", "description", "الاسم الانجليزي", "الاسم الإنجليزي", "item name"]),
        new("type", "النوع", "Type", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]>
        {
            ["goods"] = ["stock", "stock item", "item", "مادة", "مادة مخزنية", "بضاعة", "صنف"], ["service"] = ["services", "خدمة", "خدمات"],
        }, Aliases: ["item type", "نوع المادة"]),
        new("group_code", "رمز المجموعة", "Group code", ImportFieldType.Code, Required: true, MaxLength: 20, UpperCase: true, Aliases: ["group", "item group", "المجموعة", "رمز المجموعة"]),
        new("base_unit_code", "رمز الوحدة الأساسية", "Base unit code", ImportFieldType.Code, Required: true, MaxLength: 20, UpperCase: true, Aliases: ["base unit", "unit", "uom", "الوحدة", "الوحدة الأساسية"]),
        new("purchase_unit_code", "رمز وحدة الشراء", "Purchase unit code", ImportFieldType.Code, MaxLength: 20, UpperCase: true, Aliases: ["purchase unit", "وحدة الشراء"]),
        new("sales_unit_code", "رمز وحدة البيع", "Sales unit code", ImportFieldType.Code, MaxLength: 20, UpperCase: true, Aliases: ["sales unit", "sale unit", "وحدة البيع"]),
        new("alternate_units", "الوحدات البديلة", "Alternate units", ImportFieldType.Text, MaxLength: 500, Aliases: ["alternate units", "alt units", "conversions", "الوحدات البديلة", "التحويلات"]),
        new("barcodes", "الباركود", "Barcodes", ImportFieldType.Text, MaxLength: 1000, Aliases: ["barcode", "barcodes", "ean", "الباركود", "باركود"]),
        new("is_lot", "تتبع بالدفعات", "Track by lot", ImportFieldType.Boolean, Aliases: ["lot", "lot tracking", "تتبع بالدفعات"]),
        new("is_serial", "تتبع بالأرقام التسلسلية", "Track by serial", ImportFieldType.Boolean, Aliases: ["serial", "serial tracking", "تتبع بالأرقام التسلسلية"]),
        new("has_expiry", "تاريخ انتهاء الصلاحية", "Has expiry", ImportFieldType.Boolean, Aliases: ["expiry", "expiry date", "صلاحية", "تاريخ انتهاء الصلاحية"]),
        new("notes", "ملاحظات", "Notes", ImportFieldType.Text, MaxLength: 1000, Aliases: ["note", "ملاحظات"]),
    ];

    private sealed record BarcodeToken(string Barcode, string? UnitCode, BarcodeKind Kind);
    private sealed record ExistingItem(Guid Id, string Code, string? NameAr, string? NameEn, int Type, string GroupCode, string BaseUnit, string? Purchase, string? Sales, bool Lot, bool Serial, bool Expiry, string? Notes,
        List<(string Unit, decimal Factor)> Alternates, HashSet<string> Barcodes);

    private sealed class Draft(NormalizedRow row)
    {
        public NormalizedRow Row { get; } = row;
        public string Code = "";
        public string? NameAr, NameEn, GroupCode, BaseUnit, Purchase, Sales, Notes;
        public ItemType Type = ItemType.Goods;
        public bool Lot, Serial, Expiry;
        public List<(string Unit, decimal Factor)> Alternates = [];
        public List<BarcodeToken> Barcodes = [];
        public List<RowIssue> Issues = [];
        public RowOutcome Outcome = RowOutcome.Valid;
        public string? Explanation;
    }

    public async Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        var units = await InventoryImportSupport.LoadUnitsAsync(context, ct);
        var groups = await InventoryImportSupport.LoadGroupsAsync(context, ct);
        var defaultValuation = await InventoryImportSupport.LoadDefaultValuationAsync(context, ct);
        var (existing, barcodeOwner) = await LoadExistingAsync(context, ct);
        var drafts = rows.Select(r => Validate(r, units, groups, defaultValuation)).ToList();

        // a barcode used by two rows of the file: every row after the first that carries it is invalid (the first keeps it)
        var firstUse = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in drafts.OrderBy(d => d.Row.RowNo).Where(d => d.Outcome == RowOutcome.Valid))
            foreach (var b in d.Barcodes)
                if (!firstUse.TryAdd(b.Barcode, d.Row.RowNo) && firstUse[b.Barcode] != d.Row.RowNo) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("barcodes", "barcode_duplicate_in_file", b.Barcode)); }

        foreach (var d in drafts.Where(d => d.Outcome == RowOutcome.Valid))
        {
            if (existing.TryGetValue(d.Code, out var e))
            {
                var diffs = Differences(d, e);
                if (diffs.Count == 0) { d.Outcome = RowOutcome.Exact; d.Explanation = "exists_identical"; }
                else { d.Outcome = RowOutcome.Conflict; d.Issues.Add(new RowIssue("code", "record_exists_with_differences", string.Join(',', diffs))); d.Explanation = "record_exists_with_differences:" + string.Join(',', diffs); }
                continue;
            }
            foreach (var b in d.Barcodes)
                if (barcodeOwner.TryGetValue(b.Barcode, out var owner)) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("barcodes", "barcode_taken", b.Barcode + ":" + owner)); }
        }

        var answers = new List<RowAnalysis>();
        int goods = 0, services = 0, barcodes = 0, alternates = 0, exact = 0;
        foreach (var d in drafts.OrderBy(d => d.Row.RowNo))
        {
            switch (d.Outcome)
            {
                case RowOutcome.Valid:
                    if (d.Type == ItemType.Goods) goods++; else services++;
                    barcodes += d.Barcodes.Count; alternates += d.Alternates.Count;
                    answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Valid, Operation: Operation(d), Explanation: "will_be_created"));
                    break;
                case RowOutcome.Exact: exact++; answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Exact, Explanation: d.Explanation)); break;
                case RowOutcome.Conflict: answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Conflict, d.Issues, [ImportResolutions.Skip], Explanation: d.Explanation)); break;
                default: answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Invalid, d.Issues)); break;
            }
        }
        var sb = new StringBuilder();
        foreach (var e in existing.Values.OrderBy(e => e.Code, StringComparer.Ordinal))
            sb.Append(e.Code).Append('|').Append(e.NameAr).Append('|').Append(e.NameEn).Append('|').Append(e.Type).Append('|').Append(e.GroupCode).Append('|').Append(e.BaseUnit).Append('|').Append(e.Purchase).Append('|').Append(e.Sales).Append('|')
              .Append(e.Lot ? 1 : 0).Append(e.Serial ? 1 : 0).Append(e.Expiry ? 1 : 0).Append('|').Append(e.Notes).Append('|').Append(string.Join(',', e.Alternates.OrderBy(a => a.Unit, StringComparer.Ordinal).Select(a => a.Unit + "=" + a.Factor.ToString(CultureInfo.InvariantCulture))))
              .Append('|').Append(string.Join(',', e.Barcodes.Order(StringComparer.Ordinal))).Append('\n');
        sb.Append("units:");
        foreach (var u in units.Values.OrderBy(u => u.Code, StringComparer.Ordinal)) sb.Append(u.Code).Append(u.IsActive ? '+' : '-').Append(u.Decimals).Append(',');
        sb.Append("\ngroups:");
        foreach (var g in groups.Values.OrderBy(g => g.Code, StringComparer.Ordinal)) sb.Append(g.Code).Append(g.IsActive ? '+' : '-').Append(g.ParentCode).Append(g.Valuation).Append(',');
        sb.Append("\nvaluation:").Append(defaultValuation).Append("\nbarcodes:").Append(string.Join(',', barcodeOwner.Keys.Order(StringComparer.Ordinal)));
        return new AnalysisResult(answers, await InventoryImportSupport.HashAsync(sb),
        [
            new("items_to_create", InventoryImportSupport.Num(goods + services)), new("goods_to_create", InventoryImportSupport.Num(goods)), new("services_to_create", InventoryImportSupport.Num(services)),
            new("alternate_units_to_create", InventoryImportSupport.Num(alternates)), new("barcodes_to_create", InventoryImportSupport.Num(barcodes)), new("matched_exactly", InventoryImportSupport.Num(exact)), new("already_existing", InventoryImportSupport.Num(existing.Count)),
        ]);
    }

    private static List<string> Differences(Draft d, ExistingItem e)
    {
        var diffs = new List<string>();
        if (!string.Equals(d.NameAr, e.NameAr, StringComparison.Ordinal)) diffs.Add("name_ar");
        if (!string.Equals(d.NameEn, e.NameEn, StringComparison.Ordinal)) diffs.Add("name_en");
        if ((int)d.Type != e.Type) diffs.Add("type");
        if (!string.Equals(d.GroupCode, e.GroupCode, StringComparison.Ordinal)) diffs.Add("group_code");
        if (!string.Equals(d.BaseUnit, e.BaseUnit, StringComparison.Ordinal)) diffs.Add("base_unit_code");
        if (!string.Equals(d.Purchase, e.Purchase, StringComparison.Ordinal)) diffs.Add("purchase_unit_code");
        if (!string.Equals(d.Sales, e.Sales, StringComparison.Ordinal)) diffs.Add("sales_unit_code");
        if (d.Lot != e.Lot) diffs.Add("is_lot");
        if (d.Serial != e.Serial) diffs.Add("is_serial");
        if (d.Expiry != e.Expiry) diffs.Add("has_expiry");
        if (!string.Equals(d.Notes, e.Notes, StringComparison.Ordinal)) diffs.Add("notes");
        var same = d.Alternates.Count == e.Alternates.Count && d.Alternates.All(a => e.Alternates.Any(b => b.Unit == a.Unit && b.Factor == a.Factor));
        if (!same) diffs.Add("alternate_units");
        if (!d.Barcodes.All(b => e.Barcodes.Contains(b.Barcode))) diffs.Add("barcodes");
        return diffs;
    }

    private static Draft Validate(NormalizedRow row, Dictionary<string, InventoryImportSupport.UnitRow> units, Dictionary<string, InventoryImportSupport.GroupRow> groups, int? defaultValuation)
    {
        var d = new Draft(row) { Code = (row.Get("code") ?? "").ToUpperInvariant() };
        void Add(string field, string code, string? detail = null) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue(field, code, detail)); }
        var type = row.Get("type") == "service" ? ItemType.Service : ItemType.Goods;
        d.Type = type;
        d.GroupCode = row.Get("group_code"); d.BaseUnit = row.Get("base_unit_code"); d.Purchase = row.Get("purchase_unit_code"); d.Sales = row.Get("sales_unit_code");
        d.Lot = row.Get("is_lot") == "true"; d.Serial = row.Get("is_serial") == "true"; d.Expiry = row.Get("has_expiry") == "true";
        // shape checks reuse the domain validation (code shape, names, service tracking rules); references are checked against the real data below
        var probe = new ItemInput(row.Get("code"), row.Get("name_ar"), row.Get("name_en"), (int)type, Guid.NewGuid(), Guid.NewGuid(), null, null, d.Lot, d.Serial, d.Expiry, row.Get("notes"), []);
        if (!InventoryValidation.TryNormalizeItem(probe, null, out var n, out var errors))
            foreach (var e in errors) Add(e.Field switch { "nameAr" => "name_ar", "nameEn" => "name_en", "notes" => "notes", "itemType" => "type", var f => f }, e.Code);
        else { d.Code = n.Code ?? d.Code; d.NameAr = n.NameAr; d.NameEn = n.NameEn; d.Notes = n.Notes; }
        if (d.Code.Length == 0 && d.Outcome == RowOutcome.Valid) Add("code", "required");

        if (!groups.TryGetValue(d.GroupCode ?? "", out var group)) Add("group_code", "group_not_found", d.GroupCode);
        else
        {
            if (!group.IsActive) Add("group_code", "group_inactive", d.GroupCode);
            else if (type == ItemType.Goods)
            {
                var chain = new List<ValuationMethod?>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var cur = group; cur is not null && seen.Add(cur.Code); cur = cur.ParentCode is { } p && groups.TryGetValue(p, out var up) ? up : null) chain.Add(cur.Valuation is { } v ? (ValuationMethod)v : null);
                if (InventoryValidation.ResolveValuation(chain, defaultValuation is { } dv ? (ValuationMethod)dv : null) is null) Add("group_code", "valuation_method_unresolved", d.GroupCode);
            }
        }
        InventoryImportSupport.UnitRow? Unit(string? code, string field, bool allowInactive = false)
        {
            if (code is null) return null;
            if (!units.TryGetValue(code, out var u)) { Add(field, "unit_not_found", code); return null; }
            if (!u.IsActive && !allowInactive) { Add(field, "unit_inactive", code); return null; }
            return u;
        }
        var baseUnit = Unit(d.BaseUnit, "base_unit_code");

        // alternate units: UNIT=factor;UNIT=factor
        var seenUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Split(row.Get("alternate_units")))
        {
            var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
            var code = parts[0].ToUpperInvariant();
            if (parts.Length != 2 || code.Length == 0) { Add("alternate_units", "invalid_alternate_unit", token); continue; }
            if (code == d.BaseUnit) { Add("alternate_units", "equals_base_unit", code); continue; }
            if (!seenUnits.Add(code)) { Add("alternate_units", "duplicate_unit", code); continue; }
            if (Unit(code, "alternate_units") is null) continue;
            if (!decimal.TryParse(ImportNormalizer.Digits(parts[1]), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var factor) || !InventoryValidation.IsValidFactor(factor)) { Add("alternate_units", "invalid_factor", token); continue; }
            d.Alternates.Add((code, factor));
        }
        if (d.Alternates.Count > InventoryValidation.MaxAlternateUnits) Add("alternate_units", "too_many");
        foreach (var (code, field) in new[] { (d.Purchase, "purchase_unit_code"), (d.Sales, "sales_unit_code") })
        {
            if (code is null) continue;
            if (Unit(code, field) is null) continue;
            if (code != d.BaseUnit && d.Alternates.All(a => a.Unit != code)) Add(field, "unit_not_defined_for_item", code);
        }

        // barcodes: VALUE or VALUE@UNIT; leading zeros are kept exactly
        foreach (var token in Split(row.Get("barcodes")))
        {
            var parts = token.Split('@', 2, StringSplitOptions.TrimEntries);
            var raw = ImportNormalizer.Digits(parts[0]);
            if (!InventoryValidation.TryNormalizeBarcode(raw, null, out var barcode, out var kind, out var berr)) { foreach (var e in berr) Add("barcodes", e.Code, parts[0]); continue; }
            // a purely numeric code of a GS1 length must be a real EAN-8 / UPC-A / EAN-13: a wrong check digit is almost always a typo, never silently stored as an internal code
            if (raw.All(char.IsAsciiDigit) && raw.Length is 8 or 12 or 13 && !InventoryValidation.CheckDigitValid(raw)) { Add("barcodes", "invalid_check_digit", parts[0]); continue; }
            var unitCode = parts.Length == 2 && parts[1].Length > 0 ? parts[1].ToUpperInvariant() : null;
            if (unitCode is not null && unitCode != d.BaseUnit && d.Alternates.All(a => a.Unit != unitCode)) { Add("barcodes", "unit_not_defined_for_item", unitCode); continue; }
            if (d.Barcodes.Any(b => b.Barcode == barcode)) { Add("barcodes", "duplicate_barcode", barcode); continue; }
            d.Barcodes.Add(new BarcodeToken(barcode, unitCode, kind));
        }
        _ = baseUnit;
        return d;
    }

    private static IEnumerable<string> Split(string? cell) => (cell ?? "").Split([';', '\n', '|', '؛'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonObject Operation(Draft d) => new()
    {
        ["type"] = "create-item", ["code"] = d.Code, ["nameAr"] = d.NameAr, ["nameEn"] = d.NameEn, ["itemType"] = (int)d.Type, ["groupCode"] = d.GroupCode, ["baseUnitCode"] = d.BaseUnit,
        ["purchaseUnitCode"] = d.Purchase, ["salesUnitCode"] = d.Sales, ["isLot"] = d.Lot, ["isSerial"] = d.Serial, ["hasExpiry"] = d.Expiry, ["notes"] = d.Notes,
        ["alternates"] = new JsonArray([.. d.Alternates.Select(a => (JsonNode)new JsonObject { ["unitCode"] = a.Unit, ["factor"] = a.Factor.ToString(CultureInfo.InvariantCulture) })]),
        ["barcodes"] = new JsonArray([.. d.Barcodes.Select(b => (JsonNode)new JsonObject { ["barcode"] = b.Barcode, ["unitCode"] = b.UnitCode, ["kind"] = (int)b.Kind })]),
    };

    private static async Task<(Dictionary<string, ExistingItem> Items, Dictionary<string, string> BarcodeOwner)> LoadExistingAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        var items = new Dictionary<string, ExistingItem>(StringComparer.Ordinal);
        var byId = new Dictionary<Guid, ExistingItem>();
        await using (var q = new NpgsqlCommand(
            $"SELECT i.{I.Id}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, i.{I.ItemType}, g.{G.Code}, bu.{U.Code}, pu.{U.Code}, su.{U.Code}, i.{I.IsLot}, i.{I.IsSerial}, i.{I.HasExpiry}, i.{I.Notes} " +
            $"FROM {I.Table} i JOIN {G.Table} g ON g.{G.Id} = i.{I.GroupId} JOIN {U.Table} bu ON bu.{U.Id} = i.{I.BaseUnitId} LEFT JOIN {U.Table} pu ON pu.{U.Id} = i.{I.PurchaseUnitId} LEFT JOIN {U.Table} su ON su.{U.Id} = i.{I.SalesUnitId} ORDER BY i.{I.Code}", c.Connection, c.Transaction))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var e = new ExistingItem(r.GetGuid(0), r.GetString(1), S(2), S(3), r.GetInt16(4), r.GetString(5), r.GetString(6), S(7), S(8), r.GetBoolean(9), r.GetBoolean(10), r.GetBoolean(11), S(12), [], new HashSet<string>(StringComparer.Ordinal));
                items[e.Code] = e; byId[e.Id] = e;
            }
        await using (var q = new NpgsqlCommand($"SELECT iu.{IU.ItemId}, u.{U.Code}, iu.{IU.Factor} FROM {IU.Table} iu JOIN {U.Table} u ON u.{U.Id} = iu.{IU.UnitId}", c.Connection, c.Transaction))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) if (byId.TryGetValue(r.GetGuid(0), out var e)) e.Alternates.Add((r.GetString(1), r.GetDecimal(2)));
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var q = new NpgsqlCommand($"SELECT b.{BC.Barcode}, b.{BC.ItemId}, i.{I.Code} FROM {BC.Table} b JOIN {I.Table} i ON i.{I.Id} = b.{BC.ItemId}", c.Connection, c.Transaction))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) { owner[r.GetString(0).ToUpperInvariant()] = r.GetString(2); if (byId.TryGetValue(r.GetGuid(1), out var e)) e.Barcodes.Add(r.GetString(0).ToUpperInvariant()); }
        return (items, owner);
    }

    public Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => InventoryImportSupport.LockAsync(inventory, work, ct);

    public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
    {
        var work = context.Work;
        var units = await inventory.LoadUnitIdsInWorkAsync(work, ct);
        var groups = await inventory.LoadGroupIdsInWorkAsync(work, ct);
        int created = 0, alternates = 0, barcodes = 0;
        foreach (var op in context.Operations.OrderBy(o => o.Sequence).ThenBy(o => o.RowNo))
        {
            var o = op.Operation;
            Guid Unit(string code) => units.TryGetValue(code, out var id) ? id : throw new ImportApplyException("unit_missing", $"row {op.RowNo}: unit {code} does not exist");
            Guid? OptUnit(string key) => o[key]?.GetValue<string>() is { } c ? Unit(c) : null;
            var group = groups.TryGetValue(o["groupCode"]!.GetValue<string>(), out var gid) ? gid : throw new ImportApplyException("group_missing", $"row {op.RowNo}: the item group does not exist");
            var alts = o["alternates"]!.AsArray().Select(a => new ItemUnitInput(Unit(a!["unitCode"]!.GetValue<string>()), decimal.Parse(a["factor"]!.GetValue<string>(), CultureInfo.InvariantCulture))).ToList();
            var input = new ItemInput(o["code"]!.GetValue<string>(), o["nameAr"]?.GetValue<string>(), o["nameEn"]?.GetValue<string>(), o["itemType"]!.GetValue<int>(), group, Unit(o["baseUnitCode"]!.GetValue<string>()),
                OptUnit("purchaseUnitCode"), OptUnit("salesUnitCode"), o["isLot"]!.GetValue<bool>(), o["isSerial"]!.GetValue<bool>(), o["hasExpiry"]!.GetValue<bool>(), o["notes"]?.GetValue<string>(), alts);
            try
            {
                var item = await inventory.CreateItemInWorkAsync(work, context.Actor, input, ct);
                created++; alternates += alts.Count;
                foreach (var b in o["barcodes"]!.AsArray())
                {
                    await inventory.AddBarcodeInWorkAsync(work, context.Actor, item.Id, new BarcodeInput(b!["barcode"]!.GetValue<string>(), b["unitCode"]?.GetValue<string>() is { } uc ? Unit(uc) : null, b["kind"]!.GetValue<int>()), ct);
                    barcodes++;
                }
            }
            catch (InventoryOperationException ex) { throw InventoryImportSupport.Apply(op.RowNo, ex); }
        }
        return new ImportApplyResult(new JsonObject { ["created"] = created, ["alternateUnits"] = alternates, ["barcodes"] = barcodes });
    }
}
