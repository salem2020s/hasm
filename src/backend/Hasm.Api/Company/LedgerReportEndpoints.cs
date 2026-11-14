using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/reports: the trial balance and the general ledger (account activity), computed from the posted journal only. Branch-restricted by the caller's grants,
/// no-store, read-only. <c>format=csv</c> downloads a UTF-8 CSV with exact decimals and needs the separate export permission.
/// </summary>
public static class LedgerReportEndpoints
{
    public static void MapLedgerReportEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/reports").RequireAuthorization();
        g.MapGet("/trial-balance", TrialBalance);
        g.MapGet("/general-ledger", GeneralLedger);
    }

    private static string M(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IResult Csv(byte[] bytes, string name) => Results.File(bytes, "text/csv; charset=utf-8", name);

    private static async Task<IResult> TrialBalance(DateOnly? from, DateOnly? to, Guid? branchId, Guid? costCenterId, bool? includeZero, bool? excludeClosing, string? format, HttpContext http, AccessService access, LedgerReportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (from is null || to is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        var r = await svc.TrialBalanceAsync(actor!.UserId, actor.CompanyId, from.Value, to.Value, branchId, costCenterId, includeZero ?? false, csv, http.RequestAborted, excludeClosing ?? false);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var v = r.Value!;
        if (csv) return Csv(LedgerReportService.TrialBalanceCsv(v), $"trial-balance-{D(v.From)}-{D(v.To)}.csv");
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

    private static async Task<IResult> GeneralLedger(Guid? accountId, DateOnly? from, DateOnly? to, Guid? branchId, Guid? costCenterId, int? limit, int? offset, bool? excludeClosing, string? format, HttpContext http, AccessService access, LedgerReportManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (accountId is null || from is null || to is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        var r = await svc.GeneralLedgerAsync(actor!.UserId, actor.CompanyId, accountId.Value, from.Value, to.Value, branchId, costCenterId, limit ?? 200, offset ?? 0, csv, http.RequestAborted, excludeClosing ?? false);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, _ => new { });
        var v = r.Value!;
        if (csv) return Csv(LedgerReportService.GeneralLedgerCsv(v), $"general-ledger-{D(v.From)}-{D(v.To)}.csv");
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
