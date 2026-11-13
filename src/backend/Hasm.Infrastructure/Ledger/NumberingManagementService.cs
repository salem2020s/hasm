using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>Authorized entry point for numbering configuration (the only thing the API calls). Company-wide permissions, re-read from Hasm_System on every call.</summary>
public sealed class NumberingManagementService(AccessService access, NumberingService numbering)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<IReadOnlyList<NumberingRuleView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.NumberingView, ct)
            ? ConfigResult<IReadOnlyList<NumberingRuleView>>.Ok(await numbering.ListAsync(companyId, ct)) : Denied<IReadOnlyList<NumberingRuleView>>();

    public async Task<ConfigResult<NumberingRuleView>> PutRuleAsync(Guid userId, Guid companyId, string docType, long? revision, NumberingRuleInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.NumberingConfigure, ct) ? await numbering.PutRuleAsync(userId, companyId, docType, revision, input, ct) : Denied<NumberingRuleView>();
}
