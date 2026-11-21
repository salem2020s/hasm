using System.Net;
using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Hasm.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;

namespace Hasm.Api.Tests;

/// <summary>
/// Release 1 acceptance (security): every routed endpoint is protected unless it is on the explicit anonymous
/// allowlist below, and every anonymous request to a protected endpoint is answered 401 before any database access.
/// A new route added without authorization makes these tests fail; the allowlist is the only place to justify one.
/// </summary>
public class EndpointAuthorizationInventoryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // Anonymous by design. Every entry is justified from the code that maps it.
    private static readonly Dictionary<string, string> AnonymousAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GET /api/v1/health"] = "Program.cs: liveness probe returning only status, service name and version",
        ["POST /api/v1/auth/login"] = "AuthEndpoints: credentials are the authentication; throttled and body-limited",
        ["POST /api/v1/auth/logout"] = "AuthEndpoints: ends the caller's own session; harmless without one",
        ["GET /api/v1/auth/status"] = "AuthEndpoints: reports authenticated=false to anonymous callers (tested without a database)",
    };

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private record Route(string Method, string Pattern, RouteEndpoint Endpoint)
    {
        public string Key => $"{Method} {Pattern}";
    }

    private static readonly string[] AllMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// EVERY routed endpoint (not only /api/): the pattern is normalised with a leading slash so "api/v1/x" and "/api/v1/x" are one
    /// thing, and an endpoint with no HTTP-method metadata is probed with every method, never assumed to be GET only.
    /// </summary>
    private IReadOnlyList<Route> ApiRoutes() =>
        factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? AllMethods)
                .Select(m => new Route(m.ToUpperInvariant(), "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'), e)))
            .OrderBy(r => r.Key, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Protected means: carries authorization data and no AllowAnonymous. Any IAuthorizeData counts (a plain RequireAuthorization or a
    /// named policy); whether the endpoint truly refuses anonymous callers is proven separately by the 401 probe below, and that the
    /// admin policy really rejects a non-administrator by the policy evaluation test.
    /// </summary>
    private static bool IsProtected(RouteEndpoint e) =>
        e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 && e.Metadata.GetMetadata<IAllowAnonymous>() is null;

    private static string Materialise(string pattern) =>
        Regex.Replace(pattern, @"\{[^}]*\}", m =>
            m.Value.Contains(":guid") ? "6f1d3c0a-0000-4000-8000-000000000001"
            : m.Value.Contains(":int") || m.Value.Contains(":long") ? "1" : "x");

    [Fact]
    public void The_inventory_is_not_empty_and_the_allowlist_has_no_stale_entries()
    {
        var routes = ApiRoutes();
        Assert.True(routes.Count > 100, $"Only {routes.Count} API routes discovered; enumeration is broken.");
        var keys = routes.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = AnonymousAllowlist.Keys.Where(k => !keys.Contains(k)).ToList();
        Assert.True(stale.Count == 0, string.Join(", ", stale));
    }

    [Fact]
    public void Every_api_route_requires_authorization_unless_explicitly_allowlisted()
    {
        var offenders = ApiRoutes().Where(r => !IsProtected(r.Endpoint) && !AnonymousAllowlist.ContainsKey(r.Key)).Select(r => r.Key).ToList();
        Assert.True(offenders.Count == 0, "Unprotected routes (add RequireAuthorization or justify in the allowlist):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Allowlisted_routes_are_exactly_the_anonymous_ones()
    {
        var anonymous = ApiRoutes().Where(r => !IsProtected(r.Endpoint)).Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(AnonymousAllowlist.Keys.OrderBy(k => k, StringComparer.Ordinal), anonymous);
    }

    [Fact]
    public void Admin_routes_require_the_identity_administrator_policy()
    {
        var admin = ApiRoutes().Where(r => r.Pattern.StartsWith("/api/v1/admin/", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(admin);
        foreach (var r in admin)
            Assert.True(r.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == AuthDefaults.AdministratorPolicy), r.Key);
    }

    [Fact]
    public async Task The_administrator_policy_rejects_an_authenticated_non_administrator_and_accepts_an_administrator()
    {
        var authz = factory.Services.GetRequiredService<IAuthorizationService>();
        static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, AuthDefaults.Scheme));
        var plain = User(new Claim(ClaimTypes.NameIdentifier, "u1"));
        var notAdmin = User(new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(AuthDefaults.AdministratorClaim, "false"));
        var admin = User(new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(AuthDefaults.AdministratorClaim, "true"));
        Assert.False((await authz.AuthorizeAsync(plain, null, AuthDefaults.AdministratorPolicy)).Succeeded);
        Assert.False((await authz.AuthorizeAsync(notAdmin, null, AuthDefaults.AdministratorPolicy)).Succeeded);
        Assert.False((await authz.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, AuthDefaults.AdministratorPolicy)).Succeeded);
        Assert.True((await authz.AuthorizeAsync(admin, null, AuthDefaults.AdministratorPolicy)).Succeeded);
    }

    [Fact]
    public async Task Anonymous_requests_to_every_protected_route_get_401_without_a_database()
    {
        var client = Client();
        var failures = new List<string>();
        foreach (var r in ApiRoutes().Where(r => !AnonymousAllowlist.ContainsKey(r.Key)))
        {
            var req = new HttpRequestMessage(new HttpMethod(r.Method), Materialise(r.Pattern));
            req.Headers.Add("X-Hasm-Request", "1");
            if (r.Method is "POST" or "PUT" or "PATCH") req.Content = JsonContent.Create(new { });
            var res = await client.SendAsync(req);
            if (res.StatusCode != HttpStatusCode.Unauthorized) failures.Add($"{r.Key} -> {(int)res.StatusCode}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
