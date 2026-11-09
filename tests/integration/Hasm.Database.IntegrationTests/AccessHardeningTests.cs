using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Codes = Hasm.Infrastructure.Access.PermissionCatalog.Codes;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Mem = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess;

namespace Hasm.Database.IntegrationTests;

/// <summary>Final-review regression tests: the last Company Administrator cannot be removed through normal operations.</summary>
public class LastAdministratorTests : AccessTestBase
{
    /// <summary>
    /// A member holding every access permission through a CUSTOM role. They are powerful enough to revoke Company
    /// Administrators (the escalation rules allow it) but are not themselves counted as administrators.
    /// </summary>
    private async Task<UserRecord> NewOperator(Guid company, UserRecord inviter, string prefix = "operator")
    {
        var user = await NewMember(company, inviter, prefix);
        await SeedAssignment(user.Id, company, await SeedRole(company, "All access " + prefix, PermissionCatalog.Default.All.Select(p => p.Code).ToArray()));
        return user;
    }

    private async Task<long> ActiveAdminAssignments(Guid company) => await Scalar<long>(
        $"SELECT count(*) FROM {Ra.Table} WHERE {Ra.CompanyId}=@c AND {Ra.RoleId}=@r AND {Ra.RevokedAtUtc} IS NULL", ("c", company), ("r", PresetRoles.CompanyAdministratorId));

    [PostgresFact]
    public async Task The_only_administrators_membership_and_assignment_cannot_be_revoked()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var op = await NewOperator(company, admin);
        var assignment = Ok(await Access.ListAssignmentsAsync(op.Id, company, admin.Id)).Single(a => a.RoleId == PresetRoles.CompanyAdministratorId);
        var auditBefore = await AuditCount(company);

        Fails(await Access.RevokeMembershipAsync(op.Id, company, admin.Id), AccessStatus.Conflict, "last_administrator");
        Fails(await Access.RevokeAssignmentAsync(op.Id, company, assignment.Id), AccessStatus.Conflict, "last_administrator");

