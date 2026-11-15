using System.Globalization;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;

namespace Hasm.Infrastructure.Reporting;

/// <summary>Everything one statement needs, read from ONE consistent snapshot: the chart, the mapping version, and the signed posted balances per account for each requested range.</summary>
public sealed record StatementData(
    string BaseCurrency, IReadOnlyList<StatementAccount> Accounts, StatementMappingVersionView? Version, IReadOnlyDictionary<Guid, StatementSection> Mapping, IReadOnlyList<IReadOnlyDictionary<Guid, decimal>> Figures);

/// <summary>Reads the statement data. Balances come from the SAME ledger line selection as the trial balance (posted and reversed entries, authorization and cost-center shares applied inside the query).</summary>
public sealed class StatementDataReader(CompanyDatabaseResolver resolver)
{
    /// <param name="requests">One entry per figure set: the scope, and whether the figures are cumulative balances at the end date (balance sheet) or the movement from the start date (profit and loss).</param>
    public async Task<StatementData?> ReadAsync(Guid companyId, IReadOnlyList<(ReportScope Scope, bool Cumulative)> requests, int? mappingVersion, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        string baseCurrency;
        await using (var q = new NpgsqlCommand($"SELECT {CompanySchema.CompanyCurrency.Code} FROM {CompanySchema.CompanyCurrency.Table} WHERE {CompanySchema.CompanyCurrency.IsBase}", conn, tx))
            baseCurrency = ((await q.ExecuteScalarAsync(ct)) as string ?? "").Trim();
        var mapping = await StatementMappingService.ReadAsync(conn, tx, mappingVersion, ct);
        if (mapping is null) { await tx.CommitAsync(ct); return null; }
        var accounts = await StatementMappingService.ReadAccountsAsync(conn, tx, ct);
        var figures = new List<IReadOnlyDictionary<Guid, decimal>>();
        foreach (var (scope, cumulative) in requests)
        {
            var d = new Dictionary<Guid, decimal>();
            await using var cmd = new NpgsqlCommand(LedgerReportService.LinesSql + "\nSELECT account, COALESCE(SUM(bd - bc) FILTER (WHERE @cum OR d >= @from), 0) FROM scoped GROUP BY account", conn, tx);
            LedgerReportService.Bind(cmd.Parameters, scope);
            cmd.Parameters.Add(new NpgsqlParameter("cum", NpgsqlDbType.Boolean) { Value = cumulative });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) d[r.GetGuid(0)] = r.GetDecimal(1);
            figures.Add(d);
        }
        await tx.CommitAsync(ct);
        return new StatementData(baseCurrency, accounts, mapping.Version, mapping.Entries, figures);
    }
}

public sealed record StatementResult(FinancialStatement Statement, ResolvedPeriod Period, StatementMappingVersionView? Mapping, string BaseCurrency, bool HasComparative, bool AsOfDate);

/// <summary>Bilingual titles of the statement lines (used by exports; the screen has its own translated texts).</summary>
public static class StatementTitles
{
    private static readonly Dictionary<string, (string Ar, string En)> T = new()
    {
        ["operating_revenue"] = ("الإيرادات التشغيلية", "Operating revenue"), ["revenue_reductions"] = ("مردودات وخصومات المبيعات", "Revenue reductions (returns and discounts)"), ["net_revenue"] = ("صافي الإيرادات", "Net revenue"),
        ["net_sales"] = ("صافي المبيعات", "Net sales"), ["cost_of_sales"] = ("تكلفة المبيعات", "Cost of sales"), ["direct_service_costs"] = ("التكاليف المباشرة للخدمات", "Direct service costs"),
        ["gross_profit"] = ("مجمل الربح", "Gross profit"), ["operating_expenses"] = ("المصروفات التشغيلية", "Operating expenses"), ["operating_profit"] = ("الربح التشغيلي", "Operating profit"),
        ["other_income"] = ("إيرادات أخرى", "Other income"), ["finance_and_other_expenses"] = ("مصروفات تمويلية وأخرى", "Finance and other expenses"), ["net_result"] = ("صافي الربح أو الخسارة للفترة", "Profit or loss for the period"),
        ["unclassified"] = ("حسابات غير مصنّفة", "Unclassified accounts"), ["total_unclassified_revenue"] = ("إجمالي الإيرادات غير المصنّفة", "Total unclassified revenue"), ["total_unclassified_expenses"] = ("إجمالي المصروفات غير المصنّفة", "Total unclassified expenses"),
        ["current_assets"] = ("الأصول المتداولة", "Current assets"), ["non_current_assets"] = ("الأصول غير المتداولة", "Non-current assets"), ["unclassified_assets"] = ("أصول غير مصنّفة", "Unclassified assets"),
        ["total_assets"] = ("إجمالي الأصول", "Total assets"), ["current_liabilities"] = ("الخصوم المتداولة", "Current liabilities"), ["non_current_liabilities"] = ("الخصوم غير المتداولة", "Non-current liabilities"),
        ["unclassified_liabilities"] = ("خصوم غير مصنّفة", "Unclassified liabilities"), ["total_liabilities"] = ("إجمالي الخصوم", "Total liabilities"), ["equity_capital"] = ("رأس المال", "Capital"),
        ["equity_retained"] = ("الأرباح المحتجزة", "Retained earnings"), ["equity_other"] = ("حقوق ملكية أخرى", "Other equity"), ["unclassified_equity"] = ("حقوق ملكية غير مصنّفة", "Unclassified equity"),
        ["total_equity"] = ("إجمالي حقوق الملكية", "Total equity"), ["total_liabilities_and_equity"] = ("إجمالي الخصوم وحقوق الملكية", "Total liabilities and equity"),
        ["unclosed_result"] = ("ربح أو خسارة لم تُقفل بعد", "Profit or loss not yet closed to equity"),
    };

