using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.13 slice S2 over real HTTP: authentication, company selection, branch-scoped RBAC, the validation body, the stable refusal codes, idempotent create, the derived quantities, and no PUT or DELETE route (a link is append-only). Disposable cluster only.</summary>
public class DocumentLinkApiTests : CommercialTestBase
{
    private const string Base = "/api/v1/company/commercial-documents";

    [PostgresFact]
    public async Task The_links_api_authenticates_scopes_branches_validates_and_exposes_no_change_or_delete()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterTestType));   // prepares the company itself; the fixtures are composed after it
        await ComposeAsync();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("AL1");
        var approver = await NewApprover();
        var order = await NewApproved(branch, party, "api-order-0001", approver, CommercialTestType.Name, (item, unit, 10m));
        var delivery = await NewApproved(branch, party, "api-deliv-0001", approver, CommercialTestType.Name, (item, unit, 6m));
        var routes = new (HttpMethod, string)[] { (HttpMethod.Post, $"{Base}/links"), (HttpMethod.Get, $"{Base}/{order.Id}/links"), (HttpMethod.Get, $"{Base}/{order.Id}/line-quantities") };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged")).Status);
        }
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes) Assert.Equal("company_not_selected", (await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany)).Error);

        var cookie = await SignIn(Admin);
        object Body(string key, decimal qty, int kind = 1) => new { fromDocumentId = order.Id, fromLineNo = 1, toDocumentId = delivery.Id, toLineNo = 1, kind, quantity = qty, idempotencyKey = key, unitPrice = 999, createdBy = Guid.NewGuid() };   // unknown fields are ignored
        var created = await Send(HttpMethod.Post, $"{Base}/links", Body("api-link-00001", 4m), cookie);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal(4m, created.Json.GetProperty("quantity").GetDecimal());
        Assert.Equal(Admin.Id, created.Json.GetProperty("createdBy").GetGuid());                                                  // the creator is the session user, never a body field
        Assert.False(created.Json.GetProperty("replayed").GetBoolean());
        var replay = await Send(HttpMethod.Post, $"{Base}/links", Body("api-link-00001", 4m), cookie);
        Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(created.Json.GetProperty("id").GetGuid(), replay.Json.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"{Base}/links", Body("api-link-00001", 5m), cookie)).Status);                   // the key with another payload
        // refusals: stable codes, nothing written
        var over = await Send(HttpMethod.Post, $"{Base}/links", Body("api-link-00002", 4m, 1), cookie);                                                       // the same pair and kind
        Assert.Equal(HttpStatusCode.Conflict, over.Status);
        Assert.Equal("link_exists", over.Error);
        var bad = await Send(HttpMethod.Post, $"{Base}/links", Body("api-link-00003", 0m), cookie);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/links", Body("short", 1m), cookie)).Status);
        // reads
        var links = await Send(HttpMethod.Get, $"{Base}/{order.Id}/links", null, cookie);
        Assert.Equal(1, links.Json.GetProperty("links").GetArrayLength());
        var q = (await Send(HttpMethod.Get, $"{Base}/{order.Id}/line-quantities", null, cookie)).Json.GetProperty("lines")[0];
        Assert.Equal((10m, 4m, 6m), (q.GetProperty("ordered").GetDecimal(), q.GetProperty("fulfilled").GetDecimal(), q.GetProperty("remainingToFulfil").GetDecimal()));
        // a link is append-only: no change or delete route exists
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            Assert.True((await Send(method, $"{Base}/links/{created.Json.GetProperty("id").GetGuid()}", method == HttpMethod.Delete ? null : new { }, cookie, ifMatch: "\"1\"")).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        // permissions: view alone reads but cannot link; nothing at all is refused
        var (viewer, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, branch);
        var viewerCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/links", Body("api-link-00004", 1m), viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/{order.Id}/links", null, viewerCookie)).Status);
        var (nobody, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        var nobodyCookie = await SignIn(nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{order.Id}/links", null, nobodyCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{order.Id}/line-quantities", null, nobodyCookie)).Status);
    }
}
