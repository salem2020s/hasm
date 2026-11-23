using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Owner decision 2026-11-22 (Q1): DISABLE/ENABLE TRIGGER pairs are frozen to the migrations that already used the pattern, each bound to its exact set, number, name,
/// checksum and triggers. Every existing migration still passes; a new migration with a pair, an allowlisted migration with another checksum, and a pair copied into another
/// number, name or set are refused. Design: docs/MIGRATION-GUARD.md.
/// </summary>
public class MigrationTriggerSwitchAllowlistTests
{
    // The pinned list, written out independently of the production list: the 26 SYSTEM migrations on main that contain a matched pair (no company migration does).
    private static readonly string[] Pinned =
    [
        "0006_company_configuration_support.sql",
        "0007_branch_management_access.sql",
        "0008_warehouse_management_access.sql",
        "0009_fiscal_calendar_access.sql",
        "0010_chart_of_accounts_access.sql",
        "0011_general_ledger_access.sql",
        "0012_cost_center_scope.sql",
        "0013_import_framework_access.sql",
        "0014_financial_reporting_access.sql",
        "0015_inventory_master_data_access.sql",
        "0016_stock_documents_access.sql",
        "0017_inventory_accounting_access.sql",
        "0018_stock_posting_access.sql",
        "0019_stock_reversal_access.sql",
        "0020_stock_balance_access.sql",
        "0021_stocktake_access.sql",
        "0022_negative_stock_access.sql",
        "0023_party_registry.sql",
        "0024_tax_access.sql",
        "0025_party_roles_access.sql",
        "0026_attachments_access.sql",
        "0027_pricing_access.sql",
        "0028_commercial_documents_access.sql",
        "0029_commercial_approver.sql",
        "0030_attachment_retention_access.sql",
        "0034_company_mfa_policy.sql"
    ];

    private static IReadOnlyList<MigrationScript> System_ => MigrationScript.LoadEmbedded();
    private static IReadOnlyList<MigrationScript> Company => MigrationScript.LoadEmbeddedCompany();
    private static string Code(MigrationScript s) => Regex.Replace(s.Sql, "--[^\n]*", "");
    private static readonly Regex Pair = new(@"(?im)^ALTER TABLE\s+(\S+)\s+DISABLE TRIGGER\s+(\S+);");
    private const string PairSql = "ALTER TABLE t_a DISABLE TRIGGER r_a;\nINSERT INTO t_a VALUES (1);\nALTER TABLE t_a ENABLE TRIGGER r_a;";

    private static MigrationScript Make(int version, string name, string sql, MigrationSet set) => new MigrationScript(version, name, sql) { Set = set };
    private static string Refused(Action act) => Assert.Throws<MigrationException>(act).Message;

    [Fact]
    public void The_embedded_sets_are_loaded_with_their_set_identity()
    {
        Assert.All(System_, s => Assert.Equal(MigrationSet.System, s.Set));
        Assert.All(Company, s => Assert.Equal(MigrationSet.Company, s.Set));
        Assert.Equal(MigrationSet.Unspecified, MigrationScript.Parse("0001_a.sql", "SELECT 1;").Set);
    }

