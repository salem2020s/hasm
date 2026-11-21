namespace Hasm.Infrastructure.Access;

/// <summary>
/// The ONE central definition of "privileged" for MFA enforcement (Phase 4.1, lead decision D-096 under owner decision D-080, fail-closed). A permission or role is privileged when it is the Company Administrator preset
/// or holds ANY permission to manage roles, grants or members, audit, security or approval settings, or any approval/override/reopen/final-close action; when in doubt it counts as privileged.
/// Central (system) administrators are not roles: they are identified by <c>IsIdentityAdministrator</c> and always count. The classification is explicit for every catalog resource, so a resource added to the
/// catalog without a classification is treated as privileged and fails the completeness test until someone classifies it on purpose.
/// </summary>
public static class PrivilegedAccess
{
    /// <summary>Resources whose every action is privileged: role, grant and member management, audit administration, company security settings, approval policies.</summary>
    public static IReadOnlySet<string> PrivilegedResources { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "access.members", "access.roles", "access.assignments", "access.audit", "company.settings", "company.approvalpolicies",
    };

    /// <summary>Resources classified as ordinary at resource level (their privileged ACTIONS, see <see cref="PrivilegedActions"/>, still count). Listing a resource here is a deliberate review decision.</summary>
    public static IReadOnlySet<string> OrdinaryResources { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "company.profile", "company.currencies", "company.config", "company.branches", "company.branchstructure", "company.warehouses", "company.warehousestructure",
        "company.fiscalyears", "company.fiscalperiods", "company.fiscalreopen", "company.accounts", "company.numbering", "company.exchangerates", "company.costcenters",
        "company.journal", "company.journalsoftclosed", "company.journalrate", "company.ledgersettings", "company.ledgerreports", "company.costcenterpolicies", "company.fiscalclosing",
        "company.imports", "company.financialstatements", "company.statementmapping", "company.units", "company.itemgroups", "company.items", "company.barcodes",
        "company.inventorysettings", "company.inventoryaccounting", "company.stockdocs", "company.stockbalance", "company.stocktake", "company.parties", "company.tax",
        "company.partyroles", "company.salesreps", "company.pricing", "company.commercialdocs", "company.attachments", "company.attachmentssensitive",
    };

    /// <summary>Actions that are privileged on every resource: approving (an approver is a sensitive grant), overriding a control, reopening and final-closing a period, approving the negative-stock exception.</summary>
    public static IReadOnlySet<PermissionAction> PrivilegedActions { get; } = new HashSet<PermissionAction>
    {
        PermissionAction.Approve, PermissionAction.Override, PermissionAction.Reopen, PermissionAction.FinalClose, PermissionAction.NegativeException,
    };

    /// <summary>True when the permission code is privileged. An unknown code, or a code of a resource nobody classified, is privileged (fail closed).</summary>
    public static bool IsPrivilegedPermission(string? code)
    {
        if (!PermissionCatalog.Default.TryGet(code, out var permission)) return true;
        var key = permission.Resource.Key;
        if (PrivilegedResources.Contains(key)) return true;
        if (!OrdinaryResources.Contains(key)) return true;
        return PrivilegedActions.Contains(permission.Action);
    }

    /// <summary>True when the role is the Company Administrator preset, or holds at least one privileged permission. A role with no permissions is not privileged.</summary>
    public static bool IsPrivilegedRole(Guid? presetRoleId, IEnumerable<string> permissionCodes)
    {
        if (presetRoleId == PresetRoles.CompanyAdministratorId) return true;
        foreach (var code in permissionCodes) if (IsPrivilegedPermission(code)) return true;
        return false;
    }
}
