using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Wh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using Asg = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;

namespace Hasm.Database.IntegrationTests;

/// <summary>Warehouse-scoped RBAC, activation epochs and the authorized facade (system migration 0008, Phase 3.1C).</summary>
public abstract class WarehouseAccessTestBase : HttpApiTestBase
{
    protected CompanyWarehouseService WhDomain { get; private set; } = null!;
    protected WarehouseManagementService WhMgmt { get; private set; } = null!;

    protected async Task PrepareWarehouses(int maxWarehouses = 50)
    {
        await PrepareRbac();
        WhDomain = new CompanyWarehouseService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new WarehouseLimitPolicy(maxWarehouses), Clock);
        WhMgmt = new WarehouseManagementService(Rbac, WhDomain);
    }

    protected static WarehouseInput Input(string code, Guid branchId, string? en = "Warehouse") =>
        new(code, branchId, "مستودع", en, null, null, null, null, null, null, null, null);

    protected async Task<WarehouseView> NewWarehouse(string code, Guid? branchId = null) =>
        Ok(await WhDomain.CreateAsync(Admin.Id, Company.Id, Input(code, branchId ?? (await Main()).Id)));

    protected Task<AccessResult<AssignmentView>> AssignWh(UserRecord user, Guid roleId, Guid warehouseId) =>
        Rbac.AssignRoleAsync(Admin.Id, Company.Id, user.Id, roleId, ScopeKind.Warehouse, warehouseId);

    protected Task<long> RevokedWarehouseAssignments() =>
        Scalar<long>($"SELECT count(*) FROM {Asg.Table} WHERE {Asg.RevokedAtUtc} IS NOT NULL AND {Asg.ScopeKind} = 2");

    protected static string[] WhViewOnly => [PermissionCatalog.Codes.WarehousesView];
    protected static string[] WhEdit => [PermissionCatalog.Codes.WarehousesView, PermissionCatalog.Codes.WarehousesUpdate];
}

