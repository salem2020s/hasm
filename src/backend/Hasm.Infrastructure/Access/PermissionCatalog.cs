using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Access;

/// <summary>What a permission allows. Names are persisted as lower-case suffixes of permission codes; never rename.</summary>
public enum PermissionAction { View, Create, Update, Delete, Approve, Post, Export, Print, Activate, Designate, Close, FinalClose, Reopen, Recode, Restructure, Configure, Submit, Reverse, Override, Upload, Apply, Cancel }

/// <summary>
/// Granularity at which a role assignment applies. Numeric values are persisted; never renumber.
/// Company covers everything in the company; Branch and Warehouse cover only the identified branch/warehouse.
/// </summary>
public enum ScopeKind : short { Company = 0, Branch = 1, Warehouse = 2, CostCenter = 3 }

/// <summary>A concrete target of an authorization check (what the caller wants to act on).</summary>
public readonly record struct ScopeTarget(ScopeKind Kind, Guid? Id)
{
    public static ScopeTarget Company { get; } = new(ScopeKind.Company, null);
    public static ScopeTarget Branch(Guid id) => new(ScopeKind.Branch, id);
    public static ScopeTarget Warehouse(Guid id) => new(ScopeKind.Warehouse, id);
    public static ScopeTarget CostCenter(Guid id) => new(ScopeKind.CostCenter, id);

    /// <summary>A Company target has no id; Branch and Warehouse targets require a non-empty id.</summary>
    public bool IsWellFormed => Kind switch
    {
        ScopeKind.Company => Id is null,
        ScopeKind.Branch or ScopeKind.Warehouse or ScopeKind.CostCenter => Id is { } id && id != Guid.Empty,
        _ => false,
    };
}

/// <summary>A resource whose permissions can be granted. <see cref="Scope"/> is the narrowest scope the resource's data lives at.</summary>
/// <param name="AcceptsCostCenter">The resource also accepts cost-center-scoped grants (journal entries and ledger reports carry cost-center allocations).</param>
public sealed record PermissionResource(string Key, ScopeKind Scope, IReadOnlyList<PermissionAction> Actions, bool AcceptsCostCenter = false);

/// <summary>A structured permission: <c>resource.action</c>, for example <c>access.roles.update</c>.</summary>
public sealed record Permission(PermissionResource Resource, PermissionAction Action)
{
    public string Code => $"{Resource.Key}.{Action.ToString().ToLowerInvariant()}";
    public override string ToString() => Code;
}

/// <summary>
/// The structured permission catalog. The catalog is code: roles reference permissions by code, unknown codes are
/// rejected and never grant anything. Later phases register their resources here (together with a migration that
/// extends the preset roles); this phase registers only the resources of access administration itself.
/// </summary>
public sealed partial class PermissionCatalog
{
    private readonly Dictionary<string, Permission> byCode;

    public IReadOnlyList<PermissionResource> Resources { get; }
    public IReadOnlyCollection<Permission> All => byCode.Values;

