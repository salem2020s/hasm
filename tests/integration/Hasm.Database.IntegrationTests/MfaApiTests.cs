using System.Net;
using System.Net.Http.Json;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.1 slice S2, review M1: the REAL dependency-injection graph of the API (not a hand-built service) answers a login for a user who needs the second factor with a challenge and never with a password-only session.
/// Against a real disposable PostgreSQL system database.
/// </summary>
public class MfaApiTests : IdentityTestBase
{
    private const string CookieName = "__Host-hasm_session";
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;

    private async Task StartApi()
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
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton(_ => Audit);
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        await Start();                                                          // the identity store and the test password policy (low cost) for seeding
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        if (Store is not null) await Store.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SetState(string login, int state) =>
        Exec($"UPDATE {U.Table} SET {M.State} = @s WHERE {U.Login} = @l", ("s", (short)state), ("l", login));

    private async Task<(HttpStatusCode Status, string Body, bool Cookie)> PostAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Hasm-Request", "1");
        using var response = await client.SendAsync(request);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var v) && v.Any(c => c.StartsWith(CookieName + "="));
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), cookie);
    }

    [PostgresFact]
    public async Task The_real_api_never_issues_a_password_only_session_to_a_user_who_needs_the_second_factor()
    {
        await StartApi();
        await NewUser("plain");
        await NewUser("enrolled");
        await NewUser("must");
        await NewUser("lost");
        await SetState("enrolled", 2);
        await SetState("must", 1);
        await SetState("lost", 2);
        await SetState("lost", 3);

        var ok = await PostAsync("/api/v1/auth/login", new { login = "plain", password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.True(ok.Cookie);                                                                          // a user without MFA still gets the cookie session

        var enrolled = await PostAsync("/api/v1/auth/login", new { login = "enrolled", password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.OK, enrolled.Status);
        Assert.False(enrolled.Cookie);                                                                   // NO session
        Assert.Contains("\"status\":\"mfa_required\"", enrolled.Body);
        Assert.Contains("\"challenge\":", enrolled.Body);

        var must = await PostAsync("/api/v1/auth/login", new { login = "must", password = Pw, client = "desktop" });
        Assert.False(must.Cookie);
        Assert.Contains("\"status\":\"mfa_enrolment_required\"", must.Body);
        Assert.DoesNotContain("\"token\"", must.Body);                                                   // and no bearer token for the desktop client either

        var lost = await PostAsync("/api/v1/auth/login", new { login = "lost", password = Pw, client = "web" });
        Assert.False(lost.Cookie);
        Assert.Contains("\"status\":\"mfa_recovery_required\"", lost.Body);
        Assert.DoesNotContain("\"challenge\"", lost.Body);
    }

    [PostgresFact]
    public async Task A_wrong_code_at_the_second_step_is_the_uniform_refusal_with_no_session()
    {
        await StartApi();
        await NewUser("enrolled");
        await SetState("enrolled", 2);
        var login = await PostAsync("/api/v1/auth/login", new { login = "enrolled", password = Pw, client = "web" });
        var challenge = System.Text.Json.JsonDocument.Parse(login.Body).RootElement.GetProperty("challenge").GetString();
        var verify = await PostAsync("/api/v1/auth/mfa/verify", new { challenge, code = "000000", client = "web" });
        Assert.Equal(HttpStatusCode.Unauthorized, verify.Status);
        Assert.False(verify.Cookie);
        Assert.Contains("invalid_credentials", verify.Body);
        var unknown = await PostAsync("/api/v1/auth/mfa/verify", new { challenge = SessionToken.Generate(), code = "000000", client = "web" });
        Assert.Equal(verify.Status, unknown.Status);                                                      // an unknown challenge answers exactly the same
        Assert.Equal(verify.Body, unknown.Body);
    }

    [PostgresFact]
    public async Task The_status_endpoint_reports_the_second_factor_state_of_the_signed_in_user()
    {
        await StartApi();
        await NewUser("plain");
        var login = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { login = "plain", password = Pw, client = "desktop" }), Headers = { { "X-Hasm-Request", "1" } } });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/status");
        status.Headers.Add("Authorization", "Bearer " + token);
        using var response = await client.SendAsync(status);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"mfa\":{\"state\":\"none\",\"verified\":false", body);
    }
}
