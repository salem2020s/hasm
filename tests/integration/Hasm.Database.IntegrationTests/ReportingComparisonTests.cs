using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Reporting;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.3 S2 against real PostgreSQL: fiscal-year and period selection, previous-period and previous-year comparatives computed by the same ledger engine, depth limits and deterministic sorting, authorization of the
/// comparison columns, monthly subtotals and the reconciliation of the General Ledger with the Trial Balance. Disposable cluster only.
/// </summary>
public class ReportingComparisonTests : LedgerReportTestBase
{
    private FiscalYearView Fy2025 = null!;
    private LedgerReportManagementService Mgmt = null!;
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private ReportExportService Exporter() => new(Tx, new ReportOptions(), [new CsvReportRenderer()]);

    /// <summary>FY2025 and FY2026 (calendar, monthly). 2025: Jan Dr Cash 500 / Cr Sales 500; Dec Dr Expense 100 / Cr Cash 100. 2026: Feb Dr Expense 50 / Cr Cash 50; Mar Dr Cash 1000 / Cr Sales 1000.</summary>
    private async Task Seed()
    {
        await PrepareReports();
        Mgmt = new LedgerReportManagementService(Rbac, Reports, Journal, new ReportOptions(), Cal);
        Fy2025 = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2025", new DateOnly(2025, 1, 1), "monthly")));
        await Posted(Entry("2025-01-15", Dr(Cash.Id, 500m), Cr(Sales.Id, 500m)));
        await Posted(Entry("2025-12-20", Dr(Expense.Id, 100m), Cr(Cash.Id, 100m)));
        await Posted(Entry("2026-02-10", Dr(Expense.Id, 50m), Cr(Cash.Id, 50m)));
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1000m), Cr(Sales.Id, 1000m)));
    }

    private Task<ConfigResult<ComparativeTrialBalanceView>> Report(ReportPeriodRequest p, Guid? user = null, Guid? branch = null, bool zero = false, int? maxLevel = null, string? sort = null) =>
        Mgmt.TrialBalanceReportAsync(user ?? Admin.Id, Company.Id, p, branch, null, zero, false, maxLevel, sort, false);

    private static ComparativeTrialBalanceRow Row(ComparativeTrialBalanceView v, Guid id) => v.Rows.Single(r => r.AccountId == id);

    [PostgresFact]
    public async Task A_fiscal_year_is_compared_with_the_previous_year_using_the_same_engine_and_the_balances_are_continuous()
    {
        await Seed();
        var v = Ok(await Report(new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear)));
        Assert.Equal((D("2026-01-01"), D("2026-12-31")), (v.Current.From, v.Current.To));
        Assert.Equal((D("2025-01-01"), D("2025-12-31")), (v.Comparative!.From, v.Comparative.To));
        var cash = Row(v, Cash.Id);
        Assert.Equal((400m, 1000m, 50m, 1350m), (cash.Opening, cash.Debit, cash.Credit, cash.Closing));                   // 2026
        Assert.Equal((0m, 500m, 100m, 400m), (cash.CmpOpening, cash.CmpDebit, cash.CmpCredit, cash.CmpClosing));           // 2025
        Assert.Equal(950m, cash.Change);
        Assert.Equal(cash.Opening, cash.CmpClosing);                                                                      // the previous year closes where this year opens
        // the comparative column equals a separate run over the previous year (no second engine, no estimation)
        var separate = Ok(await Report(new ReportPeriodRequest(Fy2025.Id, null, null, null)));
        Assert.Equal(Row(separate, Cash.Id).Closing, cash.CmpClosing);
        Assert.Equal(Row(separate, Sales.Id).Credit, Row(v, Sales.Id).CmpCredit);
        Assert.Null(separate.Comparative);
    }

    [PostgresFact]
    public async Task The_first_year_has_no_comparison_and_says_why_while_a_period_is_compared_with_the_period_before_it()
    {
        await Seed();
        var first = Ok(await Report(new ReportPeriodRequest(Fy2025.Id, null, null, null, ComparisonMode.PreviousYear)));
        Assert.Null(first.Comparative);
        Assert.Equal("no_previous_year", first.Period.ComparativeUnavailable);
        Assert.All(first.Rows, r => Assert.Null(r.CmpClosing));                                                          // nothing invented
        var march = Year.Periods.Single(p => p.Number == 3);
        var v = Ok(await Report(new ReportPeriodRequest(null, march.Id, null, null, ComparisonMode.PreviousPeriod)));
        Assert.Equal((D("2026-02-01"), D("2026-02-28")), (v.Comparative!.From, v.Comparative.To));
        var cash = Row(v, Cash.Id);
        Assert.Equal((350m, 1000m, 0m, 1350m), (cash.Opening, cash.Debit, cash.Credit, cash.Closing));
        Assert.Equal((400m, 0m, 50m, 350m), (cash.CmpOpening, cash.CmpDebit, cash.CmpCredit, cash.CmpClosing));            // February
        var january = Year.Periods.Single(p => p.Number == 1);
        var across = Ok(await Report(new ReportPeriodRequest(null, january.Id, null, null, ComparisonMode.PreviousPeriod)));
        Assert.Equal((D("2025-12-01"), D("2025-12-31")), (across.Comparative!.From, across.Comparative.To));              // across the year boundary: December of the previous year
        Assert.Equal(100m, Row(across, Cash.Id).CmpCredit);
    }

    [PostgresFact]
    public async Task A_partial_custom_range_is_compared_with_the_same_range_a_year_earlier()
    {
        await Seed();
        var v = Ok(await Report(new ReportPeriodRequest(null, null, D("2026-02-01"), D("2026-03-15"), ComparisonMode.PreviousYear)));
        Assert.Equal((D("2025-02-01"), D("2025-03-15")), (v.Comparative!.From, v.Comparative.To));
        Assert.Equal(500m, Row(v, Cash.Id).CmpOpening);                                                                   // January 2025 is before the comparative range
        var prev = Ok(await Report(new ReportPeriodRequest(null, null, D("2026-03-01"), D("2026-03-10"), ComparisonMode.PreviousPeriod)));
        Assert.Equal((D("2026-02-19"), D("2026-02-28")), (prev.Comparative!.From, prev.Comparative.To));                  // ten days ending the day before
    }

    [PostgresFact]
    public async Task The_comparison_column_never_contains_more_than_the_caller_may_see()
    {
        await Seed();
        await Posted(Entry("2025-06-01", Dr(Cash.Id, 77m, null, B2), Cr(Sales.Id, 77m, null, B2)));                       // 2025, branch B2 only
        await Posted(Entry("2026-04-01", Dr(Cash.Id, 5m), Cr(Sales.Id, 5m)));
        var (mainOnly, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Branch, Main);
        var all = Ok(await Report(new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear)));
        var restricted = Ok(await Report(new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear), mainOnly.Id));
        Assert.Equal(477m, Row(all, Cash.Id).CmpClosing);                                                                 // 500 - 100 + 77
        Assert.Equal(400m, Row(restricted, Cash.Id).CmpClosing);                                                          // Main's share only
        Assert.Equal(Row(restricted, Cash.Id).Opening, Row(restricted, Cash.Id).CmpClosing);
        var (nobody, _) = await Member([PermissionCatalog.Codes.JournalView], ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await Report(new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear), nobody.Id)).Status);
        // a user with the view permission only cannot obtain the export document
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.TrialBalanceDocumentAsync(mainOnly.Id, Company.Id, new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear), null, null, false, false, null, null)).Status);
    }

    [PostgresFact]
    public async Task Depth_limit_and_deterministic_sorting_apply_among_siblings_and_the_hierarchy_is_not_double_counted()
    {
        await PrepareReports();
        Mgmt = new LedgerReportManagementService(Rbac, Reports, Journal, new ReportOptions(), Cal);
        var group = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("12", null, "مجموعة", "Group", "asset", null, true, null, null)));
        var a = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("1201", group.Id, "أ", "Alpha", null, null, false, null, null)));
        var b = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("1202", group.Id, "ب", "Beta", null, null, false, null, null)));
        var c = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("1203", group.Id, "ج", "Gamma", null, null, false, null, null)));
        await Posted(Entry("2026-03-10", Dr(a.Id, 10m), Dr(b.Id, 300m), Dr(c.Id, 20m), Cr(Sales.Id, 330m)));
        var p = new ReportPeriodRequest(Year.Id, null, null, null);
        var byCode = Ok(await Report(p));
        Assert.Equal(["1201", "1202", "1203"], byCode.Rows.Where(r => r.ParentId == group.Id).Select(r => r.Code));
        var desc = Ok(await Report(p, sort: TrialBalanceSorts.ClosingDesc));
        Assert.Equal(["1202", "1203", "1201"], desc.Rows.Where(r => r.ParentId == group.Id).Select(r => r.Code));
        var asc = Ok(await Report(p, sort: TrialBalanceSorts.ClosingAsc));
        Assert.Equal(["1201", "1203", "1202"], asc.Rows.Where(r => r.ParentId == group.Id).Select(r => r.Code));
        var byName = Ok(await Report(p, sort: TrialBalanceSorts.Name));
        Assert.Equal(["1201", "1202", "1203"], byName.Rows.Where(r => r.ParentId == group.Id).Select(r => r.Code));          // Alpha, Beta, Gamma
        Assert.Equal(desc.Rows.Select(r => r.Code), Ok(await Report(p, sort: TrialBalanceSorts.ClosingDesc)).Rows.Select(r => r.Code));          // deterministic
        Assert.Equal(330m, Row(byCode, group.Id).Closing);                                                               // the group is the sum of its children, counted once
        var top = Ok(await Report(p, maxLevel: 0));
        Assert.DoesNotContain(top.Rows, r => r.ParentId == group.Id);
        Assert.Contains(top.Rows, r => r.AccountId == group.Id && r.Closing == 330m);
        Assert.Equal(["invalid_sort"], (await Report(p, sort: "bogus")).Fields!.Select(f => f.Code));
        Assert.Equal(["invalid_level"], (await Mgmt.TrialBalanceReportAsync(Admin.Id, Company.Id, p, null, null, false, false, 99, null, false)).Fields!.Select(f => f.Code));
        Assert.Equal(330m, byCode.Current.TotalDebit);                                                                    // leaf totals, never parents
    }

    [PostgresFact]
    public async Task A_comparative_trial_balance_exports_with_its_comparison_columns_and_the_general_ledger_exports_monthly_subtotals()
    {
        await Seed();
        await using var doc = Ok(await Mgmt.TrialBalanceDocumentAsync(Admin.Id, Company.Id, new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear), null, null, false, false, null, null));
        Assert.Contains(doc.Columns, c => c.Key == "cmpClosing");
        await using var file = Ok(await Exporter().ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "csv", ["code", "closing", "cmpClosing", "change"], "tb")));
        var lines = new StreamReader(file.Content, new UTF8Encoding(true)).ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Code,Closing,Closing (comparative),Change in closing", lines[0]);
        Assert.Contains("1101,1350.0000,400.0000,950.0000", lines);
        // the same report without a comparison has no comparison columns at all
        await using var plain = Ok(await Mgmt.TrialBalanceDocumentAsync(Admin.Id, Company.Id, new ReportPeriodRequest(Year.Id, null, null, null), null, null, false, false, null, null));
        Assert.DoesNotContain(plain.Columns, c => c.Key == "cmpClosing");

        await using var gl = Ok(await Mgmt.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false, default, true));
        var rows = new List<ReportRow>(); await foreach (var r in gl.Rows(default)) rows.Add(r);
        var subtotals = rows.Where(r => r.Kind == ReportRowKind.Subtotal && r.Cells[3] is string s && s.StartsWith("Subtotal")).ToList();
        Assert.Equal(["Subtotal 2026-02", "Subtotal 2026-03"], subtotals.Select(r => (string)r.Cells[3]!));
        Assert.Equal((0m, 50m), ((decimal)subtotals[0].Cells[9]!, (decimal)subtotals[0].Cells[10]!));                    // February: Cr 50
        Assert.Equal((1000m, 0m), ((decimal)subtotals[1].Cells[9]!, (decimal)subtotals[1].Cells[10]!));
        Assert.Equal(1350m, (decimal)subtotals[1].Cells[11]!);                                                           // running balance at the end of March
        Assert.True(gl.RowCount >= rows.Count);                                                                          // the limit check never under-counts
    }

    [PostgresFact]
    public async Task The_reconciliation_proves_the_general_ledger_equals_the_trial_balance_and_restricted_viewers_are_reconciled_over_what_they_see()
    {
        await Seed();
        await Posted(Entry("2026-03-12", Dr(Cash.Id, 7m, null, B2), Cr(Sales.Id, 7m, null, Main)));                       // a cross-branch document
        var all = Ok(await Mgmt.ReconciliationAsync(Admin.Id, Company.Id, D("2026-02-01"), D("2026-03-31"), null, null, false));
        Assert.True(all.AllPassed);
        Assert.Equal(["trial_balance_balances", "group_roll_up", "ledger_matches_trial_balance", "opening_equals_previous_closing"], all.Checks.Select(c => c.Code));
        Assert.All(all.Checks, c => Assert.Equal(ReconciliationStatus.Passed, c.Status));
        var (mainOnly, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Branch, Main);
        var restricted = Ok(await Mgmt.ReconciliationAsync(mainOnly.Id, Company.Id, D("2026-02-01"), D("2026-03-31"), null, null, false));
        Assert.True(restricted.AllPassed);
        Assert.Equal(ReconciliationStatus.NotApplicable, restricted.Checks.Single(c => c.Code == "trial_balance_balances").Status);   // part of a cross-branch document is not balanced
        Assert.Equal(ReconciliationStatus.Passed, restricted.Checks.Single(c => c.Code == "ledger_matches_trial_balance").Status);
        var (nobody, _) = await Member([PermissionCatalog.Codes.JournalView], ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await Mgmt.ReconciliationAsync(nobody.Id, Company.Id, D("2026-02-01"), D("2026-03-31"), null, null, false)).Status);
        // the whole history from before the first entry also reconciles
        Assert.True(Ok(await Mgmt.ReconciliationAsync(Admin.Id, Company.Id, D("2025-01-01"), D("2026-12-31"), null, null, false)).AllPassed);
    }

    [PostgresFact]
    public async Task The_endpoints_resolve_fiscal_selectors_comparisons_reconciliation_and_monthly_grouping()
    {
        await StartApi();
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Fy2025 = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2025", new DateOnly(2025, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset"); Sales = await NewAccount("4101", "revenue");
        Main = await MainBranch();
        await Posted(Entry("2025-06-10", Dr(Cash.Id, 200m), Cr(Sales.Id, 200m)));
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1000m), Cr(Sales.Id, 1000m)));
        const string Base = "/api/v1/company/reports";
        var cookie = await SignIn(Admin);
        var tb = await Send(HttpMethod.Get, $"{Base}/trial-balance?fiscalYearId={Year.Id}&comparison=previous_year", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, tb.Status);
        Assert.Equal("2025-01-01", tb.Json.GetProperty("period").GetProperty("comparative").GetProperty("from").GetString());
        var cashRow = tb.Json.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "1101");
        Assert.Equal(("1200.0000", "200.0000", "1000.0000"), (cashRow.GetProperty("closing").GetString(), cashRow.GetProperty("comparative").GetProperty("closing").GetString(), cashRow.GetProperty("change").GetString()));
        Assert.Equal("1000.0000", tb.Json.GetProperty("totals").GetProperty("debit").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/trial-balance?fiscalYearId={Year.Id}&comparison=nonsense", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/trial-balance?sort=bogus&from=2026-01-01&to=2026-12-31", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/trial-balance?fiscalYearId={Guid.NewGuid()}", cookie: cookie)).Status);
        var rec = await Send(HttpMethod.Get, $"{Base}/reconciliation?fiscalYearId={Year.Id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, rec.Status);
        Assert.True(rec.Json.GetProperty("allPassed").GetBoolean());
        Assert.Equal(4, rec.Json.GetProperty("checks").GetArrayLength());
        var gl = await Send(HttpMethod.Get, $"{Base}/general-ledger?accountId={Cash.Id}&fiscalYearId={Year.Id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, gl.Status);
        Assert.Equal("2026-01-01", gl.Json.GetProperty("from").GetString());
        Assert.Equal(1, gl.Json.GetProperty("totalLines").GetInt32());
        var csv = await client.SendAsync(Req($"{Base}/general-ledger?accountId={Cash.Id}&fiscalYearId={Year.Id}&format=csv&grouping=month", cookie));
        Assert.Contains("Subtotal 2026-03", await csv.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/general-ledger?accountId={Cash.Id}&fiscalYearId={Year.Id}&grouping=week", cookie: cookie)).Status);
        var tbCsv = await client.SendAsync(Req($"{Base}/trial-balance?fiscalYearId={Year.Id}&comparison=previous_year&format=csv&columns=code,cmpClosing", cookie));
        Assert.Contains("1101,200.0000", await tbCsv.Content.ReadAsStringAsync());
    }

    private HttpRequestMessage Req(string path, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return req;
    }
}
