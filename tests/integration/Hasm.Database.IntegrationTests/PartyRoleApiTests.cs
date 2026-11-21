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
    public async Task The_role_list_is_unchanged_without_parameters_and_pages_and_searches_with_them_under_the_same_permission()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        foreach (var code in new[] { "A1", "A2", "B1" })
        {
            var party = await NewParty(code);
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Roles, new { partyId = party, role = 1, paymentMode = 2, termDays = 30 }, cookie)).Status);
        }
        // no parameters: exactly today's shape (all roles, no total)
        var all = await Send(HttpMethod.Get, Roles, cookie: cookie);
        Assert.Equal(3, all.Json.GetProperty("items").GetArrayLength());
        Assert.False(all.Json.TryGetProperty("total", out _));
        Assert.Equal(3, (await Send(HttpMethod.Get, $"{Roles}?kind=1", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
        // with parameters: one page, ordered by party code, plus the total of the matches
        var search = await Send(HttpMethod.Get, $"{Roles}?search=a", cookie: cookie);
        Assert.Equal(2L, search.Json.GetProperty("total").GetInt64());
        Assert.Equal(["A1", "A2"], search.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("partyCode").GetString()).ToArray());
        var page = await Send(HttpMethod.Get, $"{Roles}?limit=1&offset=1", cookie: cookie);
        Assert.Equal(3L, page.Json.GetProperty("total").GetInt64());
        Assert.Equal("A2", Assert.Single(page.Json.GetProperty("items").EnumerateArray()).GetProperty("partyCode").GetString());
        Assert.True(page.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{Roles}?search=zzz", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
        Assert.Equal(3L, (await Send(HttpMethod.Get, $"{Roles}?limit=1000", cookie: cookie)).Json.GetProperty("total").GetInt64());      // the page size is clamped, not refused
        // malformed parameters are refused
        foreach (var bad in new[] { "limit=0", "limit=-1", "offset=-1", "limit=abc", "kind=7&limit=5", $"search={new string('x', 101)}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Roles}?{bad}", cookie: cookie)).Status);
        // the permission is unchanged: a viewer may search, a user without the permission may not
        var (viewer, _) = await Member([PermissionCatalog.Codes.PartyRolesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Roles}?search=a", cookie: await SignIn(viewer))).Status);
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Roles}?search=a", cookie: await SignIn(outsider))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Roles}?search=a")).Status);
    }

    [PostgresFact]
    public async Task The_role_list_can_be_looked_up_by_party_ids_with_a_bound_and_the_same_permission()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        var parties = new Dictionary<string, Guid>();
        foreach (var code in new[] { "K1", "K2", "K3" })
        {
            parties[code] = await NewParty(code);
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Roles, new { partyId = parties[code], role = 1, paymentMode = 2, termDays = 30 }, cookie)).Status);
        }
        string Ids(params Guid[] ids) => string.Join("&", ids.Select(i => "ids=" + i));
        var two = await Send(HttpMethod.Get, $"{Roles}?{Ids(parties["K3"], parties["K1"])}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, two.Status);
        Assert.Equal(2L, two.Json.GetProperty("total").GetInt64());
        Assert.Equal(["K1", "K3"], two.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("partyCode").GetString()).ToArray());
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{Roles}?{Ids(Guid.NewGuid())}", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Roles}?kind=1&limit=1&{Ids(parties["K1"], parties["K2"])}", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
        // the bound: at most 100 ids; a malformed id is refused
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Roles}?{string.Join("&", Enumerable.Range(0, 100).Select(_ => "ids=" + Guid.NewGuid()))}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Roles}?{string.Join("&", Enumerable.Range(0, 101).Select(_ => "ids=" + Guid.NewGuid()))}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Roles}?ids=not-a-guid", cookie: cookie)).Status);
        // the same permission: a viewer may look up, a user without it may not, and nothing is exposed that the unpaged list does not show
        var (viewer, _) = await Member([PermissionCatalog.Codes.PartyRolesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Roles}?{Ids(parties["K1"])}", cookie: await SignIn(viewer))).Status);
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Roles}?{Ids(parties["K1"])}", cookie: await SignIn(outsider))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Roles}?{Ids(parties["K1"])}")).Status);
    }

    [PostgresFact]
    public async Task The_list_endpoints_answer_a_malformed_query_parameter_with_the_standard_body_naming_the_parameter()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        // party roles: every parameter, units, item groups and customer groups: the paging parameters
        var cases = new (string Url, string Field)[]
        {
            ($"{Roles}?limit=abc", "limit"), ($"{Roles}?limit=0", "limit"), ($"{Roles}?offset=-1", "offset"), ($"{Roles}?offset=x", "offset"), ($"{Roles}?kind=7", "kind"), ($"{Roles}?kind=abc", "kind"),
            ($"{Roles}?ids=not-a-guid", "ids"), ($"{Roles}?{string.Join("&", Enumerable.Range(0, 101).Select(_ => "ids=" + Guid.NewGuid()))}", "ids"), ($"{Roles}?search={new string('x', 101)}", "search"),
            ("/api/v1/company/inventory/units?limit=abc", "limit"), ("/api/v1/company/inventory/units?offset=-1", "offset"), ("/api/v1/company/inventory/units?search=a%0Ab", "search"),
            ("/api/v1/company/inventory/groups?limit=0", "limit"), ("/api/v1/company/inventory/groups?offset=1.5", "offset"),
            ("/api/v1/company/pricing/customer-groups?limit=-2", "limit"), ("/api/v1/company/pricing/customer-groups?offset=zz", "offset"),
        };
        foreach (var (url, field) in cases)
        {
            var r = await Send(HttpMethod.Get, url, cookie: cookie);
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"{url} -> {(int)r.Status}");
            Assert.Equal("invalid_request", r.Error);                                       // the standard body, not the framework's bare 400
            Assert.Equal(field, r.Json.GetProperty("field").GetString());
        }
        // valid and absent values still work, an empty offset or limit counts as absent, and an unauthenticated caller still gets 401 before any parameter is looked at
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Roles}?limit=&offset=", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/company/inventory/units?limit=5&offset=0", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Roles}?limit=abc")).Status);
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
