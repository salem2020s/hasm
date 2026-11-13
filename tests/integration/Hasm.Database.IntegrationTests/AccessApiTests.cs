using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Codes = Hasm.Infrastructure.Access.PermissionCatalog.Codes;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests of company access and RBAC against a real disposable PostgreSQL system database.
/// Users are seeded through the same services the administrative tool uses (there is no public registration and no
/// public administration endpoint); everything else goes through the real API pipeline.
/// </summary>
public class AccessApiTests : AccessTestBase
{
    private const string CookieName = "__Host-hasm_session";
    private const string RootPassword = "Root-Passphrase-Strong-1";

    private readonly LogCapture logs = new();
    private readonly List<string> bodies = [];
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
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton(_ => Audit);                 // the suite's audit trail (shared keys and checkpoints) so tests can verify what the API wrote
                s.AddSingleton<IStartupFilter>(new RemoteIpFilter());
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        await base.DisposeAsync();
    }

    private async Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, string? cookie = null, string? bearer = null, bool header = true, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is string raw) request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body);
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        foreach (var (k, v) in headers ?? []) request.Headers.Add(k, v);
        request.Headers.Add("X-Test-Ip", "10.0.0.1");
        using var response = await client.SendAsync(request);
        var result = new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
        lock (bodies) bodies.Add(result.Body);
        return result;
    }

    private async Task<string> SignIn(UserRecord user)
    {
        var r = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = user.Login, password = Pw });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.SetCookies.Single(c => c.StartsWith(CookieName + "=")).Split(';')[0][(CookieName.Length + 1)..];
    }

    private async Task<string> SignInAndSelect(UserRecord user, Guid company)
    {
        var cookie = await SignIn(user);
        var r = await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return cookie;
    }

    private async Task GrantRole(string adminCookie, UserRecord user, Guid roleId, string scope = "company", Guid? scopeId = null)
    {
        var r = await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = user.Id, roleId, scope, scopeId }, adminCookie);
        Assert.Equal(HttpStatusCode.Created, r.Status);
    }

    private Task<ApiResponse> Get(string path, string cookie) => Send(HttpMethod.Get, path, cookie: cookie);

    // ---------------------------------------------------------------- authentication and company selection

    [PostgresFact]
    public async Task Every_access_endpoint_requires_authentication()
    {
        await StartApi();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/v1/companies"), (HttpMethod.Post, "/api/v1/session/company"), (HttpMethod.Delete, "/api/v1/session/company"),
            (HttpMethod.Get, "/api/v1/access/me"), (HttpMethod.Get, "/api/v1/access/permissions"),
            (HttpMethod.Get, "/api/v1/access/roles"), (HttpMethod.Post, "/api/v1/access/roles"),
            (HttpMethod.Put, $"/api/v1/access/roles/{id}/permissions"), (HttpMethod.Post, $"/api/v1/access/roles/{id}/deactivate"),
            (HttpMethod.Get, "/api/v1/access/members"), (HttpMethod.Post, "/api/v1/access/members"), (HttpMethod.Delete, $"/api/v1/access/members/{id}"),
            (HttpMethod.Get, "/api/v1/access/assignments"), (HttpMethod.Post, "/api/v1/access/assignments"), (HttpMethod.Delete, $"/api/v1/access/assignments/{id}"),
            (HttpMethod.Get, "/api/v1/access/audit"),
        };
        foreach (var (method, path) in routes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { }, cookie: "forged-token")).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { }, bearer: "forged-token")).Status);
        }
    }

    [PostgresFact]
    public async Task Authenticated_users_without_membership_cannot_select_or_reach_any_company()
    {
        await StartApi();
        var company = await NewCompany();
        await NewAdmin(company);
        var user = await NewPerson("plain");
        var cookie = await SignIn(user);

        Assert.Equal(0, (await Get("/api/v1/companies", cookie)).Json.GetProperty("companies").GetArrayLength());
        foreach (var path in new[] { "/api/v1/access/me", "/api/v1/access/roles", "/api/v1/access/members", "/api/v1/access/audit", "/api/v1/access/assignments", "/api/v1/access/permissions" })
        {
            var r = await Get(path, cookie);
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("company_not_selected", r.Error);
        }
        var real = await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, cookie);
        var unknown = await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = Guid.NewGuid() }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, real.Status);
        Assert.Equal(real.Status, unknown.Status);                                  // existing and unknown companies are indistinguishable
        Assert.Equal(real.Body, unknown.Body);
        foreach (var bad in new object?[] { new { }, new { companyId = Guid.Empty }, "not json", "{\"companyId\":\"nope\"}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/session/company", bad, cookie)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Get("/api/v1/access/me", cookie)).Status);   // still nothing selected
    }

    [PostgresFact]
    public async Task A_member_selects_a_company_and_sees_exactly_its_own_permissions_and_never_physical_identifiers()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var auditor = await NewMember(company, admin, "auditor");
        Ok(await Access.AssignRoleAsync(admin.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var physical = await Scalar<string>($"SELECT {Co.PhysicalDatabaseName} FROM {Co.Table} WHERE {Co.Id}=@c", ("c", company));

        var cookie = await SignIn(auditor);
        var companies = await Get("/api/v1/companies", cookie);
        Assert.Equal(company, companies.Json.GetProperty("companies")[0].GetProperty("id").GetGuid());
        Assert.Equal(["id", "nameAr", "nameEn"], companies.Json.GetProperty("companies")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, cookie)).Status);

        var me = await Get("/api/v1/access/me", cookie);
        Assert.Equal(company, me.Json.GetProperty("companyId").GetGuid());
        var grant = me.Json.GetProperty("grants").EnumerateArray().Single();
        Assert.Equal("company", grant.GetProperty("scope").GetString());
        Assert.Equal(PresetRoles.Definitions[PresetRoles.AccessAuditorId].Permissions.Order(), grant.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/audit", cookie)).Status);

        await Get("/api/v1/access/members", cookie);
        await Get("/api/v1/access/roles", cookie);
        var all = string.Join('\n', bodies);
        Assert.DoesNotContain(physical, all);                                        // no physical database name, ever
        Assert.DoesNotContain("Host=", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join('\n', logs.Entries), physical);
    }

    [PostgresFact]
    public async Task Desktop_bearer_sessions_use_the_same_company_context()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var login = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = admin.Login, password = Pw, client = "desktop" });
        var token = login.Json.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Get, "/api/v1/access/me", bearer: token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, bearer: token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/access/members", bearer: token)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, "/api/v1/session/company", bearer: token)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Get, "/api/v1/access/members", bearer: token)).Status);
    }

    [PostgresFact]
    public async Task Switching_companies_switches_the_authorization_context_completely()
    {
        await StartApi();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");
        var multi = await NewPerson("multi");
        Ok(await Access.GrantMembershipAsync(adminA.Id, a, multi.Login));
        Ok(await Access.GrantMembershipAsync(adminB.Id, b, multi.Login));
        Ok(await Access.AssignRoleAsync(adminA.Id, a, multi.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));   // auditor in A only
        Ok(await Access.CreateRoleAsync(adminA.Id, a, "Only in A", []));
        var cookie = await SignIn(multi);

        Assert.Equal(2, (await Get("/api/v1/companies", cookie)).Json.GetProperty("companies").GetArrayLength());
        await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = a }, cookie);
        var rolesInA = await Get("/api/v1/access/roles", cookie);
        Assert.Equal(HttpStatusCode.OK, rolesInA.Status);
        Assert.Contains("Only in A", rolesInA.Body);

        await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = b }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/roles", cookie)).Status);   // no role in B
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/audit", cookie)).Status);
        Assert.Empty((await Get("/api/v1/access/me", cookie)).Json.GetProperty("grants").EnumerateArray());
    }

    // ---------------------------------------------------------------- management and enforcement

    [PostgresFact]
    public async Task Role_and_membership_management_works_end_to_end_over_http()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var newcomer = await NewPerson("newcomer");
        var cookie = await SignInAndSelect(admin, company);

        var created = await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = "Audit reader", permissions = new[] { Codes.AuditView, Codes.MembersView } }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var roleId = created.Json.GetProperty("id").GetGuid();
        Assert.False(created.Json.GetProperty("preset").GetBoolean());

        var member = await Send(HttpMethod.Post, "/api/v1/access/members", new { login = newcomer.Login }, cookie);
        Assert.Equal(HttpStatusCode.Created, member.Status);
        await GrantRole(cookie, newcomer, roleId);

        var newcomerCookie = await SignInAndSelect(newcomer, company);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/audit", newcomerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/roles", newcomerCookie)).Status);   // role lacks roles.view

        var updated = await Send(HttpMethod.Put, $"/api/v1/access/roles/{roleId}/permissions", new { permissions = new[] { Codes.MembersView, Codes.RolesView } }, cookie);
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/audit", newcomerCookie)).Status);   // permission change effective immediately
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/roles", newcomerCookie)).Status);

        var assignments = await Get($"/api/v1/access/assignments?userId={newcomer.Id}", cookie);
        var assignmentId = assignments.Json.GetProperty("assignments")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/v1/access/assignments/{assignmentId}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/roles", newcomerCookie)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/access/roles/{roleId}/deactivate", cookie: cookie)).Status);
        var members = await Get("/api/v1/access/members", cookie);
        Assert.Contains(newcomer.Login, members.Body);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/v1/access/members/{newcomer.Id}", cookie: cookie)).Status);
        Assert.DoesNotContain(newcomer.Login, (await Get("/api/v1/access/members", cookie)).Body);

        var audit = await Get("/api/v1/access/audit", cookie);
        var events = audit.Json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList();
        foreach (var expected in new[] { "RoleCreated", "MembershipGranted", "RoleAssigned", "RolePermissionsChanged", "RoleAssignmentRevoked", "RoleDeactivated", "MembershipRevoked" })
            Assert.Contains(expected, events);
        var catalog = await Get("/api/v1/access/permissions", cookie);
        Assert.Contains(Codes.RolesUpdate.Replace(".update", ""), catalog.Body);
    }

    [PostgresFact]
    public async Task Users_without_the_permission_are_refused_by_every_management_endpoint()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var auditor = await NewMember(company, admin, "auditor");
        Ok(await Access.AssignRoleAsync(admin.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var target = await NewMember(company, admin, "target");
        var role = Ok(await Access.CreateRoleAsync(admin.Id, company, "R", []));
        var assignment = Ok(await Access.AssignRoleAsync(admin.Id, company, target.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var cookie = await SignInAndSelect(auditor, company);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = "x", permissions = Array.Empty<string>() }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"/api/v1/access/roles/{role.Id}/permissions", new { permissions = new[] { Codes.AuditView } }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/access/roles/{role.Id}/deactivate", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/access/members", new { login = admin.Login }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/api/v1/access/members/{target.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = target.Id, roleId = role.Id, scope = "company" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/api/v1/access/assignments/{assignment.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/members", cookie)).Status);      // what the role does grant still works

        // nothing changed
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.Role.Table} WHERE {SystemSchema.Role.CompanyId}=@c", ("c", company)));
        Assert.True(await Allowed(target, company, Codes.AuditView));
    }

    [PostgresFact]
    public async Task Privilege_escalation_attempts_over_http_are_refused()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var manager = await NewMember(company, admin, "manager");
        Ok(await Access.AssignRoleAsync(admin.Id, company, manager.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));
        var victim = await NewMember(company, admin, "victim");
        var cookie = await SignInAndSelect(manager, company);

        var toAdmin = await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = victim.Id, roleId = PresetRoles.CompanyAdministratorId, scope = "company" }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, toAdmin.Status);
        Assert.Equal("cannot_grant_permission", toAdmin.Error);
        var self = await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = manager.Id, roleId = PresetRoles.AccessAuditorId, scope = "company" }, cookie);
        Assert.Equal("cannot_modify_self", self.Error);
        Assert.Equal("preset_role_immutable", (await Send(HttpMethod.Put, $"/api/v1/access/roles/{PresetRoles.AccessManagerId}/permissions", new { permissions = new[] { Codes.RolesCreate } }, await SignInAndSelect(admin, company))).Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/api/v1/access/members/{admin.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/api/v1/access/members/{manager.Id}", cookie: cookie)).Status);

        Assert.Empty((await Access.GetEffectivePermissionsAsync(victim.Id, company)).Grants);
        Assert.True(await Allowed(admin, company, Codes.RolesCreate));
    }

    [PostgresFact]
    public async Task Company_ids_in_the_request_are_ignored_and_other_companies_objects_are_not_found()
    {
        await StartApi();
        var a = await NewCompany();
        var b = await NewCompany();
        var adminA = await NewAdmin(a, "adminA");
        var adminB = await NewAdmin(b, "adminB");
        var roleB = Ok(await Access.CreateRoleAsync(adminB.Id, b, "Secret B role", [Codes.AuditView]));
        var userB = await NewMember(b, adminB, "userB");
        var assignmentB = Ok(await Access.AssignRoleAsync(adminB.Id, b, userB.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var userA = await NewMember(a, adminA, "userA");
        var cookie = await SignInAndSelect(adminA, a);

        // company claims in headers, query string and body never change the company the server uses
        var claims = new Dictionary<string, string> { ["X-Company-Id"] = b.ToString(), ["X-Hasm-Company"] = b.ToString(), ["Company"] = b.ToString() };
        var roles = await Send(HttpMethod.Get, $"/api/v1/access/roles?companyId={b}&company={b}", cookie: cookie, headers: claims);
        Assert.DoesNotContain("Secret B role", roles.Body);
        var members = await Send(HttpMethod.Get, $"/api/v1/access/members?companyId={b}", cookie: cookie, headers: claims);
        Assert.DoesNotContain(adminB.Login, members.Body);
        Assert.DoesNotContain(userB.Login, members.Body);
        var created = await Send(HttpMethod.Post, $"/api/v1/access/roles?companyId={b}", new { name = "Created in A", permissions = Array.Empty<string>(), companyId = b }, cookie, headers: claims);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Contains("Created in A", Ok(await Access.ListRolesAsync(adminA.Id, a)).Select(r => r.Name));
        Assert.DoesNotContain("Created in A", Ok(await Access.ListRolesAsync(adminB.Id, b)).Select(r => r.Name));

        // identifiers from company B used while working in A: not found, never forbidden/200 (no IDOR, no existence oracle)
        Assert.Equal("role_not_found", (await Send(HttpMethod.Put, $"/api/v1/access/roles/{roleB.Id}/permissions", new { permissions = Array.Empty<string>() }, cookie)).Error);
        Assert.Equal("role_not_found", (await Send(HttpMethod.Post, $"/api/v1/access/roles/{roleB.Id}/deactivate", cookie: cookie)).Error);
        Assert.Equal("role_not_found", (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = userA.Id, roleId = roleB.Id, scope = "company" }, cookie)).Error);
        Assert.Equal("assignment_not_found", (await Send(HttpMethod.Delete, $"/api/v1/access/assignments/{assignmentB.Id}", cookie: cookie)).Error);
        Assert.Equal("member_not_found", (await Send(HttpMethod.Delete, $"/api/v1/access/members/{userB.Id}", cookie: cookie)).Error);
        Assert.Equal("member_not_found", (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = userB.Id, roleId = PresetRoles.AccessAuditorId, scope = "company" }, cookie)).Error);
        Assert.Empty((await Get($"/api/v1/access/assignments?userId={userB.Id}", cookie)).Json.GetProperty("assignments").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = b }, cookie)).Status);
        // B is untouched
        Assert.True(await Allowed(userB, b, Codes.AuditView));
        Assert.Equal([Codes.AuditView], Ok(await Access.ListRolesAsync(adminB.Id, b)).Single(r => r.Id == roleB.Id).Permissions);
    }

    [PostgresFact]
    public async Task Identity_administrators_get_no_company_access_and_there_is_no_public_administration_endpoint()
    {
        await StartApi();
        var company = await NewCompany();
        await NewAdmin(company);
        var root = await NewUser("rootadmin", Pw, admin: true);
        var cookie = await SignIn(root);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, cookie)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Get("/api/v1/access/members", cookie)).Status);

        foreach (var path in new[]
                 {
                     "/api/v1/access/bootstrap", "/api/v1/access/administrators", $"/api/v1/admin/companies/{company}/administrators", $"/api/v1/admin/companies/{company}/members",
                     "/api/v1/admin/company-access", "/api/v1/admin/access", $"/api/v1/companies/{company}/members", $"/api/v1/companies/{company}/roles",
                 })
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put })
            {
                var r = await Send(method, path, method == HttpMethod.Get ? null : new { login = root.Login, userId = root.Id, companyId = company }, cookie);
                Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path} -> {r.Status}");
            }
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.UserCompanyAccess.Table} WHERE {SystemSchema.UserCompanyAccess.UserId}=@u", ("u", root.Id)));
    }

    // ---------------------------------------------------------------- revocation during an active session

    [PostgresFact]
    public async Task Revocations_take_effect_on_the_very_next_request_of_the_same_session()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin, "worker");
        var adminCookie = await SignInAndSelect(admin, company);
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var cookie = await SignInAndSelect(user, company);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/audit", cookie)).Status);

        // 1. assignment revoked
        var assignmentId = Ok(await Access.ListAssignmentsAsync(admin.Id, company, user.Id)).Single().Id;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/v1/access/assignments/{assignmentId}", cookie: adminCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/audit", cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/me", cookie)).Status);              // still authenticated, merely unauthorized

        // 2. role deactivated
        var custom = Ok(await Access.CreateRoleAsync(admin.Id, company, "Temp", [Codes.AuditView]));
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, custom.Id, ScopeKind.Company, null));
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/audit", cookie)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/access/roles/{custom.Id}/deactivate", cookie: adminCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/v1/access/audit", cookie)).Status);

        // 3. membership revoked: the company selection itself disappears and cannot be re-established
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/audit", cookie)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/v1/access/members/{user.Id}", cookie: adminCookie)).Status);
        var after = await Get("/api/v1/access/audit", cookie);
        Assert.Equal(HttpStatusCode.Conflict, after.Status);
        Assert.Equal("company_not_selected", after.Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, cookie)).Status);
        Assert.Equal(0, (await Get("/api/v1/companies", cookie)).Json.GetProperty("companies").GetArrayLength());
    }

    [PostgresFact]
    public async Task Disabling_a_user_ends_the_session_and_all_access_at_once()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var user = await NewMember(company, admin, "worker");
        Ok(await Access.AssignRoleAsync(admin.Id, company, user.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var cookie = await SignInAndSelect(user, company);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/audit", cookie)).Status);

        await Auth.SetUserStateAsync(admin.Id, user.Id, UserState.Disabled);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/access/audit", cookie)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/access/me", cookie)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = user.Login, password = Pw })).Status);
    }

    [PostgresFact]
    public async Task A_revoked_administrator_cannot_continue_a_management_session()
    {
        await StartApi();
        var company = await NewCompany();
        var first = await NewAdmin(company, "first");
        var second = await NewAdmin(company, "second");
        var secondCookie = await SignInAndSelect(second, company);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/access/members", secondCookie)).Status);

        Ok(await Access.RevokeMembershipAsync(first.Id, company, second.Id));

        Assert.Equal(HttpStatusCode.Conflict, (await Get("/api/v1/access/members", secondCookie)).Status);
        var attempt = await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = "late", permissions = Array.Empty<string>() }, secondCookie);
        Assert.Equal(HttpStatusCode.Conflict, attempt.Status);
        Assert.DoesNotContain("late", Ok(await Access.ListRolesAsync(first.Id, company)).Select(r => r.Name));
    }

    // ---------------------------------------------------------------- input and transport hardening

    [PostgresFact]
    public async Task Malformed_and_hostile_input_is_rejected_with_generic_errors()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var cookie = await SignInAndSelect(admin, company);
        var member = await NewMember(company, admin);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/roles", "{ not json", cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = "x", permissions = new[] { "drop.table.users" } }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = "'; DROP TABLE x;--", permissions = new[] { "'; --" } }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = member.Id, roleId = PresetRoles.AccessAuditorId, scope = "galaxy" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = member.Id, roleId = PresetRoles.AccessAuditorId, scope = "branch" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { roleId = PresetRoles.AccessAuditorId, scope = "company" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, "/api/v1/access/assignments/not-a-guid", cookie: cookie)).Status);
        // (the Kestrel request-size limit is not enforced by the in-memory test server; the service-level length rule still rejects it)
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = new string('x', 40_000), permissions = Array.Empty<string>() }, cookie)).Status);
        var unknownMember = await Send(HttpMethod.Post, "/api/v1/access/members", new { login = "missing.person" }, cookie);
        Assert.Equal("user_not_found", unknownMember.Error);

        var text = string.Join('\n', bodies);
        Assert.DoesNotContain("Npgsql", text);
        Assert.DoesNotContain("   at ", text);
        Assert.DoesNotContain("SELECT", text);
        Assert.Equal(4L + 0, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.Role.Table}"));    // no role was created by any hostile request
    }

    [PostgresFact]
    public async Task State_changing_access_requests_need_the_csrf_header_and_responses_are_not_cached()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var cookie = await SignInAndSelect(admin, company);

        var noHeader = await Send(HttpMethod.Post, "/api/v1/access/roles", new { name = "csrf", permissions = Array.Empty<string>() }, cookie, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Equal("missing_request_header", noHeader.Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = company }, cookie, header: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, "/api/v1/access/members/" + Guid.NewGuid(), cookie: cookie, header: false)).Status);
        Assert.DoesNotContain("csrf", Ok(await Access.ListRolesAsync(admin.Id, company)).Select(r => r.Name));

        foreach (var path in new[] { "/api/v1/access/me", "/api/v1/access/roles", "/api/v1/access/members", "/api/v1/companies", "/api/v1/access/audit" })
        {
            var r = await Get(path, cookie);
            Assert.Contains("no-store", r.Headers.CacheControl?.ToString() ?? "");
        }
    }

    [PostgresFact]
    public async Task Access_logging_never_contains_tokens_or_credentials()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var cookie = await SignInAndSelect(admin, company);
        await Get("/api/v1/access/members", cookie);
        await Send(HttpMethod.Post, "/api/v1/access/members", new { login = "nobody.here" }, cookie);
        await Send(HttpMethod.Post, "/api/v1/access/roles", "{ broken", cookie);

        var text = string.Join('\n', logs.Entries);
        Assert.DoesNotContain(cookie, text);
        Assert.DoesNotContain(Pw, text);
        Assert.DoesNotContain(Options.Password ?? "never-set", text);
    }

    [PostgresFact]
    public async Task Unauthorized_callers_cannot_tell_existing_from_unknown_logins_or_user_ids_over_http()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var roleless = await NewMember(company, admin, "roleless");
        var auditor = await NewMember(company, admin, "auditor");
        Ok(await Access.AssignRoleAsync(admin.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var disabled = await NewPerson("disabled");
        await Auth.SetUserStateAsync(admin.Id, disabled.Id, UserState.Disabled);
        var logins = new[] { admin.Login, roleless.Login, disabled.Login, "ghost.user", "bad login!" };
        var ids = new[] { admin.Id, roleless.Id, disabled.Id, Guid.NewGuid() };

        foreach (var actor in new[] { roleless, auditor })
        {
            var cookie = await SignInAndSelect(actor, company);
            var answers = new List<(HttpStatusCode, string)>();
            foreach (var login in logins)
            {
                var r = await Send(HttpMethod.Post, "/api/v1/access/members", new { login }, cookie);
                answers.Add((r.Status, r.Body));
                Assert.DoesNotContain(login, r.Body);
            }
            foreach (var id in ids)
            {
                var r1 = await Send(HttpMethod.Delete, $"/api/v1/access/members/{id}", cookie: cookie);
                var r2 = await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = id, roleId = PresetRoles.AccessAuditorId, scope = "company" }, cookie);
                answers.Add((r1.Status, r1.Body));
                answers.Add((r2.Status, r2.Body));
            }
            Assert.Single(answers.Distinct());                         // one status and one body for everything
            Assert.Equal(HttpStatusCode.Forbidden, answers[0].Item1);
        }

        // a caller with no membership at all is stopped even earlier, again identically
        var outsider = await NewPerson("outsider");
        var oc = await SignIn(outsider);
        var outsiderAnswers = new HashSet<string>();
        foreach (var login in logins)
        {
            var r = await Send(HttpMethod.Post, "/api/v1/access/members", new { login }, oc);
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            outsiderAnswers.Add(r.Body);
        }
        Assert.Single(outsiderAnswers);

        // legitimate administrative workflow keeps working
        var adminCookie = await SignInAndSelect(admin, company);
        var newcomer = await NewPerson("newcomer");
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/api/v1/access/members", new { login = newcomer.Login }, adminCookie)).Status);
        Assert.Equal("user_not_found", (await Send(HttpMethod.Post, "/api/v1/access/members", new { login = disabled.Login }, adminCookie)).Error);
        Assert.Equal("user_not_found", (await Send(HttpMethod.Post, "/api/v1/access/members", new { login = "ghost.user" }, adminCookie)).Error);
    }

    [PostgresFact]
    public async Task Branch_and_warehouse_scoped_assignments_are_refused_until_scope_targets_can_be_verified()
    {
        await StartApi();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var member = await NewMember(company, admin);
        var cookie = await SignInAndSelect(admin, company);

        foreach (var (scope, id) in new[] { ("branch", BranchA), ("warehouse", WarehouseX), ("branch", Guid.NewGuid()) })
        {
            var r = await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = member.Id, roleId = PresetRoles.AccessAuditorId, scope, scopeId = id }, cookie);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("scope_not_verifiable", r.Error);
        }
        Assert.Empty((await Access.GetEffectivePermissionsAsync(member.Id, company)).Grants);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/api/v1/access/assignments", new { userId = member.Id, roleId = PresetRoles.AccessAuditorId, scope = "company" }, cookie)).Status);
    }
}
