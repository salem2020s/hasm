using Hasm.Infrastructure.Access;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Authorized entry point for the fiscal calendar (the only thing the API calls). Permissions are re-read from Hasm_System on every call (no cache)
/// and are company-wide: the calendar belongs to the whole company, so a branch or warehouse grant never reaches it. Viewing, creating a year, closing a year,
/// soft closing and reopening a soft close, final closing, and the exceptional reopening APPROVAL workflow (request / approve) are SEPARATE
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


    public async Task<ConfigResult<FiscalPeriodView>> SoftCloseAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsClose, ct) ? await calendar.SoftCloseAsync(userId, companyId, id, revision, ct) : Denied<FiscalPeriodView>();

    public async Task<ConfigResult<FiscalPeriodView>> ReopenAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsReopen, ct) ? await calendar.ReopenAsync(userId, companyId, id, revision, ct) : Denied<FiscalPeriodView>();

    public async Task<ConfigResult<FiscalPeriodView>> FinalCloseAsync(Guid userId, Guid companyId, Guid id, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalPeriodsFinalClose, ct) ? await calendar.FinalCloseAsync(userId, companyId, id, revision, ct) : Denied<FiscalPeriodView>();


    // ---- exceptional reopening: request -> approval (company-wide permissions, no direct reopening exists)

    public async Task<ConfigResult<IReadOnlyList<FiscalReopenRequestView>>> ListRequestsAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalReopenView, ct)
            ? ConfigResult<IReadOnlyList<FiscalReopenRequestView>>.Ok(await calendar.ListRequestsAsync(companyId, ct)) : Denied<IReadOnlyList<FiscalReopenRequestView>>();

    public async Task<ConfigResult<FiscalReopenRequestView>> RequestPeriodReopenAsync(Guid userId, Guid companyId, Guid periodId, long? revision, ReasonInput? reason, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalReopenRequest, ct) ? await calendar.RequestPeriodReopenAsync(userId, companyId, periodId, revision, reason, ct) : Denied<FiscalReopenRequestView>();

    public async Task<ConfigResult<FiscalReopenRequestView>> RequestYearReopenAsync(Guid userId, Guid companyId, Guid yearId, long? revision, ReasonInput? reason, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalReopenRequest, ct) ? await calendar.RequestYearReopenAsync(userId, companyId, yearId, revision, reason, ct) : Denied<FiscalReopenRequestView>();

    /// <summary>
    /// Approves (and executes) a request. Permissions are re-read NOW for the approver and for the original requester: a requester who lost the right to
    /// request, or left the company, can no longer have a request executed. The remaining re-validation (state, revision, segregation of duties) is in the
    /// domain service, inside the executing transaction.
    /// </summary>
    public async Task<ConfigResult<FiscalReopenRequestView>> ApproveRequestAsync(Guid userId, Guid companyId, Guid requestId, long? revision, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalReopenApprove, ct)) return Denied<FiscalReopenRequestView>();
        var request = await calendar.GetRequestAsync(companyId, requestId, ct);
        if (request is null) return ConfigResult<FiscalReopenRequestView>.Fail(ConfigStatus.NotFound, "reopen_request_not_found");
        var requesterStillAuthorized = await Allowed(request.RequestedBy, companyId, PermissionCatalog.Codes.FiscalReopenRequest, ct);
        return await calendar.ApproveRequestAsync(userId, companyId, requestId, revision, requesterStillAuthorized, ct);
    }

    public async Task<ConfigResult<FiscalReopenRequestView>> DenyRequestAsync(Guid userId, Guid companyId, Guid requestId, long? revision, string? note, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalReopenApprove, ct) ? await calendar.DenyRequestAsync(userId, companyId, requestId, revision, note, ct) : Denied<FiscalReopenRequestView>();

    public async Task<ConfigResult<FiscalReopenRequestView>> CancelRequestAsync(Guid userId, Guid companyId, Guid requestId, long? revision, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalReopenRequest, ct) ? await calendar.CancelRequestAsync(userId, companyId, requestId, revision, ct) : Denied<FiscalReopenRequestView>();
}
