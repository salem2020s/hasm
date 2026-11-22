using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.Extensions.DependencyInjection;
using Hasm.Infrastructure.Security;

namespace Hasm.Database.IntegrationTests;

/// <summary>Branch scope, company isolation and the privilege classification of the cash and bank account permissions (Phase 3.14 slice S1).</summary>
public class CashAccountAccessTests : CashAccountTestBase
{
    [PostgresFact]
    public async Task A_branch_scoped_user_sees_and_changes_only_the_accounts_of_that_branch_and_another_branch_answers_like_an_unknown_id()
    {
        await Start();
        var a = await MainBranch(); var b = (await Make("B2")).Id;
        var admin = await Manager();
        var idA = IdOf(await Create(admin, Cashbox("ca", a, await Gl("1101"))));
        var idB = IdOf(await Create(admin, Cashbox("cb", b, await Gl("1102"))));

        var cookie = await AsMember(Manage, ScopeKind.Branch, a);
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(["CA"], list.Json.GetProperty("accounts").EnumerateArray().Select(x => x.GetProperty("code").GetString()).ToArray());     // another branch's account is not listed, not even masked
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/{idA}", cookie: cookie)).Status);
        var other = await Send(HttpMethod.Get, $"{Base}/{idB}", cookie: cookie);
        var unknown = await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie);
        Assert.Equal((HttpStatusCode.NotFound, "cash_account_not_found"), (other.Status, other.Error));
        Assert.Equal((other.Status, other.Error), (unknown.Status, unknown.Error));                                                       // no probing of other branches
        var body = new { kind = "cashbox", code = "cb", nameAr = "x", nameEn = "Nope", branchId = b, accountId = await Gl("1103") };
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"{Base}/{idB}", body, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Base}/{idB}/deactivate", new { }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, Cashbox("new", b, await Gl("1104")), cookie)).Status);   // create on a branch the caller does not hold
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, Base, Cashbox("new", a, await Gl("1105")), cookie)).Status);
        // moving an account to a branch the caller does not hold is refused as well
        var move = new { kind = "cashbox", code = "ca", nameAr = "الصندوق الرئيسي", nameEn = "Main cash", branchId = b, accountId = (Guid?)null };
        var g = await Sql<Guid>($"SELECT {CompanySchema.CashAccount.AccountId} FROM {CompanySchema.CashAccount.Table} WHERE {CompanySchema.CashAccount.Id} = '{idA}'");
        var moved = await Send(HttpMethod.Put, $"{Base}/{idA}", new { move.kind, move.code, move.nameAr, move.nameEn, move.branchId, accountId = g }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, moved.Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {CompanySchema.CashAccount.Table} WHERE {CompanySchema.CashAccount.BranchId} = '{b}'"));
    }

    [PostgresFact]
    public async Task Permissions_are_separate_view_create_update_and_activate_and_no_permission_is_forbidden()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101");
        var admin = await Manager();
        var id = IdOf(await Create(admin, Cashbox("c1", main, g)));
        var none = await AsMember([PermissionCatalog.Codes.WarehousesView]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Base, cookie: none)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}", cookie: none)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, Cashbox("c2", main, await Gl("1102")), none)).Status);

        var viewer = await AsMember([PermissionCatalog.Codes.CashAccountsView]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Base, cookie: viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, Cashbox("c2", main, await Gl("1103")), viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/{id}", Cashbox("c1", main, g), viewer, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, viewer, ifMatch: "\"1\"")).Status);

        var creator = await AsMember([PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsCreate]);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, Base, Cashbox("c3", main, await Gl("1104")), creator)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/{id}", Cashbox("c1", main, g), creator, ifMatch: "\"1\"")).Status);

        var activator = await AsMember([PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsActivate]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, activator, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/{id}", Cashbox("c1", main, g), activator, ifMatch: "\"2\"")).Status);
        // the account number is a separate permission again: view, create and update never reveal it
        foreach (var cookie in new[] { admin, viewer, creator, activator }) Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}/account-number", cookie: cookie)).Status);
    }

    [PostgresFact]
    public async Task Another_company_never_sees_the_accounts_and_nothing_is_visible_without_a_selected_company()
    {
        await Start();
        var main = await MainBranch(); var g = await Gl("1101"); var admin = await Manager();
        var id = IdOf(await Create(admin, Cashbox("c1", main, g)));
        var foreign = await ReadyCompany();
        var resolver = SinglePrincipalResolver(foreign);
        var service = new CashAccountService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), new EnvelopeEncryptor(Keys), Probe, Clock);
        Assert.Empty(await service.ListAsync(foreign.Id));
        Assert.Null(await service.GetAsync(foreign.Id, id));
        var none = await SignIn((await Member(Manage, ScopeKind.Company, null)).User, selectCompany: false);
        var r = await Send(HttpMethod.Get, Base, cookie: none);
        Assert.Equal((HttpStatusCode.Conflict, "company_not_selected"), (r.Status, r.Error));
    }

    [PostgresFact]
    public async Task The_cash_permissions_are_classified_update_activate_and_the_account_number_are_privileged_and_view_and_create_are_ordinary()
    {
        await Start();
        Assert.True(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.CashAccountsUpdate));
        Assert.True(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.CashAccountsActivate));
        Assert.True(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.CashAccountsSensitiveView));
        Assert.False(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.CashAccountsView));
        Assert.False(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.CashAccountsCreate));
        Assert.False(PrivilegedAccess.IsPrivilegedRole(null, [PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsCreate]));
        Assert.True(PrivilegedAccess.IsPrivilegedRole(null, [PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsActivate]));
        // every permission of the two resources is classified on purpose: none falls into the "unclassified, so privileged" default by accident
        foreach (var resource in new[] { "company.cashaccounts", "company.cashaccountssensitive" })
            Assert.True(PrivilegedAccess.PrivilegedResources.Contains(resource) || PrivilegedAccess.OrdinaryResources.Contains(resource), resource);
    }
}
