using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.1 slice S3a through the REAL API graph: the MfaVerified/MfaFresh policies on the named privileged routes, the step-up endpoint and the stable 401 answer, and the
/// mandatory pass-through for users without MFA (D-161). Against a real disposable PostgreSQL system database.
/// </summary>
public class MfaStepUpApiTests : IdentityTestBase
{
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;
    private readonly InMemoryKekRing ring = new("k1");

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
                s.AddSingleton<KekRing>(ring);
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        await Start();
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        if (Store is not null) await Store.DisposeAsync();
        await base.DisposeAsync();
    }

    private long Step => Totp.StepFor(Clock.GetUtcNow());

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Hasm-Request", "1");
        if (token is not null) request.Headers.Add("Authorization", "Bearer " + token);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> DesktopLogin(string login)
    {
        var r = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", null, new { login, password = Pw, client = "desktop" });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return JsonDocument.Parse(r.Body).RootElement.GetProperty("token").GetString()!;
    }

    private async Task<(string Token, byte[] Secret)> EnrolledAdminSignedIn(string login)
    {
        var user = await NewUser(login, admin: true);
        var secret = Totp.GenerateSecret();
        var mfa = factory!.Services.GetRequiredService<MfaStore>();
        Assert.True(await mfa.ActivatePendingAsync(user.Id, await mfa.CreatePendingAsync(user.Id, secret, null), Step - 10, null));
        var first = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", null, new { login, password = Pw, client = "desktop" });
        var challenge = JsonDocument.Parse(first.Body).RootElement.GetProperty("challenge").GetString();
        var verify = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/verify", null, new { challenge, code = Totp.Compute(secret, Step), client = "desktop" });
        Assert.Equal(HttpStatusCode.OK, verify.Status);
        return (JsonDocument.Parse(verify.Body).RootElement.GetProperty("token").GetString()!, secret);
    }

    /// <summary>The access group needs a selected company; reaching that business answer (409 company_not_selected) proves the MFA policy let the request through.</summary>
    private async Task PassedThePolicy(string token)
    {
        var r = await SendAsync(HttpMethod.Get, "/api/v1/access/permissions", token);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Contains("company_not_selected", r.Body);
    }

    [PostgresFact]
    public async Task A_user_without_MFA_keeps_every_privileged_flow_and_step_up_does_not_apply_to_them()
    {
        await StartApi();
        await NewUser("admin", admin: true);
        var target = await NewUser("target");
        var token = await DesktopLogin("admin");

        await PassedThePolicy(token);                                                                                                                                 // MfaVerified group
        Clock.Advance(TimeSpan.FromHours(2));                                                                                                                        // far beyond the freshness window
        token = await DesktopLogin("admin");                                                                                                                         // (the idle session would have expired)
        var disable = await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/state", token, new { state = "disabled" });                                // MfaFresh route
        Assert.Equal(HttpStatusCode.NoContent, disable.Status);
        var stepUp = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/step-up", token, new { code = "123456" });
        Assert.Equal(HttpStatusCode.Conflict, stepUp.Status);
        Assert.Contains("mfa_not_enrolled", stepUp.Body);
    }

    [PostgresFact]
    public async Task An_enrolled_administrator_needs_a_fresh_proof_for_the_named_operations_and_step_up_restores_it()
    {
        await StartApi();
        var target = await NewUser("target");
        var (token, secret) = await EnrolledAdminSignedIn("admin");

        var path = $"/api/v1/admin/users/{target.Id}/state";
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, path, token, new { state = "disabled" })).Status);        // fresh: login proof just made

        Clock.Advance(TimeSpan.FromMinutes(16));                                                                                          // proof older than the 15 minute window; idle session still live
        var stale = await SendAsync(HttpMethod.Post, path, token, new { state = "active" });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.Status);
        Assert.Contains("mfa_step_up_required", stale.Body);
        await PassedThePolicy(token);                                                                                                     // MfaVerified-only routes are not affected by age
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/auth/status", token)).Status);

        var wrong = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/step-up", token, new { code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Contains("invalid_code", wrong.Body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, path, token, new { state = "active" })).Status);       // still not fresh

        var good = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/step-up", token, new { code = Totp.Compute(secret, Step) });
        Assert.Equal(HttpStatusCode.OK, good.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, path, token, new { state = "active" })).Status);

        var anonymous = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/step-up", null, new { code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.DoesNotContain("mfa_step_up_required", anonymous.Body);                                                                    // an unauthenticated caller gets the ordinary 401
    }

    [PostgresFact]
    public async Task Session_mode_enrolment_refuses_a_transport_that_differs_from_the_sessions_client()
    {
        await StartApi();
        await NewUser("plain");
        var token = await DesktopLogin("plain");                                                                                          // a desktop session
        var begin = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/enrol/begin", token, new { password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.BadRequest, begin.Status);
        var confirm = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/enrol/confirm", token, new { code = "123456", client = "web" });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.Status);
        var matching = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/enrol/begin", token, new { password = Pw, client = "desktop" });
        Assert.NotEqual(HttpStatusCode.BadRequest, matching.Status);                                                                      // the same client is accepted by the transport check
    }
}
