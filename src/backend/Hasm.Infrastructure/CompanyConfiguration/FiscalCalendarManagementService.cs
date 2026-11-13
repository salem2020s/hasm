using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Authorized entry point for the fiscal calendar (the only thing the API calls). Permissions are re-read from Hasm_System on every call (no cache)
/// and are company-wide: the calendar belongs to the whole company, so a branch or warehouse grant never reaches it. Viewing, creating a year, closing a year,
/// soft closing and reopening a soft close, final closing, and the two exceptional reopenings (a Final Closed period, a closed year) are SEPARATE
/// permissions. The business rules (state machine, reasons, segregation of duties, close guard, audit, concurrency) live in
/// <see cref="FiscalCalendarService"/> and PostgreSQL; the client is never trusted for any of them.
/// </summary>
public sealed class FiscalCalendarManagementService(AccessService access, FiscalCalendarService calendar)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<IReadOnlyList<FiscalYearView>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalYearsView, ct) ? ConfigResult<IReadOnlyList<FiscalYearView>>.Ok(await calendar.ListAsync(companyId, ct)) : Denied<IReadOnlyList<FiscalYearView>>();

    public async Task<ConfigResult<FiscalYearView>> GetAsync(Guid userId, Guid companyId, Guid id, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalYearsView, ct)) return Denied<FiscalYearView>();
        var y = await calendar.GetAsync(companyId, id, ct);
        return y is null ? ConfigResult<FiscalYearView>.Fail(ConfigStatus.NotFound, "fiscal_year_not_found") : ConfigResult<FiscalYearView>.Ok(y);
    }

    public async Task<ConfigResult<FiscalYearView>> CreateYearAsync(Guid userId, Guid companyId, FiscalYearInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalYearsCreate, ct) ? await calendar.CreateYearAsync(userId, companyId, input, ct) : Denied<FiscalYearView>();

    public async Task<ConfigResult<FiscalYearView>> CloseYearAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalYearsClose, ct) ? await calendar.CloseYearAsync(userId, companyId, id, revision, ct) : Denied<FiscalYearView>();

    public async Task<ConfigResult<FiscalYearView>> ReopenYearAsync(Guid userId, Guid companyId, Guid id, long? revision, ReasonInput? reason, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalYearsReopen, ct) ? await calendar.ReopenYearAsync(userId, companyId, id, revision, reason, ct) : Denied<FiscalYearView>();

    public async Task<ConfigResult<FiscalPeriodView>> SoftCloseAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsClose, ct) ? await calendar.SoftCloseAsync(userId, companyId, id, revision, ct) : Denied<FiscalPeriodView>();

    public async Task<ConfigResult<FiscalPeriodView>> ReopenAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsReopen, ct) ? await calendar.ReopenAsync(userId, companyId, id, revision, ct) : Denied<FiscalPeriodView>();

    public async Task<ConfigResult<FiscalPeriodView>> FinalCloseAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsFinalClose, ct) ? await calendar.FinalCloseAsync(userId, companyId, id, revision, ct) : Denied<FiscalPeriodView>();

    public async Task<ConfigResult<FiscalPeriodView>> ReopenFinalAsync(Guid userId, Guid companyId, Guid id, long? revision, ReasonInput? reason, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsReopenFinal, ct) ? await calendar.ReopenFinalAsync(userId, companyId, id, revision, reason, ct) : Denied<FiscalPeriodView>();
}
