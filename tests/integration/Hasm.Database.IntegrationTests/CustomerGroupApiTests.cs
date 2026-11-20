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
