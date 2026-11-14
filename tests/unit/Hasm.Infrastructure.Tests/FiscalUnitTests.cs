using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.1D: fiscal validation, enum stability, permission model, close guard and the static shape of the migrations (no database needed).</summary>
public class FiscalUnitTests
{
    private static string CompanyCode => Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 6).Sql, "--[^\n]*", "");
    private static string SystemCode => Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == 9).Sql, "--[^\n]*", "");

    [Fact]
    public void Persisted_enum_values_never_change()
    {
        Assert.Equal([1, 2, 3], new[] { FiscalPeriodState.Open, FiscalPeriodState.SoftClosed, FiscalPeriodState.FinalClosed }.Select(s => (int)s));
        Assert.Equal([1, 2], new[] { FiscalPeriodType.Monthly, FiscalPeriodType.Quarterly }.Select(s => (int)s));
    }

    [Theory]
    [InlineData("2026-01-01", "monthly", FiscalPeriodType.Monthly)]
    [InlineData("2026-07-01", " Quarterly ", FiscalPeriodType.Quarterly)]
    public void A_valid_year_input_is_accepted(string start, string type, FiscalPeriodType expected)
    {
        Assert.True(ConfigurationValidation.TryNormalizeFiscalYear(new("  FY 26 ", DateOnly.Parse(start), type), out var name, out var s, out var t, out var errors), string.Join(',', errors));
        Assert.Equal("FY 26", name);
        Assert.Equal(DateOnly.Parse(start), s);
        Assert.Equal(expected, t);
    }

    [Theory]
    [InlineData(null, "2026-01-01", "monthly", "name")]
    [InlineData("  ", "2026-01-01", "monthly", "name")]
    [InlineData("FY", null, "monthly", "startDate")]
    [InlineData("FY", "2026-01-02", "monthly", "startDate")]
    [InlineData("FY", "2026-01-31", "quarterly", "startDate")]
    [InlineData("FY", "1800-01-01", "monthly", "startDate")]
    [InlineData("FY", "2026-01-01", "weekly", "periodType")]
    [InlineData("FY", "2026-01-01", null, "periodType")]
    public void An_invalid_year_input_is_rejected_with_the_failing_field(string? name, string? start, string? type, string field)
    {
        Assert.False(ConfigurationValidation.TryNormalizeFiscalYear(new(name, start is null ? null : DateOnly.Parse(start), type), out _, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == field);
    }

    [Fact]
    public void A_name_over_100_characters_and_a_missing_body_are_rejected()
    {
        Assert.False(ConfigurationValidation.TryNormalizeFiscalYear(new(new string('x', 101), new DateOnly(2026, 1, 1), "monthly"), out _, out _, out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "name", Code: "too_long" });
        Assert.False(ConfigurationValidation.TryNormalizeFiscalYear(null, out _, out _, out _, out var e2));
        Assert.Contains(e2, e => e.Field == "body");
    }

    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData("         ", false)] [InlineData("too short", false)]
    [InlineData("exactly 10", true)] [InlineData("A documented correction reason", true)]
    public void A_reopening_reason_needs_at_least_ten_characters(string? reason, bool ok)
    {
        Assert.Equal(ok, ConfigurationValidation.TryNormalizeReason(new(reason), out var text, out _));
        if (ok) Assert.Equal(reason!.Trim(), text);
        Assert.False(ConfigurationValidation.TryNormalizeReason(new(new string('x', 501)), out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeReason(null, out _, out _));
    }

    [Fact]
    public void Fiscal_audit_events_are_unique_business_events_after_the_warehouse_range()
    {
        short[] events = [ConfigAuditEvents.FiscalYearCreated, ConfigAuditEvents.FiscalYearClosed, ConfigAuditEvents.FiscalYearReopened, ConfigAuditEvents.FiscalPeriodSoftClosed,
            ConfigAuditEvents.FiscalPeriodReopened, ConfigAuditEvents.FiscalPeriodFinalClosed, ConfigAuditEvents.FiscalPeriodFinalReopened,
            ConfigAuditEvents.FiscalReopenRequested, ConfigAuditEvents.FiscalReopenApproved, ConfigAuditEvents.FiscalReopenDenied, ConfigAuditEvents.FiscalReopenCancelled];
        Assert.Equal(events.Length, events.Distinct().Count());
        Assert.All(events, e => Assert.InRange(e, (short)1017, (short)1027));
    }

    [Fact]
    public void All_fiscal_permissions_are_company_wide_and_only_the_fiscal_approver_preset_can_approve_reopenings()
    {
        string[] codes = ["company.fiscalyears.view", "company.fiscalyears.create", "company.fiscalyears.close",
            "company.fiscalperiods.close", "company.fiscalperiods.finalclose", "company.fiscalperiods.reopen",
            "company.fiscalreopen.view", "company.fiscalreopen.create", "company.fiscalreopen.approve"];
        foreach (var c in codes) Assert.Equal(ScopeKind.Company, PermissionCatalog.Default.Get(c).Resource.Scope);
        Assert.Equal(codes.Order(), PermissionCatalog.Default.All.Select(p => p.Code).Where(c => c.StartsWith("company.fiscal") && !c.StartsWith("company.fiscalclosing.")).Order());   // year-end closing permissions (3.2C S3) are pinned in CostCenterPolicyUnitTests
        // There is no direct one-step reopening permission any more.
        Assert.False(PermissionCatalog.Default.TryGet("company.fiscalperiods.reopenfinal", out _));
        Assert.False(PermissionCatalog.Default.TryGet("company.fiscalyears.reopen", out _));
        var holders = PresetRoles.Definitions.Where(d => d.Value.Permissions.Contains(PermissionCatalog.Codes.FiscalReopenApprove)).Select(d => d.Key).ToArray();
        Assert.Equal([PresetRoles.FiscalApproverId], holders);
        var admin = PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions;
        Assert.Contains(PermissionCatalog.Codes.FiscalPeriodsFinalClose, admin);
        Assert.Contains(PermissionCatalog.Codes.FiscalReopenRequest, admin);                          // may request ...
        Assert.DoesNotContain(PermissionCatalog.Codes.FiscalReopenApprove, admin);                    // ... never approve
        Assert.DoesNotContain(PermissionCatalog.Codes.FiscalReopenRequest, PresetRoles.Definitions[PresetRoles.FiscalApproverId].Permissions);   // the approver role cannot request
    }

    [Fact]
    public void A_branch_or_warehouse_grant_never_covers_the_company_wide_calendar()
    {
        var branch = Guid.NewGuid();
        var eff = new EffectivePermissions(PermissionCatalog.Default, [new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), ScopeKind.Branch, branch, new HashSet<string> { PermissionCatalog.Codes.FiscalYearsView })]);
        Assert.False(eff.Has(PermissionCatalog.Codes.FiscalYearsView, ScopeTarget.Company));
        Assert.False(eff.Has(PermissionCatalog.Codes.FiscalYearsView, ScopeTarget.Branch(branch)));
    }

    [Fact]
    public void The_close_guard_reports_unimplemented_domains_until_a_real_probe_replaces_them()
    {
        Assert.Equal(PeriodCloseGuard.RequiredDomains.Order(StringComparer.Ordinal), new PeriodCloseGuard().UnimplementedDomains);
        var real = new PeriodCloseGuard([new Probe(PeriodCloseGuard.RequiredDomains[0])]);
        Assert.Equal(PeriodCloseGuard.RequiredDomains.Skip(1).Order(StringComparer.Ordinal), real.UnimplementedDomains);
        Assert.Equal(4, PeriodCloseGuard.RequiredDomains.Length);   // pending postings, inventory differences, pending adjustments, year-end consistency
    }

    [Fact]
    public void A_domain_whose_module_is_absent_is_listed_as_not_applicable_never_as_implemented_and_does_not_make_closing_refuse()
    {
        var guard = new PeriodCloseGuard([new Probe("pending_accounting_postings"), new Probe("pending_adjustments"), new Probe("year_end_closing"), new Hasm.Infrastructure.Ledger.ModuleAbsentPeriodCloseProbe("inventory_valuation_differences")]);
        Assert.Empty(guard.UnimplementedDomains);
        Assert.Equal(["inventory_valuation_differences"], guard.NotApplicableDomains);
        Assert.False(new Hasm.Infrastructure.Ledger.ModuleAbsentPeriodCloseProbe("x").Implemented);                                 // never claims to be implemented
        // A domain with neither a real probe nor an absent module still counts as unimplemented.
        Assert.Equal(["year_end_closing"], new PeriodCloseGuard([new Probe("pending_accounting_postings"), new Probe("pending_adjustments"), new Hasm.Infrastructure.Ledger.ModuleAbsentPeriodCloseProbe("inventory_valuation_differences")]).UnimplementedDomains);
    }

    private sealed class Probe(string domain) : IPeriodCloseProbe
    {
        public string Domain => domain;
        public bool Implemented => true;
        public Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public void Company_migration_0006_creates_exactly_the_fiscal_tables_seeds_nothing_grants_nothing_and_never_drops()
    {
        var created = Regex.Matches(CompanyCode, @"CREATE\s+TABLE\s+(\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(new[] { CompanySchema.FiscalYear.Table, CompanySchema.FiscalPeriod.Table, CompanySchema.FiscalHistory.Table, CompanySchema.FiscalReopenRequest.Table }.Order(), created);
        Assert.Contains("requires an approved request", CompanyCode);                                   // the approval requirement is a database rule
        Assert.DoesNotMatch(@"\bINSERT\s+INTO\b", CompanyCode);
        Assert.DoesNotMatch(@"\bDROP\b", CompanyCode);
        Assert.DoesNotMatch(@"\bGRANT\b", CompanyCode);
        Assert.Contains("EXCLUDE USING gist", CompanyCode);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", CompanyCode);
        Assert.Equal(2, Regex.Matches(CompanyCode, "EXCLUDE USING gist").Count);                       // years and periods never overlap
    }

    [Fact]
    public void All_fiscal_physical_names_are_opaque_unique_and_present_in_the_migration()
    {
        var names = new[] { typeof(CompanySchema.FiscalYear), typeof(CompanySchema.FiscalPeriod), typeof(CompanySchema.FiscalHistory), typeof(CompanySchema.FiscalReopenRequest), typeof(CompanySchema.FiscalCalendar) }
            .SelectMany(t => t.GetFields()).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[tcfrk]_[0-9a-f]{8}$", n));
        Assert.All(names, n => Assert.Contains(n, CompanyCode));
        foreach (var other in new[] { typeof(CompanySchema.Branch), typeof(CompanySchema.Warehouse), typeof(CompanySchema.Profile) })
            Assert.Empty(names.Intersect(other.GetFields().Select(f => (string)f.GetRawConstantValue()!)));
    }

    [Fact]
    public void System_migration_0009_adds_the_fiscal_permissions_and_the_approver_role_and_keeps_approval_out_of_the_administrator_preset()
    {
        Assert.DoesNotMatch(@"\bDROP\b", SystemCode);
        Assert.DoesNotMatch(@"\bCREATE\s+TABLE\b", SystemCode);
        Assert.Contains("ENABLE TRIGGER", SystemCode);
        foreach (var code in new[] { "company.fiscalyears.view", "company.fiscalyears.create", "company.fiscalyears.close", "company.fiscalperiods.close", "company.fiscalperiods.finalclose", "company.fiscalperiods.reopen" })
            Assert.Contains($"'{code}'", SystemCode);
        Assert.DoesNotContain("reopenfinal", SystemCode);                                                // no direct one-step reopening permission exists
        Assert.Contains("('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalreopen.view')", SystemCode);       // the administrator may view and request ...
        Assert.Contains("('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalreopen.create')", SystemCode);
        Assert.DoesNotContain("('5c1b0f6e-0000-4a00-8000-000000000001', 'company.fiscalreopen.approve')", SystemCode);   // ... never approve
        Assert.Contains("('5c1b0f6e-0000-4a00-8000-000000000004', 'company.fiscalreopen.approve')", SystemCode);         // the Fiscal Approver preset does
        Assert.Contains("fiscal-approver", SystemCode);
    }

    [Fact]
    public void Runtime_privileges_for_the_fiscal_calendar_never_include_delete_truncate_or_history_updates()
    {
        string[] Privileges(string table) => [.. CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges.Order()];
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.FiscalYear.Table));
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.FiscalPeriod.Table));
        Assert.Equal(["INSERT", "SELECT"], Privileges(CompanySchema.FiscalHistory.Table));
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.FiscalReopenRequest.Table));
    }
}
