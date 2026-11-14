using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Reporting;

namespace Hasm.Infrastructure.Tests;

/// <summary>The financial statement classification and builders (pure): sections, type compatibility, inheritance, template defaults, and the statements' structure, totals, unclassified handling and checks.</summary>
public class StatementUnitTests
{
    private static StatementAccount Acc(string code, AccountType type, bool group = false, StatementAccount? parent = null, AccountNature? nature = null, string? template = null, string? en = null) =>
        new(Guid.NewGuid(), code, null, en ?? code, parent?.Id, type, nature ?? ConfigurationValidation.DefaultNature(type), group, template);

    /// <summary>The accounts of a starter template exactly as a company that applied it holds them.</summary>
    private static List<StatementAccount> Applied(string key)
    {
        var template = ChartTemplates.Find(key)!;
        var ids = template.Accounts.ToDictionary(a => a.Code, _ => Guid.NewGuid());
        return [.. template.Accounts.Select(a => new StatementAccount(ids[a.Code], a.Code, a.NameAr, a.NameEn, a.ParentCode is null ? null : ids[a.ParentCode], a.Type, a.Nature, a.IsGroup, key))];
    }

    [Fact]
    public void Section_keys_round_trip_and_each_account_type_has_its_own_sections_only()
    {
        foreach (var s in StatementSections.All) { Assert.True(StatementSections.TryParse(StatementSections.Key(s), out var back)); Assert.Equal(s, back); }
        Assert.False(StatementSections.TryParse("nonsense", out _));
        Assert.True(StatementSections.TryParse(" CURRENT_ASSETS ", out var ca) && ca == StatementSection.CurrentAssets);
        // every section belongs to exactly one account type, and the table is total
        foreach (var s in StatementSections.All) Assert.Single(Enum.GetValues<AccountType>(), t => StatementSections.Allows(t, s));
        Assert.True(StatementSections.IsBalanceSheet(StatementSection.EquityOther) && !StatementSections.IsBalanceSheet(StatementSection.OperatingRevenue));
        Assert.False(StatementSections.Allows(AccountType.Asset, StatementSection.OperatingExpenses));
    }

    [Fact]
    public void An_account_inherits_the_nearest_mapped_ancestor_a_more_specific_mapping_wins_and_an_unmapped_account_is_unclassified()
    {
        var group = Acc("11", AccountType.Asset, true); var cash = Acc("1101", AccountType.Asset, false, group); var fixedAsset = Acc("1201", AccountType.Asset, false, group); var other = Acc("1301", AccountType.Asset);
        var map = new Dictionary<Guid, StatementSection> { [group.Id] = StatementSection.CurrentAssets, [fixedAsset.Id] = StatementSection.NonCurrentAssets };
        var r = StatementClassifier.Resolve([group, cash, fixedAsset, other], map);
        Assert.Equal((StatementSection.CurrentAssets, StatementSection.CurrentAssets, StatementSection.NonCurrentAssets, (StatementSection?)null), (r[group.Id], r[cash.Id], r[fixedAsset.Id], r[other.Id]));
    }

    [Fact]
    public void An_incompatible_mapping_entry_is_ignored_by_the_classifier_so_a_wrong_entry_can_never_misplace_an_account()
    {
        var a = Acc("1101", AccountType.Asset);
        Assert.Null(StatementClassifier.Resolve([a], new Dictionary<Guid, StatementSection> { [a.Id] = StatementSection.OperatingRevenue })[a.Id]);
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("service")]
    [InlineData("retail")]
    [InlineData("wholesale")]
    public void The_defaults_classify_every_postable_account_of_every_starter_template_into_a_compatible_section(string key)
    {
        var accounts = Applied(key);
        var sections = StatementClassifier.Resolve(accounts, StatementDefaults.Propose(accounts));
        foreach (var a in accounts.Where(a => !a.IsGroup))
        {
            Assert.True(sections[a.Id] is not null, $"{key} {a.Code} {a.NameEn} is unclassified");
            Assert.True(StatementSections.Allows(a.Type, sections[a.Id]!.Value), $"{key} {a.Code}");
        }
        Assert.Equal(StatementSection.EquityRetained, sections[accounts.Single(a => a.Code == "3102").Id]);
    }

