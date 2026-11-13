using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

public class ProfileValidationTests
{
    private static ProfileInput Input(string? nameAr = "شركة", string? nameEn = null, string? country = "SA", string? tax = null, string? reg = null,
        string? phone = null, string? email = null, string? website = null) =>
        new(nameAr, nameEn, null, reg, tax, country, null, null, null, null, null, phone, email, website);

    private static (bool Ok, ProfileData Data, List<FieldError> Errors) Run(ProfileInput i)
    {
        var ok = ConfigurationValidation.TryNormalizeProfile(i, out var data, out var errors);
        return (ok, data, errors);
    }

    [Fact]
    public void At_least_one_name_is_required_but_either_alone_is_enough()
    {
        Assert.Contains(Run(Input(nameAr: null, nameEn: null)).Errors, e => e.Code == "name_required");
        Assert.Contains(Run(Input(nameAr: "  ", nameEn: "")).Errors, e => e.Code == "name_required");
        Assert.True(Run(Input(nameAr: "شركة", nameEn: null)).Ok);
        Assert.True(Run(Input(nameAr: null, nameEn: "Acme")).Ok);
    }

    [Fact]
    public void The_country_is_required_has_no_default_and_must_be_a_real_ISO_code()
    {
        Assert.Contains(Run(Input(country: null)).Errors, e => e is { Field: "countryCode", Code: "required" });
        Assert.Contains(Run(Input(country: "  ")).Errors, e => e.Field == "countryCode");
        Assert.Contains(Run(Input(country: "XX")).Errors, e => e.Code == "unknown_country");
        Assert.Contains(Run(Input(country: "SAU")).Errors, e => e.Code == "unknown_country");
        Assert.Equal("DE", Run(Input(country: " de ")).Data.CountryCode);
        Assert.All(new[] { "SA", "AE", "EG", "US", "DE", "JP", "BR", "ZA" }, c => Assert.True(ConfigurationValidation.IsCountry(c)));
    }

    [Theory]
    [InlineData("DE 123.456.789")]
    [InlineData("HRB 12345 B (München)")]
    [InlineData("1234567890/001")]
    [InlineData("CNPJ 12.345.678/0001-95")]
    [InlineData("سجل ٣٤٥٦")]
    [InlineData("GB-123 4567 89 ÄÖÜ_x#y")]
    public void International_identifiers_are_not_restricted_by_character_rules(string id)
    {
        var r = Run(Input(tax: id, reg: id));
        Assert.True(r.Ok, string.Join(",", r.Errors.Select(e => e.Field + ":" + e.Code)));
        Assert.Equal(id.Normalize(System.Text.NormalizationForm.FormC), r.Data.TaxRegistrationNo);
    }

    [Fact]
    public void Control_characters_and_over_long_values_are_rejected()
    {
        Assert.Contains(Run(Input(tax: "abc\ndef")).Errors, e => e is { Field: "taxRegistrationNo", Code: "invalid_characters" });
        Assert.Contains(Run(Input(reg: new string('x', 65))).Errors, e => e.Code == "too_long");
        Assert.Contains(Run(Input(nameAr: new string('ش', 201))).Errors, e => e is { Field: "nameAr", Code: "too_long" });
        Assert.Contains(Run(Input(nameEn: "a\u0000b")).Errors, e => e.Code == "invalid_characters");
    }

    [Theory]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    [InlineData("a b@c.com", false)]
    [InlineData("info@example.com", true)]
    public void Email_syntax(string email, bool ok) => Assert.Equal(ok, Run(Input(email: email)).Ok);

    [Theory]
    [InlineData("+966 11 234 5678", true)]
    [InlineData("(020) 7946-0958", true)]
    [InlineData("12", false)]
    [InlineData("call me", false)]
    public void Phone_shape(string phone, bool ok) => Assert.Equal(ok, Run(Input(phone: phone)).Ok);

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("example.com", false)]
    [InlineData("https://user:pw@example.com", false)]
    public void Website_must_be_an_absolute_http_or_https_url_without_credentials(string url, bool ok) => Assert.Equal(ok, Run(Input(website: url)).Ok);

    [Fact]
    public void Values_are_trimmed_normalised_and_blank_becomes_null()
    {
        var r = Run(new ProfileInput("  شركة  ", "  ", "  ", " ", "\t", "sa", "  ", null, " City ", null, null, null, null, null));
        Assert.True(r.Ok);
        Assert.Equal("شركة", r.Data.NameAr);
        Assert.Null(r.Data.NameEn);
        Assert.Null(r.Data.LegalName);
        Assert.Null(r.Data.CommercialRegistrationNo);
        Assert.Equal("City", r.Data.City);
        Assert.Equal("é", Run(Input(nameAr: "é")).Data.NameAr);                 // NFC
        Assert.False(ConfigurationValidation.TryNormalizeProfile(null, out _, out _));
    }
}

