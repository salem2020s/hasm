using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;

namespace Hasm.Database.IntegrationTests;

/// <summary>Chart of Accounts RBAC (company-wide, separate permissions; system migration 0010) and the HTTP API.</summary>
public abstract class ChartAccessTestBase : HttpApiTestBase
{
    protected ChartOfAccountsService ChartDomain { get; private set; } = null!;
    protected ChartOfAccountsManagementService CMgmt { get; private set; } = null!;

    protected async Task PrepareAccounts()
    {
        await PrepareRbac();
        BuildChartFacade();
    }

    protected void BuildChartFacade()
    {
        ChartDomain = new ChartOfAccountsService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        CMgmt = new ChartOfAccountsManagementService(Rbac, ChartDomain);
    }

    protected static AccountInput Top(string? code, string type = "asset") => new(code, null, "حساب", "Account", type, null, true, null, null);
    protected Task<(Hasm.Infrastructure.Identity.UserRecord User, Guid RoleId)> Holder(params string[] permissions) => Member(permissions, ScopeKind.Company, null);
}

public class ChartAccessTests : ChartAccessTestBase
{
    [PostgresFact]
    public async Task The_company_administrator_preset_holds_every_chart_permission_and_no_other_preset_does()
    {
        await PrepareAccounts();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "view", "create", "update", "activate", "recode", "restructure", "configure" }) Assert.Contains($"company.accounts.{c}", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.accounts"));
        Assert.Empty(Ok(await CMgmt.ListAsync(Admin.Id, Company.Id)));
        Assert.Equal(ChartSettingsView.Defaults, Ok(await CMgmt.GetSettingsAsync(Admin.Id, Company.Id)));
    }

    [PostgresFact]
    public async Task Every_chart_operation_is_a_separate_permission()
    {
        await PrepareAccounts();
        var root = Ok(await CMgmt.CreateAsync(Admin.Id, Company.Id, Top("1")));
        var leaf = Ok(await CMgmt.CreateAsync(Admin.Id, Company.Id, new AccountInput("101", root.Id, "ا", "A", null, null, false, null, null)));
        var other = Ok(await CMgmt.CreateAsync(Admin.Id, Company.Id, Top("2", "liability")));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.AccountsView);
        var (creator, _) = await Holder(PermissionCatalog.Codes.AccountsCreate);
        var (updater, _) = await Holder(PermissionCatalog.Codes.AccountsUpdate);
        var (activator, _) = await Holder(PermissionCatalog.Codes.AccountsActivate);
        var (recoder, _) = await Holder(PermissionCatalog.Codes.AccountsRecode);
        var (mover, _) = await Holder(PermissionCatalog.Codes.AccountsRestructure);
        var (configurer, _) = await Holder(PermissionCatalog.Codes.AccountsConfigure);
        var details = new AccountDetailsInput("اسم", "Name", null, null, null, null);

        Assert.Equal(2 + 1, Ok(await CMgmt.ListAsync(viewer.Id, Company.Id)).Count);
        Assert.Equal(leaf.Id, Ok(await CMgmt.GetAsync(viewer.Id, Company.Id, leaf.Id)).Id);
        Assert.Equal(ChartSettingsView.Defaults, Ok(await CMgmt.GetSettingsAsync(viewer.Id, Company.Id)));
        // Nobody can do more than their own permission: not even view (a holder of a single action sees nothing).
        foreach (var user in new[] { creator, updater, activator, recoder, mover, configurer })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ListAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.GetAsync(user.Id, Company.Id, leaf.Id)).Status);
        }
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.CreateAsync(viewer.Id, Company.Id, Top("9"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.UpdateAsync(viewer.Id, Company.Id, leaf.Id, 1, details)).Status);
        Ok(await CMgmt.CreateAsync(creator.Id, Company.Id, Top("9")));
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.UpdateAsync(creator.Id, Company.Id, leaf.Id, 1, details)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ApplyTemplateAsync(updater.Id, Company.Id, "generic")).Status);
        Assert.Equal("Name", Ok(await CMgmt.UpdateAsync(updater.Id, Company.Id, leaf.Id, 1, details)).NameEn);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.RecodeAsync(updater.Id, Company.Id, leaf.Id, 2, new AccountCodeInput("900"))).Status);        // updating is not re-coding
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.MoveAsync(updater.Id, Company.Id, leaf.Id, 2, new AccountMoveInput(null))).Status);           // nor restructuring
        Assert.Equal("900", Ok(await CMgmt.RecodeAsync(recoder.Id, Company.Id, leaf.Id, 2, new AccountCodeInput("900"))).Code);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.DeactivateAsync(recoder.Id, Company.Id, leaf.Id, 3)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.MoveAsync(recoder.Id, Company.Id, leaf.Id, 3, new AccountMoveInput(null))).Status);
        Assert.Null(Ok(await CMgmt.MoveAsync(mover.Id, Company.Id, leaf.Id, 3, new AccountMoveInput(null))).ParentId);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.RecodeAsync(mover.Id, Company.Id, leaf.Id, 4, new AccountCodeInput("901"))).Status);
        Assert.False(Ok(await CMgmt.DeactivateAsync(activator.Id, Company.Id, other.Id, 1)).IsActive);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.PutSettingsAsync(activator.Id, Company.Id, 0, new ChartSettingsInput(3, true))).Status);
        Assert.Equal(3, Ok(await CMgmt.PutSettingsAsync(configurer.Id, Company.Id, 0, new ChartSettingsInput(3, true))).SegmentWidth);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.CreateAsync(configurer.Id, Company.Id, Top("8"))).Status);
    }

    [PostgresFact]
    public async Task Branch_and_warehouse_grants_never_reach_the_company_wide_chart_and_strangers_get_nothing()
    {
        await PrepareAccounts();
        Ok(await CMgmt.CreateAsync(Admin.Id, Company.Id, Top("1")));
        var main = await Main();
        var (branchUser, _) = await Member([PermissionCatalog.Codes.AccountsView, PermissionCatalog.Codes.AccountsCreate], ScopeKind.Branch, main.Id);
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ListAsync(branchUser.Id, Company.Id)).Status);
        var plain = await NewUser("plain-chart");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var stranger = await NewUser("chart-stranger");
        foreach (var user in new[] { plain, stranger })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ListAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.CreateAsync(user.Id, Company.Id, Top("X"))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ApplyTemplateAsync(user.Id, Company.Id, "generic")).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.UpdateAsync(user.Id, Company.Id, Guid.NewGuid(), 1, new AccountDetailsInput("ا", "A", null, null, null, null))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.RecodeAsync(user.Id, Company.Id, Guid.NewGuid(), 1, new AccountCodeInput("1"))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.MoveAsync(user.Id, Company.Id, Guid.NewGuid(), 1, new AccountMoveInput(null))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.DeactivateAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ReactivateAsync(user.Id, Company.Id, Guid.NewGuid(), 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.PutSettingsAsync(user.Id, Company.Id, 0, new ChartSettingsInput(2, true))).Status);
        }
        var other = await ReadyCompany();
        Assert.Equal(ConfigStatus.Forbidden, (await CMgmt.ListAsync(Admin.Id, other.Id)).Status);                                  // an administrator of company A has nothing in company B
        Assert.Equal(1L, await Count(Company, Ac.Table));
    }
}

