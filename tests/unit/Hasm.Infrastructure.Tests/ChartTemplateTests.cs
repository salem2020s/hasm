using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Structural validation of the shipped generic template and of the PROPOSED business-type templates (test-side data, not exposed by the product until the owner approves the content), plus the owner's
/// accounting decisions of 2026-11-14 as assertions.
/// </summary>
public class ChartTemplateTests
{
    private static TemplateAccount A(string code, string? parent, AccountType type, bool group, AccountNature? nature = null, string ar = "حساب", string en = "Account") =>
        new(code, parent, ar, en, type, nature ?? ConfigurationValidation.DefaultNature(type), group);

    private static ChartTemplate Of(params TemplateAccount[] accounts) => new("t", "قالب", "Template", accounts);

    public static IEnumerable<object[]> Proposed() => ProposedChartTemplates.All.Select(t => new object[] { t });

    [Fact]
    public void The_shipped_generic_template_is_structurally_valid_and_unchanged()
    {
        Assert.Empty(ChartTemplateValidator.Validate(ChartTemplates.Find(ChartTemplates.Generic)!));
        Assert.Equal([ChartTemplates.Generic], ChartTemplates.All.Select(t => t.Key));          // the proposed templates are NOT exposed as production options
        Assert.Equal(38, ChartTemplates.Find(ChartTemplates.Generic)!.Accounts.Count);
    }

    [Theory]
    [MemberData(nameof(Proposed))]
    public void Every_proposed_template_satisfies_every_chart_rule(ChartTemplate template) => Assert.Empty(ChartTemplateValidator.Validate(template));

    [Fact]
    public void The_three_proposed_templates_have_the_reviewed_sizes_and_are_not_registered_for_production()
    {
        Assert.Equal([54, 65, 64], ProposedChartTemplates.All.Select(t => t.Accounts.Count));
        Assert.Equal(["service", "retail", "wholesale"], ProposedChartTemplates.All.Select(t => t.Key));
        Assert.Null(ChartTemplates.Find("service"));
        Assert.Null(ChartTemplates.Find("retail"));
        Assert.Null(ChartTemplates.Find("wholesale"));
    }

    [Theory]
    [MemberData(nameof(Proposed))]
    public void Contra_accounts_are_only_postable_accounts_with_the_opposite_nature(ChartTemplate template)
    {
        foreach (var a in template.Accounts)
        {
            var contra = a.Nature != ConfigurationValidation.DefaultNature(a.Type);
            if (contra) Assert.False(a.IsGroup, a.Code);
        }
        var contras = template.Accounts.Where(a => a.Nature != ConfigurationValidation.DefaultNature(a.Type)).Select(a => a.NameEn).Order().ToList();
        var expected = new List<string> { "Accumulated amortization", "Accumulated depreciation" };
        if (template.Key == "service") expected.Add("Discounts allowed");
        else
        {
            expected.AddRange(["Purchase discounts and rebates received", "Purchase returns", "Sales discounts allowed", "Sales returns"]);
            if (template.Key == "wholesale") expected.Add("Volume rebates and incentives granted");
        }
        Assert.Equal(expected.Order(), contras);
    }

    [Theory]
    [MemberData(nameof(Proposed))]
    public void Retained_earnings_is_a_postable_equity_account_and_no_account_id_or_code_is_wired_into_posting(ChartTemplate template)
    {
        var re = template.Accounts.Single(a => a.Code == "3102");
        Assert.Equal(("Retained earnings", AccountType.Equity, false, AccountNature.Credit), (re.NameEn, re.Type, re.IsGroup, re.Nature));
        Assert.DoesNotContain(template.Accounts, a => a.Type == AccountType.Equity && a.NameEn.Contains("Current-year", StringComparison.OrdinalIgnoreCase));      // no separate current-year result account by default
    }

    [Theory]
    [MemberData(nameof(Proposed))]
    public void There_are_no_cheque_accounts_and_no_jurisdiction_specific_tax_accounts(ChartTemplate template)
    {
        Assert.DoesNotContain(template.Accounts, a => a.NameEn.Contains("cheque", StringComparison.OrdinalIgnoreCase) || a.NameAr.Contains("شيك") || a.NameAr.Contains("شيكات"));
        Assert.Equal(["Taxes payable", "Taxes receivable"], template.Accounts.Where(a => a.NameEn.Contains("tax", StringComparison.OrdinalIgnoreCase)).Select(a => a.NameEn).Order());
    }

    [Theory]
    [MemberData(nameof(Proposed))]
    public void Foreign_exchange_gains_and_losses_are_separate_profit_and_loss_accounts_and_gains_are_not_sales_revenue(ChartTemplate template)
    {
        var byCode = template.Accounts.ToDictionary(a => a.Code);
        var gains = template.Accounts.Single(a => a.NameEn == "Foreign exchange gains");
        var losses = template.Accounts.Single(a => a.NameEn == "Foreign exchange losses");
        Assert.Equal((AccountType.Revenue, AccountNature.Credit, false), (gains.Type, gains.Nature, gains.IsGroup));
        Assert.Equal((AccountType.Expense, AccountNature.Debit, false), (losses.Type, losses.Nature, losses.IsGroup));
        Assert.Equal("Other income", byCode[gains.ParentCode!].NameEn);                                                // never under Sales / Service revenue
        Assert.DoesNotContain(template.Accounts, a => a.NameEn.Contains("exchange differences", StringComparison.OrdinalIgnoreCase));      // the single combined account is gone
    }

