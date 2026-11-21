using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Inventory;
using BA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using SysAsg = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using SysAudit = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using Route = Hasm.Database.IntegrationTests.EndpointPermissionMatrixTests.Route;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The warehouse scope of the per-endpoint 403 matrix (review M1 of #119): a member holding every warehouse-scoped permission for warehouse W1 only is refused with 403 on every warehouse-scoped
/// mutating route acting on a real resource of warehouse W2 (the warehouse itself, a stock document, a stocktake), and nothing is written; the same grant works on W1 (control). The routes come from
/// <see cref="EndpointPermissionMatrixTests.Matrix"/>, so a renamed route fails here as well. Stock balances have no mutating route. The stock-adjustment posting and reversal routes act on an
/// adjustment generated from an approved stocktake; they are covered by the no-permission and read-only checks of the matrix. Disposable cluster only.
/// </summary>
public class EndpointPermissionMatrixWarehouseTests : StocktakeTestBase
{
    /// <summary>The warehouse-scoped resource a matrix route acts on.</summary>
    private enum Target { WarehouseItself, StockDocument, StockDocumentCreate, Stocktake, StocktakeCreate }

    private static readonly IReadOnlyDictionary<string, Target> WarehouseScoped = new Dictionary<string, Target>
    {
        ["PUT /api/v1/company/warehouses/{id:guid}"] = Target.WarehouseItself,
        ["POST /api/v1/company/warehouses/{id:guid}/deactivate"] = Target.WarehouseItself,
        ["POST /api/v1/company/warehouses/{id:guid}/activate"] = Target.WarehouseItself,
        ["POST /api/v1/company/stock-documents/"] = Target.StockDocumentCreate,
        ["PUT /api/v1/company/stock-documents/{id:guid}"] = Target.StockDocument,
        ["POST /api/v1/company/stock-documents/{id:guid}/submit"] = Target.StockDocument,
        ["POST /api/v1/company/stock-documents/{id:guid}/withdraw"] = Target.StockDocument,
        ["POST /api/v1/company/stock-documents/{id:guid}/approve"] = Target.StockDocument,
        ["POST /api/v1/company/stock-documents/{id:guid}/reject"] = Target.StockDocument,
        ["POST /api/v1/company/stock-documents/{id:guid}/cancel"] = Target.StockDocument,
        ["POST /api/v1/company/stock-documents/{id:guid}/negative-exception/approvals"] = Target.StockDocument,
        ["POST /api/v1/company/stocktakes/"] = Target.StocktakeCreate,
        ["PUT /api/v1/company/stocktakes/{id:guid}"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/generate-sheet"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/submit"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/withdraw"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/approve"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/reject"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/cancel"] = Target.Stocktake,
        ["POST /api/v1/company/stocktakes/{id:guid}/adjustment"] = Target.Stocktake,
    };

    private async Task<ApiResponse> Call(string method, string path, string? body, string cookie, string ifMatch = "\"1\"")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        request.Headers.Add("X-Hasm-Request", "1");
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private async Task<(long Business, long Access, long Assignments)> Footprint() =>
        (await Sql<long>($"SELECT count(*) FROM {BA.Table}"), await Scalar<long>($"SELECT count(*) FROM {SysAudit.Table}"), await Scalar<long>($"SELECT count(*) FROM {SysAsg.Table}"));

    [PostgresFact]
    public async Task A_member_scoped_to_another_warehouse_is_refused_403_on_the_warehouse_scoped_endpoints_while_the_same_grant_works_on_its_own_warehouse()
    {
        await StartApi();
        await PrepareStocktake();
        var docW1 = await Draft(Receipt(W1)); var docW2 = await Draft(Receipt(W2));
        var takeW2 = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W2)));
        var held = (await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id)).HeldPermissionCodes.ToHashSet();
        var warehouseCodes = PermissionCatalog.Default.All.Where(p => p.Resource.Scope == ScopeKind.Warehouse && held.Contains(p.Code)).Select(p => p.Code).Order().ToArray();
        Assert.Contains(PermissionCatalog.Codes.StockDocsCreate, warehouseCodes);
        Assert.Contains(PermissionCatalog.Codes.WarehousesUpdate, warehouseCodes);
        var (user, _) = await Member(warehouseCodes, ScopeKind.Warehouse, W1.Id);
        var cookie = await SignIn(user);

        string DocBody(WarehouseView w) => $$"""{"docType":1,"targetWarehouseId":"{{w.Id}}","documentDate":"2026-03-10","reference":"MX","lines":[{"itemId":"{{Apple.Id}}","unitId":"{{Pcs.Id}}","quantity":2}]}""";
        string TakeBody(WarehouseView w) => $$"""{"warehouseId":"{{w.Id}}","countDate":"{{CountDate}}","reference":"MX","lines":[]}""";
        string WarehouseBody(WarehouseView w) => $$"""{"code":"{{w.Data.Code}}","branchId":"{{w.BranchId}}","nameAr":"مستودع","nameEn":"Edited"}""";
        (string Path, string? Body) Target2(Route r, Target t) => t switch
        {
            Target.WarehouseItself => (r.Pattern.Replace("{id:guid}", W2.Id.ToString()), r.Method == "PUT" ? WarehouseBody(W2) : null),
            Target.StockDocument => (r.Pattern.Replace("{id:guid}", docW2.Id.ToString()), r.Method == "PUT" ? DocBody(W2) : null),
            Target.StockDocumentCreate => (r.Pattern, DocBody(W2)),
            Target.Stocktake => (r.Pattern.Replace("{id:guid}", takeW2.Id.ToString()), r.Method == "PUT" ? TakeBody(W2) : null),
            Target.StocktakeCreate => (r.Pattern, TakeBody(W2)),
            _ => throw new InvalidOperationException(r.Key),
        };
        var routes = EndpointPermissionMatrixTests.Matrix.Where(r => WarehouseScoped.ContainsKey(r.Key)).ToList();
        Assert.Equal(WarehouseScoped.Count, routes.Count);                                                           // every listed route is a matrix route (renamed routes fail here)

        // warehouse W2: every warehouse-scoped route refuses with the authorization error, and nothing is written
        var before = await Footprint();
        var failures = new List<string>();
        foreach (var r in routes)
        {
            var (path, body) = Target2(r, WarehouseScoped[r.Key]);
            var res = await Call(r.Method, path, body, cookie);
            if (res.Status != HttpStatusCode.Forbidden || EndpointPermissionMatrixTests.ErrorOf(res) != "forbidden") failures.Add($"{r.Key} ({path}) -> {(int)res.Status} {(res.Body.Length > 160 ? res.Body[..160] : res.Body)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} warehouse-scoped endpoint(s) did not answer 403 to a member scoped to another warehouse:\n" + string.Join("\n", failures));
        Assert.Equal(before, await Footprint());

        // control: the same grant works on W1 (the refusals above come from the scope, not from a broken role)
        var created = await Call("POST", "/api/v1/company/stock-documents", DocBody(W1), cookie);
        Assert.True(created.Status == HttpStatusCode.Created, $"stock document create on W1: {(int)created.Status} {created.Body}");
        var submitted = await Call("POST", $"/api/v1/company/stock-documents/{docW1.Id}/submit", null, cookie, $"\"{docW1.Revision}\"");
        Assert.True(submitted.Status == HttpStatusCode.OK, $"stock document submit on W1: {(int)submitted.Status} {submitted.Body}");
        var take = await Call("POST", "/api/v1/company/stocktakes", TakeBody(W1), cookie);
        Assert.True(take.Status == HttpStatusCode.Created, $"stocktake create on W1: {(int)take.Status} {take.Body}");
        var edited = await Call("PUT", $"/api/v1/company/warehouses/{W1.Id}", WarehouseBody(W1), cookie, $"\"{W1.Revision}\"");
        Assert.True(edited.Status == HttpStatusCode.OK, $"warehouse edit on W1: {(int)edited.Status} {edited.Body}");
        // H's follow-up Low: on W1 every warehouse-scoped route acting on a W1 resource gets past authorization (a 409 or 400 from the state or the body is fine, never 403 forbidden), so the refusals
        // on W2 above come from the scope, route by route (approve, reject, cancel, negative-exception approvals and the stocktake adjustment included)
        var ownDoc = await Draft(Receipt(W1));
        var ownTake = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, Head(W1)));
        var notAuthorized = new List<string>();
        foreach (var r in routes.Where(r => WarehouseScoped[r.Key] is Target.StockDocument or Target.Stocktake))
        {
            var isDoc = WarehouseScoped[r.Key] == Target.StockDocument;
            var path = r.Pattern.Replace("{id:guid}", (isDoc ? ownDoc.Id : ownTake.Id).ToString());
            var body = r.Method == "PUT" ? (isDoc ? DocBody(W1) : TakeBody(W1)) : null;
            var res = await Call(r.Method, path, body, cookie, $"\"{(isDoc ? ownDoc.Revision : ownTake.Revision)}\"");
            if (res.Status == HttpStatusCode.Forbidden && EndpointPermissionMatrixTests.ErrorOf(res) == "forbidden") notAuthorized.Add($"{r.Key} on W1: {res.Body}");
        }
        Assert.True(notAuthorized.Count == 0, "the W1 grant must pass authorization on its own warehouse:\n" + string.Join("\n", notAuthorized));
        // the warehouse structure operations need the company-wide structure permission: refused on W1 as well
        foreach (var r in routes.Where(r => WarehouseScoped[r.Key] == Target.WarehouseItself && r.Method == "POST"))
        {
            var res = await Call(r.Method, r.Pattern.Replace("{id:guid}", W1.Id.ToString()), null, cookie);
            Assert.True(res.Status == HttpStatusCode.Forbidden && EndpointPermissionMatrixTests.ErrorOf(res) == "forbidden", $"{r.Key} on W1: {(int)res.Status} {res.Body}");
        }
    }
}
