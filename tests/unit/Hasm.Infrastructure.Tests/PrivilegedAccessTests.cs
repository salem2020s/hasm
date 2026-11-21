using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 (D-096): the central, fail-closed definition of a privileged permission or role.</summary>
public class PrivilegedAccessTests
{
    [Fact]
    public void Every_catalog_resource_is_classified_exactly_once_and_nothing_classified_is_missing_from_the_catalog()
    {
        var catalog = PermissionCatalog.Default.Resources.Select(r => r.Key).ToHashSet();
        var privileged = PrivilegedAccess.PrivilegedResources;
        var ordinary = PrivilegedAccess.OrdinaryResources;
        Assert.Empty(privileged.Intersect(ordinary));                                                         // never both
        Assert.Empty(catalog.Except(privileged).Except(ordinary));                                            // a new catalog resource must be classified on purpose
        Assert.Empty(privileged.Union(ordinary).Except(catalog));                                             // no stale entry
    }

    [Fact]
    public void The_exact_classification_is_pinned_so_flipping_any_entry_fails_a_test()
    {
        // review L1 of PR #172: an explicit expected list, not a rule derived from the table under test. Changing the classification is a deliberate, reviewed change to THIS list.
        Assert.Equal(
            ["access.assignments", "access.audit", "access.members", "access.roles", "company.approvalpolicies", "company.attachmentretention", "company.attachmentssensitive",
             "company.costcenterpolicies", "company.journalsoftclosed", "company.ledgersettings", "company.settings"],
            PrivilegedAccess.PrivilegedResources.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(["company.imports.configure"], PrivilegedAccess.PrivilegedPermissions.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            [PermissionAction.Approve, PermissionAction.FinalClose, PermissionAction.NegativeException, PermissionAction.Override, PermissionAction.Reopen],
            PrivilegedAccess.PrivilegedActions.OrderBy(a => a.ToString(), StringComparer.Ordinal).ToArray());
        Assert.All(PermissionCatalog.Default.Resources.Where(r => r.Key.StartsWith("access.", StringComparison.Ordinal)), r => Assert.Contains(r.Key, PrivilegedAccess.PrivilegedResources));   // every access.* resource is privileged
    }

    [Fact]
    public void Role_grant_member_audit_and_security_settings_permissions_are_privileged()
    {
        foreach (var code in PermissionCatalog.Default.All.Where(p => PrivilegedAccess.PrivilegedResources.Contains(p.Resource.Key)).Select(p => p.Code))
            Assert.True(PrivilegedAccess.IsPrivilegedPermission(code), code);
        Assert.True(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.MembersView));            // even viewing is classified: when in doubt it counts
    }

    [Fact]
    public void Approval_override_reopen_final_close_and_negative_exception_are_privileged_on_every_resource()
    {
        foreach (var p in PermissionCatalog.Default.All.Where(p => PrivilegedAccess.PrivilegedActions.Contains(p.Action)))
            Assert.True(PrivilegedAccess.IsPrivilegedPermission(p.Code), p.Code);
        Assert.True(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.JournalApprove));
        Assert.True(PrivilegedAccess.IsPrivilegedPermission(PermissionCatalog.Codes.FiscalReopenApprove));
    }

    [Fact]
    public void Ordinary_business_permissions_are_not_privileged()
    {
        foreach (var code in new[] { "company.items.view", "company.items.create", "company.journal.create", "company.journal.post", "company.stockdocs.submit", "company.ledgerreports.export", "company.pricing.configure" })
            Assert.False(PrivilegedAccess.IsPrivilegedPermission(code), code);
    }

    [Theory]
    [InlineData("company.attachmentssensitive.view")]
    [InlineData("company.attachmentssensitive.upload")]            // authorizes the encryption-key rewrap of attachments (review M3 of PR #133)
    [InlineData("company.journalsoftclosed.post")]                  // override-like: posting into a soft-closed period
    [InlineData("company.imports.configure")]                       // import maintenance, including the key rewrap of imported files
    [InlineData("company.attachmentretention.hold")]               // Phase R R1: retention policy, legal holds and attachment deletion
    [InlineData("company.attachmentretention.execute")]
    public void Permissions_that_authorize_key_rewrap_or_override_a_control_are_privileged(string code) => Assert.True(PrivilegedAccess.IsPrivilegedPermission(code), code);

    [Theory]
    [InlineData("company.imports.view")]
    [InlineData("company.attachments.view")]
    [InlineData("company.attachments.upload")]
    public void Their_ordinary_neighbours_stay_ordinary(string code) => Assert.False(PrivilegedAccess.IsPrivilegedPermission(code), code);

    [Fact]
    public void Unknown_malformed_and_unclassified_codes_fail_closed()
    {
        foreach (var code in new[] { null, "", "nonsense", "company.unknownresource.view", "access.roles.frobnicate", " " })
            Assert.True(PrivilegedAccess.IsPrivilegedPermission(code), code ?? "(null)");
    }

    [Fact]
    public void A_role_is_privileged_when_it_is_the_company_administrator_preset_or_holds_any_privileged_permission()
    {
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.CompanyAdministratorId, []));
        Assert.True(PrivilegedAccess.IsPrivilegedRole(null, ["company.items.view", "access.roles.view"]));
        Assert.True(PrivilegedAccess.IsPrivilegedRole(null, ["company.items.view", "not.a.permission"]));
        Assert.False(PrivilegedAccess.IsPrivilegedRole(null, ["company.items.view", "company.items.create"]));
        Assert.False(PrivilegedAccess.IsPrivilegedRole(null, []));
    }

    [Fact]
    public void Every_preset_role_is_classified_as_expected()
    {
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.CompanyAdministratorId, PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions));
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.AccessManagerId, PresetRoles.Definitions[PresetRoles.AccessManagerId].Permissions));
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.AccessAuditorId, PresetRoles.Definitions[PresetRoles.AccessAuditorId].Permissions));
        foreach (var approver in new[] { PresetRoles.FiscalApproverId, PresetRoles.JournalApproverId, PresetRoles.ImportApproverId, PresetRoles.CommercialApproverId })
            Assert.True(PrivilegedAccess.IsPrivilegedRole(approver, PresetRoles.Definitions[approver].Permissions), approver.ToString());
    }
}
