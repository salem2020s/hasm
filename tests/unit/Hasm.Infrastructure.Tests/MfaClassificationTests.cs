using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 slice S3b-1 (D-165): the ONE classification of privileged permissions. A new permission cannot silently be unprivileged (LEAD D-096).</summary>
public class MfaClassificationTests
{
    [Fact]
    public void Every_resource_of_the_catalog_is_explicitly_classified_and_no_entry_is_stale()
    {
        var resources = PermissionCatalog.Default.Resources.Select(r => r.Key).ToHashSet();
        var unclassified = resources.Where(k => !MfaClassification.RequiresMfaByResource.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(unclassified.Count == 0, "unclassified permission resources (classify them in MfaClassification): " + string.Join(", ", unclassified));
        var stale = MfaClassification.RequiresMfaByResource.Keys.Where(k => !resources.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(stale.Count == 0, "classification entries for resources that no longer exist: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_permission_code_resolves_through_the_classification()
    {
        foreach (var p in PermissionCatalog.Default.All)
            Assert.Equal(MfaClassification.RequiresMfaByResource[p.Resource.Key], MfaClassification.RequiresMfa(p.Code));
    }

    [Theory]
    [InlineData("access.roles.create")]
    [InlineData("access.assignments.create")]
    [InlineData("access.members.delete")]
    [InlineData("access.audit.view")]
    [InlineData("company.settings.update")]
    [InlineData("company.approvalpolicies.update")]
    public void Role_grant_membership_audit_and_security_settings_permissions_require_MFA(string code) => Assert.True(MfaClassification.RequiresMfa(code));

    [Theory]
    [InlineData("company.journal.create")]
    [InlineData("company.items.view")]
    [InlineData("company.ledgerreports.view")]
    public void Ordinary_business_permissions_do_not(string code) => Assert.False(MfaClassification.RequiresMfa(code));

    [Theory]
    [InlineData("")]
    [InlineData("nonsense.permission.view")]
    [InlineData("access.roles.frobnicate")]
    public void An_unknown_permission_code_counts_as_privileged(string code) => Assert.True(MfaClassification.RequiresMfa(code));

    [Fact]
    public void A_role_is_privileged_when_it_is_the_company_administrator_preset_or_holds_any_privileged_permission()
    {
        Assert.True(MfaClassification.IsPrivilegedRole(PresetRoles.CompanyAdministratorKey, []));
        Assert.True(MfaClassification.IsPrivilegedRole(null, ["company.items.view", "access.roles.view"]));
        Assert.False(MfaClassification.IsPrivilegedRole(null, ["company.items.view", "company.journal.create"]));
        Assert.False(MfaClassification.IsPrivilegedRole(null, []));
        Assert.True(PresetRoles.Definitions.Where(d => d.Value.Key == PresetRoles.CompanyAdministratorKey).All(d => MfaClassification.IsPrivilegedRole(d.Value.Key, d.Value.Permissions)));
    }

    [Fact]
    public void Every_preset_that_can_manage_roles_or_assignments_is_privileged()
    {
        foreach (var (_, (key, permissions)) in PresetRoles.Definitions)
            if (permissions.Any(c => c.StartsWith("access.roles.c") || c.StartsWith("access.assignments.c")))
                Assert.True(MfaClassification.IsPrivilegedRole(key, permissions), key);
    }
}