    [Theory]
    [MemberData(nameof(Proposed))]
    public void Terminology_distinguishes_returns_discounts_and_the_other_reviewed_terms(ChartTemplate template)
    {
        string Ar(string en) => template.Accounts.Single(a => a.NameEn == en).NameAr;
        Assert.Equal("الأرباح المحتجزة", Ar("Retained earnings"));
        Assert.Equal("مجمع الإهلاك", Ar("Accumulated depreciation"));
        Assert.Equal("أرباح فروق أسعار الصرف", Ar("Foreign exchange gains"));
        Assert.Equal("خسائر فروق أسعار الصرف", Ar("Foreign exchange losses"));
        if (template.Key == "service") { Assert.Equal("الخصم المسموح به", Ar("Discounts allowed")); Assert.Equal("إيرادات مستحقة", Ar("Accrued revenue")); Assert.Equal("إيرادات مؤجلة (مقبوضة مقدماً)", Ar("Deferred revenue")); return; }
        Assert.Equal("مردودات المبيعات", Ar("Sales returns"));
        Assert.Equal("مردودات المشتريات", Ar("Purchase returns"));
        Assert.Equal("الخصم المسموح به على المبيعات", Ar("Sales discounts allowed"));
        Assert.Equal("الخصم المكتسب وحوافز المشتريات", Ar("Purchase discounts and rebates received"));
        Assert.Equal("تكلفة البضاعة المباعة", Ar("Cost of goods sold"));
        Assert.Equal("بضاعة في الطريق", Ar("Goods in transit"));
        Assert.Equal("مصاريف شحن المشتريات وتكاليف الاستيراد", Ar("Freight-in and import costs"));
    }

    // Arabic must be stored in logical Unicode order. The strings below are written as code points, independent of how any editor or terminal displays right-to-left text.
    private static string U(params int[] cp) => string.Concat(cp.Select(char.ConvertFromUtf32));