        Assert.True(await Allowed(admin, company, Codes.RolesCreate));
        Assert.Equal(1L, await ActiveAdminAssignments(company));
        Assert.Equal(auditBefore, await AuditCount(company));                       // refused attempts leave no trace and no partial change
    }

    [PostgresFact]
    public async Task With_two_administrators_one_can_go_and_then_the_remaining_one_is_protected()
    {
        await StartAccess();
        var company = await NewCompany();
        var first = await NewAdmin(company, "first");
        var second = await NewAdmin(company, "second");
        var op = await NewOperator(company, first);

        Ok(await Access.RevokeMembershipAsync(op.Id, company, first.Id));
        Fails(await Access.RevokeMembershipAsync(op.Id, company, second.Id), AccessStatus.Conflict, "last_administrator");
        Assert.True(await Allowed(second, company, Codes.RolesCreate));

        // the operator holds every administrative permission, so it may appoint another administrator, which frees the older one
        var third = await NewMember(company, op, "third");
        Ok(await Access.AssignRoleAsync(op.Id, company, third.Id, PresetRoles.CompanyAdministratorId, ScopeKind.Company, null));
        Ok(await Access.RevokeMembershipAsync(op.Id, company, second.Id));
        Fails(await Access.RevokeMembershipAsync(op.Id, company, third.Id), AccessStatus.Conflict, "last_administrator");
    }

    [PostgresFact]
    public async Task Disabled_users_and_revoked_memberships_do_not_count_as_administrators()
    {
        await StartAccess();
        var company = await NewCompany();
        var live = await NewAdmin(company, "live");
        var sleeper = await NewAdmin(company, "sleeper");
        var op = await NewOperator(company, live);

        await Auth.SetUserStateAsync(op.Id, sleeper.Id, UserState.Disabled);
        Fails(await Access.RevokeMembershipAsync(op.Id, company, live.Id), AccessStatus.Conflict, "last_administrator");       // the disabled one is not a usable administrator
        Ok(await Access.RevokeMembershipAsync(op.Id, company, sleeper.Id));                                                     // removing an already unusable one is fine
        Fails(await Access.RevokeMembershipAsync(op.Id, company, live.Id), AccessStatus.Conflict, "last_administrator");

        await Auth.SetUserStateAsync(op.Id, sleeper.Id, UserState.Active);                                                     // re-enabled, but membership stays revoked
        Fails(await Access.RevokeMembershipAsync(op.Id, company, live.Id), AccessStatus.Conflict, "last_administrator");
        Assert.True(await Allowed(live, company, Codes.RolesCreate));
        Assert.False(await Allowed(sleeper, company, Codes.RolesCreate));
    }

    [PostgresFact]
    public async Task Non_administrators_and_non_admin_assignments_are_never_blocked_by_the_guard()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var op = await NewOperator(company, admin);
        var auditor = await NewMember(company, admin, "auditor");
        var a = Ok(await Access.AssignRoleAsync(op.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));

        Ok(await Access.RevokeAssignmentAsync(op.Id, company, a.Id));
        Ok(await Access.RevokeMembershipAsync(op.Id, company, auditor.Id));
        Assert.Equal(1L, await ActiveAdminAssignments(company));
    }

    [PostgresFact]
    public async Task A_company_that_already_has_no_administrator_can_still_be_cleaned_up_and_recovered_offline()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var op = await NewOperator(company, admin);
        var other = await NewMember(company, admin, "other");
        // Simulate an out-of-band loss of the administrator (e.g. identity administrator disabled them).
        await Auth.SetUserStateAsync(op.Id, admin.Id, UserState.Disabled);

        Ok(await Access.RevokeMembershipAsync(op.Id, company, other.Id));                                                       // total == 0 administrators: nothing to protect
        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync((await NewPerson("rescuer")).Login, company)); // offline recovery
        var rescuer = Ok(await Access.ListMembersAsync(op.Id, company)).Single(m => m.Login.StartsWith("rescuer"));
        Assert.True(await Access.IsAllowedAsync(rescuer.UserId, company, Codes.RolesCreate, ScopeTarget.Company));
    }

    [PostgresFact]
    public async Task Concurrent_removals_of_the_last_two_administrators_leave_exactly_one()
    {
        await StartAccess();
        for (var round = 0; round < 6; round++)
        {
            var company = await NewCompany();
            var a = await NewAdmin(company, "adma");
            var b = await NewAdmin(company, "admb");
            var op1 = await NewOperator(company, a, "opone");
            var op2 = await NewOperator(company, a, "optwo");

            var results = await Task.WhenAll(
                Access.RevokeMembershipAsync(op1.Id, company, a.Id),
                Access.RevokeMembershipAsync(op2.Id, company, b.Id));

            Assert.Equal(1, results.Count(r => r.IsOk));
            Assert.Equal(1, results.Count(r => r.Status == AccessStatus.Conflict && r.Code == "last_administrator"));
            Assert.Equal(1L, await ActiveAdminAssignments(company));
            Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Mem.Table} WHERE {Mem.CompanyId}=@c AND {Mem.RevokedAtUtc} IS NOT NULL", ("c", company)));
        }
    }

    [PostgresFact]
    public async Task Concurrent_assignment_and_membership_removals_of_the_same_last_administrator_cannot_both_win()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var ops = new[] { await NewOperator(company, admin, "opa"), await NewOperator(company, admin, "opb"), await NewOperator(company, admin, "opc") };
        var assignment = Ok(await Access.ListAssignmentsAsync(ops[0].Id, company, admin.Id)).Single(x => x.RoleId == PresetRoles.CompanyAdministratorId);

        var results = await Task.WhenAll(
            Access.RevokeAssignmentAsync(ops[0].Id, company, assignment.Id),
            Access.RevokeMembershipAsync(ops[1].Id, company, admin.Id),
            Access.RevokeMembershipAsync(ops[2].Id, company, admin.Id));

        Assert.Equal(0, results.Count(r => r.IsOk));
        Assert.Equal(1L, await ActiveAdminAssignments(company));
        Assert.True(await Allowed(admin, company, Codes.RolesCreate));
    }
}

