using System.Net;
using System.Net.Http.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Asg = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;

namespace Hasm.Database.IntegrationTests;

/// <summary>Branch-scoped RBAC, activation epochs and the authorized facade (system migration 0007, Phase 3.1B Checkpoint C).</summary>
public abstract class BranchAccessTestBase : BranchTestBase
{
    protected AccessService Rbac { get; private set; } = null!;
    protected BranchManagementService Mgmt { get; private set; } = null!;
    protected CompanyBranchService Domain { get; private set; } = null!;
    protected CompanyDatabaseResolver Resolver { get; private set; } = null!;
    private int people;

    protected async Task PrepareRbac(IEntitlementPolicy? policy = null)
    {
        await Prepare();
        Resolver = SinglePrincipalResolver(Company);
        Rbac = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, PermissionCatalog.Default, Audit, new CompanyBranchScopeValidator(() => Resolver));
        Domain = new CompanyBranchService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), policy ?? new BranchLimitPolicy(50), Clock);
        Mgmt = new BranchManagementService(Rbac, Domain);
    }

    public override async Task DisposeAsync()
    {
        if (Rbac is not null) await Rbac.DisposeAsync();
        await base.DisposeAsync();
    }

    protected async Task<(UserRecord User, Guid RoleId)> Member(string[] permissions, ScopeKind scope, Guid? scopeId)
    {
        var user = await NewUser($"brmember{Interlocked.Increment(ref people)}");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        var role = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, $"brole{Interlocked.Increment(ref people)}", permissions);
        Assert.True(role.IsOk, role.Code);
        var assigned = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, user.Id, role.Value!.Id, scope, scopeId);
        Assert.True(assigned.IsOk, assigned.Code);
        return (user, role.Value.Id);
    }

    protected Task<AccessResult<AssignmentView>> Assign(UserRecord user, Guid roleId, Guid branchId) =>
        Rbac.AssignRoleAsync(Admin.Id, Company.Id, user.Id, roleId, ScopeKind.Branch, branchId);

    protected Task<long> RevokedAssignments() =>
        Scalar<long>($"SELECT count(*) FROM {Asg.Table} WHERE {Asg.RevokedAtUtc} IS NOT NULL AND {Asg.ScopeKind} = 1");

    protected static string[] BranchView_ => [PermissionCatalog.Codes.BranchesView];
    protected static string[] BranchEdit => [PermissionCatalog.Codes.BranchesView, PermissionCatalog.Codes.BranchesUpdate];
}

