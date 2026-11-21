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
        Assert.Equal(Admin.Id, created.Json.GetProperty("lastEditedBy").GetGuid());                          // the read model names the last editor (the screen mirrors D-066 with it)
        Assert.False(created.Json.GetProperty("replayed").GetBoolean());
        Assert.False(created.Json.GetProperty("lines")[0].TryGetProperty("unitPrice", out _));                                // the view carries none either
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(?<![0-9A-Za-z-])999(?![0-9A-Za-z-])"), created.Body);   // the client-sent price is not echoed (a random id may contain the digits, a number token may not)
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

    /// <summary>S6: the registered types, names only. This test runs the PRODUCTION composition (no test type registered), so the answer is the empty list the screen turns into "no document types activated".</summary>
    [PostgresFact]
    public async Task The_types_route_answers_the_empty_production_registry_to_a_caller_with_view_or_create_and_refuses_everyone_else()
    {
        await StartApi();                                                                                                 // production composition: no document type is registered
        var path = $"{Base}/types";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, path)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, path, cookie: "forged")).Status);
        Assert.Equal("company_not_selected", (await Send(HttpMethod.Get, path, cookie: await SignIn(Admin, selectCompany: false))).Error);
        var admin = await Send(HttpMethod.Get, path, cookie: await SignIn(Admin));
        Assert.Equal(HttpStatusCode.OK, admin.Status);
        Assert.Equal(0, admin.Json.GetProperty("types").GetArrayLength());
        Assert.Equal(0, admin.Json.GetProperty("linkPairs").GetArrayLength());                                           // no link pair is registered in production either
        var branch = await NewBranchId("BA");
        var (viewer, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, branch);                // a branch grant is enough ("in any branch")
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, path, cookie: await SignIn(viewer))).Status);
        var (creator, _) = await Member([PermissionCatalog.Codes.CommercialCreate], ScopeKind.Branch, branch);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, path, cookie: await SignIn(creator))).Status);
        var (updater, _) = await Member([PermissionCatalog.Codes.CommercialUpdate], ScopeKind.Company, null);               // neither view nor create
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, path, cookie: await SignIn(updater))).Status);
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, path, cookie: await SignIn(outsider))).Status);
    }

    /// <summary>S6 Links (G's L2): the list filters party, currency and number search run on the SERVER before paging, so a page and its total are exact and an old source is reachable; invalid values answer the standard 400; branch scope still applies.</summary>
    [PostgresFact]
    public async Task The_list_filters_party_currency_and_number_search_are_server_side_exact_validated_and_branch_scoped()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterTestType));
        await ComposeAsync();
        var a = await NewBranchId("BA"); var b2 = await NewBranchId("BB"); var p1 = await NewCustomerId("C1"); var p2 = await NewCustomerId("C2"); var (item, unit) = await NewItemAndUnit("LF1");
        var approver = await NewApprover();
        var n1 = await NewApproved(a, p1, "lf-doc-000001", approver, "commercial_test", (item, unit, 1m));          // 2026-000001, oldest
        Clock.Advance(TimeSpan.FromMinutes(1));   // the list is newest first: distinct creation times make that order (and the paging below) explicit; equal times are ordered by id, which is deterministic but not chronological
        var n2 = await NewApproved(a, p2, "lf-doc-000002", approver, "commercial_test", (item, unit, 1m));          // 2026-000002
        Clock.Advance(TimeSpan.FromMinutes(1));   // the list is newest first: distinct creation times make that order (and the paging below) explicit; equal times are ordered by id, which is deterministic but not chronological
        var n3 = await NewApproved(a, p1, "lf-doc-000003", approver, "commercial_test", (item, unit, 1m));          // 2026-000003
        Clock.Advance(TimeSpan.FromMinutes(1));   // the list is newest first: distinct creation times make that order (and the paging below) explicit; equal times are ordered by id, which is deterministic but not chronological
        var usd = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(a, p1, item, unit, "lf-usd-000001") with { Currency = "USD" }));   // a USD draft
        Clock.Advance(TimeSpan.FromMinutes(1));   // the list is newest first: distinct creation times make that order (and the paging below) explicit; equal times are ordered by id, which is deterministic but not chronological
        var other = await NewApproved(b2, p1, "lf-doc-000004", approver, "commercial_test", (item, unit, 1m));       // another branch
        var cookie = await SignIn(Admin);
        async Task<(HttpStatusCode Status, Guid[] Ids, long Total)> Get(string query)
        {
            var r = await Send(HttpMethod.Get, $"{Base}?{query}", cookie: cookie);
            return (r.Status, r.Status == HttpStatusCode.OK ? [.. r.Json.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetGuid())] : [], r.Status == HttpStatusCode.OK ? r.Json.GetProperty("total").GetInt64() : -1);
        }
        // party and currency are applied by the query: exact page and exact total
        var byParty = await Get($"branchId={a}&partyId={p1}");
        Assert.Equal(3L, byParty.Total); Assert.Equal(new[] { n1.Id, n3.Id, usd.Id }.Order(), byParty.Ids.Order());
        var byCurrency = await Get($"branchId={a}&currency=usd");
        Assert.Equal(1L, byCurrency.Total); Assert.Equal([usd.Id], byCurrency.Ids);
        var both = await Get($"branchId={a}&status=3&partyId={p1}&currency=JOD");
        Assert.Equal(2L, both.Total); Assert.Equal([n3.Id, n1.Id], both.Ids);                                          // newest first
        // the matching source beyond the first page: limit 1, the oldest document is found by its number alone, and by offset
        var oldest = await Get($"branchId={a}&search=000001&limit=1");
        Assert.Equal(1L, oldest.Total); Assert.Equal([n1.Id], oldest.Ids);
        var paged = await Get($"branchId={a}&status=3&limit=1&offset=2");
        Assert.Equal(3L, paged.Total); Assert.Equal([n1.Id], paged.Ids);                                              // the third of three approved: the oldest, with the exact total
        var beyond = await Get($"branchId={a}&status=3&limit=1&offset=9");
        Assert.Equal((3L, 0), (beyond.Total, beyond.Ids.Length));                                                   // an offset past the end still reports the total
        // the search is a literal, case-insensitive part of the number: wildcards of the text are not wildcards
        Assert.Equal((3L, 3), ((await Get($"branchId={a}&status=3&search=2026-")).Total, (await Get($"branchId={a}&status=3&search=2026-")).Ids.Length));
        Assert.Equal(0L, (await Get($"branchId={a}&search=%25")).Total);
        Assert.Equal(0L, (await Get($"branchId={a}&search=_")).Total);
        // invalid values: the standard 400 naming the field, nothing queried
        foreach (var (query, field) in new[] { ("partyId=not-a-guid", "partyId"), ("currency=EURO", "currency"), ("currency=12", "currency"), ($"search={new string('x', 101)}", "search") })
        {
            var bad = await Send(HttpMethod.Get, $"{Base}?{query}", cookie: cookie);
            Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
            Assert.Equal("invalid_request", bad.Error);
            Assert.Equal(field, bad.Json.GetProperty("fields")[0].GetProperty("field").GetString());
        }
        // the branch scope still decides: a viewer of branch A never sees branch B through any filter
        var (viewerA, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, a);
        var scoped = await Send(HttpMethod.Get, $"{Base}?partyId={p1}&search=000004", cookie: await SignIn(viewerA));
        Assert.Equal(HttpStatusCode.OK, scoped.Status);
        Assert.Equal(0L, scoped.Json.GetProperty("total").GetInt64());
        Assert.DoesNotContain(other.Id, scoped.Json.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));
        Assert.Equal(1L, (await Get($"search=000004")).Total);                                                       // the company-wide caller sees it
    }
}
