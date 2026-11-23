using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Imports;
using Microsoft.Extensions.DependencyInjection;
using Ib = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using Ir = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportRow;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// A test-only adapter for "items": it proves the framework works for a domain that is NOT the chart of accounts. Existing records live in this object (standing in for business tables), so a test can
/// show that analysis only ever READS them.
/// </summary>
public sealed class ItemsTestAdapter : IImportAdapter
{
    public Dictionary<string, (string Name, string Kind)> Existing { get; } = new(StringComparer.Ordinal);
    public int Analyses; public bool Throw;
    public string Code => "items"; public int Version => 1; public string NameAr => "أصناف"; public string NameEn => "Items";
    public string UploadPermission => "company.accounts.create"; public string ViewPermission => "company.accounts.view";

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "الرمز", "Code", ImportFieldType.Code, Required: true, MaxLength: 20, IsKey: true, UpperCase: true),
        new("name", "الاسم", "Name", ImportFieldType.Text, Required: true, MaxLength: 60),
        new("qty", "الكمية", "Quantity", ImportFieldType.Integer),
        new("price", "السعر", "Price", ImportFieldType.Decimal),
        new("start", "التاريخ", "Start date", ImportFieldType.Date),
        new("active", "فعال", "Active", ImportFieldType.Boolean),
        new("kind", "النوع", "Kind", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]> { ["goods"] = ["بضاعة"], ["service"] = ["خدمة"] }),
    ];

    public Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        Interlocked.Increment(ref Analyses);
        if (Throw) throw new InvalidOperationException("analysis failed");
        var result = new List<RowAnalysis>(); decimal value = 0; var creates = 0;
        foreach (var r in rows)
        {
            var code = r.Get("code")!; var name = r.Get("name")!; var kind = r.Get("kind") ?? "goods";
            if (r.Get("qty") is { } q && long.Parse(q) < 0) { result.Add(new RowAnalysis(r.RowNo, RowOutcome.Invalid, [new RowIssue("qty", "negative_quantity")])); continue; }
            if (Existing.TryGetValue(code, out var cur))
            {
                result.Add(cur == (name, kind)
                    ? new RowAnalysis(r.RowNo, RowOutcome.Exact, Explanation: "same as the existing item")
                    : new RowAnalysis(r.RowNo, RowOutcome.Conflict, [new RowIssue("code", "exists_with_different_data", $"{cur.Name}/{cur.Kind}")], [ImportResolutions.Skip], Explanation: "an item with this code exists with different data"));
                continue;
            }
            creates++;
            value += decimal.Parse(r.Get("qty") ?? "0") * decimal.Parse(r.Get("price") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new RowAnalysis(r.RowNo, RowOutcome.Valid, Operation: new JsonObject { ["type"] = "create-item", ["code"] = code, ["name"] = name, ["kind"] = kind, ["qty"] = r.Get("qty"), ["price"] = r.Get("price") }, Explanation: "will be created", Sequence: code.StartsWith('Z') ? 1 : 0));
        }
        var baseline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', Existing.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value.Name}/{e.Value.Kind}"))))).ToLowerInvariant();
        return Task.FromResult(new AnalysisResult(result, baseline, [new ReconLine("items_to_create", creates.ToString()), new ReconLine("stock_value", value.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
    }
}

/// <summary>Mapping, normalization, validation, conflict resolution, plan hashing and stale detection (Phase 3.2D S2) against real PostgreSQL; disposable cluster only.</summary>
public class ImportDryRunTests : ImportTestBase
{
    private ItemsTestAdapter Items = null!;

    private async Task Prepare()
    {
        Items = new ItemsTestAdapter();
        await PrepareImports(adapters: [Items]);
    }

    private const string Header = "Code,Name,Qty,Price,Start,Active,Kind\n";
    private static readonly JsonObject FullMapping = JsonNode.Parse("""{"fields":{"code":{"column":0},"name":{"column":1},"qty":{"column":2},"price":{"column":3},"start":{"column":4},"active":{"column":5},"kind":{"column":6}}}""")!.AsObject();

    private async Task<ImportBatchView> StageItems(string rows, string header = Header)
    {
        var b = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "items", "items.csv", Bytes(header + rows), null));
        return Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(0, 1, "utf-8", null)));
    }

    private async Task<ImportBatchView> Mapped(string rows, JsonNode? mapping = null, string header = Header)
    {
        var s = await StageItems(rows, header);
        return Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, mapping ?? FullMapping));
    }

    private async Task<ImportBatchView> Validated(string rows, JsonNode? mapping = null)
    {
        var m = await Mapped(rows, mapping);
        return Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision));
    }

    private async Task<Dictionary<int, ImportRowView>> RowMap(Guid id) => (await Imports.RowsAsync(Company.Id, id, 0, 500, null))!.Rows.ToDictionary(r => r.RowNo);

    private const string Mixed =
        "a1,Pen,10,1.50,2026-03-01,yes,goods\n" +        // 2: valid
        "A2,Ink,5,2,2026-03-02,no,خدمة\n" +             // 3: valid (Arabic enum alias, upper-cased code)
        "E1,Existing,1,1,2026-03-03,yes,goods\n" +      // 4: exact match of an existing item
        "E2,Changed name,1,1,2026-03-03,yes,goods\n" +  // 5: conflict with an existing item
        "N1,Negative,-3,1,2026-03-04,yes,goods\n" +     // 6: invalid (adapter rule)
        "B1,Bad date,1,1,2026-13-45,yes,goods\n" +      // 7: invalid (normalization)
        "D1,Twice,1,1,2026-03-05,yes,goods\n" +         // 8: valid
        "d1,Twice,1,1,2026-03-05,yes,goods\n" +         // 9: identical repeat of row 8 (resolvable)
        "D2,One,1,1,2026-03-05,yes,goods\n" +           // 10: valid
        "D2,Other,1,1,2026-03-05,yes,goods\n" +         // 11: a DIFFERENT repeat (blocking)
        ",NoCode,1,1,2026-03-05,yes,goods\n";           // 12: invalid (required)

    [PostgresFact]
    public async Task Mapping_is_validated_suggested_stored_and_reused_and_changing_it_voids_everything_built_on_it()
    {
        await Prepare();
        var s = await StageItems("A1,Pen,1,1,2026-03-01,yes,goods\n", "الرمز,Name,الكمية,Price,Start date,فعال,Kind\n");
        var suggestion = Ok(await Imports.SuggestMappingAsync(Company.Id, s.Id));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, new[] { "code", "name", "qty", "price", "start", "active", "kind" }.Select(k => suggestion["fields"]![k]!["column"]!.GetValue<int>()));
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, null, suggestion)).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision - 1, suggestion)).Status);
        foreach (var (json, code) in new[]
        {
            ("""{"fields":{"name":{"column":1}}}""", "required_field_unmapped"), ("""{"fields":{"code":{"column":9},"name":{"column":1}}}""", "invalid_column"),
            ("""{"fields":{"code":{"column":0},"name":{"column":0}}}""", "duplicate_column"), ("""{"fields":{"code":{"column":0},"name":{"column":1},"zzz":{"column":2}}}""", "unknown_field"),
        })
        {
            var r = await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, JsonNode.Parse(json));
            Assert.Equal((ConfigStatus.Invalid, true), (r.Status, r.Fields!.Any(f => f.Code == code)));
        }
        Assert.Equal(ImportStatus.Staged, (await Reload(s.Id)).Status);                              // refusals change nothing
        var mapped = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, suggestion));
        Assert.Equal((ImportStatus.Mapped, null), (mapped.Status, mapped.PlanHash));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{s.Id}' AND {Ir.Status} <> 1"));
        // a second batch with the same headers is offered the mapping that was used before
        var other = await StageItems("A2,Ink,1,1,2026-03-01,yes,goods\n", "الرمز,Name,الكمية,Price,Start date,فعال,Kind\n");
        var reused = Ok(await Imports.SuggestMappingAsync(Company.Id, other.Id));
        Assert.Equal(ImportNormalizerJson(mapped.Mapping), ImportNormalizerJson(reused));
        // after a dry run, a new mapping returns the batch to Mapped, drops the plan and resets rows and resolutions
        var v = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, mapped.Id, mapped.Revision));
        Assert.Equal(ImportStatus.Validated, v.Status);
        Assert.NotNull(v.PlanHash);
        var re = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, v.Id, v.Revision, JsonNode.Parse("""{"fields":{"code":{"column":0},"name":{"column":1}},"options":{"decimalSeparator":","}}""")));
        Assert.Equal((ImportStatus.Mapped, null), (re.Status, re.PlanHash));
        Assert.Null(re.Plan);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{s.Id}' AND ({Ir.Status} <> 1 OR {Ir.Normalized} IS NOT NULL OR {Ir.Analysis} IS NOT NULL)"));
        Assert.Contains(ImportEventKind.Mapped, (await Imports.EventsAsync(Company.Id, s.Id)).Select(e => e.Kind));
        await AssertAuditIntact();
    }

    private static string ImportNormalizerJson(JsonNode? n) => ImportNormalizer.CanonicalJson(n);

    [PostgresFact]
    public async Task The_dry_run_validates_detects_duplicates_and_conflicts_explains_each_row_reconciles_and_writes_no_business_data()
    {
        await Prepare();
        Items.Existing["E1"] = ("Existing", "goods"); Items.Existing["E2"] = ("Original name", "goods");
        var before = Items.Existing.ToDictionary(e => e.Key, e => e.Value);
        var v = await Validated(Mixed);
        Assert.Equal(ImportStatus.Validated, v.Status);
        var rows = await RowMap(v.Id);
        (short Status, string Code)[] expected =
        [
            ((short)RowOutcome.Valid, ""), ((short)RowOutcome.Valid, ""), ((short)RowOutcome.Exact, ""), ((short)RowOutcome.Conflict, "exists_with_different_data"), ((short)RowOutcome.Invalid, "negative_quantity"),
            ((short)RowOutcome.Invalid, "invalid_date"), ((short)RowOutcome.Valid, ""), ((short)RowOutcome.Conflict, "duplicate_in_file"), ((short)RowOutcome.Valid, ""), ((short)RowOutcome.Invalid, "conflicting_duplicate_in_file"),
            ((short)RowOutcome.Invalid, "required"),
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var r = rows[i + 2];
            Assert.Equal(expected[i].Status, r.Status);
            if (expected[i].Code != "") Assert.Equal(expected[i].Code, r.Issues![0]!["code"]!.GetValue<string>());
        }
        Assert.Equal("A1", rows[2].Normalized!["code"]!.GetValue<string>());                          // upper-cased
        Assert.Equal("1.5", rows[2].Normalized!["price"]!.GetValue<string>());
        Assert.Equal("service", rows[3].Normalized!["kind"]!.GetValue<string>());                      // Arabic alias
        Assert.Equal("create-item", rows[2].Operation!["type"]!.GetValue<string>());
        Assert.Equal(["skip"], rows[5].AllowedResolutions);
        Assert.Equal("an item with this code exists with different data", rows[5].Explanation);
        Assert.Equal("duplicate_in_file:8", rows[9].Explanation);
        Assert.Null(rows[4].Operation);
        // the plan: counts, reconciliation, a canonical hash, and "cannot apply" while anything blocks
        var summary = v.Plan!["summary"]!;
        Assert.Equal((11, 4, 1, 4, 2, 0, 4, 6), (summary["total"]!.GetValue<int>(), summary["valid"]!.GetValue<int>(), summary["exact"]!.GetValue<int>(), summary["invalid"]!.GetValue<int>(),
            summary["conflict"]!.GetValue<int>(), summary["resolved"]!.GetValue<int>(), summary["operations"]!.GetValue<int>(), summary["blocking"]!.GetValue<int>()));
        Assert.False(v.Plan["canApply"]!.GetValue<bool>());
        Assert.Equal(["items_to_create", "stock_value"], v.Plan["reconciliation"]!.AsArray().Select(l => l!["key"]!.GetValue<string>()));
        Assert.Equal("4", v.Plan["reconciliation"]![0]!["value"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", v.PlanHash!);
        // nothing business-side changed and the adapter only read
        Assert.Equal(before, Items.Existing);
        // deterministic: the same file, mapping and data give the same plan hash and the same rows
        var again = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, v.Revision));
        Assert.Equal(v.PlanHash, again.PlanHash);
        Assert.Equal(2, Items.Analyses);
        Assert.Contains(ImportEventKind.Validated, (await Imports.EventsAsync(Company.Id, v.Id)).Select(e => e.Kind));
        // filters and pagination of rows by status
        Assert.Equal(new[] { 6, 7, 11, 12 }, (await Imports.RowsAsync(Company.Id, v.Id, 0, 100, (short)RowOutcome.Invalid))!.Rows.Select(r => r.RowNo));
        Assert.Equal(4, (await Imports.RowsAsync(Company.Id, v.Id, 0, 100, (short)RowOutcome.Invalid))!.Total);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Resolutions_are_explicit_validated_recorded_and_force_the_dry_run_and_the_plan_hash_to_be_recomputed()
    {
        await Prepare();
        Items.Existing["E2"] = ("Original name", "goods");
        var v = await Validated("N1,New,1,1,2026-03-01,yes,goods\nE2,Changed,1,1,2026-03-01,yes,goods\nD1,Twice,1,1,2026-03-01,yes,goods\nD1,Twice,1,1,2026-03-01,yes,goods\nE3,Fine,1,1,2026-03-01,yes,goods\n");
        Assert.Equal(2, v.Plan!["summary"]!["conflict"]!.GetValue<int>());
        var firstHash = v.PlanHash!;
        // refusals: unknown row, row that is not in conflict, an action the row does not allow, repeated row numbers, no input
        Assert.Contains("row_not_found", (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(99, "skip")])).Fields!.Select(f => f.Code));
        Assert.Contains("row_not_in_conflict", (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(2, "skip")])).Fields!.Select(f => f.Code));
        Assert.Contains("resolution_not_allowed", (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(3, "overwrite")])).Fields!.Select(f => f.Code));
        Assert.Equal("duplicate_row", (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(3, "skip"), new(3, "skip")])).Fields!.Single().Code);
        Assert.Equal(ConfigStatus.Invalid, (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [])).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, null, [new(3, "skip")])).Status);
        Assert.Equal((ImportStatus.Validated, firstHash), ((await Reload(v.Id)).Status, (await Reload(v.Id)).PlanHash));       // refusals changed nothing
        // a valid decision returns the batch to Mapped (plan void) and is in the history; the dry run is recomputed
        var decided = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(3, "skip")]));
        Assert.Equal((ImportStatus.Mapped, null), (decided.Status, decided.PlanHash));
        Assert.Equal(ConfigStatus.Conflict, (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, decided.Revision, [new(5, "skip")])).Status);           // not validated any more
        var rerun = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, decided.Revision));
        Assert.Equal(1, rerun.Plan!["summary"]!["conflict"]!.GetValue<int>());
        Assert.Equal(1, rerun.Plan["summary"]!["resolved"]!.GetValue<int>());
        Assert.NotEqual(firstHash, rerun.PlanHash);                                                  // a decision changes the plan hash
        var rows = await RowMap(v.Id);
        Assert.Equal(((short)RowOutcome.Resolved, "skip", null), (rows[3].Status, rows[3].Resolution, rows[3].Operation));
        // resolve the repeat as well: nothing blocks any more and the plan can be applied
        var second = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, rerun.Revision, [new(5, "skip")]));
        var ready = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, second.Revision));
        Assert.Equal((0, 2, true, 3), (ready.Plan!["summary"]!["blocking"]!.GetValue<int>(), ready.Plan["summary"]!["resolved"]!.GetValue<int>(), ready.Plan["canApply"]!.GetValue<bool>(), ready.Plan["summary"]!["operations"]!.GetValue<int>()));
        // a decision can be withdrawn: the conflict is back and blocks again
        var cleared = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, ready.Revision, [new(3, null)]));
        var back = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, cleared.Revision));
        Assert.Equal((1, false), (back.Plan!["summary"]!["conflict"]!.GetValue<int>(), back.Plan["canApply"]!.GetValue<bool>()));
        // every decision is in the immutable history, and the staged content is untouched by decisions
        var events = await Imports.EventsAsync(Company.Id, v.Id);
        Assert.Equal(3, events.Count(e => e.Kind == ImportEventKind.ResolutionChanged));
        Assert.Equal(1, (await Imports.RowsAsync(Company.Id, v.Id, 0, 10, null))!.Rows.Count(r => r.RowNo == 3 && r.Cells[0] == "E2"));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_nothing_to_apply_file_cannot_be_applied_a_plan_goes_stale_when_data_changes_and_a_new_dry_run_replaces_it()
    {
        await Prepare();
        Items.Existing["E1"] = ("Existing", "goods");
        var only = await Validated("E1,Existing,1,1,2026-03-01,yes,goods\n");
        Assert.Equal((0, false), (only.Plan!["summary"]!["blocking"]!.GetValue<int>(), only.Plan["canApply"]!.GetValue<bool>()));   // everything exact: nothing to do
        var v = await Validated("N1,New,2,3,2026-03-01,yes,goods\nN2,New two,1,1,2026-03-01,yes,service\n");
        Assert.True(v.Plan!["canApply"]!.GetValue<bool>());
        var fresh = Ok(await Imports.CheckPlanAsync(Company.Id, v.Id));
        Assert.Equal((true, v.PlanHash, 0), (fresh.Fresh, fresh.CurrentHash, fresh.Blocking));
        Items.Existing["N1"] = ("New", "goods");                                                      // somebody created the item meanwhile
        var stale = Ok(await Imports.CheckPlanAsync(Company.Id, v.Id));
        Assert.False(stale.Fresh);
        Assert.NotEqual(v.PlanHash, stale.CurrentHash);
        Assert.Equal(v.PlanHash, stale.ApprovedHash);
        Assert.Equal(v.PlanHash, (await Reload(v.Id)).PlanHash);                                      // checking never rewrites the stored plan
        var replaced = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, (await Reload(v.Id)).Revision));
        Assert.NotEqual(v.PlanHash, replaced.PlanHash);
        Assert.Equal(1, replaced.Plan!["summary"]!["exact"]!.GetValue<int>());
        Assert.True(Ok(await Imports.CheckPlanAsync(Company.Id, v.Id)).Fresh);
        // the same data under another mapping option is another plan
        var m = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, v.Id, replaced.Revision, JsonNode.Parse("""{"fields":{"code":{"column":0},"name":{"column":1},"qty":{"column":2},"price":{"column":3}},"options":{"dateFormat":"dd/MM/yyyy"}}""")));
        Assert.Equal(ConfigStatus.NotFound, (await Imports.CheckPlanAsync(Company.Id, Guid.NewGuid())).Status);
        Assert.Equal("batch_not_validated", (await Imports.CheckPlanAsync(Company.Id, m.Id)).Code);
    }

    [PostgresFact]
    public async Task The_dry_run_refuses_wrong_states_stale_revisions_and_a_failing_adapter_changes_nothing()
    {
        await Prepare();
        var s = await StageItems("A1,Pen,1,1,2026-03-01,yes,goods\n");
        Assert.Equal("batch_not_validatable", (await Imports.DryRunAsync(Admin.Id, Company.Id, s.Id, s.Revision)).Code);          // not mapped yet
        var m = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, FullMapping));
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, null)).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision - 1)).Status);
        Items.Throw = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision));
        var after = await Reload(m.Id);
        Assert.Equal((ImportStatus.Mapped, m.Revision, null), (after.Status, after.Revision, after.PlanHash));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{m.Id}' AND {Ir.Status} <> 1"));
        Items.Throw = false;
        Assert.Equal(ImportStatus.Validated, Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision)).Status);
        // an unknown or changed adapter version cannot be validated with new rules against old staging
        await Sql($"UPDATE {Ib.Table} SET {Ib.AdapterVersion} = 7, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{m.Id}'");
        Assert.Equal("adapter_version_changed", (await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, (await Reload(m.Id)).Revision)).Code);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_large_file_is_normalized_and_validated_in_one_dry_run_with_exact_counts()
    {
        await Prepare();
        var sb = new StringBuilder();
        for (var i = 1; i <= 5000; i++) sb.Append($"C{i:00000},Item {i},{i % 7},{i % 13}.5,2026-03-01,yes,goods\n");
        sb.Append("C00001,Item 1,1,1.5,2026-03-01,yes,goods\n");                                     // one identical repeat
        sb.Append("C00002,Different,1,1,2026-03-01,yes,goods\n");                                     // one conflicting repeat
        var v = await Validated(sb.ToString());
        var s = v.Plan!["summary"]!;
        Assert.Equal((5002, 5000, 1, 1, 5000), (s["total"]!.GetValue<int>(), s["valid"]!.GetValue<int>(), s["conflict"]!.GetValue<int>(), s["invalid"]!.GetValue<int>(), s["operations"]!.GetValue<int>()));
        Assert.Equal(5002L, await Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{v.Id}' AND {Ir.Normalized} IS NOT NULL"));
        var page = (await Imports.RowsAsync(Company.Id, v.Id, 4999, 3, null))!;
        Assert.Equal(new[] { 5001, 5002, 5003 }, page.Rows.Select(r => r.RowNo));
        Assert.Equal(v.PlanHash, Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, v.Revision)).PlanHash);
    }

    [PostgresFact]
    public async Task Mapping_dry_run_and_resolutions_need_the_import_and_adapter_permissions_and_viewing_needs_the_view_permissions()
    {
        await Prepare();
        var v = await Validated("N1,New,1,1,2026-03-01,yes,goods\nD1,T,1,1,2026-03-01,yes,goods\nD1,T,1,1,2026-03-01,yes,goods\n");
        var (importOnly, _) = await Member([PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsView], ScopeKind.Company, null);
        var (businessOnly, _) = await Member([PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        var (both, _) = await Member([PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        var (viewer, _) = await Member([PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        foreach (var denied in new[] { importOnly, businessOnly, viewer })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await IMg.SetMappingAsync(denied.Id, Company.Id, v.Id, v.Revision, FullMapping)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await IMg.DryRunAsync(denied.Id, Company.Id, v.Id, v.Revision)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await IMg.SetResolutionsAsync(denied.Id, Company.Id, v.Id, v.Revision, [new(4, "skip")])).Status);
        }
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.FieldsAsync(importOnly.Id, Company.Id, v.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.CheckPlanAsync(businessOnly.Id, Company.Id, v.Id)).Status);
        Assert.Equal(7, Ok(await IMg.FieldsAsync(viewer.Id, Company.Id, v.Id)).Fields.Count);
        Assert.True(Ok(await IMg.CheckPlanAsync(viewer.Id, Company.Id, v.Id)).Fresh);
        Assert.True(Ok(await IMg.SuggestMappingAsync(viewer.Id, Company.Id, v.Id)).ContainsKey("fields"));
        var done = Ok(await IMg.SetResolutionsAsync(both.Id, Company.Id, v.Id, v.Revision, [new(4, "skip")]));
        Assert.Equal(ImportStatus.Mapped, done.Status);
        Assert.Equal(ImportStatus.Validated, Ok(await IMg.DryRunAsync(both.Id, Company.Id, v.Id, done.Revision)).Status);
    }

    [PostgresFact]
    public async Task The_mapping_wizard_flow_works_over_http_with_fields_suggestion_mapping_dry_run_resolutions_and_plan_check()
    {
        Items = new ItemsTestAdapter();
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Imports:StorageDirectory", StorageDir);
            b.ConfigureServices(s => { s.AddSingleton<Hasm.Infrastructure.Security.KekRing>(new Hasm.Infrastructure.Security.InMemoryKekRing("k1", TestIdentityKeys.Master)); s.AddSingleton(TestEncryption); s.AddSingleton<IImportAdapter>(Items); });
        });
        Items.Existing["E2"] = ("Original", "goods");
        const string Base = "/api/v1/company/imports";
        var cookie = await SignIn(Admin);
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"{Base}?adapter=items&fileName=items.csv") { Content = new ByteArrayContent(Bytes(Header + "N1,New,1,2.5,2026-03-01,yes,goods\nE2,Changed,1,1,2026-03-01,yes,goods\n")) };
        upload.Headers.Add("X-Hasm-Request", "1"); upload.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var up = await client.SendAsync(upload);
        var created = JsonDocument(await up.Content.ReadAsStringAsync());
        var id = created.GetProperty("id").GetGuid();
        var staged = await Send(HttpMethod.Post, $"{Base}/{id}/stage", new { sheet = 0, headerRow = 1 }, cookie, ifMatch: up.Headers.ETag!.Tag);
        var fields = await Send(HttpMethod.Get, $"{Base}/{id}/fields", cookie: cookie);
        Assert.Equal(7, fields.Json.GetProperty("fields").GetArrayLength());
        Assert.Equal("skip", fields.Json.GetProperty("resolutions")[0].GetString());
        Assert.Equal("goods", fields.Json.GetProperty("fields")[6].GetProperty("values")[0].GetProperty("value").GetString());
        var suggestion = await Send(HttpMethod.Get, $"{Base}/{id}/mapping/suggestion", cookie: cookie);
        Assert.Equal(0, suggestion.Json.GetProperty("mapping").GetProperty("fields").GetProperty("code").GetProperty("column").GetInt32());
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{id}/mapping", suggestion.Json.GetProperty("mapping"), cookie)).Status);
        var bad = await Send(HttpMethod.Put, $"{Base}/{id}/mapping", new { fields = new { name = new { column = 1 } } }, cookie, ifMatch: Etag(staged));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var mapped = await Send(HttpMethod.Put, $"{Base}/{id}/mapping", suggestion.Json.GetProperty("mapping"), cookie, ifMatch: Etag(staged));
        Assert.Equal("mapped", mapped.Json.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Get, $"{Base}/{id}/plan-check", cookie: cookie)).Status);   // not validated yet
        var validated = await Send(HttpMethod.Post, $"{Base}/{id}/dry-run", new { }, cookie, ifMatch: Etag(mapped));
        Assert.Equal(("validated", 1), (validated.Json.GetProperty("status").GetString(), validated.Json.GetProperty("plan").GetProperty("summary").GetProperty("conflict").GetInt32()));
        var rows = await Send(HttpMethod.Get, $"{Base}/{id}/rows?status=4", cookie: cookie);
        var row = rows.Json.GetProperty("rows")[0];
        Assert.Equal((3, "skip", "exists_with_different_data"), (row.GetProperty("rowNo").GetInt32(), row.GetProperty("allowedResolutions")[0].GetString(), row.GetProperty("issues")[0].GetProperty("code").GetString()));
        Assert.Equal("Changed", row.GetProperty("normalized").GetProperty("name").GetString());
        var decided = await Send(HttpMethod.Post, $"{Base}/{id}/resolutions", new { resolutions = new[] { new { rowNo = 3, action = "skip" } } }, cookie, ifMatch: Etag(validated));
        Assert.Equal("mapped", decided.Json.GetProperty("status").GetString());
        var again = await Send(HttpMethod.Post, $"{Base}/{id}/dry-run", new { }, cookie, ifMatch: Etag(decided));
        Assert.True(again.Json.GetProperty("plan").GetProperty("canApply").GetBoolean());
        var check = await Send(HttpMethod.Get, $"{Base}/{id}/plan-check", cookie: cookie);
        Assert.True(check.Json.GetProperty("fresh").GetBoolean());
        Assert.Equal(again.Json.GetProperty("planHash").GetString(), check.Json.GetProperty("currentPlanHash").GetString());
        // a member without the business permission is refused
        var (nobody, _) = await Member([PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsView], ScopeKind.Company, null);
        var nc = await SignIn(nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{id}/dry-run", new { }, nc, ifMatch: Etag(again))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}/fields", cookie: nc)).Status);
    }

    private static System.Text.Json.JsonElement JsonDocument(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
}
