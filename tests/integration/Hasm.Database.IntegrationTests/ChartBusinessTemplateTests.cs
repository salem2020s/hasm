using System.Net;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using Ar = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountRevision;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The owner-approved Service, Retail and Wholesale templates (roadmap 3.2A-2) applied through the real Chart of Accounts service and PostgreSQL: exact content, empty-chart-only rule, single application,
/// concurrency, company isolation and audit integrity. Disposable cluster only.
/// </summary>
public class ChartBusinessTemplateTests : ChartTestBase
{
    private async Task AssertAppliedExactly(string key, int expectedCount)
    {
        await PrepareChart();
        var template = ChartTemplates.Find(key)!;
        Assert.Equal(expectedCount, template.Accounts.Count);
        var applied = Ok(await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, key));
        Assert.Equal(expectedCount, applied.Count);
        Assert.Equal((long)expectedCount, await AccountRows());
        var byCode = applied.ToDictionary(a => a.Code);
        var byId = applied.ToDictionary(a => a.Id);
        foreach (var t in template.Accounts)
        {
            var a = byCode[t.Code];
            Assert.Equal((t.NameAr, t.NameEn, t.Type, t.Nature, t.IsGroup), (a.NameAr, a.NameEn, a.Type, a.Nature, a.IsGroup));
            Assert.Equal(t.ParentCode, a.ParentId is { } p ? byId[p].Code : null);                                                        // stable parent relationships
            Assert.Equal(t.Type <= AccountType.Equity ? AccountStatement.BalanceSheet : AccountStatement.ProfitAndLoss, a.Statement);      // classification follows the type
            Assert.Equal(key, a.TemplateKey);
            Assert.True(a.IsActive);
            Assert.Equal(!t.IsGroup, a.IsPostable);
            Assert.Equal((AccountCurrencyPolicy.Base, null), (a.CurrencyPolicy, a.CurrencyCode));
            if (a.ParentId is { } parent) { Assert.True(byId[parent].IsGroup); Assert.Equal(byId[parent].Type, a.Type); }
        }
        Assert.Equal(AccountNature.Credit, byCode["1204"].Nature);                                                                      // accumulated depreciation: contra asset
        Assert.Equal(AccountNature.Credit, byCode["1206"].Nature);                                                                      // accumulated amortization: contra asset
        Assert.Equal((AccountType.Equity, false, AccountNature.Credit), (byCode["3102"].Type, byCode["3102"].IsGroup, byCode["3102"].Nature));
        Assert.Equal((AccountType.Revenue, AccountNature.Credit), (byCode["4202"].Type, byCode["4202"].Nature));                        // FX gains, other income
        Assert.Equal((AccountType.Expense, AccountNature.Debit), (byCode["5215"].Type, byCode["5215"].Nature));                         // FX losses
        Assert.Equal(AuditBaseline + 1, await Count(Company, Audit_.Table));                                                            // one audited event covers the whole application
        Assert.Equal(ConfigAuditEvents.ChartTemplateApplied, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table}"));
        Assert.Equal(1L, await Count(Company, Ar.Table));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact] public Task The_service_template_creates_exactly_54_valid_accounts_on_an_empty_chart() => AssertAppliedExactly("service", 54);
    [PostgresFact] public Task The_retail_template_creates_exactly_66_valid_accounts_on_an_empty_chart() => AssertAppliedExactly("retail", 66);
    [PostgresFact] public Task The_wholesale_template_creates_exactly_64_valid_accounts_on_an_empty_chart() => AssertAppliedExactly("wholesale", 64);

    [PostgresFact]
    public async Task Every_business_template_is_refused_on_a_chart_that_already_has_accounts_and_nothing_changes()
    {
        await PrepareChart();
        var generic = Ok(await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "generic"));
        var events = await Count(Company, Audit_.Table);
        foreach (var key in new[] { "service", "retail", "wholesale", "generic" })
        {
            var refused = await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, key);
            Assert.Equal((ConfigStatus.Conflict, "chart_not_empty"), (refused.Status, refused.Code));
        }
        Assert.Equal((long)generic.Count, await AccountRows());
        Assert.Equal(events, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task A_single_manual_account_also_blocks_every_template_and_template_names_are_case_insensitive_but_unknown_ones_fail()
    {
        await PrepareChart();
        await NewRoot("9");
        foreach (var key in new[] { "service", "retail", "wholesale" })
            Assert.Equal("chart_not_empty", (await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, key)).Code);
        var unknown = await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "restaurant");
        Assert.Equal((ConfigStatus.NotFound, "template_not_found"), (unknown.Status, unknown.Code));
        Assert.Equal(1L, await AccountRows());
    }

    [PostgresFact]
    public async Task A_template_applies_only_once_and_concurrent_applications_produce_exactly_one_consistent_chart()
    {
        await PrepareChart();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Chart().ApplyTemplateAsync(Admin.Id, Company.Id, i % 2 == 0 ? "retail" : "wholesale")));
        Assert.Single(results, r => r.IsOk);
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal("chart_not_empty", r.Code));
        var winner = results.Single(r => r.IsOk).Value!;
        Assert.Equal((long)winner.Count, await AccountRows());                                                                          // no duplicated accounts, no mixture of two templates
        Assert.Single(winner.Select(a => a.TemplateKey).Distinct());
        Assert.Equal(winner.Count, winner.Select(a => a.Code).Distinct().Count());
        Assert.Equal(AuditBaseline + 1, await Count(Company, Audit_.Table));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        Assert.Equal("chart_not_empty", (await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, winner[0].TemplateKey)).Code);
    }

    [PostgresFact]
    public async Task Applying_a_template_in_one_company_never_touches_another_company()
    {
        await PrepareChart();
        var other = await ReadyCompany();
        var otherBaseline = await Count(other, Audit_.Table);
        var mine = Ok(await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "wholesale"));
        Assert.Equal(0L, await Count(other, Ac.Table));
        Assert.Equal(otherBaseline, await Count(other, Audit_.Table));
        Assert.Null(await Chart(company: other).GetAsync(other.Id, mine[0].Id));
        Assert.Empty(await Chart(company: other).ListAsync(other.Id));
        // the other company is independent: it can still apply its own (different) template, once
        var theirs = Ok(await Chart(company: other).ApplyTemplateAsync(Admin.Id, other.Id, "service"));
        Assert.Equal((54, 64), (theirs.Count, mine.Count));
        Assert.Equal(64L, await Count(Company, Ac.Table));
        Assert.Equal(54L, await Count(other, Ac.Table));
        Assert.All(theirs, a => Assert.Equal("service", a.TemplateKey));
        Assert.Equal("chart_not_empty", (await Chart(company: other).ApplyTemplateAsync(Admin.Id, other.Id, "retail")).Code);
    }

    [PostgresFact]
    public async Task Applied_business_template_accounts_are_ordinary_accounts_that_can_be_edited_and_the_hierarchy_stays_unlimited()
    {
        await PrepareChart();
        var all = Ok(await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "retail"));
        var cash = all.Single(a => a.Code == "1101");
        var renamed = Ok(await Chart().UpdateDetailsAsync(Admin.Id, Company.Id, cash.Id, cash.Revision, new AccountDetailsInput("نقدية المتجر", "Store cash", null, null, null, null)));
        Assert.Equal("Store cash", renamed.NameEn);
        // a postable account can be turned into a group by its owner only while unused; here a group (current assets) simply receives a deeper tree
        var parent = all.Single(a => a.Code == "112");
        var deeper = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, new AccountInput("11203", parent.Id, "مخزون فرعي", "Sub inventory", null, null, true, null, null)));
        var deepest = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, new AccountInput("1120301", deeper.Id, "مخزون فرعي 1", "Sub inventory 1", null, null, false, null, null)));
        Assert.Equal((AccountType.Asset, deeper.Id), (deepest.Type, deepest.ParentId));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }
}

