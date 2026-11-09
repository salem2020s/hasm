using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hasm.Database.IntegrationTests;

/// <summary>Records every log entry (message, state values, exception text) so tests can prove no secret is ever logged.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> entries = [];
    public IReadOnlyList<string> Entries { get { lock (entries) return [.. entries]; } }
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CaptureLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{category}: {formatter(state, exception)} {exception}";
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs) text += " " + string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"));
            lock (owner.entries) owner.entries.Add(text);
        }
    }
}

public sealed record ApiResponse(HttpStatusCode Status, HttpResponseHeaders Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    public IEnumerable<string> SetCookies => Headers.TryGetValues("Set-Cookie", out var v) ? v : [];
    public string? Error => Json.TryGetProperty("error", out var e) ? e.GetString() : null;
}

/// <summary>End-to-end HTTP tests of the authentication API against a real disposable PostgreSQL system database.</summary>
public class AuthApiTests : IdentityTestBase
{
    private const string CookieName = "__Host-hasm_session";
    private const string AdminPassword = "Admin-Passphrase-Strong-1";
    private const string UserPassword = "User-Passphrase-Strong-2";

    private readonly LogCapture logs = new();
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;

    private sealed class RemoteIpFilter : IStartupFilter
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

    private async Task StartApi(Action<IWebHostBuilder>? configure = null)
    {
        await new SystemDatabaseInitializer(Options).InitializeAsync();
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
            b.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            configure?.Invoke(b);
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton<IStartupFilter>(new RemoteIpFilter());
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false,
        });

        // Seed through the same code path as the Hasm.Admin bootstrap command (not through HTTP: there is no public registration).
        await Start();
        var seed = new AuthService(Store, new PasswordService(100_000), AuthSettings);
        Assert.Equal(CreateUserStatus.Created, (await seed.BootstrapAdministratorAsync("root", "Root Admin", AdminPassword))!.Status);
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        if (Store is not null) await Store.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, string? cookie = null, string? bearer = null,
        bool header = true, string ip = "10.0.0.1", string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Test-Ip", ip);
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private static string CookieValue(ApiResponse r) =>
        r.SetCookies.Single(c => c.StartsWith(CookieName + "=")).Split(';')[0][(CookieName.Length + 1)..];

    private Task<ApiResponse> WebLogin(string login, string password, string ip = "10.0.0.1") =>
        Send(HttpMethod.Post, "/api/v1/auth/login", new { login, password }, ip: ip);

    private async Task<string> AdminCookie() => CookieValue(await WebLogin("root", AdminPassword));

    private async Task CreateUserViaApi(string cookie, string login, string password = UserPassword)
    {
        var r = await Send(HttpMethod.Post, "/api/v1/admin/users", new { login, displayName = "User " + login, password }, cookie);
        Assert.Equal(HttpStatusCode.Created, r.Status);
    }

    // ---- status / login / logout ----

    [PostgresFact]
    public async Task Anonymous_status_reports_unauthenticated_and_is_never_cached()
    {
        await StartApi();
        var r = await Send(HttpMethod.Get, "/api/v1/auth/status");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.Json.GetProperty("authenticated").GetBoolean());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [PostgresFact]
    public async Task Web_login_issues_a_hardened_cookie_and_keeps_the_token_out_of_the_body()
    {
        await StartApi();
        var r = await WebLogin("root", AdminPassword);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var cookie = r.SetCookies.Single();
        Assert.StartsWith(CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);   // required by the __Host- prefix
        Assert.DoesNotContain(CookieValue(r), r.Body);
        Assert.Equal(JsonValueKind.Null, r.Json.GetProperty("token").ValueKind);
        Assert.Equal("root", r.Json.GetProperty("user").GetProperty("login").GetString());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
    }

    [PostgresFact]
    public async Task Cookie_authenticates_status_and_logout_revokes_it_server_side()
    {
        await StartApi();
        var cookie = CookieValue(await WebLogin("root", AdminPassword));

        var status = await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: cookie);
        Assert.True(status.Json.GetProperty("authenticated").GetBoolean());
        Assert.Equal("web", status.Json.GetProperty("session").GetProperty("client").GetString());

        var logout = await Send(HttpMethod.Post, "/api/v1/auth/logout", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        Assert.Contains(logout.SetCookies, c => c.StartsWith(CookieName + "=;", StringComparison.Ordinal) && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        // The old cookie value is dead even if a thief kept a copy.
        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: cookie)).Json.GetProperty("authenticated").GetBoolean());
    }

    [PostgresFact]
    public async Task Desktop_login_returns_a_bearer_token_and_no_cookie()
    {
        await StartApi();
        var r = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = "root", password = AdminPassword, client = "desktop" });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Empty(r.SetCookies);
        var token = r.Json.GetProperty("token").GetString()!;
        Assert.True(SessionToken.IsWellFormed(token));

        var status = await Send(HttpMethod.Get, "/api/v1/auth/status", bearer: token);
        Assert.True(status.Json.GetProperty("authenticated").GetBoolean());
        Assert.Equal("desktop", status.Json.GetProperty("session").GetProperty("client").GetString());

        await Send(HttpMethod.Post, "/api/v1/auth/logout", bearer: token);
        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", bearer: token)).Json.GetProperty("authenticated").GetBoolean());
    }

    [PostgresFact]
    public async Task Failed_logins_return_identical_responses_whatever_the_reason()
    {
        await StartApi();
        var cookie = await AdminCookie();
        await CreateUserViaApi(cookie, "disabled.user");
        await CreateUserViaApi(cookie, "locked.user");
        var disabled = (await Store.FindUserByLoginAsync("disabled.user"))!;
        var locked = (await Store.FindUserByLoginAsync("locked.user"))!;
        await Send(HttpMethod.Post, $"/api/v1/admin/users/{disabled.Id}/state", new { state = "Disabled" }, cookie);
        await Send(HttpMethod.Post, $"/api/v1/admin/users/{locked.Id}/state", new { state = "Locked" }, cookie);

        var responses = new[]
        {
            await WebLogin("root", "wrong-password-xx", "10.1.0.1"),
            await WebLogin("nobody", AdminPassword, "10.1.0.2"),
            await WebLogin("bad login!!", AdminPassword, "10.1.0.3"),
            await WebLogin("disabled.user", UserPassword, "10.1.0.4"),
            await WebLogin("locked.user", UserPassword, "10.1.0.5"),
        };

        Assert.All(responses, r =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
            Assert.Equal("{\"error\":\"invalid_credentials\"}", r.Body);
            Assert.Empty(r.SetCookies);
        });
    }

    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("{\"login\":\"root\"}")]
    [InlineData("{\"login\":\"root\",\"password\":\"x\",\"client\":\"tv\"}")]
    [InlineData("not json")]
    public async Task Malformed_login_requests_are_rejected_without_detail(string body)
    {
        await StartApi();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Hasm-Request", "1");
        using var response = await client.SendAsync(request);
        Assert.True((int)response.StatusCode is 400 or 415);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("   at ", text);
    }

    [PostgresFact]
    public async Task State_changing_calls_without_the_request_header_are_refused()
    {
        await StartApi();
        var cookie = await AdminCookie();
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = "root", password = AdminPassword }, header: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/auth/logout", cookie: cookie, header: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "x1x", displayName = "X", password = UserPassword }, cookie, header: false)).Status);
        // ...and the refused logout really did nothing
        Assert.True((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: cookie)).Json.GetProperty("authenticated").GetBoolean());
    }

    [PostgresFact]
    public async Task Garbage_credentials_are_treated_as_anonymous()
    {
        await StartApi();
        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: "not-a-token")).Json.GetProperty("authenticated").GetBoolean());
        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", bearer: SessionToken.Generate())).Json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = "a", newPassword = "b" }, bearer: "x")).Status);
    }

    [PostgresFact]
    public async Task Idle_and_absolute_expiry_apply_to_http_sessions()
    {
        await StartApi();
        var cookie = CookieValue(await WebLogin("root", AdminPassword));
        Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: cookie)).Json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = AdminPassword, newPassword = "x" }, cookie)).Status);
    }

    // ---- throttling ----

    [PostgresFact]
    public async Task Brute_force_attempts_receive_429_with_retry_after_and_a_generic_body()
    {
        await StartApi();
        for (var i = 0; i < AuthSettings.MaxFailuresPerLogin; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin("root", "wrong-password-" + i, "10.2.0." + i)).Status);

        var blocked = await WebLogin("root", AdminPassword, "10.2.9.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.Status);
        Assert.Equal("too_many_attempts", blocked.Error);
        Assert.InRange(int.Parse(blocked.Headers.GetValues("Retry-After").Single()), 1, 15 * 60);

        // An identifier that never existed is throttled in exactly the same way (no account enumeration).
        for (var i = 0; i < AuthSettings.MaxFailuresPerLogin; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin("never-existed", "wrong-password-" + i, "10.2.1." + i)).Status);
        var ghost = await WebLogin("never-existed", AdminPassword, "10.2.9.8");
        Assert.Equal(HttpStatusCode.TooManyRequests, ghost.Status);
        Assert.Equal(blocked.Body, ghost.Body);
    }

    // ---- authorization of identity administration ----

    [PostgresFact]
    public async Task Identity_administration_requires_authentication_and_the_administrator_flag()
    {
        await StartApi();
        var admin = await AdminCookie();
        await CreateUserViaApi(admin, "plain.user");
        var plain = CookieValue(await WebLogin("plain.user", UserPassword, "10.3.0.1"));
        var newUser = new { login = "intruder", displayName = "Intruder", password = UserPassword };

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/admin/users", newUser)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/admin/users", newUser, bearer: SessionToken.Generate())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/admin/users", newUser, plain)).Status);
        var victim = (await Store.FindUserByLoginAsync("root"))!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{victim.Id}/state", new { state = "Disabled" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{victim.Id}/state", new { state = "Disabled" }, plain)).Status);

        Assert.Null(await Store.FindUserByLoginAsync("intruder"));
        Assert.Equal(UserState.Active, (await Store.FindUserByIdAsync(victim.Id))!.State);
    }

    [PostgresFact]
    public async Task Administrator_creates_users_who_can_then_log_in()
    {
        await StartApi();
        var admin = await AdminCookie();
        var created = await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "New.User", displayName = "New User", password = UserPassword }, admin);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("new.user", created.Json.GetProperty("login").GetString());
        Assert.False(created.Json.GetProperty("isIdentityAdministrator").GetBoolean());
        Assert.DoesNotContain("password", created.Body, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await WebLogin("new.user", UserPassword, "10.4.0.1")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = " NEW.user ", displayName = "Dup", password = UserPassword }, admin)).Status);
    }

    [PostgresFact]
    public async Task User_creation_validates_input()
    {
        await StartApi();
        var admin = await AdminCookie();
        var weak = await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "weak.user", displayName = "W", password = "short" }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);
        Assert.Equal("weak_password", weak.Error);
        Assert.DoesNotContain("short", weak.Body.Replace("too_short", ""));
        Assert.Equal("invalid_login", (await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "bad login", displayName = "W", password = UserPassword }, admin)).Error);
        Assert.Equal("invalid_display_name", (await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "okay.user", displayName = " ", password = UserPassword }, admin)).Error);
        Assert.Null(await Store.FindUserByLoginAsync("weak.user"));
    }

    [PostgresFact]
    public async Task Disabling_a_user_ends_their_sessions_and_blocks_login_until_reactivated()
    {
        await StartApi();
        var admin = await AdminCookie();
        await CreateUserViaApi(admin, "target");
        var target = (await Store.FindUserByLoginAsync("target"))!;
        var targetCookie = CookieValue(await WebLogin("target", UserPassword, "10.5.0.1"));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/state", new { state = "disabled" }, admin)).Status);

        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: targetCookie)).Json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin("target", UserPassword, "10.5.0.2")).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/state", new { state = "Active" }, admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await WebLogin("target", UserPassword, "10.5.0.3")).Status);
    }

    [PostgresFact]
    public async Task State_change_edge_cases()
    {
        await StartApi();
        var admin = await AdminCookie();
        var root = (await Store.FindUserByLoginAsync("root"))!;
        Assert.Equal("cannot_change_own_state", (await Send(HttpMethod.Post, $"/api/v1/admin/users/{root.Id}/state", new { state = "Disabled" }, admin)).Error);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{Guid.NewGuid()}/state", new { state = "Disabled" }, admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{root.Id}/state", new { state = "1" }, admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{root.Id}/state", new { state = "Deleted" }, admin)).Status);
        Assert.Equal(UserState.Active, (await Store.FindUserByIdAsync(root.Id))!.State);
    }

    [PostgresTheory]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/auth/signup")]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/register")]
    public async Task There_is_no_public_self_registration(string path)
    {
        await StartApi();
        var r = await Send(HttpMethod.Post, path, new { login = "self.service", displayName = "S", password = UserPassword });
        Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Null(await Store.FindUserByLoginAsync("self.service"));
    }

    [PostgresFact]
    public async Task Change_password_endpoint_rotates_credentials_and_revokes_other_sessions()
    {
        await StartApi();
        var first = CookieValue(await WebLogin("root", AdminPassword, "10.6.0.1"));
        var second = CookieValue(await WebLogin("root", AdminPassword, "10.6.0.2"));
        const string replacement = "Replacement-Passphrase-3";

        Assert.Equal("invalid_current_password", (await Send(HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = "nope-nope-nope", newPassword = replacement }, first)).Error);
        Assert.Equal("weak_password", (await Send(HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = AdminPassword, newPassword = "short" }, first)).Error);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = AdminPassword, newPassword = replacement }, first)).Status);

        Assert.True((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: first)).Json.GetProperty("authenticated").GetBoolean());
        Assert.False((await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: second)).Json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin("root", AdminPassword, "10.6.0.3")).Status);
        Assert.Equal(HttpStatusCode.OK, (await WebLogin("root", replacement, "10.6.0.3")).Status);
    }

    // ---- company isolation ----

    [PostgresFact]
    public async Task Authentication_exposes_and_grants_no_company_access()
    {
        await StartApi();
        var admin = await AdminCookie();
        var status = await Send(HttpMethod.Get, "/api/v1/auth/status", cookie: admin);
        Assert.DoesNotContain("compan", status.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/v1/companies", cookie: admin)).Status);   // even administrators get no registry endpoint
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.UserCompanyAccess.Table}"));
    }

    // ---- transport hardening ----

    [PostgresFact]
    public async Task Cors_allows_credentials_only_for_configured_origins()
    {
        await StartApi();
        var allowed = await Send(HttpMethod.Options, "/api/v1/auth/login", origin: "http://localhost:5173", header: false);
        var foreign = await Send(HttpMethod.Options, "/api/v1/auth/login", origin: "https://evil.example", header: false);
        Assert.True(allowed.Headers.Contains("Access-Control-Allow-Credentials") || allowed.Status == HttpStatusCode.NoContent);
        Assert.False(foreign.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(foreign.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [PostgresFact]
    public async Task Runtime_requests_never_fall_back_to_the_provisioning_administrator_account()
    {
        // Valid administrator credentials are configured, but the runtime account is not usable: the API must fail
        // closed (503) instead of silently borrowing the privileged account.
        await StartApi(b =>
        {
            b.UseSetting("Hasm:Database:Username", "hasm_no_such_runtime_role");
            b.UseSetting("Hasm:Database:Password", "irrelevant-runtime-password");
        });
        var r = await WebLogin("root", AdminPassword);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status);
        Assert.Equal("service_unavailable", r.Error);
        Assert.DoesNotContain("irrelevant-runtime-password", r.Body + string.Join("|", logs.Entries));
        Assert.DoesNotContain(Options.AdminPassword!, r.Body + string.Join("|", logs.Entries));
    }

    // ---- sensitive information ----

    [PostgresFact]
    public async Task No_secret_ever_appears_in_responses_or_logs()
    {
        await StartApi();
        var secrets = new List<string> { AdminPassword, UserPassword, "wrong-password-secret-1", "Replacement-Passphrase-9", Options.Password!, Options.AdminPassword! };
        var bodies = new List<string>();

        var ok = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = "root", password = AdminPassword, client = "desktop" });
        var token = ok.Json.GetProperty("token").GetString()!;
        secrets.Add(token);
        var cookieLogin = await WebLogin("root", AdminPassword, "10.7.0.1");
        secrets.Add(CookieValue(cookieLogin));
        bodies.Add(cookieLogin.Body);
        bodies.Add((await WebLogin("root", "wrong-password-secret-1", "10.7.0.2")).Body);
        bodies.Add((await WebLogin("ghost", "wrong-password-secret-1", "10.7.0.3")).Body);
        bodies.Add((await Send(HttpMethod.Get, "/api/v1/auth/status", bearer: token)).Body);
        bodies.Add((await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "someone", displayName = "S", password = UserPassword }, bearer: token)).Body);
        bodies.Add((await Send(HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = AdminPassword, newPassword = "Replacement-Passphrase-9" }, bearer: token)).Body);
        await Send(HttpMethod.Post, "/api/v1/auth/logout", bearer: token);

        var hash = await Scalar<string>($"SELECT {SystemSchema.User.PasswordHash} FROM {SystemSchema.User.Table} WHERE {SystemSchema.User.Login} = 'root'");
        secrets.Add(hash);

        var everything = string.Join("\n", logs.Entries) + "\n" + string.Join("\n", bodies);
        foreach (var secret in secrets.Distinct())
            Assert.DoesNotContain(secret, everything);
        Assert.Contains(logs.Entries, e => e.Contains("auth.login"));   // the audit trail exists...
        Assert.DoesNotContain(logs.Entries, e => e.Contains("ghost"));   // ...and does not record attacker-supplied identifiers
    }
}
