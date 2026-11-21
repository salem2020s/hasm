using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 S3b-1b: the physical names of the company MFA policy schema are the deterministic derivation of their logical tokens (seed without the migration number), unique and distinct from every other system name; the tables are not secret stores.</summary>
public class MfaPolicySchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.mfa-policy.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> Entries() =>
        SystemSchema.Map.Where(kv => kv.Key.StartsWith("MfaPolicy", StringComparison.Ordinal));

    [Fact]
    public void Every_policy_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = Entries().ToList();
        Assert.Equal(16, entries.Count);
        foreach (var (key, physical) in entries)
        {
            var token = key.EndsWith(".Table", StringComparison.Ordinal) ? key[..^".Table".Length] : key;
            Assert.Equal(Derive(token), physical);
        }
    }

    [Fact]
    public void The_policy_names_are_unique_across_the_whole_system_dictionary_and_the_tables_are_not_secret_stores()
    {
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        Assert.DoesNotContain("MfaPolicy.Table", SystemSchema.SecretStoreTables);
        Assert.DoesNotContain("MfaPolicyRole.Table", SystemSchema.SecretStoreTables);
        Assert.Equal(SystemSchema.MfaPolicy.Table, SystemSchema.Map["MfaPolicy.Table"]);
        Assert.Equal(SystemSchema.MfaPolicyRole.Table, SystemSchema.Map["MfaPolicyRole.Table"]);
    }
}