    public PermissionCatalog(IEnumerable<PermissionResource> resources)
    {
        Resources = [.. resources];
        byCode = [];
        foreach (var resource in Resources)
        {
            if (!ResourceKey().IsMatch(resource.Key)) throw new ArgumentException("Invalid resource key.", nameof(resources));
            foreach (var action in resource.Actions)
            {
                var permission = new Permission(resource, action);
                if (!byCode.TryAdd(permission.Code, permission)) throw new ArgumentException("Duplicate permission.", nameof(resources));
            }
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*){0,2}$")]
    private static partial Regex ResourceKey();

    public bool TryGet(string? code, out Permission permission)
    {
        permission = null!;
        return code is not null && byCode.TryGetValue(code, out permission!);
    }

    /// <summary>Resources of access administration (this phase). All of them are company-wide administrative data.</summary>
    public static PermissionCatalog Default { get; } = new(
    [
        new("access.members", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Delete]),
        new("access.roles", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Delete]),
        new("access.assignments", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Delete]),
        new("access.audit", ScopeKind.Company, [PermissionAction.View]),
        // Company administration (Phase 3.1A-2, system migration 0006).
        new("company.profile", ScopeKind.Company, [PermissionAction.View, PermissionAction.Update]),
        new("company.settings", ScopeKind.Company, [PermissionAction.View, PermissionAction.Update]),
        new("company.currencies", ScopeKind.Company, [PermissionAction.View, PermissionAction.Update]),
        new("company.config", ScopeKind.Company, [PermissionAction.View]),
        // Branch management (Phase 3.1B, system migration 0007). Viewing and editing a branch are exercisable through a branch-scoped assignment
        // (only for that branch); creating, activating/deactivating and designating the main branch change the company structure and are company-wide only.
        new("company.branches", ScopeKind.Branch, [PermissionAction.View, PermissionAction.Update]),
        new("company.branchstructure", ScopeKind.Company, [PermissionAction.Create, PermissionAction.Activate, PermissionAction.Designate]),
        // Warehouse management (Phase 3.1C, system migration 0008). Viewing and editing a warehouse are exercisable through a warehouse-scoped assignment
        // (only for that warehouse; no inheritance from branch scope); creating and activating/deactivating change the company structure: company-wide only.
        new("company.warehouses", ScopeKind.Warehouse, [PermissionAction.View, PermissionAction.Update]),
        new("company.warehousestructure", ScopeKind.Company, [PermissionAction.Create, PermissionAction.Activate]),
        // Fiscal calendar (Phase 3.1D, system migration 0009): company-wide only. Soft closing, reopening a soft close and final closing are separate permissions.
        new("company.fiscalyears", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Close]),
        new("company.fiscalperiods", ScopeKind.Company, [PermissionAction.Close, PermissionAction.FinalClose, PermissionAction.Reopen]),
        // Exceptional reopening of a Final Closed period or a closed year is an APPROVAL workflow: Create = request it, Approve = approve (and execute) it.
        // Approve is in no preset except Fiscal Approver, and never in the Company Administrator preset; self-approval is refused by the service and by PostgreSQL.
        new("company.fiscalreopen", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Approve]),
        // Chart of Accounts (Phase 3.2A, system migration 0010): company-wide only (the chart belongs to the whole company). Re-coding an account and moving it in the
        // tree are separate, controlled permissions; configuring the automatic code assignment is another.
        new("company.accounts", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Activate, PermissionAction.Recode, PermissionAction.Restructure, PermissionAction.Configure]),
        // General Ledger foundation (Phase 3.2B, system migration 0011), company-wide only. Numbering: view the rules, configure them.
        new("company.numbering", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure]),
        // Exchange rates: manual company-wide master data; entering a rate is the only write (append-only history).
        new("company.exchangerates", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create]),
        // Cost centers: company-wide, like the chart of accounts; re-coding and restructuring are separate, controlled permissions.
        new("company.costcenters", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Activate, PermissionAction.Recode, PermissionAction.Restructure]),
        // Approval policies (configuration only). Approving a DOCUMENT is a separate journal permission (slice S5) that is never in the Company Administrator preset.
        new("company.approvalpolicies", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Activate]),
        // Journal (slice S5): branch-scoped. Approve is NOT in the Company Administrator preset (separation of duties; it belongs to Journal Approver and custom roles).
        // Posting into a Soft Closed period and overriding an exchange rate are separate company-wide permissions.
        new("company.journal", ScopeKind.Branch, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Submit, PermissionAction.Approve, PermissionAction.Post, PermissionAction.Reverse], AcceptsCostCenter: true),
        new("company.journalsoftclosed", ScopeKind.Company, [PermissionAction.Post]),
        new("company.journalrate", ScopeKind.Company, [PermissionAction.Override]),
        new("company.ledgersettings", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure]),
        // Ledger reports (slice S6): branch-scoped (a branch grant sees only its own branches); export is separate.
        new("company.ledgerreports", ScopeKind.Branch, [PermissionAction.View, PermissionAction.Export], AcceptsCostCenter: true),
        // Cost-center accounting policies (3.2C S1): which accounts require a cost center and which branches a cost center is eligible for. Company-wide.
        new("company.costcenterpolicies", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure]),
        // Fiscal-year closing and opening balances (3.2C S3): view/preview, configure the profit and loss account, generate the closing/opening entries (create), and reverse them. Company-wide.
        new("company.fiscalclosing", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure, PermissionAction.Create, PermissionAction.Reverse]),
        // Generic import framework (3.2D): staging, validation, dry run, independent approval and application of imported data. Company-wide; each adapter additionally requires its own business permission.
        new("company.imports", ScopeKind.Company, [PermissionAction.View, PermissionAction.Upload, PermissionAction.Configure, PermissionAction.Submit, PermissionAction.Approve, PermissionAction.Apply, PermissionAction.Cancel]),
        // Financial statements (3.3): profit and loss, balance sheet, trading account. Branch-scoped like the ledger reports (a branch grant sees only its own branches); export is separate. Distinct from the ledger-report permissions.
        new("company.financialstatements", ScopeKind.Branch, [PermissionAction.View, PermissionAction.Export], AcceptsCostCenter: true),
        // The classification (statement mapping) that decides where each account appears in the statements. Company-wide; changing it is audited and creates a new version.
        new("company.statementmapping", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure]),
        // Inventory master data (3.4): company-wide (item data is not owned by a branch or warehouse). Stock movement permissions belong to the later stock phase.
        new("company.units", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Activate]),
        new("company.itemgroups", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Activate]),
        new("company.items", ScopeKind.Company, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Activate]),
        new("company.barcodes", ScopeKind.Company, [PermissionAction.Create, PermissionAction.Activate]),
        new("company.inventorysettings", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure]),
        // Stock movement documents (3.5): warehouse-scoped like warehouses (a warehouse grant reaches only that warehouse, a company grant all of them; no inheritance from branches). A transfer needs the permission on BOTH warehouses.
        // Approve is held by the Company Administrator preset, but separation of duties (the approver is never the submitter) is enforced by the service AND by PostgreSQL.
        // Inventory accounting configuration (3.7A): which ledger accounts hold inventory and take the other side of stock movements. Company-wide; configuring it is audited and versioned.
        new("company.inventoryaccounting", ScopeKind.Company, [PermissionAction.View, PermissionAction.Configure]),
        new("company.stockdocs", ScopeKind.Warehouse, [PermissionAction.View, PermissionAction.Create, PermissionAction.Update, PermissionAction.Submit, PermissionAction.Approve, PermissionAction.Cancel, PermissionAction.Post, PermissionAction.Reverse]),
        new("company.stockbalance", ScopeKind.Warehouse, [PermissionAction.View]),
    ]);

    public static class Codes
    {
        public const string MembersView = "access.members.view";
        public const string MembersCreate = "access.members.create";
        public const string MembersDelete = "access.members.delete";
        public const string RolesView = "access.roles.view";
        public const string RolesCreate = "access.roles.create";
        public const string RolesUpdate = "access.roles.update";
        public const string RolesDelete = "access.roles.delete";
        public const string AssignmentsView = "access.assignments.view";
        public const string AssignmentsCreate = "access.assignments.create";
        public const string AssignmentsDelete = "access.assignments.delete";
        public const string AuditView = "access.audit.view";
        public const string ProfileView = "company.profile.view";
        public const string ProfileUpdate = "company.profile.update";
        public const string SettingsView = "company.settings.view";
        public const string SettingsUpdate = "company.settings.update";
        public const string CurrenciesView = "company.currencies.view";
        public const string CurrenciesUpdate = "company.currencies.update";
        public const string ConfigView = "company.config.view";
        public const string BranchesView = "company.branches.view";
        public const string BranchesUpdate = "company.branches.update";
        public const string BranchCreate = "company.branchstructure.create";
        public const string BranchActivate = "company.branchstructure.activate";
        public const string BranchDesignate = "company.branchstructure.designate";
        public const string WarehousesView = "company.warehouses.view";
        public const string WarehousesUpdate = "company.warehouses.update";
        public const string WarehouseCreate = "company.warehousestructure.create";
        public const string WarehouseActivate = "company.warehousestructure.activate";
        public const string FiscalYearsView = "company.fiscalyears.view";
        public const string FiscalYearsCreate = "company.fiscalyears.create";
        public const string FiscalYearsClose = "company.fiscalyears.close";
        public const string FiscalPeriodsClose = "company.fiscalperiods.close";
        public const string FiscalPeriodsFinalClose = "company.fiscalperiods.finalclose";
        public const string FiscalPeriodsReopen = "company.fiscalperiods.reopen";
        public const string FiscalReopenView = "company.fiscalreopen.view";
        public const string FiscalReopenRequest = "company.fiscalreopen.create";
        public const string FiscalReopenApprove = "company.fiscalreopen.approve";
        public const string AccountsView = "company.accounts.view";
        public const string AccountsCreate = "company.accounts.create";
        public const string AccountsUpdate = "company.accounts.update";
        public const string AccountsActivate = "company.accounts.activate";
        public const string AccountsRecode = "company.accounts.recode";
        public const string AccountsRestructure = "company.accounts.restructure";
        public const string AccountsConfigure = "company.accounts.configure";
        public const string NumberingView = "company.numbering.view";
        public const string NumberingConfigure = "company.numbering.configure";
        public const string ExchangeRatesView = "company.exchangerates.view";
        public const string ExchangeRatesCreate = "company.exchangerates.create";
        public const string CostCentersView = "company.costcenters.view";
        public const string CostCentersCreate = "company.costcenters.create";
        public const string CostCentersUpdate = "company.costcenters.update";
        public const string CostCentersActivate = "company.costcenters.activate";
        public const string CostCentersRecode = "company.costcenters.recode";
        public const string CostCentersRestructure = "company.costcenters.restructure";
        public const string ApprovalPoliciesView = "company.approvalpolicies.view";
        public const string ApprovalPoliciesCreate = "company.approvalpolicies.create";
        public const string ApprovalPoliciesUpdate = "company.approvalpolicies.update";
        public const string ApprovalPoliciesActivate = "company.approvalpolicies.activate";
        public const string JournalView = "company.journal.view";
        public const string JournalCreate = "company.journal.create";
        public const string JournalUpdate = "company.journal.update";
        public const string JournalSubmit = "company.journal.submit";
        public const string JournalApprove = "company.journal.approve";
        public const string JournalPost = "company.journal.post";
        public const string JournalReverse = "company.journal.reverse";
        public const string JournalSoftClosedPost = "company.journalsoftclosed.post";
        public const string JournalRateOverride = "company.journalrate.override";
        public const string LedgerSettingsView = "company.ledgersettings.view";
        public const string LedgerSettingsConfigure = "company.ledgersettings.configure";
        public const string LedgerReportsView = "company.ledgerreports.view";
        public const string LedgerReportsExport = "company.ledgerreports.export";
        public const string FinancialStatementsView = "company.financialstatements.view";
        public const string FinancialStatementsExport = "company.financialstatements.export";
        public const string StatementMappingView = "company.statementmapping.view";
        public const string StatementMappingConfigure = "company.statementmapping.configure";
        public const string UnitsView = "company.units.view";
        public const string UnitsCreate = "company.units.create";
        public const string UnitsUpdate = "company.units.update";
        public const string UnitsActivate = "company.units.activate";
        public const string ItemGroupsView = "company.itemgroups.view";
        public const string ItemGroupsCreate = "company.itemgroups.create";
        public const string ItemGroupsUpdate = "company.itemgroups.update";
        public const string ItemGroupsActivate = "company.itemgroups.activate";
        public const string ItemsView = "company.items.view";
        public const string ItemsCreate = "company.items.create";
        public const string ItemsUpdate = "company.items.update";
        public const string ItemsActivate = "company.items.activate";
        public const string BarcodesCreate = "company.barcodes.create";
        public const string BarcodesActivate = "company.barcodes.activate";
        public const string InventorySettingsView = "company.inventorysettings.view";
        public const string InventorySettingsConfigure = "company.inventorysettings.configure";
        public const string InventoryAccountingView = "company.inventoryaccounting.view";
        public const string InventoryAccountingConfigure = "company.inventoryaccounting.configure";
        public const string StockDocsView = "company.stockdocs.view";
        public const string StockDocsCreate = "company.stockdocs.create";
        public const string StockDocsUpdate = "company.stockdocs.update";
        public const string StockDocsSubmit = "company.stockdocs.submit";
        public const string StockDocsApprove = "company.stockdocs.approve";
        public const string StockDocsCancel = "company.stockdocs.cancel";
        public const string StockDocsPost = "company.stockdocs.post";
        public const string StockDocsReverse = "company.stockdocs.reverse";
        public const string StockBalanceView = "company.stockbalance.view";
        public const string CostCenterPoliciesView = "company.costcenterpolicies.view";
        public const string CostCenterPoliciesConfigure = "company.costcenterpolicies.configure";
        public const string FiscalClosingView = "company.fiscalclosing.view";
        public const string FiscalClosingConfigure = "company.fiscalclosing.configure";
        public const string FiscalClosingCreate = "company.fiscalclosing.create";
        public const string FiscalClosingReverse = "company.fiscalclosing.reverse";
        public const string ImportsView = "company.imports.view";
        public const string ImportsUpload = "company.imports.upload";
        public const string ImportsConfigure = "company.imports.configure";
        public const string ImportsSubmit = "company.imports.submit";
        public const string ImportsApprove = "company.imports.approve";
        public const string ImportsApply = "company.imports.apply";
        public const string ImportsCancel = "company.imports.cancel";
    }

    public Permission Get(string code) =>
        TryGet(code, out var p) ? p : throw new KeyNotFoundException("Unknown permission.");
}