/// <summary>The templates over HTTP: what the shared template picker receives and applies (same API for the Web and the Desktop clients).</summary>
public class ChartBusinessTemplateApiTests : ChartAccessTestBase
{
    private const string Base = "/api/v1/company/accounts";

    private async Task Start() { await StartApi(); BuildChartFacade(); }

    [PostgresFact]
    public async Task The_picker_data_lists_the_generic_and_the_three_business_templates_with_exact_counts_and_apply_creates_only_the_chosen_one()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        var templates = list.Json.GetProperty("templates").EnumerateArray().ToDictionary(t => t.GetProperty("key").GetString()!, t => (t.GetProperty("accounts").GetInt32(), t.GetProperty("nameAr").GetString(), t.GetProperty("nameEn").GetString()));
        Assert.Equal(["generic", "retail", "service", "wholesale"], templates.Keys.Order());
        Assert.Equal((38, 54, 66, 64), (templates["generic"].Item1, templates["service"].Item1, templates["retail"].Item1, templates["wholesale"].Item1));
        Assert.All(templates.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Item2) || string.IsNullOrWhiteSpace(t.Item3)));
        var applied = await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "retail" }, cookie);
        Assert.Equal(HttpStatusCode.Created, applied.Status);
        Assert.Equal(66, applied.Json.GetProperty("accounts").GetArrayLength());
        var again = await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "service" }, cookie);
        Assert.Equal((HttpStatusCode.Conflict, "chart_not_empty"), (again.Status, again.Error));
        Assert.Equal(66L, await Sql<long>($"SELECT count(*) FROM {Ac.Table}"));
    }

    [PostgresFact]
    public async Task A_user_without_the_create_permission_cannot_apply_a_business_template()
    {
        await Start();
        var (viewer, _) = await Holder(Hasm.Infrastructure.Access.PermissionCatalog.Codes.AccountsView);
        var cookie = await SignIn(viewer);
        var denied = await Send(HttpMethod.Post, $"{Base}/apply-template", new { template = "wholesale" }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ac.Table}"));
    }
}
