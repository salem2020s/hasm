using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 S1: the physical names of the MFA schema are the deterministic derivation of their logical tokens (seed without the migration number), unique and distinct from every other system and company name.</summary>
public class MfaSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.mfa-identity.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> MfaEntries() =>
        SystemSchema.Map.Where(kv => kv.Key.StartsWith("UserMfa.", StringComparison.Ordinal) || kv.Key.StartsWith("UserTotp", StringComparison.Ordinal));

    [Fact]
    public void Every_mfa_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = MfaEntries().ToList();
        Assert.True(entries.Count >= 25);
        foreach (var (key, physical) in entries)
        {
            var token = key.EndsWith(".Table", StringComparison.Ordinal) ? key[..^".Table".Length] : key;       // the dictionary names a table "Entity.Table"
            Assert.Equal(Derive(token), physical);
        }
    }

    [Fact]
    public void The_secret_table_is_flagged_as_a_secret_store_and_mapped()
    {
        Assert.Contains("UserTotp.Table", SystemSchema.SecretStoreTables);
        Assert.Equal(SystemSchema.UserTotp.Table, SystemSchema.Map["UserTotp.Table"]);
    }

    [Fact]
    public void Mfa_names_are_unique_and_collide_with_no_other_system_or_company_name()
    {
        var names = MfaEntries().Select(kv => kv.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = SystemSchema.Map.Where(kv => !MfaEntries().Any(m => m.Key == kv.Key)).Select(kv => kv.Value).ToHashSet();
        Assert.Empty(names.Where(others.Contains));
        var company = new HashSet<string>(Hasm.Infrastructure.CompanyDatabase.CompanySchema.Map.Values);
        Assert.Empty(names.Where(company.Contains));
    }

    [Fact]
    public void The_migration_creates_the_secret_table_with_its_unique_indexes_and_guard_and_destroys_nothing()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Name.Contains("mfa_state_and_totp")).Sql, "--[^\n]*", "");
        Assert.Contains($"CREATE TABLE {SystemSchema.UserTotp.Table}", sql);
        Assert.Contains($"CREATE UNIQUE INDEX {SystemSchema.UserTotp.UqActive}", sql);
        Assert.Contains($"CREATE UNIQUE INDEX {SystemSchema.UserTotp.UqPending}", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.UserTotp.TrRow} BEFORE UPDATE OR DELETE ON {SystemSchema.UserTotp.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.UserTotp.TrTruncate} BEFORE TRUNCATE ON {SystemSchema.UserTotp.Table}", sql);
        Assert.Contains($"REVOKE ALL ON TABLE {SystemSchema.UserTotp.Table} FROM PUBLIC", sql);
        Assert.DoesNotMatch(@"\bDROP\b|\bTRUNCATE\s+TABLE\b|\bDELETE\s+FROM\b", sql);
        foreach (var column in new[] { SystemSchema.UserMfa.State, SystemSchema.UserMfa.StateChangedAtUtc, SystemSchema.UserMfa.EnrolmentCodeHash, SystemSchema.UserMfa.EnrolmentCodeExpiresAtUtc, SystemSchema.UserMfa.EnrolmentCodeConsumedAtUtc })
            Assert.Contains($"ADD COLUMN {column}", sql);
    }
}
