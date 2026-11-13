using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Authorized entry point for branch management (the only thing the API calls). Permissions are re-read from Hasm_System on every call (no
/// cache) and checked for the selected company; branch-scoped grants are filtered by the activation-epoch rule inside
/// <see cref="AccessService"/>. Viewing and editing are branch-scoped (a branch-scoped holder reaches only that branch); creating,
/// activating/deactivating and designating the main branch change the company structure and need company-wide grants. A failed branch-specific
/// check is never reinterpreted as company-wide access. The business rules (invariants, Trial limit, audit, concurrency) live in
/// <see cref="CompanyBranchService"/> and PostgreSQL; the client is never trusted for any of them.
/// </summary>
public sealed class BranchManagementService(AccessService access, CompanyBranchService branches)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    /// <summary>Branches the user may view (company-wide: all; branch-scoped: only those branches). Forbidden when the user may view none.</summary>
    public async Task<ConfigResult<IReadOnlyList<BranchView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.BranchesView)) return Denied<IReadOnlyList<BranchView>>();
        var all = await branches.ListAsync(companyId, ct);
        var visible = all.Where(b => eff.Has(PermissionCatalog.Codes.BranchesView, ScopeTarget.Branch(b.Id))).ToList();
        return visible.Count == 0 ? Denied<IReadOnlyList<BranchView>>() : ConfigResult<IReadOnlyList<BranchView>>.Ok(visible);
    }

    public async Task<ConfigResult<BranchView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchesView, ScopeTarget.Branch(id), ct)) return Denied<BranchView>();
        var b = await branches.GetAsync(companyId, id, ct);
        return b is null ? ConfigResult<BranchView>.Fail(ConfigStatus.NotFound, "branch_not_found") : ConfigResult<BranchView>.Ok(b);
    }

    public async Task<ConfigResult<BranchView>> CreateAsync(Guid userId, Guid companyId, BranchInput? input, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchCreate, ScopeTarget.Company, ct)
            ? await branches.CreateAsync(userId, companyId, input, ct) : Denied<BranchView>();

    public async Task<ConfigResult<BranchView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, BranchInput? input, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchesUpdate, ScopeTarget.Branch(id), ct)
            ? await branches.UpdateAsync(userId, companyId, id, expectedRevision, input, ct) : Denied<BranchView>();

    public async Task<ConfigResult<BranchView>> DeactivateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, Guid? newMainId, CancellationToken ct = default)
    {
        var allowed = await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchActivate, ScopeTarget.Company, ct)
            && (newMainId is null || await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchDesignate, ScopeTarget.Company, ct));
        return allowed ? await branches.DeactivateAsync(userId, companyId, id, expectedRevision, newMainId, ct) : Denied<BranchView>();
    }

    public async Task<ConfigResult<BranchView>> ReactivateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchActivate, ScopeTarget.Company, ct)
            ? await branches.ReactivateAsync(userId, companyId, id, expectedRevision, ct) : Denied<BranchView>();

    public async Task<ConfigResult<BranchView>> MakeMainAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.BranchDesignate, ScopeTarget.Company, ct)
            ? await branches.MakeMainAsync(userId, companyId, id, expectedRevision, ct) : Denied<BranchView>();

    /// <summary>The edition limit and how many branches were ever created (inactive ones count); requires any branch view permission.</summary>
    public async Task<ConfigResult<BranchLimits>> LimitsAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.BranchesView)) return Denied<BranchLimits>();
        return ConfigResult<BranchLimits>.Ok(new BranchLimits(branches.MaxBranches, (await branches.ListAsync(companyId, ct)).Count));
    }
}

/// <summary>The branch limit of the edition and the number of branches ever created (a new one is possible only while Created &lt; Limit).</summary>
public sealed record BranchLimits(int Limit, long EverCreated);
