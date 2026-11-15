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
        g.MapGet("/reconciliation", Reconciliation);
    }

    private static string M(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static bool IsExport(string? format) => !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the document (authorized, export permission), exports it through the shared service and returns the finished file. The file is deleted when the response ends.</summary>
    internal static async Task<IResult> ExportAsync(HttpContext http, ReportExportService exporter, Guid companyId, Guid userId, string format, string? columns, string stem, Func<Task<ConfigResult<ReportDocument>>> build, string? lang = null)
    {
        var built = await build();
        if (!built.IsOk) return CompanyConfigurationEndpoints.Map(built, _ => new { });
        await using var doc = built.Value!;
        var cols = string.IsNullOrWhiteSpace(columns) ? null : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var r = await exporter.ExportAsync(new ReportExportRequest(companyId, userId, doc, format, cols, stem, string.Equals(lang, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en"), http.RequestAborted);
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

    private static object Cmp(ComparativeTrialBalanceRow x) => new { opening = M(x.CmpOpening ?? 0), debit = M(x.CmpDebit ?? 0), credit = M(x.CmpCredit ?? 0), closing = M(x.CmpClosing ?? 0) };

    private static async Task<IResult> TrialBalance(DateOnly? from, DateOnly? to, Guid? fiscalYearId, Guid? fiscalPeriodId, string? comparison, Guid? branchId, Guid? costCenterId, bool? includeZero, bool? excludeClosing, int? maxLevel, string? sort, string? format, string? columns, string? lang,
        HttpContext http, AccessService access, LedgerReportManagementService svc, ReportExportService exporter)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!TryComparison(comparison, out var mode)) return CompanyConfigurationEndpoints.Error(400, "invalid_comparison");
        if (from is null && to is null && fiscalYearId is null && fiscalPeriodId is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        var period = new ReportPeriodRequest(fiscalYearId, fiscalPeriodId, from, to, mode);
        if (IsExport(format))
            return await ExportAsync(http, exporter, actor!.CompanyId, actor.UserId, format!, columns, from is not null && to is not null ? $"trial-balance-{D(from.Value)}-{D(to.Value)}" : "trial-balance",
                () => svc.TrialBalanceDocumentAsync(actor.UserId, actor.CompanyId, period, branchId, costCenterId, includeZero ?? false, excludeClosing ?? false, maxLevel, sort, http.RequestAborted), lang);
        var r = await svc.TrialBalanceReportAsync(actor!.UserId, actor.CompanyId, period, branchId, costCenterId, includeZero ?? false, excludeClosing ?? false, maxLevel, sort, false, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var v = r.Value!; var cur = v.Current; var cmp = v.Comparative;
        return Results.Json(new
        {
            from = D(cur.From), to = D(cur.To), baseCurrency = cur.BaseCurrency, balanced = cur.Balanced, sort = v.Sort, maxLevel = v.MaxLevel,
            period = new { current = Range(v.Period.Current), comparative = v.Period.Comparative is null ? null : Range(v.Period.Comparative), comparativeUnavailable = v.Period.ComparativeUnavailable },
            totals = new { opening = M(cur.TotalOpening), debit = M(cur.TotalDebit), credit = M(cur.TotalCredit), closing = M(cur.TotalClosing) },
            comparativeTotals = cmp is null ? null : new { opening = M(cmp.TotalOpening), debit = M(cmp.TotalDebit), credit = M(cmp.TotalCredit), closing = M(cmp.TotalClosing) },
            rows = v.Rows.Select(x => new
            {
                accountId = x.AccountId, code = x.Code, nameAr = x.NameAr, nameEn = x.NameEn, parentId = x.ParentId, level = x.Level, isGroup = x.IsGroup,
                opening = M(x.Opening), debit = M(x.Debit), credit = M(x.Credit), closing = M(x.Closing),
                comparative = cmp is null ? null : Cmp(x), change = x.Change is null ? null : M(x.Change.Value),
            }),
        });
    }

    private static async Task<(DateOnly From, DateOnly To)?> RangeAsync(LedgerReportManagementService svc, Guid userId, Guid companyId, DateOnly? from, DateOnly? to, Guid? fiscalYearId, Guid? fiscalPeriodId, CancellationToken ct)
    {
        if (fiscalYearId is null && fiscalPeriodId is null) return from is null || to is null ? null : (from.Value, to.Value);
        var r = await svc.ResolvePeriodAsync(userId, companyId, new ReportPeriodRequest(fiscalYearId, fiscalPeriodId, from, to), ct);
        return r.IsOk ? (r.Value!.Current.From, r.Value.Current.To) : null;
    }

    private static async Task<IResult> GeneralLedger(Guid? accountId, DateOnly? from, DateOnly? to, Guid? fiscalYearId, Guid? fiscalPeriodId, Guid? branchId, Guid? costCenterId, int? limit, int? offset, bool? excludeClosing, string? grouping, string? format, string? columns, string? lang,
        HttpContext http, AccessService access, LedgerReportManagementService svc, ReportExportService exporter)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (accountId is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        var range = await RangeAsync(svc, actor!.UserId, actor.CompanyId, from, to, fiscalYearId, fiscalPeriodId, http.RequestAborted);
        if (range is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        (from, to) = (range.Value.From, range.Value.To);
        if (grouping is not null and not ("month" or "none")) return CompanyConfigurationEndpoints.Error(400, "invalid_grouping");
        if (IsExport(format))
            return await ExportAsync(http, exporter, actor.CompanyId, actor.UserId, format!, columns, $"general-ledger-{D(from.Value)}-{D(to.Value)}",
                () => svc.GeneralLedgerDocumentAsync(actor.UserId, actor.CompanyId, accountId.Value, from.Value, to.Value, branchId, costCenterId, excludeClosing ?? false, http.RequestAborted, grouping == "month"), lang);
        var r = await svc.GeneralLedgerAsync(actor.UserId, actor.CompanyId, accountId.Value, from.Value, to.Value, branchId, costCenterId, limit ?? 200, offset ?? 0, false, http.RequestAborted, excludeClosing ?? false);
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

    private static async Task<IResult> Reconciliation(DateOnly? from, DateOnly? to, Guid? fiscalYearId, Guid? fiscalPeriodId, Guid? branchId, Guid? costCenterId, bool? excludeClosing, HttpContext http, AccessService access, LedgerReportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var range = await RangeAsync(svc, actor!.UserId, actor.CompanyId, from, to, fiscalYearId, fiscalPeriodId, http.RequestAborted);
        if (range is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        var r = await svc.ReconciliationAsync(actor.UserId, actor.CompanyId, range.Value.From, range.Value.To, branchId, costCenterId, excludeClosing ?? false, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new
        {
            from = D(v.From), to = D(v.To), baseCurrency = v.BaseCurrency, allPassed = v.AllPassed,
            checks = v.Checks.Select(c => new
            {
                code = c.Code, status = c.Status.ToString().ToLowerInvariant().Replace("notapplicable", "not_applicable"), differences = c.Differences,
                samples = c.Samples.Select(d => new { accountId = d.AccountId, code = d.Code, item = d.Item, expected = M(d.Expected), actual = M(d.Actual) }),
            }),
        });
    }
}
