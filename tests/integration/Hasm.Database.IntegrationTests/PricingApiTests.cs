using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using PL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceList;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.11 slice 2c: the pricing management API over real HTTP: authentication, company selection, RBAC (view / configure / override), optimistic concurrency (revision and policy version), no DELETE, validation and the end-to-end flow to a sealed quote.</summary>
public class PricingApiTests : HttpApiTestBase
{
    private const string Base = "/api/v1/company/pricing";

    private async Task<(Guid Item, Guid Unit, Guid Group)> Catalog()
    {
        var inv = new CompanyInventoryService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        var unit = inv.CreateUnitAsync(Admin.Id, Company.Id, new UnitInput("PCS", "وحدة", "Piece", 0)).Result.Value!;
        var group = inv.CreateGroupAsync(Admin.Id, Company.Id, new ItemGroupInput("G1", "مجموعة", "Group", null, 1)).Result.Value!;
        var item = inv.CreateItemAsync(Admin.Id, Company.Id, new ItemInput("I1", "صنف", "Item", 1, group.Id, unit.Id, null, null, false, false, false, null, [])).Result.Value!;
        return (item.Id, unit.Id, group.Id);
    }

    [PostgresFact]
    public async Task Every_pricing_endpoint_needs_authentication_and_a_selected_company_and_nothing_can_be_deleted()
    {
        await StartApi();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, $"{Base}/policy"), (HttpMethod.Post, $"{Base}/policy"), (HttpMethod.Get, $"{Base}/price-lists"), (HttpMethod.Post, $"{Base}/price-lists"), (HttpMethod.Put, $"{Base}/price-lists/{id}"),
            (HttpMethod.Get, $"{Base}/price-lists/{id}/entries"), (HttpMethod.Post, $"{Base}/price-lists/{id}/entries"), (HttpMethod.Put, $"{Base}/entries/{id}"),
            (HttpMethod.Get, $"{Base}/discounts"), (HttpMethod.Post, $"{Base}/discounts"), (HttpMethod.Put, $"{Base}/discounts/{id}"), (HttpMethod.Post, $"{Base}/quote"),
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
        foreach (var path in new[] { $"{Base}/policy", $"{Base}/price-lists/{id}", $"{Base}/entries/{id}", $"{Base}/discounts/{id}" })
            Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, path, null, cookie)).Status);                    // there is no delete route at all
    }

    [PostgresFact]
    public async Task The_administrator_configures_the_policy_lists_entries_and_discounts_and_gets_a_sealed_quote_end_to_end()
    {
        await StartApi();
        var (item, unit, group) = await Catalog();
        var cookie = await SignIn(Admin);

        var policy = await Send(HttpMethod.Get, $"{Base}/policy", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, policy.Status);
        Assert.Equal((0, true), (policy.Json.GetProperty("policy").GetProperty("version").GetInt32(), policy.Json.GetProperty("policy").GetProperty("isDefault").GetBoolean()));
        Assert.True(policy.Json.GetProperty("can").GetProperty("configure").GetBoolean());

        // the policy: a version fence (If-Match carries the version looked at; 0 = none saved), validation, append-only history
        var body = new { priceDecimals = 2, moneyDecimals = 2, marginAction = 1, listPrecedence = 1, discountPrecedence = 1 };
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/policy", body, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/policy", body, cookie, ifMatch: "abc")).Status);
        var bad = await Send(HttpMethod.Post, $"{Base}/policy", new { priceDecimals = 9, moneyDecimals = 2, marginAction = 1, listPrecedence = 1, discountPrecedence = 1 }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("priceDecimals", bad.Body);
        var missing = await Send(HttpMethod.Post, $"{Base}/policy", new { priceDecimals = 2 }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("moneyDecimals", missing.Body);                                                                        // never filled with a default
        var v1 = await Send(HttpMethod.Post, $"{Base}/policy", body, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, v1.Status);
        Assert.Equal("\"1\"", Etag(v1));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/policy", body, cookie, ifMatch: "\"0\"")).Status);

        // a price list and its entries
        var created = await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "retail", nameAr = "قائمة", nameEn = "Retail", priority = 5, currency = "JOD", validFrom = "2026-01-01" }, cookie);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var listId = created.Json.GetProperty("id").GetGuid();
        Assert.Equal("RETAIL", created.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "RETAIL", nameAr = "مكرر", priority = 1, currency = "JOD", validFrom = "2026-01-01" }, cookie)).Status);   // code_taken
        var noDate = await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "X", nameAr = "س", priority = 1, currency = "JOD" }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, noDate.Status);
        Assert.Contains("validFrom", noDate.Body);                                                                             // an omitted date is reported, never 0001-01-01
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/price-lists/{listId}", new { nameAr = "ق", priority = 5, validFrom = "2026-01-01" }, cookie)).Status);
        var stale = await Send(HttpMethod.Put, $"{Base}/price-lists/{listId}", new { nameAr = "ق", priority = 5, validFrom = "2026-01-01" }, cookie, ifMatch: "\"9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        var renamed = await Send(HttpMethod.Put, $"{Base}/price-lists/{listId}", new { nameAr = "قائمة جديدة", priority = 5, validFrom = "2026-01-01" }, cookie, ifMatch: Etag(created));
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal("\"2\"", Etag(renamed));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/price-lists/{listId}", new { code = "OTHER", nameAr = "ق", priority = 5, validFrom = "2026-01-01" }, cookie, ifMatch: Etag(renamed))).Status);   // code_immutable

        var entry = await Send(HttpMethod.Post, $"{Base}/price-lists/{listId}/entries", new { itemId = item, unitId = unit, minQuantity = 0, unitPrice = 10 }, cookie);
        Assert.Equal(HttpStatusCode.OK, entry.Status);
        var entryId = entry.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/price-lists/{listId}/entries", new { itemId = item, unitId = unit, minQuantity = 0, unitPrice = 11 }, cookie)).Status);   // tier_exists
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/price-lists/{listId}/entries", new { itemId = item, itemGroupId = group, unitId = unit, minQuantity = 1, unitPrice = 1 }, cookie)).Status);   // item or group
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"{Base}/price-lists/{Guid.NewGuid()}/entries", new { itemId = item, unitId = unit, minQuantity = 0, unitPrice = 1 }, cookie)).Status);
        var repriced = await Send(HttpMethod.Put, $"{Base}/entries/{entryId}", new { unitPrice = 12 }, cookie, ifMatch: Etag(entry));
        Assert.Equal(HttpStatusCode.OK, repriced.Status);
        Assert.Equal(12m, repriced.Json.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Base}/price-lists/{listId}/entries", cookie: cookie)).Json.GetProperty("entries").GetArrayLength());

        // a discount rule
        var disc = await Send(HttpMethod.Post, $"{Base}/discounts", new { code = "promo", nameAr = "خصم", priority = 1, validFrom = "2026-01-01", percent = 25 }, cookie);
        Assert.Equal(HttpStatusCode.OK, disc.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/discounts", new { code = "BAD", nameAr = "خصم", priority = 1, validFrom = "2026-01-01", percent = 150 }, cookie)).Status);
        var noPercent = await Send(HttpMethod.Post, $"{Base}/discounts", new { code = "NOPCT", nameAr = "خصم", priority = 1, validFrom = "2026-01-01" }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, noPercent.Status);
        Assert.Contains("percent", noPercent.Body);
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Base}/discounts", cookie: cookie)).Json.GetProperty("discounts").GetArrayLength());

        // the quote: 12 x 0.75 = 9.00 per unit, x 4 = 36.00; sealed, nothing stored
        var quote = await Send(HttpMethod.Post, $"{Base}/quote", new { date = "2026-06-15", currency = "jod", itemId = item, itemGroupId = group, unitId = unit, quantity = 4 }, cookie);
        Assert.Equal(HttpStatusCode.OK, quote.Status);
        Assert.Equal("pricing-snapshot-v1", quote.Json.GetProperty("version").GetString());
        var result = quote.Json.GetProperty("result");
        Assert.Equal((12m, 25m, 9m, 36m), (result.GetProperty("listUnitPrice").GetDecimal(), result.GetProperty("discountPercent").GetDecimal(), result.GetProperty("netUnitPrice").GetDecimal(), result.GetProperty("lineNet").GetDecimal()));
        Assert.Equal(64, quote.Json.GetProperty("contentHash").GetString()!.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/quote", new { currency = "JOD" }, cookie)).Status);                                  // an incomplete request is refused
        var noPrice = await Send(HttpMethod.Post, $"{Base}/quote", new { date = "2025-01-01", currency = "JOD", itemId = item, unitId = unit, quantity = 1 }, cookie);   // before the list is valid
        Assert.Equal(HttpStatusCode.BadRequest, noPrice.Status);
        Assert.Contains("no_price", noPrice.Body);
    }

    [PostgresFact]
    public async Task Viewers_read_and_quote_but_cannot_configure_and_an_override_needs_its_own_permission()
    {
        await StartApi();
        var (item, unit, _) = await Catalog();
        var admin = await SignIn(Admin);
        var list = await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "L1", nameAr = "ق", priority = 1, currency = "JOD", validFrom = "2026-01-01" }, admin);
        await Send(HttpMethod.Post, $"{Base}/price-lists/{list.Json.GetProperty("id").GetGuid()}/entries", new { itemId = item, unitId = unit, minQuantity = 0, unitPrice = 10 }, admin);

        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        var outsiderCookie = await SignIn(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/price-lists", cookie: outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/policy", cookie: outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/quote", new { date = "2026-06-15", currency = "JOD", itemId = item, unitId = unit, quantity = 1 }, outsiderCookie)).Status);

        var (viewer, _) = await Member([PermissionCatalog.Codes.PricingView], ScopeKind.Company, null);
        var viewerCookie = await SignIn(viewer);
        var lists = await Send(HttpMethod.Get, $"{Base}/price-lists", cookie: viewerCookie);
        Assert.Equal(HttpStatusCode.OK, lists.Status);
        Assert.False(lists.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/price-lists", new { code = "X", nameAr = "س", priority = 1, currency = "JOD", validFrom = "2026-01-01" }, viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/policy", new { priceDecimals = 2, moneyDecimals = 2, marginAction = 1, listPrecedence = 1, discountPrecedence = 1 }, viewerCookie, ifMatch: "\"0\"")).Status);
        var plain = new { date = "2026-06-15", currency = "JOD", itemId = item, unitId = unit, quantity = 1 };
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/quote", plain, viewerCookie)).Status);
        var withOverride = new { date = "2026-06-15", currency = "JOD", itemId = item, unitId = unit, quantity = 1, @override = new { unitPrice = 7, reason = "negotiated" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/quote", withOverride, viewerCookie)).Status);

        var (overrider, _) = await Member([PermissionCatalog.Codes.PricingView, PermissionCatalog.Codes.PricingOverride], ScopeKind.Company, null);
        var overridden = await Send(HttpMethod.Post, $"{Base}/quote", withOverride, await SignIn(overrider));
        Assert.Equal(HttpStatusCode.OK, overridden.Status);
        Assert.Equal((true, 7m, "negotiated"), (overridden.Json.GetProperty("result").GetProperty("overridden").GetBoolean(), overridden.Json.GetProperty("result").GetProperty("netUnitPrice").GetDecimal(), overridden.Json.GetProperty("result").GetProperty("overrideReason").GetString()));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {PL.Table}"));                                                                                              // the denied writes created nothing
    }
}
