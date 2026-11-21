using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public sealed record DenyRequestBody(string? Note);

public static class FiscalSetup
{
    public static IServiceCollection AddHasmFiscalCalendar(this IServiceCollection services)
    {
        // Real probes: pending entries, pending adjustments and year-end consistency block a close. The inventory valuation probe (Phase 3.7C) is real: stock history that no longer agrees with the ledger, or a stock posting whose accounting side failed, blocks a close.
        services.AddSingleton(new PeriodCloseGuard([new Hasm.Infrastructure.Ledger.PendingJournalPeriodCloseProbe(), new Hasm.Infrastructure.Ledger.PendingAdjustmentsPeriodCloseProbe(), new Hasm.Infrastructure.Ledger.YearEndClosingPeriodCloseProbe(), new Hasm.Infrastructure.Inventory.InventoryValuationPeriodCloseProbe()]));
        services.AddSingleton(sp => new FiscalCalendarService(
            sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(),
            sp.GetRequiredService<PeriodCloseGuard>(), sp.GetRequiredService<FinancialHistoryGuard>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new FiscalCalendarManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<FiscalCalendarService>()));
        return services;
    }
}

/// <summary>
/// /api/v1/company/fiscal-years and /api/v1/company/fiscal-periods: the fiscal calendar. Same rules as the other company endpoints: the company comes ONLY
/// from the server-side session, every call is authorized inside <see cref="FiscalCalendarManagementService"/> (company-wide RBAC, separate permissions
/// for each kind of closing/reopening), responses are no-store, state-changing requests pass the anti-CSRF middleware, and changes use If-Match with the
/// revision of the year or period (428 missing, 412 stale). Both clients (Web and Windows Desktop) use exactly these endpoints.
/// </summary>
public static class FiscalEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => MaxBodyBytes; }

    public static void MapFiscalEndpoints(this WebApplication app)
    {
        var years = app.MapGroup("/api/v1/company/fiscal-years").RequireAuthorization();
        years.MapGet("", List);
        years.MapPost("", CreateYear).WithMetadata(new BodyLimit());
        years.MapGet("/{id:guid}", GetYear);
        years.MapPost("/{id:guid}/close", CloseYear).WithMetadata(new BodyLimit());
        years.MapPost("/{id:guid}/reopen-requests", RequestYearReopen).WithMetadata(new BodyLimit());
        var periods = app.MapGroup("/api/v1/company/fiscal-periods").RequireAuthorization();
        periods.MapPost("/{id:guid}/soft-close", SoftClose).WithMetadata(new BodyLimit());
        periods.MapPost("/{id:guid}/reopen", Reopen).WithMetadata(new BodyLimit());
        periods.MapPost("/{id:guid}/final-close", FinalClose).WithMetadata(new BodyLimit());
        periods.MapPost("/{id:guid}/reopen-requests", RequestPeriodReopen).WithMetadata(new BodyLimit());
        var requests = app.MapGroup("/api/v1/company/fiscal-reopen-requests").RequireAuthorization();
        requests.MapGet("", ListRequests);
        requests.MapPost("/{id:guid}/approve", ApproveRequest).WithMetadata(new BodyLimit());
        requests.MapPost("/{id:guid}/deny", DenyRequest).WithMetadata(new BodyLimit());
        requests.MapPost("/{id:guid}/cancel", CancelRequest).WithMetadata(new BodyLimit());
    }

    private static object PeriodBody(FiscalPeriodView p) => new
    {
        id = p.Id, yearId = p.YearId, number = p.Number, startDate = p.StartDate.ToString("yyyy-MM-dd"), endDate = p.EndDate.ToString("yyyy-MM-dd"),
        status = p.State switch { FiscalPeriodState.Open => "open", FiscalPeriodState.SoftClosed => "soft_closed", _ => "final_closed" },
        statusChangedAtUtc = p.StatusChangedAtUtc, revision = p.Revision,
    };

    private static object YearBody(FiscalYearView y) => new
    {
        id = y.Id, name = y.Name, startDate = y.StartDate.ToString("yyyy-MM-dd"), endDate = y.EndDate.ToString("yyyy-MM-dd"),
        periodType = y.PeriodType.ToString().ToLowerInvariant(), timeZone = y.TimeZone, closed = y.IsClosed, closedAtUtc = y.ClosedAtUtc,
        revision = y.Revision, createdAtUtc = y.CreatedAtUtc, periods = y.Periods.Select(PeriodBody),
    };

    private static async Task<IResult> List(HttpContext http, AccessService access, FiscalCalendarManagementService svc, FiscalCalendarService calendar)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        var today = await calendar.TodayAsync(actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            years = list.Value!.Select(YearBody),
            // null until the company business time zone has been chosen: a year cannot be created before that (nothing is defaulted).
            today = today?.ToString("yyyy-MM-dd"),
            // Closing consults modules that do not exist yet; the UI must not present a close as verified while this is false.
            closeChecksImplemented = calendar.UnimplementedCloseChecks.Count == 0,
            closeChecksNotApplicable = calendar.NotApplicableCloseChecks,
            can = new
            {
                create = eff.Has(PermissionCatalog.Codes.FiscalYearsCreate, ScopeTarget.Company),
                closeYear = eff.Has(PermissionCatalog.Codes.FiscalYearsClose, ScopeTarget.Company),
                requestReopen = eff.Has(PermissionCatalog.Codes.FiscalReopenRequest, ScopeTarget.Company),
                approveReopen = eff.Has(PermissionCatalog.Codes.FiscalReopenApprove, ScopeTarget.Company),
                viewRequests = eff.Has(PermissionCatalog.Codes.FiscalReopenView, ScopeTarget.Company),
                softClose = eff.Has(PermissionCatalog.Codes.FiscalPeriodsClose, ScopeTarget.Company),
                reopen = eff.Has(PermissionCatalog.Codes.FiscalPeriodsReopen, ScopeTarget.Company),
                finalClose = eff.Has(PermissionCatalog.Codes.FiscalPeriodsFinalClose, ScopeTarget.Company),
            },
        });
    }

    private static async Task<IResult> GetYear(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.GetAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted), YearBody, v => v.Revision);
    }

    private static async Task<IResult> CreateYear(FiscalYearInput? body, HttpContext http, AccessService access, FiscalCalendarManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CreateYearAsync(actor!.UserId, actor.CompanyId, body, http.RequestAborted);
        return r.IsOk ? new CreatedEtag($"/api/v1/company/fiscal-years/{r.Value!.Id}", r.Value.Revision, YearBody(r.Value)) : CompanyConfigurationEndpoints.Map(r, YearBody);
    }

    private static async Task<IResult> Run<T>(HttpContext http, AccessService access, Func<Guid, Guid, long?, Task<ConfigResult<T>>> action, Func<T, object> body, Func<T, long?> revision)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await action(actor!.UserId, actor.CompanyId, expected), body, revision);
    }

    private static Task<IResult> CloseYear(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.CloseYearAsync(u, c, id, rev, http.RequestAborted), YearBody, v => v.Revision);
    private static Task<IResult> SoftClose(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.SoftCloseAsync(u, c, id, rev, http.RequestAborted), PeriodBody, v => v.Revision);
    private static Task<IResult> Reopen(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.ReopenAsync(u, c, id, rev, http.RequestAborted), PeriodBody, v => v.Revision);
    private static Task<IResult> FinalClose(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        Run(http, access, (u, c, rev) => svc.FinalCloseAsync(u, c, id, rev, http.RequestAborted), PeriodBody, v => v.Revision);

    // ---- exceptional reopening: request -> approval

    private static object RequestBody(FiscalReopenRequestView r, Guid actor, bool mayApprove) => new
    {
        id = r.Id, target = r.Target == FiscalReopenTarget.Period ? "period" : "year", targetId = r.TargetId, yearName = r.YearName, periodNumber = r.PeriodNumber,
        startDate = r.StartDate?.ToString("yyyy-MM-dd"), endDate = r.EndDate?.ToString("yyyy-MM-dd"), expectedRevision = r.ExpectedRevision, reason = r.Reason,
        status = r.Status.ToString().ToLowerInvariant(), requestedAtUtc = r.RequestedAtUtc, decidedAtUtc = r.DecidedAtUtc, decisionNote = r.DecisionNote, revision = r.Revision,
        // The server states who may do what; the user ids themselves are not exposed. Approving one's own request is never offered (and is refused by the server).
        mine = r.RequestedBy == actor,
        canApprove = mayApprove && r.Status == FiscalReopenStatus.Pending && r.RequestedBy != actor,
        canCancel = r.Status == FiscalReopenStatus.Pending && r.RequestedBy == actor,
    };

    private static async Task<IResult> ListRequests(HttpContext http, AccessService access, FiscalCalendarManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.ListRequestsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!list.IsOk) return CompanyConfigurationEndpoints.Map(list, _ => new { });
        var mayApprove = await access.IsAllowedAsync(actor.UserId, actor.CompanyId, PermissionCatalog.Codes.FiscalReopenApprove, ScopeTarget.Company, http.RequestAborted);
        return Results.Json(new { requests = list.Value!.Select(r => RequestBody(r, actor.UserId, mayApprove)) });
    }

    private static async Task<IResult> RunRequest(HttpContext http, AccessService access, bool created, Func<Guid, Guid, long?, Task<ConfigResult<FiscalReopenRequestView>>> action)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        var r = await action(actor!.UserId, actor.CompanyId, expected);
        // After this caller's own action the response never offers approval to them (they are the requester or already decided it).
        object Body(FiscalReopenRequestView v) => RequestBody(v, actor.UserId, false);
        return r.IsOk && created ? new CreatedEtag($"/api/v1/company/fiscal-reopen-requests/{r.Value!.Id}", r.Value.Revision, Body(r.Value)) : CompanyConfigurationEndpoints.Map(r, Body, v => v.Revision);
    }

    private static Task<IResult> RequestPeriodReopen(Guid id, ReasonInput? body, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        RunRequest(http, access, true, (u, c, rev) => svc.RequestPeriodReopenAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> RequestYearReopen(Guid id, ReasonInput? body, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        RunRequest(http, access, true, (u, c, rev) => svc.RequestYearReopenAsync(u, c, id, rev, body, http.RequestAborted));
    private static Task<IResult> ApproveRequest(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        RunRequest(http, access, false, (u, c, rev) => svc.ApproveRequestAsync(u, c, id, rev, http.RequestAborted));
    private static Task<IResult> DenyRequest(Guid id, DenyRequestBody? body, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        RunRequest(http, access, false, (u, c, rev) => svc.DenyRequestAsync(u, c, id, rev, body?.Note, http.RequestAborted));
    private static Task<IResult> CancelRequest(Guid id, HttpContext http, AccessService access, FiscalCalendarManagementService svc) =>
        RunRequest(http, access, false, (u, c, rev) => svc.CancelRequestAsync(u, c, id, rev, http.RequestAborted));

    /// <summary>201 with the strong revision as ETag and the new resource location.</summary>
    private sealed class CreatedEtag(string location, long revision, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.Location = location;
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return httpContext.Response.WriteAsJsonAsync(body);
        }
    }
}