    [Fact]
    public void Arabic_names_are_stored_in_logical_unicode_order_not_reversed()
    {
        var retained = U(0x627, 0x644, 0x623, 0x631, 0x628, 0x627, 0x62D) + " " + U(0x627, 0x644, 0x645, 0x62D, 0x62A, 0x62C, 0x632, 0x629);                 // الأرباح المحتجزة
        var accumulated = U(0x645, 0x62C, 0x645, 0x639) + " " + U(0x627, 0x644, 0x625, 0x647, 0x644, 0x627, 0x643);                                          // مجمع الإهلاك
        var salesReturns = U(0x645, 0x631, 0x62F, 0x648, 0x62F, 0x627, 0x62A) + " " + U(0x627, 0x644, 0x645, 0x628, 0x64A, 0x639, 0x627, 0x62A);             // مردودات المبيعات
        var purchaseReturns = U(0x645, 0x631, 0x62F, 0x648, 0x62F, 0x627, 0x62A) + " " + U(0x627, 0x644, 0x645, 0x634, 0x62A, 0x631, 0x64A, 0x627, 0x62A);  // مردودات المشتريات
        var amortization = U(0x645, 0x62C, 0x645, 0x639) + " " + U(0x627, 0x644, 0x625, 0x637, 0x641, 0x627, 0x621);                                         // مجمع الإطفاء
        foreach (var t in ProposedChartTemplates.All)
        {
            var ar = t.Accounts.ToDictionary(a => a.NameEn, a => a.NameAr);
            Assert.Equal(retained, ar["Retained earnings"]);
            Assert.Equal(accumulated, ar["Accumulated depreciation"]);
            Assert.Equal(amortization, ar["Accumulated amortization"]);
            if (t.Key != "service") { Assert.Equal(salesReturns, ar["Sales returns"]); Assert.Equal(purchaseReturns, ar["Purchase returns"]); }
        }
        // a reversed Arabic word would start with a letter that only occurs at the END of words (ta marbuta, final forms)
        foreach (var name in ProposedChartTemplates.All.Concat(ChartTemplates.All).SelectMany(t => t.Accounts).Select(a => a.NameAr).Distinct())
            foreach (var word in name.Split([' ', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.NotEqual('\u0629', word[0]);          // ة never starts a word
                Assert.NotEqual('\u0649', word[0]);          // ى never starts a word
            }
    }

    [Fact]
    public void Tangible_depreciation_and_intangible_amortization_are_separate_accounts_in_every_template()
    {
        foreach (var t in ProposedChartTemplates.All)
        {
            var by = t.Accounts.ToDictionary(a => a.NameEn);
            Assert.Equal((AccountType.Asset, AccountNature.Credit, false), (by["Accumulated depreciation"].Type, by["Accumulated depreciation"].Nature, by["Accumulated depreciation"].IsGroup));
            Assert.Equal((AccountType.Asset, AccountNature.Credit, false), (by["Accumulated amortization"].Type, by["Accumulated amortization"].Nature, by["Accumulated amortization"].IsGroup));
            Assert.Equal((AccountType.Asset, AccountNature.Debit), (by["Software and other intangible assets"].Type, by["Software and other intangible assets"].Nature));
            Assert.Equal((AccountType.Expense, AccountType.Expense), (by["Depreciation expense"].Type, by["Amortization expense"].Type));
            Assert.Equal(by["Accumulated depreciation"].ParentCode, by["Accumulated amortization"].ParentCode);
            Assert.DoesNotContain(t.Accounts, a => a.NameEn.Contains("software", StringComparison.OrdinalIgnoreCase) && a.NameEn.Contains("computers", StringComparison.OrdinalIgnoreCase));      // software is no longer mixed with tangible computers
        }
    }

    [Fact]
    public void Inventory_related_accounts_exist_only_in_the_trading_templates_and_nothing_is_automatic()
    {
        string[] inventory = ["Inventory", "Merchandise inventory", "Goods in transit", "Goods received not invoiced (GRNI)", "Cost of goods sold", "Freight-in and import costs"];
        foreach (var t in ProposedChartTemplates.All)
        {
            var has = inventory.All(n => t.Accounts.Any(a => a.NameEn == n));
            Assert.Equal(t.Key != "service", has);
            Assert.Equal(t.Key == "service", t.Accounts.Any(a => a.NameEn == "Direct service costs"));
        }
    }

    [Fact]
    public void Trading_templates_differ_only_in_their_channel_specific_accounts()
    {
        var retail = ProposedChartTemplates.Retail.Accounts.Select(a => a.NameEn).ToHashSet();
        var wholesale = ProposedChartTemplates.Wholesale.Accounts.Select(a => a.NameEn).ToHashSet();
        Assert.Equal(["Card processing fees", "Card settlements receivable", "Cash over and short", "Store supplies and packaging"], retail.Except(wholesale).Order());
        Assert.Equal(["Sales commissions", "Volume rebates and incentives granted", "Warehousing and logistics"], wholesale.Except(retail).Order());
    }

    // ----------------------------------------------------------------------------------------------------------------------------------- the validator itself

    [Fact]
    public void The_validator_accepts_a_minimal_valid_template()
    {
        Assert.Empty(ChartTemplateValidator.Validate(Of(
            A("1", null, AccountType.Asset, true), A("1101", "1", AccountType.Asset, false), A("1204", "1", AccountType.Asset, false, AccountNature.Credit))));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing_parent")]
    [InlineData("parent_not_group")]
    [InlineData("type_mismatch")]
    [InlineData("postable_with_children")]
    [InlineData("root_not_group")]
    [InlineData("group_contra")]
    [InlineData("empty_group")]
    [InlineData("cycle")]
    [InlineData("bad_code")]
    [InlineData("lowercase_code")]
    [InlineData("no_arabic")]
    [InlineData("arabic_in_english")]
    [InlineData("vat_name")]
    [InlineData("zakat_name")]
    public void The_validator_rejects_every_kind_of_structural_violation(string kind)
    {
        var root = A("1", null, AccountType.Asset, true);
        var t = kind switch
        {
            "duplicate" => Of(root, A("1101", "1", AccountType.Asset, false), A("1101", "1", AccountType.Asset, false)),
            "missing_parent" => Of(root, A("1101", "9", AccountType.Asset, false)),
            "parent_not_group" => Of(root, A("1101", "1", AccountType.Asset, false), A("110101", "1101", AccountType.Asset, false)),
            "type_mismatch" => Of(root, A("2101", "1", AccountType.Liability, false)),
            "postable_with_children" => Of(root, A("11", "1", AccountType.Asset, false), A("1101", "11", AccountType.Asset, false)),
            "root_not_group" => Of(A("1", null, AccountType.Asset, false)),
            "group_contra" => Of(root, A("11", "1", AccountType.Asset, true, AccountNature.Credit), A("1101", "11", AccountType.Asset, false)),
            "empty_group" => Of(root, A("11", "1", AccountType.Asset, true), A("1101", "1", AccountType.Asset, false)),
            "cycle" => Of(root, A("11", "12", AccountType.Asset, true), A("12", "11", AccountType.Asset, true), A("1101", "11", AccountType.Asset, false)),
            "bad_code" => Of(root, A("11 01", "1", AccountType.Asset, false)),
            "lowercase_code" => Of(root, A("ab1", "1", AccountType.Asset, false)),
            "no_arabic" => Of(root, A("1101", "1", AccountType.Asset, false, ar: "Cash")),
            "arabic_in_english" => Of(root, A("1101", "1", AccountType.Asset, false, en: "نقدية")),
            "vat_name" => Of(root, A("1101", "1", AccountType.Asset, false, en: "VAT receivable")),
            "zakat_name" => Of(root, A("1101", "1", AccountType.Asset, false, ar: "مخصص الزكاة")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.NotEmpty(ChartTemplateValidator.Validate(t));
    }
}
