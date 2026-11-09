using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Access;

/// <summary>What a permission allows. Names are persisted as lower-case suffixes of permission codes; never rename.</summary>
public enum PermissionAction { View, Create, Update, Delete, Approve, Post, Export, Print }

/// <summary>
/// Granularity at which a role assignment applies. Numeric values are persisted; never renumber.
/// Company covers everything in the company; Branch and Warehouse cover only the identified branch/warehouse.
/// </summary>
public enum ScopeKind : short { Company = 0, Branch = 1, Warehouse = 2 }

/// <summary>A concrete target of an authorization check (what the caller wants to act on).</summary>
public readonly record struct ScopeTarget(ScopeKind Kind, Guid? Id)
{
    public static ScopeTarget Company { get; } = new(ScopeKind.Company, null);
    public static ScopeTarget Branch(Guid id) => new(ScopeKind.Branch, id);
    public static ScopeTarget Warehouse(Guid id) => new(ScopeKind.Warehouse, id);

    /// <summary>A Company target has no id; Branch and Warehouse targets require a non-empty id.</summary>
    public bool IsWellFormed => Kind switch
    {
        ScopeKind.Company => Id is null,
        ScopeKind.Branch or ScopeKind.Warehouse => Id is { } id && id != Guid.Empty,
        _ => false,
    };
}

/// <summary>A resource whose permissions can be granted. <see cref="Scope"/> is the narrowest scope the resource's data lives at.</summary>
public sealed record PermissionResource(string Key, ScopeKind Scope, IReadOnlyList<PermissionAction> Actions);

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
    }

    public Permission Get(string code) =>
        TryGet(code, out var p) ? p : throw new KeyNotFoundException("Unknown permission.");
}

/// <summary>Preset roles shipped with the software (database rows are seeded by migration 0004 and must match this list).</summary>
public static class PresetRoles
{
    public static readonly Guid CompanyAdministratorId = new("5c1b0f6e-0000-4a00-8000-000000000001");
    public static readonly Guid AccessManagerId = new("5c1b0f6e-0000-4a00-8000-000000000002");
    public static readonly Guid AccessAuditorId = new("5c1b0f6e-0000-4a00-8000-000000000003");

    public const string CompanyAdministratorKey = "company-administrator";

    public static IReadOnlyDictionary<Guid, (string Key, IReadOnlySet<string> Permissions)> Definitions { get; } =
        new Dictionary<Guid, (string, IReadOnlySet<string>)>
        {
            [CompanyAdministratorId] = (CompanyAdministratorKey, PermissionCatalog.Default.All.Select(p => p.Code).ToHashSet()),
            [AccessManagerId] = ("access-manager", new HashSet<string>
            {
                PermissionCatalog.Codes.MembersView, PermissionCatalog.Codes.MembersCreate, PermissionCatalog.Codes.MembersDelete,
                PermissionCatalog.Codes.RolesView,
                PermissionCatalog.Codes.AssignmentsView, PermissionCatalog.Codes.AssignmentsCreate, PermissionCatalog.Codes.AssignmentsDelete,
            }),
            [AccessAuditorId] = ("access-auditor", new HashSet<string>
            {
                PermissionCatalog.Codes.MembersView, PermissionCatalog.Codes.RolesView,
                PermissionCatalog.Codes.AssignmentsView, PermissionCatalog.Codes.AuditView,
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
        if (target.Kind != ScopeKind.Company && target.Kind != permission.Resource.Scope) return false;
        foreach (var grant in Grants)
        {
            if (!grant.Permissions.Contains(code)) continue;
            if (grant.Scope == ScopeKind.Company) return true;
            if (grant.Scope == target.Kind && grant.ScopeId == target.Id && permission.Resource.Scope == grant.Scope) return true;
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
            if (scope.Kind != ScopeKind.Company && permission.Resource.Scope != scope.Kind) continue;
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
}

public sealed class DenyAllScopeTargetValidator : IScopeTargetValidator
{
    public static DenyAllScopeTargetValidator Instance { get; } = new();
    public Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) => Task.FromResult(false);
}