    public static (string Ar, string En) Of(string key)
    {
        if (T.TryGetValue(key, out var t)) return t;
        if (key.StartsWith("total_", StringComparison.Ordinal) && T.TryGetValue(key[6..], out var s)) return ("إجمالي " + s.Ar, "Total " + s.En);
        return (key, key);
    }
}

/// <summary>
/// Authorized entry point for the financial statements and the statement classification. Statements need <c>company.financialstatements.view</c> (or <c>.export</c> for a file): branch- and cost-center-scoped like the
/// ledger reports, with the restriction applied inside the queries before anything is built. The classification needs <c>company.statementmapping.view</c> / <c>.configure</c> (company-wide).
/// </summary>
public sealed class FinancialStatementManagementService(AccessService access, LedgerReportManagementService ledger, StatementDataReader reader, StatementMappingService mapping, ReportOptions? reportOptions = null)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private readonly ReportOptions options = reportOptions ?? new ReportOptions();

    // ------------------------------------------------------------------------------------------------------------- statements

    public async Task<ConfigResult<StatementResult>> StatementAsync(Guid userId, Guid companyId, StatementKind kind, ReportPeriodRequest period, Guid? branchId, Guid? costCenterId, int? mappingVersion, bool export, CancellationToken ct = default)
    {
        var (ok, allowed, ccs) = await ledger.ScopeAsync(userId, companyId, export ? PermissionCatalog.Codes.FinancialStatementsExport : PermissionCatalog.Codes.FinancialStatementsView, ct);
        if (!ok) return Denied<StatementResult>();
        var resolved = await ledger.ResolvePeriodUncheckedAsync(companyId, period, ct);
        if (!resolved.IsOk) return ConfigResult<StatementResult>.Fail(resolved.Status, resolved.Code!);
        var p = resolved.Value!;
        if (ledger.ValidateScope<StatementResult>(p.Current.From, p.Current.To, branchId, allowed, ccs) is { } bad) return bad;
        var asOf = kind == StatementKind.BalanceSheet;
        // the Profit and Loss and the Trading Account show the ACTIVITY of the period (year-end closing entries only move the result to equity, so they are excluded); the Balance Sheet shows cumulative balances at the end date
        ReportScope Scope(ResolvedRange r) => new(asOf ? r.To : r.From, r.To, branchId, costCenterId, allowed, ccs, ExcludeClosing: !asOf);
        var requests = new List<(ReportScope, bool)> { (Scope(p.Current), asOf) };
        if (p.Comparative is { } c) requests.Add((Scope(c), asOf));
        var data = await reader.ReadAsync(companyId, requests, mappingVersion, ct);
        if (data is null) return ConfigResult<StatementResult>.Fail(ConfigStatus.NotFound, "mapping_version_not_found");
        var sections = StatementClassifier.Resolve(data.Accounts, data.Mapping);
        var noMapping = data.Version is null;
        var cmp = data.Figures.Count > 1 ? data.Figures[1] : null;
        var restricted = allowed is not null || ccs is not null || branchId is not null || costCenterId is not null;
        var statement = kind switch
        {
            StatementKind.ProfitAndLoss => StatementBuilder.ProfitAndLoss(data.Accounts, sections, data.Figures[0], cmp, noMapping),
            StatementKind.TradingAccount => StatementBuilder.TradingAccount(data.Accounts, sections, data.Figures[0], cmp, noMapping),
            _ => StatementBuilder.BalanceSheet(data.Accounts, sections, data.Figures[0], cmp, noMapping, restricted),
        };
        return ConfigResult<StatementResult>.Ok(new StatementResult(statement, p, data.Version, data.BaseCurrency, cmp is not null, asOf));
    }

    public async Task<ConfigResult<ReportDocument>> StatementDocumentAsync(Guid userId, Guid companyId, StatementKind kind, ReportPeriodRequest period, Guid? branchId, Guid? costCenterId, int? mappingVersion, CancellationToken ct = default)
    {
        var r = await StatementAsync(userId, companyId, kind, period, branchId, costCenterId, mappingVersion, true, ct);
        if (!r.IsOk) return ConfigResult<ReportDocument>.Fail(r.Status, r.Code!);
        return ConfigResult<ReportDocument>.Ok(StatementDocuments.From(r.Value!, branchId, costCenterId));
    }

    // ------------------------------------------------------------------------------------------------------------- classification

    private async Task<bool> HoldsAsync(Guid userId, Guid companyId, string code, CancellationToken ct) =>
        (await access.GetEffectivePermissionsAsync(userId, companyId, ct)).Has(code, ScopeTarget.Company);

    public async Task<ConfigResult<IReadOnlyList<StatementMappingVersionView>>> VersionsAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await HoldsAsync(userId, companyId, PermissionCatalog.Codes.StatementMappingView, ct) ? ConfigResult<IReadOnlyList<StatementMappingVersionView>>.Ok(await mapping.ListVersionsAsync(companyId, ct)) : Denied<IReadOnlyList<StatementMappingVersionView>>();

    public async Task<ConfigResult<(StatementMappingView Mapping, IReadOnlyList<StatementAccount> Accounts, IReadOnlyDictionary<Guid, StatementSection?> Effective)>> MappingAsync(Guid userId, Guid companyId, int? versionNo, CancellationToken ct = default)
    {
        if (!await HoldsAsync(userId, companyId, PermissionCatalog.Codes.StatementMappingView, ct)) return Denied<(StatementMappingView, IReadOnlyList<StatementAccount>, IReadOnlyDictionary<Guid, StatementSection?>)>();
        var m = await mapping.GetAsync(companyId, versionNo, ct);
        if (m is null) return ConfigResult<(StatementMappingView, IReadOnlyList<StatementAccount>, IReadOnlyDictionary<Guid, StatementSection?>)>.Fail(ConfigStatus.NotFound, "mapping_version_not_found");
        var accounts = await mapping.AccountsAsync(companyId, ct);
        return ConfigResult<(StatementMappingView, IReadOnlyList<StatementAccount>, IReadOnlyDictionary<Guid, StatementSection?>)>.Ok((m, accounts, StatementClassifier.Resolve(accounts, m.Entries)));
    }

    public async Task<ConfigResult<IReadOnlyDictionary<Guid, StatementSection>>> SuggestAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await HoldsAsync(userId, companyId, PermissionCatalog.Codes.StatementMappingView, ct) ? ConfigResult<IReadOnlyDictionary<Guid, StatementSection>>.Ok(await mapping.ProposeDefaultAsync(companyId, ct)) : Denied<IReadOnlyDictionary<Guid, StatementSection>>();

    public async Task<ConfigResult<StatementMappingView>> SaveAsync(Guid userId, Guid companyId, long? expectedVersion, StatementMappingInput? input, CancellationToken ct = default) =>
        await HoldsAsync(userId, companyId, PermissionCatalog.Codes.StatementMappingConfigure, ct) ? await mapping.SaveAsync(userId, companyId, expectedVersion, input, ct) : Denied<StatementMappingView>();
}

