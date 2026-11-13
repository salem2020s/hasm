using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Authorized entry point for warehouse management (the only thing the API calls). Permissions are re-read from Hasm_System on every call (no
/// cache) and checked for the selected company; warehouse-scoped grants are filtered by the activation-epoch rule inside <see cref="AccessService"/>.
/// Viewing and editing are warehouse-scoped (a warehouse-scoped holder reaches only that warehouse); creating and activating/deactivating change the
/// company structure and need company-wide grants. There is NO inheritance: branch grants never reach warehouses. A failed warehouse-specific check
/// is never reinterpreted as company-wide access. The business rules (invariants, Trial limit, audit, concurrency) live in
/// <see cref="CompanyWarehouseService"/> and PostgreSQL; the client is never trusted for any of them.
/// </summary>
public sealed class WarehouseManagementService(AccessService access, CompanyWarehouseService warehouses)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    /// <summary>Warehouses the user may view (company-wide: all; warehouse-scoped: only those). Forbidden when the user may view none.</summary>
    public async Task<ConfigResult<IReadOnlyList<WarehouseView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.WarehousesView)) return Denied<IReadOnlyList<WarehouseView>>();
        var all = await warehouses.ListAsync(companyId, ct);
        var visible = all.Where(w => eff.Has(PermissionCatalog.Codes.WarehousesView, ScopeTarget.Warehouse(w.Id))).ToList();
        return visible.Count == 0 && all.Count > 0 ? Denied<IReadOnlyList<WarehouseView>>() : ConfigResult<IReadOnlyList<WarehouseView>>.Ok(visible);
    }

    public async Task<ConfigResult<WarehouseView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.WarehousesView, ScopeTarget.Warehouse(id), ct)) return Denied<WarehouseView>();
        var w = await warehouses.GetAsync(companyId, id, ct);
        return w is null ? ConfigResult<WarehouseView>.Fail(ConfigStatus.NotFound, "warehouse_not_found") : ConfigResult<WarehouseView>.Ok(w);
    }

    public async Task<ConfigResult<WarehouseView>> CreateAsync(Guid userId, Guid companyId, WarehouseInput? input, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.WarehouseCreate, ScopeTarget.Company, ct)
            ? await warehouses.CreateAsync(userId, companyId, input, ct) : Denied<WarehouseView>();

    public async Task<ConfigResult<WarehouseView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, WarehouseInput? input, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.WarehousesUpdate, ScopeTarget.Warehouse(id), ct)
            ? await warehouses.UpdateAsync(userId, companyId, id, expectedRevision, input, ct) : Denied<WarehouseView>();

    public async Task<ConfigResult<WarehouseView>> DeactivateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.WarehouseActivate, ScopeTarget.Company, ct)
            ? await warehouses.DeactivateAsync(userId, companyId, id, expectedRevision, ct) : Denied<WarehouseView>();

    public async Task<ConfigResult<WarehouseView>> ReactivateAsync(Guid userId, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.WarehouseActivate, ScopeTarget.Company, ct)
            ? await warehouses.ReactivateAsync(userId, companyId, id, expectedRevision, ct) : Denied<WarehouseView>();

    /// <summary>The edition limit and how many warehouses were ever created (inactive ones count); requires any warehouse view permission.</summary>
    public async Task<ConfigResult<WarehouseLimits>> LimitsAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (!eff.HeldPermissionCodes.Contains(PermissionCatalog.Codes.WarehousesView)) return Denied<WarehouseLimits>();
        return ConfigResult<WarehouseLimits>.Ok(new WarehouseLimits(warehouses.MaxWarehouses, (await warehouses.ListAsync(companyId, ct)).Count));
    }
}
