using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of the plan-execution company migration (Phase 3.8D step 4, PROVISIONAL number 0033) are DERIVED, not invented: prefix + the first 8 hex digits of SHA-256("hasm.company.&lt;number&gt;.plan-execution.v1:" +
/// logical token), exactly as <c>tools/schema/company-0033-plan-execution.cjs</c> generates them. The migration is ADDITIVE (owner decision X1-A): it replaces no function, pauses no trigger, alters no table, removes
/// nothing, seeds nothing and reads no setting.
/// </summary>
public class PlanExecutionSchemaDerivationTests
{
    private const string Number = "0033";   // the number follows the merge order: a renumbering changes this constant, the generator constant and regenerates the files
    private static readonly string[] Entities = ["CompensationPlan", "CompensationPlanPosition", "CompensationPlanLine", "CompensationPlanEvent", "CompensationPlanApproval", "CompensationPlanExecution",
        "CompensationPlanExecutionJournal", "EpochMember", "EpochEntryLineage", "EpochGuard"];

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hasm.company.{Number}.plan-execution.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0]));
    private static string Sql() => Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Sql.Contains(CompanySchema.Map["EpochGuard.FnEntry"])).Sql, "--[^\n]*", "");

    [Fact]
    public void Every_plan_execution_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.Equal(9, entries.Count(e => !e.Key.Contains('.')));                                                               // nine tables; EpochGuard has none
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Plan_execution_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_is_additive_replaces_no_function_pauses_no_trigger_alters_nothing_and_reads_no_setting()
    {
        var sql = Sql();
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bDELETE\s+FROM\b", sql);
        Assert.DoesNotMatch(@"(?<!\bBEFORE\s+)\bTRUNCATE\b", sql);
        Assert.DoesNotMatch(@"\bCREATE\s+OR\s+REPLACE\b", sql);                                                                 // no existing function is replaced
        Assert.DoesNotMatch(@"\bDISABLE\b", sql);                                                                               // no trigger is paused
        Assert.DoesNotMatch(@"\bALTER\b", sql);                                                                                 // no existing table, constraint, function or trigger is altered
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bSECURITY\s+DEFINER\b", sql);                                                                    // the optional definer of design 3.6 is not part of this slice
        Assert.DoesNotMatch(@"current_setting|set_config|\bSHOW\b|session_user|application_name", sql);                         // never a session flag (owner safeguard 9)
        Assert.DoesNotMatch(@"\bINSERT\s+INTO\b", sql);                                                                         // nothing is seeded, not even inside a function
        // every function and every trigger created here carries a name of this migration: no existing name is redefined
        var own = OwnEntries().Select(e => e.Value).ToHashSet();
        foreach (Match m in Regex.Matches(sql, @"CREATE\s+FUNCTION\s+(\w+)")) Assert.Contains(m.Groups[1].Value, own);
        foreach (Match m in Regex.Matches(sql, @"CREATE\s+(?:CONSTRAINT\s+)?TRIGGER\s+(\w+)")) Assert.Contains(m.Groups[1].Value, own);
        foreach (Match m in Regex.Matches(sql, @"CREATE\s+TABLE\s+(\w+)")) Assert.Contains(m.Groups[1].Value, own);
        // the only UPDATE statement is the closure-hash refresh of the plan, whose value the plan's own trigger computes
        var updates = Regex.Matches(sql, @"\bUPDATE\s+(t_[0-9a-f]{8})\s+SET\b");
        Assert.Equal(CompanySchema.CompensationPlan.Table, Assert.Single(updates).Groups[1].Value);
    }

    [Fact]
    public void Exactly_three_triggers_are_added_to_existing_tables_and_all_are_insert_guards()
    {
        var sql = Sql();
        var own = Entities.Where(e => CompanySchema.Map.ContainsKey(e)).Select(e => CompanySchema.Map[e]).ToHashSet();
        var onExisting = Regex.Matches(sql, @"CREATE\s+(?:CONSTRAINT\s+)?TRIGGER\s+(\w+)\s+(BEFORE|AFTER)\s+([A-Z ]+?)\s+ON\s+(\w+)").Where(m => !own.Contains(m.Groups[4].Value))
            .Select(m => (Name: m.Groups[1].Value, When: m.Groups[2].Value, Event: m.Groups[3].Value, Table: m.Groups[4].Value)).OrderBy(x => x.Table).ToList();
        Assert.Equal(new[]
        {
            (CompanySchema.EpochGuard.TrEntry, "BEFORE", "INSERT", CompanySchema.CostEntry.Table),
            (CompanySchema.EpochGuard.TrOperation, "BEFORE", "INSERT", CompanySchema.CostOperation.Table),
            (CompanySchema.EpochGuard.TrPosting, "BEFORE", "INSERT", CompanySchema.StockPosting.Table),
        }.OrderBy(x => x.Item4), onExisting);
    }

    [Fact]
    public void The_closure_hash_is_computed_by_PostgreSQL_in_the_byte_exact_canonical_form()
    {
        var sql = Sql();
        var c = CompanySchema.CompensationPlanPosition.ItemId; var w = CompanySchema.CompensationPlanPosition.WarehouseId;
        // lowercase hyphenated uuid text, a colon, sorted on the uuid columns (not on text), one newline, UTF-8, SHA-256, lowercase hex
        Assert.Contains($"encode(sha256(convert_to(COALESCE(string_agg(lower(c.{c}::text) || ':' || lower(c.{w}::text), E'\\n'", sql);
        Assert.Matches($@"ORDER BY c\.{c}, c\.{w}\), ''\), 'UTF8'\)\), 'hex'\)", sql);
        // the plan's trigger assigns it: the application never supplies it
        Assert.Contains($"NEW.{CompanySchema.CompensationPlan.ClosureHash} := {CompanySchema.EpochGuard.FnClosureHash}(NEW.{CompanySchema.CompensationPlan.Id});", sql);
        Assert.Contains($"NEW.{CompanySchema.CompensationPlan.CreatedXact} := pg_current_xact_id();", sql);
        Assert.Contains($"NEW.{CompanySchema.CompensationPlanExecution.CreatedXact} := pg_current_xact_id();", sql);
    }
}
