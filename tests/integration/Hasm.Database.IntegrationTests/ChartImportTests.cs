using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Tests;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using Ah = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountRevision;
using Ba = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Ib = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The Chart of Accounts import (Phase 3.2D S5, roadmap 3.2A-3) end to end against real PostgreSQL: real CSV and XLSX files, the real adapter, the real Chart domain service, independent approval and the atomic
/// application. Disposable cluster only.
/// </summary>
public class ChartImportTests : ImportTestBase
{
    private ChartOfAccountsService ChartDomain = null!;
    private ChartOfAccountsImportAdapter Adapter = null!;
    private const string Header = "code,name_ar,name_en,parent_code,type,nature,is_group\n";
    private static readonly JsonObject Map = JsonNode.Parse("""{"fields":{"code":{"column":0},"name_ar":{"column":1},"name_en":{"column":2},"parent_code":{"column":3},"type":{"column":4},"nature":{"column":5},"is_group":{"column":6}}}""")!.AsObject();

    private async Task Prepare()
    {
        await PrepareRbac();
        ChartDomain = new ChartOfAccountsService(Resolver, new Hasm.Infrastructure.CompanyDatabase.CompanyAuditedTransactions(Resolver, Audit, Clock), new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        Adapter = new ChartOfAccountsImportAdapter(ChartDomain);
        BuildImportFacade(adapters: [Adapter]);
    }

    private async Task<ImportBatchView> Validate(byte[] data, string name = "chart.csv", bool xlsx = false)
    {
        var u = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, ChartOfAccountsImportAdapter.AdapterCode, name, data, null));
        var s = Ok(await Imports.StageAsync(Admin.Id, Company.Id, u.Id, u.Revision, xlsx ? new StageInput(0, 1, null, null) : new StageInput(0, 1, "utf-8", null)));
        var m = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, Map));
        return Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision));
    }
    private Task<ImportBatchView> Validate(string rows) => Validate(Bytes(Header + rows));

    private async Task<ImportBatchView> Approve(ImportBatchView validated)
    {
        var sub = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, validated.Id, validated.Revision));
        var (user, _) = await Member([PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(user.Login, Company.Id));
        return Ok(await Imports.DecideAsync(user.Id, Company.Id, sub.Id, sub.Revision, true, null));
    }

    private async Task<ImportBatchView> Applied(string rows)
    {
        var v = await Validate(rows);
        var a = await Approve(v);
        return Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
    }

    private async Task<Dictionary<string, AccountView>> Accounts() => (await ChartDomain.ListAsync(Company.Id)).ToDictionary(a => a.Code);
    private async Task<Dictionary<int, ImportRowView>> RowMap(Guid id) => (await Imports.RowsAsync(Company.Id, id, 0, 500, null))!.Rows.ToDictionary(r => r.RowNo);
    private static IEnumerable<string> Codes(JsonNode? issues) => issues is JsonArray a ? a.Select(i => i!["code"]!.GetValue<string>()) : [];
    private async Task<long> AccountEvents() => await Sql<long>($"SELECT count(*) FROM {Ba.Table} WHERE {Ba.EventType} = {ConfigAuditEvents.AccountCreated}");

    [PostgresFact]
    public async Task A_chart_is_imported_into_an_empty_company_with_parents_after_children_leading_zeros_and_both_languages()
    {
        await Prepare();
        // children listed BEFORE their parents, a three-level tree, codes with leading zeros, Arabic and English names, inherited type, default natures
        var rows =
            "010101,الصندوق,Cash,0101,,,\n" +
            "0101,الأصول المتداولة,Current assets,01,,,true\n" +
            "01,الأصول,Assets,,asset,,true\n" +
            "0201,الموردون,,02,,,\n" +
            "02,الالتزامات,Liabilities,,liability,,yes\n" +
            "0301,,Sales,03,,credit,\n" +
            "03,الإيرادات,Revenue,,إيرادات,,true\n" +
            "040101,,Contra revenue,03,,debit,\n";
        var v = await Validate(rows);
        Assert.Equal(8, v.Plan!["summary"]!["operations"]!.GetValue<int>());
        Assert.True(v.Plan["canApply"]!.GetValue<bool>());
        var map = await RowMap(v.Id);
        Assert.All(map.Values, r => Assert.Equal((short)RowOutcome.Valid, r.Status));
        Assert.Equal(("010101", "الصندوق"), (map[2].Normalized!["code"]!.GetValue<string>(), map[2].Normalized!["name_ar"]!.GetValue<string>()));       // leading zeros preserved
        var events = await AccountEvents();
        var applied = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, (await Approve(v)).Id, (await Reload(v.Id)).Revision));
        Assert.Equal(ImportStatus.Applied, applied.Status);
        Assert.Equal(8, applied.Result!["summary"]!["created"]!.GetValue<int>());
        var acc = await Accounts();
        Assert.Equal(8, acc.Count);
        Assert.Equal(acc["0101"].Id, acc["010101"].ParentId);
        Assert.Equal(acc["01"].Id, acc["0101"].ParentId);
        Assert.Null(acc["01"].ParentId);
        Assert.Equal((AccountType.Asset, AccountNature.Debit, true, false), (acc["010101"].Type, acc["010101"].Nature, acc["0101"].IsGroup, acc["010101"].IsGroup));
        Assert.Equal((AccountType.Liability, AccountNature.Credit), (acc["0201"].Type, acc["0201"].Nature));
        Assert.Equal((AccountType.Revenue, AccountNature.Credit, AccountNature.Debit), (acc["0301"].Type, acc["0301"].Nature, acc["040101"].Nature));
        Assert.Equal(("الصندوق", "Cash"), (acc["010101"].NameAr, acc["010101"].NameEn));
        Assert.Null(acc["0201"].NameEn);
        Assert.Equal(events + 8, await AccountEvents());                                                                   // one audit event per account, through the ordinary domain path
        Assert.Equal(8L, await Sql<long>($"SELECT count(*) FROM {Ah.Table}"));                                            // and one history row each
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_real_excel_workbook_with_arabic_names_is_imported()
    {
        await Prepare();
        var xlsx = new XlsxBuilder().Sheet("Sheet1", new object?[][]
        {
            ["code", "name_ar", "name_en", "parent_code", "type", "nature", "is_group"],
            ["1", "الأصول", "Assets", null, "أصول", null, "نعم"],
            ["101", "النقدية", "Cash", "1", null, null, null],
        }).Build();
        var v = await Validate(xlsx, "chart.xlsx", xlsx: true);
        Assert.Equal((2, true), (v.Plan!["summary"]!["operations"]!.GetValue<int>(), v.Plan["canApply"]!.GetValue<bool>()));
        var a = await Approve(v);
        Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
        var acc = await Accounts();
        Assert.Equal((AccountType.Asset, true, "النقدية"), (acc["101"].Type, acc["1"].IsGroup, acc["101"].NameAr));
        Assert.Equal(acc["1"].Id, acc["101"].ParentId);
    }

    [PostgresFact]
    public async Task Existing_accounts_are_preserved_identical_rows_match_exactly_and_differing_rows_are_conflicts_that_only_skip_can_resolve()
    {
        await Prepare();
        var root = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("1", null, "الأصول", "Assets", "asset", null, true, null, null)));
        Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("101", root.Id, "النقدية", "Cash", null, null, false, null, null)));
        var before = await Accounts();
        var v = await Validate(
            "1,الأصول,Assets,,asset,,true\n" +          // identical: exact
            "101,النقدية,Cash in hand,1,,,\n" +         // same code, other English name: conflict
            "102,البنك,Bank,1,,,\n");                   // new, under the existing parent
        var map = await RowMap(v.Id);
        Assert.Equal(((short)RowOutcome.Exact, (short)RowOutcome.Conflict, (short)RowOutcome.Valid), (map[2].Status, map[3].Status, map[4].Status));
        Assert.Contains("exists_with_differences", Codes(map[3].Issues));
        Assert.Equal(["skip"], map[3].AllowedResolutions);
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());                                                                                   // the conflict blocks until it is decided
        Assert.Equal("blocking_rows", (await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, v.Revision)).Code);
        // only skip exists: there is no overwrite, merge or recode resolution
        Assert.NotEqual(ConfigStatus.Ok, (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(3, "overwrite")])).Status);
        var resolved = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(3, "skip")]));
        var revalidated = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, resolved.Id, resolved.Revision));
        Assert.True(revalidated.Plan!["canApply"]!.GetValue<bool>());
        var a = await Approve(revalidated);
        Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
        var after = await Accounts();
        Assert.Equal(3, after.Count);
        Assert.Equal(("Cash", before["101"].Revision), (after["101"].NameEn, after["101"].Revision));                       // the existing account is untouched
        Assert.Equal(root.Id, after["102"].ParentId);
    }

    [PostgresFact]
    public async Task A_new_code_with_the_same_names_as_an_existing_sibling_is_an_ambiguous_match_and_a_conflict_not_a_silent_duplicate()
    {
        await Prepare();
        var root = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("1", null, "الأصول", "Assets", "asset", null, true, null, null)));
        Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("101", root.Id, "النقدية", "Cash", null, null, false, null, null)));
        var v = await Validate("105,,  cash ,1,,,\n106,النقدية,,1,,,\n107,,Petty cash,1,,,\n");
        var map = await RowMap(v.Id);
        Assert.Equal(((short)RowOutcome.Conflict, (short)RowOutcome.Conflict, (short)RowOutcome.Valid), (map[2].Status, map[3].Status, map[4].Status));
        Assert.Contains("possible_duplicate_of_existing", Codes(map[2].Issues));
        Assert.Equal("101", map[2].Issues![0]!["detail"]!.GetValue<string>());
        Assert.Equal(["skip"], map[2].AllowedResolutions);
    }

    [PostgresFact]
    public async Task Hierarchy_and_domain_errors_are_blocking_with_precise_codes()
    {
        await Prepare();
        var parked = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("9", null, "معطل", "Parked", "asset", null, true, null, null)));
        var leaf = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("8", null, "ورقة", "Leaf", "asset", null, false, null, null)));
        Ok(await ChartDomain.DeactivateAsync(Admin.Id, Company.Id, parked.Id, parked.Revision));
        var v = await Validate(
            "A1,x,x,GHOST,asset,,\n" +                  // 2 missing parent
            "B1,x,x,B2,,,true\n" +                      // 3 cycle
            "B2,x,x,B1,,,true\n" +                      // 4 cycle
            "C1,x,x,,,,\n" +                            // 5 root without a type
            "D1,,,,asset,,\n" +                         // 6 no name at all
            "E1,x,x,,asset,,true\n" +                   // 7 fine
            "E2,x,x,E1,liability,,\n" +                 // 8 child type differs from its parent
            "F1,x,x,8,,,\n" +                           // 9 existing parent that is not a group
            "F2,x,x,9,,,\n" +                           // 10 existing parent that is inactive
            "G1,x,x,E2,,,\n" +                          // 11 parent row is invalid
            "H1,x,x,H1,asset,,\n" +                     // 12 its own parent
            "I1,x,x,E1,,,true\n" +                      // 13 fine, group
            "I2,x,x,I1,,,\n" +                          // 14 fine
            "J1!,x,x,,asset,,\n" +                      // 15 invalid code characters
            "K1,x,x,E1,,sideways,\n" +                  // 16 invalid nature
            "L1,x,x,,asset,,,\n" +                      // 17 fine (extra empty cell)
            "M1,x,x,L1,,,\n");                          // 18 parent exists in the file but is NOT a group
        var map = await RowMap(v.Id);
        string First(int row) => Codes(map[row].Issues).FirstOrDefault() ?? "-";
        Assert.Equal("parent_not_found", First(2));
        Assert.Equal("parent_cycle", First(3));
        Assert.Equal("parent_cycle", First(4));
        Assert.Equal("required", First(5));
        Assert.Equal("name_required", First(6));
        Assert.Equal((short)RowOutcome.Valid, map[7].Status);
        Assert.Equal("parent_type_mismatch", First(8));
        Assert.Equal("parent_not_group", First(9));
        Assert.Equal("parent_inactive", First(10));
        Assert.Equal("parent_row_invalid", First(11));
        Assert.Equal("parent_is_self", First(12));
        Assert.Equal((short)RowOutcome.Valid, map[13].Status);
        Assert.Equal((short)RowOutcome.Valid, map[14].Status);
        Assert.Equal((short)RowOutcome.Invalid, map[15].Status);
        Assert.Equal("invalid_value", First(16));
        Assert.Equal("parent_not_group", First(18));
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ac.Table} WHERE {Ac.Code} IN ('A1','E1')"));              // a dry run writes no business data
        _ = leaf;
    }

    [PostgresFact]
    public async Task A_child_of_a_skipped_or_undecided_row_is_a_conflict_of_its_own_that_can_be_skipped_too()
    {
        await Prepare();
        var root = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("1", null, "الأصول", "Assets", "asset", null, true, null, null)));
        Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("101", root.Id, "النقدية", "Cash", null, null, true, null, null)));
        var v = await Validate("105,النقدية,,1,,,true\n10501,x,x,105,,,\n");
        var map = await RowMap(v.Id);
        Assert.Equal(((short)RowOutcome.Conflict, (short)RowOutcome.Conflict), (map[2].Status, map[3].Status));
        Assert.Contains("parent_row_unresolved", Codes(map[3].Issues));
        var skipped = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, v.Revision, [new(2, "skip"), new(3, "skip")]));
        Assert.True((await Imports.DryRunAsync(Admin.Id, Company.Id, skipped.Id, skipped.Revision)).Value!.Plan!["summary"]!["operations"]!.GetValue<int>() == 0);
        var onlyParent = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, (await Reload(v.Id)).Revision, [new(2, "skip"), new(3, null)]));
        var again = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, onlyParent.Id, onlyParent.Revision));
        Assert.Contains("parent_row_skipped", Codes((await RowMap(again.Id))[3].Issues));
    }

    [PostgresFact]
    public async Task Application_is_all_or_nothing_a_failure_midway_rolls_back_every_account_and_the_same_batch_is_retried_after_the_cause_is_removed()
    {
        await Prepare();
        var v = await Validate("A,x,x,,asset,,true\nB,x,x,A,,,\nBOOM,x,x,A,,,\nC,x,x,A,,,\n");
        var a = await Approve(v);
        await Sql($"CREATE OR REPLACE FUNCTION public.hasm_test_boom() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.{Ac.Code} = 'BOOM' THEN RAISE EXCEPTION 'boom'; END IF; RETURN NEW; END $$");
        await Sql($"CREATE TRIGGER hasm_test_boom BEFORE INSERT ON {Ac.Table} FOR EACH ROW EXECUTE FUNCTION public.hasm_test_boom()");
        var events = await AccountEvents();
        var failed = await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision);
        Assert.False(failed.IsOk);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ac.Table}"));                                          // A and B were inserted in the transaction and rolled back
        Assert.Equal(events, await AccountEvents());
        Assert.Equal(ImportStatus.Failed, (await Reload(a.Id)).Status);
        await Sql($"DROP TRIGGER hasm_test_boom ON {Ac.Table}");
        var f = await Reload(a.Id);
        var done = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, f.Id, f.Revision));
        Assert.Equal((ImportStatus.Applied, 4), (done.Status, (await Accounts()).Count));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_change_of_the_chart_after_the_dry_run_makes_the_plan_stale_and_nothing_is_applied()
    {
        await Prepare();
        var v = await Validate("A,x,x,,asset,,true\nB,x,x,A,,,\n");
        var a = await Approve(v);
        Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("OTHER", null, "آخر", "Other", "asset", null, false, null, null)));
        var stale = await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision);
        Assert.Equal("plan_stale", stale.Code);
        Assert.Equal(1, (await Accounts()).Count);
    }

    [PostgresFact]
    public async Task Concurrent_applications_create_every_account_exactly_once()
    {
        await Prepare();
        var v = await Validate("A,x,x,,asset,,true\nB,x,x,A,,,\nC,x,x,A,,,\n");
        var a = await Approve(v);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision)));
        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        Assert.Equal(3, (await Accounts()).Count);
        Assert.Equal(3L, await AccountEvents());
    }

    [PostgresFact]
    public async Task A_large_import_is_applied_completely_and_a_very_deep_hierarchy_is_analysed_without_recursion()
    {
        await Prepare();
        var sb = new StringBuilder("G,Group,Group,,asset,,true\n");
        for (var i = 0; i < 600; i++) sb.Append($"A{i:0000},حساب {i},Account {i},G,,,\n");
        var done = await Applied(sb.ToString());
        Assert.Equal(601, done.Result!["summary"]!["created"]!.GetValue<int>());
        Assert.Equal(601, (await Accounts()).Count);
        // 5,000 levels deep, children listed before parents: the analysis must neither recurse nor be quadratic (a dry run only: nothing is written)
        var chain = new StringBuilder();
        for (var i = 5000; i >= 1; i--) chain.Append($"L{i},x,x,{(i == 1 ? "" : "L" + (i - 1))},{(i == 1 ? "asset" : "")},,true\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var deep = await Validate(chain.ToString());
        Assert.Equal(5000, deep.Plan!["summary"]!["operations"]!.GetValue<int>());
        Assert.True(deep.Plan["canApply"]!.GetValue<bool>());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), watch.Elapsed.ToString());
        var cycle = new StringBuilder();
        for (var i = 1; i <= 3000; i++) cycle.Append($"C{i},x,x,C{i % 3000 + 1},asset,,true\n");
        Assert.Equal(3000, (await Validate(cycle.ToString())).Plan!["summary"]!["invalid"]!.GetValue<int>());                                      // one big cycle: every row refused, no hang
    }

    [PostgresFact]
    public async Task Only_a_holder_of_the_accounts_create_permission_sees_and_uses_the_chart_adapter()
    {
        await Prepare();
        var (viewer, _) = await Member([PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        var denied = await IMg.UploadAsync(viewer.Id, Company.Id, ChartOfAccountsImportAdapter.AdapterCode, "c.csv", Bytes(Header + "A,x,x,,asset,,\n"), null);
        Assert.Equal(ConfigStatus.Forbidden, denied.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table}"));
    }
}
