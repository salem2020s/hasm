using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>Authorized entry point for exchange rates (the only thing the API calls). Company-wide permissions re-read from Hasm_System on every call; view and create are separate.</summary>
public sealed class ExchangeRateManagementService(AccessService access, ExchangeRateService rates)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<ExchangeRateOverview>> OverviewAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ExchangeRatesView, ct)) return Denied<ExchangeRateOverview>();
        var o = await rates.OverviewAsync(companyId, ct);
        return o is null ? ConfigResult<ExchangeRateOverview>.Fail(ConfigStatus.Conflict, "base_currency_not_selected") : ConfigResult<ExchangeRateOverview>.Ok(o);
    }

    public async Task<ConfigResult<IReadOnlyList<ExchangeRateView>>> ListAsync(Guid userId, Guid companyId, string? currency, DateOnly? from, DateOnly? to, int limit, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ExchangeRatesView, ct)
            ? ConfigResult<IReadOnlyList<ExchangeRateView>>.Ok(await rates.ListAsync(companyId, currency, from, to, limit, ct)) : Denied<IReadOnlyList<ExchangeRateView>>();

    public async Task<ConfigResult<AppliedRate>> ResolveAsync(Guid userId, Guid companyId, string? currency, DateOnly date, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.ExchangeRatesView, ct)) return Denied<AppliedRate>();
        if (string.IsNullOrWhiteSpace(currency)) return ConfigResult<AppliedRate>.Invalid([new FieldError("currency", "required")]);
        var r = await rates.ResolveAsync(companyId, currency, date, ct);
        return r is null ? ConfigResult<AppliedRate>.Fail(ConfigStatus.NotFound, "rate_missing") : ConfigResult<AppliedRate>.Ok(r);
    }

    public async Task<ConfigResult<ExchangeRateView>> EnterAsync(Guid userId, Guid companyId, long? expectedLatestId, ExchangeRateInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.ExchangeRatesCreate, ct) ? await rates.EnterAsync(userId, companyId, expectedLatestId, input, ct) : Denied<ExchangeRateView>();
}
