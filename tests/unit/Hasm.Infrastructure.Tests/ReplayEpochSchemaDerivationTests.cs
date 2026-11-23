using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0039 (T2 slice A3: the commit-time checks of a replay grant and the epoch text) are DERIVED: prefix + the first 8 hex digits of SHA-256("hasm.company.0039.replay-epoch.v1:" + logical token), exactly as
/// <c>tools/schema/company-0039-replay-epoch-commit.cjs</c> generates them. The migration creates no table, replaces nothing and passes the migration guard.
/// </summary>
public class ReplayEpochSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0039.replay-epoch.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => kv.Key.StartsWith("ReplayEpoch.", StringComparison.Ordinal));
    private static string Sql() => Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 39).Sql, "--[^\n]*", "");

    [Fact]
    public void Every_name_is_the_deterministic_derivation_of_its_logical_token_and_none_collides()
    {
        var entries = OwnEntries().ToList();
        Assert.Equal(6, entries.Count);          // four functions and two triggers
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
        var names = entries.Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !kv.Key.StartsWith("ReplayEpoch.", StringComparison.Ordinal)).Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_creates_only_functions_and_two_deferred_triggers_names_every_relation_with_its_schema_and_uses_no_unsafe_construct()
    {
        var sql = Sql();
        Assert.DoesNotMatch(@"\bCREATE\s+TABLE\b|\bCREATE\s+TRIGGER\b|\bDROP\b|\bALTER\b|\bCREATE\s+OR\s+REPLACE\b|\bGRANT\b|\bREVOKE\b|\bDISABLE\b|\bENABLE\b|\bDELETE\s+FROM\b|\bINSERT\s+INTO\b|\bUPDATE\s+t_", sql);
        Assert.DoesNotMatch(@"SECURITY\s+DEFINER|search_path|set_config|\bEXECUTE\s+'|CREATE\s+EXTENSION|\bTEMP(?:ORARY)?\b|\bdblink|\bpg_\w+", sql);
        Assert.Equal(4, Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b").Count);
        Assert.Equal(2, Regex.Matches(sql, @"\bCREATE\s+CONSTRAINT\s+TRIGGER\b").Count);
        Assert.Equal(2, Regex.Matches(sql, @"DEFERRABLE INITIALLY DEFERRED").Count);
        Assert.True(string.CompareOrdinal(CompanySchema.Map["ReplayEpoch.TrCommitExecution"], CompanySchema.Map["CompensationPlanExecution.TrCommit"]) > 0, "it must fire after the completeness trigger of 0036 on the same event");
        Assert.DoesNotMatch(@"\bLANGUAGE\s+(?!sql\b|plpgsql\b)", sql);
        // every relation named in the text is schema-qualified
        foreach (Match m in Regex.Matches(sql, @"\b(?:FROM|JOIN|INTO|ON)\s+(t_[0-9a-f]{8})\b")) Assert.Fail("an unqualified relation: " + m.Value);
        foreach (Match m in Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+public\.(t_[0-9a-f]{8})\b")) Assert.StartsWith("t_", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(sql, @"\bCREATE\s+FUNCTION\s+(\S+?)\(")) Assert.StartsWith("public.", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(sql, @"\bEXECUTE\s+FUNCTION\s+(\S+?)\(")) Assert.StartsWith("public.", m.Groups[1].Value);
        // the message is built with chr(10) and fixed formats only: no backslash literal, no locale-dependent text
        Assert.DoesNotContain("E'", sql);
        Assert.DoesNotContain("\\n", sql);
        Assert.DoesNotMatch(@"\bto_char\s*\([^)]*,\s*'(?!YYYY-MM-DD')", sql);
        Assert.DoesNotContain("hasm-replay-v4", sql);
    }

    [Fact]
    public void The_embedded_set_contains_the_migration_as_number_39_and_the_guard_accepts_the_whole_set()
    {
        var company = MigrationScript.LoadEmbeddedCompany();
        Assert.Equal("0039_company_replay_epoch_commit.sql", company.Single(s => s.Version == 39).Name);
        MigrationGuard.EnsureAcceptableSet(company);
    }
}
