using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.2C S1: deterministic allocation maths, cost-center-scoped permission evaluation and the shape of the new migrations (no database needed).</summary>
public class CostCenterPolicyUnitTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a"), B = Guid.Parse("00000000-0000-0000-0000-00000000000b"), C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Theory]
    [InlineData("100", 3, "33.3333,33.3333,33.3334")]
    [InlineData("0.01", 2, "50,50")]
    [InlineData("100.001", 3, "33.3333,33.3333,33.3334")]
    [InlineData("1", 0, "33.3333,33.3333,33.3334")]
    [InlineData("999999999999.999", 3, "12.5,12.5,25,50")]
    [InlineData("0.05", 2, "1,99")]
    public void Distribution_always_adds_up_exactly_and_never_goes_negative(string total, int minor, string percents)
    {
        var parts = percents.Split(',').Select((p, i) => (Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}"), decimal.Parse(p, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var t = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture);
        var shares = AllocationMath.Distribute(t, minor, parts);
        Assert.Equal(t, shares.Values.Sum());
        Assert.All(shares.Values, v => { Assert.True(v >= 0); Assert.Equal(v, decimal.Round(v, minor)); });
    }

    [Fact]
    public void Distribution_is_deterministic_whatever_the_order_and_breaks_ties_by_percentage_then_cost_center_id()
    {
        var parts = new[] { (A, 33.3333m), (B, 33.3333m), (C, 33.3334m) };
        var first = AllocationMath.Distribute(100m, 2, parts);
        var reversed = AllocationMath.Distribute(100m, 2, parts.Reverse().ToArray());
        Assert.Equal(first.OrderBy(k => k.Key), reversed.OrderBy(k => k.Key));
        Assert.Equal(100m, first.Values.Sum());
        // Three equal thirds of 0.10 (two decimals): one unit is left over; the tie goes to the LOWEST id, every time.
        var thirds = AllocationMath.Distribute(0.10m, 2, [(C, 33.3333m), (B, 33.3333m), (A, 33.3334m)]);
        Assert.Equal(0.04m, thirds[A]);                                                                               // the largest percentage wins the leftover unit
        var equal = AllocationMath.Distribute(1m, 0, [(C, 50m), (B, 50m)]);
        Assert.Equal(1m, equal[B]);                                                                                    // equal remainders, equal percentages: the lower id
        Assert.Equal(0m, equal[C]);
        for (var i = 0; i < 20; i++) Assert.Equal(equal.OrderBy(k => k.Key), AllocationMath.Distribute(1m, 0, [(B, 50m), (C, 50m)]).OrderBy(k => k.Key));
    }

    [Fact]
    public void Distribution_refuses_malformed_input_instead_of_guessing()
    {
        Assert.Throws<ArgumentException>(() => AllocationMath.Distribute(10m, 2, [(A, 60m), (B, 30m)]));               // not 100
        Assert.Throws<ArgumentException>(() => AllocationMath.Distribute(10m, 2, [(A, 100m), (B, 0m)]));               // zero share
        Assert.Throws<ArgumentException>(() => AllocationMath.Distribute(10m, 2, []));
        Assert.Throws<ArgumentException>(() => AllocationMath.Distribute(10.005m, 2, [(A, 100m)]));                    // more decimals than the currency allows
    }

    [Fact]
    public void Only_journal_entries_and_ledger_reports_accept_cost_center_scoped_grants()
    {
        foreach (var r in PermissionCatalog.Default.Resources) Assert.Equal(r.Key is "company.journal" or "company.ledgerreports", r.AcceptsCostCenter);
        string[] held = [PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalPost, PermissionCatalog.Codes.CostCentersView, PermissionCatalog.Codes.LedgerReportsView];
        var eff = new EffectivePermissions(PermissionCatalog.Default, [new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), ScopeKind.CostCenter, A, new HashSet<string>(held))]);
        Assert.True(eff.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.CostCenter(A)));
        Assert.True(eff.Has(PermissionCatalog.Codes.JournalPost, ScopeTarget.CostCenter(A)));
        Assert.True(eff.Has(PermissionCatalog.Codes.LedgerReportsView, ScopeTarget.CostCenter(A)));
        Assert.False(eff.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.CostCenter(B)));                         // another cost center
        Assert.False(eff.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.Company));                                // never company-wide
        Assert.False(eff.Has(PermissionCatalog.Codes.JournalView, ScopeTarget.Branch(A)));                              // nor a branch
        Assert.False(eff.Has(PermissionCatalog.Codes.CostCentersView, ScopeTarget.Company));                            // company-level resources stay inert
        Assert.False(eff.Has(PermissionCatalog.Codes.CostCentersView, ScopeTarget.CostCenter(A)));
        Assert.False(eff.Has(PermissionCatalog.Codes.JournalApprove, ScopeTarget.CostCenter(A)));                       // not held
        Assert.True(ScopeTarget.CostCenter(A).IsWellFormed);
        Assert.False(ScopeTarget.CostCenter(Guid.Empty).IsWellFormed);
        // A company-wide holder may hand out the cost-center scope; a branch-only holder may not hand out what a cost center would reach.
        var company = new EffectivePermissions(PermissionCatalog.Default, [new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), ScopeKind.Company, null, new HashSet<string>(held))]);
        Assert.True(company.CanGrant([PermissionCatalog.Codes.JournalView], ScopeTarget.CostCenter(A)));
        var branchOnly = new EffectivePermissions(PermissionCatalog.Default, [new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), ScopeKind.Branch, B, new HashSet<string>(held))]);
        Assert.False(branchOnly.CanGrant([PermissionCatalog.Codes.JournalView], ScopeTarget.CostCenter(A)));
        Assert.True(eff.CanGrant([PermissionCatalog.Codes.JournalView], ScopeTarget.CostCenter(A)));
        Assert.False(eff.CanGrant([PermissionCatalog.Codes.JournalView], ScopeTarget.CostCenter(B)));
    }

    [Fact]
    public void The_policy_permissions_are_company_wide_and_in_the_administrator_preset()
    {
        foreach (var code in new[] { PermissionCatalog.Codes.CostCenterPoliciesView, PermissionCatalog.Codes.CostCenterPoliciesConfigure })
        {
            Assert.Equal(ScopeKind.Company, PermissionCatalog.Default.Get(code).Resource.Scope);
            Assert.Contains(code, PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions);
            Assert.DoesNotContain(code, PresetRoles.Definitions[PresetRoles.JournalApproverId].Permissions);
        }
    }

    [Fact]
    public void The_new_migrations_have_no_drops_no_floating_point_and_protect_their_tables()
    {
        var company = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 9).Sql, "--[^\n]*", "");
        var system = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == 12).Sql, "--[^\n]*", "");
        foreach (var sql in new[] { company, system })
        {
            Assert.DoesNotMatch(@"\bDROP\b", sql);
            Assert.DoesNotMatch(@"(?i)double precision|\breal\b|\bfloat\b", sql);
            Assert.DoesNotMatch(@"(?i)\bTRUNCATE\s+TABLE\b", sql);
        }
        foreach (var table in new[] { CompanySchema.AccountCostCenterRule.Table, CompanySchema.CostCenterBranchRule.Table, CompanySchema.CostCenterBranchEligibility.Table, CompanySchema.PolicyHistory.Table })
        {
            Assert.Contains($"CREATE TABLE {table}", company);
            Assert.Contains($"REVOKE ALL ON TABLE {table} FROM PUBLIC", company);
        }
        Assert.Contains($"CREATE TABLE {SystemSchema.CostCenterAssignment.Table}", system);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.CostCenterAssignment.GuardTrigger} BEFORE UPDATE OR DELETE", system);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.JournalPolicy.PostingTrigger} BEFORE UPDATE ON {CompanySchema.JournalEntry.Table}", company);
        Assert.Contains($"{CompanySchema.CostCenterPolicy.ActivationEpoch} bigint NOT NULL DEFAULT 1", company);
    }

    [Theory]
    [InlineData("a line on an account that requires a cost center has no allocation", "cost_center_required")]
    [InlineData("the cost center is not eligible for the branch of the line", "cost_center_not_eligible")]
    [InlineData("allocations may only target active leaf cost centers", "cost_center_not_allocatable")]
    public void New_database_rules_map_to_stable_codes(string message, string code) => Assert.Equal(code, JournalService.MapDatabaseRule(message));
}
