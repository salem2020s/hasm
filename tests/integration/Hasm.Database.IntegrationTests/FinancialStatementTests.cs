using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Reporting;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Mv = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StatementMappingVersion;
using Me = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StatementMappingEntry;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.3 S3 against real PostgreSQL: the versioned statement classification (immutability, concurrency, audit, database-enforced compatibility) and the Profit and Loss, Balance Sheet and Trading Account
/// computed from REAL posted data of a company created from the Retail template, before and after year-end closing, across consecutive years, reversal and re-closing, with authorization and exports.
/// Disposable cluster only.
/// </summary>
public class FinancialStatementTests : FiscalClosingTestBase
{
    private LedgerReportService Rep = null!;
    private LedgerReportManagementService RepMgmt = null!;
    private FiscalYearClosingManagementService CloseMgmt = null!;
    private StatementMappingService MapSvc = null!;
    private FinancialStatementManagementService Fin = null!;
    private FiscalYearClosingService ClosingSvc = null!;
    private Dictionary<string, AccountView> Acc = null!;
    private static DateOnly D(string s) => DateOnly.Parse(s);

    private static StatementMappingInput ToInput(IEnumerable<KeyValuePair<Guid, StatementSection>> e, string? note = null, string? source = null) =>
        new([.. e.Select(x => new StatementMappingEntryInput(x.Key, StatementSections.Key(x.Value)))], note, source);

