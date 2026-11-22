using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of the commercial-policies company migration (Phase 3.13 S5a, provisional number 0032) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.&lt;number&gt;.approval-policy-identity.v1:" +
/// logical token), exactly as <c>tools/schema/company-0032-commercial-policies.cjs</c> generates them. The migration is ADDITIVE and touches nothing of the journal: it creates no table, replaces no function and drops nothing.
/// </summary>
public class ApprovalPolicyIdentitySchemaDerivationTests
{
    private const string Number = "0033";   // the number follows the merge order: a renumbering changes this constant, the generator constant and regenerates the files

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hasm.company.{Number}.approval-policy-identity.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] == "ApprovalPolicyIdentity");

    [Fact]
    public void Every_policy_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.Equal(19, entries.Count);                                                                                         // the table, 4 columns, 5 constraints and indexes, 5 functions, 4 triggers
        Assert.Single(entries, e => !e.Key.Contains('.'));                                                                      // exactly one table of its own
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Policy_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => kv.Key.Split('.')[0] != "ApprovalPolicyIdentity").Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_is_additive_alters_nothing_and_changes_no_policy_row()
    {
        var script = MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 33);
        var sql = Regex.Replace(script.Sql, "--[^\n]*", "");
        var policy = CompanySchema.Map["ApprovalPolicy"]; var stage = CompanySchema.Map["ApprovalPolicyStage"]; var own = CompanySchema.Map["ApprovalPolicyIdentity"];
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bCREATE\s+OR\s+REPLACE\b", sql);                                                             // no existing function is replaced
        Assert.DoesNotMatch(@"\bALTER\b", sql);                                                                               // no existing table, constraint, function or trigger is altered
        Assert.DoesNotMatch(@"\b(DISABLE|ENABLE)\b", sql);                                                                    // no trigger is switched
        Assert.DoesNotMatch(@"\bDELETE\s+FROM\b", sql);
        Assert.DoesNotMatch(@"\bEXECUTE\b(?!\s+FUNCTION\b)", sql);                                                          // no dynamic SQL (EXECUTE FUNCTION is the trigger clause)
        Assert.All(Regex.Matches(sql, @"\bLANGUAGE\s+(\w+)").Select(m => m.Groups[1].Value), l => Assert.Contains(l, new[] { "sql", "plpgsql" }));
        Assert.Equal([own], Regex.Matches(sql, @"\bCREATE\s+TABLE\s+(\w+)").Select(m => m.Groups[1].Value));
        // the only rows written are identities: no statement writes the policy or the stage table
        Assert.All(Regex.Matches(sql, @"\b(?:INSERT\s+INTO|UPDATE)\s+([tcfkr]_[0-9a-f]{8})").Select(m => m.Groups[1].Value), t => Assert.Equal(own, t));
        // the identity follows every write of a policy and every stage written for it, at commit
        Assert.Contains($"CREATE CONSTRAINT TRIGGER {CompanySchema.Map["ApprovalPolicyIdentity.TrOnPolicy"]} AFTER INSERT OR UPDATE ON {policy} DEFERRABLE INITIALLY DEFERRED", sql);
        Assert.Contains($"CREATE CONSTRAINT TRIGGER {CompanySchema.Map["ApprovalPolicyIdentity.TrOnStage"]} AFTER INSERT ON {stage} DEFERRABLE INITIALLY DEFERRED", sql);
        Assert.Contains($"CREATE UNIQUE INDEX {CompanySchema.Map["ApprovalPolicyIdentity.UqActive"]} ON {own} ({CompanySchema.Map["ApprovalPolicyIdentity.Fingerprint"]}) WHERE {CompanySchema.Map["ApprovalPolicyIdentity.IsActive"]} AND NOT {CompanySchema.Map["ApprovalPolicyIdentity.PreExisting"]}", sql);
        // the backfill (the only writer of the pre-existing mark) comes BEFORE the guard that resets the mark on every later write
        Assert.True(sql.IndexOf($"INSERT INTO {own}", StringComparison.Ordinal) < sql.IndexOf($"CREATE TRIGGER {CompanySchema.Map["ApprovalPolicyIdentity.TrGuard"]}", StringComparison.Ordinal));
        Assert.Contains($"NEW.{CompanySchema.Map["ApprovalPolicyIdentity.PreExisting"]} := false;", sql);
        // every part of the identity is in the fingerprint, and nothing else of the policy row is
        foreach (var part in new[] { "ApprovalPolicy.DocType", "ApprovalPolicy.Source", "ApprovalPolicy.BranchId", "ApprovalPolicy.MinAmount", "ApprovalPolicy.RequiredApprovals", "ApprovalPolicyConditions.JournalType",
                     "ApprovalPolicyConditions.RateOverride", "ApprovalPolicyConditions.SoftClosed", "ClosingLogic.PolicyClosingKind", "ApprovalPolicyStage.Required", "ApprovalPolicyStage.RoleId", "ApprovalPolicyStage.StageNo" })
            Assert.Contains(CompanySchema.Map[part], sql);
        foreach (var notPart in new[] { "ApprovalPolicy.NameAr", "ApprovalPolicy.NameEn", "ApprovalPolicy.UpdatedAtUtc" }) Assert.DoesNotContain(CompanySchema.Map[notPart], sql);
    }
}
