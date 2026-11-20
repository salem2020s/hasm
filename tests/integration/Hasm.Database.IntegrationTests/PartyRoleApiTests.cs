using System.Net;
using Hasm.Infrastructure.Access;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.9B: the customer/supplier and sales-representative API over real HTTP: authentication, company selection, RBAC, optimistic concurrency, no DELETE, and the end-to-end flow.</summary>
public class PartyRoleApiTests : HttpApiTestBase
{
    private const string Roles = "/api/v1/company/party-roles", Reps = "/api/v1/company/sales-representatives";

    private async Task<Guid> NewParty(string code)
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{id}', '{code}', 'p-{code}', true, 1, 1, '{Admin.Id}', now(), now())");
        return id;
    }

    [PostgresFact]
    public async Task Every_endpoint_needs_authentication_and_a_selected_company_and_nothing_can_be_deleted()
    {
        await StartApi();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, Roles), (HttpMethod.Post, Roles), (HttpMethod.Put, $"{Roles}/{id}"),
            (HttpMethod.Get, Reps), (HttpMethod.Post, Reps), (HttpMethod.Put, $"{Reps}/{id}"),
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
        var cookie = await SignIn(Admin);
        foreach (var path in new[] { Roles, $"{Roles}/{id}", Reps, $"{Reps}/{id}" })
            Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, path, null, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Roles}?kind=7", cookie: cookie)).Status);
    }

    [PostgresFact]
    public async Task The_administrator_manages_roles_and_representatives_end_to_end_with_etags_and_account_validation()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        var party = await NewParty("C1");
        var asset = await Send(HttpMethod.Post, "/api/v1/company/accounts", new { code = "1201", nameAr = "الذمم المدينة", nameEn = "Receivables", type = "asset" }, cookie);
        var liability = await Send(HttpMethod.Post, "/api/v1/company/accounts", new { code = "2101", nameAr = "الذمم الدائنة", nameEn = "Payables", type = "liability" }, cookie);
        Assert.Equal(HttpStatusCode.Created, asset.Status);
        var assetId = asset.Json.GetProperty("id").GetGuid(); var liabilityId = liability.Json.GetProperty("id").GetGuid();

        var empty = await Send(HttpMethod.Get, Roles, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Equal(0, empty.Json.GetProperty("items").GetArrayLength());
        Assert.True(empty.Json.GetProperty("can").GetProperty("configure").GetBoolean());

        var rep = await Send(HttpMethod.Post, Reps, new { code = "r1", nameAr = "مندوب", nameEn = "Rep" }, cookie);
        Assert.Equal(HttpStatusCode.OK, rep.Status);
        Assert.Equal("\"1\"", Etag(rep));
        var repId = rep.Json.GetProperty("id").GetGuid();

        var wrong = await Send(HttpMethod.Post, Roles, new { partyId = party, role = 1, paymentMode = 2, termDays = 30, controlAccountId = liabilityId }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("customer_account_must_be_asset", wrong.Body);
        var role = await Send(HttpMethod.Post, Roles, new { partyId = party, role = 1, paymentMode = 2, termDays = 30, creditLimit = 2500.75, controlAccountId = assetId, representativeId = repId }, cookie);
        Assert.Equal(HttpStatusCode.OK, role.Status);
        Assert.Equal("\"1\"", Etag(role));
        var roleId = role.Json.GetProperty("id").GetGuid();
        Assert.Equal((2500.75m, "1201", repId), (role.Json.GetProperty("creditLimit").GetDecimal(), role.Json.GetProperty("controlAccountCode").GetString(), role.Json.GetProperty("representativeId").GetGuid()));
        var dup = await Send(HttpMethod.Post, Roles, new { partyId = party, role = 1, paymentMode = 1 }, cookie);
        Assert.Equal(HttpStatusCode.Conflict, dup.Status);
        Assert.Equal("role_exists", dup.Error);
        // omitted required fields are reported, never defaulted
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Roles, new { role = 2, paymentMode = 1 }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Roles, new { partyId = party, role = 2 }, cookie)).Status);

        // revision fence
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Roles}/{roleId}", new { paymentMode = 2, termDays = 45 }, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Roles}/{roleId}", new { paymentMode = 2, termDays = 45 }, cookie, ifMatch: "\"9\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Roles}/{roleId}", new { paymentMode = 2, termDays = 45 }, cookie, ifMatch: "abc")).Status);
        var updated = await Send(HttpMethod.Put, $"{Roles}/{roleId}", new { paymentMode = 2, termDays = 45, controlAccountId = assetId }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("\"2\"", Etag(updated));
        Assert.Equal(45, updated.Json.GetProperty("termDays").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, updated.Json.GetProperty("creditLimit").ValueKind);   // PUT replaces the terms: an omitted limit clears it
        var stillOn = await Send(HttpMethod.Put, $"{Roles}/{roleId}", new { paymentMode = 2, termDays = 45, controlAccountId = assetId, isActive = false }, cookie, ifMatch: "\"2\"");
        Assert.False(stillOn.Json.GetProperty("isActive").GetBoolean());
        var kept = await Send(HttpMethod.Put, $"{Roles}/{roleId}", new { paymentMode = 2, termDays = 45, controlAccountId = assetId }, cookie, ifMatch: "\"3\"");
        Assert.False(kept.Json.GetProperty("isActive").GetBoolean());                                                                       // omitted flag never reactivates

        var customers = await Send(HttpMethod.Get, $"{Roles}?kind=1", cookie: cookie);
        Assert.Equal(1, customers.Json.GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{Roles}?kind=2", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
        var repRename = await Send(HttpMethod.Put, $"{Reps}/{repId}", new { nameAr = "مندوب جديد" }, cookie, ifMatch: "\"1\"");
        Assert.Equal("\"2\"", Etag(repRename));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"{Reps}/{Guid.NewGuid()}", new { nameAr = "x" }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {PR.Table}"));
    }
    [PostgresFact]
    public async Task A_user_without_the_permissions_reads_and_writes_nothing_and_a_viewer_cannot_configure()
    {
        await StartApi();
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        var outsiderCookie = await SignIn(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Roles, cookie: outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Reps, cookie: outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Reps, new { code = "R", nameAr = "م" }, outsiderCookie)).Status);

        var (viewer, _) = await Member([PermissionCatalog.Codes.PartyRolesView], ScopeKind.Company, null);
        var viewerCookie = await SignIn(viewer);
        var list = await Send(HttpMethod.Get, Roles, cookie: viewerCookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.False(list.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Roles, new { partyId = Guid.NewGuid(), role = 1, paymentMode = 1 }, viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Reps, cookie: viewerCookie)).Status);                           // representatives need their own permission
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {PR.Table}"));
    }
}