    [Fact]
    public void The_allowlist_is_exactly_the_embedded_migrations_that_use_the_pattern_with_their_real_checksums_and_triggers()
    {
        var allowed = MigrationGuard.ApprovedTriggerSwitches;
        Assert.Equal(26, allowed.Count);
        Assert.All(allowed, a => Assert.Equal(MigrationSet.System, a.Set));
        Assert.Equal(Pinned.Order(StringComparer.Ordinal), allowed.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.Contains("0034_company_mfa_policy.sql", Pinned);

        // Found independently of the guard (a text scan of every embedded script of both sets): the same migrations, no more and no fewer.
        var using_ = System_.Concat(Company).Where(s => Regex.IsMatch(Code(s), @"(?i)DISABLE\s+TRIGGER")).ToList();
        Assert.Equal(Pinned.Select(n => $"System:{n}").Order(StringComparer.Ordinal), using_.Select(s => $"{s.Set}:{s.Name}").Order(StringComparer.Ordinal));
        Assert.Empty(Company.Where(s => Regex.IsMatch(Code(s), @"(?i)DISABLE\s+TRIGGER")));

        foreach (var script in using_)
        {
            var entry = Assert.Single(allowed, a => a.Name == script.Name);
            Assert.Equal(script.Version, entry.Version);
            Assert.Equal(int.Parse(script.Name[..4]), entry.Version);
            Assert.Equal(script.Checksum, entry.Checksum);
            Assert.Equal(Pair.Matches(Code(script)).Select(m => $"{m.Groups[1].Value}/{m.Groups[2].Value}").Order(StringComparer.Ordinal), entry.Triggers.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Every_allowlisted_migration_passes_as_loaded_and_both_sets_validate()
    {
        MigrationScript.Validate(System_);
        MigrationScript.Validate(Company);
        foreach (var a in MigrationGuard.ApprovedTriggerSwitches) MigrationGuard.EnsureAcceptableSet([System_.Single(s => s.Name == a.Name)]);
    }

    [Theory]
    [InlineData(MigrationSet.System)]
    [InlineData(MigrationSet.Company)]
    public void A_new_migration_with_a_matched_pair_is_refused_in_either_set(MigrationSet set)
    {
        var existing = set == MigrationSet.System ? System_ : Company;
        var next = existing.Count + 1;
        var added = Make(next, $"{next:0000}_new.sql", PairSql, set);
        Assert.Contains("frozen", Refused(() => MigrationScript.Validate([.. existing, added])));
        // the single-script rule alone still accepts the canonical pair (it is the SET rule that freezes it)
        MigrationScript.Parse(added.Name, PairSql);
    }

    [Fact]
    public void A_hand_built_script_whose_set_is_unspecified_never_matches_an_entry()
    {
        var real = System_.Single(s => s.Name == "0034_company_mfa_policy.sql");
        var forged = Make(real.Version, real.Name, real.Sql, MigrationSet.Unspecified);
        Assert.Equal(real.Checksum, forged.Checksum);
        Assert.Contains("frozen", Refused(() => MigrationGuard.EnsureAcceptableSet([forged])));
    }

    [Fact]
    public void An_allowlisted_migration_with_a_changed_checksum_is_refused()
    {
        foreach (var real in System_.Where(s => MigrationGuard.ApprovedTriggerSwitches.Any(a => a.Name == s.Name)))
        {
            // a comment only: the statements, the pair and the set, number and name are identical; the checksum is the single difference
            var edited = Make(real.Version, real.Name, real.Sql + "\n-- edited\n", MigrationSet.System);
            Assert.NotEqual(real.Checksum, edited.Checksum);
            Assert.Contains("frozen", Refused(() => MigrationGuard.EnsureAcceptableSet([edited])));
        }
    }

    [Theory]
    [InlineData("0006_company_configuration_support.sql")]
    [InlineData("0034_company_mfa_policy.sql")]
    public void A_pair_copied_into_another_number_name_or_set_is_refused(string name)
    {
        var real = System_.Single(s => s.Name == name);
        Assert.Contains("frozen", Refused(() => MigrationGuard.EnsureAcceptableSet([Make(99, "0099_copy.sql", real.Sql, MigrationSet.System)])));            // another number and name
        Assert.Contains("frozen", Refused(() => MigrationGuard.EnsureAcceptableSet([Make(99, real.Name, real.Sql, MigrationSet.System)])));                  // same name, another number
        Assert.Contains("frozen", Refused(() => MigrationGuard.EnsureAcceptableSet([Make(real.Version, "0099_copy.sql", real.Sql, MigrationSet.System)])));   // same number, another name
        Assert.Contains("frozen", Refused(() => MigrationGuard.EnsureAcceptableSet([Make(real.Version, real.Name, real.Sql, MigrationSet.Company)])));        // the other set
        // and into a full company set at the same number
        var company = Company;
        var copy = Make(real.Version, real.Name, real.Sql, MigrationSet.Company);
        Assert.Contains("frozen", Refused(() => MigrationScript.Validate([.. company.Where(s => s.Version != real.Version), copy])));
    }

    [Fact]
    public void Each_condition_of_the_match_is_needed_on_its_own()
    {
        var real = System_.Single(s => s.Name == "0034_company_mfa_policy.sql");
        var switched = Pair.Matches(Code(real)).Select(m => $"{m.Groups[1].Value}/{m.Groups[2].Value}").Order(StringComparer.Ordinal).ToList();
        var exact = new ApprovedTriggerSwitch(MigrationSet.System, real.Version, real.Name, real.Checksum, switched);
        Assert.Null(MigrationGuard.TriggerSwitchProblem([exact], real, switched));
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([], real, switched));
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([exact with { Set = MigrationSet.Company }], real, switched));
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([exact with { Version = real.Version + 1 }], real, switched));
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([exact with { Name = "0034_other.sql" }], real, switched));
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([exact with { Checksum = new string('0', 64) }], real, switched));
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([exact with { Triggers = [switched[0]] }], real, switched));                 // fewer triggers than the migration switches
        Assert.NotNull(MigrationGuard.TriggerSwitchProblem([exact with { Triggers = [.. switched, "t_x/r_x"] }], real, switched));      // more triggers than the entry names
        Assert.Null(MigrationGuard.TriggerSwitchProblem([], real, []));                                                                      // no switch, nothing to approve
    }

    [Fact]
    public void The_refusal_names_the_migration_its_identity_checksum_and_triggers()
    {
        var message = Refused(() => MigrationScript.Validate([.. System_, Make(System_.Count + 1, $"{System_.Count + 1:0000}_new.sql", PairSql, MigrationSet.System)]));
        var next = System_.Count + 1;                                   // the number of the next system migration (it follows the merge order: no hard-coded pin)
        Assert.Contains($"{next:0000}_new.sql", message);
        Assert.Contains("set System", message);
        Assert.Contains($"number {next:0000}", message);
        Assert.Contains("t_a/r_a", message);
        Assert.Matches("checksum [0-9a-f]{64}", message);
        Assert.Contains("Q1", message);
    }

    [Fact]
    public void The_checksum_follows_the_text_of_a_copy()
    {
        var real = System_[0];
        var changed = real with { Sql = real.Sql + "\nSELECT 1;" };
        Assert.NotEqual(real.Checksum, changed.Checksum);
    }
}