public class WarehouseAccessTests : WarehouseAccessTestBase
{
    [PostgresFact]
    public async Task The_company_administrator_preset_holds_the_warehouse_permissions_and_other_presets_do_not()
    {
        await PrepareWarehouses();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "company.warehouses.view", "company.warehouses.update", "company.warehousestructure.create", "company.warehousestructure.activate" })
            Assert.Contains(c, admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.warehouse"));
        Assert.Empty(Ok(await WhMgmt.ListAsync(Admin.Id, Company.Id)));
        Assert.Equal(new WarehouseLimits(50, 0), Ok(await WhMgmt.LimitsAsync(Admin.Id, Company.Id)));
    }

    [PostgresFact]
    public async Task A_warehouse_assignment_is_verified_against_the_real_warehouses_of_this_company_and_records_the_epoch()
    {
        await PrepareWarehouses();
        var w = await NewWarehouse("W1");
        var (user, roleId) = await Member(WhViewOnly, ScopeKind.Warehouse, w.Id);
        var assignment = Assert.Single((await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, user.Id)).Value!);
        Assert.Equal(ScopeKind.Warehouse, assignment.Scope);
        Assert.Equal(w.Id, assignment.ScopeId);
        Assert.Equal(1, assignment.ScopeEpoch);
        Assert.Equal(1L, await Scalar<long>($"SELECT {Asg.WarehouseScopeEpoch} FROM {Asg.Table} WHERE {Asg.UserId} = @u", ("u", user.Id)));

        var other = await NewUser("other-wh-user");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, other.Login)).IsOk);
        Assert.Equal("scope_not_verifiable", (await AssignWh(other, roleId, Guid.NewGuid())).Code);                          // unknown warehouse
        Assert.Equal("scope_not_verifiable", (await AssignWh(other, roleId, (await Main()).Id)).Code);                       // a BRANCH id is not a warehouse
        var foreign = await ReadyCompany();
        var foreignWh = Ok(await new CompanyWarehouseService(SinglePrincipalResolver(foreign), new CompanyAuditedTransactions(SinglePrincipalResolver(foreign), Audit, Clock),
            new WarehouseLimitPolicy(5), Clock).CreateAsync(Admin.Id, foreign.Id, Input("FW", (await Branches(company: foreign).ListAsync(foreign.Id)).Single().Id)));
        Assert.Equal("scope_not_verifiable", (await AssignWh(other, roleId, foreignWh.Id)).Code);                            // a warehouse of ANOTHER company
        var inactive = await NewWarehouse("OFF");
        Ok(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, inactive.Id, 1));
        Assert.Equal("scope_not_verifiable", (await AssignWh(other, roleId, inactive.Id)).Code);                             // inactive warehouse
        Assert.Equal("invalid_scope", (await Rbac.AssignRoleAsync(Admin.Id, Company.Id, other.Id, roleId, ScopeKind.Warehouse, null)).Code);
    }

    [PostgresFact]
    public async Task A_warehouse_scoped_user_reaches_only_that_warehouse_and_never_the_structure_operations()
    {
        await PrepareWarehouses();
        var a = await NewWarehouse("A"); var b = await NewWarehouse("B");
        var (editor, _) = await Member(WhEdit, ScopeKind.Warehouse, a.Id);

        Assert.Equal(a.Id, Assert.Single(Ok(await WhMgmt.ListAsync(editor.Id, Company.Id))).Id);                              // filtered: no other warehouse is disclosed
        Assert.Equal(a.Id, Ok(await WhMgmt.GetAsync(editor.Id, Company.Id, a.Id)).Id);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(editor.Id, Company.Id, b.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(editor.Id, Company.Id, Guid.NewGuid())).Status);         // no existence probing
        Assert.Equal("Renamed", Ok(await WhMgmt.UpdateAsync(editor.Id, Company.Id, a.Id, 1, Input("A", a.BranchId, en: "Renamed"))).Data.NameEn);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.UpdateAsync(editor.Id, Company.Id, b.Id, 1, Input("B", b.BranchId, en: "Nope"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.CreateAsync(editor.Id, Company.Id, Input("NEW", a.BranchId))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.DeactivateAsync(editor.Id, Company.Id, a.Id, 2)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.ReactivateAsync(editor.Id, Company.Id, a.Id, 2)).Status);
        Assert.Equal(new WarehouseLimits(50, 2), Ok(await WhMgmt.LimitsAsync(editor.Id, Company.Id)));
        Assert.Equal(1, (await WhDomain.GetAsync(Company.Id, b.Id))!.Revision);                                              // the other warehouse was not touched
    }

    [PostgresFact]
    public async Task Branch_and_warehouse_access_never_imply_each_other()
    {
        await PrepareWarehouses();
        var main = await Main();
        var w = await NewWarehouse("W1", main.Id);
        // A branch-scoped branch manager has no warehouse access (not even to the warehouses of that branch)...
        var (branchUser, _) = await Member(BranchEdit, ScopeKind.Branch, main.Id);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(branchUser.Id, Company.Id, w.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.ListAsync(branchUser.Id, Company.Id)).Status);
        // ... and a warehouse-scoped user has no branch access (not even to the branch of the warehouse).
        var (whUser, _) = await Member(WhEdit, ScopeKind.Warehouse, w.Id);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.GetAsync(whUser.Id, Company.Id, main.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ListAsync(whUser.Id, Company.Id)).Status);
        // A role holding warehouse permissions assigned at BRANCH scope is inert for warehouses (the resource is warehouse-level).
        var (mixed, _) = await Member(WhEdit, ScopeKind.Branch, main.Id);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(mixed.Id, Company.Id, w.Id)).Status);
    }

    [PostgresFact]
    public async Task A_member_without_warehouse_permissions_and_a_non_member_are_refused_everywhere()
    {
        await PrepareWarehouses();
        var w = await NewWarehouse("W1");
        var plain = await NewUser("plain-wh-member");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var stranger = await NewUser("wh-stranger");
        foreach (var user in new[] { plain, stranger })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.ListAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(user.Id, Company.Id, w.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.CreateAsync(user.Id, Company.Id, Input("X", w.BranchId))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.UpdateAsync(user.Id, Company.Id, w.Id, 1, Input("W1", w.BranchId))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.DeactivateAsync(user.Id, Company.Id, w.Id, 1)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.ReactivateAsync(user.Id, Company.Id, w.Id, 1)).Status);
        }
        var other = await ReadyCompany();
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.ListAsync(Admin.Id, other.Id)).Status);                           // an administrator of company A has nothing in company B
    }

    [PostgresFact]
    public async Task The_trial_limit_is_enforced_through_the_authorized_backend_path()
    {
        await PrepareWarehouses();
        var trial = new WarehouseManagementService(Rbac, new CompanyWarehouseService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), TrialEntitlementPolicy.Instance, Clock));
        var main = await Main();
        Ok(await trial.CreateAsync(Admin.Id, Company.Id, Input("ONE", main.Id)));
        Assert.Equal(ConfigStatus.EntitlementDenied, (await trial.CreateAsync(Admin.Id, Company.Id, Input("TWO", main.Id))).Status);
        Assert.Equal(new WarehouseLimits(1, 1), Ok(await trial.LimitsAsync(Admin.Id, Company.Id)));
    }
}

