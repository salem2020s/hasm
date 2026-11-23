using Hasm.Infrastructure.Access;
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
    private IdentityKeys? sessionKeys;
    protected override IdentityKeys SessionKeys => sessionKeys ??= new IdentityKeys(ring);
    /// <summary>Test seam for the request pipeline's policy question only (the selection endpoint uses the real service): lets a test act between authorization and the endpoint.</summary>
    private Func<ICompanyMfaPolicyEvaluator, ICompanyMfaPolicyEvaluator>? wrapEvaluator;

    private sealed class HookedEvaluator(ICompanyMfaPolicyEvaluator inner, Func<Guid, Guid, Task> before) : ICompanyMfaPolicyEvaluator
    {
        public async Task<bool> RequiresMfaAsync(Guid userId, Guid companyId, CancellationToken ct = default)
        {
            var answer = await inner.RequiresMfaAsync(userId, companyId, ct);
            await before(userId, companyId);                                                                           // AFTER the decision, BEFORE the endpoint runs
            return answer;
        }
    }

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
                if (wrapEvaluator is { } wrap) s.AddSingleton<ICompanyMfaPolicyEvaluator>(sp => wrap(sp.GetRequiredService<MfaPolicyService>()));   // the last registration wins: what the authorization handler asks
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

    private async Task<(HttpStatusCode Status, string Body, string? ETag)> SendWithAsync(HttpMethod method, string path, string? token, object? body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Hasm-Request", "1");
        if (token is not null) request.Headers.Add("Authorization", "Bearer " + token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.ETag?.Tag);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        var r = await SendWithAsync(method, path, token, body, null);
        return (r.Status, r.Body);
    }

    private const string PolicyPath = "/api/v1/access/mfa-policy";

    /// <summary>What a client does: read the policy, then change it with the revision it read in If-Match.</summary>
    private async Task<(HttpStatusCode Status, string Body)> PutPolicy(string token, object body)
    {
        var read = await SendWithAsync(HttpMethod.Get, PolicyPath, token, null, null);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        var r = await SendWithAsync(HttpMethod.Put, PolicyPath, token, body, read.ETag);
        return (r.Status, r.Body);
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
        var set = await PutPolicy(adminToken, new { policy = "all" });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        var read = await SendAsync(HttpMethod.Get, "/api/v1/access/mfa-policy", adminToken);
        Assert.Equal("all", JsonDocument.Parse(read.Body).RootElement.GetProperty("policy").GetString());

        var token = await PasswordLogin(plain);
        var select = await SendAsync(HttpMethod.Post, "/api/v1/session/company", token, new { companyId = company });
        Assert.Equal(HttpStatusCode.Forbidden, select.Status);
        Assert.Contains("mfa_enrolment_required", select.Body);                                                       // refused at selection, nothing served
        Assert.Equal("company_not_selected", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));              // and the selection was undone

        // a stale selection (made before the policy existed) is refused per request too, while the open routes keep working
        await SelectCompanyForSessions(plain.Id, company);
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

        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(adminToken, new { policy = "roles", roleIds = new[] { role } })).Status);
        var holderToken = await PasswordLogin(holder);
        var freeToken = await PasswordLogin(free);
        Assert.Equal(HttpStatusCode.Forbidden, await Select(holderToken, company));
        Assert.Equal(HttpStatusCode.OK, await Select(freeToken, company));                                           // not listed: pass-through

        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(adminToken, new { policy = "off" })).Status);
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

        Assert.Equal(HttpStatusCode.BadRequest, (await PutPolicy(token, new { policy = "sometimes" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutPolicy(token, new { policy = "roles" })).Status);   // roles kind needs roles

        Clock.Advance(TimeSpan.FromMinutes(16));                                                                      // the proof is older than the window
        var stale = await PutPolicy(token, new { policy = "all" });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.Status);
        Assert.Contains("mfa_step_up_required", stale.Body);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/access/mfa-policy", token)).Status);   // reading is not a fresh operation
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/step-up", token, new { code = Totp.Compute(secret, Step) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(token, new { policy = "all" })).Status);

        // an administrator without an authenticator cannot require MFA of themselves (409), whatever the freshness (pass-through gives them a fresh session)
        var plainToken = await PasswordLogin(plainAdmin);
        var off = await PutPolicy(token, new { policy = "off" });
        Assert.Equal(HttpStatusCode.OK, off.Status);
        Assert.Equal(HttpStatusCode.OK, await Select(plainToken, company));
        var lockout = await PutPolicy(plainToken, new { policy = "all" });
        Assert.Equal(HttpStatusCode.Conflict, lockout.Status);
        Assert.Contains("mfa_policy_requires_own_enrolment", lockout.Body);
    }

    // ---- optimistic concurrency of the contract (review M1 of PR 181)

    private static (string Error, long? Current) Conflict(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        return (root.GetProperty("error").GetString()!, root.TryGetProperty("currentRevision", out var c) ? c.GetInt64() : null);
    }

    [PostgresFact]
    public async Task The_policy_contract_carries_the_revision_as_ETag_and_requires_If_Match_428_without_412_when_stale_and_one_winner_of_two_concurrent_changes()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var (token, _) = await MfaLogin(admin);
        Assert.Equal(HttpStatusCode.OK, await Select(token, company));

        var first = await SendWithAsync(HttpMethod.Get, PolicyPath, token, null, null);
        Assert.Equal((HttpStatusCode.OK, "\"0\""), (first.Status, first.ETag));                                      // no stored policy: Off at revision 0
        var firstBody = JsonDocument.Parse(first.Body).RootElement;
        Assert.Equal(("off", 0L), (firstBody.GetProperty("policy").GetString(), firstBody.GetProperty("revision").GetInt64()));

        var missing = await SendWithAsync(HttpMethod.Put, PolicyPath, token, new { policy = "all" }, null);
        Assert.Equal((HttpStatusCode)428, missing.Status);
        Assert.Equal("precondition_required", Conflict(missing.Body).Error);
        var malformed = await SendWithAsync(HttpMethod.Put, PolicyPath, token, new { policy = "all" }, "\"abc\"");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Equal("invalid_if_match", Conflict(malformed.Body).Error);
        Assert.Equal("\"0\"", (await SendWithAsync(HttpMethod.Get, PolicyPath, token, null, null)).ETag);            // neither refusal wrote anything

        var created = await SendWithAsync(HttpMethod.Put, PolicyPath, token, new { policy = "all" }, "\"0\"");
        Assert.Equal((HttpStatusCode.OK, "\"1\""), (created.Status, created.ETag));
        Assert.Equal(1L, JsonDocument.Parse(created.Body).RootElement.GetProperty("revision").GetInt64());

        var stale = await SendWithAsync(HttpMethod.Put, PolicyPath, token, new { policy = "off" }, "\"0\"");          // a stale screen
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(("revision_conflict", 1L), Conflict(stale.Body));
        var afterStale = await SendWithAsync(HttpMethod.Get, PolicyPath, token, null, null);
        Assert.Equal("\"1\"", afterStale.ETag);
        Assert.Equal("all", JsonDocument.Parse(afterStale.Body).RootElement.GetProperty("policy").GetString());      // the stale change did not land

        // two concurrent changes with the SAME If-Match: exactly one wins, the other is told the new revision
        var race = await Task.WhenAll(
            SendWithAsync(HttpMethod.Put, PolicyPath, token, new { policy = "off" }, "\"1\""),
            SendWithAsync(HttpMethod.Put, PolicyPath, token, new { policy = "all" }, "\"1\""));
        var won = Assert.Single(race, r => r.Status == HttpStatusCode.OK);
        var lost = Assert.Single(race, r => r.Status == HttpStatusCode.PreconditionFailed);
        Assert.Equal("\"2\"", won.ETag);
        Assert.Equal(("revision_conflict", 2L), Conflict(lost.Body));
        var final = await SendWithAsync(HttpMethod.Get, PolicyPath, token, null, null);
        Assert.Equal("\"2\"", final.ETag);
        Assert.Equal(JsonDocument.Parse(won.Body).RootElement.GetProperty("policy").GetString(), JsonDocument.Parse(final.Body).RootElement.GetProperty("policy").GetString());
    }

    // ---- identity administration is not company-scoped (review L2 of PR 181)

    [PostgresFact]
    public async Task An_identity_administrator_without_an_authenticator_keeps_identity_administration_while_a_requiring_company_is_selected()
    {
        await StartApi();
        var company = await NewCompany();
        var enrolled = await NewAdmin(company, "enrolled");
        var root = await NewUser("rootadmin", admin: true);                                                           // identity administrator, MFA state None
        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(root.Login, company));
        var rootToken = await PasswordLogin(root);
        Assert.Equal(HttpStatusCode.OK, await Select(rootToken, company));                                           // selected while the policy is still Off

        var (adminToken, _) = await MfaLogin(enrolled);
        Assert.Equal(HttpStatusCode.OK, await Select(adminToken, company));
        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(adminToken, new { policy = "all" })).Status);

        // the requiring company is still the SELECTED company of the identity administrator's session: company routes are refused...
        Assert.Equal("mfa_enrolment_required", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", rootToken));
        Assert.Equal("mfa_enrolment_required", await ErrorOf(HttpMethod.Get, PolicyPath, rootToken));
        // ...but identity administration is not: creating a user, changing a state (the fresh operation; pass-through for state None, D-161) and the shared registry
        var create = await SendAsync(HttpMethod.Post, "/api/v1/admin/users", rootToken, new { login = "newhire", displayName = "New Hire", password = "New-Passphrase-Strong-3", isIdentityAdministrator = false });
        Assert.Equal(HttpStatusCode.Created, create.Status);
        var newId = JsonDocument.Parse(create.Body).RootElement.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{newId}/state", rootToken, new { state = "disabled" })).Status);
        var search = await SendAsync(HttpMethod.Get, "/api/v1/admin/parties/search?term=none", rootToken);
        Assert.Equal(HttpStatusCode.OK, search.Status);
        var proposals = await SendAsync(HttpMethod.Get, "/api/v1/admin/parties/proposals", rootToken);
        Assert.Equal(HttpStatusCode.OK, proposals.Status);

        // the exemption is identity administration only: a company administrator who is NOT an identity administrator is still refused there (403), and an enrolled identity administrator still needs the proof
        var plain = await NewAdmin(company, "plainadmin");
        await SelectCompanyForSessions(plain.Id, null);
        var plainToken = await PasswordLogin(plain);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/v1/admin/users", plainToken, new { login = "nope1", displayName = "No", password = "New-Passphrase-Strong-3" })).Status);
    }

    // ---- review of PR 181 by FG (H1, L1, L3, L4)

    /// <summary>A second company administrator of the company: holds every company permission and has NO authenticator.</summary>
    private Task<UserRecord> PasswordOnlyAdmin(Guid company, string login) => NewAdmin(company, login);

    private async Task<(Guid Company, string AdminToken)> CompanyRequiringMfaOfAll()
    {
        var company = await NewCompany();
        var admin = await NewAdmin(company, "owner" + Guid.NewGuid().ToString("N")[..6]);
        var (adminToken, _) = await MfaLogin(admin);
        Assert.Equal(HttpStatusCode.OK, await Select(adminToken, company));
        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(adminToken, new { policy = "all" })).Status);
        return (company, adminToken);
    }

    [PostgresFact]
    public async Task Racing_company_selection_never_serves_a_requiring_company_to_a_password_only_member_who_holds_permissions()
    {
        await StartApi();
        var (company, _) = await CompanyRequiringMfaOfAll();
        var racer = await PasswordOnlyAdmin(company, "racer");                                                         // HOLDS the permissions: only the MFA policy stands between this session and the company
        var token = await PasswordLogin(racer);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selections = new System.Collections.Concurrent.ConcurrentBag<HttpStatusCode>();
        var reads = new System.Collections.Concurrent.ConcurrentBag<(string Path, HttpStatusCode Status, string Body)>();
        async Task SelectLoop() { while (!stop.IsCancellationRequested) selections.Add(await Select(token, company)); }
        async Task ReadLoop(string path) { while (!stop.IsCancellationRequested) { var r = await SendAsync(HttpMethod.Get, path, token); reads.Add((path, r.Status, r.Body)); } }
        await Task.WhenAll(
            Task.Run(SelectLoop), Task.Run(SelectLoop),
            Task.Run(() => ReadLoop("/api/v1/access/me")), Task.Run(() => ReadLoop("/api/v1/access/me")), Task.Run(() => ReadLoop("/api/v1/access/me")),
            Task.Run(() => ReadLoop("/api/v1/access/roles")), Task.Run(() => ReadLoop("/api/v1/access/roles")), Task.Run(() => ReadLoop("/api/v1/company/branches")));

        Assert.True(selections.Count >= 20 && reads.Count >= 60, $"too few requests to mean anything: {selections.Count} selections, {reads.Count} reads");
        Assert.All(selections, st => Assert.Equal(HttpStatusCode.Forbidden, st));
        Assert.DoesNotContain(reads, r => r.Status == HttpStatusCode.OK);                                              // not once: no permissions, no roles, no company data
        Assert.DoesNotContain(reads, r => r.Body.Contains(company.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.All(reads.Where(r => r.Path.StartsWith("/api/v1/access/")), r => Assert.Contains(r.Body, new[] { "{\"error\":\"company_not_selected\"}" }));   // the selection was never written, so there is nothing else to answer
        Assert.Null(await Scalar<object?>($"SELECT (SELECT {S.SelectedCompanyId} FROM {S.Table} WHERE {S.UserId} = @u LIMIT 1)", ("u", racer.Id)) as Guid?);
    }

    [PostgresFact]
    public async Task A_request_authorized_for_one_company_is_never_served_for_a_company_selected_after_authorization()
    {
        Guid target = Guid.Empty; var armed = 0;
        AccessService? raw = null;
        Guid sessionId = Guid.Empty;
        // between the policy decision and the endpoint, "another request" binds the session to the requiring company (written directly: whatever writer does it, the endpoint must not serve it)
        wrapEvaluator = inner => new HookedEvaluator(inner, async (user, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1) Assert.True(await raw!.SelectCompanyAsync(sessionId, user, target));
        });
        await StartApi();
        raw = Access;
        var (requiring, _) = await CompanyRequiringMfaOfAll();
        var open = await NewCompany();
        var openAdmin = await NewAdmin(open, "openadmin");
        var racer = await PasswordOnlyAdmin(requiring, "racer");
        Ok(await Access.GrantMembershipAsync(openAdmin.Id, open, racer.Login));
        Ok(await Access.AssignRoleAsync(openAdmin.Id, open, racer.Id, PresetRoles.CompanyAdministratorId, ScopeKind.Company, null));
        var token = await PasswordLogin(racer);
        sessionId = await Scalar<Guid>($"SELECT {S.Id} FROM {S.Table} WHERE {S.UserId} = @u", ("u", racer.Id));
        Assert.Equal(HttpStatusCode.OK, await Select(token, open));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/access/me", token)).Status);

        target = requiring; armed = 1;
        var raced = await SendAsync(HttpMethod.Get, "/api/v1/access/me", token);
        Assert.Equal(HttpStatusCode.Conflict, raced.Status);                                                           // judged for the open company, not served for the requiring one
        Assert.Equal("{\"error\":\"company_selection_changed\"}", raced.Body);
        Assert.Equal(requiring, await Scalar<Guid>($"SELECT {S.SelectedCompanyId} FROM {S.Table} WHERE {S.Id} = @s", ("s", sessionId)));
        Assert.Equal("mfa_enrolment_required", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));            // the repeated request is judged for the company the session now has

        // the same with no company selected at authorization
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/v1/session/company", token)).Status);
        // (no company: the handler asks no policy question, so the seam cannot act; the binding itself is asserted through the database instead)
        await SelectCompanyForSessions(racer.Id, null);
        Assert.Equal("company_not_selected", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));
    }

    [PostgresFact]
    public async Task A_refused_selection_keeps_the_previous_company_and_a_policy_that_begins_to_require_MFA_refuses_the_next_request_of_a_session_selected_before()
    {
        await StartApi();
        var (requiring, _) = await CompanyRequiringMfaOfAll();
        var open = await NewCompany();
        var openAdmin = await NewAdmin(open, "openadmin");
        var (openAdminToken, _) = await MfaLogin(openAdmin);
        var member = await PasswordOnlyAdmin(requiring, "member");
        Ok(await Access.GrantMembershipAsync(openAdmin.Id, open, member.Login));
        Ok(await Access.AssignRoleAsync(openAdmin.Id, open, member.Id, PresetRoles.CompanyAdministratorId, ScopeKind.Company, null));
        var token = await PasswordLogin(member);

        Assert.Equal(HttpStatusCode.OK, await Select(token, open));
        var refused = await SendAsync(HttpMethod.Post, "/api/v1/session/company", token, new { companyId = requiring });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("mfa_enrolment_required", refused.Body);
        var me = await SendAsync(HttpMethod.Get, "/api/v1/access/me", token);
        Assert.Equal(HttpStatusCode.OK, me.Status);                                                                    // still in the company selected before (review L4): nothing was written and undone
        Assert.Contains(open.ToString(), me.Body);
        Assert.DoesNotContain(requiring.ToString(), me.Body);

        // the open company begins to require MFA while the member has it selected: the very next request is refused, and so is selecting it again
        Assert.Equal(HttpStatusCode.OK, await Select(openAdminToken, open));
        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(openAdminToken, new { policy = "all" })).Status);
        Assert.Equal("mfa_enrolment_required", await ErrorOf(HttpMethod.Get, "/api/v1/access/me", token));
        Assert.Equal("mfa_enrolment_required", await ErrorOf(HttpMethod.Get, "/api/v1/access/roles", token));
        Assert.Equal(HttpStatusCode.Forbidden, await Select(token, open));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/companies", token)).Status);         // and the member can still list and leave
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/v1/session/company", token)).Status);
    }

    [PostgresFact]
    public async Task A_password_only_session_of_a_user_who_enrolled_afterwards_is_no_session_at_all_so_it_is_never_served_and_never_told_to_enrol()
    {
        // review L1 of PR 181, "can such a session exist?": no. AuthService.AuthenticateAsync does not accept a session without the proof once its user is not MFA-free, so the request is
        // unauthenticated (401, no body) before any policy is asked. The 401 mfa_step_up_required answers of the selection and of the company-policy refusal are defence in depth (unit-tested).
        await StartApi();
        var (company, _) = await CompanyRequiringMfaOfAll();
        var late = await PasswordOnlyAdmin(company, "late");
        var token = await PasswordLogin(late);                                                                         // a password-only session, issued before the user had an authenticator
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/auth/status", token)).Status);
        var mfa = factory!.Services.GetRequiredService<MfaStore>();
        Assert.True(await mfa.ActivatePendingAsync(late.Id, await mfa.CreatePendingAsync(late.Id, Totp.GenerateSecret(), null), Step - 10, null));   // enrolled elsewhere; THIS session keeps no proof
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL", ("u", late.Id)));
        await SelectCompanyForSessions(late.Id, company);   // even with a selection from before the policy

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[] { (HttpMethod.Post, "/api/v1/session/company", new { companyId = company }), (HttpMethod.Get, "/api/v1/access/me", null), (HttpMethod.Get, "/api/v1/company/branches", null) })
        {
            var r = await SendAsync(method, path, token, body);
            Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
            Assert.DoesNotContain("mfa_enrolment_required", r.Body);
            Assert.DoesNotContain(company.ToString(), r.Body);
        }
    }

    [PostgresFact]
    public async Task Changing_or_switching_off_the_policy_needs_an_authenticator_of_the_person_doing_it_whether_or_not_the_policy_covers_them()
    {
        await StartApi();
        var company = await NewCompany();
        var enrolled = await NewAdmin(company, "enrolled");
        var (enrolledToken, _) = await MfaLogin(enrolled);
        var passwordOnly = await PasswordOnlyAdmin(company, "pwonly");
        var auditor = await NewMember(company, enrolled, "auditor");
        Ok(await Access.AssignRoleAsync(enrolled.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var token = await PasswordLogin(passwordOnly);
        Assert.Equal(HttpStatusCode.OK, await Select(token, company));

        // a roles policy that does NOT cover the password-only administrator, then switching a policy off: both refused, nothing written
        foreach (var body in new object[] { new { policy = "roles", roleIds = new[] { PresetRoles.AccessAuditorId } }, new { policy = "off" }, new { policy = "all" } })
        {
            var r = await PutPolicy(token, body);
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("{\"error\":\"mfa_policy_requires_own_enrolment\"}", r.Body);
        }
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaPolicy.Table}"));

        Assert.Equal(HttpStatusCode.OK, await Select(enrolledToken, company));
        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(enrolledToken, new { policy = "roles", roleIds = new[] { PresetRoles.AccessAuditorId } })).Status);
        var off = await PutPolicy(token, new { policy = "off" });                                                      // not covered by the policy, still may not switch it off with a password-only session
        Assert.Equal(HttpStatusCode.Conflict, off.Status);
        Assert.Equal(HttpStatusCode.OK, (await PutPolicy(enrolledToken, new { policy = "off" })).Status);
    }
}
