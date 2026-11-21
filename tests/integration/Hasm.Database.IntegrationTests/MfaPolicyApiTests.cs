using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.1 slice S3b-1b through the REAL API graph: the company MFA policy endpoints (an MFA-fresh change), the company-selection answer and the per-request enforcement for a user without an authenticator
/// (403 mfa_enrolment_required, no company data), with the routes a user must always reach left open. Against a real disposable PostgreSQL system database.
/// </summary>
public class MfaPolicyApiTests : AccessTestBase
{
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;
    private readonly InMemoryKekRing ring = new("k1");

    private async Task StartApi()
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
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton(_ => Audit);
                s.AddSingleton<KekRing>(ring);
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
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

    private async Task<string> PasswordLogin(UserRecord user)
    {
        var r = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", null, new { login = user.Login, password = Pw, client = "desktop" });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return JsonDocument.Parse(r.Body).RootElement.GetProperty("token").GetString()!;
    }

    private async Task<(string Token, byte[] Secret)> MfaLogin(UserRecord user)
    {
        var secret = Totp.GenerateSecret();
        var mfa = factory!.Services.GetRequiredService<MfaStore>();
        Assert.True(await mfa.ActivatePendingAsync(user.Id, await mfa.CreatePendingAsync(user.Id, secret, null), Step - 10, null));
        var first = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", null, new { login = user.Login, password = Pw, client = "desktop" });
        var challenge = JsonDocument.Parse(first.Body).RootElement.GetProperty("challenge").GetString();
        var verify = await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/verify", null, new { challenge, code = Totp.Compute(secret, Step), client = "desktop" });
        Assert.Equal(HttpStatusCode.OK, verify.Status);
        return (JsonDocument.Parse(verify.Body).RootElement.GetProperty("token").GetString()!, secret);
    }

    private Task<HttpStatusCode> Select(string token, Guid company) => SendAsync(HttpMethod.Post, "/api/v1/session/company", token, new { companyId = company }).ContinueWith(t => t.Result.Status);

    private async Task<string> ErrorOf(HttpMethod method, string path, string token, object? body = null) =>
        JsonDocument.Parse((await SendAsync(method, path, token, body)).Body).RootElement.GetProperty("error").GetString()!;

    [PostgresFact]
    public async Task A_user_without_an_authenticator_is_refused_in_a_company_that_requires_MFA_and_can_always_leave_it()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var plain = await NewMember(company, admin, "plain");
        var (adminToken, _) = await MfaLogin(admin);

        Assert.Equal(HttpStatusCode.OK, await Select(adminToken, company));
        var set = await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", adminToken, new { policy = "all" });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        var read = await SendAsync(HttpMethod.Get, "/api/v1/access/mfa-policy", adminToken);
        Assert.Equal("all", JsonDocument.Parse(read.Body).RootElement.GetProperty("policy").GetString());

        var token = await PasswordLogin(plain);
        var select = await SendAsync(HttpMethod.Post, "/api/v1/session/company", token, new { companyId = company });
        Assert.Equal(HttpStatusCode.Forbidden, select.Status);
        Assert.Contains("mfa_enrolment_required", select.Body);                                                       // refused at selection, nothing served
        Assert.Equal("company_not_selected", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));              // and the selection was undone

        // a stale selection (made before the policy existed) is refused per request too, while the open routes keep working
        await Exec($"UPDATE {S.Table} SET {S.SelectedCompanyId} = @c WHERE {S.UserId} = @u", ("c", company), ("u", plain.Id));
        Assert.Equal("mfa_enrolment_required", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/company/branches", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/companies", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/auth/status", token)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/v1/session/company", token)).Status);   // the user can leave
        Assert.Equal("company_not_selected", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));

        // the administrator, who has an authenticator, is unaffected
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/access/me", adminToken)).Status);
    }

    [PostgresFact]
    public async Task A_roles_policy_refuses_only_the_holders_of_a_listed_role_and_turning_it_off_restores_access()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var holder = await NewMember(company, admin, "holder");
        var free = await NewMember(company, admin, "free");
        var role = await SeedRole(company, "Needs MFA", "company.items.view");
        await SeedAssignment(holder.Id, company, role);
        var (adminToken, _) = await MfaLogin(admin);
        Assert.Equal(HttpStatusCode.OK, await Select(adminToken, company));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", adminToken, new { policy = "roles", roleIds = new[] { role } })).Status);
        var holderToken = await PasswordLogin(holder);
        var freeToken = await PasswordLogin(free);
        Assert.Equal(HttpStatusCode.Forbidden, await Select(holderToken, company));
        Assert.Equal(HttpStatusCode.OK, await Select(freeToken, company));                                           // not listed: pass-through

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", adminToken, new { policy = "off" })).Status);
        Assert.Equal(HttpStatusCode.OK, await Select(holderToken, company));
    }

    [PostgresFact]
    public async Task Changing_the_policy_needs_a_fresh_proof_a_valid_request_and_never_locks_out_the_administrator()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var plainAdmin = await NewAdmin(company, "plainadmin");                                                      // another administrator WITHOUT an authenticator
        var (token, secret) = await MfaLogin(admin);
        Assert.Equal(HttpStatusCode.OK, await Select(token, company));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", token, new { policy = "sometimes" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", token, new { policy = "roles" })).Status);   // roles kind needs roles

        Clock.Advance(TimeSpan.FromMinutes(16));                                                                      // the proof is older than the window
        var stale = await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", token, new { policy = "all" });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.Status);
        Assert.Contains("mfa_step_up_required", stale.Body);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/access/mfa-policy", token)).Status);   // reading is not a fresh operation
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/step-up", token, new { code = Totp.Compute(secret, Step) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", token, new { policy = "all" })).Status);

        // an administrator without an authenticator cannot require MFA of themselves (409), whatever the freshness (pass-through gives them a fresh session)
        var plainToken = await PasswordLogin(plainAdmin);
        var off = await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", token, new { policy = "off" });
        Assert.Equal(HttpStatusCode.OK, off.Status);
        Assert.Equal(HttpStatusCode.OK, await Select(plainToken, company));
        var lockout = await SendAsync(HttpMethod.Put, "/api/v1/access/mfa-policy", plainToken, new { policy = "all" });
        Assert.Equal(HttpStatusCode.Conflict, lockout.Status);
        Assert.Contains("mfa_policy_requires_own_enrolment", lockout.Body);
    }
}