/// <summary>Preset roles shipped with the software (database rows are seeded by migration 0004 and the later versioned migrations, and must match this list).</summary>
public static class PresetRoles
{
    public static readonly Guid CompanyAdministratorId = new("5c1b0f6e-0000-4a00-8000-000000000001");
    public static readonly Guid AccessManagerId = new("5c1b0f6e-0000-4a00-8000-000000000002");
    public static readonly Guid AccessAuditorId = new("5c1b0f6e-0000-4a00-8000-000000000003");
    public static readonly Guid FiscalApproverId = new("5c1b0f6e-0000-4a00-8000-000000000004");
    public static readonly Guid JournalApproverId = new("5c1b0f6e-0000-4a00-8000-000000000005");
    public static readonly Guid ImportApproverId = new("5c1b0f6e-0000-4a00-8000-000000000006");

    public const string CompanyAdministratorKey = "company-administrator";

    public static IReadOnlyDictionary<Guid, (string Key, IReadOnlySet<string> Permissions)> Definitions { get; } =
        new Dictionary<Guid, (string, IReadOnlySet<string>)>
        {
            // The approval of exceptional reopenings is deliberately outside this preset (segregation of duties); it belongs to Fiscal Approver.
            [CompanyAdministratorId] = (CompanyAdministratorKey, PermissionCatalog.Default.All.Select(p => p.Code).Except([PermissionCatalog.Codes.FiscalReopenApprove, PermissionCatalog.Codes.JournalApprove, PermissionCatalog.Codes.ImportsApprove]).ToHashSet()),
            [AccessManagerId] = ("access-manager", new HashSet<string>
            {
                PermissionCatalog.Codes.MembersView, PermissionCatalog.Codes.MembersCreate, PermissionCatalog.Codes.MembersDelete,
                PermissionCatalog.Codes.RolesView,
                PermissionCatalog.Codes.AssignmentsView, PermissionCatalog.Codes.AssignmentsCreate, PermissionCatalog.Codes.AssignmentsDelete,
            }),
            [FiscalApproverId] = ("fiscal-approver", new HashSet<string>
            {
                PermissionCatalog.Codes.FiscalReopenApprove, PermissionCatalog.Codes.FiscalReopenView, PermissionCatalog.Codes.FiscalYearsView,
            }),
            [JournalApproverId] = ("journal-approver", new HashSet<string> { PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalApprove }),
            [ImportApproverId] = ("import-approver", new HashSet<string> { PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsApprove }),
            [AccessAuditorId] = ("access-auditor", new HashSet<string>
            {
                PermissionCatalog.Codes.MembersView, PermissionCatalog.Codes.RolesView,
                PermissionCatalog.Codes.AssignmentsView, PermissionCatalog.Codes.AuditView, PermissionCatalog.Codes.ConfigView,
            }),
        };
}

