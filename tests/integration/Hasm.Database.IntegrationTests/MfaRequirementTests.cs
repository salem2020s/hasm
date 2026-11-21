using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;
using Ra = Hasm.Infrastructure.SystemDatabase.SystemSchema.RoleAssignment;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S3b-1 against real PostgreSQL: the CENTRAL MFA requirement is computed from the user's union of privileges across ALL companies (D-080 (1), D-165). Disposable cluster only.</summary>
public class MfaRequirementTests : AccessTestBase
{
    private async Task<MfaRequirement> RequirementOf(UserRecord user) => await Store.GetMfaRequirementAsync(user.Id);

    [PostgresFact]
    public async Task A_plain_user_and_a_user_with_only_business_roles_have_no_central_requirement()
    {
        await StartAccess();
        var company = await NewCompany();
        var plain = await NewPerson();
        Assert.False((await RequirementOf(plain)).Mandatory);
        var clerk = await NewMember(company, await NewAdmin(company, "boot"), "clerk");
        await SeedAssignment(clerk.Id, company, await SeedRole(company, "Clerk", "company.journal.create", "company.items.view"));
        Assert.False((await RequirementOf(clerk)).Mandatory);
    }

    [PostgresFact]
    public async Task An_identity_administrator_is_subject_even_with_no_company()
    {
        await StartAccess();
        var admin = await NewUser("sysadmin", admin: true);
        var r = await RequirementOf(admin);
        Assert.True(r.Mandatory);
        Assert.Equal(MfaMandatoryReason.IdentityAdministrator, r.Reasons);
    }

    [PostgresFact]
    public async Task A_company_administrator_in_ANY_company_is_subject_not_only_in_the_selected_one()
    {
        await StartAccess();
        var companyA = await NewCompany();
        var companyB = await NewCompany();
        var admin = await NewAdmin(companyB);                                                                   // administrator of B only; A is not even a membership
        var r = await RequirementOf(admin);
        Assert.True(r.Reasons.HasFlag(MfaMandatoryReason.CompanyAdministratorPreset));
        Assert.True(r.Reasons.HasFlag(MfaMandatoryReason.PrivilegedRole));
        Assert.NotEqual(companyA, companyB);
    }

    [PostgresFact]
    public async Task A_custom_role_with_a_privileged_permission_makes_the_holder_subject()
    {
        await StartAccess();
        var company = await NewCompany();
        var user = await NewMember(company, await NewAdmin(company, "boot"), "manager");
        await SeedAssignment(user.Id, company, await SeedRole(company, "Grants", "access.assignments.create"));
        var r = await RequirementOf(user);
        Assert.Equal(MfaMandatoryReason.PrivilegedRole, r.Reasons);
    }

    [PostgresFact]
    public async Task A_revoked_assignment_or_a_deactivated_role_no_longer_counts()
    {
        await StartAccess();
        var company = await NewCompany();
        var revoked = await NewMember(company, await NewAdmin(company, "boot"), "revoked");
        var role = await SeedRole(company, "Grants", "access.roles.create");
        await SeedAssignment(revoked.Id, company, role);
        Assert.True((await RequirementOf(revoked)).Mandatory);
        await Exec($"UPDATE {Ra.Table} SET {Ra.RevokedAtUtc} = now() WHERE {Ra.UserId} = @u", ("u", revoked.Id));
        Assert.False((await RequirementOf(revoked)).Mandatory);                                                 // recomputed on every call: a revoke applies at once

        var inactive = await NewMember(company, await NewAdmin(company, "boot2"), "inactive");
        var role2 = await SeedRole(company, "Grants2", "access.roles.create");
        await SeedAssignment(inactive.Id, company, role2);
        Assert.True((await RequirementOf(inactive)).Mandatory);
        await Exec($"UPDATE {Ro.Table} SET {Ro.IsActive} = false WHERE {Ro.Id} = @r", ("r", role2));
        Assert.False((await RequirementOf(inactive)).Mandatory);
    }

    [PostgresFact]
    public async Task A_role_with_no_permissions_and_an_unknown_user_have_no_requirement()
    {
        await StartAccess();
        var company = await NewCompany();
        var user = await NewMember(company, await NewAdmin(company, "boot"), "empty");
        await SeedAssignment(user.Id, company, await SeedRole(company, "Empty"));
        Assert.False((await RequirementOf(user)).Mandatory);
        Assert.False((await Store.GetMfaRequirementAsync(Guid.NewGuid())).Mandatory);
    }
}
