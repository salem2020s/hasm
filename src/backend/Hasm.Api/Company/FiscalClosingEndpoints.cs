using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Ledger;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/fiscal-closing: year-end closing and opening balances (Phase 3.2C S3). Company from the server-side session only; authorized inside
/// <see cref="FiscalYearClosingManagementService"/> (separate company-wide permissions); no-store responses. The generated entries are ordinary journal entries: approve, post and
/// reverse them through <c>/api/v1/company/journal</c>.
/// </summary>
public static class FiscalClosingEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 8 * 1024; }

    public static void MapFiscalClosingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/fiscal-closing").RequireAuthorization();
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", PutSettings).WithMetadata(new BodyLimit());
        g.MapGet("/years", List);
        g.MapGet("/years/{yearId:guid}/preview", Preview);
        g.MapPost("/years/{yearId:guid}/close", Close);
        g.MapPost("/years/{yearId:guid}/carry-forward", CarryForward);
        g.MapPost("/years/{yearId:guid}/reverse-opening", ReverseOpening).WithMetadata(new BodyLimit());
        g.MapPost("/years/{yearId:guid}/reverse-closing", ReverseClosing).WithMetadata(new BodyLimit());
    }

    private static string M(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string StatusName(JournalStatus s) => s switch { JournalStatus.PendingApproval => "pending_approval", _ => s.ToString().ToLowerInvariant() };

    private static object SettingsBody(ClosingSettingsView s) => new { equityAccountId = s.EquityAccountId, equityCode = s.EquityCode, equityNameAr = s.EquityNameAr, equityNameEn = s.EquityNameEn, requireApproval = s.RequireApproval, revision = s.Revision, configured = s.Configured };
    private static object Line(YearEndLine l) => new { accountId = l.AccountId, branchId = l.BranchId, debit = M(l.Debit), credit = M(l.Credit) };

    private static object ClosingBody(FiscalYearClosingView c) => new
    {
        id = c.Id, yearId = c.YearId, version = c.Version, netResult = M(c.NetResult), equityAccountId = c.EquityAccountId, closingEntryId = c.ClosingEntryId, closingStatus = StatusName(c.ClosingStatus), closingNumber = c.ClosingNumber,
        carryforwardId = c.CarryforwardId, openingEntryId = c.OpeningEntryId, openingStatus = c.OpeningStatus is { } s ? StatusName(s) : null, openingNumber = c.OpeningNumber, toYearId = c.ToYearId,
        state = c.State, stale = c.Stale, evidenceHash = c.EvidenceHash, createdAtUtc = c.CreatedAtUtc, createdBy = c.CreatedBy,
    };

    private static object CarryBody(FiscalYearCarryforwardView c) => new
    {
        id = c.Id, fromYearId = c.FromYearId, closingId = c.ClosingId, toYearId = c.ToYearId, entryId = c.EntryId, entryStatus = StatusName(c.EntryStatus), entryNumber = c.EntryNumber, stale = c.Stale, evidenceHash = c.EvidenceHash, createdAtUtc = c.CreatedAtUtc, createdBy = c.CreatedBy,
    };

    private static object PreviewBody(ClosingPreview p) => new
    {
        yearId = p.YearId, yearName = p.YearName, yearStart = D(p.YearStart), yearEnd = D(p.YearEnd), baseCurrency = p.BaseCurrency, equityAccountId = p.EquityAccountId, netResult = M(p.NetResult), isProfit = p.NetResult < 0,
        closingLines = p.ClosingLines.Select(Line), openingLines = p.OpeningLines.Select(Line), nextYearId = p.NextYearId, nextYearStart = p.NextYearStart is { } s ? D(s) : null,
        checks = p.Checks.Select(c => new { code = c.Code, status = c.Status, detail = c.Detail, phase = c.Phase }), canClose = p.CanClose, canCarryForward = p.CanCarryForward, nextVersion = p.NextVersion, evidenceHash = p.EvidenceHash, activeClosingId = p.ActiveClosingId,
    };

    private static async Task<IResult> GetSettings(HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.GetSettingsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var eff = await access.GetEffectivePermissionsAsync(actor.UserId, actor.CompanyId, http.RequestAborted);
        return new CompanyConfigurationEndpoints.EtagResult(r.Value!.Revision, new
        {
            settings = SettingsBody(r.Value),
            can = new { configure = eff.Has(PermissionCatalog.Codes.FiscalClosingConfigure, ScopeTarget.Company), create = eff.Has(PermissionCatalog.Codes.FiscalClosingCreate, ScopeTarget.Company) && eff.Has(PermissionCatalog.Codes.JournalCreate, ScopeTarget.Company), reverse = eff.Has(PermissionCatalog.Codes.FiscalClosingReverse, ScopeTarget.Company) },
        });
    }

    private static async Task<IResult> PutSettings(ClosingSettingsInput? body, HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.PutSettingsAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted), SettingsBody, v => v.Revision);
    }

    private static async Task<IResult> List(HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ListAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new
        {
            years = v.Select(y => new { yearId = y.YearId, name = y.YearName, startDate = D(y.StartDate), endDate = D(y.EndDate), closings = y.Closings.Select(ClosingBody), carryforwards = y.Carryforwards.Select(CarryBody) }),
        });
    }

    private static async Task<IResult> Preview(Guid yearId, HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.PreviewAsync(actor!.UserId, actor.CompanyId, yearId, http.RequestAborted), PreviewBody);
    }

    private static async Task<IResult> Close(Guid yearId, HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.StartClosingAsync(actor!.UserId, actor.CompanyId, yearId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, ClosingBody);
        return Results.Json(ClosingBody(r.Value!), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> CarryForward(Guid yearId, HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.CarryForwardAsync(actor!.UserId, actor.CompanyId, yearId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, CarryBody);
        return Results.Json(CarryBody(r.Value!), statusCode: StatusCodes.Status201Created);
    }

    private static object EntryRef(JournalEntryView v) => new { entryId = v.Header.Id, status = StatusName(v.Header.Status), type = v.Header.Type.ToString().ToLowerInvariant(), revision = v.Header.Revision, entryDate = D(v.Header.EntryDate) };

    private static async Task<IResult> ReverseOpening(Guid yearId, YearEndReversalInput? body, HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ReverseOpeningAsync(actor!.UserId, actor.CompanyId, yearId, body, http.RequestAborted), EntryRef);
    }

    private static async Task<IResult> ReverseClosing(Guid yearId, YearEndReversalInput? body, HttpContext http, AccessService access, FiscalYearClosingManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.ReverseClosingAsync(actor!.UserId, actor.CompanyId, yearId, body, http.RequestAborted), EntryRef);
    }
}