/// <summary>Unauthorized callers must not be able to learn whether a login / user id exists.</summary>
public class AccountEnumerationTests : AccessTestBase
{
    [PostgresFact]
    public async Task Unauthorized_actors_get_identical_results_for_existing_disabled_and_unknown_logins()
    {
        await StartAccess();
        var company = await NewCompany();
        var other = await NewCompany();
        var admin = await NewAdmin(company);
        var otherAdmin = await NewAdmin(other, "otheradm");
        var outsider = await NewPerson("outsider");                                  // authenticated, no membership anywhere
        var roleless = await NewMember(company, admin, "roleless");                   // member without roles
        var auditor = await NewMember(company, admin, "auditor");                     // can view, cannot add members
        Ok(await Access.AssignRoleAsync(admin.Id, company, auditor.Id, PresetRoles.AccessAuditorId, ScopeKind.Company, null));
        var existingMember = await NewMember(company, admin, "existing");
        var disabled = await NewPerson("disabled");
        await Auth.SetUserStateAsync(admin.Id, disabled.Id, UserState.Disabled);
        var locked = await NewPerson("lockedacct");
        await Auth.SetUserStateAsync(admin.Id, locked.Id, UserState.Locked);

        var logins = new[] { existingMember.Login, otherAdmin.Login, outsider.Login, disabled.Login, locked.Login, "ghost.user", "bad login!", "", "x" };
        foreach (var actor in new[] { outsider, roleless, auditor, otherAdmin })     // otherAdmin is powerful, but only in another company
        {
            var results = new List<AccessResult<MemberView>>();
            foreach (var login in logins) results.Add(await Access.GrantMembershipAsync(actor.Id, company, login));
            Assert.All(results, r => Assert.Equal((AccessStatus.Forbidden, "forbidden"), (r.Status, r.Code)));
        }

        // the same holds for every id-based operation: the answer never depends on whether the id exists
        foreach (var actor in new[] { outsider, roleless, auditor })
        {
            var ids = new[] { existingMember.Id, disabled.Id, admin.Id, Guid.NewGuid() };
            foreach (var id in ids)
            {
                Fails(await Access.RevokeMembershipAsync(actor.Id, company, id), AccessStatus.Forbidden, "forbidden");
                Fails(await Access.AssignRoleAsync(actor.Id, company, id, PresetRoles.AccessAuditorId, ScopeKind.Company, null), AccessStatus.Forbidden, "forbidden");
            }
        }
        Assert.Equal(4L, await AuditCount(company, AccessEvent.MembershipGranted));        // bootstrap + the three legitimate grants; no refused attempt left a trace
    }

    [PostgresFact]
    public async Task Authorized_administrators_still_get_one_neutral_answer_for_unusable_logins()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var disabled = await NewPerson("disabled");
        await Auth.SetUserStateAsync(admin.Id, disabled.Id, UserState.Disabled);
        var locked = await NewPerson("lockedacct");
        await Auth.SetUserStateAsync(admin.Id, locked.Id, UserState.Locked);

        foreach (var login in new[] { disabled.Login, locked.Login, "ghost.user", "bad login!", "x" })
            Fails(await Access.GrantMembershipAsync(admin.Id, company, login), AccessStatus.NotFound, "user_not_found");
        var legit = await NewPerson("legit");
        Assert.Equal(legit.Id, Ok(await Access.GrantMembershipAsync(admin.Id, company, legit.Login)).UserId);   // legitimate workflow intact
    }
}

