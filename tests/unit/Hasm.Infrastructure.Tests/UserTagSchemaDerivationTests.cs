using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Identity integrity slice S-2a (provisional system migration 0038): the three physical names are the deterministic derivation of their logical tokens (seed without the migration number, tools/schema/system-user-tag.cjs), unique across the dictionary, and the migration is strictly additive: two NULLable columns and one check on the user table.</summary>
public class UserTagSchemaDerivationTests
{
    private const int Version = 38;   // provisional number: changes with the merge order (together with the generator constant and the file name)

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = Regex.IsMatch(last, "^Ck[A-Z]") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.user-tag.v1:" + token)))[..8].ToLowerInvariant();
    }

    [Fact]
    public void The_names_are_the_deterministic_derivation_of_their_tokens_and_unique_in_the_system_dictionary()
    {
        var names = new Dictionary<string, string> { ["UserTag.KeyId"] = SystemSchema.UserTag.KeyId, ["UserTag.Tag"] = SystemSchema.UserTag.Tag, ["UserTag.CkTag"] = SystemSchema.UserTag.CkTag };
        foreach (var (token, name) in names) { Assert.Equal(Derive(token), name); Assert.Equal(name, SystemSchema.Map[token]); }
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        var elsewhere = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Where(s => s.Version != Version).Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        foreach (var name in names.Values) Assert.DoesNotContain(name, elsewhere);
    }

    [Fact]
    public void The_migration_is_additive_and_adds_only_the_two_columns_and_the_check()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == Version).Sql, "--[^\n]*", "");
        Assert.Single(Regex.Matches(sql, @"\bALTER\s+TABLE\b"));
        Assert.Equal(2, Regex.Matches(sql, @"\bADD\s+COLUMN\b").Count);
        Assert.DoesNotMatch(new Regex(@"\b(OR\s+REPLACE|DROP|GRANT|REVOKE|INSERT|DELETE|DISABLE|ENABLE|CREATE|TRIGGER|FUNCTION|EXECUTE|SECURITY\s+DEFINER|search_path|set_config)\b|\bTRUNCATE\b|\bUPDATE\b", RegexOptions.IgnoreCase), sql);
        Assert.Empty(Regex.Matches(sql, @"\bpg_\w+"));
        Assert.Contains($"{SystemSchema.UserTag.Tag} bytea NULL", sql);                                              // NULLable: an untagged legacy user stays a valid row and is simply never honoured
        Assert.Contains($"octet_length({SystemSchema.UserTag.Tag}) = 32", sql);
        Assert.Contains($"ALTER TABLE {SystemSchema.User.Table}", sql);
    }
}
