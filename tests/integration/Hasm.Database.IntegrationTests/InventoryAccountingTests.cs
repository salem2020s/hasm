using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using IV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingVersion;
using IG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingGroup;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.7A: the versioned inventory-accounting configuration (company migration 0015, system migration 0017) against real PostgreSQL. Nothing is posted by it. Disposable cluster only.</summary>
public abstract class InventoryAccountingTestBase : StockTestBase
{
    protected InventoryAccountingService Acc { get; private set; } = null!;
    protected InventoryAccountingManagementService AccMgmt { get; private set; } = null!;
    protected ChartOfAccountsService Chart { get; private set; } = null!;
    protected AccountView Stock = null!, Stock2 = null!, Receivable = null!, Goods = null!, Cogs = null!, Group = null!, Inactive = null!;
    protected ItemGroupView Parent = null!, Child = null!;

    protected async Task PrepareAccounting()
    {
        await Prepare();
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Acc = new InventoryAccountingService(Resolver, tx, Clock);
        AccMgmt = new InventoryAccountingManagementService(Rbac, Acc);
        Chart = new ChartOfAccountsService(Resolver, tx, new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        AccountView Make(string code, string type, bool group = false) => Ok(Chart.CreateAsync(Admin.Id, Company.Id, new AccountInput(code, null, "حساب", "Account " + code, type, null, group, null, null)).GetAwaiter().GetResult());
        Stock = Make("1301", "asset"); Stock2 = Make("1302", "asset"); Receivable = Make("1201", "asset"); Goods = Make("2101", "liability"); Cogs = Make("5101", "expense"); Group = Make("13", "asset", true);
        Inactive = Make("1399", "asset");
        Ok(await Chart.DeactivateAsync(Admin.Id, Company.Id, Inactive.Id, Inactive.Revision));
        Parent = Ok(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("PARENT", "x", "Parent", null, null)));
        Child = Ok(await Inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("CHILD", "x", "Child", Parent.Id, null)));
    }

    protected InventoryAccountingInput Cfg(Guid? def = null, Guid? receipt = null, Guid? issue = null, params (ItemGroupView G, AccountView A)[] groups) =>
        new(def, receipt, issue, [.. groups.Select(g => new InventoryAccountingGroupInput(g.G.Id, g.A.Id))], null);
    protected static void Invalid<T>(ConfigResult<T> r, string field, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, f => f.Field == field && f.Code == code); }
}