public class BranchAccessTests : BranchAccessTestBase
{
    [PostgresFact]
    public async Task The_company_administrator_preset_holds_the_branch_permissions_and_sees_the_main_branch()
    {
        await PrepareRbac();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "company.branches.view", "company.branches.update", "company.branchstructure.create", "company.branchstructure.activate", "company.branchstructure.designate" })
            Assert.Contains(c, admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.branch"));
        var list = Ok(await Mgmt.ListAsync(Admin.Id, Company.Id));
        Assert.Equal("MAIN", Assert.Single(list).Data.Code);
        var limits = Ok(await Mgmt.LimitsAsync(Admin.Id, Company.Id));
        Assert.Equal(new BranchLimits(50, 1), limits);
    }

    [PostgresFact]
    public async Task A_branch_assignment_is_verified_against_the_real_branches_of_this_company_and_records_the_epoch()
    {
        await PrepareRbac();
        var b = await Make("B1");
        var (user, roleId) = await Member(BranchView_, ScopeKind.Branch, b.Id);
        var assignment = Assert.Single((await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, user.Id)).Value!);
        Assert.Equal(ScopeKind.Branch, assignment.Scope);
        Assert.Equal(b.Id, assignment.ScopeId);
        Assert.Equal(1, assignment.ScopeEpoch);

        var other = await NewUser("other-branch-user");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, other.Login)).IsOk);
        Assert.Equal("scope_not_verifiable", (await Assign(other, roleId, Guid.NewGuid())).Code);                          // unknown branch
        var foreign = await ReadyCompany();
        var foreignMain = (await Branches(company: foreign).ListAsync(foreign.Id)).Single();
        Assert.Equal("scope_not_verifiable", (await Assign(other, roleId, foreignMain.Id)).Code);                           // a branch of ANOTHER company
        var inactive = await Make("OFF");
        Ok(await Domain.DeactivateAsync(Admin.Id, Company.Id, inactive.Id, 1));
        Assert.Equal("scope_not_verifiable", (await Assign(other, roleId, inactive.Id)).Code);                              // inactive branch
        Assert.Equal("invalid_scope", (await Rbac.AssignRoleAsync(Admin.Id, Company.Id, other.Id, roleId, ScopeKind.Branch, null)).Code);
        Assert.Equal("scope_not_verifiable", (await Rbac.AssignRoleAsync(Admin.Id, Company.Id, other.Id, roleId, ScopeKind.Warehouse, Guid.NewGuid())).Code);   // warehouses: Phase 3.1C
    }

    [PostgresFact]
    public async Task A_branch_scoped_user_reaches_only_that_branch_and_never_the_structure_operations()
    {
        await PrepareRbac();
        var a = await Make("A"); var b = await Make("B");
        var (editor, _) = await Member(BranchEdit, ScopeKind.Branch, a.Id);

        var list = Ok(await Mgmt.ListAsync(editor.Id, Company.Id));
        Assert.Equal(a.Id, Assert.Single(list).Id);                                                                         // filtered: no other branch is disclosed
        Assert.Equal(a.Id, Ok(await Mgmt.GetAsync(editor.Id, Company.Id, a.Id)).Id);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(editor.Id, Company.Id, b.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(editor.Id, Company.Id, Guid.NewGuid())).Status);          // no existence probing
        Assert.Equal("Renamed", Ok(await Mgmt.UpdateAsync(editor.Id, Company.Id, a.Id, 1, NewBranch("A", en: "Renamed"))).Data.NameEn);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.UpdateAsync(editor.Id, Company.Id, b.Id, 1, NewBranch("B", en: "Nope"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.CreateAsync(editor.Id, Company.Id, NewBranch("NEW"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.DeactivateAsync(editor.Id, Company.Id, a.Id, 2, null)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ReactivateAsync(editor.Id, Company.Id, a.Id, 2)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.MakeMainAsync(editor.Id, Company.Id, a.Id, 2)).Status);
        Assert.Equal(new BranchLimits(50, 3), Ok(await Mgmt.LimitsAsync(editor.Id, Company.Id)));                          // any branch viewer may read the edition facts
        Assert.Equal(1, (await Domain.GetAsync(Company.Id, b.Id))!.Revision);                                              // the other branch was not touched
    }

    [PostgresFact]
    public async Task A_member_without_branch_permissions_and_a_non_member_are_refused_everywhere()
    {
        await PrepareRbac();
        var b = await Make("B");
        var plain = await NewUser("plain-member");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var stranger = await NewUser("stranger");
        foreach (var user in new[] { plain, stranger })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ListAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(user.Id, Company.Id, b.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.CreateAsync(user.Id, Company.Id, NewBranch("X"))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.UpdateAsync(user.Id, Company.Id, b.Id, 1, NewBranch("B"))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.DeactivateAsync(user.Id, Company.Id, b.Id, 1, null)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ReactivateAsync(user.Id, Company.Id, b.Id, 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.MakeMainAsync(user.Id, Company.Id, b.Id, 1)).Status);
        }
        var other = await ReadyCompany();
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ListAsync(Admin.Id, other.Id)).Status);                            // an administrator of company A has nothing in company B
        Assert.Equal(1L, await Count(other, Br.Table));
    }

    [PostgresFact]
    public async Task Deactivating_the_main_branch_with_a_successor_needs_the_designate_permission_too()
    {
        await PrepareRbac();
        var main = await Main();
        var b = await Make("B");
        var (manager, _) = await Member([PermissionCatalog.Codes.BranchActivate, PermissionCatalog.Codes.BranchesView], ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.DeactivateAsync(manager.Id, Company.Id, main.Id, 1, b.Id)).Status);
        Assert.Equal(1L, await MainCount());
        Assert.Equal(main.Id, (await Main()).Id);
        Assert.Equal("main_branch_deactivation_denied", (await Mgmt.DeactivateAsync(manager.Id, Company.Id, main.Id, 1, null)).Code);   // allowed to try, refused by the rules
        Assert.False(Ok(await Mgmt.DeactivateAsync(manager.Id, Company.Id, b.Id, 1, null)).IsActive);                      // a non-main branch needs only the activate permission
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.MakeMainAsync(manager.Id, Company.Id, b.Id, 2)).Status);
    }

    [PostgresFact]
    public async Task The_trial_limit_is_enforced_through_the_authorized_backend_path()
    {
        await PrepareRbac(TrialEntitlementPolicy.Instance);
        var denied = await Mgmt.CreateAsync(Admin.Id, Company.Id, NewBranch("SECOND"));
        Assert.Equal(ConfigStatus.EntitlementDenied, denied.Status);
        Assert.Equal(1L, await BranchRows());
        Assert.Equal(new BranchLimits(1, 1), Ok(await Mgmt.LimitsAsync(Admin.Id, Company.Id)));
    }
}

