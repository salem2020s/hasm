using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 2.3C-1 end to end: throttling follows the validated client identity (direct peer, or a forwarded address accepted only from a
/// configured proxy), spoofed forwarded headers neither evade nor frame anyone, and the administrator-continuity guard reaches the API.
/// The connection peer is simulated through X-Test-Ip (the in-memory transport has no sockets).
/// </summary>
public class SecurityHardeningApiTests : AccessTestBase
{
    private const string CookieName = "__Host-hasm_session";
    private const string RootPassword = "Root-Passphrase-Strong-1";
    private const string Proxy = "10.1.1.1";

    private readonly LogCapture logs = new();
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;

    private sealed class PeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Ip", out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return nxt();
            });
            next(app);
        };
    }

    private async Task StartApi(bool proxy = false)
    {
        await StartAccess();
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
            b.UseSetting("Hasm:Auth:Pbkdf2Iterations", "100000");
            if (proxy)
            {
                b.UseSetting("Hasm:ReverseProxy:Enabled", "true");
                b.UseSetting("Hasm:ReverseProxy:KnownProxies:0", Proxy);
            }
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton<IStartupFilter>(new PeerFilter());
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        await base.DisposeAsync();
    }

    private async Task<ApiResponse> Send(HttpMethod method, string path, object? body, string peer, string? forwardedFor = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Hasm-Request", "1");
        request.Headers.Add("X-Test-Ip", peer);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private Task<ApiResponse> TryLogin(string login, string password, string peer, string? forwardedFor = null) =>
        Send(HttpMethod.Post, "/api/v1/auth/login", new { login, password }, peer, forwardedFor);

    private async Task<HttpStatusCode> Wrong(string login, string peer, string? forwardedFor = null, int attempt = 0) =>
        (await TryLogin(login, "wrong-password-" + attempt, peer, forwardedFor)).Status;

    private async Task BlockPair(string login, string peer, Func<int, string?>? forwardedFor = null)
    {
        for (var i = 0; i < AuthSettings.MaxFailuresPerLoginSource; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await Wrong(login, peer, forwardedFor?.Invoke(i), i));
    }

    // ---------------------------------------------------------------- direct connections

    [PostgresFact]
    public async Task Without_a_proxy_forwarded_headers_are_ignored_so_rotating_them_does_not_evade_the_throttle()
    {
        await StartApi();
        await NewUser("alice");

        await BlockPair("alice", "203.0.113.5", i => $"198.51.100.{i + 1}");     // the attacker rotates X-Forwarded-For on every attempt

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, "203.0.113.5", "198.51.100.99")).Status);
    }

    [PostgresFact]
    public async Task Without_a_proxy_an_attacker_cannot_frame_another_client_by_claiming_its_address()
    {
        await StartApi();
        await NewUser("alice");
        await BlockPair("alice", "203.0.113.5", _ => "198.51.100.7");            // attacker claims to be the victim's address

        var victim = await TryLogin("alice", Pw, "198.51.100.7");

        Assert.Equal(HttpStatusCode.OK, victim.Status);
    }

    [PostgresFact]
    public async Task The_real_user_signs_in_normally_while_an_attacker_is_blocked()
    {
        await StartApi();
        await NewUser("alice");
        await BlockPair("alice", "203.0.113.5");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, "203.0.113.5")).Status);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("alice", Pw, "198.51.100.7")).Status);
    }

    // ---------------------------------------------------------------- behind a configured proxy

    [PostgresFact]
    public async Task Behind_a_trusted_proxy_each_forwarded_client_has_its_own_throttle()
    {
        await StartApi(proxy: true);
        await NewUser("alice");
        await BlockPair("alice", Proxy, _ => "198.51.100.1");                    // client 1, seen through the proxy

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, Proxy, "198.51.100.1")).Status);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("alice", Pw, Proxy, "198.51.100.2")).Status);        // client 2 behind the same proxy is unaffected
    }

    [PostgresFact]
    public async Task Behind_a_trusted_proxy_a_client_cannot_rotate_by_prepending_forged_addresses()
    {
        await StartApi(proxy: true);
        await NewUser("alice");
        // the proxy appends the real client address; everything the client put to its left is attacker-controlled
        await BlockPair("alice", Proxy, i => $"9.9.9.{i + 1}, 198.51.100.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, Proxy, "6.6.6.6, 198.51.100.1")).Status);
    }

    [PostgresFact]
    public async Task A_peer_that_is_not_a_configured_proxy_gets_no_benefit_from_forwarded_headers()
    {
        await StartApi(proxy: true);
        await NewUser("alice");

        await BlockPair("alice", "203.0.113.5", i => $"198.51.100.{i + 1}");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, "203.0.113.5", "198.51.100.200")).Status);
    }

    [PostgresFact]
    public async Task Per_client_spraying_limit_follows_the_forwarded_client_and_not_the_shared_proxy()
    {
        await StartApi(proxy: true);
        await NewUser("alice");
        for (var i = 0; i < 20; i++)                                              // MaxFailuresPerSource (default 20), each login tried once
            Assert.Equal(HttpStatusCode.Unauthorized, await Wrong("victim" + i, Proxy, "198.51.100.1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, Proxy, "198.51.100.1")).Status);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("alice", Pw, Proxy, "198.51.100.2")).Status);       // everyone else behind the proxy still signs in
    }

    // ---------------------------------------------------------------- IPv6

    [PostgresFact]
    public async Task One_ipv6_subscriber_cannot_rotate_addresses_inside_its_slash_64()
    {
        await StartApi();
        await NewUser("alice");

        await BlockPair("alice", "2001:db8:7:7::1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin("alice", Pw, "2001:db8:7:7:abcd::99")).Status);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("alice", Pw, "2001:db8:7:8::1")).Status);           // a different /64 is a different client
    }

    // ---------------------------------------------------------------- administrator continuity over HTTP

    private async Task<string> RootCookie()
    {
        var r = await TryLogin("root", RootPassword, "198.51.100.250");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.SetCookies.Single(c => c.StartsWith(CookieName + "=")).Split(';')[0][(CookieName.Length + 1)..];
    }

    private async Task SeedRoot() =>
        Assert.Equal(CreateUserStatus.Created, (await new AuthService(Store, new PasswordService(100_000), AuthSettings)
            .BootstrapAdministratorAsync("root", "Root", RootPassword))!.Status);

    [PostgresFact]
    public async Task The_state_endpoint_refuses_to_disable_the_last_company_administrator_with_a_generic_conflict()
    {
        await StartApi();
        await SeedRoot();
        var company = await NewCompany();
        var boss = await NewAdmin(company, "boss");
        var cookie = await RootCookie();

        var refused = await Send(HttpMethod.Post, $"/api/v1/admin/users/{boss.Id}/state", new { state = "disabled" }, "198.51.100.250", cookie: cookie);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("last_company_administrator", refused.Error);
        Assert.DoesNotContain(company.ToString(), refused.Body, StringComparison.OrdinalIgnoreCase);       // no company detail for the caller

        var status = await Send(HttpMethod.Post, $"/api/v1/auth/login", new { login = boss.Login, password = Pw }, "198.51.100.251");
        Assert.Equal(HttpStatusCode.OK, status.Status);                                                      // still Active and able to sign in

        await NewAdmin(company, "deputy");
        var allowed = await Send(HttpMethod.Post, $"/api/v1/admin/users/{boss.Id}/state", new { state = "locked" }, "198.51.100.250", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, allowed.Status);
    }

    [PostgresFact]
    public async Task The_state_endpoint_refuses_to_disable_the_last_other_identity_administrator_path_stays_consistent()
    {
        await StartApi();
        await SeedRoot();
        var cookie = await RootCookie();
        var second = await Send(HttpMethod.Post, "/api/v1/admin/users",
            new { login = "root2", displayName = "Second", password = "Second-Passphrase-Strong-2", isIdentityAdministrator = true }, "198.51.100.250", cookie: cookie);
        Assert.Equal(HttpStatusCode.Created, second.Status);
        var secondId = second.Json.GetProperty("id").GetGuid();

        // root may disable root2 (root remains); root cannot touch itself
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{secondId}/state", new { state = "disabled" }, "198.51.100.250", cookie: cookie)).Status);
        var rootId = (await Send(HttpMethod.Get, "/api/v1/auth/status", null, "198.51.100.250", cookie: cookie)).Json.GetProperty("user").GetProperty("id").GetGuid();
        var own = await Send(HttpMethod.Post, $"/api/v1/admin/users/{rootId}/state", new { state = "disabled" }, "198.51.100.250", cookie: cookie);
        Assert.Equal(HttpStatusCode.Conflict, own.Status);
        Assert.Equal("cannot_change_own_state", own.Error);
    }
}