/// <summary>One role grant held by a user: the role's permission codes at an assignment scope.</summary>
public sealed record GrantedRole(Guid AssignmentId, Guid RoleId, ScopeKind Scope, Guid? ScopeId, IReadOnlySet<string> Permissions);

/// <summary>
/// The permissions a user effectively holds in one company, evaluated deny-by-default. Evaluation rules:
/// a permission is held for a target only if some active assignment's role contains it AND the assignment scope covers the target.
/// Company-wide assignments cover every target; Branch/Warehouse assignments cover only their own id.
/// Permissions of company-level resources (<see cref="ScopeKind.Company"/>) are never exercisable through a narrower assignment;
/// a Branch-level resource accepts company-wide or Branch targets/assignments; a Warehouse-level resource accepts company-wide or Warehouse ones.
/// Unknown permission codes and malformed targets are denied.
/// </summary>
public sealed class EffectivePermissions(PermissionCatalog catalog, IReadOnlyList<GrantedRole> grants)
{
    public IReadOnlyList<GrantedRole> Grants { get; } = grants;

    public bool Has(string code, ScopeTarget target)
    {
        if (!catalog.TryGet(code, out var permission) || !target.IsWellFormed) return false;
        // The target must be at a granularity the resource knows (a branch target for a company-level resource is a caller bug: deny).
        if (target.Kind != ScopeKind.Company && target.Kind != permission.Resource.Scope && !(target.Kind == ScopeKind.CostCenter && permission.Resource.AcceptsCostCenter)) return false;
        foreach (var grant in Grants)
        {
            if (!grant.Permissions.Contains(code)) continue;
            if (grant.Scope == ScopeKind.Company) return true;
            if (grant.Scope == target.Kind && grant.ScopeId == target.Id && (permission.Resource.Scope == grant.Scope || (grant.Scope == ScopeKind.CostCenter && permission.Resource.AcceptsCostCenter))) return true;
        }
        return false;
    }

