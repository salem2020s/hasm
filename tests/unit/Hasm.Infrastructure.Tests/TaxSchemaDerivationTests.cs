using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0022 (tax engine) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.0022.tax.v1:" + logical token), exactly as
/// <c>tools/schema/company-0022-tax.cjs</c> generates them. These tests re-derive every name from the committed dictionary, so a hand edit of a name, of the dictionary or of the migration
/// that does not come from the generator fails here, and uniqueness against every other name of the schema is proved.
/// </summary>
public class TaxSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0022.tax.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static readonly string[] Entities = ["TaxDefinition", "TaxRateVersion", "TaxClass", "TaxItemClass", "TaxPartyClass", "TaxRule", "TaxRuleBase", "TaxPolicyVersion"];

    private static IEnumerable<KeyValuePair<string, string>> TaxEntries() =>
        CompanySchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0]));

    [Fact]
    public void Every_tax_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = TaxEntries().ToList();
        Assert.True(entries.Count > 100);
        Assert.Equal(Entities.Length, entries.Count(e => !e.Key.Contains('.')));
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Tax_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var tax = TaxEntries().Select(e => e.Value).ToList();
        Assert.Equal(tax.Count, tax.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.Empty(tax.Where(others.Contains));
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.Empty(tax.Where(system.Contains));
    }

    [Fact]
    public void The_migration_is_additive_forward_only_and_never_grants_or_drops()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 22).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\b(INSERT|UPDATE|DELETE)\s+(INTO|FROM)?\s*t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded or rewritten outside the guard functions
        foreach (var entity in Entities) Assert.Contains($"REVOKE ALL ON TABLE {CompanySchema.Map[entity]} FROM PUBLIC", sql);
        // append-only tables are guarded by triggers on update, delete and truncate
        foreach (var guarded in new[] { "TaxRateVersion", "TaxPolicyVersion" })
        {
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[guarded + ".TrRow"]} BEFORE INSERT OR UPDATE OR DELETE ON {CompanySchema.Map[guarded]}", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[guarded + ".TrTruncate"]} BEFORE TRUNCATE ON {CompanySchema.Map[guarded]}", sql);
        }
    }
}
