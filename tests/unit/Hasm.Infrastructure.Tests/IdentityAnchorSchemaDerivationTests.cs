using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Identity integrity slice S-2c part 1 (provisional system migration 0040): the physical names are the deterministic derivation of their logical tokens (seed without the migration number, tools/schema/system-identity-anchor.cjs), unique across the dictionary, and the migration is additive: one new append-only table with its guard, one extra trigger on the challenge table.</summary>
public class IdentityAnchorSchemaDerivationTests
{
    private const int Version = 40;   // provisional number: changes with the merge order (together with the generator constant and the file name)

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)") ? "k_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.identity-anchor.v1:" + token)))[..8].ToLowerInvariant();
    }

    [Fact]
    public void The_names_are_the_deterministic_derivation_of_their_tokens_and_unique_in_the_system_dictionary()
    {
        var a = typeof(SystemSchema.IdentityAnchor);
        var names = new Dictionary<string, string> { ["IdentityAnchor.Table"] = SystemSchema.IdentityAnchor.Table };
        foreach (var f in a.GetFields().Where(f => f.Name != "Table")) names["IdentityAnchor." + f.Name] = (string)f.GetValue(null)!;
        names["ChallengeGuard.FnMargin"] = SystemSchema.ChallengeGuard.FnMargin; names["ChallengeGuard.TrMargin"] = SystemSchema.ChallengeGuard.TrMargin;
        foreach (var (token, name) in names)
        {
            Assert.Equal(token == "IdentityAnchor.Table" ? Derive("IdentityAnchor") : Derive(token), name);
            Assert.Equal(name, SystemSchema.Map[token]);
        }
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        var elsewhere = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Where(s => s.Version != Version).Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        foreach (var name in names.Values) Assert.DoesNotContain(name, elsewhere);
    }

    [Fact]
    public void The_migration_is_additive_append_only_and_adds_the_margin_trigger_without_replacing_the_guard_of_0039()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == Version).Sql, "--[^\n]*", "");
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TABLE\b"));
        Assert.Equal(2, Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b").Count);
        Assert.Equal(2, Regex.Matches(sql, @"\bCREATE\s+TRIGGER\b").Count);
        Assert.DoesNotMatch(new Regex(@"\b(OR\s+REPLACE|DROP|ALTER|GRANT|INSERT|DISABLE|ENABLE|SECURITY\s+DEFINER|search_path|set_config|EXECUTE\s+(?!FUNCTION))\b|\bTRUNCATE\b|\bUPDATE\s+t_|\bDELETE\s+FROM\b", RegexOptions.IgnoreCase), sql);
        Assert.Empty(Regex.Matches(sql, @"\bpg_\w+"));
        Assert.Contains($"CREATE TRIGGER {SystemSchema.IdentityAnchor.TrAppendOnly} BEFORE UPDATE OR DELETE ON {SystemSchema.IdentityAnchor.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.ChallengeGuard.TrMargin} BEFORE DELETE ON {SystemSchema.MfaChallenge.Table}", sql);
        Assert.Contains($"OLD.{SystemSchema.MfaChallenge.ExpiresAtUtc} >= clock_timestamp() - interval '1 hour'", sql);
        Assert.Contains($"REVOKE ALL ON TABLE {SystemSchema.IdentityAnchor.Table} FROM PUBLIC", sql);
    }
}
