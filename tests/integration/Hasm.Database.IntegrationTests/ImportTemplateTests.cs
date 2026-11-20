using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Imports;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using IT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportMappingTemplate;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.9C slice 1 (company migration 0025): reusable import mapping templates - per company and adapter, by header name, versioned, append-only, audited, and never a way around validation.</summary>
public class ImportTemplateTests : ImportTestBase
{
    private readonly ItemsTestAdapter Items = new();
    private ImportTemplateService Tpl = null!;

    private async Task Prepare()
    {
        await PrepareImports(adapters: [Items]);
        Tpl = new ImportTemplateService(Rbac, Resolver, Tx, Imports, Registry, Clock);
    }

    private async Task<ImportBatchView> StagedItems(string csv)
    {
        var b = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "items", "items.csv", Bytes(csv), null));
        return Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(0, 1, "utf-8", null)));
    }
    private static JsonObject Map(int code, int name, int? qty = null) => new() { ["fields"] = new JsonObject
    {
        ["code"] = new JsonObject { ["column"] = code }, ["name"] = new JsonObject { ["column"] = name },
        ["qty"] = qty is null ? new JsonObject { ["default"] = "1" } : new JsonObject { ["column"] = qty },
    }, ["options"] = new JsonObject { ["decimalSeparator"] = ".", ["thousandsSeparator"] = ",", ["dateFormat"] = "yyyy-MM-dd" } };
    private async Task<ImportBatchView> Mapped(string csv, JsonObject mapping)
    {
        var b = await StagedItems(csv);
        return Ok(await IMg.SetMappingAsync(Admin.Id, Company.Id, b.Id, b.Revision, mapping));
    }
    private static void Fails<T>(ConfigResult<T> r, ConfigStatus status, string code) { Assert.Equal((status, code), (r.Status, r.Code)); }
    private static void Invalid<T>(ConfigResult<T> r, string field, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, f => f.Field == field && f.Code == code); }
    private Task<long> TemplateEvents() => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} IN (1700, 1701)");

    [PostgresFact]
    public async Task A_mapping_is_saved_by_header_name_and_applies_to_a_file_with_the_columns_in_another_order_without_skipping_validation()
    {
        await Prepare();
        var first = await Mapped("code,name,qty\nA1,Alpha,5\n", Map(0, 1, 2));
        var saved = Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, first.Id, "  Items sheet ", 0));
        Assert.Equal(("items", "Items sheet", 1, false), (saved.Adapter, saved.Name, saved.Version, saved.IsRetired));
        Assert.Equal("code", (string)saved.Content["fields"]!["code"]!["header"]!);                                          // a header, never a position
        Assert.Null(saved.Content["fields"]!["code"]!["column"]);

        // the same data with the columns in another order: the template finds them by header
        var other = await StagedItems("Quantity , NAME,Code\n7,Beta,B2\n".Replace("Quantity ", "qty"));
        var applied = Ok(await Tpl.ApplyAsync(Admin.Id, Company.Id, other.Id, "Items sheet"));
        Assert.Equal((1, 0), (applied.Version, applied.Unmatched.Count));
        Assert.Equal((2, 1, 0), (applied.Mapping["fields"]!["code"]!["column"]!.GetValue<int>(), applied.Mapping["fields"]!["name"]!["column"]!.GetValue<int>(), applied.Mapping["fields"]!["qty"]!["column"]!.GetValue<int>()));
        // applying changed NOTHING on the batch: the mapping still goes through the ordinary validated step
        Assert.Null((await Reload(other.Id)).Mapping);
        var set = Ok(await IMg.SetMappingAsync(Admin.Id, Company.Id, other.Id, other.Revision, applied.Mapping));
        Assert.Equal(ImportStatus.Mapped, set.Status);

        // a file whose headers do not match leaves the fields unmapped and reports them; the ordinary validation then REFUSES the mapping (a template never makes an invalid mapping valid)
        var foreign = await StagedItems("code,label\nC3,Gamma\n");
        var partial = Ok(await Tpl.ApplyAsync(Admin.Id, Company.Id, foreign.Id, "Items sheet"));
        Assert.Equal(["name", "qty"], partial.Unmatched.Order());
        var refused = await IMg.SetMappingAsync(Admin.Id, Company.Id, foreign.Id, foreign.Revision, partial.Mapping);
        Assert.Equal(ConfigStatus.Invalid, refused.Status);
        Assert.Contains(refused.Fields!, f => f.Code == "required_field_unmapped");
    }

    [PostgresFact]
    public async Task Versions_are_append_only_stale_saves_are_refused_and_retirement_is_a_new_version()
    {
        await Prepare();
        var b = await Mapped("code,name,qty\nA1,Alpha,5\n", Map(0, 1, 2));
        Fails(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "T", null), ConfigStatus.PreconditionRequired, "precondition_required");
        Invalid(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "  ", 0), "name", "name_invalid");
        Invalid(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, new string('x', 101), 0), "name", "name_invalid");
        Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "T", 0));
        Fails(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "T", 0), ConfigStatus.PreconditionFailed, "revision_conflict");      // 0 = new, but it exists
        var v2 = Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "T", 1));
        Assert.Equal(2, v2.Version);
        var stale = await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "T", 1);
        Assert.Equal((ConfigStatus.PreconditionFailed, 2L), (stale.Status, stale.CurrentRevision));
        // the same name under another adapter or a different name is independent
        Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "Other", 0));

        Fails(await Tpl.RetireAsync(Admin.Id, Company.Id, "items", "T", null), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Tpl.RetireAsync(Admin.Id, Company.Id, "items", "T", 1), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Tpl.RetireAsync(Admin.Id, Company.Id, "items", "Nope", 0), ConfigStatus.NotFound, "template_not_found");
        var retired = Ok(await Tpl.RetireAsync(Admin.Id, Company.Id, "items", "T", 2));
        Assert.Equal((3, true), (retired.Version, retired.IsRetired));
        Assert.Equal(v2.Content.ToJsonString(), retired.Content.ToJsonString());                                              // retirement keeps the content
        Fails(await Tpl.RetireAsync(Admin.Id, Company.Id, "items", "T", 3), ConfigStatus.Conflict, "already_retired");
        var fresh = await StagedItems("code,name,qty\nZ,Z,1\n");
        Fails(await Tpl.ApplyAsync(Admin.Id, Company.Id, fresh.Id, "T"), ConfigStatus.NotFound, "template_not_found");        // a retired template is no longer applied
        var list = Ok(await Tpl.ListAsync(Admin.Id, Company.Id, "items"));
        Assert.Equal([("Other", 1, false), ("T", 3, true)], list.Select(t => (t.Name, t.Version, t.IsRetired)));            // latest version of each
        Assert.Equal([3, 2, 1], Ok(await Tpl.HistoryAsync(Admin.Id, Company.Id, "items", "T")).Select(t => t.Version));
        Assert.Equal(4L, await TemplateEvents());                                                                              // 3 saves + 1 retirement... (T v1, T v2, Other v1, T retired)
        // the new version after a retirement revives it
        var revived = Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "T", 3));
        Assert.Equal((4, false), (revived.Version, revived.IsRetired));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Unusable_ambiguous_or_unmapped_batches_are_refused_and_concurrent_saves_yield_one_winner()
    {
        await Prepare();
        var staged = await StagedItems("code,name,qty\nA1,Alpha,5\n");
        Fails(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, staged.Id, "T", 0), ConfigStatus.Conflict, "batch_not_mapped");
        Fails(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, Guid.NewGuid(), "T", 0), ConfigStatus.NotFound, "import_not_found");
        var dup = await Mapped("Code,CODE ,name\nA1,A1,Alpha\n", new JsonObject { ["fields"] = new JsonObject { ["code"] = new JsonObject { ["column"] = 0 }, ["name"] = new JsonObject { ["column"] = 2 } } });
        Invalid(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, dup.Id, "T", 0), "mapping.fields.code.column", "header_ambiguous");     // the template could not be re-applied deterministically
        var b = await Mapped("code,name,qty\nA1,Alpha,5\n", Map(0, 1, 2));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "RACE", 0))));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {IT.Table} WHERE {IT.Name} = 'RACE'"));
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_changing_deleting_truncating_or_skipping_a_version()
    {
        await Prepare();
        var b = await Mapped("code,name,qty\nA1,Alpha,5\n", Map(0, 1, 2));
        var t = Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "G", 0));
        Assert.Equal("P0001", await Fails($"UPDATE {IT.Table} SET {IT.Content} = '{{}}' WHERE {IT.Id} = '{t.Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {IT.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {IT.Table}"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {IT.Table} ({IT.Id}, {IT.Adapter}, {IT.Name}, {IT.Version}, {IT.Content}, {IT.CreatedBy}, {IT.CreatedAtUtc}) VALUES ('{Guid.NewGuid()}', 'items', 'G', 5, '{{}}', '{Admin.Id}', now())"));     // a gap
        Assert.Equal("23514", await Fails($"INSERT INTO {IT.Table} ({IT.Id}, {IT.Adapter}, {IT.Name}, {IT.Version}, {IT.Content}, {IT.CreatedBy}, {IT.CreatedAtUtc}) VALUES ('{Guid.NewGuid()}', 'BAD ADAPTER', 'H', 1, '{{}}', '{Admin.Id}', now())"));
        Assert.Equal(1L, await Count(Company, IT.Table));
    }

    [PostgresFact]
    public async Task Templates_need_the_import_and_the_adapter_permissions_like_the_mapping_they_prefill()
    {
        await Prepare();
        var b = await Mapped("code,name,qty\nA1,Alpha,5\n", Map(0, 1, 2));
        Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "P", 0));
        var other = await StagedItems("code,name,qty\nA2,Beta,6\n");
        var outsider = await NewUser("tplout");
        Fails(await Tpl.ListAsync(outsider.Id, Company.Id, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await Tpl.SaveFromBatchAsync(outsider.Id, Company.Id, b.Id, "X", 0), ConfigStatus.Forbidden, "forbidden");
        Fails(await Tpl.ApplyAsync(outsider.Id, Company.Id, other.Id, "P"), ConfigStatus.Forbidden, "forbidden");
        Fails(await Tpl.RetireAsync(outsider.Id, Company.Id, "items", "P", 1), ConfigStatus.Forbidden, "forbidden");
        Fails(await Tpl.HistoryAsync(outsider.Id, Company.Id, "items", "P"), ConfigStatus.Forbidden, "forbidden");
        var (viewer, _) = await Member([PermissionCatalog.Codes.ImportsView, "company.accounts.view"], ScopeKind.Company, null);
        Assert.Single(Ok(await Tpl.ListAsync(viewer.Id, Company.Id, null)));                                                    // may read
        Fails(await Tpl.SaveFromBatchAsync(viewer.Id, Company.Id, b.Id, "X", 0), ConfigStatus.Forbidden, "forbidden");           // may not write or apply
        Fails(await Tpl.ApplyAsync(viewer.Id, Company.Id, other.Id, "P"), ConfigStatus.Forbidden, "forbidden");
        var (blind, _) = await Member([PermissionCatalog.Codes.ImportsView], ScopeKind.Company, null);
        Assert.Empty(Ok(await Tpl.ListAsync(blind.Id, Company.Id, null)));                                                      // without the adapter's own view permission its templates are invisible
        Assert.Equal(1L, await Count(Company, IT.Table));
    }

    [PostgresFact]
    public async Task Names_are_normalized_unaddressable_or_lookalike_names_are_refused_and_the_response_is_the_row_that_was_saved()
    {
        await Prepare();
        var b = await Mapped("code,name,qty\nA1,Alpha,5\n", Map(0, 1, 2));
        foreach (var bad in new[] { "..", ".", "Sheet​", "‮sheet", "a\u0085b" })
            Invalid(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, bad, 0), "name", "name_invalid");
        Invalid(await Tpl.RetireAsync(Admin.Id, Company.Id, "items", "..", 1), "name", "name_invalid");
        Assert.Empty(Ok(await Tpl.HistoryAsync(Admin.Id, Company.Id, "items", "..")));
        // NFC: the composed and the decomposed spelling are ONE template
        var composed = Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "café", 0));
        var again = Ok(await Tpl.SaveFromBatchAsync(Admin.Id, Company.Id, b.Id, "café", 1));
        Assert.Equal((composed.Name, 2), (again.Name, again.Version));
        Assert.Equal(1L, await Sql<long>($"SELECT count(DISTINCT {IT.Name}) FROM {IT.Table}"));
    }
}
