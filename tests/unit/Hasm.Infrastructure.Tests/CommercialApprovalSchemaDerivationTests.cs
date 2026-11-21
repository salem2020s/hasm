using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0029 (commercial approval, numbering and snapshots, Phase 3.13 S1b) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.0029.commercial-approval.v1:" +
/// logical token), exactly as <c>tools/schema/company-0029-commercial-approval.cjs</c> generates them. The migration only ADDS columns, one index and replaces guard functions (no DROP, no new table).
/// </summary>
public class CommercialApprovalSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0029.commercial-approval.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static readonly string[] Entities = ["CommercialDocumentApproval", "CommercialDocumentSnapshot"];

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0]));

    [Fact]
    public void Every_approval_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.True(entries.Count >= 22);
        Assert.DoesNotContain(entries, e => !e.Key.Contains('.'));                                                              // these entities have no table of their own
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Approval_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_only_adds_columns_replaces_the_two_guards_and_has_no_drop_grant_or_new_table()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 29).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bCREATE\s+TABLE\b", sql);
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM)\s+t_", Regex.Replace(sql, @"CREATE OR REPLACE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded or rewritten outside the guard functions
        // the guards of 0028 are replaced under their own names (no new object takes an old name)
        Assert.Contains($"CREATE OR REPLACE FUNCTION {CompanySchema.Map["CommercialDocument.FnGuard"]}()", sql);
        Assert.Contains($"CREATE OR REPLACE FUNCTION {CompanySchema.Map["CommercialDocumentEvent.FnGuard"]}()", sql);
        Assert.DoesNotContain($"CREATE OR REPLACE FUNCTION {CompanySchema.Map["CommercialDocumentLine.FnGuard"]}()", sql);      // the draft-lines guard is unchanged
        // the replaced document guard knows the whole matrix: approval, rejection, re-approval, reopening and the cancellation of an approved document
        Assert.Contains("IN (1, 3, 4, 5)", sql);
        Assert.Contains("IN (1, 3, 5)", sql);
        Assert.Contains("the approver must be independent of the creator, the last editor and the submitter", sql);
        // every column is nullable (existing rows stay valid) and the number is unique per document type
        Assert.DoesNotMatch(@"ADD COLUMN \w+ [^;]*NOT NULL", sql);
        Assert.Contains($"CREATE UNIQUE INDEX {CompanySchema.Map["CommercialDocumentApproval.UqNumber"]}", sql);
    }
}