public class ScopeFailClosedTests : AccessTestBase
{
    [PostgresFact]
    public async Task Without_a_validator_scoped_assignments_are_refused_while_company_wide_ones_still_work()
    {
        await StartAccess();
        var company = await NewCompany();
        var super = await NewSuper(company);
        var member = await NewMember(company, super);
        var role = await SeedRole(company, "Reviewer", DocView);
        await using var strict = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog);   // production default: no validator registered

        Fails(await strict.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Branch, BranchA), AccessStatus.Invalid, "scope_not_verifiable");
        Fails(await strict.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Warehouse, WarehouseX), AccessStatus.Invalid, "scope_not_verifiable");
        Assert.Empty((await Access.GetEffectivePermissionsAsync(member.Id, company)).Grants);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId}=@u", ("u", member.Id)));
        Ok(await strict.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Company, null));
    }

    [PostgresFact]
    public async Task Unknown_ids_wrong_kinds_and_validator_failures_are_refused()
    {
        await StartAccess();
        var company = await NewCompany();
        var super = await NewSuper(company);
        var member = await NewMember(company, super);
        var role = await SeedRole(company, "Reviewer", DocView, StockView);

        Fails(await Access.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Branch, Guid.NewGuid()), AccessStatus.Invalid, "scope_not_verifiable");
        Fails(await Access.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Warehouse, BranchA), AccessStatus.Invalid, "scope_not_verifiable");   // a branch id is not a warehouse
        Fails(await Access.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Branch, WarehouseX), AccessStatus.Invalid, "scope_not_verifiable");
        await using var broken = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog, new ThrowingScopeValidator());
        Fails(await broken.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Branch, BranchA), AccessStatus.Invalid, "scope_not_verifiable");  // fail closed on errors
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ra.Table} WHERE {Ra.UserId}=@u", ("u", member.Id)));

        Ok(await Access.AssignRoleAsync(super.Id, company, member.Id, role, ScopeKind.Branch, BranchA));                                                    // verified id works
        Assert.True(await Allowed(member, company, DocView, ScopeTarget.Branch(BranchA)));
        Assert.False(await Allowed(member, company, DocView, ScopeTarget.Branch(BranchB)));
    }

    [PostgresFact]
    public async Task Scoped_rows_that_bypass_the_service_never_authorize_more_than_their_exact_target()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var member = await NewMember(company, admin);
        var unverified = Guid.NewGuid();
        var docs = await SeedRole(company, "Docs", DocView, DocApprove, StockView);
        await SeedAssignment(member.Id, company, docs, ScopeKind.Branch, unverified);                                            // written behind the service's back
        await SeedAssignment(member.Id, company, PresetRoles.CompanyAdministratorId, ScopeKind.Branch, unverified);              // administrative role at branch scope
        await SeedAssignment(member.Id, company, PresetRoles.AccessAuditorId, ScopeKind.Warehouse, unverified);

        // nothing company-wide, nothing for other targets, no administrative power, no cross-kind use of the same id
        foreach (var p in TestCatalog.All)
        {
            Assert.False(await Allowed(member, company, p.Code), p.Code);
            Assert.False(await Allowed(member, company, p.Code, ScopeTarget.Branch(BranchA)), p.Code);
        }
        Assert.False(await Allowed(member, company, DocView, ScopeTarget.Warehouse(unverified)));
        Assert.False(await Allowed(member, company, StockView, ScopeTarget.Warehouse(unverified)));
        Assert.False(await Allowed(member, company, StockView, ScopeTarget.Branch(unverified)));
        Assert.True(await Allowed(member, company, DocView, ScopeTarget.Branch(unverified)));                                   // exactly its own target only
        Fails(await Access.ListMembersAsync(member.Id, company), AccessStatus.Forbidden);
        Fails(await Access.CreateRoleAsync(member.Id, company, "x", []), AccessStatus.Forbidden);
        Fails(await Access.ListAuditAsync(member.Id, company), AccessStatus.Forbidden);
    }
}
