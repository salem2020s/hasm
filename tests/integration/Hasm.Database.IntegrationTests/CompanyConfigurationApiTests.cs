using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Hasm.Infrastructure.SystemDatabase;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests of /api/v1/company/* against real PostgreSQL: authentication, session-bound company, anti-CSRF, optimistic
/// concurrency status codes, RBAC, no-store, atomic audit failures and the absence of leaks. The company database is reached through
/// the single disposable test principal (per-role privilege checks live in the role-capable isolation suite).
/// </summary>
public class CompanyConfigurationApiTests : ConfigurationTestBase
{
    private const string CookieName = "__Host-hasm_session";
    private readonly List<string> bodies = [];
    private readonly LogCapture logs = new();
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;

    private async Task StartApi()
    {
        await Prepare();
        var resolver = SinglePrincipalResolver(Company);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Hasm:Database:Host", Options.Host);
            b.UseSetting("Hasm:Database:Port", Options.Port.ToString());
            b.UseSetting("Hasm:Database:SystemDatabaseName", Options.SystemDatabaseName);
            b.UseSetting("Hasm:Database:Username", Options.Username);
            b.UseSetting("Hasm:Database:Password", Options.Password);
            b.UseSetting("Hasm:Database:AdminUsername", Options.AdminUsername);
            b.UseSetting("Hasm:Database:AdminPassword", Options.AdminPassword);
            b.UseSetting("Hasm:Database:SslMode", Options.SslMode.ToString());
            b.UseSetting("Hasm:Database:AllowInsecureTransport", "true");       // disposable loopback test server only
            b.UseSetting("Hasm:Database:CompanyCredentialDirectory", CredentialDirectory);
            b.UseSetting("Hasm:Auth:Pbkdf2Iterations", "100000");
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton(_ => Audit);
                s.AddSingleton(resolver);                                            // the test principal stands in for the per-company role
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        await base.DisposeAsync();
    }

    private async Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, string? cookie = null, bool header = true, string? ifMatch = null, string? raw = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (raw is not null) request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body);
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await client.SendAsync(request);
        var result = new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
        lock (bodies) bodies.Add(result.Body);
        return result;
    }

    private async Task<string> SignIn(UserRecord user, Guid? select)
    {
        var r = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = user.Login, password = Pw });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var cookie = r.SetCookies.Single(c => c.StartsWith(CookieName + "=")).Split(';')[0][(CookieName.Length + 1)..];
        if (select is { } id) Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = id }, cookie)).Status);
        return cookie;
    }

    private static object ProfileBody(string nameAr = "شركة الأمل", string? nameEn = "Amal Co", string country = "SA", string? tax = null, string? city = null) =>
        new { nameAr, nameEn, countryCode = country, taxRegistrationNo = tax, city };

    [PostgresFact]
    public async Task Every_company_endpoint_requires_authentication_and_a_selected_company()
    {
        await StartApi();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/v1/company/profile"), (HttpMethod.Put, "/api/v1/company/profile"), (HttpMethod.Post, "/api/v1/company/profile/display-name-sync"),
            (HttpMethod.Get, "/api/v1/company/settings"), (HttpMethod.Put, "/api/v1/company/settings"),
            (HttpMethod.Get, "/api/v1/company/currencies"), (HttpMethod.Get, "/api/v1/company/currencies/reference"), (HttpMethod.Put, "/api/v1/company/currencies/SAR"),
            (HttpMethod.Get, "/api/v1/company/history"), (HttpMethod.Get, "/api/v1/company/entitlements"),
        };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged-token")).Status);
        }
        var noCompany = await SignIn(Admin, select: null);
        foreach (var (method, path) in routes)
        {
            var r = await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany, ifMatch: "\"0\"");
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("company_not_selected", r.Error);
        }
    }

    [PostgresFact]
    public async Task State_changing_requests_need_the_anti_CSRF_header()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);
        var r = await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), cookie, header: false, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Equal("missing_request_header", r.Error);
        Assert.Equal(0L, await Count(Company, CompanySchema.Profile.Table));
    }

    [PostgresFact]
    public async Task Profile_endpoints_use_ETag_preconditions_and_never_cache()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);

        var empty = await Send(HttpMethod.Get, "/api/v1/company/profile", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Equal("\"0\"", empty.Headers.ETag!.Tag);
        Assert.Contains("no-store", empty.Headers.CacheControl!.ToString());
        Assert.Equal(JsonValueKind.Null, empty.Json.GetProperty("profile").ValueKind);
        Assert.Equal(0L, await Count(Company, CompanySchema.Profile.Table));                          // GET never creates

        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), cookie, ifMatch: "abc")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), cookie, ifMatch: "\"-1\"")).Status);

        var created = await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(city: "Riyadh"), cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("\"1\"", created.Headers.ETag!.Tag);
        Assert.Equal("synced", created.Json.GetProperty("projection").GetString());
        Assert.Equal("Riyadh", created.Json.GetProperty("profile").GetProperty("city").GetString());

        var stale = await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(city: "x"), cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal("revision_conflict", stale.Error);
        Assert.Equal(1, stale.Json.GetProperty("currentRevision").GetInt32());

        var invalid = await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(nameAr: "", nameEn: null, country: "ZZ"), cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Equal("validation_failed", invalid.Error);
        Assert.Contains(invalid.Json.GetProperty("fields").EnumerateArray(), f => f.GetProperty("field").GetString() == "countryCode");

        var oversize = await Send(HttpMethod.Put, "/api/v1/company/profile", cookie: cookie, ifMatch: "\"1\"", raw: "{\"nameAr\":\"" + new string('x', 40_000) + "\"}");
        Assert.Contains(oversize.Status, new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest });   // rejected by the body-size limit (TestServer reports 400, Kestrel 413)
        Assert.Equal("Riyadh", (await Send(HttpMethod.Get, "/api/v1/company/profile", cookie: cookie)).Json.GetProperty("profile").GetProperty("city").GetString());
    }

    [PostgresFact]
    public async Task Access_is_deny_by_default_and_responses_never_reveal_physical_names_or_credentials()
    {
        await StartApi();
        var adminCookie = await SignIn(Admin, Company.Id);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), adminCookie, ifMatch: "\"0\"")).Status);

        var nobody = await MemberWith(Company, Admin);
        var viewer = await MemberWith(Company, Admin, "company.profile.view");
        var nobodyCookie = await SignIn(nobody, Company.Id);
        var viewerCookie = await SignIn(viewer, Company.Id);

        foreach (var path in new[] { "/api/v1/company/profile", "/api/v1/company/settings", "/api/v1/company/currencies", "/api/v1/company/currencies/reference", "/api/v1/company/history", "/api/v1/company/entitlements" })
        {
            var denied = await Send(HttpMethod.Get, path, cookie: nobodyCookie);
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
            Assert.Equal("forbidden", denied.Error);
        }
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/company/profile", cookie: viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(city: "x"), viewerCookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/company/history", cookie: viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/company/profile/display-name-sync", cookie: viewerCookie)).Status);

        AssertNoLeak(bodies, Company);
        AssertNoLeak(logs.Entries, Company);
    }

    [PostgresFact]
    public async Task A_user_of_another_company_cannot_select_or_reach_this_company()
    {
        await StartApi();
        var other = await ReadyCompany();
        var otherAdmin = await NewAdminOf(other);
        var cookie = await SignIn(otherAdmin, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = Company.Id }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Get, "/api/v1/company/profile", cookie: cookie)).Status);   // still no company selected: nothing is reachable
        var list = await Send(HttpMethod.Get, "/api/v1/companies", cookie: cookie);
        Assert.DoesNotContain(Company.Id.ToString(), list.Body);
    }

    [PostgresFact]
    public async Task The_company_list_shows_names_only_to_members_and_never_physical_identifiers()
    {
        await StartApi();
        var adminCookie = await SignIn(Admin, Company.Id);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody("شركة سرية", "Secret Co"), adminCookie, ifMatch: "\"0\"")).Status);

        var mine = await Send(HttpMethod.Get, "/api/v1/companies", cookie: adminCookie);
        var entry = Assert.Single(mine.Json.GetProperty("companies").EnumerateArray());
        Assert.Equal("شركة سرية", entry.GetProperty("nameAr").GetString());
        Assert.Equal("Secret Co", entry.GetProperty("nameEn").GetString());

        var outsider = await NewUser("outsider");
        var theirs = await Send(HttpMethod.Get, "/api/v1/companies", cookie: await SignIn(outsider, null));
        Assert.Empty(theirs.Json.GetProperty("companies").EnumerateArray());
        Assert.DoesNotContain("Secret", theirs.Body);
        AssertNoLeak(bodies, Company);
    }

    [PostgresFact]
    public async Task Settings_and_currencies_flow_including_the_Trial_currency_denial()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);

        var settings = await Send(HttpMethod.Get, "/api/v1/company/settings", cookie: cookie);
        Assert.False(settings.Json.GetProperty("setupComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settings.Json.GetProperty("settings").GetProperty("timeZone").ValueKind);
        var saved = await Send(HttpMethod.Put, "/api/v1/company/settings", new { defaultLanguage = "ar", dateFormat = "yyyy-MM-dd", numeralSystem = "latin", timeZone = "UTC" }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.True(saved.Json.GetProperty("setupComplete").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/api/v1/company/settings", new { defaultLanguage = "xx" }, cookie, ifMatch: "\"1\"")).Status);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/company/currencies/reference", cookie: cookie)).Status);
        var noBase = await Send(HttpMethod.Get, "/api/v1/company/currencies", cookie: cookie);
        Assert.False(noBase.Json.GetProperty("baseCurrencySelected").GetBoolean());
        Assert.False(noBase.Json.GetProperty("baseCurrencyLock").GetProperty("checksImplemented").GetBoolean());

        var set = await Send(HttpMethod.Put, "/api/v1/company/currencies/SAR", new { enabled = true, isBase = true }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal("SAR", set.Json.GetProperty("baseCurrency").GetString());
        var denied = await Send(HttpMethod.Put, "/api/v1/company/currencies/USD", new { enabled = true, isBase = false }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("entitlement_denied", denied.Error);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/api/v1/company/currencies/sar", new { enabled = true, isBase = true }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, "/api/v1/company/currencies/EUR", new { enabled = true, isBase = false }, cookie)).Status);

        var entitlements = await Send(HttpMethod.Get, "/api/v1/company/entitlements", cookie: cookie);
        Assert.Equal("trial", entitlements.Json.GetProperty("edition").GetString());
        Assert.Contains(entitlements.Json.GetProperty("restrictions").EnumerateArray(), r => r.GetProperty("key").GetString() == "users" && !r.GetProperty("enforced").GetBoolean());
    }

    [PostgresFact]
    public async Task History_is_readable_by_permission_and_hides_sensitive_values()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);
        await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(tax: "TAX-SECRET-777", city: "Riyadh"), cookie, ifMatch: "\"0\"");
        var history = await Send(HttpMethod.Get, "/api/v1/company/history?entity=profile&limit=10", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        Assert.DoesNotContain("SECRET", history.Body);
        Assert.Contains("Riyadh", history.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/api/v1/company/history?entity=bogus", cookie: cookie)).Status);
    }

    [PostgresFact]
    public async Task An_unavailable_company_database_gives_one_generic_503_and_leaks_nothing()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);
        // The company database no longer matches what this backend expects (an edited migration checksum): the schema gate refuses it.
        await InCompany(Company.PhysicalDatabaseName, $"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'tampered' WHERE version = 3");
        var r = await Send(HttpMethod.Get, "/api/v1/company/profile", cookie: cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status);
        Assert.Equal("company_unavailable", r.Error);
        AssertNoLeak(bodies, Company);
        AssertNoLeak(logs.Entries, Company);
    }

    [PostgresFact]
    public async Task A_company_that_is_no_longer_Ready_is_refused_before_any_company_database_access()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);
        await Exec($"UPDATE {Co.Table} SET {Co.ProvisioningState} = 3 WHERE {Co.Id} = @i", ("i", Company.Id));       // Failed
        var r = await Send(HttpMethod.Get, "/api/v1/company/profile", cookie: cookie);
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);                                                              // authorization fails closed first
        AssertNoLeak(bodies, Company);
    }

    [PostgresFact]
    public async Task When_the_audit_cannot_be_written_the_request_fails_and_nothing_is_saved()
    {
        await StartApi();
        var cookie = await SignIn(Admin, Company.Id);
        AuditKeys.Unavailable = true;
        var r = await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), cookie, ifMatch: "\"0\"");
        AuditKeys.Unavailable = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status);
        Assert.Equal("audit_unavailable", r.Error);
        Assert.Equal(0L, await Count(Company, CompanySchema.Profile.Table));
        Assert.Equal(0L, await Count(Company, CompanySchema.ConfigRevision.Table));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/v1/company/profile", ProfileBody(), cookie, ifMatch: "\"0\"")).Status);
    }
}
