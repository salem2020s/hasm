using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The owner-only guard on the TOTP secret material (second part of owner decision D-235, provisional system migration 0035): its two physical names are the deterministic derivation of their logical tokens (seed
/// without the migration number, <c>tools/schema/system-totp-owner-only.cjs</c>), unique across the system dictionary, and the migration is strictly additive: one function, one trigger, nothing replaced, dropped,
/// disabled or written, no dynamic SQL, no <c>search_path</c> or <c>set_config</c>, no SECURITY DEFINER, and no catalog name except the one read-only role-membership function.
/// </summary>
public class TotpOwnerOnlySchemaDerivationTests
{
    private const int Version = 35;   // provisional number: changes with the merge order (together with the generator constant and the file name)

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.totp-owner-only.v1:" + token)))[..8].ToLowerInvariant();
    }

    [Fact]
    public void The_two_names_are_the_deterministic_derivation_of_their_tokens_and_unique_in_the_system_dictionary()
    {
        Assert.Equal(Derive("TotpGuard.FnOwnerOnly"), SystemSchema.TotpGuard.FnOwnerOnly);
        Assert.Equal(Derive("TotpGuard.TrOwnerOnly"), SystemSchema.TotpGuard.TrOwnerOnly);
        Assert.Equal(SystemSchema.TotpGuard.FnOwnerOnly, SystemSchema.Map["TotpGuard.FnOwnerOnly"]);
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        var inOtherMigrations = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Where(s => s.Version != Version).Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(SystemSchema.TotpGuard.FnOwnerOnly, inOtherMigrations);
        Assert.DoesNotContain(SystemSchema.TotpGuard.TrOwnerOnly, inOtherMigrations);
    }

    [Fact]
    public void The_migration_is_additive_and_leaves_the_existing_guard_alone()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == Version).Sql, "--[^\n]*", "");
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b"));
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TRIGGER\b"));
        Assert.DoesNotMatch(@"\bOR\s+REPLACE\b", sql);                                                          // the function of migration 0031 is NOT replaced
        Assert.DoesNotMatch(new Regex(@"\b(DROP|ALTER|GRANT|REVOKE|INSERT|UPDATE\s+t_|DELETE|DISABLE|ENABLE|EXECUTE\s+(?!FUNCTION)|SECURITY\s+DEFINER|search_path|set_config|current_setting)\b|\bTRUNCATE\b(?!')", RegexOptions.IgnoreCase), sql.Replace("BEFORE UPDATE ON", ""));
        Assert.Contains($"CREATE TRIGGER {SystemSchema.TotpGuard.TrOwnerOnly} BEFORE UPDATE ON {SystemSchema.UserTotp.Table} FOR EACH ROW EXECUTE FUNCTION {SystemSchema.TotpGuard.FnOwnerOnly}()", sql);
        // the four material columns, and only those, decide; the identity, state and progress columns are not looked at
        foreach (var column in new[] { SystemSchema.UserTotp.Cipher, SystemSchema.UserTotp.Nonce, SystemSchema.UserTotp.Tag, SystemSchema.UserTotp.KekId })
            Assert.Contains($"NEW.{column} IS DISTINCT FROM OLD.{column}", sql);
        foreach (var column in new[] { SystemSchema.UserTotp.State, SystemSchema.UserTotp.LastStep, SystemSchema.UserTotp.FailedConfirmations, SystemSchema.UserTotp.ConfirmedAtUtc, SystemSchema.UserTotp.SecretId, SystemSchema.UserTotp.UserId })
            Assert.DoesNotContain(column, sql);
        // the only name starting with pg_ is the read-only membership function, called unqualified
        var pgNames = Regex.Matches(sql, @"\bpg_\w+").Select(m => m.Value).Distinct().ToArray();
        Assert.Equal(["pg_has_role"], pgNames);
        Assert.Matches(@"(?<![.\w""])pg_has_role\(", sql);
        // the previous guard and its flag layer are untouched in migration 0031
        var m31 = MigrationScript.LoadEmbedded().Single(s => s.Version == 31).Sql;
        Assert.Contains("hasm.totp_rewrap", m31);
    }
}