public class InventoryAccountingTests : InventoryAccountingTestBase
{
    [PostgresFact]
    public async Task Nothing_is_configured_by_default_and_the_administrator_preset_holds_the_permissions()
    {
        await PrepareAccounting();
        var v = Ok(await AccMgmt.GetAsync(Admin.Id, Company.Id));
        Assert.Null(v.Version);
        Assert.Equal((0L, 0L), (await Rows(IV.Table), await Rows(IG.Table)));
        var admin = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains("company.inventoryaccounting.view", admin.Permissions);
        Assert.Contains("company.inventoryaccounting.configure", admin.Permissions);
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExecuteAsync(Company.Id, work => InventoryAccountingService.ResolveAsync(work, Parent.Id, default), default));
        Assert.Equal("inventory_accounting_not_configured", ex.Message);                                                              // an unconfigured company cannot post
    }

    [PostgresFact]
    public async Task Every_save_is_a_new_immutable_version_an_unchanged_save_creates_nothing_and_concurrency_is_fenced_by_the_version_number()
    {
        await PrepareAccounting();
        var audit0 = await Rows(Audit_.Table);
        var v1 = Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Stock.Id, Goods.Id, Cogs.Id, (Parent, Stock2))));
        Assert.Equal((1, Stock.Id, Goods.Id, Cogs.Id), (v1.Version!.VersionNo, v1.DefaultInventoryAccountId, v1.ReceiptCounterAccountId, v1.IssueCounterAccountId));
        Assert.Equal(Stock2.Id, v1.GroupAccounts[Parent.Id]);
        Assert.Equal(audit0 + 1, await Rows(Audit_.Table));
        var same = Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, Cfg(Stock.Id, Goods.Id, Cogs.Id, (Parent, Stock2))));          // identical: no new version, no audit
        Assert.Equal((1, audit0 + 1), (same.Version!.VersionNo, await Rows(Audit_.Table)));
        var v2 = Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, Cfg(Stock2.Id, Goods.Id, Cogs.Id)));
        Assert.Equal((2, 0), (v2.Version!.VersionNo, v2.GroupAccounts.Count));
        Assert.Equal(Stock2.Id, (await Acc.GetAsync(Company.Id)).DefaultInventoryAccountId);                                          // the current one
        Assert.Equal(Stock.Id, (await Acc.GetAsync(Company.Id, 1)).DefaultInventoryAccountId);                                       // the old one is still exactly what it was
        Assert.Equal([2, 1], (await Acc.ListVersionsAsync(Company.Id)).Select(x => x.VersionNo));
        Fails(await Acc.SaveAsync(Admin.Id, Company.Id, 1, Cfg(Stock.Id)), ConfigStatus.PreconditionFailed, "accounting_version_conflict");
        Fails(await Acc.SaveAsync(Admin.Id, Company.Id, null, Cfg(Stock.Id)), ConfigStatus.PreconditionRequired, "precondition_required");
        // two simultaneous saves from the same version: exactly one wins
        var race = await Task.WhenAll(new[] { Stock.Id, Stock2.Id }.Select(a => Acc.SaveAsync(Admin.Id, Company.Id, 2, Cfg(a, Goods.Id, Cogs.Id))));
        Assert.Equal(1, race.Count(r => r.IsOk));
        Assert.Equal(3, (await Acc.GetAsync(Company.Id)).Version!.VersionNo);
        // PostgreSQL keeps versions immutable even against direct SQL
        Assert.NotNull(await Raw($"UPDATE {IV.Table} SET {IV.Note} = 'x'"));
        Assert.NotNull(await Raw($"DELETE FROM {IV.Table}"));
        Assert.NotNull(await Raw($"TRUNCATE {IV.Table}"));
        Assert.NotNull(await Raw($"DELETE FROM {IG.Table}"));
        Assert.NotNull(await Raw($"UPDATE {IG.Table} SET {IG.AccountId} = '{Stock.Id}'"));
    }

    [PostgresFact]
    public async Task Accounts_are_validated_against_the_live_chart_and_nothing_is_saved_when_any_is_wrong()
    {
        await PrepareAccounting();
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Goods.Id)), "defaultInventoryAccountId", "inventory_account_must_be_asset");                         // a liability is not inventory
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Group.Id)), "defaultInventoryAccountId", "account_not_postable");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Inactive.Id)), "defaultInventoryAccountId", "account_inactive");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(Guid.NewGuid(), null, null, [], null)), "defaultInventoryAccountId", "account_not_found");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Stock.Id, Stock.Id)), "receiptCounterAccountId", "counter_equals_inventory_account");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Stock.Id, null, Stock2.Id, (Parent, Stock2))), "issueCounterAccountId", "counter_equals_inventory_account");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(null, null, null, [new InventoryAccountingGroupInput(Guid.NewGuid(), Stock.Id)], null)), "groups[0].groupId", "group_not_found");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(null, null, null, [new InventoryAccountingGroupInput(Parent.Id, Stock.Id), new InventoryAccountingGroupInput(Parent.Id, Stock2.Id)], null)), "groups[1].groupId", "duplicate_group");
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(null, null, null, [new InventoryAccountingGroupInput(Parent.Id, Cogs.Id)], null)), "groups[0].accountId", "inventory_account_must_be_asset");
        Assert.Equal(0L, await Rows(IV.Table));
        // a partial configuration is allowed (a default alone, or counters alone): what is missing simply cannot be posted yet
        Assert.Equal(1, Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(Stock.Id, null, null, [], "first"))).Version!.VersionNo);
    }

    [PostgresFact]
    public async Task A_foreign_currency_only_account_is_refused_because_stock_is_valued_in_the_base_currency()
    {
        await PrepareAccounting();
        await Sql($"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('USD', true, true, 1, now()), ('EUR', false, true, 1, now())");
        var eur = Ok(await Chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("1303", null, "يورو", "Euro stock", "asset", null, false, "specific", "EUR")));
        var usd = Ok(await Chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("1304", null, "دولار", "Dollar stock", "asset", null, false, "specific", "USD")));
        Invalid(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(eur.Id)), "defaultInventoryAccountId", "account_currency_not_base");
        Assert.Equal(1, Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(usd.Id))).Version!.VersionNo);                              // limited to the BASE currency is fine
    }

    [PostgresFact]
    public async Task The_inventory_account_of_a_group_is_its_own_then_the_nearest_parent_then_the_company_default()
    {
        await PrepareAccounting();
        Assert.Equal(Stock2.Id, InventoryAccountResolver.Resolve(new Dictionary<Guid, Guid> { [Parent.Id] = Stock2.Id }, [Child.Id, Parent.Id], Stock.Id));
        Assert.Equal(Stock.Id, InventoryAccountResolver.Resolve(new Dictionary<Guid, Guid>(), [Child.Id, Parent.Id], Stock.Id));
        Assert.Equal(Stock2.Id, InventoryAccountResolver.Resolve(new Dictionary<Guid, Guid> { [Child.Id] = Stock2.Id, [Parent.Id] = Stock.Id }, [Child.Id, Parent.Id], null));
        Assert.Null(InventoryAccountResolver.Resolve(new Dictionary<Guid, Guid>(), [Child.Id], null));
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Stock.Id, Goods.Id, Cogs.Id, (Parent, Stock2))));
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        ResolvedInventoryAccounts? childResolved = null, rootResolved = null;
        await tx.ExecuteAsync(Company.Id, async work =>
        {
            childResolved = await InventoryAccountingService.ResolveAsync(work, Child.Id, default);                                      // CHILD has no mapping: PARENT's applies
            rootResolved = await InventoryAccountingService.ResolveAsync(work, Parent.Id, default);
            await work.AppendAuditAsync(new Hasm.Infrastructure.Audit.AuditEventData(1160, ActorUserId: Admin.Id, Detail: "resolve probe"), default);
            return 0;
        });
        Assert.Equal((Stock2.Id, 1, Goods.Id, Cogs.Id), (childResolved!.InventoryAccountId, childResolved.VersionNo, childResolved.ReceiptCounterAccountId, childResolved.IssueCounterAccountId));
        Assert.Equal(Stock2.Id, rootResolved!.InventoryAccountId);
    }

    [PostgresFact]
    public async Task Viewing_and_configuring_are_separate_company_wide_permissions()
    {
        await PrepareAccounting();
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, Cfg(Stock.Id)));
        var (viewer, _) = await Member([PermissionCatalog.Codes.InventoryAccountingView], ScopeKind.Company, null);
        var (nobody, _) = await Member([PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.Equal(1, Ok(await AccMgmt.GetAsync(viewer.Id, Company.Id)).Version!.VersionNo);
        Assert.Single(Ok(await AccMgmt.VersionsAsync(viewer.Id, Company.Id)));
        Assert.NotEmpty(Ok(await AccMgmt.LookupsAsync(viewer.Id, Company.Id)).Accounts);                                              // a viewer must be able to read the names of the accounts
        Fails(await AccMgmt.LookupsAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await AccMgmt.SaveAsync(viewer.Id, Company.Id, 1, Cfg(Stock2.Id)), ConfigStatus.Forbidden, "forbidden");
        Fails(await AccMgmt.GetAsync(nobody.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(1, (await Acc.GetAsync(Company.Id)).Version!.VersionNo);
        var (configurer, _) = await Member([PermissionCatalog.Codes.InventoryAccountingConfigure], ScopeKind.Company, null);
        Assert.NotEmpty(Ok(await AccMgmt.LookupsAsync(configurer.Id, Company.Id)).Accounts);
        Assert.Equal(2, Ok(await AccMgmt.SaveAsync(configurer.Id, Company.Id, 1, Cfg(Stock2.Id))).Version!.VersionNo);
    }
}

public class InventoryAccountingApiTests : InventoryAccountingTestBase
{
    private const string Base = "/api/v1/company/inventory/accounting";

    [PostgresFact]
    public async Task The_configuration_endpoints_need_authentication_and_work_end_to_end_with_versions_as_etags()
    {
        await StartApi();
        await PrepareAccounting();
        foreach (var (m, p) in new[] { (HttpMethod.Get, Base), (HttpMethod.Get, Base + "/versions"), (HttpMethod.Put, Base) })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(m, p, m == HttpMethod.Get ? null : new { })).Status);
        var cookie = await SignIn(Admin);
        var first = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal((HttpStatusCode.OK, "\"0\""), (first.Status, Etag(first)));
        Assert.True(first.Json.GetProperty("canConfigure").GetBoolean());
        Assert.True(first.Json.GetProperty("accounts").GetArrayLength() >= 5);
        Assert.Equal(3, first.Json.GetProperty("groups").GetArrayLength());
        var body = new { defaultInventoryAccountId = Stock.Id, receiptCounterAccountId = Goods.Id, issueCounterAccountId = Cogs.Id, groups = new[] { new { groupId = Parent.Id, accountId = Stock2.Id } }, note = "initial" };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Put, Base, body, cookie)).Status);
        var saved = await Send(HttpMethod.Put, Base, body, cookie, ifMatch: "\"0\"");
        Assert.Equal((HttpStatusCode.OK, "\"1\""), (saved.Status, Etag(saved)));
        Assert.Equal(Stock.Id, saved.Json.GetProperty("defaultInventoryAccountId").GetGuid());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, Base, body with { note = "again" }, cookie, ifMatch: "\"0\"")).Status);
        var bad = await Send(HttpMethod.Put, Base, new { defaultInventoryAccountId = Goods.Id }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("inventory_account_must_be_asset", bad.Json.ToString());
        Assert.Equal(1, (await Send(HttpMethod.Get, Base + "/versions", cookie: cookie)).Json.GetProperty("versions").GetArrayLength());
        var (viewer, _) = await Member([PermissionCatalog.Codes.InventoryAccountingView], ScopeKind.Company, null);
        var v = await Send(HttpMethod.Get, Base, cookie: await SignIn(viewer));
        Assert.False(v.Json.GetProperty("canConfigure").GetBoolean());
        Assert.True(v.Json.GetProperty("accounts").GetArrayLength() > 0);                                                              // the names are readable, the editing is not
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, Base, body, await SignIn(viewer), ifMatch: "\"1\"")).Status);
    }
}