public class BranchEpochTests : BranchAccessTestBase
{
    [PostgresFact]
    public async Task Deactivating_and_reactivating_a_branch_never_restores_old_access_without_an_explicit_new_assignment()
    {
        await PrepareRbac();
        var b = await Make("EPOCH");
        var (user, roleId) = await Member(BranchEdit, ScopeKind.Branch, b.Id);
        Assert.Equal(b.Id, Ok(await Mgmt.GetAsync(user.Id, Company.Id, b.Id)).Id);                                           // effective at epoch 1

        Ok(await Domain.DeactivateAsync(Admin.Id, Company.Id, b.Id, 1));                                                     // epoch 2, inactive
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(user.Id, Company.Id, b.Id)).Status);                       // immediate: no cache, same session state
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ListAsync(user.Id, Company.Id)).Status);
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, Company.Id)).Grants);

        Ok(await Domain.ReactivateAsync(Admin.Id, Company.Id, b.Id, 2));                                                     // epoch 3, active again
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(user.Id, Company.Id, b.Id)).Status);                       // the old assignment stays dead
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, Company.Id)).Grants);
        Assert.Equal(0L, await RevokedAssignments());                                                                         // nothing was silently revoked or restored

        // Only an explicit, authorized re-assignment restores access, recorded at the new epoch; the dead row is retired (audited).
        var again = await Assign(user, roleId, b.Id);
        Assert.True(again.IsOk, again.Code);
        Assert.Equal(3, again.Value!.ScopeEpoch);
        Assert.Equal(b.Id, Ok(await Mgmt.GetAsync(user.Id, Company.Id, b.Id)).Id);
        Assert.Equal(1L, await RevokedAssignments());
        Assert.Equal(1, (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, user.Id)).Value!.Count);
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM " + SystemSchema.AccessAudit.Table + " WHERE " + SystemSchema.AccessAudit.Detail + " LIKE '%stale activation epoch%'"));

        // A second deactivation kills the new assignment as well.
        Ok(await Domain.DeactivateAsync(Admin.Id, Company.Id, b.Id, 3));
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(user.Id, Company.Id, b.Id)).Status);
    }

    [PostgresFact]
    public async Task The_epoch_of_an_assignment_is_required_for_branch_scope_and_immutable()
    {
        await PrepareRbac();
        var b = await Make("IMM");
        var (user, _) = await Member(BranchView_, ScopeKind.Branch, b.Id);
        Assert.Equal(1L, await Scalar<long>($"SELECT {Asg.ScopeEpoch} FROM {Asg.Table} WHERE {Asg.UserId} = @u", ("u", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Asg.Table} SET {Asg.ScopeEpoch} = {Asg.ScopeEpoch} + 1 WHERE {Asg.UserId} = @u", ("u", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Asg.Table} SET {Asg.ScopeEpoch} = NULL WHERE {Asg.UserId} = @u", ("u", user.Id)));
        // Company-wide assignments carry no epoch and the CHECK refuses one; a branch assignment without an epoch is refused too.
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Asg.Table} SET {Asg.ScopeKind} = 0, {Asg.ScopeId} = NULL WHERE {Asg.UserId} = @u", ("u", user.Id)));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Asg.Table} WHERE {Asg.ScopeKind} = 0 AND {Asg.ScopeEpoch} IS NOT NULL"));
    }

    [PostgresFact]
    public async Task When_branch_validation_is_unavailable_branch_grants_fail_closed_and_company_wide_grants_keep_working()
    {
        await PrepareRbac();
        var b = await Make("DOWN");
        var (branchUser, _) = await Member(BranchEdit, ScopeKind.Branch, b.Id);
        var (companyUser, _) = await Member([PermissionCatalog.Codes.ProfileView], ScopeKind.Company, null);
        Assert.NotEmpty((await Rbac.GetEffectivePermissionsAsync(branchUser.Id, Company.Id)).Grants);

        // The company database becomes unusable for the API (checksum of its migration journal edited outside the application).
        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'edited' WHERE version = 4");
        var down = await Rbac.GetEffectivePermissionsAsync(branchUser.Id, Company.Id);
        Assert.Empty(down.Grants);                                                                                            // dropped, never turned into company-wide access
        Assert.False(down.Has(PermissionCatalog.Codes.BranchesView, ScopeTarget.Company));
        Assert.False(down.Has(PermissionCatalog.Codes.BranchesView, ScopeTarget.Branch(b.Id)));
        Assert.True((await Rbac.GetEffectivePermissionsAsync(companyUser.Id, Company.Id)).Has(PermissionCatalog.Codes.ProfileView, ScopeTarget.Company));   // independent grant intact
        Assert.True((await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id)).HasCompanyWide(PermissionCatalog.Codes.BranchesView));
        // A new branch assignment cannot be made while validation is unavailable.
        var role = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "late-role", BranchView_)).Value!;
        var late = await NewUser("late-user");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, late.Login)).IsOk);
        Assert.Equal("scope_not_verifiable", (await Assign(late, role.Id, b.Id)).Code);

        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = '{MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 4).Checksum}' WHERE version = 4");
        Assert.NotEmpty((await Rbac.GetEffectivePermissionsAsync(branchUser.Id, Company.Id)).Grants);                         // recovers by itself: nothing was changed
    }

    [PostgresFact]
    public async Task A_branch_grant_never_applies_in_another_company_even_with_the_same_branch_id()
    {
        await PrepareRbac();
        var b = await Make("HOME");
        var (user, _) = await Member(BranchEdit, ScopeKind.Branch, b.Id);
        var other = await ReadyCompany();
        // The same user is a member of the other company without any grant there.
        Assert.True((await Access.GrantMembershipAsync((await NewAdminOf(other)).Id, other.Id, user.Login)).IsOk);
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, other.Id)).Grants);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(user.Id, other.Id, b.Id)).Status);
    }
}

