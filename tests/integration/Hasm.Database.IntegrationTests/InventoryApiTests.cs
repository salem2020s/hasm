using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Inventory;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using BC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemBarcode;

namespace Hasm.Database.IntegrationTests;

/// <summary>End-to-end HTTP tests of /api/v1/company/inventory (Web and Windows Desktop use exactly these endpoints).</summary>
public class InventoryApiTests : InventoryTestBase
{
    private const string Base = "/api/v1/company/inventory";

    private async Task Start()
    {
        await StartApi();
        await PrepareInventory();
    }

    [PostgresFact]
    public async Task Every_inventory_endpoint_needs_authentication_and_a_selected_company()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, $"{Base}/capabilities"), (HttpMethod.Get, $"{Base}/settings"), (HttpMethod.Put, $"{Base}/settings"),
            (HttpMethod.Get, $"{Base}/units"), (HttpMethod.Post, $"{Base}/units"), (HttpMethod.Put, $"{Base}/units/{id}"), (HttpMethod.Post, $"{Base}/units/{id}/deactivate"), (HttpMethod.Post, $"{Base}/units/{id}/activate"),
            (HttpMethod.Get, $"{Base}/groups"), (HttpMethod.Post, $"{Base}/groups"), (HttpMethod.Put, $"{Base}/groups/{id}"), (HttpMethod.Post, $"{Base}/groups/{id}/deactivate"), (HttpMethod.Post, $"{Base}/groups/{id}/activate"),
            (HttpMethod.Get, $"{Base}/items"), (HttpMethod.Post, $"{Base}/items"), (HttpMethod.Get, $"{Base}/items/{id}"), (HttpMethod.Put, $"{Base}/items/{id}"),
            (HttpMethod.Post, $"{Base}/items/{id}/deactivate"), (HttpMethod.Post, $"{Base}/items/{id}/activate"),
            (HttpMethod.Get, $"{Base}/items/{id}/barcodes"), (HttpMethod.Post, $"{Base}/items/{id}/barcodes"),
            (HttpMethod.Post, $"{Base}/items/{id}/barcodes/{id}/deactivate"), (HttpMethod.Post, $"{Base}/items/{id}/barcodes/{id}/activate"), (HttpMethod.Get, $"{Base}/barcode-lookup?barcode=A"),
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
    }

    [PostgresFact]
    public async Task The_unit_and_group_lists_are_unchanged_without_parameters_and_page_and_search_with_them_under_the_same_permission()
    {
        await Start();
        var cookie = await SignIn(Admin);
        foreach (var (code, name) in new[] { ("U1", "Piece"), ("U2", "Box"), ("U3", "Boxes") })
            Ok(await Inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput(code, "وحدة", name, 0)));
        foreach (var code in new[] { "G1", "G2", "G3" }) await Group(code);
        foreach (var (route, key) in new[] { ("units", "units"), ("groups", "groups") })
        {
            // no parameters: exactly today's shape (every row, no total)
            var all = await Send(HttpMethod.Get, $"{Base}/{route}", cookie: cookie);
            Assert.Equal(3, all.Json.GetProperty(key).GetArrayLength());
            Assert.False(all.Json.TryGetProperty("total", out _));
            // with parameters: one page ordered by code plus the total
            var page = await Send(HttpMethod.Get, $"{Base}/{route}?limit=2&offset=1", cookie: cookie);
            Assert.Equal(3L, page.Json.GetProperty("total").GetInt64());
            Assert.Equal(2, page.Json.GetProperty(key).GetArrayLength());
            Assert.Equal(route == "units" ? "U2" : "G2", page.Json.GetProperty(key)[0].GetProperty("code").GetString());
            Assert.Equal(1L, (await Send(HttpMethod.Get, $"{Base}/{route}?search={(route == "units" ? "piece" : "g3")}", cookie: cookie)).Json.GetProperty("total").GetInt64());
            Assert.Equal(3L, (await Send(HttpMethod.Get, $"{Base}/{route}?limit=1000", cookie: cookie)).Json.GetProperty("total").GetInt64());     // clamped, not refused
            foreach (var bad in new[] { "limit=0", "limit=-1", "offset=-1", "limit=abc", $"search={new string('x', 101)}" })
                Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/{route}?{bad}", cookie: cookie)).Status);
            // the permission is unchanged
            var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{route}?search=a", cookie: await SignIn(outsider))).Status);
            var (viewer, _) = await Member([PermissionCatalog.Codes.ItemsView], ScopeKind.Company, null);
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/{route}?search=a", cookie: await SignIn(viewer))).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/{route}?search=a")).Status);
        }
    }

    [PostgresFact]
    public async Task The_administrator_builds_units_groups_items_and_barcodes_end_to_end_with_etags_and_capabilities()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var caps = await Send(HttpMethod.Get, $"{Base}/capabilities", cookie: cookie);
        Assert.Contains("no-store", caps.Headers.CacheControl!.ToString());
        Assert.True(caps.Json.GetProperty("itemsCreate").GetBoolean() && caps.Json.GetProperty("settingsConfigure").GetBoolean());

        var settings0 = await Send(HttpMethod.Get, $"{Base}/settings", cookie: cookie);
        Assert.Equal("\"0\"", Etag(settings0));
        var settings = await Send(HttpMethod.Put, $"{Base}/settings", new { defaultValuation = 2 }, cookie, ifMatch: "\"0\"");
        Assert.Equal((HttpStatusCode.OK, "\"1\""), (settings.Status, Etag(settings)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/settings", new { defaultValuation = 1 }, cookie, ifMatch: "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Put, $"{Base}/settings", new { defaultValuation = 1 }, cookie)).Status);

        var unit = await Send(HttpMethod.Post, $"{Base}/units", new { code = "pcs", nameAr = "قطعة", nameEn = "Piece", decimals = 0 }, cookie);
        Assert.Equal(HttpStatusCode.Created, unit.Status);
        Assert.Equal("\"1\"", Etag(unit));
        Assert.Equal("PCS", unit.Json.GetProperty("code").GetString());
        var unitId = unit.Json.GetProperty("id").GetGuid();
        Assert.Contains($"{Base}/units/{unitId}", unit.Headers.Location!.ToString());
        var box = await Send(HttpMethod.Post, $"{Base}/units", new { code = "box", nameEn = "Box", decimals = 0 }, cookie);
        var boxId = box.Json.GetProperty("id").GetGuid();
        var dup = await Send(HttpMethod.Post, $"{Base}/units", new { code = "PCS", nameEn = "x", decimals = 0 }, cookie);
        Assert.Equal((HttpStatusCode.Conflict, "unit_code_taken"), (dup.Status, dup.Error));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/units", new { code = "bad code", nameEn = "x", decimals = 0 }, cookie)).Status);

        var group = await Send(HttpMethod.Post, $"{Base}/groups", new { code = "FOOD", nameEn = "Food" }, cookie);
        Assert.Equal(HttpStatusCode.Created, group.Status);
        Assert.Equal(2, group.Json.GetProperty("effectiveValuation").GetInt32());                                                                       // the company default
        var groupId = group.Json.GetProperty("id").GetGuid();

        var item = await Send(HttpMethod.Post, $"{Base}/items", new
        {
            nameAr = "تفاح", nameEn = "Apple", itemType = 1, groupId, baseUnitId = unitId, purchaseUnitId = boxId, salesUnitId = unitId, isLot = true,
            alternateUnits = new[] { new { unitId = boxId, factor = 12.5m } },
        }, cookie);
        Assert.Equal(HttpStatusCode.Created, item.Status);
        Assert.Equal("ITM-000001", item.Json.GetProperty("code").GetString());
        Assert.Equal(12.5m, item.Json.GetProperty("alternateUnits")[0].GetProperty("factor").GetDecimal());
        Assert.Equal(2, item.Json.GetProperty("effectiveValuation").GetInt32());
        var itemId = item.Json.GetProperty("id").GetGuid();
        Assert.Equal("\"1\"", Etag(await Send(HttpMethod.Get, $"{Base}/items/{itemId}", cookie: cookie)));

        var updated = await Send(HttpMethod.Put, $"{Base}/items/{itemId}", new
        {
            nameAr = "تفاح أحمر", nameEn = "Red apple", itemType = 1, groupId, baseUnitId = unitId, isLot = true, alternateUnits = new[] { new { unitId = boxId, factor = 12.5m } },
        }, cookie, ifMatch: "\"1\"");
        Assert.Equal((HttpStatusCode.OK, "\"2\""), (updated.Status, Etag(updated)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/items/{itemId}", new { nameEn = "x", itemType = 1, groupId, baseUnitId = unitId }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Put, $"{Base}/items/{itemId}", new { nameEn = "x", itemType = 1, groupId, baseUnitId = unitId }, cookie)).Status);

        var barcode = await Send(HttpMethod.Post, $"{Base}/items/{itemId}/barcodes", new { barcode = "4006381333931" }, cookie);
        Assert.Equal(HttpStatusCode.Created, barcode.Status);
        Assert.Equal(1, barcode.Json.GetProperty("kind").GetInt32());
        var barcodeId = barcode.Json.GetProperty("id").GetGuid();
        var bdup = await Send(HttpMethod.Post, $"{Base}/items/{itemId}/barcodes", new { barcode = "4006381333931" }, cookie);
        Assert.Equal((HttpStatusCode.Conflict, "barcode_taken"), (bdup.Status, bdup.Error));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/items/{itemId}/barcodes", new { barcode = "4006381333932", kind = 1 }, cookie)).Status);
        var lookup = await Send(HttpMethod.Get, $"{Base}/barcode-lookup?barcode=4006381333931", cookie: cookie);
        Assert.Equal(("ITM-000001", "PCS"), (lookup.Json.GetProperty("itemCode").GetString(), lookup.Json.GetProperty("unitCode").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/barcode-lookup?barcode=NOPE", cookie: cookie)).Status);
        Assert.Single((await Send(HttpMethod.Get, $"{Base}/items/{itemId}/barcodes", cookie: cookie)).Json.GetProperty("barcodes").EnumerateArray());
        Assert.False((await Send(HttpMethod.Post, $"{Base}/items/{itemId}/barcodes/{barcodeId}/deactivate", null, cookie)).Json.GetProperty("isActive").GetBoolean());

        var list = await Send(HttpMethod.Get, $"{Base}/items?search=red&limit=10", cookie: cookie);
        Assert.Equal((1, 1), (list.Json.GetProperty("items").GetArrayLength(), list.Json.GetProperty("total").GetInt32()));
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{Base}/items?search=zzz", cookie: cookie)).Json.GetProperty("items").GetArrayLength());
        var off = await Send(HttpMethod.Post, $"{Base}/items/{itemId}/deactivate", null, cookie, ifMatch: "\"2\"");
        Assert.False(off.Json.GetProperty("isActive").GetBoolean());
        Assert.True((await Send(HttpMethod.Post, $"{Base}/items/{itemId}/activate", null, cookie, ifMatch: "\"3\"")).Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(2, (await Send(HttpMethod.Get, $"{Base}/units", cookie: cookie)).Json.GetProperty("units").GetArrayLength());
        Assert.Single((await Send(HttpMethod.Get, $"{Base}/groups", cookie: cookie)).Json.GetProperty("groups").EnumerateArray());
    }

    [PostgresFact]
    public async Task A_viewer_reads_over_http_but_every_change_is_forbidden_and_leaves_no_trace()
    {
        await Start();
        var pcs = await Unit("PCS", 0);
        Ok(await Inv.SaveSettingsAsync(Admin.Id, Company.Id, 0, new InventorySettingsInput(1)));
        var g = await Group("G");
        var item = Ok(await Inv.CreateItemAsync(Admin.Id, Company.Id, NewItem(g.Id, pcs.Id)));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.ItemsView);
        var cookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/items", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/items/{item.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{Base}/units", cookie: cookie)).Status);
        var caps = await Send(HttpMethod.Get, $"{Base}/capabilities", cookie: cookie);
        Assert.True(caps.Json.GetProperty("itemsView").GetBoolean());
        Assert.False(caps.Json.GetProperty("itemsCreate").GetBoolean() || caps.Json.GetProperty("unitsCreate").GetBoolean());
        async Task<long> Rows() => await Count(Company, I.Table) + await Count(Company, U.Table) + await Count(Company, BC.Table) + await Count(Company, Audit_.Table);
        var before = await Rows();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, $"{Base}/items", new { nameEn = "x", itemType = 1, groupId = g.Id, baseUnitId = pcs.Id }), (HttpMethod.Post, $"{Base}/units", new { code = "X", nameEn = "x", decimals = 0 }),
            (HttpMethod.Post, $"{Base}/groups", new { code = "X", nameEn = "x" }), (HttpMethod.Post, $"{Base}/items/{item.Id}/barcodes", new { barcode = "A1" }),
            (HttpMethod.Put, $"{Base}/settings", new { defaultValuation = 2 }),
        })
        {
            var r = await Send(method, path, body, cookie, ifMatch: "\"1\"");
            Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (r.Status, r.Error));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/settings", cookie: cookie)).Status);
        Assert.Equal(before, await Rows());
    }
}