    [Fact]
    public void The_template_defaults_put_contra_revenue_in_reductions_fx_and_financing_apart_and_direct_service_costs_apart_from_cost_of_sales()
    {
        StatementSection? Of(List<StatementAccount> accounts, string code) => StatementClassifier.Resolve(accounts, StatementDefaults.Propose(accounts))[accounts.Single(a => a.Code == code).Id];
        var retail = Applied("retail");
        Assert.Equal(StatementSection.RevenueReductions, Of(retail, "4102"));                 // sales returns
        Assert.Equal(StatementSection.RevenueReductions, Of(retail, "4103"));                 // sales discounts allowed
        Assert.Equal(StatementSection.OtherIncome, Of(retail, "4202"));                       // foreign exchange gains: never sales revenue
        Assert.Equal(StatementSection.OtherIncome, Of(retail, "4203"));                       // cash surpluses
        Assert.Equal(StatementSection.FinanceAndOtherExpenses, Of(retail, "5215"));           // foreign exchange losses
        Assert.Equal(StatementSection.FinanceAndOtherExpenses, Of(retail, "5213"));           // bank charges
        Assert.Equal(StatementSection.OperatingExpenses, Of(retail, "5210"));                 // cash shortages
        Assert.Equal(StatementSection.CostOfSales, Of(retail, "5104"));                       // purchase returns (contra, but cost of sales)
        Assert.Equal(StatementSection.CurrentAssets, Of(retail, "11202"));                    // goods in transit under inventory under current assets
        Assert.Equal(StatementSection.NonCurrentAssets, Of(retail, "1206"));                  // accumulated amortization
        var service = Applied("service");
        Assert.Equal(StatementSection.DirectServiceCosts, Of(service, "5101"));
        Assert.Equal(StatementSection.RevenueReductions, Of(service, "4103"));
        Assert.Equal(StatementSection.OperatingRevenue, Of(service, "4102"));                 // contract revenue is revenue, not a reduction
        var generic = Applied("generic");
        Assert.Equal(StatementSection.CostOfSales, Of(generic, "5101"));
        Assert.Equal(StatementSection.FinanceAndOtherExpenses, Of(generic, "5206"));
        Assert.Equal(StatementSection.RevenueReductions, Of(generic, "4102"));
    }

