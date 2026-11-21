using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Parties;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.11 customer groups over real HTTP: authentication, RBAC (the pricing permissions), If-Match revisions (assignment revision 0 = never assigned), no DELETE, the customer-or-group scope, and a quote whose customer group comes ONLY from the stored assignment.</summary>
public class CustomerGroupApiTests : HttpApiTestBase
{
    private const string Base = "/api/v1/company/pricing";

    [PostgresFact]
    public async Task The_customer_group_list_filters_by_active_with_a_total_that_matches_and_refuses_a_malformed_value()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        string? bId = null;
        foreach (var code in new[] { "a", "b", "c" })
        {
            var created = await Send(HttpMethod.Post, $"{Base}/customer-groups", new { code, nameAr = "مجموعة " + code, nameEn = "Group " + code }, cookie);
            Assert.Equal(HttpStatusCode.OK, created.Status);
            if (code == "b") bId = created.Json.GetProperty("id").GetGuid().ToString();
        }
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/customer-groups/{bId}", new { nameAr = "مجموعة b", nameEn = "Group b", isActive = false }, cookie, ifMatch: "\"1\"")).Status);
        string[] Codes(ApiResponse r) => [.. r.Json.GetProperty("customerGroups").EnumerateArray().Select(g => g.GetProperty("code").GetString()!)];

        var onlyActive = await Send(HttpMethod.Get, $"{Base}/customer-groups?active=true", cookie: cookie);
        Assert.Equal(["A", "C"], Codes(onlyActive));
        Assert.Equal(2L, onlyActive.Json.GetProperty("total").GetInt64());               // the total counts the filtered rows
        var onlyInactive = await Send(HttpMethod.Get, $"{Base}/customer-groups?active=FALSE", cookie: cookie);
        Assert.Equal(["B"], Codes(onlyInactive));
        Assert.Equal(1L, onlyInactive.Json.GetProperty("total").GetInt64());
        var page = await Send(HttpMethod.Get, $"{Base}/customer-groups?active=true&limit=1&offset=1", cookie: cookie);
        Assert.Equal(["C"], Codes(page));
        Assert.Equal(2L, page.Json.GetProperty("total").GetInt64());
        // without the parameter nothing changes: all rows, and no total unless another paging parameter is used
        var all = await Send(HttpMethod.Get, $"{Base}/customer-groups", cookie: cookie);
        Assert.Equal(["A", "B", "C"], Codes(all));
        Assert.False(all.Json.TryGetProperty("total", out _));
        Assert.Equal(3L, (await Send(HttpMethod.Get, $"{Base}/customer-groups?limit=10", cookie: cookie)).Json.GetProperty("total").GetInt64());
        // a malformed value is the standard validation body naming the parameter
        foreach (var bad in new[] { "active=maybe", "active=1", "active=yes" })
        {
            var r = await Send(HttpMethod.Get, $"{Base}/customer-groups?{bad}", cookie: cookie);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("invalid_request", r.Error);
            var named = Assert.Single(r.Json.GetProperty("fields").EnumerateArray());
            Assert.Equal(("active", "invalid_value"), (named.GetProperty("field").GetString(), named.GetProperty("code").GetString()));
        }
        // the permission is unchanged
        var (viewer, _) = await Member([PermissionCatalog.Codes.PricingView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/customer-groups?active=true", cookie: await SignIn(viewer))).Status);
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/customer-groups?active=true", cookie: await SignIn(outsider))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/customer-groups?active=true")).Status);
    }

    [PostgresFact]
    public async Task The_customer_group_list_is_unchanged_without_parameters_and_pages_and_searches_with_them_under_the_pricing_view_permission()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        foreach (var code in new[] { "a", "b", "c" })
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/customer-groups", new { code, nameAr = "مجموعة " + code, nameEn = "Group " + code }, cookie)).Status);
        var all = await Send(HttpMethod.Get, $"{Base}/customer-groups", cookie: cookie);
        Assert.Equal(3, all.Json.GetProperty("customerGroups").GetArrayLength());
        Assert.False(all.Json.TryGetProperty("total", out _));                      // today's shape
        var page = await Send(HttpMethod.Get, $"{Base}/customer-groups?limit=2&offset=1", cookie: cookie);
        Assert.Equal(3L, page.Json.GetProperty("total").GetInt64());
        Assert.Equal(["B", "C"], page.Json.GetProperty("customerGroups").EnumerateArray().Select(g => g.GetProperty("code").GetString()).ToArray());
        Assert.True(page.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        Assert.Equal(1L, (await Send(HttpMethod.Get, $"{Base}/customer-groups?search=group%20c", cookie: cookie)).Json.GetProperty("total").GetInt64());
        Assert.Equal(3L, (await Send(HttpMethod.Get, $"{Base}/customer-groups?limit=1000", cookie: cookie)).Json.GetProperty("total").GetInt64());      // clamped, not refused
        foreach (var bad in new[] { "limit=0", "limit=-1", "offset=-1", "limit=abc", $"search={new string('x', 101)}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/customer-groups?{bad}", cookie: cookie)).Status);
        var (viewer, _) = await Member([PermissionCatalog.Codes.PricingView], ScopeKind.Company, null);
        var viewerPage = await Send(HttpMethod.Get, $"{Base}/customer-groups?search=a", cookie: await SignIn(viewer));
        Assert.Equal(HttpStatusCode.OK, viewerPage.Status);
        Assert.False(viewerPage.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/customer-groups?search=a", cookie: await SignIn(outsider))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/customer-groups?search=a")).Status);
    }

    [PostgresFact]
    public async Task Customer_groups_and_assignments_are_managed_over_http_with_rbac_revisions_and_a_server_derived_quote_group()
    {
        await StartApi();
        var id = Guid.NewGuid();
        foreach (var (method, path) in new (HttpMethod, string)[] { (HttpMethod.Get, $"{Base}/customer-groups"), (HttpMethod.Post, $"{Base}/customer-groups"), (HttpMethod.Put, $"{Base}/customer-groups/{id}"), (HttpMethod.Get, $"{Base}/customer-assignments"), (HttpMethod.Put, $"{Base}/customer-assignments/{id}") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, method == HttpMethod.Get ? null : new { })).Status);

        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        var inv = new CompanyInventoryService(Resolver, tx, Clock);
        var unit = inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("PCS", "وحدة", "Piece", 0)).Result.Value!;
        var grp = inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("G1", "مجموعة", "Group", null, 1)).Result.Value!;
        var item = inv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput("I1", "صنف", "Item", 1, grp.Id, unit.Id, null, null, false, false, false, null, [])).Result.Value!;
        var party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'C1', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
        var roles = new PartyRoleService(Resolver, tx, Clock);
        Assert.True((await roles.CreateRoleAsync(Admin.Id, Company.Id, new PartyRoleInput(party, PartyRoleKind.Customer, PartyPaymentMode.Cash, null, null, null, null))).IsOk);

        var cookie = await SignIn(Admin);
        var created = await Send(HttpMethod.Post, $"{Base}/customer-groups", new { code = "vip", nameAr = "كبار", nameEn = "VIP" }, cookie);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var groupId = created.Json.GetProperty("id").GetGuid();
        Assert.Equal("VIP", created.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/customer-groups", new { code = "VIP", nameAr = "مكرر" }, cookie)).Status);          // code_taken
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/customer-groups/{groupId}", new { nameAr = "ق" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/customer-groups/{groupId}", new { nameAr = "ق" }, cookie, ifMatch: "\"9\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/customer-groups/{groupId}", new { code = "OTHER", nameAr = "ق" }, cookie, ifMatch: "\"1\"")).Status);   // code_immutable
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"{Base}/customer-groups/{Guid.NewGuid()}", new { nameAr = "ق" }, cookie, ifMatch: "\"1\"")).Status);

        var listed = await Send(HttpMethod.Get, $"{Base}/customer-assignments", cookie: cookie);
        Assert.Equal(0L, listed.Json.GetProperty("assignments")[0].GetProperty("revision").GetInt64());
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/customer-assignments/{party}", new { customerGroupId = groupId }, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/customer-assignments/{party}", new { customerGroupId = groupId }, cookie, ifMatch: "\"4\"")).Status);
        var assigned = await Send(HttpMethod.Put, $"{Base}/customer-assignments/{party}", new { customerGroupId = groupId }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, assigned.Status);
        Assert.Equal(("VIP", 1L), (assigned.Json.GetProperty("customerGroupCode").GetString(), assigned.Json.GetProperty("revision").GetInt64()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/customer-assignments/{Guid.NewGuid()}", new { customerGroupId = groupId }, cookie, ifMatch: "\"0\"")).Status);   // not a customer

        // a group list: customer AND group is refused, a group alone is priced for the member only; a group claimed in the quote body is ignored
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "BOTH", nameAr = "ق", priority = 1, currency = "JOD", validFrom = "2026-01-01", customerId = party, customerGroupId = groupId }, cookie)).Status);
        var general = await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "GEN", nameAr = "ق", priority = 1, currency = "JOD", validFrom = "2026-01-01" }, cookie);
        var vipList = await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "VIPL", nameAr = "ق", priority = 9, currency = "JOD", validFrom = "2026-01-01", customerGroupId = groupId }, cookie);
        Assert.Equal(groupId, vipList.Json.GetProperty("customerGroupId").GetGuid());
        await Send(HttpMethod.Post, $"{Base}/price-lists/{general.Json.GetProperty("id").GetGuid()}/entries", new { itemId = item.Id, unitId = unit.Id, minQuantity = 0, unitPrice = 10 }, cookie);
        await Send(HttpMethod.Post, $"{Base}/price-lists/{vipList.Json.GetProperty("id").GetGuid()}/entries", new { itemId = item.Id, unitId = unit.Id, minQuantity = 0, unitPrice = 8 }, cookie);
        var member = await Send(HttpMethod.Post, $"{Base}/quote", new { date = "2026-06-15", currency = "JOD", itemId = item.Id, unitId = unit.Id, quantity = 1, customerId = party }, cookie);
        Assert.Equal(8m, member.Json.GetProperty("result").GetProperty("listUnitPrice").GetDecimal());
        var claim = await Send(HttpMethod.Post, $"{Base}/quote", new { date = "2026-06-15", currency = "JOD", itemId = item.Id, unitId = unit.Id, quantity = 1, customerGroupId = groupId }, cookie);
        Assert.Equal(10m, claim.Json.GetProperty("result").GetProperty("listUnitPrice").GetDecimal());

        // authorization and no delete
        var (viewer, _) = await Member([PermissionCatalog.Codes.PricingView], ScopeKind.Company, null);
        var viewerCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/customer-groups", cookie: viewerCookie)).Status);
        var viewerAssignments = await Send(HttpMethod.Get, $"{Base}/customer-assignments", cookie: viewerCookie);   // a pricing-view-only user gets ids and group data, never customer names or codes
        Assert.Equal(HttpStatusCode.OK, viewerAssignments.Status);
        var first = viewerAssignments.Json.GetProperty("assignments")[0];
        Assert.Equal(party, first.GetProperty("partyId").GetGuid());
        Assert.Equal("VIP", first.GetProperty("customerGroupCode").GetString());
        Assert.False(first.TryGetProperty("partyCode", out _));
        Assert.False(first.TryGetProperty("partyName", out _));
        Assert.DoesNotContain("\"C1\"", viewerAssignments.Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/customer-groups", new { code = "X", nameAr = "س" }, viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/customer-assignments/{party}", new { customerGroupId = (Guid?)null }, viewerCookie, ifMatch: "\"1\"")).Status);
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/customer-assignments", cookie: await SignIn(outsider))).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"{Base}/customer-groups/{groupId}", null, cookie)).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"{Base}/customer-assignments/{party}", null, cookie)).Status);
    }
}
