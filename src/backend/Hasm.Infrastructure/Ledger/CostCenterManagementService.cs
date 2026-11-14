using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Authorized entry point for cost centers (the only thing the API calls). Permissions are re-read from Hasm_System on every call and are company-wide: view,
/// create, update, activate/deactivate, re-code and restructure (move in the tree) are separate. A branch grant never reaches cost centers.
/// </summary>
public sealed class CostCenterManagementService(AccessService access, CostCenterService costCenters)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<IReadOnlyList<CostCenterView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersView, ct) ? ConfigResult<IReadOnlyList<CostCenterView>>.Ok(await costCenters.ListAsync(companyId, ct)) : Denied<IReadOnlyList<CostCenterView>>();

    public async Task<ConfigResult<CostCenterView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersView, ct)) return Denied<CostCenterView>();
        var c = await costCenters.GetAsync(companyId, id, ct);
        return c is null ? ConfigResult<CostCenterView>.Fail(ConfigStatus.NotFound, "cost_center_not_found") : ConfigResult<CostCenterView>.Ok(c);
    }

    public async Task<ConfigResult<CostCenterView>> CreateAsync(Guid userId, Guid companyId, CostCenterInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersCreate, ct) ? await costCenters.CreateAsync(userId, companyId, input, ct) : Denied<CostCenterView>();

    public async Task<ConfigResult<CostCenterView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? revision, CostCenterDetailsInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersUpdate, ct) ? await costCenters.UpdateDetailsAsync(userId, companyId, id, revision, input, ct) : Denied<CostCenterView>();

    public async Task<ConfigResult<CostCenterView>> RecodeAsync(Guid userId, Guid companyId, Guid id, long? revision, CostCenterCodeInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersRecode, ct) ? await costCenters.RecodeAsync(userId, companyId, id, revision, input, ct) : Denied<CostCenterView>();

    public async Task<ConfigResult<CostCenterView>> MoveAsync(Guid userId, Guid companyId, Guid id, long? revision, CostCenterMoveInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersRestructure, ct) ? await costCenters.MoveAsync(userId, companyId, id, revision, input, ct) : Denied<CostCenterView>();

    public async Task<ConfigResult<CostCenterView>> DeactivateAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersActivate, ct) ? await costCenters.DeactivateAsync(userId, companyId, id, revision, ct) : Denied<CostCenterView>();

    public async Task<ConfigResult<CostCenterView>> ReactivateAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCentersActivate, ct) ? await costCenters.ReactivateAsync(userId, companyId, id, revision, ct) : Denied<CostCenterView>();
}