public class SettingsValidationTests
{
    [Fact]
    public void Documented_defaults_apply_to_missing_values_and_the_time_zone_is_never_defaulted()
    {
        Assert.True(ConfigurationValidation.TryNormalizeSettings(new SettingsInput(null, null, null, null), out var d, out _));
        Assert.Equal(new SettingsData("ar", "yyyy-MM-dd", "latin", null), d);
        Assert.Equal(SettingsData.Defaults, d);
        Assert.Null(SettingsData.Defaults.TimeZone);
    }

    [Fact]
    public void Only_supported_values_are_accepted()
    {
        Assert.True(ConfigurationValidation.TryNormalizeSettings(new SettingsInput("en", "dd/MM/yyyy", "arabic-indic", "UTC"), out var d, out _));
        Assert.Equal("en", d.DefaultLanguage);
        Assert.False(ConfigurationValidation.TryNormalizeSettings(new SettingsInput("fr", null, null, null), out _, out var e1));
        Assert.Contains(e1, e => e.Field == "defaultLanguage");
        Assert.False(ConfigurationValidation.TryNormalizeSettings(new SettingsInput(null, "yy-m-d", null, null), out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeSettings(new SettingsInput(null, null, "roman", null), out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeSettings(null, out _, out _));
    }

    [Theory]
    [InlineData("UTC", true)]
    [InlineData("Europe/Paris", true)]
    [InlineData("Asia/Riyadh", true)]
    [InlineData("Mars/Phobos", false)]
    [InlineData("../etc/passwd", false)]
    [InlineData("UTC; DROP", false)]
    public void Time_zones_must_be_known_IANA_ids(string id, bool ok) => Assert.Equal(ok, ConfigurationValidation.IsKnownTimeZone(id));

    [Fact]
    public void No_financial_rounding_or_precision_setting_exists_in_this_phase()
    {
        var names = typeof(SettingsData).GetProperties().Select(p => p.Name).Concat(typeof(SettingsInput).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain(names, n => n.Contains("Decimal", StringComparison.OrdinalIgnoreCase) || n.Contains("Round", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Precision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_company_name_falls_back_to_the_other_language()
    {
        Assert.Equal("الأمل", CompanyNames.Display("الأمل", "Amal", "ar"));
        Assert.Equal("Amal", CompanyNames.Display("الأمل", "Amal", "en"));
        Assert.Equal("الأمل", CompanyNames.Display("الأمل", null, "en"));
        Assert.Equal("Amal", CompanyNames.Display(null, "Amal", "ar"));
        Assert.Equal("Amal", CompanyNames.Display(" ", "Amal", "ar"));
        Assert.Null(CompanyNames.Display(null, null, "en"));
    }
}

public class EntitlementAndLockContractTests
{
    [Fact]
    public void Trial_allows_only_one_enabled_currency_and_lists_every_approved_restriction()
    {
        var p = TrialEntitlementPolicy.Instance;
        Assert.Equal("trial", p.Edition);
        Assert.Equal(1, p.MaxEnabledCurrencies);
        var byKey = p.Restrictions.ToDictionary(r => r.Key);
        Assert.Equal(1, byKey["companies"].Limit);
        Assert.Equal(2, byKey["users"].Limit);
        Assert.Equal(1, byKey["branches"].Limit);
        Assert.Equal(1, byKey["warehouses"].Limit);
        Assert.Equal(100, byKey["posted_business_transactions"].Limit);
        Assert.Equal(Enforcement.Enforced, byKey["multi_currency"].Enforcement);
    }

    [Fact]
    public void Restrictions_that_are_not_yet_enforced_are_declared_so_and_never_silently_allowed()
    {
        var notEnforced = TrialEntitlementPolicy.Instance.Restrictions.Where(r => r.Enforcement == Enforcement.NotEnforced).Select(r => r.Key).Order().ToArray();
        Assert.Equal(["companies", "posted_business_transactions", "users"], notEnforced);   // branches (3.1B) and warehouses (3.1C) are enforced
        Assert.All(TrialEntitlementPolicy.Instance.Restrictions.Where(r => r.Enforcement == Enforcement.NotEnforced), r => Assert.False(string.IsNullOrWhiteSpace(r.Note)));
    }

    [Fact]
    public void There_is_no_allow_all_policy_in_the_production_assembly()
    {
        var policies = typeof(IEntitlementPolicy).Assembly.GetTypes().Where(t => typeof(IEntitlementPolicy).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }).ToList();
        Assert.Equal([typeof(TrialEntitlementPolicy)], policies);
    }

    [Fact]
    public async Task Every_required_financial_domain_starts_unimplemented_and_a_registered_probe_replaces_only_its_own_domain()
    {
        var guard = new FinancialHistoryGuard();
        Assert.Equal(FinancialHistoryGuard.RequiredDomains.Order(), guard.UnimplementedDomains);
        var info = await guard.EvaluateAsync(Guid.NewGuid(), null!, null!, default);
        Assert.False(info.Locked);
        Assert.False(info.ChecksImplemented);                                                    // "unlocked" is never presented as "safe"

        var real = new FinancialHistoryGuard([new StubProbe("general_ledger", implemented: true, history: true)]);
        Assert.DoesNotContain("general_ledger", real.UnimplementedDomains);
        Assert.Contains("inventory_valuation", real.UnimplementedDomains);
        Assert.True((await real.EvaluateAsync(Guid.NewGuid(), null!, null!, default)).Locked);
    }

    [Fact]
    public void An_unknown_probe_domain_cannot_hide_a_required_one()
    {
        var guard = new FinancialHistoryGuard([new StubProbe("something_else", true, false)]);
        Assert.Equal(FinancialHistoryGuard.RequiredDomains.Order(), guard.UnimplementedDomains);
    }

    private sealed class StubProbe(string domain, bool implemented, bool history) : IFinancialHistoryProbe
    {
        public string Domain => domain;
        public bool Implemented => implemented;
        public Task<bool> HasHistoryAsync(Guid companyId, Npgsql.NpgsqlConnection c, Npgsql.NpgsqlTransaction t, CancellationToken ct) => Task.FromResult(history);
    }
}

public class ConfigurationSchemaContractTests
{
    private static readonly string[] ApprovedTables =
    [
        CompanySchema.Identity.Table, CompanySchema.BusinessAudit.Table, CompanySchema.Profile.Table, CompanySchema.Settings.Table,
        CompanySchema.Currency.Table, CompanySchema.CompanyCurrency.Table, CompanySchema.ConfigRevision.Table,
        CompanySchema.Branch.Table, CompanySchema.BranchRevision.Table,   // Phase 3.1B (migration 0004)
        CompanySchema.Warehouse.Table, CompanySchema.WarehouseRevision.Table,   // Phase 3.1C (migration 0005)
        CompanySchema.FiscalYear.Table, CompanySchema.FiscalPeriod.Table, CompanySchema.FiscalHistory.Table, CompanySchema.FiscalReopenRequest.Table,   // Phase 3.1D (migration 0006): calendar structure and reopen requests, no financial data
        CompanySchema.Account.Table, CompanySchema.AccountSettings.Table, CompanySchema.AccountRevision.Table,   // Phase 3.2A (migration 0007): the chart of accounts is master data, not financial history
        CompanySchema.LedgerHistory.Table, CompanySchema.NumberingRule.Table, CompanySchema.NumberCounter.Table, CompanySchema.NumberIssue.Table, CompanySchema.ExchangeRate.Table, CompanySchema.CostCenter.Table, CompanySchema.ApprovalPolicy.Table, CompanySchema.ApprovalDecision.Table,
        CompanySchema.LedgerSettings.Table, CompanySchema.JournalEntry.Table, CompanySchema.JournalLine.Table, CompanySchema.JournalAllocation.Table, CompanySchema.JournalEvent.Table,   // Phase 3.2B S1-S5 (migration 0008): numbering, exchange rates, cost centers, approvals and the journal; no journal or balance exists yet
    ];

    /// <summary>
    /// The base-currency lock contract can only stay safe if no financial table appears without a registered probe. A new company table must
    /// be added to this list deliberately, and the PR adding it must register an <c>IFinancialHistoryProbe</c> for its domain.
    /// </summary>
    [Fact]
    public void Company_migrations_create_only_the_approved_tables_until_financial_probes_exist()
    {
        var sql = string.Join("\n", MigrationScript.LoadEmbeddedCompany().Select(s => Regex.Replace(s.Sql, "--[^\n]*", "")));
        var created = Regex.Matches(sql, @"CREATE\s+TABLE\s+(\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(ApprovedTables.Order(), created);
    }

    [Fact]
    public void Migration_0003_seeds_a_valid_currency_reference_and_no_company_data()
    {
        var sql = MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 3).Sql;
        var rows = Regex.Matches(sql, @"^\s*\('(?<c>[A-Z]{3})', '(?<n>\d{3})', '(?<name>[^']|'')+', (?<m>\d|NULL), true, 1\)", RegexOptions.Multiline).ToList();
        Assert.True(rows.Count >= 150);
        Assert.Equal(rows.Count, rows.Select(r => r.Groups["c"].Value).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.Groups["n"].Value).Distinct().Count());
        Assert.Equal("NULL", rows.Single(r => r.Groups["c"].Value == "XDR").Groups["m"].Value);                  // undefined exponent is not invented
        Assert.Equal("3", rows.Single(r => r.Groups["c"].Value == "KWD").Groups["m"].Value);
        Assert.Equal("0", rows.Single(r => r.Groups["c"].Value == "JPY").Groups["m"].Value);
        Assert.DoesNotContain("INSERT INTO " + CompanySchema.Profile.Table, sql);
        Assert.DoesNotContain("INSERT INTO " + CompanySchema.Settings.Table, sql);
        Assert.DoesNotContain("INSERT INTO " + CompanySchema.CompanyCurrency.Table, sql);
        Assert.DoesNotMatch(@"(?i)decimal|round", Regex.Replace(sql, "--[^\n]*", ""));                            // no precision/rounding behaviour in this phase
    }

    [Fact]
    public void Migration_0003_protects_history_and_references_the_audit_event()
    {
        var sql = MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 3).Sql;
        Assert.Contains("configuration revisions are append-only", sql);
        Assert.Contains($"BEFORE TRUNCATE ON {CompanySchema.ConfigRevision.Table}", sql);
        Assert.Contains($"REFERENCES {CompanySchema.BusinessAudit.Table}", sql);
        Assert.DoesNotContain("GRANT", sql);                                                                       // privileges come from grant-company-runtime only
    }

    [Fact]
    public void System_migration_0006_extends_the_registry_nullably_and_monotonically()
    {
        var sql = MigrationScript.LoadEmbedded().Single(s => s.Version == 6).Sql;
        Assert.Contains($"ADD COLUMN {SystemSchema.Company.DisplayNameAr} varchar(200) NULL", sql);
        Assert.Contains($"ADD COLUMN {SystemSchema.Company.DisplayNameEn} varchar(200) NULL", sql);
        Assert.Contains($"{SystemSchema.Company.DisplayNameRevision} bigint NOT NULL DEFAULT 0", sql);
        Assert.Contains("ENABLE TRIGGER", sql);                                                                    // the preset-role guard is restored
    }

    private static readonly string[] BranchCodes = ["company.branches.view", "company.branches.update", "company.branchstructure.create", "company.branchstructure.activate", "company.branchstructure.designate"];
    private static readonly string[] WarehouseCodes = ["company.warehouses.view", "company.warehouses.update", "company.warehousestructure.create", "company.warehousestructure.activate"];
    private static readonly string[] FiscalCodes = ["company.fiscalyears.view", "company.fiscalyears.create", "company.fiscalyears.close", "company.fiscalperiods.close", "company.fiscalperiods.finalclose", "company.fiscalperiods.reopen", "company.fiscalreopen.view", "company.fiscalreopen.create"];
    private static readonly string[] AccountCodes = ["company.accounts.view", "company.accounts.create", "company.accounts.update", "company.accounts.activate", "company.accounts.recode", "company.accounts.restructure", "company.accounts.configure"];

    [Fact]
    public void Company_administration_permissions_are_in_the_catalog_and_scoped_to_the_company()
    {
        string[] codes = ["company.profile.view", "company.profile.update", "company.settings.view", "company.settings.update", "company.currencies.view", "company.currencies.update", "company.config.view"];
        foreach (var c in codes)
        {
            Assert.True(PermissionCatalog.Default.TryGet(c, out var p), c);
            Assert.Equal(ScopeKind.Company, p.Resource.Scope);
        }
        Assert.Equal(codes.Concat(BranchCodes).Concat(WarehouseCodes).Concat(FiscalCodes).Concat(AccountCodes).Concat(["company.numbering.view", "company.numbering.configure", "company.exchangerates.view", "company.exchangerates.create", "company.costcenters.view", "company.costcenters.create", "company.costcenters.update", "company.costcenters.activate", "company.costcenters.recode", "company.costcenters.restructure", "company.approvalpolicies.view", "company.approvalpolicies.create", "company.approvalpolicies.update", "company.approvalpolicies.activate", "company.journal.view", "company.journal.create", "company.journal.update", "company.journal.submit", "company.journal.post", "company.journal.reverse", "company.journalsoftclosed.post", "company.journalrate.override", "company.ledgersettings.view", "company.ledgersettings.configure"]).Order(), PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions.Where(c => c.StartsWith("company.")).Order());
        Assert.Equal(["company.config.view"], PresetRoles.Definitions[PresetRoles.AccessAuditorId].Permissions.Where(c => c.StartsWith("company.")));
        Assert.Empty(PresetRoles.Definitions[PresetRoles.AccessManagerId].Permissions.Where(c => c.StartsWith("company.")));
    }

    [Fact]
    public void Audit_event_types_of_configuration_are_in_the_business_range_and_unique()
    {
        var types = typeof(ConfigAuditEvents).GetFields().Select(f => (short)f.GetRawConstantValue()!).ToList();
        Assert.All(types, t => Assert.True(t >= 1000));
        Assert.Equal(types.Count, types.Distinct().Count());
    }
}