/// <summary>End-to-end HTTP tests of /api/v1/company/accounts (both clients use these endpoints).</summary>
public class ChartApiTests : ChartAccessTestBase
{
    private const string Base = "/api/v1/company/accounts";

    private async Task Start()
    {
        await StartApi();
        BuildChartFacade();
    }

    [PostgresFact]
    public async Task Every_chart_endpoint_needs_authentication_and_a_selected_company()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, Base), (HttpMethod.Post, Base), (HttpMethod.Post, $"{Base}/apply-template"), (HttpMethod.Put, $"{Base}/settings"), (HttpMethod.Get, $"{Base}/{id}"),
            (HttpMethod.Put, $"{Base}/{id}"), (HttpMethod.Post, $"{Base}/{id}/recode"), (HttpMethod.Post, $"{Base}/{id}/move"),
            (HttpMethod.Post, $"{Base}/{id}/deactivate"), (HttpMethod.Post, $"{Base}/{id}/activate"),
        };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged")).Status);
        }
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes)
        {
            var r = await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("company_not_selected", r.Error);
        }
    }

    [PostgresFact]
    public async Task The_administrator_builds_and_reorganises_a_chart_end_to_end_with_etags_and_the_caller_capabilities()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var empty = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Contains("no-store", empty.Headers.CacheControl!.ToString());
        Assert.Equal(0, empty.Json.GetProperty("accounts").GetArrayLength());
        Assert.False(empty.Json.GetProperty("usageChecksImplemented").GetBoolean());                                               // never presented as verified
        Assert.Equal("generic", empty.Json.GetProperty("templates")[0].GetProperty("key").GetString());
        Assert.Equal(2, empty.Json.GetProperty("settings").GetProperty("segmentWidth").GetInt32());
        Assert.False(empty.Json.GetProperty("settings").GetProperty("configured").GetBoolean());
        foreach (var c in new[] { "create", "update", "activate", "recode", "restructure", "configure" }) Assert.True(empty.Json.GetProperty("can").GetProperty(c).GetBoolean(), c);

        var root = await Send(HttpMethod.Post, Base, new { nameAr = "الأصول", nameEn = "Assets", type = "asset", isGroup = true }, cookie);
        Assert.Equal(HttpStatusCode.Created, root.Status);
        Assert.Equal("\"1\"", Etag(root));
        var rootId = root.Json.GetProperty("id").GetGuid();
        Assert.Equal("1", root.Json.GetProperty("code").GetString());                                                              // automatic
        Assert.Equal("balance_sheet", root.Json.GetProperty("statement").GetString());
        Assert.False(root.Json.GetProperty("postable").GetBoolean());
        Assert.Contains($"{Base}/{rootId}", root.Headers.Location!.ToString());
        var child = await Send(HttpMethod.Post, Base, new { parentId = rootId, nameEn = "Cash", isGroup = false, currencyPolicy = "base" }, cookie);
        Assert.Equal("101", child.Json.GetProperty("code").GetString());
        Assert.Equal("asset", child.Json.GetProperty("type").GetString());
        Assert.True(child.Json.GetProperty("postable").GetBoolean());
        var childId = child.Json.GetProperty("id").GetGuid();
        Assert.Equal("\"1\"", Etag(await Send(HttpMethod.Get, $"{Base}/{childId}", cookie: cookie)));

        var updated = await Send(HttpMethod.Put, $"{Base}/{childId}", new { nameAr = "الصندوق", nameEn = "Cash box", nature = "debit", isGroup = false, currencyPolicy = "base" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("\"2\"", Etag(updated));
        var recoded = await Send(HttpMethod.Post, $"{Base}/{childId}/recode", new { code = "٢٠٠٠" }, cookie, ifMatch: "\"2\"");
        Assert.Equal("2000", recoded.Json.GetProperty("code").GetString());
        var liab = await Send(HttpMethod.Post, Base, new { nameEn = "Liabilities", type = "liability", isGroup = true }, cookie);
        var moveWrong = await Send(HttpMethod.Post, $"{Base}/{childId}/move", new { parentId = liab.Json.GetProperty("id").GetGuid() }, cookie, ifMatch: "\"3\"");
        Assert.Equal((HttpStatusCode.Conflict, "parent_type_mismatch"), (moveWrong.Status, moveWrong.Error));
        var moved = await Send(HttpMethod.Post, $"{Base}/{childId}/move", new { parentId = (Guid?)null }, cookie, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, moved.Json.GetProperty("parentId").ValueKind);
        var off = await Send(HttpMethod.Post, $"{Base}/{childId}/deactivate", null, cookie, ifMatch: "\"4\"");
        Assert.False(off.Json.GetProperty("isActive").GetBoolean());
        Assert.False(off.Json.GetProperty("postable").GetBoolean());
        var on = await Send(HttpMethod.Post, $"{Base}/{childId}/activate", null, cookie, ifMatch: "\"5\"");
        Assert.True(on.Json.GetProperty("postable").GetBoolean());
        var settings = await Send(HttpMethod.Put, $"{Base}/settings", new { segmentWidth = 3, autoCodes = true }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, settings.Status);
        Assert.Equal("\"1\"", Etag(settings));
        Assert.True((await Send(HttpMethod.Get, Base, cookie: cookie)).Json.GetProperty("settings").GetProperty("configured").GetBoolean());
    }

    [PostgresFact]
    public async Task The_generic_template_over_http_and_the_server_rules_for_validation_and_concurrency()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var applied = await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "generic" }, cookie);
        Assert.Equal(HttpStatusCode.Created, applied.Status);
        Assert.Equal(ChartTemplates.Find("generic")!.Accounts.Count, applied.Json.GetProperty("accounts").GetArrayLength());
        Assert.Equal((HttpStatusCode.Conflict, "chart_not_empty"), ((await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "generic" }, cookie)) is var again ? (again.Status, again.Error) : default));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "bakery" }, cookie)).Status);

        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        var cash = list.Json.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1101");
        var cashId = cash.GetProperty("id").GetGuid();
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{cashId}", new { nameEn = "X", isGroup = false }, cookie)).Status);
        var stale = await Send(HttpMethod.Put, $"{Base}/{cashId}", new { nameEn = "X", isGroup = false }, cookie, ifMatch: "\"9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.Json.GetProperty("currentRevision").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/{cashId}", new { nameEn = "X", isGroup = false }, cookie, ifMatch: "abc")).Status);
        var noName = await Send(HttpMethod.Put, $"{Base}/{cashId}", new { nameAr = " ", nameEn = "", isGroup = false }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, noName.Status);
        Assert.Contains("name_required", noName.Body);
        var dup = await Send(HttpMethod.Post, $"{Base}/{cashId}/recode", new { code = "1102" }, cookie, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.Conflict, "account_code_taken"), (dup.Status, dup.Error));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{cashId}/recode", new { code = "A B" }, cookie, ifMatch: "\"1\"")).Status);
        var notGroup = await Send(HttpMethod.Post, Base, new { parentId = cashId, nameEn = "Under a leaf", isGroup = false }, cookie);
        Assert.Equal((HttpStatusCode.Conflict, "parent_not_group"), (notGroup.Status, notGroup.Error));
        var assets = list.Json.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1").GetProperty("id").GetGuid();
        var cycle = await Send(HttpMethod.Post, $"{Base}/{assets}/move", new { parentId = list.Json.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "11").GetProperty("id").GetGuid() }, cookie, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.Conflict, "account_cycle"), (cycle.Status, cycle.Error));
        var hasActive = await Send(HttpMethod.Post, $"{Base}/{assets}/deactivate", null, cookie, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.Conflict, "account_has_active_children"), (hasActive.Status, hasActive.Error));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie)).Status);
        var missingType = await Send(HttpMethod.Post, Base, new { nameEn = "No type", isGroup = true }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, missingType.Status);
        Assert.Contains("\"type\"", missingType.Body);
    }

    [PostgresFact]
    public async Task State_changes_need_the_csrf_header_and_unauthorized_users_get_403_without_data()
    {
        await Start();
        var admin = await SignIn(Admin);
        var noHeader = await Send(HttpMethod.Post, Base, new { nameEn = "X", type = "asset", isGroup = true }, admin, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Equal("missing_request_header", noHeader.Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "generic" }, admin, header: false)).Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ac.Table}"));

        var created = await Send(HttpMethod.Post, Base, new { nameEn = "Secret assets", type = "asset", isGroup = true }, admin);
        var plain = await NewUser("api-plain-chart");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var cookie = await SignIn(plain);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, Base, null), (HttpMethod.Post, Base, new { nameEn = "Y", type = "asset", isGroup = true }), (HttpMethod.Post, $"{Base}/apply-template", new { template = "generic" }),
            (HttpMethod.Put, $"{Base}/settings", new { segmentWidth = 3, autoCodes = true }), (HttpMethod.Get, $"{Base}/{created.Json.GetProperty("id").GetGuid()}", null),
        })
        {
            var r = await Send(method, path, body, cookie, ifMatch: "\"0\"");
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Equal("forbidden", r.Error);
            Assert.DoesNotContain("Secret", r.Body);
        }
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ac.Table}"));
    }
}