    /// <summary>A company with the Retail template applied to an empty chart, fiscal year 2026 (calendar), the base currency and the main branch.</summary>
    private async Task Prepare(bool applyDefaults = true)
    {
        await PrepareRbac();
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Main = await MainBranch();
        B2 = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{B2}', 'B2', 'Second', true, false, 1, 1, now(), now())");
        Acc = Ok(await ChartDomain.ApplyTemplateAsync(Admin.Id, Company.Id, "retail")).ToDictionary(a => a.Code);
        Rep = new LedgerReportService(Resolver);
        RepMgmt = new LedgerReportManagementService(Rbac, Rep, Journal, new ReportOptions(), Cal);
        MapSvc = new StatementMappingService(Resolver, Tx, Clock);
        Fin = new FinancialStatementManagementService(Rbac, RepMgmt, new StatementDataReader(Resolver), MapSvc, new ReportOptions());
        ClosingSvc = new FiscalYearClosingService(Resolver, Tx, Journal, RealGuard(), Clock);
        CloseMgmt = new FiscalYearClosingManagementService(Rbac, ClosingSvc, JMg);
        Ok(await ClosingSvc.PutSettingsAsync(Admin.Id, Company.Id, 0, new ClosingSettingsInput(Acc["3102"].Id, null)));
        if (applyDefaults) Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, ToInput(await MapSvc.ProposeDefaultAsync(Company.Id), "from template", "template")));
    }

    /// <summary>
    /// 2026, Retail: capital 10,000 cash; inventory purchase 4,000 on credit; sale 1,500 cash; sales return 100; cost of goods sold 600; rent 200; purchase return 50; FX gain 20; FX loss 30; bank charge 10.
    /// Result: revenue 1,500 - returns 100 = 1,400; cost of sales 600 - 50 = 550; gross 850; rent 200; operating 650; other income 20; finance 40 => profit 630.
    /// </summary>
    private async Task Seed2026()
    {
        JournalLineInput Dr_(string c, decimal v, Guid? b = null) => Dr(Acc[c].Id, v, null, b);
        JournalLineInput Cr_(string c, decimal v, Guid? b = null) => Cr(Acc[c].Id, v, null, b);
        await Posted(Entry("2026-01-05", Dr_("1101", 10000m), Cr_("3101", 10000m)));
        await Posted(Entry("2026-01-10", Dr_("11201", 4000m), Cr_("2101", 4000m)));
        await Posted(Entry("2026-02-03", Dr_("1101", 1500m), Cr_("4101", 1500m)));
        await Posted(Entry("2026-02-04", Dr_("4102", 100m), Cr_("1101", 100m)));
        await Posted(Entry("2026-02-05", Dr_("5101", 600m), Cr_("11201", 600m)));
        await Posted(Entry("2026-03-01", Dr_("5202", 200m), Cr_("1101", 200m)));
        await Posted(Entry("2026-03-02", Dr_("2101", 50m), Cr_("5104", 50m)));
        await Posted(Entry("2026-03-03", Dr_("1101", 20m), Cr_("4202", 20m)));
        await Posted(Entry("2026-03-04", Dr_("5215", 30m), Cr_("1101", 30m)));
        await Posted(Entry("2026-03-05", Dr_("5213", 10m), Cr_("1101", 10m)));
    }

    private Task<ConfigResult<StatementResult>> Get(StatementKind kind, ReportPeriodRequest p, Guid? user = null, Guid? branch = null, int? version = null, bool export = false) =>
        Fin.StatementAsync(user ?? Admin.Id, Company.Id, kind, p, branch, null, version, export);
    private ReportPeriodRequest Y26(ComparisonMode c = ComparisonMode.None) => new(Year.Id, null, null, null, c);
    private static decimal Total(StatementResult r, string key) => r.Statement.Lines.Single(l => l.Key == key && l.Kind is "total" or "subtotal").Current;
    private static decimal? TotalC(StatementResult r, string key) => r.Statement.Lines.Single(l => l.Key == key && l.Kind is "total" or "subtotal").Comparative;
    private Task<long> Events(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");
    private AuditVerifier CompanyVerifier() => new(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints);

    // ------------------------------------------------------------------------------------------------------------------------------------ classification

    [PostgresFact]
    public async Task The_classification_is_versioned_immutable_audited_and_an_unchanged_mapping_creates_nothing()
    {
        await Prepare(applyDefaults: false);
        var none = await MapSvc.GetAsync(Company.Id, null);
        Assert.Null(none!.Version);
        var suggestion = await MapSvc.ProposeDefaultAsync(Company.Id);
        Assert.True(suggestion.Count >= 14);
        Assert.Equal(StatementSection.EquityRetained, suggestion[Acc["3102"].Id]);
        var v1 = Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, ToInput(suggestion, "initial", "template")));
        Assert.Equal((1, 2, suggestion.Count), (v1.Version!.VersionNo, (int)v1.Version.Source, v1.Version.EntryCount));
        var events = await Events(ReportAuditEvents.StatementMappingSaved);
        Assert.Equal(1L, events);
        // an identical mapping: no new version, no new audit event
        var same = Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, ToInput(suggestion)));
        Assert.Equal(1, same.Version!.VersionNo);
        Assert.Equal(events, await Events(ReportAuditEvents.StatementMappingSaved));
        // a change creates version 2 and leaves version 1 exactly as it was
        var changed = suggestion.ToDictionary(kv => kv.Key, kv => kv.Value); changed[Acc["4202"].Id] = StatementSection.OperatingRevenue;
        var v2 = Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, ToInput(changed, "FX gains as revenue (test)")));
        Assert.Equal(2, v2.Version!.VersionNo);
        Assert.Equal(StatementSection.OtherIncome, (await MapSvc.GetAsync(Company.Id, 1))!.Entries[Acc["42"].Id]);                  // version 1: the whole "other income" group
        Assert.False((await MapSvc.GetAsync(Company.Id, 1))!.Entries.ContainsKey(Acc["4202"].Id));
        Assert.Equal(StatementSection.OperatingRevenue, (await MapSvc.GetAsync(Company.Id, 2))!.Entries[Acc["4202"].Id]);
        Assert.Equal([2, 1], (await MapSvc.ListVersionsAsync(Company.Id)).Select(v => v.VersionNo));
        Assert.Null(await MapSvc.GetAsync(Company.Id, 9));
        Assert.Equal(2L, await Events(ReportAuditEvents.StatementMappingSaved));
        // the history cannot be rewritten, even by a superuser connection: update, delete and truncate are refused
        Assert.Equal("P0001", await Fails($"UPDATE {Mv.Table} SET {Mv.Note} = 'x'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Mv.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {Me.Table} SET {Me.Section} = 1"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Me.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Me.Table}"));
        Assert.Equal(AuditVerdict.Verified, (await CompanyVerifier().VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Concurrency_validation_and_the_database_enforce_the_rules_of_a_mapping()
    {
        await Prepare(applyDefaults: false);
        var cash = Acc["1101"].Id; var sales = Acc["4101"].Id;
        Assert.Equal(ConfigStatus.PreconditionRequired, (await MapSvc.SaveAsync(Admin.Id, Company.Id, null, new([], null, null))).Status);
        Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, new([new(cash, "current_assets")], null, null)));
        var stale = await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, new([new(cash, "non_current_assets")], null, null));
        Assert.Equal((ConfigStatus.PreconditionFailed, 1L), (stale.Status, stale.CurrentRevision));
        // validation: section must fit the type, the account must exist, no duplicates, known sections only
        var incompatible = await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, new([new(cash, "operating_revenue")], null, null));
        Assert.Equal("section_not_allowed", incompatible.Fields![0].Code);
        Assert.Equal("account_not_found", (await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, new([new(Guid.NewGuid(), "current_assets")], null, null))).Fields![0].Code);
        Assert.Equal("duplicate_account", (await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, new([new(cash, "current_assets"), new(cash, "non_current_assets")], null, null))).Fields![0].Code);
        Assert.Equal("invalid_value", (await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, new([new(cash, "bogus")], null, null))).Fields![0].Code);
        Assert.Equal("too_long", (await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, new([], new string('x', 501), null))).Fields![0].Code);
        Assert.Equal(1, (await MapSvc.ListVersionsAsync(Company.Id)).Count);                                              // nothing of the refused saves was written
        // PostgreSQL refuses a wrong section whoever writes the row
        var version = (await MapSvc.GetAsync(Company.Id, 1))!.Version!;
        Assert.Equal("P0001", await Fails($"INSERT INTO {Me.Table} ({Me.VersionId}, {Me.AccountId}, {Me.Section}) VALUES ('{version.Id}', '{sales}', 1)"));
        // concurrent saves from the same version: exactly one wins
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => MapSvc.SaveAsync(Admin.Id, Company.Id, 1, new([new(cash, "current_assets"), new(sales, i % 2 == 0 ? "operating_revenue" : "other_income")], $"n{i}", null))));
        Assert.Single(results, r => r.IsOk && r.Value!.Version!.VersionNo == 2);
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
        Assert.Equal(2, (await MapSvc.ListVersionsAsync(Company.Id)).Count);
    }

    [PostgresFact]
    public async Task Viewing_and_changing_the_classification_need_their_own_company_wide_permissions()
    {
        await Prepare(applyDefaults: false);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StatementMappingView], ScopeKind.Company, null);
        Assert.True((await Fin.MappingAsync(viewer.Id, Company.Id, null)).IsOk);
        Assert.True((await Fin.SuggestAsync(viewer.Id, Company.Id)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await Fin.SaveAsync(viewer.Id, Company.Id, 0, new([], null, null))).Status);
        var (nobody, _) = await Member([PermissionCatalog.Codes.LedgerReportsView, PermissionCatalog.Codes.FinancialStatementsView], ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await Fin.MappingAsync(nobody.Id, Company.Id, null)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Fin.VersionsAsync(nobody.Id, Company.Id)).Status);
        var (configurer, _) = await Member([PermissionCatalog.Codes.StatementMappingView, PermissionCatalog.Codes.StatementMappingConfigure], ScopeKind.Company, null);
        Assert.True((await Fin.SaveAsync(configurer.Id, Company.Id, 0, new([new(Acc["1101"].Id, "current_assets")], null, null))).IsOk);
        var (branchOnly, _) = await Member([PermissionCatalog.Codes.StatementMappingConfigure], ScopeKind.Branch, Main);
        Assert.Equal(ConfigStatus.Forbidden, (await Fin.SaveAsync(branchOnly.Id, Company.Id, 1, new([], null, null))).Status);            // company-wide only
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ statements from real data

    [PostgresFact]
    public async Task The_profit_and_loss_the_balance_sheet_and_the_trading_account_come_from_posted_data_and_reconcile()
    {
        await Prepare();
        await Seed2026();
        var pl = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        Assert.True(pl.Statement.Complete);
        Assert.Equal((1500m, -100m, 1400m), (Total(pl, "total_operating_revenue"), Total(pl, "total_revenue_reductions"), Total(pl, "net_revenue")));
        Assert.Equal((550m, 850m), (Total(pl, "total_cost_of_sales"), Total(pl, "gross_profit")));                         // 600 cost of goods sold less the 50 purchase return
        Assert.Equal((200m, 650m, 20m), (Total(pl, "total_operating_expenses"), Total(pl, "operating_profit"), Total(pl, "total_other_income")));
        Assert.Equal((40m, 630m), (Total(pl, "total_finance_and_other_expenses"), Total(pl, "net_result")));
        Assert.Contains(pl.Statement.Lines, l => l.Code == "4202" && l.Key == "other_income" && l.Current == 20m);           // FX gains are not sales revenue
        Assert.All(pl.Statement.Checks, c => Assert.Equal("passed", c.Status));
        Assert.Equal(1, pl.Mapping!.VersionNo);
        var bs = Ok(await Get(StatementKind.BalanceSheet, Y26()));
        Assert.True(bs.Statement.Complete);
        Assert.Equal((14580m, 3950m, 10630m), (Total(bs, "total_assets"), Total(bs, "total_liabilities"), Total(bs, "total_equity")));
        Assert.Equal(630m, bs.Statement.Lines.Single(l => l.Key == "unclosed_result").Current);
        Assert.Equal(14580m, Total(bs, "total_liabilities_and_equity"));
        Assert.Equal(11180m, bs.Statement.Lines.Single(l => l.Code == "1101").Current);
        Assert.Equal(3400m, bs.Statement.Lines.Single(l => l.Code == "11201").Current);
        Assert.All(bs.Statement.Checks, c => Assert.Equal("passed", c.Status));
        Assert.True(bs.AsOfDate && !pl.AsOfDate);
        var trading = Ok(await Get(StatementKind.TradingAccount, Y26()));
        Assert.True(trading.Statement.Applicable);
        Assert.Equal((1400m, 550m, 850m), (Total(trading, "net_sales"), Total(trading, "total_cost_of_sales"), Total(trading, "gross_profit")));
        // the trial balance agrees: the net result equals the movement of the revenue and expense accounts
        var tb = await Rep.TrialBalanceAsync(Company.Id, new ReportScope(D("2026-01-01"), D("2026-12-31"), null, null, null), false);
        Assert.Equal(0m, tb.TotalClosing);
        Assert.Equal(AuditVerdict.Verified, (await CompanyVerifier().VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task The_statements_are_correct_before_and_after_the_year_end_closing_across_consecutive_years_and_after_reversal_and_re_closing()
    {
        await Prepare();
        await Seed2026();
        var year27 = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2027", new DateOnly(2027, 1, 1), "monthly")));
        var before = Ok(await Get(StatementKind.BalanceSheet, Y26()));
        Assert.Equal((630m, 0m), (before.Statement.Lines.Single(l => l.Key == "unclosed_result").Current, Total(before, "total_equity_retained")));
        // close the year: the closing entry moves the result to the configured retained-earnings account
        var closing = Ok(await ClosingSvc.StartClosingAsync(Admin.Id, Company.Id, Year.Id));
        await PostYearEnd(closing.ClosingEntryId);
        var closed = Ok(await Get(StatementKind.BalanceSheet, Y26()));
        Assert.Equal((0m, 630m, 10630m, 14580m), (closed.Statement.Lines.Single(l => l.Key == "unclosed_result").Current, Total(closed, "total_equity_retained"), Total(closed, "total_equity"), Total(closed, "total_assets")));   // not counted twice
        Assert.All(closed.Statement.Checks, c => Assert.Equal("passed", c.Status));
        var plClosed = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        Assert.Equal(630m, Total(plClosed, "net_result"));                                                                  // the closing entries are not activity
        Assert.Equal(1400m, Total(plClosed, "net_revenue"));
        // carry the balances forward: 2027 opens with the same position and no activity of its own
        var carry = Ok(await ClosingSvc.CarryForwardAsync(Admin.Id, Company.Id, Year.Id));
        await PostYearEnd(carry.EntryId);
        var y27 = new ReportPeriodRequest(year27.Id, null, null, null, ComparisonMode.PreviousYear);
        var bs27 = Ok(await Get(StatementKind.BalanceSheet, y27));
        Assert.Equal((14580m, 10630m, 0m), (Total(bs27, "total_assets"), Total(bs27, "total_equity"), bs27.Statement.Lines.Single(l => l.Key == "unclosed_result").Current));
        Assert.Equal((14580m, 10630m), (TotalC(bs27, "total_assets"), TotalC(bs27, "total_equity")));                      // the comparative column is 2026-12-31: the same position
        var pl27 = Ok(await Get(StatementKind.ProfitAndLoss, y27));
        Assert.Equal((0m, 630m), (Total(pl27, "net_result"), TotalC(pl27, "net_result")!.Value));                           // 2027 has no activity; the comparison is the previous year's real result
        // 2027 activity: sales 300 cash, cost 100
        await Posted(Entry("2027-02-02", Dr(Acc["1101"].Id, 300m), Cr(Acc["4101"].Id, 300m)));
        await Posted(Entry("2027-02-03", Dr(Acc["5101"].Id, 100m), Cr(Acc["11201"].Id, 100m)));
        var pl27b = Ok(await Get(StatementKind.ProfitAndLoss, y27));
        Assert.Equal((200m, 630m), (Total(pl27b, "net_result"), TotalC(pl27b, "net_result")!.Value));
        var bs27b = Ok(await Get(StatementKind.BalanceSheet, y27));
        Assert.Equal((200m, 14780m), (bs27b.Statement.Lines.Single(l => l.Key == "unclosed_result").Current, Total(bs27b, "total_assets")));
        Assert.All(bs27b.Statement.Checks, c => Assert.Equal("passed", c.Status));
        // a custom partial range: February 2027 against the same range a year earlier (2026 February: sale 1,500, return 100, cost 600)
        var feb = Ok(await Get(StatementKind.ProfitAndLoss, new ReportPeriodRequest(null, null, D("2027-02-01"), D("2027-02-28"), ComparisonMode.PreviousYear)));
        Assert.Equal((200m, 800m), (Total(feb, "net_result"), TotalC(feb, "net_result")!.Value));                           // 1500 - 100 - 600 = 800
    }

    [PostgresFact]
    public async Task Reversing_and_re_closing_a_year_keeps_the_statements_exact()
    {
        await Prepare();
        await Seed2026();
        var first = Ok(await ClosingSvc.StartClosingAsync(Admin.Id, Company.Id, Year.Id));
        await PostYearEnd(first.ClosingEntryId);
        Assert.Equal(630m, Total(Ok(await Get(StatementKind.BalanceSheet, Y26())), "total_equity_retained"));
        var rr = await CloseMgmt.ReverseClosingAsync(Admin.Id, Company.Id, Year.Id, new YearEndReversalInput(D("2026-12-31")));
        Assert.True(rr.IsOk, $"{rr.Status} {rr.Code} {string.Join(",", rr.Fields?.Select(x => x.Field + ":" + x.Code) ?? [])}");
        var reversal = rr.Value!;
        await PostYearEnd(reversal.Header.Id);
        var reopened = Ok(await Get(StatementKind.BalanceSheet, Y26()));
        Assert.Equal((630m, 0m, 14580m), (reopened.Statement.Lines.Single(l => l.Key == "unclosed_result").Current, Total(reopened, "total_equity_retained"), Total(reopened, "total_assets")));   // open again: the derived line returns
        Assert.Equal(630m, Total(Ok(await Get(StatementKind.ProfitAndLoss, Y26())), "net_result"));                          // the activity never changed
        var second = Ok(await ClosingSvc.StartClosingAsync(Admin.Id, Company.Id, Year.Id));
        await PostYearEnd(second.ClosingEntryId);
        var again = Ok(await Get(StatementKind.BalanceSheet, Y26()));
        Assert.Equal((0m, 630m, 14580m), (again.Statement.Lines.Single(l => l.Key == "unclosed_result").Current, Total(again, "total_equity_retained"), Total(again, "total_assets")));
        Assert.All(again.Statement.Checks, c => Assert.Equal("passed", c.Status));
    }

    [PostgresFact]
    public async Task A_company_without_a_mapping_and_an_unclassified_account_are_shown_never_dropped_and_the_statement_is_marked_incomplete()
    {
        await Prepare(applyDefaults: false);
        await Seed2026();
        var none = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        Assert.Null(none.Mapping);
        Assert.False(none.Statement.Complete);
        Assert.Contains(none.Statement.Issues, i => i.Code == "no_mapping");
        Assert.Equal(630m, Total(none, "net_result"));                                                                       // every account still counted
        Assert.Equal("failed", none.Statement.Checks.Single(c => c.Code == "classification_complete").Status);
        // classify everything except the rent account (5202 sits under group 52 in the defaults; remove its group entry and map the others explicitly)
        var defaults = (await MapSvc.ProposeDefaultAsync(Company.Id)).ToDictionary(k => k.Key, k => k.Value);
        defaults.Remove(Acc["52"].Id);
        foreach (var code in new[] { "5201", "5203", "5204", "5205", "5208", "5209", "5210", "5211", "5212", "5214", "5216" }) defaults[Acc[code].Id] = StatementSection.OperatingExpenses;
        Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, ToInput(defaults)));
        var partial = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        Assert.False(partial.Statement.Complete);
        var issue = Assert.Single(partial.Statement.Issues);
        Assert.Equal(("unclassified_accounts", 1, -200m), (issue.Code, issue.Count, issue.Amount));
        Assert.Contains(partial.Statement.Lines, l => l.Code == "5202" && l.Key == "unclassified" && l.Current == 200m);
        Assert.Equal(630m, Total(partial, "net_result"));                                                                    // shown in its own block AND counted
        var doc = Ok(await Fin.StatementDocumentAsync(Admin.Id, Company.Id, StatementKind.ProfitAndLoss, Y26(), null, null, null));
        Assert.Contains(doc.Notices, n => n.Code == "incomplete_classification");
        // balance sheet: an unclassified asset
        Assert.True(Ok(await Get(StatementKind.BalanceSheet, Y26())).Statement.Complete);                                  // the balance sheet does not depend on the expense classification
    }

    [PostgresFact]
    public async Task An_earlier_classification_version_reproduces_the_statement_it_produced_and_a_later_change_never_reinterprets_it()
    {
        await Prepare();
        await Seed2026();
        var v1 = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        var changed = (await MapSvc.GetAsync(Company.Id, 1))!.Entries.ToDictionary(k => k.Key, k => k.Value);
        changed[Acc["4202"].Id] = StatementSection.OperatingRevenue;                                                         // FX gains moved into operating revenue (version 2)
        Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 1, ToInput(changed)));
        var current = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        Assert.Equal((2, 1520m, 0m), (current.Mapping!.VersionNo, Total(current, "total_operating_revenue"), Total(current, "total_other_income")));
        var asOfV1 = Ok(await Get(StatementKind.ProfitAndLoss, Y26(), version: 1));
        Assert.Equal((1, 1500m, 20m), (asOfV1.Mapping!.VersionNo, Total(asOfV1, "total_operating_revenue"), Total(asOfV1, "total_other_income")));
        Assert.Equal(Total(v1, "net_result"), Total(asOfV1, "net_result"));                                                  // the result never changes with the classification, only its presentation
        Assert.Equal(v1.Statement.Lines.Select(l => (l.Key, l.Code, l.Current)), asOfV1.Statement.Lines.Select(l => (l.Key, l.Code, l.Current)));   // version 1 reproduces the original statement exactly
        Assert.Equal((ConfigStatus.NotFound, "mapping_version_not_found"), ((await Get(StatementKind.ProfitAndLoss, Y26(), version: 7)).Status, (await Get(StatementKind.ProfitAndLoss, Y26(), version: 7)).Code));
    }

    [PostgresFact]
    public async Task A_service_company_without_trading_accounts_gets_a_not_applicable_trading_account_and_direct_costs_in_the_profit_and_loss()
    {
        await PrepareRbac();
        BuildChartFacade(); BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Main = await MainBranch();
        var acc = Ok(await ChartDomain.ApplyTemplateAsync(Admin.Id, Company.Id, "service")).ToDictionary(a => a.Code);
        Rep = new LedgerReportService(Resolver); RepMgmt = new LedgerReportManagementService(Rbac, Rep, Journal, new ReportOptions(), Cal);
        MapSvc = new StatementMappingService(Resolver, Tx, Clock);
        Fin = new FinancialStatementManagementService(Rbac, RepMgmt, new StatementDataReader(Resolver), MapSvc, new ReportOptions());
        Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, ToInput(await MapSvc.ProposeDefaultAsync(Company.Id))));
        await Posted(Entry("2026-02-01", Dr(acc["1101"].Id, 1000m), Cr(acc["4101"].Id, 1000m)));
        await Posted(Entry("2026-02-02", Dr(acc["5101"].Id, 300m), Cr(acc["1101"].Id, 300m)));
        await Posted(Entry("2026-02-03", Dr(acc["5201"].Id, 200m), Cr(acc["1101"].Id, 200m)));
        var trading = Ok(await Get(StatementKind.TradingAccount, Y26()));
        Assert.Equal((false, "no_cost_of_sales_accounts"), (trading.Statement.Applicable, trading.Statement.NotApplicableReason));
        Assert.Empty(trading.Statement.Lines);
        var pl = Ok(await Get(StatementKind.ProfitAndLoss, Y26()));
        Assert.Equal((300m, 700m, 500m), (Total(pl, "total_direct_service_costs"), Total(pl, "gross_profit"), Total(pl, "net_result")));
        var doc = Ok(await Fin.StatementDocumentAsync(Admin.Id, Company.Id, StatementKind.TradingAccount, Y26(), null, null, null));
        Assert.Contains(doc.Notices, n => n.Code == "not_applicable");
    }

    [PostgresFact]
    public async Task A_statement_exports_to_xlsx_with_bilingual_titles_typed_amounts_the_classification_version_and_the_incomplete_warning()
    {
        await Prepare(applyDefaults: false);
        await Seed2026();
        var defaults = (await MapSvc.ProposeDefaultAsync(Company.Id)).ToDictionary(k => k.Key, k => k.Value);
        defaults.Remove(Acc["52"].Id);                                                                                      // operating expenses lose their classification: the statement is incomplete
        foreach (var code in new[] { "5201", "5203", "5204", "5205", "5208", "5209", "5210", "5211", "5212", "5214", "5216" }) defaults[Acc[code].Id] = StatementSection.OperatingExpenses;
        Ok(await MapSvc.SaveAsync(Admin.Id, Company.Id, 0, ToInput(defaults)));
        await using var doc = Ok(await Fin.StatementDocumentAsync(Admin.Id, Company.Id, StatementKind.ProfitAndLoss, new ReportPeriodRequest(Year.Id, null, null, null, ComparisonMode.PreviousYear), null, null, null));
        var export = new ReportExportService(Tx, new ReportOptions(), [new XlsxReportRenderer()]);
        await using var file = Ok(await export.ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "xlsx", null, "pl", "en")));
        var rows = ExcelExportTests.ReadRows(file.Content);
        Assert.Equal("Profit and loss statement", rows[0][0]);
        Assert.Contains(rows, r => r.Count >= 2 && r[0] == "Classification version" && r[1] == "v1");
        Assert.Contains(rows, r => r.Contains("Merchandise sales") && r.Contains("1500"));                                  // typed number, code and English title
        Assert.Contains(rows, r => r.Contains("Profit or loss for the period") && r.Contains("630"));
        Assert.Contains(rows, r => r.Count > 0 && r[0].StartsWith("Warning: this statement is INCOMPLETE"));
        Assert.Contains(rows, r => r.Contains("Comparative") && r.Any(c => c.Contains("n/a: no_previous_year")));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ authorization

    [PostgresFact]
    public async Task Statements_need_their_own_permissions_a_branch_viewer_sees_only_the_branch_and_a_file_needs_the_export_permission()
    {
        await Prepare();
        await Posted(Entry("2026-02-01", Dr(Acc["1101"].Id, 500m), Cr(Acc["4101"].Id, 500m)));                             // Main
        await Posted(Entry("2026-02-02", Dr(Acc["1101"].Id, 70m, null, B2), Cr(Acc["4101"].Id, 70m, null, B2)));            // branch B2 only
        var (ledgerOnly, _) = await Member([PermissionCatalog.Codes.LedgerReportsView, PermissionCatalog.Codes.LedgerReportsExport], ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await Get(StatementKind.ProfitAndLoss, Y26(), ledgerOnly.Id)).Status);          // ledger-report permissions do not open the statements
        var (mainOnly, _) = await Member([PermissionCatalog.Codes.FinancialStatementsView], ScopeKind.Branch, Main);
        var all = Ok(await Get(StatementKind.ProfitAndLoss, Y26(), Admin.Id));
        var mine = Ok(await Get(StatementKind.ProfitAndLoss, Y26(), mainOnly.Id));
        Assert.Equal((570m, 500m), (Total(all, "total_operating_revenue"), Total(mine, "total_operating_revenue")));
        var bsMine = Ok(await Get(StatementKind.BalanceSheet, Y26(), mainOnly.Id));
        Assert.Equal("not_applicable", bsMine.Statement.Checks.Single(c => c.Code == "accounting_equation").Status);            // a branch view is not a closed set of books
        // comparison columns carry nothing the viewer may not see
        await Posted(Entry("2026-03-01", Dr(Acc["1101"].Id, 5m), Cr(Acc["4101"].Id, 5m)));
        var cmp = Ok(await Get(StatementKind.ProfitAndLoss, new ReportPeriodRequest(null, null, D("2026-03-01"), D("2026-03-31"), ComparisonMode.PreviousPeriod), mainOnly.Id));
        Assert.Equal((5m, 500m), (Total(cmp, "total_operating_revenue"), TotalC(cmp, "total_operating_revenue")!.Value));   // February: Main's 500 only, never B2's 70
        // viewing is not exporting
        Assert.Equal(ConfigStatus.Forbidden, (await Fin.StatementDocumentAsync(mainOnly.Id, Company.Id, StatementKind.ProfitAndLoss, Y26(), null, null, null)).Status);
        var (exporter, _) = await Member([PermissionCatalog.Codes.FinancialStatementsView, PermissionCatalog.Codes.FinancialStatementsExport], ScopeKind.Branch, Main);
        await using var doc = Ok(await Fin.StatementDocumentAsync(exporter.Id, Company.Id, StatementKind.ProfitAndLoss, Y26(), null, null, null));
        await using var file = Ok(await new ReportExportService(Tx, new ReportOptions(), [new CsvReportRenderer()]).ExportAsync(new ReportExportRequest(Company.Id, exporter.Id, doc, "csv", null, "pl")));
        var text = new StreamReader(file.Content, new UTF8Encoding(true)).ReadToEnd();
        Assert.True(text.Contains("505.0000"), text);
        Assert.DoesNotContain("575.0000", text);
        Assert.Equal(1L, await Events(ReportAuditEvents.Exported));
        Assert.Equal(ConfigStatus.Forbidden, (await Get(StatementKind.ProfitAndLoss, Y26(), Guid.NewGuid())).Status);
        Assert.Equal(["balance-sheet", "profit-and-loss", "trading-account"], (await RepMgmt.DefinitionsAsync(mainOnly.Id, Company.Id)).Select(d => d.Definition.Id).Order());
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ HTTP

    [PostgresFact]
    public async Task The_statement_and_classification_endpoints_work_over_http_with_etags_exports_and_authorization()
    {
        await StartApi();
        await Prepare();
        await Seed2026();
        const string Base = "/api/v1/company/statements";
        var cookie = await SignIn(Admin);
        var pl = await Send(HttpMethod.Get, $"{Base}/profit-and-loss?fiscalYearId={Year.Id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, pl.Status);
        Assert.Contains("no-store", pl.Headers.CacheControl!.ToString());
        Assert.True(pl.Json.GetProperty("complete").GetBoolean());
        var net = pl.Json.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "net_result");
        Assert.Equal("630.0000", net.GetProperty("current").GetString());
        Assert.Equal(1, pl.Json.GetProperty("mapping").GetProperty("versionNo").GetInt32());
        var bs = await Send(HttpMethod.Get, $"{Base}/balance-sheet?fiscalYearId={Year.Id}&comparison=previous_year", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, bs.Status);
        Assert.Equal("14580.0000", bs.Json.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "total_assets").GetProperty("current").GetString());
        Assert.Equal("no_previous_year", bs.Json.GetProperty("period").GetProperty("comparativeUnavailable").GetString());
        Assert.True((await Send(HttpMethod.Get, $"{Base}/trading-account?fiscalYearId={Year.Id}", cookie: cookie)).Json.GetProperty("applicable").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/cash-flow?fiscalYearId={Year.Id}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/profit-and-loss", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/profit-and-loss?fiscalYearId={Year.Id}&comparison=bogus", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/profit-and-loss?fiscalYearId={Year.Id}&mappingVersion=9", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/profit-and-loss?fiscalYearId={Year.Id}")).Status);
        var csv = await client.SendAsync(Req($"{Base}/profit-and-loss?fiscalYearId={Year.Id}&format=csv&columns=nameEn,amount", cookie));
        var csvText = await csv.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains("Profit or loss for the period,630.0000", csvText);
        // the classification: etag = version number, If-Match required, stale refused, suggestion, versions
        var map = await Send(HttpMethod.Get, $"{Base}/mapping", cookie: cookie);
        Assert.Equal("\"1\"", map.Headers.ETag!.Tag);
        Assert.Contains(map.Json.GetProperty("accounts").EnumerateArray(), a => a.GetProperty("code").GetString() == "1101" && a.GetProperty("effectiveSection").GetString() == "current_assets");
        Assert.True(map.Json.GetProperty("sections").GetArrayLength() >= 14);
        var entries = map.Json.GetProperty("entries").EnumerateArray().Select(e => new { accountId = e.GetProperty("accountId").GetGuid(), section = e.GetProperty("section").GetString() }).ToList();
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Put, $"{Base}/mapping", new { entries, note = "x" }, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Put, $"{Base}/mapping", new { entries, note = "x" }, cookie, ifMatch: "\"0\"")).Status);
        var changed = entries.Append(new { accountId = Acc["4202"].Id, section = "operating_revenue" }).ToList();
        var saved = await Send(HttpMethod.Put, $"{Base}/mapping", new { entries = changed, note = "FX as revenue" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal(2, saved.Json.GetProperty("version").GetProperty("versionNo").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/mapping", new { entries = new[] { new { accountId = Acc["1101"].Id, section = "operating_revenue" } } }, cookie, ifMatch: "\"2\"")).Status);
        Assert.Equal(2, (await Send(HttpMethod.Get, $"{Base}/mapping/versions", cookie: cookie)).Json.GetProperty("versions").GetArrayLength());
        Assert.True((await Send(HttpMethod.Get, $"{Base}/mapping/suggestion", cookie: cookie)).Json.GetProperty("entries").GetArrayLength() >= 14);
        // a user without the statement permissions sees nothing
        var (nobody, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Company, null);
        var other = await SignIn(nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/profit-and-loss?fiscalYearId={Year.Id}", cookie: other)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/mapping", cookie: other)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/mapping", new { entries = changed }, other, ifMatch: "\"2\"")).Status);
        var defs = await Send(HttpMethod.Get, "/api/v1/company/reports/definitions", cookie: cookie);
        Assert.Contains("profit-and-loss", defs.Json.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
    }

    private HttpRequestMessage Req(string path, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return req;
    }
}
