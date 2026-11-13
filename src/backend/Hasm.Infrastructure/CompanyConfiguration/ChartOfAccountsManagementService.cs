using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Authorized entry point for the Chart of Accounts (the only thing the API calls). Permissions are re-read from Hasm_System on every call (no cache) and are
/// company-wide: the chart belongs to the whole company, so a branch or warehouse grant never reaches it. View, create (also applying a template), update,
/// activate/deactivate, RE-CODE, RESTRUCTURE (move in the tree) and configure (automatic code assignment) are separate permissions. The business rules (tree
/// integrity, history protection, audit, concurrency) live in <see cref="ChartOfAccountsService"/> and PostgreSQL; the client is never trusted for any of them.
/// </summary>
public sealed class ChartOfAccountsManagementService(AccessService access, ChartOfAccountsService chart)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public IReadOnlyList<string> UnimplementedUsageChecks => chart.UnimplementedUsageChecks;

    public async Task<ConfigResult<IReadOnlyList<AccountView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsView, ct) ? ConfigResult<IReadOnlyList<AccountView>>.Ok(await chart.ListAsync(companyId, ct)) : Denied<IReadOnlyList<AccountView>>();

    public async Task<ConfigResult<AccountView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsView, ct)) return Denied<AccountView>();
        var a = await chart.GetAsync(companyId, id, ct);
        return a is null ? ConfigResult<AccountView>.Fail(ConfigStatus.NotFound, "account_not_found") : ConfigResult<AccountView>.Ok(a);
    }

    public async Task<ConfigResult<ChartSettingsView>> GetSettingsAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsView, ct) ? ConfigResult<ChartSettingsView>.Ok(await chart.GetSettingsAsync(companyId, ct)) : Denied<ChartSettingsView>();

    public async Task<ConfigResult<ChartSettingsView>> PutSettingsAsync(Guid userId, Guid companyId, long? revision, ChartSettingsInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsConfigure, ct) ? await chart.PutSettingsAsync(userId, companyId, revision, input, ct) : Denied<ChartSettingsView>();

    public async Task<ConfigResult<AccountView>> CreateAsync(Guid userId, Guid companyId, AccountInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsCreate, ct) ? await chart.CreateAsync(userId, companyId, input, ct) : Denied<AccountView>();

    public async Task<ConfigResult<IReadOnlyList<AccountView>>> ApplyTemplateAsync(Guid userId, Guid companyId, string? key, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsCreate, ct) ? await chart.ApplyTemplateAsync(userId, companyId, key, ct) : Denied<IReadOnlyList<AccountView>>();

    public async Task<ConfigResult<AccountView>> UpdateAsync(Guid userId, Guid companyId, Guid id, long? revision, AccountDetailsInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsUpdate, ct) ? await chart.UpdateDetailsAsync(userId, companyId, id, revision, input, ct) : Denied<AccountView>();

    public async Task<ConfigResult<AccountView>> RecodeAsync(Guid userId, Guid companyId, Guid id, long? revision, AccountCodeInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsRecode, ct) ? await chart.RecodeAsync(userId, companyId, id, revision, input, ct) : Denied<AccountView>();

    public async Task<ConfigResult<AccountView>> MoveAsync(Guid userId, Guid companyId, Guid id, long? revision, AccountMoveInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsRestructure, ct) ? await chart.MoveAsync(userId, companyId, id, revision, input, ct) : Denied<AccountView>();

    public async Task<ConfigResult<AccountView>> DeactivateAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsActivate, ct) ? await chart.DeactivateAsync(userId, companyId, id, revision, ct) : Denied<AccountView>();

    public async Task<ConfigResult<AccountView>> ReactivateAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.AccountsActivate, ct) ? await chart.ReactivateAsync(userId, companyId, id, revision, ct) : Denied<AccountView>();
}
