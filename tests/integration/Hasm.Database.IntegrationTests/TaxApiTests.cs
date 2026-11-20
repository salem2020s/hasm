using System.Net;
using Hasm.Infrastructure.Access;
using TD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.TaxDefinition;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.10 slice 3: the tax management API over real HTTP: authentication, company selection, RBAC, optimistic concurrency, no DELETE, and the end-to-end configuration flow.</summary>
public class TaxApiTests : HttpApiTestBase
{
    private const string Base = "/api/v1/company/taxes";
    private static readonly Guid[] None = [];

    private static object RuleBody(Guid taxId, string validFrom, bool active = true) =>
        new { documentType = "sales_invoice", operation = "sale", taxId, priority = 10, includesTaxIds = None, validFrom, isActive = active };

    [PostgresFact]
    public async Task Every_tax_endpoint_needs_authentication_and_a_selected_company_and_nothing_can_be_deleted()
    {
        await StartApi();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, Base), (HttpMethod.Post, Base), (HttpMethod.Put, $"{Base}/{id}"), (HttpMethod.Post, $"{Base}/{id}/rates"),
            (HttpMethod.Get, $"{Base}/classes"), (HttpMethod.Post, $"{Base}/classes"), (HttpMethod.Put, $"{Base}/classes/{id}"),
            (HttpMethod.Put, $"{Base}/item-classes/{id}"), (HttpMethod.Put, $"{Base}/party-classes/{id}"),
            (HttpMethod.Get, $"{Base}/rules"), (HttpMethod.Post, $"{Base}/rules"), (HttpMethod.Put, $"{Base}/rules/{id}"),
            (HttpMethod.Get, $"{Base}/policy"), (HttpMethod.Post, $"{Base}/policy"), (HttpMethod.Get, $"{Base}/resolve?documentType=a&operation=b&taxDate=2026-01-01"),
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
        foreach (var path in new[] { Base, $"{Base}/{id}", $"{Base}/classes/{id}", $"{Base}/rules/{id}", $"{Base}/policy" })
            Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Delete, path, null, cookie)).Status);                    // there is no delete route at all
    }

    [PostgresFact]
    public async Task The_administrator_configures_taxes_rates_classes_rules_and_policy_and_resolves_end_to_end()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        var empty = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Equal(0, empty.Json.GetProperty("taxes").GetArrayLength());
        Assert.True(empty.Json.GetProperty("can").GetProperty("configure").GetBoolean());

        var vat = await Send(HttpMethod.Post, Base, new { code = "vat", nameAr = "ضريبة القيمة المضافة", nameEn = "VAT" }, cookie);
        Assert.Equal(HttpStatusCode.OK, vat.Status);
        Assert.Equal("\"1\"", Etag(vat));
        var vatId = vat.Json.GetProperty("id").GetGuid();
        Assert.Equal("VAT", vat.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base, new { code = "VAT", nameAr = "مكرر" }, cookie)).Status);   // code_taken

        var rated = await Send(HttpMethod.Post, $"{Base}/{vatId}/rates", new { effectiveFrom = "2026-01-01", treatment = 1, ratePercent = 15 }, cookie);
        Assert.Equal(HttpStatusCode.OK, rated.Status);
        Assert.Equal(1, rated.Json.GetProperty("rates").GetArrayLength());
        var notLater = await Send(HttpMethod.Post, $"{Base}/{vatId}/rates", new { effectiveFrom = "2026-01-01", treatment = 1, ratePercent = 16 }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, notLater.Status);
        Assert.Contains("rate_not_later", notLater.Body);

        // revision fence on update
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{vatId}", new { nameAr = "ض" }, cookie)).Status);
        var stale = await Send(HttpMethod.Put, $"{Base}/{vatId}", new { nameAr = "ض" }, cookie, ifMatch: "\"9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/{vatId}", new { nameAr = "ض" }, cookie, ifMatch: "abc")).Status);
        var renamed = await Send(HttpMethod.Put, $"{Base}/{vatId}", new { nameAr = "ضريبة", nameEn = "Value added tax" }, cookie, ifMatch: "\"1\"");
        Assert.Equal("\"2\"", Etag(renamed));
        Assert.True(renamed.Json.GetProperty("isActive").GetBoolean());                                                                // an omitted flag never changes it

        var food = await Send(HttpMethod.Post, $"{Base}/classes", new { kind = 1, code = "food", nameAr = "غذاء" }, cookie);
        Assert.Equal(HttpStatusCode.OK, food.Status);
        var foodId = food.Json.GetProperty("id").GetGuid();
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Base}/classes", cookie: cookie)).Json.GetProperty("classes").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"{Base}/item-classes/{Guid.NewGuid()}", new { classId = foodId }, cookie)).Status);   // unknown item

        var rule = await Send(HttpMethod.Post, $"{Base}/rules", RuleBody(vatId, "2026-01-01"), cookie);
        Assert.Equal(HttpStatusCode.OK, rule.Status);
        var ruleId = rule.Json.GetProperty("id").GetGuid();
        var overlap = await Send(HttpMethod.Post, $"{Base}/rules", RuleBody(vatId, "2026-03-01"), cookie);
        Assert.Equal(HttpStatusCode.Conflict, overlap.Status);
        Assert.Equal("rule_overlap", overlap.Error);
        var ruleOff = await Send(HttpMethod.Put, $"{Base}/rules/{ruleId}", RuleBody(vatId, "2026-01-01", false), cookie, ifMatch: Etag(rule));
        Assert.Equal("\"2\"", Etag(ruleOff));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/rules/{ruleId}", RuleBody(vatId, "2026-01-01"), cookie, ifMatch: "\"2\"")).Status);

        Assert.False((await Send(HttpMethod.Get, $"{Base}/policy", cookie: cookie)).Json.GetProperty("policy").GetProperty("configured").GetBoolean());
        var policy = await Send(HttpMethod.Post, $"{Base}/policy", new { moneyDecimals = 2, mode = 1, level = 1 }, cookie);
        Assert.Equal(HttpStatusCode.OK, policy.Status);
        Assert.Equal(1, policy.Json.GetProperty("version").GetInt32());

        var resolved = await Send(HttpMethod.Get, $"{Base}/resolve?documentType=sales_invoice&operation=sale&taxDate=2026-02-01", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, resolved.Status);
        var component = Assert.Single(resolved.Json.GetProperty("components").EnumerateArray());
        Assert.Equal((vatId, 15m, 1), (component.GetProperty("taxId").GetGuid(), component.GetProperty("ratePercent").GetDecimal(), component.GetProperty("treatment").GetInt32()));
        Assert.Equal(1, resolved.Json.GetProperty("policyVersion").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/resolve?documentType=sales_invoice", cookie: cookie)).Status);
        var before = await Send(HttpMethod.Get, $"{Base}/resolve?documentType=sales_invoice&operation=sale&taxDate=2025-01-01", cookie: cookie);   // no rule is valid yet: nothing applies
        Assert.Equal(HttpStatusCode.OK, before.Status);
        Assert.Equal(0, before.Json.GetProperty("components").GetArrayLength());
    }

    [PostgresFact]
    public async Task Incomplete_or_malformed_requests_are_refused_and_never_filled_with_defaults_or_treated_as_a_removal()
    {
        await StartApi();
        var cookie = await SignIn(Admin);
        var vat = await Send(HttpMethod.Post, Base, new { code = "VAT", nameAr = "ضريبة" }, cookie);
        var vatId = vat.Json.GetProperty("id").GetGuid();
        // an omitted required field is reported, never turned into 0001-01-01 / 0 (rate versions are append-only)
        var noDate = await Send(HttpMethod.Post, $"{Base}/{vatId}/rates", new { treatment = 1, ratePercent = 15 }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, noDate.Status);
        Assert.Contains("effectiveFrom", noDate.Body);
        Assert.Equal(0, (await Send(HttpMethod.Get, Base, cookie: cookie)).Json.GetProperty("taxes")[0].GetProperty("rates").GetArrayLength());
        var noDecimals = await Send(HttpMethod.Post, $"{Base}/policy", new { mode = 1, level = 1 }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, noDecimals.Status);
        Assert.Contains("moneyDecimals", noDecimals.Body);
        var noValidFrom = await Send(HttpMethod.Post, $"{Base}/rules", new { documentType = "sales_invoice", operation = "sale", taxId = vatId, priority = 10, includesTaxIds = None }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, noValidFrom.Status);
        Assert.Contains("validFrom", noValidFrom.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/classes", new { kind = 99, code = "X", nameAr = "س" }, cookie)).Status);

        // a rule update that omits isActive never reactivates a switched-off rule
        var rule = await Send(HttpMethod.Post, $"{Base}/rules", RuleBody(vatId, "2026-01-01"), cookie);
        var ruleId = rule.Json.GetProperty("id").GetGuid();
        var off = await Send(HttpMethod.Put, $"{Base}/rules/{ruleId}", RuleBody(vatId, "2026-01-01", false), cookie, ifMatch: Etag(rule));
        var omitted = await Send(HttpMethod.Put, $"{Base}/rules/{ruleId}", new { documentType = "sales_invoice", operation = "sale", taxId = vatId, priority = 10, includesTaxIds = None, validFrom = "2026-01-01" }, cookie, ifMatch: Etag(off));
        Assert.Equal(HttpStatusCode.OK, omitted.Status);
        Assert.False(omitted.Json.GetProperty("isActive").GetBoolean());

        // the assignment endpoints distinguish "remove" (classId: null) from a missing or malformed body, which is a client error
        var item = Guid.NewGuid();
        foreach (var body in new object?[] { null, new { }, new { classID = Guid.NewGuid() }, new { classId = "not-a-guid" }, new { classId = 7 } })
        {
            var r = await Send(HttpMethod.Put, $"{Base}/item-classes/{item}", body, cookie);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("invalid_request", r.Error);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"{Base}/item-classes/{item}", new { classId = (Guid?)null }, cookie)).Status);   // explicit removal of a missing assignment
        var listed = await Send(HttpMethod.Get, $"{Base}/item-classes", cookie: cookie);
        Assert.Equal(0, listed.Json.GetProperty("assignments").GetArrayLength());
    }

    [PostgresFact]
    public async Task A_user_without_the_tax_permissions_reads_and_writes_nothing_and_a_viewer_cannot_configure()
    {
        await StartApi();
        var (outsider, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        var outsiderCookie = await SignIn(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Base, cookie: outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, new { code = "X", nameAr = "س" }, outsiderCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/resolve?documentType=a&operation=b&taxDate=2026-01-01", cookie: outsiderCookie)).Status);

        var (viewer, _) = await Member([PermissionCatalog.Codes.TaxView], ScopeKind.Company, null);
        var viewerCookie = await SignIn(viewer);
        var list = await Send(HttpMethod.Get, Base, cookie: viewerCookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.False(list.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Base, new { code = "X", nameAr = "س" }, viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/policy", new { moneyDecimals = 2, mode = 1, level = 1 }, viewerCookie)).Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {TD.Table}"));
    }
}
