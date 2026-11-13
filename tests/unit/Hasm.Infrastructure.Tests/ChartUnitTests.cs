using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2A: account-code normalisation, validation, templates, permission model and the static shape of the migrations (no database needed).</summary>
public class ChartUnitTests
{
    private static string CompanySql => MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 7).Sql;
    private static string CompanyCode => Regex.Replace(CompanySql, "--[^\n]*", "");
    private static string SystemCode => Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == 10).Sql, "--[^\n]*", "");

    [Theory]
    [InlineData("1", "1")]
    [InlineData("  ab-1 ", "AB-1")]
    [InlineData("1101.01", "1101.01")]
    [InlineData("a_b.c-d", "A_B.C-D")]
    [InlineData("٢٠٠١", "2001")]                      // Arabic-Indic digits
    [InlineData("۱۲۳۴", "1234")]                      // Extended Arabic-Indic digits
    [InlineData("١A٢", "1A2")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
    public void Codes_are_normalised_consistently(string raw, string expected) => Assert.Equal(expected, ConfigurationValidation.NormalizeAccountCode(raw));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("-1")] [InlineData(".1")] [InlineData("_1")] [InlineData("A B")] [InlineData("A/B")] [InlineData("A\u0000")]
    [InlineData("1234567890123456789012345678901")] [InlineData("حساب")] [InlineData("A:B")]
    public void Invalid_codes_are_rejected(string? raw) => Assert.Null(ConfigurationValidation.NormalizeAccountCode(raw));

    [Fact]
    public void The_hierarchy_never_depends_on_the_code_format_or_a_fixed_depth()
    {
        // Codes are free-form within the shape; nothing parses a prefix to find the parent and no depth limit exists in the model or the SQL.
        Assert.Equal("X.Y.Z", ConfigurationValidation.NormalizeAccountCode("x.y.z"));
        Assert.DoesNotMatch(@"depth\s*[<>]=?\s*\d", CompanyCode.Replace("WHERE t.depth", ""));
        Assert.Contains("WITH RECURSIVE", CompanyCode);
    }

    [Theory]
    [InlineData("asset", AccountType.Asset)] [InlineData(" Liability ", AccountType.Liability)] [InlineData("EQUITY", AccountType.Equity)]
    [InlineData("revenue", AccountType.Revenue)] [InlineData("expense", AccountType.Expense)]
    public void Account_types_parse_case_insensitively(string raw, AccountType expected)
    {
        Assert.True(ConfigurationValidation.TryParseAccountType(raw, out var t));
        Assert.Equal(expected, t);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("income")] [InlineData("assets")]
    public void Unknown_account_types_are_rejected(string? raw) => Assert.False(ConfigurationValidation.TryParseAccountType(raw, out _));

    [Fact]
    public void Persisted_enum_values_and_the_statement_split_never_change()
    {
        Assert.Equal([1, 2, 3, 4, 5], new[] { AccountType.Asset, AccountType.Liability, AccountType.Equity, AccountType.Revenue, AccountType.Expense }.Select(x => (int)x));
        Assert.Equal([1, 2], new[] { AccountNature.Debit, AccountNature.Credit }.Select(x => (int)x));
        Assert.Equal([1, 2], new[] { AccountStatement.BalanceSheet, AccountStatement.ProfitAndLoss }.Select(x => (int)x));
        Assert.Equal([1, 2, 3], new[] { AccountCurrencyPolicy.Base, AccountCurrencyPolicy.Any, AccountCurrencyPolicy.Specific }.Select(x => (int)x));
        Assert.Equal(AccountNature.Debit, ConfigurationValidation.DefaultNature(AccountType.Asset));
        Assert.Equal(AccountNature.Debit, ConfigurationValidation.DefaultNature(AccountType.Expense));
        foreach (var t in new[] { AccountType.Liability, AccountType.Equity, AccountType.Revenue }) Assert.Equal(AccountNature.Credit, ConfigurationValidation.DefaultNature(t));
    }

    [Fact]
    public void A_valid_account_input_is_accepted_with_defaults()
    {
        Assert.True(ConfigurationValidation.TryNormalizeAccount(new(" 11 ", null, " نقد ", null, "Asset", null, null, null, null), out var code, out var ar, out var en, out var type, out var nature,
            out var group, out var policy, out var cur, out var errors), string.Join(',', errors));
        Assert.Equal(("11", "نقد", null, AccountType.Asset, null, false, AccountCurrencyPolicy.Base, null), (code, ar, en, type, nature, group, policy, cur));
        Assert.True(ConfigurationValidation.TryNormalizeAccount(new(null, null, null, "Cash", null, "credit", true, "specific", "usd"), out code, out _, out _, out _, out nature, out group, out policy, out cur, out _));
        Assert.Equal((null, AccountNature.Credit, true, AccountCurrencyPolicy.Specific, "USD"), (code, nature, group, policy, cur));
    }

    [Theory]
    [InlineData("A B", "x", null, null, null, "code")]
    [InlineData(null, null, null, null, null, "nameAr")]
    [InlineData("1", "x", "income", null, null, "type")]
    [InlineData("1", "x", null, "both", null, "nature")]
    [InlineData("1", "x", null, null, "weird", "currencyPolicy")]
    public void Invalid_account_input_names_the_failing_field(string? code, string? en, string? type, string? nature, string? policy, string field)
    {
        Assert.False(ConfigurationValidation.TryNormalizeAccount(new(code, null, null, en, type, nature, false, policy, null), out _, out _, out _, out _, out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == field);
    }

    [Theory]
    [InlineData("specific", null, "currencyCode", "required")]
    [InlineData("specific", "US", "currencyCode", "invalid_format")]
    [InlineData("base", "USD", "currencyCode", "not_allowed")]
    [InlineData("any", "USD", "currencyCode", "not_allowed")]
    public void The_currency_policy_and_code_must_agree(string policy, string? code, string field, string reason)
    {
        Assert.False(ConfigurationValidation.TryNormalizeAccount(new("1", null, null, "x", "asset", null, false, policy, code), out _, out _, out _, out _, out _, out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == field && e.Code == reason);
    }

    [Fact]
    public void Account_names_are_limited_and_at_least_one_is_required()
    {
        Assert.False(ConfigurationValidation.TryNormalizeAccount(new("1", null, new string('x', 201), null, "asset", null, false, null, null), out _, out _, out _, out _, out _, out _, out _, out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "nameAr", Code: "too_long" });
        Assert.False(ConfigurationValidation.TryNormalizeAccountDetails(new(" ", "", null, null, null, null), out _, out _, out _, out _, out _, out _, out var e2));
        Assert.Contains(e2, e => e.Code == "name_required");
        Assert.True(ConfigurationValidation.TryNormalizeAccountDetails(new(null, "Cash", "debit", false, "any", null), out _, out var en, out var nature, out var group, out var policy, out _, out _));
        Assert.Equal(("Cash", AccountNature.Debit, false, AccountCurrencyPolicy.Any), (en, nature, group, policy));
    }

    [Theory]
    [InlineData(1, true)] [InlineData(4, true)] [InlineData(0, false)] [InlineData(5, false)] [InlineData(null, false)]
    public void The_segment_width_is_bounded(int? width, bool ok) => Assert.Equal(ok, ConfigurationValidation.TryNormalizeChartSettings(new(width, true), out _, out _, out _));

    [Fact]
    public void Chart_settings_need_both_values()
    {
        Assert.False(ConfigurationValidation.TryNormalizeChartSettings(new(2, null), out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == "autoCodes");
        Assert.False(ConfigurationValidation.TryNormalizeChartSettings(null, out _, out _, out _));
        Assert.Equal(new ChartSettingsView(2, true, 0, false), ChartSettingsView.Defaults);
    }

    [Fact]
    public void The_generic_template_is_a_valid_bilingual_chart_with_real_parents_and_the_right_natures()
    {
        var t = ChartTemplates.Find("GENERIC")!;
        Assert.NotNull(t);
        Assert.Null(ChartTemplates.Find("restaurant"));                                                            // business-type templates are outstanding, not silently faked
        Assert.Equal(t.Accounts.Count, t.Accounts.Select(a => a.Code).Distinct(StringComparer.Ordinal).Count());
        var byCode = t.Accounts.ToDictionary(a => a.Code);
        Assert.All(t.Accounts, a =>
        {
            Assert.Equal(a.Code, ConfigurationValidation.NormalizeAccountCode(a.Code));
            Assert.False(string.IsNullOrWhiteSpace(a.NameAr));
            Assert.False(string.IsNullOrWhiteSpace(a.NameEn));
            if (a.ParentCode is null) return;
            var parent = byCode[a.ParentCode];
            Assert.True(parent.IsGroup, a.Code);
            Assert.Equal(parent.Type, a.Type);
            Assert.True(t.Accounts.ToList().IndexOf(parent) < t.Accounts.ToList().IndexOf(a), "parents precede children");
        });
        Assert.Equal(5, t.Accounts.Count(a => a.ParentCode is null));
        Assert.All(t.Accounts.Where(a => a.ParentCode is null), a => Assert.True(a.IsGroup));
        Assert.Equal(AccountNature.Credit, byCode["1202"].Nature);                                                 // accumulated depreciation
        Assert.Equal(AccountNature.Debit, byCode["4102"].Nature);                                                  // sales returns
        Assert.Contains(t.Accounts, a => a.Code == "5206" && a.NameEn.Contains("exchange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Chart_audit_events_are_unique_business_events_after_the_fiscal_range()
    {
        short[] events = [ConfigAuditEvents.AccountCreated, ConfigAuditEvents.AccountUpdated, ConfigAuditEvents.AccountCodeChanged, ConfigAuditEvents.AccountMoved,
            ConfigAuditEvents.AccountDeactivated, ConfigAuditEvents.AccountReactivated, ConfigAuditEvents.ChartSettingsChanged, ConfigAuditEvents.ChartTemplateApplied];
        Assert.Equal(events.Length, events.Distinct().Count());
        Assert.All(events, e => Assert.InRange(e, (short)1028, (short)1035));
    }

    [Fact]
    public void Chart_permissions_are_company_wide_separate_and_all_held_by_the_company_administrator_only()
    {
        string[] codes = ["company.accounts.view", "company.accounts.create", "company.accounts.update", "company.accounts.activate", "company.accounts.recode", "company.accounts.restructure", "company.accounts.configure"];
        foreach (var c in codes) Assert.Equal(ScopeKind.Company, PermissionCatalog.Default.Get(c).Resource.Scope);
        Assert.Equal(codes.Order(), PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.StartsWith("company.accounts")).Order());
        foreach (var (id, preset) in PresetRoles.Definitions)
            Assert.Equal(id == PresetRoles.CompanyAdministratorId, codes.All(preset.Permissions.Contains));
        var eff = new EffectivePermissions(PermissionCatalog.Default, [new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), ScopeKind.Branch, Guid.NewGuid(), new HashSet<string>(codes))]);
        Assert.All(codes, c => Assert.False(eff.Has(c, ScopeTarget.Company)));                                     // a branch grant never reaches the chart
    }

    [Fact]
    public void The_usage_guard_reports_unimplemented_domains_until_a_real_probe_replaces_them()
    {
        Assert.Equal(["general_ledger_postings"], new AccountUsageGuard().UnimplementedDomains);
        Assert.Empty(new AccountUsageGuard([new Probe()]).UnimplementedDomains);
    }

    private sealed class Probe : IAccountUsageProbe
    {
        public string Domain => "general_ledger_postings";
        public bool Implemented => true;
        public Task<bool> HasPostingsAsync(Guid companyId, Guid accountId, Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public void Account_view_postability_follows_activity_and_the_group_flag()
    {
        AccountView V(bool group, bool active) => new(Guid.NewGuid(), "1", "ا", null, null, AccountType.Asset, AccountNature.Debit, AccountStatement.BalanceSheet, group, active,
            AccountCurrencyPolicy.Base, null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);
        Assert.True(V(false, true).IsPostable);
        Assert.False(V(true, true).IsPostable);
        Assert.False(V(false, false).IsPostable);
        Assert.False(V(true, false).IsPostable);
    }

    [Fact]
    public void Company_migration_0007_creates_exactly_the_chart_tables_seeds_nothing_grants_nothing_never_drops_and_enforces_the_tree_in_the_database()
    {
        var created = Regex.Matches(CompanyCode, @"CREATE\s+TABLE\s+(\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(new[] { CompanySchema.Account.Table, CompanySchema.AccountSettings.Table, CompanySchema.AccountRevision.Table }.Order(), created);
        Assert.DoesNotMatch(@"\bINSERT\s+INTO\b", CompanyCode);
        Assert.DoesNotMatch(@"\bDROP\b", CompanyCode);
        Assert.DoesNotMatch(@"\bGRANT\b", CompanyCode);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", CompanyCode);
        Assert.Contains("an account cannot be its own ancestor", CompanyCode);
        Assert.Contains("the parent of an account must be a group account", CompanyCode);
        Assert.Contains($"REFERENCES {CompanySchema.Account.Table}", CompanyCode);                                  // the hierarchy is a real self foreign key
    }

    [Fact]
    public void All_chart_physical_names_are_opaque_unique_and_present_in_the_migration()
    {
        var names = new[] { typeof(CompanySchema.Account), typeof(CompanySchema.AccountSettings), typeof(CompanySchema.AccountRevision) }
            .SelectMany(t => t.GetFields()).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[tcfrk]_[0-9a-f]{8}$", n));
        Assert.All(names, n => Assert.Contains(n, CompanyCode));
        foreach (var other in new[] { typeof(CompanySchema.Branch), typeof(CompanySchema.Warehouse), typeof(CompanySchema.FiscalYear), typeof(CompanySchema.FiscalPeriod), typeof(CompanySchema.Profile) })
            Assert.Empty(names.Intersect(other.GetFields().Select(f => (string)f.GetRawConstantValue()!)));
    }

    [Fact]
    public void System_migration_0010_only_adds_the_chart_permissions_to_the_administrator_preset_and_restores_its_guard()
    {
        Assert.DoesNotMatch(@"\bDROP\b", SystemCode);
        Assert.DoesNotMatch(@"\bCREATE\s+TABLE\b", SystemCode);
        Assert.Contains("ENABLE TRIGGER", SystemCode);
        foreach (var c in new[] { "view", "create", "update", "activate", "recode", "restructure", "configure" })
            Assert.Contains($"('5c1b0f6e-0000-4a00-8000-000000000001', 'company.accounts.{c}')", SystemCode);
        Assert.DoesNotContain("00000000000" + "2", SystemCode);
        Assert.DoesNotContain("00000000000" + "4", SystemCode);
    }

    [Fact]
    public void Runtime_privileges_for_the_chart_never_include_delete_truncate_or_history_updates()
    {
        string[] Privileges(string table) => [.. CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges.Order()];
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.Account.Table));
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.AccountSettings.Table));
        Assert.Equal(["INSERT", "SELECT"], Privileges(CompanySchema.AccountRevision.Table));
    }
}
