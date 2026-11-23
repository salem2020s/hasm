using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Identity integrity slice S-2d (provisional system migration 0039): the three physical names are the deterministic derivation of their logical tokens (seed without the migration number, tools/schema/system-challenge-tag.cjs), unique across the dictionary, and the migration is strictly additive: two NULLable columns and one check on the challenge table.</summary>
public class ChallengeTagSchemaDerivationTests
{
    private const int Version = 39;   // provisional number: changes with the merge order (together with the generator constant and the file name)

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = Regex.IsMatch(last, "^Ck[A-Z]") ? "k_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.challenge-tag.v1:" + token)))[..8].ToLowerInvariant();
    }

    [Fact]
    public void The_names_are_the_deterministic_derivation_of_their_tokens_and_unique_in_the_system_dictionary()
    {
        var names = new Dictionary<string, string> { ["ChallengeTag.KeyId"] = SystemSchema.ChallengeTag.KeyId, ["ChallengeTag.Tag"] = SystemSchema.ChallengeTag.Tag, ["ChallengeTag.CkTag"] = SystemSchema.ChallengeTag.CkTag, ["ChallengeTag.FnDeleteGuard"] = SystemSchema.ChallengeTag.FnDeleteGuard, ["ChallengeTag.TrDeleteGuard"] = SystemSchema.ChallengeTag.TrDeleteGuard };
        foreach (var (token, name) in names) { Assert.Equal(Derive(token), name); Assert.Equal(name, SystemSchema.Map[token]); }
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        var elsewhere = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Where(s => s.Version != Version).Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        foreach (var name in names.Values) Assert.DoesNotContain(name, elsewhere);
    }

    [Fact]
    public void The_migration_is_additive_and_adds_only_the_two_columns_the_check_and_the_delete_guard()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == Version).Sql, "--[^\n]*", "");
        Assert.Single(Regex.Matches(sql, @"\bALTER\s+TABLE\b"));
        Assert.Equal(2, Regex.Matches(sql, @"\bADD\s+COLUMN\b").Count);
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b"));
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TRIGGER\b"));
        Assert.DoesNotMatch(new Regex(@"\b(OR\s+REPLACE|DROP|GRANT|REVOKE|INSERT|DISABLE|ENABLE|SECURITY\s+DEFINER|search_path|set_config|EXECUTE\s+(?!FUNCTION))\b|\bTRUNCATE\b|\bUPDATE\b|\bDELETE\s+FROM\b", RegexOptions.IgnoreCase), sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.ChallengeTag.TrDeleteGuard} BEFORE DELETE ON {SystemSchema.MfaChallenge.Table}", sql);   // a SEPARATE trigger: the guard of 0033 is not replaced
        Assert.Contains($"OLD.{SystemSchema.MfaChallenge.ExpiresAtUtc} >= clock_timestamp()", sql);
        Assert.Empty(Regex.Matches(sql, @"\bpg_\w+"));
        Assert.Contains($"{SystemSchema.ChallengeTag.Tag} bytea NULL", sql);                                              // NULLable: an untagged legacy user stays a valid row and is simply never honoured
        Assert.Contains($"octet_length({SystemSchema.ChallengeTag.Tag}) = 32", sql);
        Assert.Contains($"ALTER TABLE {SystemSchema.MfaChallenge.Table}", sql);
    }
}
