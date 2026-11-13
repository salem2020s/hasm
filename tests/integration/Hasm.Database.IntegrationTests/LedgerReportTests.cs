using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Database.IntegrationTests;

/// <summary>Trial balance and general ledger (Phase 3.2B S6) computed from real posted journal data on PostgreSQL; disposable cluster only.</summary>
public abstract class LedgerReportTestBase : JournalTestBase
{
    protected LedgerReportService Reports { get; private set; } = null!;
    protected LedgerReportManagementService RMg { get; private set; } = null!;

    protected async Task PrepareReports()
    {
        await PrepareJournal();
        Reports = new LedgerReportService(Resolver);
        RMg = new LedgerReportManagementService(Rbac, Reports);
    }

    protected static ReportScope Scope(string from, string to, Guid? branch = null, Guid? cc = null, IReadOnlyList<Guid>? branches = null) => new(DateOnly.Parse(from), DateOnly.Parse(to), branch, cc, branches);

    protected async Task<TrialBalanceView> Tb(string from, string to, Guid? branch = null, Guid? cc = null, bool zero = false) => await Reports.TrialBalanceAsync(Company.Id, Scope(from, to, branch, cc), zero);

    protected static TrialBalanceRow Row(TrialBalanceView v, Guid account) => v.Rows.Single(r => r.AccountId == account);
}

public class LedgerReportTests : LedgerReportTestBase
{
    /// <summary>Jan: Dr Cash 1000 / Cr Sales 1000. Feb: Dr Expense 300 / Cr Cash 300, and USD 100 (= 71) into the USD account against Sales. A draft in Feb never counts.</summary>
    private async Task Seed()
    {
        await Posted(Entry("2026-01-10", Dr(Cash.Id, 1000m), Cr(Sales.Id, 1000m)));
        await Posted(Entry("2026-02-05", Dr(Expense.Id, 300m), Cr(Cash.Id, 300m)));
        await Posted(Entry("2026-02-10", Dr(UsdOnly.Id, 100m, "USD"), Cr(Sales.Id, 71m)));
        await Draft(Entry("2026-02-06", Dr(Expense.Id, 9999m), Cr(Cash.Id, 9999m)));
    }

    [PostgresFact]
    public async Task The_trial_balance_shows_opening_period_and_closing_balances_from_posted_entries_only()
    {
        await PrepareReports();
        await Seed();
        var v = await Tb("2026-02-01", "2026-02-28");
        Assert.Equal("JOD", v.BaseCurrency);
        Assert.Equal((1000m, 0m, 300m, 700m), (Row(v, Cash.Id).OpeningBalance, Row(v, Cash.Id).PeriodDebit, Row(v, Cash.Id).PeriodCredit, Row(v, Cash.Id).ClosingBalance));
        Assert.Equal((-1000m, 0m, 71m, -1071m), (Row(v, Sales.Id).OpeningBalance, Row(v, Sales.Id).PeriodDebit, Row(v, Sales.Id).PeriodCredit, Row(v, Sales.Id).ClosingBalance));
        Assert.Equal((0m, 300m, 0m, 300m), (Row(v, Expense.Id).OpeningBalance, Row(v, Expense.Id).PeriodDebit, Row(v, Expense.Id).PeriodCredit, Row(v, Expense.Id).ClosingBalance));
        Assert.Equal(71m, Row(v, UsdOnly.Id).ClosingBalance);                                        // the base amount, not the 100 USD
        Assert.DoesNotContain(v.Rows, r => r.AccountId == BaseOnly.Id);                              // no activity: hidden unless requested
        Assert.Contains((await Tb("2026-02-01", "2026-02-28", zero: true)).Rows, r => r.AccountId == BaseOnly.Id);
        Assert.Equal((0m, 371m, 371m, 0m), (v.TotalOpening, v.TotalDebit, v.TotalCredit, v.TotalClosing));
        Assert.True(v.Balanced);
        // Before the first entry nothing exists; the whole year nets to zero.
        Assert.Empty((await Tb("2025-01-01", "2025-12-31")).Rows);
        var year = await Tb("2026-01-01", "2026-12-31");
        Assert.Equal((0m, 1371m, 1371m, 0m), (year.TotalOpening, year.TotalDebit, year.TotalCredit, year.TotalClosing));
    }