    [Fact]
    public void A_user_created_account_is_never_given_a_default_classification()
    {
        var mine = Acc("1101", AccountType.Asset);
        var map = StatementDefaults.Propose([mine, Acc("4102", AccountType.Revenue, false, null, AccountNature.Debit)]);
        Assert.Empty(map);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ builders

    private static (List<StatementAccount> Accounts, Dictionary<Guid, StatementSection> Map, Dictionary<string, StatementAccount> By) Trading()
    {
        var by = new Dictionary<string, StatementAccount>();
        StatementAccount Add(string code, AccountType t, bool g = false, string? parent = null, AccountNature? n = null) => by[code] = Acc(code, t, g, parent is null ? null : by[parent], n);
        Add("1", AccountType.Asset, true); Add("1101", AccountType.Asset, false, "1"); Add("1201", AccountType.Asset, false, "1"); Add("1202", AccountType.Asset, false, "1", AccountNature.Credit);
        Add("2101", AccountType.Liability); Add("3101", AccountType.Equity); Add("3102", AccountType.Equity);
        Add("4101", AccountType.Revenue); Add("4102", AccountType.Revenue, false, null, AccountNature.Debit); Add("4201", AccountType.Revenue);
        Add("5101", AccountType.Expense); Add("5201", AccountType.Expense); Add("5301", AccountType.Expense);
        var map = new Dictionary<Guid, StatementSection>
        {
            [by["1"].Id] = StatementSection.CurrentAssets, [by["1201"].Id] = StatementSection.NonCurrentAssets, [by["1202"].Id] = StatementSection.NonCurrentAssets, [by["2101"].Id] = StatementSection.CurrentLiabilities,
            [by["3101"].Id] = StatementSection.EquityCapital, [by["3102"].Id] = StatementSection.EquityRetained, [by["4101"].Id] = StatementSection.OperatingRevenue, [by["4102"].Id] = StatementSection.RevenueReductions,
            [by["4201"].Id] = StatementSection.OtherIncome, [by["5101"].Id] = StatementSection.CostOfSales, [by["5201"].Id] = StatementSection.OperatingExpenses, [by["5301"].Id] = StatementSection.FinanceAndOtherExpenses,
        };
        return ([.. by.Values], map, by);
    }

    private static Dictionary<Guid, decimal> Bal(Dictionary<string, StatementAccount> by, params (string Code, decimal Signed)[] v) => v.ToDictionary(x => by[x.Code].Id, x => x.Signed);
    private static decimal Line(FinancialStatement s, string key) => s.Lines.Single(l => l.Key == key && l.Kind is "total" or "subtotal").Current;

    [Fact]
    public void The_profit_and_loss_has_the_approved_structure_signs_and_totals()
    {
        var (accounts, map, by) = Trading();
        var sections = StatementClassifier.Resolve(accounts, map);
        // revenue 1000 (credit), returns 100 (debit), cost of sales 400, operating expenses 150, other income 20 (credit), finance expense 30
        var movement = Bal(by, ("4101", -1000m), ("4102", 100m), ("5101", 400m), ("5201", 150m), ("4201", -20m), ("5301", 30m));
        var s = StatementBuilder.ProfitAndLoss(accounts, sections, movement, null, false);
        Assert.True(s.Applicable && s.Complete);
        Assert.Equal((1000m, -100m, 900m), (Line(s, "total_operating_revenue"), Line(s, "total_revenue_reductions"), Line(s, "net_revenue")));      // reductions negative: contra revenue
        Assert.Equal((400m, 500m, 350m), (Line(s, "total_cost_of_sales"), Line(s, "gross_profit"), Line(s, "operating_profit")));
        Assert.Equal((20m, 30m, 340m), (Line(s, "total_other_income"), Line(s, "total_finance_and_other_expenses"), Line(s, "net_result")));
        Assert.Equal(["passed", "passed"], s.Checks.Select(c => c.Status));
        Assert.Empty(s.Issues);
    }

    [Fact]
    public void Gross_profit_is_shown_only_when_cost_of_sales_or_direct_service_costs_are_classified()
    {
        var (accounts, map, by) = Trading();
        map.Remove(by["5101"].Id);
        var noCost = StatementBuilder.ProfitAndLoss(accounts, StatementClassifier.Resolve(accounts, map), Bal(by, ("4101", -1000m), ("5201", 150m)), null, false);
        Assert.DoesNotContain(noCost.Lines, l => l.Key == "gross_profit");
        Assert.Equal(850m, Line(noCost, "net_result"));
        map[by["5101"].Id] = StatementSection.CostOfSales;
        Assert.Contains(StatementBuilder.ProfitAndLoss(accounts, StatementClassifier.Resolve(accounts, map), Bal(by, ("4101", -1000m), ("5101", 100m)), null, false).Lines, l => l.Key == "gross_profit");
    }

    [Fact]
    public void An_unclassified_account_with_a_balance_is_shown_in_its_own_block_counted_in_the_result_and_marks_the_statement_incomplete()
    {
        var (accounts, map, by) = Trading();
        map.Remove(by["5201"].Id);                                                                                          // operating expenses account loses its classification
        var s = StatementBuilder.ProfitAndLoss(accounts, StatementClassifier.Resolve(accounts, map), Bal(by, ("4101", -1000m), ("5201", 150m), ("5101", 400m)), null, false);
        Assert.False(s.Complete);
        Assert.Equal(("unclassified_accounts", 1, -150m), (s.Issues[0].Code, s.Issues[0].Count, s.Issues[0].Amount));
        Assert.Contains(s.Lines, l => l.Kind == "account" && l.Code == "5201" && l.Key == "unclassified" && l.Current == 150m);
        Assert.Equal(450m, Line(s, "net_result"));                                                                           // 1000 - 400 - 150: the account is not dropped
        Assert.Equal("failed", s.Checks.Single(c => c.Code == "classification_complete").Status);
        Assert.Equal("passed", s.Checks.Single(c => c.Code == "result_matches_trial_balance").Status);                      // the figures still reconcile
        // an unclassified account WITHOUT a balance is not a problem
        var quiet = StatementBuilder.ProfitAndLoss(accounts, StatementClassifier.Resolve(accounts, map), Bal(by, ("4101", -1000m)), null, false);
        Assert.True(quiet.Complete);
    }

    [Fact]
    public void Without_any_mapping_everything_is_unclassified_and_the_statement_says_so()
    {
        var (accounts, _, by) = Trading();
        var none = StatementClassifier.Resolve(accounts, new Dictionary<Guid, StatementSection>());
        var s = StatementBuilder.ProfitAndLoss(accounts, none, Bal(by, ("4101", -10m), ("5201", 4m)), null, true);
        Assert.False(s.Complete);
        Assert.Contains(s.Issues, i => i.Code == "no_mapping");
        Assert.Equal(6m, Line(s, "net_result"));
    }

    [Fact]
    public void Comparative_columns_are_computed_for_every_line_and_the_change_needs_both_sides()
    {
        var (accounts, map, by) = Trading();
        var sections = StatementClassifier.Resolve(accounts, map);
        var s = StatementBuilder.ProfitAndLoss(accounts, sections, Bal(by, ("4101", -1000m), ("5201", 100m)), Bal(by, ("4101", -600m), ("5201", 50m)), false);
        Assert.Equal((900m, 550m), (s.Lines.Single(l => l.Key == "net_result").Current, s.Lines.Single(l => l.Key == "net_result").Comparative));
        var none = StatementBuilder.ProfitAndLoss(accounts, sections, Bal(by, ("4101", -1000m)), null, false);
        Assert.All(none.Lines, l => Assert.Null(l.Comparative));
    }

    [Fact]
    public void The_trading_account_applies_only_to_a_company_that_classifies_cost_of_sales_and_never_infers_inventory()
    {
        var (accounts, map, by) = Trading();
        var s = StatementBuilder.TradingAccount(accounts, StatementClassifier.Resolve(accounts, map), Bal(by, ("4101", -1000m), ("4102", 100m), ("5101", 400m)), null, false);
        Assert.True(s.Applicable);
        Assert.Equal((900m, 400m, 500m), (Line(s, "net_sales"), Line(s, "total_cost_of_sales"), Line(s, "gross_profit")));
        Assert.Equal("passed", s.Checks.Single(c => c.Code == "net_sales_matches_trial_balance").Status);
        // a service company: direct service costs are NOT cost of sales, so the statement does not apply and says why
        var service = Applied("service");
        var sections = StatementClassifier.Resolve(service, StatementDefaults.Propose(service));
        var na = StatementBuilder.TradingAccount(service, sections, new Dictionary<Guid, decimal>(), null, false);
        Assert.Equal((false, "no_cost_of_sales_accounts", true), (na.Applicable, na.NotApplicableReason, na.Complete));
        Assert.Empty(na.Lines);
        Assert.Equal("no_mapping", StatementBuilder.TradingAccount(accounts, StatementClassifier.Resolve(accounts, new Dictionary<Guid, StatementSection>()), new Dictionary<Guid, decimal>(), null, true).NotApplicableReason);
    }

    [Fact]
    public void The_balance_sheet_balances_with_the_derived_unclosed_result_and_every_account_appears_once()
    {
        var (accounts, map, by) = Trading();
        var sections = StatementClassifier.Resolve(accounts, map);
        // before closing: cash 500, fixed asset 200 less accumulated depreciation 50 (credit), payable 100, capital 300, retained 0, and an open-year result of 250 (revenue 600 - expenses 350)
        var cumulative = Bal(by, ("1101", 500m), ("1201", 200m), ("1202", -50m), ("2101", -100m), ("3101", -300m), ("4101", -600m), ("5101", 350m));
        var s = StatementBuilder.BalanceSheet(accounts, sections, cumulative, null, false, false);
        Assert.True(s.Complete);
        Assert.Equal((650m, 100m, 550m), (Line(s, "total_assets"), Line(s, "total_liabilities"), Line(s, "total_equity")));
        Assert.Equal(250m, s.Lines.Single(l => l.Key == "unclosed_result").Current);
        Assert.Equal(650m, Line(s, "total_liabilities_and_equity"));
        Assert.Equal(-50m, s.Lines.Single(l => l.Code == "1202").Current);                                                   // the contra asset is negative inside non-current assets
        Assert.All(s.Checks, c => Assert.Equal("passed", c.Status));
        Assert.Equal(accounts.Count(a => !a.IsGroup && a.Type is AccountType.Asset or AccountType.Liability or AccountType.Equity && cumulative.ContainsKey(a.Id)), s.Lines.Count(l => l.Kind == "account" && l.Code is not null));
        // after the closing entries: the result sits in retained earnings and the derived line is zero; the sheet still balances and totals are unchanged
        var closed = Bal(by, ("1101", 500m), ("1201", 200m), ("1202", -50m), ("2101", -100m), ("3101", -300m), ("3102", -250m));
        var after = StatementBuilder.BalanceSheet(accounts, sections, closed, null, false, false);
        Assert.Equal((0m, 550m, 650m), (after.Lines.Single(l => l.Key == "unclosed_result").Current, Line(after, "total_equity"), Line(after, "total_assets")));
        Assert.All(after.Checks, c => Assert.Equal("passed", c.Status));
    }

    [Fact]
    public void An_out_of_balance_ledger_is_reported_by_the_accounting_equation_check_unless_the_view_is_restricted()
    {
        var (accounts, map, by) = Trading();
        var sections = StatementClassifier.Resolve(accounts, map);
        var broken = Bal(by, ("1101", 500m), ("3101", -300m));
        Assert.Equal(("failed", 200m), (StatementBuilder.BalanceSheet(accounts, sections, broken, null, false, false).Checks.Single(c => c.Code == "accounting_equation").Status, StatementBuilder.BalanceSheet(accounts, sections, broken, null, false, false).Checks.Single(c => c.Code == "accounting_equation").Difference));
        Assert.Equal("not_applicable", StatementBuilder.BalanceSheet(accounts, sections, broken, null, false, true).Checks.Single(c => c.Code == "accounting_equation").Status);
    }

    [Fact]
    public void Unclassified_balance_sheet_accounts_are_shown_by_type_and_counted_in_the_totals()
    {
        var (accounts, map, by) = Trading();
        map.Remove(by["2101"].Id); map.Remove(by["3102"].Id); map.Remove(by["1"].Id);
        var s = StatementBuilder.BalanceSheet(accounts, StatementClassifier.Resolve(accounts, map), Bal(by, ("1101", 500m), ("1201", 200m), ("2101", -100m), ("3101", -300m), ("3102", -300m)), null, false, false);
        Assert.False(s.Complete);
        Assert.Equal(3, s.Issues.Count(i => i.Code == "unclassified_accounts"));                                              // assets (1101, which lost its group), liabilities (2101) and equity (3102)
        Assert.Contains(s.Lines, l => l.Key == "unclassified_assets" && l.Code == "1101");
        Assert.Equal(700m, Line(s, "total_assets"));
        Assert.Equal(100m, Line(s, "total_liabilities"));
        Assert.Equal("passed", s.Checks.Single(c => c.Code == "accounting_equation").Status);                                 // still reconciles
    }

    [Fact]
    public void The_titles_exist_in_both_languages_for_every_key_a_statement_can_produce_and_totals_are_derived()
    {
        var (accounts, map, by) = Trading();
        var sections = StatementClassifier.Resolve(accounts, map);
        var all = new[]
        {
            StatementBuilder.ProfitAndLoss(accounts, sections, Bal(by, ("4101", -1m), ("5101", 1m), ("5201", 1m), ("4201", -1m), ("5301", 1m)), null, false),
            StatementBuilder.BalanceSheet(accounts, sections, Bal(by, ("1101", 1m), ("2101", -1m), ("3101", 1m)), null, false, false),
            StatementBuilder.TradingAccount(accounts, sections, Bal(by, ("4101", -1m), ("5101", 1m)), null, false),
        };
        foreach (var l in all.SelectMany(s => s.Lines).Where(l => l.AccountId is null && l.Key != "unclosed_result"))
        {
            var (ar, en) = StatementTitles.Of(l.Key);
            Assert.False(ar == l.Key || en == l.Key, $"missing title for {l.Key}");
        }
    }
}
