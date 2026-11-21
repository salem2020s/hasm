using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S3b-1b against real PostgreSQL: the company MFA policy service (storage, permission, validation, the lock-out guard, audit and evaluation). Disposable cluster only.</summary>
public class MfaPolicyTests : AccessTestBase
{
    private MfaPolicyService Policies = null!;

    private async Task Setup()
    {
        await StartAccess();
        Policies = new MfaPolicyService(Options.BuildSystemConnectionString(admin: true), Clock, Audit, Access, Store);
    }

    public override async Task DisposeAsync()
    {
        if (Policies is not null) await Policies.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SetMfaState(UserRecord user, int state) => Exec($"UPDATE {U.Table} SET {M.State} = @s WHERE {U.Id} = @i", ("s", (short)state), ("i", user.Id));
    private Task<long> PolicyEvents() => Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} = @t", ("t", MfaAuditEvents.PolicyChanged));

    /// <summary>A company administrator who has an authenticator (so the lock-out guard lets them set any policy).</summary>
    private async Task<UserRecord> EnrolledAdmin(Guid company)
    {
        var admin = await NewAdmin(company, "enrolled");
        await SetMfaState(admin, 2);
        return admin;
    }

    [PostgresFact]
    public async Task A_company_without_a_stored_policy_is_Off_and_only_holders_of_the_view_permission_may_read_it()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var plain = await NewMember(company, admin, "plain");
        var read = Ok(await Policies.GetAsync(admin.Id, company));
        Assert.Equal(CompanyMfaPolicyKind.Off, read.Kind);
        Assert.Empty(read.RoleIds);
        Fails(await Policies.GetAsync(plain.Id, company), AccessStatus.Forbidden);                                   // a member without access.mfapolicy.view
        Fails(await Policies.SetAsync(plain.Id, company, CompanyMfaPolicyKind.Off, null), AccessStatus.Forbidden);   // and without update
        Assert.False(await Policies.RequiresMfaAsync(plain.Id, company));
    }

    [PostgresFact]
    public async Task Setting_the_policy_stores_it_audits_it_and_evaluates_it_for_all_and_for_roles()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var clerk = await NewMember(company, admin, "clerk");
        var other = await NewMember(company, admin, "other");
        var role = await SeedRole(company, "Needs MFA", "company.items.view");
        await SeedAssignment(clerk.Id, company, role);

        var all = Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, null));
        Assert.Equal(CompanyMfaPolicyKind.RequiredForAll, all.Kind);
        Assert.True(await Policies.RequiresMfaAsync(clerk.Id, company));
        Assert.True(await Policies.RequiresMfaAsync(other.Id, company));
        Assert.Equal(1L, await PolicyEvents());

        var roles = Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [role]));
        Assert.Equal([role], roles.RoleIds);
        Assert.True(await Policies.RequiresMfaAsync(clerk.Id, company));                                            // holds a listed role
        Assert.False(await Policies.RequiresMfaAsync(other.Id, company));                                           // does not
        Assert.Equal([role], Ok(await Policies.GetAsync(admin.Id, company)).RoleIds);

        Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.Off, null));
        Assert.False(await Policies.RequiresMfaAsync(clerk.Id, company));
        Assert.Empty(Ok(await Policies.GetAsync(admin.Id, company)).RoleIds);                                       // the list does not outlive the kind
        Assert.Equal(3L, await PolicyEvents());
    }

    [PostgresFact]
    public async Task A_revoked_assignment_no_longer_counts_for_a_roles_policy_and_another_company_is_unaffected()
    {
        await Setup();
        var company = await NewCompany();
        var otherCompany = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var clerk = await NewMember(company, admin, "clerk");
        var role = await SeedRole(company, "Needs MFA", "company.items.view");
        await SeedAssignment(clerk.Id, company, role);
        Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [role]));
        Assert.True(await Policies.RequiresMfaAsync(clerk.Id, company));
        Assert.False(await Policies.RequiresMfaAsync(clerk.Id, otherCompany));                                      // the policy belongs to ONE company
        await Exec($"UPDATE {Ra.Table} SET {Ra.RevokedAtUtc} = now() WHERE {Ra.UserId} = @u", ("u", clerk.Id));
        Assert.False(await Policies.RequiresMfaAsync(clerk.Id, company));
    }

    [PostgresFact]
    public async Task Invalid_requests_are_refused_and_change_nothing()
    {
        await Setup();
        var company = await NewCompany();
        var otherCompany = await NewCompany();
        var admin = await EnrolledAdmin(company);
        var good = await SeedRole(company, "Good", "company.items.view");
        var foreign = await SeedRole(otherCompany, "Foreign", "company.items.view");
        var inactive = await SeedRole(company, "Inactive", "company.items.view");
        await Exec($"UPDATE {Ro.Table} SET {Ro.IsActive} = false WHERE {Ro.Id} = @r", ("r", inactive));

        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, []), AccessStatus.Invalid, "invalid_request");     // roles kind needs roles
        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, [good]), AccessStatus.Invalid, "invalid_request");   // the other kinds take none
        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [foreign]), AccessStatus.Invalid, "unknown_role");
        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [inactive]), AccessStatus.Invalid, "unknown_role");
        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [Guid.NewGuid()]), AccessStatus.Invalid, "unknown_role");
        Fails(await Policies.SetAsync(admin.Id, company, (CompanyMfaPolicyKind)7, null), AccessStatus.Invalid, "invalid_request");
        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, Enumerable.Range(0, MfaPolicyService.MaxRoles + 1).Select(_ => Guid.NewGuid()).ToList()), AccessStatus.Invalid, "invalid_request");
        Assert.Equal(CompanyMfaPolicyKind.Off, Ok(await Policies.GetAsync(admin.Id, company)).Kind);
        Assert.Equal(0L, await PolicyEvents());
        // a PRESET role (no company) may be listed
        var preset = Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [PresetRoles.AccessManagerId]));
        Assert.Equal([PresetRoles.AccessManagerId], preset.RoleIds);
    }

    [PostgresFact]
    public async Task The_lock_out_guard_refuses_a_policy_that_would_require_MFA_of_an_administrator_without_an_authenticator()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await NewAdmin(company);                                                                         // MFA state None
        var role = await SeedRole(company, "Others", "company.items.view");
        var adminRole = (await Access.ListRolesAsync(admin.Id, company)).Value!.Single(r => r.PresetKey == PresetRoles.CompanyAdministratorKey).Id;

        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, null), AccessStatus.Conflict, "mfa_policy_requires_own_enrolment");
        Fails(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [adminRole]), AccessStatus.Conflict, "mfa_policy_requires_own_enrolment");
        Assert.Equal(CompanyMfaPolicyKind.Off, Ok(await Policies.GetAsync(admin.Id, company)).Kind);
        Assert.Equal(0L, await PolicyEvents());

        Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForRoles, [role]));              // a role the administrator does not hold is fine
        Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.Off, null));                             // and turning it off is always allowed

        await SetMfaState(admin, 2);                                                                                 // once the administrator has an authenticator the same change is allowed
        Ok(await Policies.SetAsync(admin.Id, company, CompanyMfaPolicyKind.RequiredForAll, null));
    }

    [PostgresFact]
    public async Task The_company_administrator_preset_holds_the_policy_permissions_and_the_access_manager_does_not()
    {
        await Setup();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var manager = await NewMember(company, admin, "manager");
        Ok(await Access.AssignRoleAsync(admin.Id, company, manager.Id, PresetRoles.AccessManagerId, ScopeKind.Company, null));
        Assert.True(await Allowed(admin, company, PermissionCatalog.Codes.MfaPolicyView));
        Assert.True(await Allowed(admin, company, PermissionCatalog.Codes.MfaPolicyUpdate));
        Assert.False(await Allowed(manager, company, PermissionCatalog.Codes.MfaPolicyView));
        Assert.False(await Allowed(manager, company, PermissionCatalog.Codes.MfaPolicyUpdate));
    }
}