/// <summary>End-to-end HTTP tests of /api/v1/company/branches (both clients use these endpoints).</summary>
public abstract class HttpApiTestBase : BranchAccessTestBase
{
    protected const string CookieName = "__Host-hasm_session";
    protected WebApplicationFactory<Program>? factory;
    protected HttpClient client = null!;
    protected readonly LogCapture apiLogs = new();

    protected async Task StartApi(IEntitlementPolicy? policy = null, Action<IWebHostBuilder>? configure = null)
    {
        await PrepareRbac();
        var resolver = Resolver;
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
            configure?.Invoke(b);
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(apiLogs));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton(_ => Audit);
                s.AddSingleton(resolver);
                if (policy is not null) s.AddSingleton(policy);                  // the LAST registration wins; production registers only the Trial policy
            });
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    }

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        await base.DisposeAsync();
    }

    protected async Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, string? cookie = null, bool header = true, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    protected async Task<string> SignIn(UserRecord user, bool selectCompany = true)
    {
        var r = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = user.Login, password = Pw });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var cookie = r.SetCookies.Single(c => c.StartsWith(CookieName + "=")).Split(';')[0][(CookieName.Length + 1)..];
        if (selectCompany) Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/session/company", new { companyId = Company.Id }, cookie)).Status);
        return cookie;
    }

    protected static string Etag(ApiResponse r) => r.Headers.ETag!.Tag;
}

