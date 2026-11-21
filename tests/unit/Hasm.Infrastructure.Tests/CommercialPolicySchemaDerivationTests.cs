using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of the commercial-policies company migration (Phase 3.13 S5a, provisional number 0032) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.&lt;number&gt;.commercial-policies.v1:" +
/// logical token), exactly as <c>tools/schema/company-0032-commercial-policies.cjs</c> generates them. The migration is ADDITIVE and touches nothing of the journal: it creates no table, replaces no function and drops nothing.
/// </summary>
public class CommercialPolicySchemaDerivationTests
{
    private const string Number = "0032";   // the number follows the merge order: a renumbering changes this constant, the generator constant and regenerates the files

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hasm.company.{Number}.commercial-policies.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] == "CommercialPolicy");

    [Fact]
    public void Every_policy_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.Equal(7, entries.Count);                                                                                          // three functions of the plan, the decision function and trigger, the gate function and trigger
        Assert.DoesNotContain(entries, e => !e.Key.Contains('.'));                                                              // no table of its own
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Policy_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] != "CommercialPolicy").Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_is_additive_creates_no_table_and_replaces_nothing_of_the_journal()
    {
        var script = MigrationScript.LoadEmbeddedCompany().Single(s => s.Sql.Contains(CompanySchema.Map["CommercialPolicy.FnPick"]));
        var sql = Regex.Replace(script.Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bCREATE\s+OR\s+REPLACE\b", sql);                                                                 // no existing function (the journal's engine included) is replaced
        Assert.DoesNotMatch(@"\bCREATE\s+TABLE\b", sql);
        Assert.DoesNotMatch(@"\bALTER\s+TABLE\b", sql);
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM|UPDATE)\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded or rewritten
        // exactly two triggers: the decision trigger on the approval decisions and the gate on the commercial document; neither touches the journal's triggers
        Assert.Equal(2, Regex.Matches(sql, @"\bCREATE\s+TRIGGER\b").Count);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["CommercialPolicy.TrDecision"]} BEFORE INSERT ON {CompanySchema.Map["ApprovalDecision"]}", sql);
        Assert.Contains($"CREATE TRIGGER {CompanySchema.Map["CommercialPolicy.TrGate"]} BEFORE UPDATE ON {CompanySchema.Map["CommercialDocument"]}", sql);
        // the decision trigger returns at once for anything that is not a decision on a commercial document, and serializes the decisions of one document
        Assert.Contains("IF NOT FOUND THEN RETURN NEW; END IF;", sql);
        Assert.Contains("FOR NO KEY UPDATE", sql);
        // the journal's engine functions are not referenced as callable objects of this migration
        foreach (var journal in new[] { "ApprovalStaging.PickFunction", "ApprovalStaging.PlanFunction", "ApprovalStaging.SatisfiedFunction", "ApprovalStaging.OrderFunction" })
            Assert.DoesNotContain(CompanySchema.Map[journal], sql);
        // the journal-only policy conditions never match a commercial document
        foreach (var condition in new[] { "ApprovalPolicyConditions.JournalType", "ApprovalPolicyConditions.RateOverride", "ApprovalPolicyConditions.SoftClosed", "ClosingLogic.PolicyClosingKind" })
            Assert.Contains($"{CompanySchema.Map[condition]} IS NULL", sql);
    }
}