    [PostgresFact]
    public async Task A_reversal_nets_the_original_out_of_the_balances_and_both_stay_visible_in_the_ledger()
    {
        await PrepareReports();
        var p = await Posted(Entry("2026-03-10", Dr(Cash.Id, 500m), Cr(Sales.Id, 500m)));
        var r = Ok(await Journal.ReverseAsync(Maker, Company.Id, p.Header.Id, p.Header.Revision, new ReversalInput(new DateOnly(2026, 3, 20), null)));
        Ok(await Journal.PostAsync(Maker, Company.Id, Ok(await Journal.SubmitAsync(Maker, Company.Id, r.Header.Id, 1)).Header.Id, 1, false));
        var tb = await Tb("2026-03-01", "2026-03-31");
        Assert.Equal((500m, 500m, 0m), (Row(tb, Cash.Id).PeriodDebit, Row(tb, Cash.Id).PeriodCredit, Row(tb, Cash.Id).ClosingBalance));
        Assert.Equal(0m, tb.TotalClosing);
        var gl = (await Reports.GeneralLedgerAsync(Company.Id, Cash.Id, Scope("2026-03-01", "2026-03-31"), 100, 0))!;
        Assert.Equal(["JV-2026-000001", "JV-2026-000002"], gl.Lines.Select(l => l.Number));
        Assert.Equal(["normal", "reversal"], gl.Lines.Select(l => l.EntryType));
        Assert.Equal([500m, 0m], gl.Lines.Select(l => l.RunningBalance));
        Assert.Equal(0m, gl.ClosingBalance);
        // Drafts, pending and cancelled entries are not in any report.
        await Draft(Entry("2026-03-25", Dr(Cash.Id, 77m), Cr(Sales.Id, 77m)));
        Assert.Equal(2, (await Reports.GeneralLedgerAsync(Company.Id, Cash.Id, Scope("2026-03-01", "2026-03-31"), 100, 0))!.TotalLines);
    }

    [PostgresFact]
    public async Task Later_rate_entries_and_master_data_changes_never_change_a_historical_report()
    {
        await PrepareReports();
        await Posted(Entry("2026-02-10", Dr(UsdOnly.Id, 1000m, "USD"), Cr(Sales.Id, 710m)));
        var before = await Tb("2026-02-01", "2026-02-28");
        Ok(await RateSvc.EnterAsync(Admin.Id, Company.Id, (await RateSvc.OverviewAsync(Company.Id))!.Currencies.Single(c => c.CurrencyCode == "USD").LatestId, new ExchangeRateInput("USD", 0.99m, new DateOnly(2026, 1, 1), "backdated")));
        Ok(await ChartDomain.UpdateDetailsAsync(Admin.Id, Company.Id, Sales.Id, 1, new AccountDetailsInput("مبيعات جديدة", "Renamed sales", "credit", null, null, null)));
        var after = await Tb("2026-02-01", "2026-02-28");
        Assert.Equal(before.Rows.Select(r => (r.AccountId, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance)), after.Rows.Select(r => (r.AccountId, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance)));
        Assert.Equal(710m, Row(after, UsdOnly.Id).ClosingBalance);
        Assert.Equal("Renamed sales", Row(after, Sales.Id).NameEn);                                  // names are current, amounts are historical
        var gl = (await Reports.GeneralLedgerAsync(Company.Id, UsdOnly.Id, Scope("2026-02-01", "2026-02-28"), 10, 0))!;
        var line = Assert.Single(gl.Lines);
        Assert.Equal(("USD", 1000m, 0.71m, 710m), (line.Currency, line.Debit, line.Rate, line.BaseDebit));   // original currency, applied rate and base amount
    }

