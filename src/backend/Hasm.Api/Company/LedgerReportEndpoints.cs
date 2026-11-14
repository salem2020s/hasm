using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Reporting;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/reports: the trial balance and the general ledger (account activity), computed from the posted journal only, plus report discovery and fiscal-period resolution. Branch-restricted by the
/// caller's grants, no-store, read-only. <c>format=csv|xlsx|pdf</c> downloads the COMPLETE authorized result (never just a page) through the shared export service and needs the separate export permission; an
/// export above the documented row limit is refused before any file is produced.
/// </summary>
public static class LedgerReportEndpoints
{
    public static void MapLedgerReportEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/reports").RequireAuthorization();
        g.MapGet("/definitions", Definitions);
        g.MapGet("/period", Period);
        g.MapGet("/trial-balance", TrialBalance);
        g.MapGet("/general-ledger", GeneralLedger);
    }

    private static string M(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static bool IsExport(string? format) => !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the document (authorized, export permission), exports it through the shared service and returns the finished file. The file is deleted when the response ends.</summary>
    internal static async Task<IResult> ExportAsync(HttpContext http, ReportExportService exporter, Guid companyId, Guid userId, string format, string? columns, string stem, Func<Task<ConfigResult<ReportDocument>>> build)
    {
        var built = await build();
        if (!built.IsOk) return CompanyConfigurationEndpoints.Map(built, _ => new { });
        await using var doc = built.Value!;
        var cols = string.IsNullOrWhiteSpace(columns) ? null : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var r = await exporter.ExportAsync(new ReportExportRequest(companyId, userId, doc, format, cols, stem), http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var file = r.Value!;
        http.Response.RegisterForDisposeAsync(file);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: false);
    }

    private static async Task<IResult> Definitions(HttpContext http, AccessService access, LedgerReportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var list = await svc.DefinitionsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return Results.Json(new
        {
            reports = list.Select(x => new
            {
                id = x.Definition.Id, titleAr = x.Definition.TitleAr, titleEn = x.Definition.TitleEn, canExport = x.CanExport, formats = x.Definition.Formats, supportsComparison = x.Definition.SupportsComparison,
                supportsGrouping = x.Definition.SupportsGrouping,
                parameters = x.Definition.Parameters.Select(p => new { name = p.Name, kind = p.Kind, required = p.Required, titleAr = p.TitleAr, titleEn = p.TitleEn }),
                columns = x.Definition.Columns.Select(c => new { key = c.Key, titleAr = c.TitleAr, titleEn = c.TitleEn, type = c.Type.ToString().ToLowerInvariant(), @default = c.Default }),
            }),
        });
    }

    internal static bool TryComparison(string? text, out ComparisonMode mode)
    {
        mode = ComparisonMode.None;
        if (string.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("previous_period", StringComparison.OrdinalIgnoreCase)) { mode = ComparisonMode.PreviousPeriod; return true; }
        if (text.Equals("previous_year", StringComparison.OrdinalIgnoreCase)) { mode = ComparisonMode.PreviousYear; return true; }
        return false;
    }

    private static object Range(ResolvedRange r) => new { from = D(r.From), to = D(r.To), fiscalYearId = r.YearId, fiscalPeriodId = r.PeriodId, label = r.Label };

    private static async Task<IResult> Period(Guid? fiscalYearId, Guid? fiscalPeriodId, DateOnly? from, DateOnly? to, string? comparison, HttpContext http, AccessService access, LedgerReportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!TryComparison(comparison, out var mode)) return CompanyConfigurationEndpoints.Error(400, "invalid_comparison");
        var r = await svc.ResolvePeriodAsync(actor!.UserId, actor.CompanyId, new ReportPeriodRequest(fiscalYearId, fiscalPeriodId, from, to, mode), http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new { current = Range(v.Current), comparative = v.Comparative is null ? null : Range(v.Comparative), comparativeUnavailable = v.ComparativeUnavailable });
    }

    private static async Task<IResult> TrialBalance(DateOnly? from, DateOnly? to, Guid? branchId, Guid? costCenterId, bool? includeZero, bool? excludeClosing, string? format, string? columns, HttpContext http, AccessService access, LedgerReportManagementService svc, ReportExportService exporter)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (from is null || to is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        if (IsExport(format))
            return await ExportAsync(http, exporter, actor!.CompanyId, actor.UserId, format!, columns, $"trial-balance-{D(from.Value)}-{D(to.Value)}",
                () => svc.TrialBalanceDocumentAsync(actor.UserId, actor.CompanyId, from.Value, to.Value, branchId, costCenterId, includeZero ?? false, excludeClosing ?? false, http.RequestAborted));
        var r = await svc.TrialBalanceAsync(actor!.UserId, actor.CompanyId, from.Value, to.Value, branchId, costCenterId, includeZero ?? false, false, http.RequestAborted, excludeClosing ?? false);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var v = r.Value!;
        return Results.Json(new
        {
            from = D(v.From), to = D(v.To), baseCurrency = v.BaseCurrency, balanced = v.Balanced,
            totals = new { opening = M(v.TotalOpening), debit = M(v.TotalDebit), credit = M(v.TotalCredit), closing = M(v.TotalClosing) },
            rows = v.Rows.Select(x => new
            {
                accountId = x.AccountId, code = x.Code, nameAr = x.NameAr, nameEn = x.NameEn, parentId = x.ParentId, level = x.Level, isGroup = x.IsGroup,
                opening = M(x.OpeningBalance), debit = M(x.PeriodDebit), credit = M(x.PeriodCredit), closing = M(x.ClosingBalance),
            }),
        });
    }

    private static async Task<IResult> GeneralLedger(Guid? accountId, DateOnly? from, DateOnly? to, Guid? branchId, Guid? costCenterId, int? limit, int? offset, bool? excludeClosing, string? format, string? columns, HttpContext http, AccessService access, LedgerReportManagementService svc, ReportExportService exporter)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (accountId is null || from is null || to is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        if (IsExport(format))
            return await ExportAsync(http, exporter, actor!.CompanyId, actor.UserId, format!, columns, $"general-ledger-{D(from.Value)}-{D(to.Value)}",
                () => svc.GeneralLedgerDocumentAsync(actor.UserId, actor.CompanyId, accountId.Value, from.Value, to.Value, branchId, costCenterId, excludeClosing ?? false, http.RequestAborted));
        var r = await svc.GeneralLedgerAsync(actor!.UserId, actor.CompanyId, accountId.Value, from.Value, to.Value, branchId, costCenterId, limit ?? 200, offset ?? 0, false, http.RequestAborted, excludeClosing ?? false);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var v = r.Value!;
        return Results.Json(new
        {
            from = D(v.From), to = D(v.To), baseCurrency = v.BaseCurrency, accountId = v.AccountId, includesDescendants = v.IncludesDescendants,
            opening = M(v.OpeningBalance), totalDebit = M(v.TotalDebit), totalCredit = M(v.TotalCredit), closing = M(v.ClosingBalance), totalLines = v.TotalLines, offset = v.Offset,
            lines = v.Lines.Select(l => new
            {
                entryId = l.EntryId, number = l.Number, entryDate = D(l.EntryDate), entryType = l.EntryType, entryDescription = l.EntryDescription, lineDescription = l.LineDescription,
                accountId = l.AccountId, currency = l.Currency, debit = M(l.Debit), credit = M(l.Credit), rate = l.Rate.ToString("0.0000000000", CultureInfo.InvariantCulture), rateId = l.RateId,
                baseDebit = M(l.BaseDebit), baseCredit = M(l.BaseCredit), branchId = l.BranchId, isRounding = l.IsRounding, runningBalance = M(l.RunningBalance),
            }),
        });
    }
}