public class BranchApiTests : HttpApiTestBase
{

    [PostgresFact]
    public async Task Every_branch_endpoint_needs_authentication_and_a_selected_company()
    {
        await StartApi();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/v1/company/branches"), (HttpMethod.Post, "/api/v1/company/branches"), (HttpMethod.Get, $"/api/v1/company/branches/{id}"),
            (HttpMethod.Put, $"/api/v1/company/branches/{id}"), (HttpMethod.Post, $"/api/v1/company/branches/{id}/deactivate"),
            (HttpMethod.Post, $"/api/v1/company/branches/{id}/activate"), (HttpMethod.Post, $"/api/v1/company/branches/{id}/make-main"),
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
    }

    [PostgresFact]
    public async Task The_administrator_manages_branches_end_to_end_with_etags_and_the_caller_capabilities()
    {
        await StartApi(new BranchLimitPolicy(5));
        var cookie = await SignIn(Admin);
        var list = await Send(HttpMethod.Get, "/api/v1/company/branches", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        var main = list.Json.GetProperty("branches")[0];
        Assert.Equal("MAIN", main.GetProperty("code").GetString());
        Assert.True(main.GetProperty("isMain").GetBoolean());
        Assert.True(main.GetProperty("seeded").GetBoolean());
        Assert.Equal(5, list.Json.GetProperty("limit").GetInt32());
        Assert.Equal(1, list.Json.GetProperty("everCreated").GetInt32());
        Assert.True(list.Json.GetProperty("can").GetProperty("create").GetBoolean());

        var created = await Send(HttpMethod.Post, "/api/v1/company/branches", new { code = "north", nameAr = "الشمال", nameEn = "North", phone = "+963 11 123456" }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal("NORTH", created.Json.GetProperty("code").GetString());
        Assert.Contains($"/api/v1/company/branches/{id}", created.Headers.Location!.ToString());

        var got = await Send(HttpMethod.Get, $"/api/v1/company/branches/{id}", cookie: cookie);
        Assert.Equal("\"1\"", Etag(got));
        var updated = await Send(HttpMethod.Put, $"/api/v1/company/branches/{id}", new { nameAr = "الشمال", nameEn = "North Branch" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("\"2\"", Etag(updated));
        Assert.Equal("North Branch", updated.Json.GetProperty("nameEn").GetString());

        var swapped = await Send(HttpMethod.Post, $"/api/v1/company/branches/{id}/make-main", null, cookie, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, swapped.Status);
        Assert.True(swapped.Json.GetProperty("isMain").GetBoolean());
        var oldMain = main.GetProperty("id").GetGuid();
        var off = await Send(HttpMethod.Post, $"/api/v1/company/branches/{oldMain}/deactivate", new { }, cookie, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, off.Status);
        Assert.False(off.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(2, off.Json.GetProperty("activationEpoch").GetInt32());
        var on = await Send(HttpMethod.Post, $"/api/v1/company/branches/{oldMain}/activate", null, cookie, ifMatch: Etag(off));
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.Equal(3, on.Json.GetProperty("activationEpoch").GetInt32());
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task The_server_enforces_concurrency_validation_the_main_rules_and_the_trial_limit()
    {
        await StartApi();                                                                                                     // the real Trial policy
        var cookie = await SignIn(Admin);
        var list = await Send(HttpMethod.Get, "/api/v1/company/branches", cookie: cookie);
        var mainId = list.Json.GetProperty("branches")[0].GetProperty("id").GetGuid();

        var trial = await Send(HttpMethod.Post, "/api/v1/company/branches", new { code = "SECOND", nameEn = "Second" }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, trial.Status);
        Assert.Equal("entitlement_denied", trial.Error);                                                                      // not only a UI rule

        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"/api/v1/company/branches/{mainId}", new { nameEn = "X" }, cookie)).Status);
        var stale = await Send(HttpMethod.Put, $"/api/v1/company/branches/{mainId}", new { nameEn = "X" }, cookie, ifMatch: "\"9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.Json.GetProperty("currentRevision").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"/api/v1/company/branches/{mainId}", new { nameEn = "X" }, cookie, ifMatch: "abc")).Status);
        var invalid = await Send(HttpMethod.Put, $"/api/v1/company/branches/{mainId}", new { code = "OTHER", nameEn = "X" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Contains("immutable", invalid.Body);
        var noName = await Send(HttpMethod.Put, $"/api/v1/company/branches/{mainId}", new { nameAr = " ", nameEn = "" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, noName.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/v1/company/branches/{Guid.NewGuid()}", cookie: cookie)).Status);

        var denied = await Send(HttpMethod.Post, $"/api/v1/company/branches/{mainId}/deactivate", new { }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, denied.Status);
        Assert.Equal("main_branch_deactivation_denied", denied.Error);
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task State_changes_need_the_csrf_header_and_unauthorized_users_get_403_without_data()
    {
        await StartApi(new BranchLimitPolicy(5));
        var admin = await SignIn(Admin);
        var noHeader = await Send(HttpMethod.Post, "/api/v1/company/branches", new { code = "X1", nameEn = "X" }, admin, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Equal("missing_request_header", noHeader.Error);
        Assert.Equal(1L, await BranchRows());

        var plain = await NewUser("api-plain");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var cookie = await SignIn(plain);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/v1/company/branches", null), (HttpMethod.Post, "/api/v1/company/branches", new { code = "X2", nameEn = "X" }),
        })
        {
            var r = await Send(method, path, body, cookie);
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Equal("forbidden", r.Error);
            Assert.DoesNotContain("MAIN", r.Body);
        }
        Assert.Equal(1L, await BranchRows());
    }

    [PostgresFact]
    public async Task A_branch_scoped_user_sees_only_their_branch_over_http()
    {
        await StartApi(new BranchLimitPolicy(5));
        var a = await Make("A"); var b = await Make("B");
        var (user, _) = await Member(BranchEdit, ScopeKind.Branch, a.Id);
        var cookie = await SignIn(user);
        var list = await Send(HttpMethod.Get, "/api/v1/company/branches", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(1, list.Json.GetProperty("branches").GetArrayLength());
        Assert.False(list.Json.GetProperty("can").GetProperty("create").GetBoolean());
        Assert.Equal(1, list.Json.GetProperty("can").GetProperty("update").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"/api/v1/company/branches/{b.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/api/v1/company/branches/{a.Id}", new { nameEn = "Mine" }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/company/branches/{a.Id}/deactivate", new { }, cookie, ifMatch: "\"2\"")).Status);
        Ok(await Domain.DeactivateAsync(Admin.Id, Company.Id, a.Id, 2));                                                      // the epoch moves: the SAME session loses access at once
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/company/branches", cookie: cookie)).Status);
    }
}