    /// <summary>True when the user holds the permission for every possible target in the company (a company-wide grant).</summary>
    public bool HasCompanyWide(string code) => Has(code, ScopeTarget.Company);

    /// <summary>
    /// "Cannot grant what you do not possess": true when every permission in <paramref name="permissions"/> is held for
    /// every target that a grant at <paramref name="scope"/> would cover. Unknown codes are never possessed.
    /// </summary>
    public bool CanGrant(IEnumerable<string> permissions, ScopeTarget scope)
    {
        foreach (var code in permissions)
        {
            if (!catalog.TryGet(code, out var permission)) return false;
            // Under a narrower assignment, permissions of coarser resources are inert (never exercisable), so granting them escalates nothing.
            if (scope.Kind != ScopeKind.Company && permission.Resource.Scope != scope.Kind && !(scope.Kind == ScopeKind.CostCenter && permission.Resource.AcceptsCostCenter)) continue;
            if (!Has(code, scope)) return false;
        }
        return true;
    }

    public IEnumerable<string> HeldPermissionCodes => Grants.SelectMany(g => g.Permissions).Distinct();
}

/// <summary>
/// Confirms that a branch or warehouse really exists in a company before a scoped assignment may reference it.
/// Branches and warehouses live in the per-company databases (a later phase), so the default implementation refuses
/// everything: scoped assignments fail closed until a real validator is registered.
/// </summary>
public interface IScopeTargetValidator
{
    Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct);

    /// <summary>
    /// Existence, activity and activation epoch of the target (null = not found or not verifiable: fail closed). The default derives a constant
    /// state from <see cref="ExistsAsync"/> for validators that know no epochs (tests); the production branch validator reads the real epoch.
    /// </summary>
    async Task<ScopeTargetState?> GetStateAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) =>
        await ExistsAsync(companyId, kind, id, ct) ? new ScopeTargetState(true, 1) : null;
}

/// <summary>The current state of a branch or warehouse as far as access control is concerned. <paramref name="Epoch"/> is its activation epoch.</summary>
public sealed record ScopeTargetState(bool Active, long Epoch);

public sealed class DenyAllScopeTargetValidator : IScopeTargetValidator
{
    public static DenyAllScopeTargetValidator Instance { get; } = new();
    public Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) => Task.FromResult(false);
}
