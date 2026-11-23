using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Identity integrity slice S-1 (provisional system migration 0037): the physical names are the deterministic derivation of their logical tokens (SHA-256 seed WITHOUT the migration number,
/// <c>tools/schema/system-identity-integrity.cjs</c>), unique across the system dictionary, and the migration is strictly additive: three NULLable columns and a check on the session table, one
/// security-revision column with a check and one guard function and trigger on the user table; nothing replaced, dropped, disabled, written or dynamic.
/// </summary>
public class IdentityIntegritySchemaDerivationTests
{
    private const int Version = 37;   // provisional number: changes with the merge order (together with the generator constant and the file name)

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^Ck[A-Z]") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.identity-integrity.v1:" + token)))[..8].ToLowerInvariant();
    }

    [Fact]
    public void The_names_are_the_deterministic_derivation_of_their_tokens_and_unique_in_the_system_dictionary()
    {
        var names = new Dictionary<string, string>
        {
            ["SessionIntegrity.KeyId"] = SystemSchema.SessionIntegrity.KeyId, ["SessionIntegrity.Tag"] = SystemSchema.SessionIntegrity.Tag,
            ["SessionIntegrity.UserRevisionAtIssue"] = SystemSchema.SessionIntegrity.UserRevisionAtIssue, ["SessionIntegrity.CkIntegrity"] = SystemSchema.SessionIntegrity.CkIntegrity,
            ["UserIntegrity.SecurityRevision"] = SystemSchema.UserIntegrity.SecurityRevision, ["UserIntegrity.CkRevision"] = SystemSchema.UserIntegrity.CkRevision,
            ["UserIntegrity.FnRevisionGuard"] = SystemSchema.UserIntegrity.FnRevisionGuard, ["UserIntegrity.TrRevisionGuard"] = SystemSchema.UserIntegrity.TrRevisionGuard,
        };
        foreach (var (token, name) in names)
        {
            Assert.Equal(Derive(token), name);
            Assert.Equal(name, SystemSchema.Map[token]);
        }
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        var inOtherMigrations = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Where(s => s.Version != Version).Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        foreach (var name in names.Values) Assert.DoesNotContain(name, inOtherMigrations);
    }

    [Fact]
    public void The_migration_is_additive_and_adds_only_the_documented_objects()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == Version).Sql, "--[^\n]*", "");
        Assert.Equal(2, Regex.Matches(sql, @"\bALTER\s+TABLE\b").Count);
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b"));
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TRIGGER\b"));
        Assert.Equal(4, Regex.Matches(sql, @"\bADD\s+COLUMN\b").Count);
        Assert.DoesNotMatch(new Regex(@"\b(OR\s+REPLACE|DROP|GRANT|REVOKE|INSERT|DELETE|DISABLE|ENABLE|SECURITY\s+DEFINER|search_path|set_config|current_setting|EXECUTE\s+(?!FUNCTION))\b|\bTRUNCATE\b|\bUPDATE\s+t_", RegexOptions.IgnoreCase), sql.Replace("BEFORE INSERT OR UPDATE ON", "").Replace("TG_OP = 'INSERT'", "").Replace("NEW.", ""));
        Assert.Empty(Regex.Matches(sql, @"\bpg_\w+"));                                                          // no catalog name at all
        // every new column is NULLable on the session table (a legacy row stays valid as a row and is simply never honoured) and the user revision starts at zero
        Assert.Contains($"{SystemSchema.SessionIntegrity.Tag} bytea NULL", sql);
        Assert.Contains($"{SystemSchema.SessionIntegrity.UserRevisionAtIssue} bigint NULL", sql);
        Assert.Contains($"{SystemSchema.UserIntegrity.SecurityRevision} bigint NOT NULL DEFAULT 0", sql);
        Assert.Contains($"{SystemSchema.UserIntegrity.SecurityRevision} < 4611686018427387904", sql);                  // the bound (2^62) applies to INSERT as well: no row can start where a bump overflows
        Assert.Contains("NEW." + SystemSchema.UserIntegrity.SecurityRevision + " <> 0", sql);                          // a user starts at zero: no start where the bump can fail
        // the database enforces what needs no key: the revision stays or grows by exactly one
        Assert.Contains($"NEW.{SystemSchema.UserIntegrity.SecurityRevision} <> OLD.{SystemSchema.UserIntegrity.SecurityRevision} + 1", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.UserIntegrity.TrRevisionGuard} BEFORE INSERT OR UPDATE ON {SystemSchema.User.Table}", sql);
    }
}
