using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0030 (document links, Phase 3.13 S2) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.0030.document-links.v1:" + logical token), exactly as
/// <c>tools/schema/company-0030-document-links.cjs</c> generates them. The migration adds one table and one guard trigger on the 0028 document table (no DROP, no replacement of an existing function, nothing seeded).
/// </summary>
public class DocumentLinkSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0030.document-links.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] == "DocumentLink");

    [Fact]
    public void Every_link_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.True(entries.Count >= 29);
        Assert.Contains(entries, e => e.Key == "DocumentLink");                                                                 // the table itself
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Link_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] != "DocumentLink").Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_adds_one_append_only_table_and_one_guard_with_no_drop_grant_or_replacement()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 30).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bCREATE\s+OR\s+REPLACE\b", sql);                                                                 // no existing function is replaced (0028 and 0029 guards are untouched)
        Assert.DoesNotMatch(@"\bALTER\s+TABLE\b", sql);                                                                         // no existing table is altered (the trigger is added with CREATE TRIGGER)
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TABLE\b"));
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM)\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded
        var link = CompanySchema.Map["DocumentLink"];
        // append-only and truncation-proof, concurrency-safe over-fulfilment guard
        Assert.Contains($"BEFORE INSERT OR UPDATE OR DELETE ON {link}", sql);
        Assert.Contains($"BEFORE TRUNCATE ON {link}", sql);
        Assert.Contains("FOR NO KEY UPDATE", sql);
        Assert.True(sql.IndexOf("FOR SHARE", StringComparison.Ordinal) < sql.IndexOf("FOR NO KEY UPDATE", StringComparison.Ordinal), "the documents are locked before the lines (the order of a document edit)");   // F review M1
        Assert.Contains("over-fulfilment", sql);
        Assert.Contains($"REVOKE ALL ON TABLE {link} FROM PUBLIC", sql);
        // the document-side guard keeps a linked approved document approved
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["DocumentLink.TrDocGuard"]} BEFORE UPDATE ON {CompanySchema.Map["CommercialDocument"]}", sql);
    }

    [Fact]
    public void The_registry_lists_its_effective_link_pairs_in_a_stable_order_and_production_lists_none()
    {
        Assert.Empty(new CommercialDocumentTypeRegistry([]).LinkPairs);
        var registry = new CommercialDocumentTypeRegistry(["order_x", "delivery_x"], [("order_x", "delivery_x", CommercialLinkKind.Invoices), ("order_x", "delivery_x", CommercialLinkKind.Fulfils), ("order_x", "ghost", CommercialLinkKind.Fulfils)]);
        Assert.Equal([new LinkPairView("order_x", "delivery_x", 1), new LinkPairView("order_x", "delivery_x", 2)], registry.LinkPairs);   // sorted, and a pair naming an unregistered type is not listed
    }

    [Fact]
    public void The_registry_allows_only_registered_link_pairs_and_production_registers_none()
    {
        var empty = new CommercialDocumentTypeRegistry([]);
        Assert.False(empty.IsLinkAllowed("a", "b", CommercialLinkKind.Fulfils));
        var registry = new CommercialDocumentTypeRegistry(["order_x", "delivery_x"], [("order_x", "delivery_x", CommercialLinkKind.Fulfils)]);
        Assert.True(registry.IsLinkAllowed("order_x", "delivery_x", CommercialLinkKind.Fulfils));
        Assert.False(registry.IsLinkAllowed("order_x", "delivery_x", CommercialLinkKind.Invoices));                             // another kind
        Assert.False(registry.IsLinkAllowed("delivery_x", "order_x", CommercialLinkKind.Fulfils));                              // the other direction
        var unregistered = new CommercialDocumentTypeRegistry(["order_x"], [("order_x", "ghost", CommercialLinkKind.Fulfils)]);
        Assert.False(unregistered.IsLinkAllowed("order_x", "ghost", CommercialLinkKind.Fulfils));                               // a pair naming an unregistered type is never allowed
    }
}
