using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Reporting;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

/// <summary>
/// /api/v1/company/statements: the Profit and Loss, the Balance Sheet and the Trading Account / Cost of Sales (computed from posted ledger data with the company's versioned classification), their exports, and the
/// classification itself (versions, current mapping, template suggestion, save). Statements are branch/cost-center restricted by the caller's grants; the classification is company-wide. No-store, audited.
/// </summary>
public static class FinancialStatementEndpoints
{
    public static IServiceCollection AddHasmFinancialStatements(this IServiceCollection services)
    {
        services.AddSingleton(sp => new StatementDataReader(sp.GetRequiredService<Hasm.Infrastructure.CompanyDatabase.CompanyDatabaseResolver>()));
        services.AddSingleton(sp => new StatementMappingService(sp.GetRequiredService<Hasm.Infrastructure.CompanyDatabase.CompanyDatabaseResolver>(), sp.GetRequiredService<Hasm.Infrastructure.CompanyDatabase.CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new FinancialStatementManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<Hasm.Infrastructure.Ledger.LedgerReportManagementService>(), sp.GetRequiredService<StatementDataReader>(),
            sp.GetRequiredService<StatementMappingService>(), sp.GetRequiredService<ReportOptions>()));
        return services;
    }

    private sealed class BodyLimit(long max) : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => max; }

    public static void MapFinancialStatementEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/statements").RequireAuthorization();
        g.MapGet("/mapping", GetMapping);
        g.MapGet("/mapping/versions", Versions);
        g.MapGet("/mapping/suggestion", Suggestion);
        g.MapPut("/mapping", SaveMapping).WithMetadata(new BodyLimit(2 * 1024 * 1024));
        g.MapGet("/{kind}", Statement);
    }

    private static string M(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static object Range(ResolvedRange r) => new { from = D(r.From), to = D(r.To), fiscalYearId = r.YearId, fiscalPeriodId = r.PeriodId, label = r.Label };

    private static bool TryKind(string kind, out StatementKind k)
    {
        k = kind switch { "profit-and-loss" => StatementKind.ProfitAndLoss, "balance-sheet" => StatementKind.BalanceSheet, "trading-account" => StatementKind.TradingAccount, _ => 0 };
        return k != 0;
    }

    private static object? VersionBody(StatementMappingVersionView? v) =>
        v is null ? null : new { id = v.Id, versionNo = v.VersionNo, createdAtUtc = v.CreatedAtUtc, createdBy = v.CreatedBy, source = v.Source == 2 ? "template" : "manual", note = v.Note, entryCount = v.EntryCount, contentHash = v.ContentHash };

    private static async Task<IResult> Statement(string kind, DateOnly? from, DateOnly? to, Guid? fiscalYearId, Guid? fiscalPeriodId, string? comparison, Guid? branchId, Guid? costCenterId, int? mappingVersion, string? format, string? columns, string? lang,
        HttpContext http, AccessService access, FinancialStatementManagementService svc, ReportExportService exporter)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!TryKind(kind, out var k)) return CompanyConfigurationEndpoints.Error(404, "report_not_found");
        if (!LedgerReportEndpoints.TryComparison(comparison, out var mode)) return CompanyConfigurationEndpoints.Error(400, "invalid_comparison");
        if (from is null && to is null && fiscalYearId is null && fiscalPeriodId is null) return CompanyConfigurationEndpoints.Error(400, "invalid_range");
        var period = new ReportPeriodRequest(fiscalYearId, fiscalPeriodId, from, to, mode);
        if (LedgerReportEndpoints.IsExport(format))
            return await LedgerReportEndpoints.ExportAsync(http, exporter, actor!.CompanyId, actor.UserId, format!, columns, kind + (to is not null ? "-" + D(to.Value) : ""),
                () => svc.StatementDocumentAsync(actor.UserId, actor.CompanyId, k, period, branchId, costCenterId, mappingVersion, http.RequestAborted), lang);
        var r = await svc.StatementAsync(actor!.UserId, actor.CompanyId, k, period, branchId, costCenterId, mappingVersion, false, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new
        {
            kind, applicable = v.Statement.Applicable, notApplicableReason = v.Statement.NotApplicableReason, complete = v.Statement.Complete, asOf = v.AsOfDate, baseCurrency = v.BaseCurrency, hasComparative = v.HasComparative,
            mapping = VersionBody(v.Mapping),
            period = new { current = Range(v.Period.Current), comparative = v.Period.Comparative is null ? null : Range(v.Period.Comparative), comparativeUnavailable = v.Period.ComparativeUnavailable },
            issues = v.Statement.Issues.Select(i => new { code = i.Code, count = i.Count, amount = M(i.Amount) }),
            checks = v.Statement.Checks.Select(c => new { code = c.Code, status = c.Status, difference = M(c.Difference) }),
            lines = v.Statement.Lines.Select(l => new
            {
                kind = l.Kind, key = l.Key, accountId = l.AccountId, code = l.Code, nameAr = l.NameAr, nameEn = l.NameEn, level = l.Level, current = M(l.Current),
                comparative = l.Comparative is null ? null : M(l.Comparative.Value), change = l.Comparative is null ? null : M(l.Current - l.Comparative.Value),
            }),
        });
    }

    private static async Task<IResult> Versions(HttpContext http, AccessService access, FinancialStatementManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.VersionsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { versions = v.Select(VersionBody) });
    }

    private static object SectionCatalog() => StatementSections.All.Select(s => new
    {
        key = StatementSections.Key(s), statement = StatementSections.IsBalanceSheet(s) ? "balance_sheet" : "profit_and_loss",
        types = new[] { AccountType.Asset, AccountType.Liability, AccountType.Equity, AccountType.Revenue, AccountType.Expense }.Where(t => StatementSections.Allows(t, s)).Select(t => t.ToString().ToLowerInvariant()),
    });

    private static async Task<IResult> GetMapping(int? version, HttpContext http, AccessService access, FinancialStatementManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.MappingAsync(actor!.UserId, actor.CompanyId, version, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new
        {
            version = VersionBody(v.Mapping.Version), sections = SectionCatalog(),
            entries = v.Mapping.Entries.Select(e => new { accountId = e.Key, section = StatementSections.Key(e.Value) }),
            accounts = v.Accounts.Select(a => new
            {
                id = a.Id, code = a.Code, nameAr = a.NameAr, nameEn = a.NameEn, parentId = a.ParentId, type = a.Type.ToString().ToLowerInvariant(), nature = a.Nature.ToString().ToLowerInvariant(), isGroup = a.IsGroup, templateKey = a.TemplateKey,
                effectiveSection = v.Effective.GetValueOrDefault(a.Id) is { } s ? StatementSections.Key(s) : null,
            }),
        }, v => v.Mapping.Version?.VersionNo ?? 0);
    }

    private static async Task<IResult> Suggestion(HttpContext http, AccessService access, FinancialStatementManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.SuggestAsync(actor!.UserId, actor.CompanyId, http.RequestAborted), v => new { entries = v.Select(e => new { accountId = e.Key, section = StatementSections.Key(e.Value) }) });
    }

    private static async Task<IResult> SaveMapping(StatementMappingInput? body, HttpContext http, AccessService access, FinancialStatementManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        var r = await svc.SaveAsync(actor!.UserId, actor.CompanyId, expected, body, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, v => new { version = VersionBody(v.Version), entries = v.Entries.Select(e => new { accountId = e.Key, section = StatementSections.Key(e.Value) }) }, v => v.Version?.VersionNo ?? 0);
    }
}
