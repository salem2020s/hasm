using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.Identity;

/// <summary>Why a user is subject to the central, always-on MFA requirement (Phase 4.1 section 3, D-080 (1); D-164, D-165).</summary>
[Flags]
public enum MfaMandatoryReason
{
    None = 0,
    IdentityAdministrator = 1,
    CompanyAdministratorPreset = 2,
    PrivilegedRole = 4,
}

/// <summary>The central requirement of one user, computed from the user's union of privileges across ALL companies (not from the selected company).</summary>
public sealed record MfaRequirement(MfaMandatoryReason Reasons)
{
    public bool Mandatory => Reasons != MfaMandatoryReason.None;
}

/// <summary>
/// The ONE classification of permissions that count as privileged for MFA (LEAD D-096, D-165; fail-closed): the catalog flag <c>RequiresMfa</c> lives here, per resource key, and a unit test
/// enumerates the whole <see cref="PermissionCatalog"/> and fails for any resource that is not explicitly classified, so a new permission cannot silently be unprivileged. A role is
/// privileged when it holds ANY privileged permission or is the Company Administrator preset. A permission code that is unknown to the catalog counts as privileged (when in doubt it counts).
/// </summary>
public static class MfaClassification
{
    /// <summary>Resource key -> whether every permission of the resource requires MFA of its holder.</summary>
    public static readonly IReadOnlyDictionary<string, bool> RequiresMfaByResource = new Dictionary<string, bool>
    {
        // roles, grants, membership and the audit of access administration (D-096: "manage roles or grants", "audit administration")
        ["access.members"] = true,
        ["access.roles"] = true,
        ["access.assignments"] = true,
        ["access.audit"] = true,
        // company settings and the configuration of approvals and retention change who may do what or how long data is kept (D-096: "security settings"); in doubt they count
        ["company.settings"] = true,
        ["company.approvalpolicies"] = true,
        ["company.fiscalreopen"] = true,
        ["company.attachmentretention"] = true,
        ["company.attachmentssensitive"] = true,
        // everything else is classified explicitly as not privileged
        ["company.profile"] = false,
        ["company.currencies"] = false,
        ["company.config"] = false,
        ["company.branches"] = false,
        ["company.branchstructure"] = false,
        ["company.warehouses"] = false,
        ["company.warehousestructure"] = false,
        ["company.fiscalyears"] = false,
        ["company.fiscalperiods"] = false,
        ["company.accounts"] = false,
        ["company.numbering"] = false,
        ["company.exchangerates"] = false,
        ["company.costcenters"] = false,
        ["company.journal"] = false,
        ["company.journalsoftclosed"] = false,
        ["company.journalrate"] = false,
        ["company.ledgersettings"] = false,
        ["company.ledgerreports"] = false,
        ["company.costcenterpolicies"] = false,
        ["company.fiscalclosing"] = false,
        ["company.imports"] = false,
        ["company.financialstatements"] = false,
        ["company.statementmapping"] = false,
        ["company.units"] = false,
        ["company.itemgroups"] = false,
        ["company.items"] = false,
        ["company.barcodes"] = false,
        ["company.inventorysettings"] = false,
        ["company.inventoryaccounting"] = false,
        ["company.stockdocs"] = false,
        ["company.stockbalance"] = false,
        ["company.stocktake"] = false,
        ["company.parties"] = false,
        ["company.tax"] = false,
        ["company.partyroles"] = false,
        ["company.salesreps"] = false,
        ["company.pricing"] = false,
        ["company.commercialdocs"] = false,
        ["company.attachments"] = false,
    };

    /// <summary>True when holding the permission requires MFA. An unclassified or unknown code counts as privileged.</summary>
    public static bool RequiresMfa(string permissionCode)
    {
        if (!PermissionCatalog.Default.TryGet(permissionCode, out var permission)) return true;
        return !RequiresMfaByResource.TryGetValue(permission.Resource.Key, out var requires) || requires;
    }

    /// <summary>A role is privileged when it is the Company Administrator preset or holds any privileged permission.</summary>
    public static bool IsPrivilegedRole(string? presetKey, IEnumerable<string> permissionCodes) =>
        presetKey == PresetRoles.CompanyAdministratorKey || permissionCodes.Any(RequiresMfa);
}
