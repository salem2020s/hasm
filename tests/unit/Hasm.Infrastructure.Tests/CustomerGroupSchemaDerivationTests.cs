using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0027 (customer groups) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.0027.customer-groups.v1:" + logical token), exactly as
/// <c>tools/schema/company-0027-customer-groups.cjs</c> generates them. These tests re-derive every name from the committed dictionary, prove uniqueness against every other name, and pin the additive
/// shape of the migration (the only DROP is the replaced discount tie index, which is recreated under a NEW generated name: names are never reused).
/// </summary>
public class CustomerGroupSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0027.customer-groups.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static readonly string[] Entities = ["CustomerGroup", "CustomerGroupMember", "PriceListGroupScope", "DiscountRuleGroupScope"];
    private static readonly string[] Tables = ["CustomerGroup", "CustomerGroupMember"];

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0]));

    [Fact]
    public void Every_customer_group_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.True(entries.Count > 30);
        Assert.Equal(Tables.Length, entries.Count(e => !e.Key.Contains('.')));
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Customer_group_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.Empty(names.Where(others.Contains));
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.Empty(names.Where(system.Contains));
    }

    [Fact]
    public void The_migration_is_additive_forward_only_replaces_one_index_under_a_new_name_and_never_grants_or_seeds()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 27).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bTRUNCATE\s+TABLE\b", sql);
        var drops = Regex.Matches(sql, @"\bDROP\s+\w+\s+\w+").Select(m => m.Value).ToList();
        Assert.Equal([$"DROP INDEX {CompanySchema.Map["DiscountRule.UqActiveKey"]}"], drops);                                       // the only removal: the old active-tie index
        Assert.NotEqual(CompanySchema.Map["DiscountRule.UqActiveKey"], CompanySchema.Map["DiscountRuleGroupScope.UqActiveKey"]);    // its replacement has a NEW generated name
        Assert.Contains($"CREATE UNIQUE INDEX {CompanySchema.Map["DiscountRuleGroupScope.UqActiveKey"]} ON {CompanySchema.Map["DiscountRule"]}", sql);
        Assert.DoesNotContain($"CREATE UNIQUE INDEX {CompanySchema.Map["DiscountRule.UqActiveKey"]}", sql);                         // the old name is never reused
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM)\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded or rewritten outside the guard functions
        foreach (var table in Tables)
        {
            Assert.Contains($"REVOKE ALL ON TABLE {CompanySchema.Map[table]} FROM PUBLIC", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[table + ".TrRow"]} BEFORE INSERT OR UPDATE OR DELETE ON {CompanySchema.Map[table]}", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[table + ".TrTruncate"]} BEFORE TRUNCATE ON {CompanySchema.Map[table]}", sql);
        }
        // the customer-OR-group scope is a database CHECK on both tables
        Assert.Contains($"ADD CONSTRAINT {CompanySchema.Map["PriceListGroupScope.CkScope"]} CHECK", sql);
        Assert.Contains($"ADD CONSTRAINT {CompanySchema.Map["DiscountRuleGroupScope.CkScope"]} CHECK", sql);
    }
}