    [PostgresFact]
    public async Task Group_accounts_roll_up_their_leaves_without_double_counting()
    {
        await PrepareReports();
        var group = await NewAccount("6", "expense", group: true);
        var a = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("61", group.Id, "أ", "A", null, null, false, null, null)));
        var b = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput("62", group.Id, "ب", "B", null, null, false, null, null)));
        await Posted(Entry("2026-03-10", Dr(a.Id, 40m), Dr(b.Id, 60m), Cr(Cash.Id, 100m)));
        var tb = await Tb("2026-03-01", "2026-03-31");
        Assert.Equal((100m, true), (Row(tb, group.Id).PeriodDebit, Row(tb, group.Id).IsGroup));
        Assert.Equal([group.Id, a.Id, b.Id], tb.Rows.Where(r => r.Code.StartsWith('6')).Select(r => r.AccountId));
        Assert.Equal([0, 1, 1], tb.Rows.Where(r => r.Code.StartsWith('6')).Select(r => r.Level));
        Assert.Equal(100m, tb.TotalDebit);                                                          // leaves only: the group is not added again
        var gl = (await Reports.GeneralLedgerAsync(Company.Id, group.Id, Scope("2026-03-01", "2026-03-31"), 10, 0))!;
        Assert.True(gl.IncludesDescendants);
        Assert.Equal([40m, 100m], gl.Lines.Select(l => l.RunningBalance));
    }

    [PostgresFact]
    public async Task Branch_and_cost_center_dimensions_filter_the_figures_exactly()
    {
        await PrepareReports();
        var parent = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("P", null, "أ", "Parent", null, true)));
        var c1 = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("C1", parent.Id, "أ", "One", null, false)));
        var c2 = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("C2", parent.Id, "ب", "Two", null, false)));
        await Posted(Entry("2026-03-10", Dr(Expense.Id, 100m, branch: B2, allocations: [(c1.Id, 30m), (c2.Id, 70m)]), Cr(Cash.Id, 100m)));
        await Posted(Entry("2026-03-11", Dr(Expense.Id, 50m, allocations: [(c1.Id, 50m)]), Cr(Cash.Id, 50m)));
        await Posted(Entry("2026-03-12", Dr(Expense.Id, 25m), Cr(Cash.Id, 25m)));                        // no allocation
        Assert.Equal(175m, Row(await Tb("2026-03-01", "2026-03-31"), Expense.Id).PeriodDebit);
        Assert.Equal(100m, Row(await Tb("2026-03-01", "2026-03-31", branch: B2), Expense.Id).PeriodDebit);
        Assert.Equal(75m, Row(await Tb("2026-03-01", "2026-03-31", branch: Main), Expense.Id).PeriodDebit);
        Assert.Equal(80m, Row(await Tb("2026-03-01", "2026-03-31", cc: c1.Id), Expense.Id).PeriodDebit);       // 30 + 50
        Assert.Equal(70m, Row(await Tb("2026-03-01", "2026-03-31", cc: c2.Id), Expense.Id).PeriodDebit);
        Assert.Equal(150m, Row(await Tb("2026-03-01", "2026-03-31", cc: parent.Id), Expense.Id).PeriodDebit);  // a parent includes its children exactly once
        Assert.Equal(30m, Row(await Tb("2026-03-01", "2026-03-31", branch: B2, cc: c1.Id), Expense.Id).PeriodDebit);
        var gl = (await Reports.GeneralLedgerAsync(Company.Id, Expense.Id, Scope("2026-03-01", "2026-03-31", cc: c1.Id), 10, 0))!;
        Assert.Equal([30m, 80m], gl.Lines.Select(l => l.RunningBalance));
        // Deactivating or renaming a cost center never changes what was posted to it.
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, c1.Id, 1));
        Assert.Equal(80m, Row(await Tb("2026-03-01", "2026-03-31", cc: c1.Id), Expense.Id).PeriodDebit);
    }

    [PostgresFact]
    public async Task The_general_ledger_has_an_opening_balance_a_running_balance_and_stable_paging()
    {
        await PrepareReports();
        for (var i = 1; i <= 7; i++) await Posted(Entry($"2026-0{(i <= 3 ? 1 : 2)}-{10 + i:D2}", Dr(Cash.Id, i * 10m), Cr(Sales.Id, i * 10m)));
        var all = (await Reports.GeneralLedgerAsync(Company.Id, Cash.Id, Scope("2026-02-01", "2026-02-28"), 100, 0))!;
        Assert.Equal(60m, all.OpeningBalance);                                                      // 10 + 20 + 30 before February
        Assert.Equal([100m, 150m, 210m, 280m], all.Lines.Select(l => l.RunningBalance));
        Assert.Equal((4, 220m, 0m, 280m), (all.TotalLines, all.TotalDebit, all.TotalCredit, all.ClosingBalance));
        var page1 = (await Reports.GeneralLedgerAsync(Company.Id, Cash.Id, Scope("2026-02-01", "2026-02-28"), 2, 0))!;
        var page2 = (await Reports.GeneralLedgerAsync(Company.Id, Cash.Id, Scope("2026-02-01", "2026-02-28"), 2, 2))!;
        Assert.Equal(all.Lines.Select(l => l.Number), page1.Lines.Concat(page2.Lines).Select(l => l.Number));
        Assert.Equal(all.Lines.Select(l => l.RunningBalance), page1.Lines.Concat(page2.Lines).Select(l => l.RunningBalance));
        Assert.Null(await Reports.GeneralLedgerAsync(Company.Id, Guid.NewGuid(), Scope("2026-02-01", "2026-02-28"), 10, 0));
    }

    [PostgresFact]
    public async Task Reports_are_authorized_per_branch_with_a_separate_export_permission_and_validated_ranges()
    {
        await PrepareReports();
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 100m, branch: B2), Cr(Sales.Id, 100m, branch: B2)));
        await Posted(Entry("2026-03-11", Dr(Cash.Id, 7m), Cr(Sales.Id, 7m)));
        var from = new DateOnly(2026, 3, 1); var to = new DateOnly(2026, 3, 31);
        var (company, _) = await Holder(PermissionCatalog.Codes.LedgerReportsView);
        var (mainOnly, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Branch, Main);
        var (exporter, _) = await Holder(PermissionCatalog.Codes.LedgerReportsExport);
        var nobody = await NewUser("rnobody");
        Assert.Equal(107m, Ok(await RMg.TrialBalanceAsync(company.Id, Company.Id, from, to, null, null, false, false)).Rows.Single(r => r.AccountId == Cash.Id).PeriodDebit);
        Assert.Equal(7m, Ok(await RMg.TrialBalanceAsync(mainOnly.Id, Company.Id, from, to, null, null, false, false)).Rows.Single(r => r.AccountId == Cash.Id).PeriodDebit);   // only its own branch
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.TrialBalanceAsync(mainOnly.Id, Company.Id, from, to, B2, null, false, false)).Status);
        Assert.Equal(7m, Ok(await RMg.GeneralLedgerAsync(mainOnly.Id, Company.Id, Cash.Id, from, to, null, null, 10, 0, false)).TotalDebit);
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.TrialBalanceAsync(nobody.Id, Company.Id, from, to, null, null, false, false)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.TrialBalanceAsync(company.Id, Company.Id, from, to, null, null, false, true)).Status);          // viewing is not exporting
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.TrialBalanceAsync(exporter.Id, Company.Id, from, to, null, null, false, false)).Status);
        Assert.True((await RMg.TrialBalanceAsync(exporter.Id, Company.Id, from, to, null, null, false, true)).IsOk);
        Assert.Equal(ConfigStatus.Invalid, (await RMg.TrialBalanceAsync(company.Id, Company.Id, to, from, null, null, false, false)).Status);
        Assert.Equal(ConfigStatus.NotFound, (await RMg.GeneralLedgerAsync(company.Id, Company.Id, Guid.NewGuid(), from, to, null, null, 10, 0, false)).Status);
    }

    [PostgresFact]
    public async Task The_csv_export_is_utf8_with_exact_decimals_and_neutralises_spreadsheet_formulas()
    {
        await PrepareReports();
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1234.5m), Cr(Sales.Id, 1234.5m)));
        var dangerous = await Posted(Entry("2026-03-11", new JournalLineInput(Cash.Id, "=HYPERLINK(\"http://x\")", null, 1m, null, null, null, null, null), Cr(Sales.Id, 1m)) with { Description = "+cmd, \"quoted\"" });
        var tb = await Tb("2026-03-01", "2026-03-31");
        var bytes = LedgerReportService.TrialBalanceCsv(tb);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("1101,حساب 1101,Account 1101,0,no,0.0000,1235.5000,0.0000,1235.5000", text);
        Assert.True(text.Contains("TOTAL,,,,,0.0000,1235.5000,1235.5000,0.0000"), text);
        var gl = LedgerReportService.GeneralLedgerCsv((await Reports.GeneralLedgerAsync(Company.Id, Cash.Id, Scope("2026-03-01", "2026-03-31"), 10, 0))!);
        var glText = Encoding.UTF8.GetString(gl, 3, gl.Length - 3);
        Assert.Contains("\"'+cmd, \"\"quoted\"\"\"", glText);                                          // a leading formula character is neutralised and quotes are escaped
        Assert.Contains("\"'=HYPERLINK(", glText);
        Assert.Contains("Opening balance", glText);
        Assert.NotEqual(Guid.Empty, dangerous.Header.Id);
    }

    [PostgresFact]
    public async Task The_report_endpoints_return_exact_json_and_csv_with_authorization()
    {
        await StartApi();
        BuildChartFacade();
        BuildJournalFacade();
        await PrepareForApiReports();
        const string Base = "/api/v1/company/reports";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/trial-balance?from=2026-03-01&to=2026-03-31")).Status);
        var cookie = await SignIn(Admin);
        var tb = await Send(HttpMethod.Get, $"{Base}/trial-balance?from=2026-03-01&to=2026-03-31", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, tb.Status);
        Assert.Contains("no-store", tb.Headers.CacheControl!.ToString());
        Assert.Equal("1000.0000", tb.Json.GetProperty("totals").GetProperty("debit").GetString());
        Assert.True(tb.Json.GetProperty("balanced").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/trial-balance?from=2026-03-01", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/trial-balance?from=2026-04-01&to=2026-03-01", cookie: cookie)).Status);
        var gl = await Send(HttpMethod.Get, $"{Base}/general-ledger?accountId={Cash.Id}&from=2026-03-01&to=2026-03-31&limit=5", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, gl.Status);
        Assert.Equal("1000.0000", gl.Json.GetProperty("closing").GetString());
        Assert.Equal(1, gl.Json.GetProperty("lines").GetArrayLength());
        var csv = await client.SendAsync(WithCookie(HttpMethod.Get, $"{Base}/trial-balance?from=2026-03-01&to=2026-03-31&format=csv", cookie));
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/general-ledger?accountId={Guid.NewGuid()}&from=2026-03-01&to=2026-03-31", cookie: cookie)).Status);
    }

    private HttpRequestMessage WithCookie(HttpMethod method, string path, string cookie)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return req;
    }

    private async Task PrepareForApiReports()
    {
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset"); Sales = await NewAccount("4101", "revenue");
        Main = await MainBranch();
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1000m), Cr(Sales.Id, 1000m)));
    }
}