public class WarehouseEpochTests : WarehouseAccessTestBase
{
    [PostgresFact]
    public async Task Deactivating_and_reactivating_a_warehouse_never_restores_old_access_without_an_explicit_new_assignment()
    {
        await PrepareWarehouses();
        var w = await NewWarehouse("EPOCH");
        var (user, roleId) = await Member(WhEdit, ScopeKind.Warehouse, w.Id);
        Assert.Equal(w.Id, Ok(await WhMgmt.GetAsync(user.Id, Company.Id, w.Id)).Id);

        Ok(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, w.Id, 1));                                                   // epoch 2, inactive
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(user.Id, Company.Id, w.Id)).Status);                     // immediate: no cache
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.ListAsync(user.Id, Company.Id)).Status);
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, Company.Id)).Grants);

        Ok(await WhDomain.ReactivateAsync(Admin.Id, Company.Id, w.Id, 2));                                                   // epoch 3, active again
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(user.Id, Company.Id, w.Id)).Status);                     // the old assignment stays dead
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, Company.Id)).Grants);
        Assert.Equal(0L, await RevokedWarehouseAssignments());

        var again = await AssignWh(user, roleId, w.Id);
        Assert.True(again.IsOk, again.Code);
        Assert.Equal(3, again.Value!.ScopeEpoch);
        Assert.Equal(w.Id, Ok(await WhMgmt.GetAsync(user.Id, Company.Id, w.Id)).Id);
        Assert.Equal(1L, await RevokedWarehouseAssignments());                                                               // the dead row was retired, audited
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM " + SystemSchema.AccessAudit.Table + " WHERE " + SystemSchema.AccessAudit.Detail + " LIKE '%stale activation epoch%'"));

        Ok(await WhDomain.DeactivateAsync(Admin.Id, Company.Id, w.Id, 3));
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(user.Id, Company.Id, w.Id)).Status);
    }

    [PostgresFact]
    public async Task The_warehouse_epoch_is_required_for_warehouse_scope_only_and_immutable()
    {
        await PrepareWarehouses();
        var w = await NewWarehouse("IMM");
        var (user, _) = await Member(WhViewOnly, ScopeKind.Warehouse, w.Id);
        Assert.Equal(1L, await Scalar<long>($"SELECT {Asg.WarehouseScopeEpoch} FROM {Asg.Table} WHERE {Asg.UserId} = @u", ("u", user.Id)));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Asg.Table} WHERE {Asg.ScopeKind} = 2 AND {Asg.ScopeEpoch} IS NOT NULL"));   // the branch column stays empty
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Asg.Table} SET {Asg.WarehouseScopeEpoch} = {Asg.WarehouseScopeEpoch} + 1 WHERE {Asg.UserId} = @u", ("u", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Asg.Table} SET {Asg.WarehouseScopeEpoch} = NULL WHERE {Asg.UserId} = @u", ("u", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Asg.Table} SET {Asg.ScopeKind} = 0, {Asg.ScopeId} = NULL WHERE {Asg.UserId} = @u", ("u", user.Id)));
        // A company-wide or branch assignment may never carry a warehouse epoch.
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Asg.Table} WHERE {Asg.ScopeKind} <> 2 AND {Asg.WarehouseScopeEpoch} IS NOT NULL"));
    }

    [PostgresFact]
    public async Task When_validation_is_unavailable_warehouse_grants_fail_closed_and_company_wide_grants_keep_working()
    {
        await PrepareWarehouses();
        var w = await NewWarehouse("DOWN");
        var (whUser, _) = await Member(WhEdit, ScopeKind.Warehouse, w.Id);
        var (companyUser, _) = await Member([PermissionCatalog.Codes.ProfileView], ScopeKind.Company, null);
        Assert.NotEmpty((await Rbac.GetEffectivePermissionsAsync(whUser.Id, Company.Id)).Grants);

        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'edited' WHERE version = 5");
        var down = await Rbac.GetEffectivePermissionsAsync(whUser.Id, Company.Id);
        Assert.Empty(down.Grants);                                                                                            // dropped, never turned into company-wide access
        Assert.False(down.Has(PermissionCatalog.Codes.WarehousesView, ScopeTarget.Company));
        Assert.False(down.Has(PermissionCatalog.Codes.WarehousesView, ScopeTarget.Warehouse(w.Id)));
        Assert.True((await Rbac.GetEffectivePermissionsAsync(companyUser.Id, Company.Id)).Has(PermissionCatalog.Codes.ProfileView, ScopeTarget.Company));
        var role = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "late-wh-role", WhViewOnly)).Value!;
        var late = await NewUser("late-wh-user");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, late.Login)).IsOk);
        Assert.Equal("scope_not_verifiable", (await AssignWh(late, role.Id, w.Id)).Code);

        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = '{MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 5).Checksum}' WHERE version = 5");
        Assert.NotEmpty((await Rbac.GetEffectivePermissionsAsync(whUser.Id, Company.Id)).Grants);                             // recovers by itself: nothing was changed
    }

    [PostgresFact]
    public async Task A_warehouse_grant_never_applies_in_another_company_even_with_the_same_warehouse_id()
    {
        await PrepareWarehouses();
        var w = await NewWarehouse("HOME");
        var (user, _) = await Member(WhEdit, ScopeKind.Warehouse, w.Id);
        var other = await ReadyCompany();
        Assert.True((await Access.GrantMembershipAsync((await NewAdminOf(other)).Id, other.Id, user.Login)).IsOk);
        Assert.Empty((await Rbac.GetEffectivePermissionsAsync(user.Id, other.Id)).Grants);
        Assert.Equal(ConfigStatus.Forbidden, (await WhMgmt.GetAsync(user.Id, other.Id, w.Id)).Status);
    }
}

