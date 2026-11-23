using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of the short-close company migration (Phase 3.13, owner decision D-201; company migration 0039) are DERIVED, not invented: prefix + the first 8 hex digits of
/// SHA-256("hasm.company.short-close.v1:" + logical token), exactly as <c>tools/schema/company-0039-short-close.cjs</c> generates them (the seed has no migration number, so a renumbering changes no name).
/// The migration adds one append-only table and two additive guards (on the link table and on the document table); it drops, grants and replaces nothing.
/// </summary>
public class ShortCloseSchemaDerivationTests
{
    private const int Version = 39;   // allocated by merge order: a renumbering changes this (together with the generator constant and the file name)

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.short-close.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] == "DocumentShortClose");

    [Fact]
    public void Every_short_close_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.True(entries.Count >= 24);
        Assert.Contains(entries, e => e.Key == "DocumentShortClose");
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Short_close_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] != "DocumentShortClose").Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_adds_one_append_only_table_and_two_additive_guards_with_no_drop_grant_or_replacement()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == Version).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bCREATE\s+OR\s+REPLACE\b", sql);                                   // no existing function is replaced (the 0028, 0029 and 0030 guards are untouched)
        Assert.DoesNotMatch(@"\bALTER\s+TABLE\b", sql);                                           // no existing table is altered (the guards are added with CREATE TRIGGER)
        Assert.DoesNotMatch(@"\bDISABLE\s+TRIGGER\b", sql);
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TABLE\b"));
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM|UPDATE)\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded or modified
        var table = CompanySchema.Map["DocumentShortClose"];
        Assert.Contains($"BEFORE INSERT OR UPDATE OR DELETE ON {table}", sql);                    // append-only, truncation-proof
        Assert.Contains($"BEFORE TRUNCATE ON {table}", sql);
        Assert.Contains($"REVOKE ALL ON TABLE {table} FROM PUBLIC", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["DocumentShortClose.TrLinkCap"]} BEFORE INSERT ON {CompanySchema.Map["DocumentLink"]}", sql);          // tolerance 0 after a close
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["DocumentShortClose.TrDocGuard"]} BEFORE UPDATE ON {CompanySchema.Map["CommercialDocument"]}", sql);   // a closed document stays approved
        // the locks are taken documents first, lines second (the order of a link and of a document edit), in both new guards, before any sum is read
        Assert.True(sql.IndexOf("FOR SHARE", StringComparison.Ordinal) < sql.IndexOf("FOR NO KEY UPDATE", StringComparison.Ordinal));
        foreach (var fn in new[] { "FnGuard", "FnLinkCap" })
        {
            var body = Regex.Match(sql, $@"CREATE FUNCTION {CompanySchema.Map["DocumentShortClose." + fn]}\(\).*?\$\$;", RegexOptions.Singleline).Value;
            Assert.True(body.IndexOf("FOR SHARE", StringComparison.Ordinal) is var share and >= 0 && share < body.IndexOf("FOR NO KEY UPDATE", StringComparison.Ordinal), fn + " locks documents before lines");
            Assert.True(body.IndexOf("FOR NO KEY UPDATE", StringComparison.Ordinal) < body.IndexOf("sum(", StringComparison.Ordinal), fn + " locks the line before reading a sum");
        }
    }
}
