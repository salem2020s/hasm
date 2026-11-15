using System.Net;
using Hasm.Infrastructure.Access;

namespace Hasm.Database.IntegrationTests;

/// <summary>End-to-end HTTP tests of /api/v1/company/stock-documents (Web and Windows Desktop use exactly these endpoints).</summary>
public class StockDocumentApiTests : StockTestBase
{
    private const string Base = "/api/v1/company/stock-documents";

    private async Task Start()
    {
        await StartApi();
        await Prepare();
    }

    private object Body(string type, string date = "2026-03-10", decimal qty = 2m) =>
        type == "receipt" ? new { docType = 1, targetWarehouseId = W1.Id, documentDate = date, reference = "R1", lines = new[] { new { itemId = Apple.Id, unitId = Box.Id, quantity = qty } } }
        : new { docType = 3, sourceWarehouseId = W1.Id, targetWarehouseId = W2.Id, documentDate = date, lines = new[] { new { itemId = Apple.Id, unitId = Pcs.Id, quantity = qty } } };

    [PostgresFact]
    public async Task Every_stock_document_endpoint_needs_authentication_and_a_selected_company()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, $"{Base}/capabilities"), (HttpMethod.Get, Base), (HttpMethod.Post, Base), (HttpMethod.Get, $"{Base}/{id}"), (HttpMethod.Put, $"{Base}/{id}"),
            (HttpMethod.Post, $"{Base}/{id}/submit"), (HttpMethod.Post, $"{Base}/{id}/withdraw"), (HttpMethod.Post, $"{Base}/{id}/approve"), (HttpMethod.Post, $"{Base}/{id}/reject"), (HttpMethod.Post, $"{Base}/{id}/cancel"),
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
            Assert.Equal((HttpStatusCode.Conflict, "company_not_selected"), (r.Status, r.Error));
        }
    }

    [PostgresFact]
    public async Task A_receipt_goes_from_draft_to_approved_over_http_with_etags_separation_of_duties_and_a_number()
    {
        await Start();
        var maker = await SignIn(Admin);
        var approverUser = await Approver();
        var approver = await SignIn(approverUser);
        var created = await Send(HttpMethod.Post, Base, Body("receipt"), maker);
        Assert.Equal((HttpStatusCode.Created, "\"1\""), (created.Status, Etag(created)));
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Contains($"{Base}/{id}", created.Headers.Location!.ToString());
        Assert.Equal(24m, created.Json.GetProperty("lines")[0].GetProperty("baseQuantity").GetDecimal());                              // 2 boxes x 12 pieces
        Assert.Equal(1, created.Json.GetProperty("status").GetInt32());

        var edited = await Send(HttpMethod.Put, $"{Base}/{id}", Body("receipt", "2026-03-11", 3m), maker, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.OK, "\"2\""), (edited.Status, Etag(edited)));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, maker)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, maker, ifMatch: "\"1\"")).Status);
        var submitted = await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, maker, ifMatch: "\"2\"");
        Assert.Equal((HttpStatusCode.OK, 2), (submitted.Status, submitted.Json.GetProperty("status").GetInt32()));
        var own = await Send(HttpMethod.Post, $"{Base}/{id}/approve", null, maker, ifMatch: "\"3\"");
        Assert.Equal((HttpStatusCode.Conflict, "self_approval_not_allowed"), (own.Status, own.Error));
        var approved = await Send(HttpMethod.Post, $"{Base}/{id}/approve", null, approver, ifMatch: "\"3\"");
        Assert.Equal((HttpStatusCode.OK, "SR-2026-000001"), (approved.Status, approved.Json.GetProperty("number").GetString()));
        Assert.Equal(new[] { 1, 2, 3, 5 }, approved.Json.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetInt32()).ToArray());
        var again = await Send(HttpMethod.Post, $"{Base}/{id}/approve", null, approver, ifMatch: "\"3\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, again.Status);                                                                   // a repeated request does nothing twice
        var list = await Send(HttpMethod.Get, $"{Base}?type=1&status=3&search=sr-2026", cookie: maker);
        Assert.Equal((1, "SR-2026-000001"), (list.Json.GetProperty("total").GetInt32(), list.Json.GetProperty("documents")[0].GetProperty("number").GetString()));
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        var cancelled = await Send(HttpMethod.Post, $"{Base}/{id}/cancel", new { reason = "entered by mistake" }, maker, ifMatch: "\"4\"");
        Assert.Equal((HttpStatusCode.OK, 5), (cancelled.Status, cancelled.Json.GetProperty("status").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{id}/cancel", new { reason = "" }, maker, ifMatch: "\"5\"")).Status);
    }

    [PostgresFact]
    public async Task Validation_errors_are_400_with_field_codes_and_a_reject_needs_a_note()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var bad = await Send(HttpMethod.Post, Base, new { docType = 1, documentDate = "2026-03-10", lines = new object[] { } }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var tracked = await Send(HttpMethod.Post, Base, new { docType = 1, targetWarehouseId = W1.Id, documentDate = "2026-03-10", lines = new[] { new { itemId = Lot.Id, unitId = Pcs.Id, quantity = 1m } } }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, tracked.Status);
        Assert.Contains("tracked_item_not_supported_yet", tracked.Json.ToString());
        var d = await Send(HttpMethod.Post, Base, Body("transfer"), cookie);
        var id = d.Json.GetProperty("id").GetGuid();
        await Send(HttpMethod.Post, $"{Base}/{id}/submit", null, cookie, ifMatch: "\"1\"");
        var approver = await SignIn(await Approver());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{id}/reject", new { note = " " }, approver, ifMatch: "\"2\"")).Status);
        var rejected = await Send(HttpMethod.Post, $"{Base}/{id}/reject", new { note = "wrong warehouse" }, approver, ifMatch: "\"2\"");
        Assert.Equal((HttpStatusCode.OK, 4), (rejected.Status, rejected.Json.GetProperty("status").GetInt32()));
    }

    [PostgresFact]
    public async Task A_warehouse_scoped_user_sees_and_changes_only_documents_of_their_warehouse_over_http()
    {
        await Start();
        var mine = await Draft(Receipt(W1));
        var theirs = await Draft(Receipt(W2));
        var (user, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsCreate], ScopeKind.Warehouse, W1.Id);
        var cookie = await SignIn(user);
        var caps = await Send(HttpMethod.Get, $"{Base}/capabilities", cookie: cookie);
        Assert.Equal(new[] { W1.Id }, caps.Json.GetProperty("create").EnumerateArray().Select(x => x.GetGuid()).ToArray());
        Assert.Equal(0, caps.Json.GetProperty("approve").GetArrayLength());
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(new[] { mine.Id }, list.Json.GetProperty("documents").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/{mine.Id}", cookie: cookie)).Status);
        var denied = await Send(HttpMethod.Get, $"{Base}/{theirs.Id}", cookie: cookie);
        Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (denied.Status, denied.Error));
        var make = await Send(HttpMethod.Post, Base, Body("transfer"), cookie);                                                          // a transfer touches W2 as well
        Assert.Equal(HttpStatusCode.Forbidden, make.Status);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, Base, Body("receipt"), cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{mine.Id}/submit", null, cookie, ifMatch: "\"1\"")).Status);
    }

    [PostgresFact]
    public async Task The_editor_lookups_offer_only_stockable_untracked_items_with_exact_unit_factors_and_only_the_callers_warehouses()
    {
        await Start();
        var (maker, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsCreate], ScopeKind.Warehouse, W1.Id);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Warehouse, W1.Id);
        var cookie = await SignIn(maker);
        var items = await Send(HttpMethod.Get, $"{Base}/items?search=a", cookie: cookie);
        var codes = items.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToArray();
        Assert.Contains("APPLE", codes);
        Assert.DoesNotContain("SERVICE", codes);                                                                                       // a service is never stocked
        Assert.DoesNotContain("LOT", codes);                                                                                           // tracked items are not supported yet
        var apple = items.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "APPLE");
        Assert.Equal(new[] { (true, 1m), (false, 12m) }, apple.GetProperty("units").EnumerateArray().Select(u => (u.GetProperty("isBase").GetBoolean(), u.GetProperty("factor").GetDecimal())).OrderByDescending(x => x.Item1).ToArray());
        var wh = await Send(HttpMethod.Get, $"{Base}/warehouses", cookie: cookie);
        Assert.Equal(new[] { "W1" }, wh.Json.GetProperty("warehouses").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToArray());
        var viewerCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/items?search=a", cookie: viewerCookie)).Status);   // viewing documents does not open the item search
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/warehouses", cookie: viewerCookie)).Status);
    }
}
