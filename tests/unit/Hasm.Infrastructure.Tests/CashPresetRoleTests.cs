using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// X4-PRESET-ROLE (owner Option A, 2026-11-22; plan docs/X4-PRESET-ROLES-PLAN.md section 3.2): the two dedicated cash and bank account presets. Each works on its own, each is privileged (mandatory MFA),
/// neither widens Company Administrator, no preset combines approve with create or execute with approve, and the seed of the migration equals the code definition (also pinned by AccessModelTests).
/// </summary>
public class CashPresetRoleTests
{
    private static IReadOnlySet<string> Of(Guid id) => PresetRoles.Definitions[id].Permissions;

    [Fact]
    public void Cash_Account_Manager_holds_exactly_the_four_configuration_codes_and_not_the_sensitive_view()
    {
        Assert.Equal("cash-account-manager", PresetRoles.Definitions[PresetRoles.CashAccountManagerId].Key);
        Assert.Equal(
            [PermissionCatalog.Codes.CashAccountsActivate, PermissionCatalog.Codes.CashAccountsCreate, PermissionCatalog.Codes.CashAccountsUpdate, PermissionCatalog.Codes.CashAccountsView],
            Of(PresetRoles.CashAccountManagerId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(PermissionCatalog.Codes.CashAccountsSensitiveView, Of(PresetRoles.CashAccountManagerId));
    }

    [Fact]
    public void Cash_Account_Sensitive_Viewer_holds_the_sensitive_view_AND_the_view_it_needs_and_nothing_that_changes_an_account()
    {
        Assert.Equal("cash-account-sensitive-viewer", PresetRoles.Definitions[PresetRoles.CashAccountSensitiveViewerId].Key);
        // the full number needs BOTH codes on the account's branch (CashAccountManagementService); the sensitive code alone would show nothing
        Assert.Equal([PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsSensitiveView], Of(PresetRoles.CashAccountSensitiveViewerId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Of(PresetRoles.CashAccountSensitiveViewerId), c => c is "company.cashaccounts.create" or "company.cashaccounts.update" or "company.cashaccounts.activate");
    }

    [Fact]
    public void Both_presets_are_privileged_so_mandatory_MFA_applies_when_they_are_assigned()
    {
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.CashAccountManagerId, Of(PresetRoles.CashAccountManagerId)));            // update and activate are privileged (D-158)
        Assert.True(PrivilegedAccess.IsPrivilegedRole(PresetRoles.CashAccountSensitiveViewerId, Of(PresetRoles.CashAccountSensitiveViewerId)));   // the sensitive resource is privileged (D-155)
        // the ordinary view alone is not privileged, so the classification really comes from the codes above
        Assert.False(PrivilegedAccess.IsPrivilegedRole(null, [PermissionCatalog.Codes.CashAccountsView]));
    }

    [Fact]
    public void Company_Administrator_is_not_widened_and_holds_no_cash_code()
    {
        var administrator = Of(PresetRoles.CompanyAdministratorId);
        foreach (var code in new[] { PermissionCatalog.Codes.CashAccountsView, PermissionCatalog.Codes.CashAccountsCreate, PermissionCatalog.Codes.CashAccountsUpdate, PermissionCatalog.Codes.CashAccountsActivate, PermissionCatalog.Codes.CashAccountsSensitiveView })
            Assert.DoesNotContain(code, administrator);
    }

    [Fact]
    public void No_preset_holds_an_approve_code_together_with_the_create_code_of_the_same_resource_or_an_execute_code_together_with_an_approve_code()
    {
        foreach (var (id, (key, permissions)) in PresetRoles.Definitions)
        {
            var parsed = permissions.Select(c => PermissionCatalog.Default.TryGet(c, out var p) ? p : throw new InvalidOperationException(c)).ToList();
            foreach (var approve in parsed.Where(p => p.Action == PermissionAction.Approve))
                Assert.DoesNotContain(parsed, p => p.Resource.Key == approve.Resource.Key && p.Action == PermissionAction.Create && id != PresetRoles.CompanyAdministratorId);
            if (id == PresetRoles.CompanyAdministratorId) continue;
            Assert.False(parsed.Any(p => p.Action == PermissionAction.Approve) && parsed.Any(p => p.Action == PermissionAction.Execute), key);
        }
        Assert.Contains(PresetRoles.Definitions.Keys, k => k == PresetRoles.CashAccountManagerId);
    }

    [Fact]
    public void The_new_preset_ids_are_distinct_and_follow_the_existing_series()
    {
        Assert.Equal(PresetRoles.Definitions.Count, PresetRoles.Definitions.Keys.Distinct().Count());
        Assert.Equal("5c1b0f6e-0000-4a00-8000-00000000000a", PresetRoles.CashAccountManagerId.ToString());
        Assert.Equal("5c1b0f6e-0000-4a00-8000-00000000000b", PresetRoles.CashAccountSensitiveViewerId.ToString());
        Assert.Equal(10, PresetRoles.Definitions.Count);
    }
}
