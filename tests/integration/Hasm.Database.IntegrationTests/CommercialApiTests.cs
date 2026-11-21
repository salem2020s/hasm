using System.Net;
using Hasm.Infrastructure.Access;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.13 slice S1a over real HTTP: authentication, company selection, branch-scoped RBAC, If-Match revisions (428/412), no DELETE and no approval route, idempotent create, and a body that carries no price, tax, cost or snapshot. Disposable cluster only.</summary>
public class CommercialApiTests : CommercialTestBase
{
    private const string Base = "/api/v1/company/commercial-documents";

    [PostgresFact]
    public async Task The_documents_api_authenticates_fences_revisions_scopes_branches_and_exposes_no_delete_or_approval()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterTestType));   // prepares the company itself; the fixtures are composed after it                                                  // the internal test type exists only in the test composition
        await ComposeAsync();
        var a = await NewBranchId("BA"); var b2 = await NewBranchId("BB"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("AP1");
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[] { (HttpMethod.Get, Base), (HttpMethod.Post, Base), (HttpMethod.Get, $"{Base}/{id}"), (HttpMethod.Put, $"{Base}/{id}"), (HttpMethod.Post, $"{Base}/{id}/submit"), (HttpMethod.Post, $"{Base}/{id}/withdraw"), (HttpMethod.Post, $"{Base}/{id}/cancel") };
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
        object Body(string key, decimal qty = 2m, Guid? branch = null) => new
        {
            docType = "commercial_test", branchId = branch ?? a, partyId = party, currency = "jod", documentDate = "2026-06-15", mode = 1, note = "api", idempotencyKey = key,
            lines = new[] { new { itemId = item, unitId = unit, quantity = qty, unitPrice = 999, discount = 50, taxAmount = 7, unitCost = 3, pricingSnapshot = "forged", itemGroupId = Guid.NewGuid() } },   // price, tax, cost and snapshot fields are ignored (S1a)
        };
        var created = await Send(HttpMethod.Post, Base, Body("api-key-00001"), cookie);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var docId = created.Json.GetProperty("id").GetGuid();
        Assert.Equal("JOD", created.Json.GetProperty("currency").GetString());
        Assert.False(created.Json.GetProperty("replayed").GetBoolean());
        Assert.False(created.Json.GetProperty("lines")[0].TryGetProperty("unitPrice", out _));                                // the view carries none either
        Assert.DoesNotContain("999", created.Body);
        var replay = await Send(HttpMethod.Post, Base, Body("api-key-00001"), cookie);
        Assert.Equal(docId, replay.Json.GetProperty("id").GetGuid());
        Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, Base, Body("api-key-00001", qty: 9m), cookie)).Status);   // the same key with another payload
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base, new { docType = "sales_invoice", branchId = a, partyId = party, currency = "JOD", documentDate = "2026-06-15", mode = 1, idempotencyKey = "api-key-00002" }, cookie)).Status);   // no production type

        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{docId}", Body("ignored-key-xx", 4m), cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/{docId}", Body("ignored-key-xx", 4m), cookie, ifMatch: "abc")).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/{docId}", Body("ignored-key-xx", 4m), cookie, ifMatch: "\"9\"")).Status);
        var edited = await Send(HttpMethod.Put, $"{Base}/{docId}", Body("ignored-key-xx", 4m), cookie, ifMatch: Etag(created));
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        Assert.Equal("\"2\"", Etag(edited));
        var submitted = await Send(HttpMethod.Post, $"{Base}/{docId}/submit", null, cookie, ifMatch: Etag(edited));
        Assert.True(submitted.Status == HttpStatusCode.OK, submitted.Body);
        Assert.Equal((short)2, submitted.Json.GetProperty("status").GetInt16());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Put, $"{Base}/{docId}", Body("ignored-key-xx", 5m), cookie, ifMatch: Etag(submitted))).Status);   // a submitted document is not editable
        var withdrawn = await Send(HttpMethod.Post, $"{Base}/{docId}/withdraw", null, cookie, ifMatch: Etag(submitted));
        Assert.Equal((short)1, withdrawn.Json.GetProperty("status").GetInt16());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{docId}/cancel", new { reason = " " }, cookie, ifMatch: Etag(withdrawn))).Status);
        var cancelled = await Send(HttpMethod.Post, $"{Base}/{docId}/cancel", new { reason = "api test" }, cookie, ifMatch: Etag(withdrawn));
        Assert.Equal((short)5, cancelled.Json.GetProperty("status").GetInt16());
        Assert.Equal(5, (await Send(HttpMethod.Get, $"{Base}/{docId}", cookie: cookie)).Json.GetProperty("events").GetArrayLength());   // created, updated, submitted, withdrawn, cancelled

        // there is no delete route; approval and rejection exist but are NOT held by the Company Administrator preset
        Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"{Base}/{docId}", null, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{docId}/approve", null, cookie, ifMatch: "\"6\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{docId}/reject", new { reason = "x" }, cookie, ifMatch: "\"6\"")).Status);
        // the delegated approver approves over HTTP: the official number and the sealed totals come back, a second approval is refused
        var second = await Send(HttpMethod.Post, Base, Body("api-key-00009"), cookie);
        var secondSubmitted = await Send(HttpMethod.Post, $"{Base}/{second.Json.GetProperty("id").GetGuid()}/submit", null, cookie, ifMatch: Etag(second));
        var approverCookie = await SignIn(await NewApproverUser());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/{second.Json.GetProperty("id").GetGuid()}/approve", null, approverCookie, ifMatch: "\"1\"")).Status);
        var approvedHttp = await Send(HttpMethod.Post, $"{Base}/{second.Json.GetProperty("id").GetGuid()}/approve", null, approverCookie, ifMatch: Etag(secondSubmitted));
        Assert.True(approvedHttp.Status == HttpStatusCode.OK, approvedHttp.Body);
        Assert.Equal((short)3, approvedHttp.Json.GetProperty("status").GetInt16());
        Assert.Matches("^2026-\\d{6}$", approvedHttp.Json.GetProperty("officialNumber").GetString()!);
        Assert.Equal(20m, approvedHttp.Json.GetProperty("totalGross").GetDecimal());
        Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{second.Json.GetProperty("id").GetGuid()}/approve", null, approverCookie, ifMatch: Etag(approvedHttp))).Status);

        // branch-scoped authorization over HTTP
        var other = await Send(HttpMethod.Post, Base, Body("api-key-00003", branch: b2), cookie);
        var otherId = other.Json.GetProperty("id").GetGuid();
        var (viewerA, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, a);
        var viewerCookie = await SignIn(viewerA);
        var list = await Send(HttpMethod.Get, Base, cookie: viewerCookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(new[] { docId, second.Json.GetProperty("id").GetGuid() }.Order(), list.Json.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Order());   // both branch A documents, never the branch B one
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{otherId}", cookie: viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, Body("api-key-00004"), viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{otherId}/submit", null, viewerCookie, ifMatch: "\"1\"")).Status);
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Base, cookie: await SignIn(outsider))).Status);
    }
}