/// <summary>Turns a statement into the neutral export document (headers, section titles, lines, totals, notices).</summary>
public static class StatementDocuments
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static readonly IReadOnlyList<ReportColumn> Columns =
    [
        new("code", "رمز الحساب", "Code", ReportColumnType.Code, 14), new("nameAr", "البند بالعربية", "Item (ar)", ReportColumnType.Text, 36), new("nameEn", "البند بالإنجليزية", "Item (en)", ReportColumnType.Text, 36),
        new("amount", "المبلغ", "Amount", ReportColumnType.Money), new("cmpAmount", "المبلغ (المقارنة)", "Amount (comparative)", ReportColumnType.Money), new("change", "التغير", "Change", ReportColumnType.Money),
    ];

    public static ReportDocument From(StatementResult r, Guid? branchId, Guid? costCenterId)
    {
        var s = r.Statement;
        var (idAr, idEn, id) = s.Kind switch
        {
            StatementKind.ProfitAndLoss => ("قائمة الأرباح والخسائر", "Profit and loss statement", ReportDefinitions.ProfitAndLoss),
            StatementKind.BalanceSheet => ("الميزانية العمومية", "Balance sheet", ReportDefinitions.BalanceSheet),
            _ => ("حساب المتاجرة وتكلفة المبيعات", "Trading account / cost of sales", ReportDefinitions.TradingAccount),
        };
        var cols = r.HasComparative ? Columns : [.. Columns.Take(4)];
        var rows = new List<ReportRow>();
        foreach (var l in s.Lines)
        {
            var (ar, en) = l.AccountId is not null || l.Key == "unclosed_result" ? (l.NameAr, l.NameEn) : StatementTitles.Of(l.Key);
            var kind = l.Kind switch { "header" => ReportRowKind.GroupHeader, "subtotal" => ReportRowKind.Subtotal, "total" => ReportRowKind.Total, _ => ReportRowKind.Detail };
            object?[] cells = l.Kind == "header" ? [null, ar, en, null] : [l.Code, ar, en, l.Current];
            if (r.HasComparative) cells = [.. cells, l.Kind == "header" ? null : l.Comparative, l.Kind == "header" || l.Comparative is null ? null : l.Current - l.Comparative];
            rows.Add(new ReportRow(kind, l.Level, cells));
        }
        var notices = new List<ReportNotice>();
        if (!s.Applicable) notices.Add(new ReportNotice("not_applicable", $"هذا القسم لا ينطبق: {s.NotApplicableReason}.", $"Not applicable: {s.NotApplicableReason}."));
        if (s.Applicable && !s.Complete)
            notices.Add(new ReportNotice("incomplete_classification", "تنبيه: القائمة غير مكتملة لوجود حسابات غير مصنّفة؛ تظهر في بند مستقل ولا تُحذف.", "Warning: this statement is INCOMPLETE because some accounts are unclassified; they are shown in their own block and none is dropped."));
        foreach (var chk in s.Checks.Where(x => x.Status == "failed" && x.Code != "classification_complete"))
            notices.Add(new ReportNotice("check_failed_" + chk.Code, $"فشل فحص المطابقة: {chk.Code} (الفرق {chk.Difference.ToString("0.0000", CultureInfo.InvariantCulture)}).", $"Reconciliation check failed: {chk.Code} (difference {chk.Difference.ToString("0.0000", CultureInfo.InvariantCulture)})."));
        var header = new List<ReportHeaderLine>
        {
            new(r.AsOfDate ? "كما في" : "الفترة", r.AsOfDate ? "As of" : "Period", r.AsOfDate ? D(r.Period.Current.To) : $"{D(r.Period.Current.From)} .. {D(r.Period.Current.To)}"),
            new("العملة الأساسية", "Base currency", r.BaseCurrency),
            new("إصدار التصنيف", "Classification version", r.Mapping is null ? "none" : $"v{r.Mapping.VersionNo}"),
        };
        if (r.Period.Comparative is { } c) header.Add(new ReportHeaderLine("فترة المقارنة", "Comparative", r.AsOfDate ? D(c.To) : $"{D(c.From)} .. {D(c.To)}"));
        else if (r.Period.ComparativeUnavailable is { } why) header.Add(new ReportHeaderLine("فترة المقارنة", "Comparative", "n/a: " + why));
        async IAsyncEnumerable<ReportRow> Stream() { foreach (var row in rows) yield return row; await Task.CompletedTask; }
        return new ReportDocument
        {
            ReportId = id, TitleAr = idAr, TitleEn = idEn, BaseCurrency = r.BaseCurrency, Header = header, Columns = cols, RowCount = rows.Count, Rows = _ => Stream(), Notices = notices,
            FiltersSummary = $"period={D(r.Period.Current.From)}..{D(r.Period.Current.To)};branch={branchId?.ToString() ?? "-"};costCenter={costCenterId?.ToString() ?? "-"};mapping={(r.Mapping is null ? "none" : r.Mapping.VersionNo.ToString())};comparative={(r.HasComparative ? "yes" : "no")}",
        };
    }
}