/// <summary>End-to-end HTTP tests of /api/v1/company/warehouses (both clients use these endpoints).</summary>
public class WarehouseApiTests : HttpApiTestBase
{
    private const string Base = "/api/v1/company/warehouses";

    [PostgresFact]
    public async Task Every_warehouse_endpoint_needs_authentication_and_a_selected_company()
    {
        await StartApi();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, Base), (HttpMethod.Post, Base), (HttpMethod.Get, $"{Base}/{id}"), (HttpMethod.Put, $"{Base}/{id}"),
            (HttpMethod.Post, $"{Base}/{id}/deactivate"), (HttpMethod.Post, $"{Base}/{id}/activate"),
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
    public async Task The_administrator_manages_warehouses_end_to_end_with_etags_and_the_caller_capabilities()
    {
        await StartApi(new WarehouseLimitPolicy(5));
        var cookie = await SignIn(Admin);
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        Assert.Equal(0, list.Json.GetProperty("warehouses").GetArrayLength());
        Assert.Equal(5, list.Json.GetProperty("limit").GetInt32());
        Assert.Equal(0, list.Json.GetProperty("everCreated").GetInt32());
        Assert.True(list.Json.GetProperty("can").GetProperty("create").GetBoolean());
        var branch = list.Json.GetProperty("branches")[0];
        Assert.Equal("MAIN", branch.GetProperty("code").GetString());
        Assert.False(branch.TryGetProperty("phone", out _));                                                                  // minimal branch fields only
        var branchId = branch.GetProperty("id").GetGuid();

        var created = await Send(HttpMethod.Post, Base, new { code = "main-wh", branchId, nameAr = "المستودع الرئيسي", nameEn = "Main Warehouse", phone = "+963 11 123456" }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal("MAIN-WH", created.Json.GetProperty("code").GetString());
        Assert.Equal(branchId, created.Json.GetProperty("branchId").GetGuid());
        Assert.Contains($"{Base}/{id}", created.Headers.Location!.ToString());

        var got = await Send(HttpMethod.Get, $"{Base}/{id}", cookie: cookie);
        Assert.Equal("\"1\"", Etag(got));
        var updated = await Send(HttpMethod.Put, $"{Base}/{id}", new { nameAr = "المستودع الرئيسي", nameEn = "Central Warehouse" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("\"2\"", Etag(updated));
        var off = await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", null, cookie, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, off.Status);
        Assert.False(off.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(2, off.Json.GetProperty("activationEpoch").GetInt32());
        var on = await Send(HttpMethod.Post, $"{Base}/{id}/activate", null, cookie, ifMatch: Etag(off));
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.Equal(3, on.Json.GetProperty("activationEpoch").GetInt32());
    }

    [PostgresFact]
    public async Task The_server_enforces_concurrency_validation_the_branch_rules_and_the_trial_limit()
    {
        await StartApi();                                                                                                     // the real Trial policy: one warehouse
        var cookie = await SignIn(Admin);
        var branchId = (await Send(HttpMethod.Get, Base, cookie: cookie)).Json.GetProperty("branches")[0].GetProperty("id").GetGuid();

        var first = await Send(HttpMethod.Post, Base, new { code = "W1", branchId, nameEn = "One" }, cookie);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        var id = first.Json.GetProperty("id").GetGuid();
        var trial = await Send(HttpMethod.Post, Base, new { code = "W2", branchId, nameEn = "Two" }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, trial.Status);
        Assert.Equal("entitlement_denied", trial.Error);                                                                      // not only a UI rule

        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{id}", new { nameEn = "X" }, cookie)).Status);
        var stale = await Send(HttpMethod.Put, $"{Base}/{id}", new { nameEn = "X" }, cookie, ifMatch: "\"9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.Json.GetProperty("currentRevision").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/{id}", new { nameEn = "X" }, cookie, ifMatch: "abc")).Status);
        var invalid = await Send(HttpMethod.Put, $"{Base}/{id}", new { code = "OTHER", nameEn = "X" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Contains("immutable", invalid.Body);
        var moved = await Send(HttpMethod.Put, $"{Base}/{id}", new { branchId = Guid.NewGuid(), nameEn = "X" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, moved.Status);
        Assert.Contains("branchId", moved.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base, new { code = "W3", nameEn = "NoBranch" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie)).Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Wh.Table}"));
    }

    [PostgresFact]
    public async Task A_branch_with_active_warehouses_cannot_be_deactivated_over_http()
    {
        await StartApi(new WarehouseLimitPolicy(5));
        var cookie = await SignIn(Admin);
        var other = await Make("OTHER");
        var created = await Send(HttpMethod.Post, Base, new { code = "W1", branchId = other.Id, nameEn = "One" }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var denied = await Send(HttpMethod.Post, $"/api/v1/company/branches/{other.Id}/deactivate", new { }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, denied.Status);
        Assert.Equal("branch_has_active_warehouses", denied.Error);
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", null, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/company/branches/{other.Id}/deactivate", new { }, cookie, ifMatch: "\"1\"")).Status);
        var inactive = await Send(HttpMethod.Post, Base, new { code = "W2", branchId = other.Id, nameEn = "Two" }, cookie);
        Assert.Equal(HttpStatusCode.Conflict, inactive.Status);
        Assert.Equal("branch_inactive", inactive.Error);
    }

    [PostgresFact]
    public async Task State_changes_need_the_csrf_header_and_unauthorized_users_get_403_without_data()
    {
        await StartApi(new WarehouseLimitPolicy(5));
        var admin = await SignIn(Admin);
        var branchId = (await Main()).Id;
        var noHeader = await Send(HttpMethod.Post, Base, new { code = "X1", branchId, nameEn = "X" }, admin, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Equal("missing_request_header", noHeader.Error);

        var plain = await NewUser("api-plain-wh");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, plain.Login)).IsOk);
        var cookie = await SignIn(plain);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, Base, null), (HttpMethod.Post, Base, new { code = "X2", branchId, nameEn = "X" }),
        })
        {
            var r = await Send(method, path, body, cookie);
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Equal("forbidden", r.Error);
            Assert.DoesNotContain("MAIN", r.Body);
        }
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Wh.Table}"));
    }

    [PostgresFact]
    public async Task A_warehouse_scoped_user_sees_only_their_warehouse_over_http_and_loses_it_when_the_epoch_moves()
    {
        await StartApi(new WarehouseLimitPolicy(5));
        var domain = new CompanyWarehouseService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new WarehouseLimitPolicy(5), Clock);
        var main = await Main();
        var a = Ok(await domain.CreateAsync(Admin.Id, Company.Id, new WarehouseInput("A", main.Id, "أ", "A", null, null, null, null, null, null, null, null)));
        var b = Ok(await domain.CreateAsync(Admin.Id, Company.Id, new WarehouseInput("B", main.Id, "ب", "B", null, null, null, null, null, null, null, null)));
        var (user, _) = await Member([PermissionCatalog.Codes.WarehousesView, PermissionCatalog.Codes.WarehousesUpdate], ScopeKind.Warehouse, a.Id);
        var cookie = await SignIn(user);
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(1, list.Json.GetProperty("warehouses").GetArrayLength());
        Assert.False(list.Json.GetProperty("can").GetProperty("create").GetBoolean());
        Assert.Equal(1, list.Json.GetProperty("can").GetProperty("update").GetArrayLength());
        Assert.Equal(1, list.Json.GetProperty("branches").GetArrayLength());                                                  // only the branch that hosts the visible warehouse
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{b.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/{a.Id}", new { nameEn = "Mine" }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{a.Id}/deactivate", null, cookie, ifMatch: "\"2\"")).Status);
        Ok(await domain.DeactivateAsync(Admin.Id, Company.Id, a.Id, 2));                                                      // the epoch moves: the SAME session loses access at once
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Base, cookie: cookie)).Status);
    }
}
