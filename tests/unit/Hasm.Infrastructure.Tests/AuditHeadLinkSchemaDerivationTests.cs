using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Audit head integrity, slice 1 (option (a) of docs/AUDIT-HEAD-INTEGRITY-PLAN.md; PROVISIONAL numbers: system 0036, company 0037): the four physical names are the deterministic derivation of their logical tokens
/// (seed without a migration number, <c>tools/schema/audit-head-link.cjs</c>), unique in their dictionaries and absent from every other migration; each migration is strictly additive (one function, one BEFORE INSERT
/// trigger, nothing replaced, dropped, disabled or written); every relation is schema-qualified and no type or relation name is left that a temporary object could shadow (review Y1 of PR 228); the genesis
/// expression is the one the plan states (review Y2); and both migrations pass the migration guard.
/// </summary>
public class AuditHeadLinkSchemaDerivationTests
{
    private const int SystemVersion = 36, CompanyVersion = 37;   // PROVISIONAL numbers: change with the merge order (together with the generator constants and the file names)
    private const string GenesisExpression = "sha256(convert_to('HASM-AUDIT-GENESIS-v1', 'UTF8') || decode('00', 'hex') || convert_to(NEW.{0}, 'UTF8'))";

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.audit-head-link.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static string Code(string sql) => Regex.Replace(sql, "--[^\n]*", "");

    [Fact]
    public void The_four_names_are_the_deterministic_derivation_of_their_tokens_and_unique()
    {
        Assert.Equal(Derive("AuditHeadLink.FnSystemLink"), SystemSchema.AuditHeadLink.FnSystemLink);
        Assert.Equal(Derive("AuditHeadLink.TrSystemLink"), SystemSchema.AuditHeadLink.TrSystemLink);
        Assert.Equal(Derive("AuditHeadLink.FnBusinessLink"), CompanySchema.AuditHeadLink.FnBusinessLink);
        Assert.Equal(Derive("AuditHeadLink.TrBusinessLink"), CompanySchema.AuditHeadLink.TrBusinessLink);
        Assert.Equal(SystemSchema.AuditHeadLink.FnSystemLink, SystemSchema.Map["AuditHeadLink.FnSystemLink"]);
        Assert.Equal(CompanySchema.AuditHeadLink.TrBusinessLink, CompanySchema.Map["AuditHeadLink.TrBusinessLink"]);
        Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());
        Assert.Equal(CompanySchema.Map.Count, CompanySchema.Map.Values.Distinct().Count());
        string Others(IReadOnlyList<MigrationScript> all, int version) => string.Join("\n", all.Where(s => s.Version != version).Select(s => s.Sql));
        var inSystem = Regex.Matches(Others(MigrationScript.LoadEmbedded(), SystemVersion), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        var inCompany = Regex.Matches(Others(MigrationScript.LoadEmbeddedCompany(), CompanyVersion), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(SystemSchema.AuditHeadLink.FnSystemLink, inSystem);
        Assert.DoesNotContain(SystemSchema.AuditHeadLink.TrSystemLink, inSystem);
        Assert.DoesNotContain(CompanySchema.AuditHeadLink.FnBusinessLink, inCompany);
        Assert.DoesNotContain(CompanySchema.AuditHeadLink.TrBusinessLink, inCompany);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_migration_is_additive_schema_qualified_and_uses_the_exact_genesis_expression(bool system)
    {
        var script = system ? MigrationScript.LoadEmbedded().Single(s => s.Version == SystemVersion) : MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == CompanyVersion);
        var sql = Code(script.Sql);
        var (table, seq, hash, prev, stream, fn, tr) = system
            ? (SystemSchema.AuditEvent.Table, SystemSchema.AuditEvent.Seq, SystemSchema.AuditEvent.Hash, SystemSchema.AuditEvent.PrevHash, SystemSchema.AuditEvent.StreamKey, SystemSchema.AuditHeadLink.FnSystemLink, SystemSchema.AuditHeadLink.TrSystemLink)
            : (CompanySchema.BusinessAudit.Table, CompanySchema.BusinessAudit.Seq, CompanySchema.BusinessAudit.Hash, CompanySchema.BusinessAudit.PrevHash, CompanySchema.BusinessAudit.StreamKey, CompanySchema.AuditHeadLink.FnBusinessLink, CompanySchema.AuditHeadLink.TrBusinessLink);

        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b"));
        Assert.Single(Regex.Matches(sql, @"\bCREATE\s+TRIGGER\b"));
        Assert.DoesNotMatch(@"\bOR\s+REPLACE\b", sql);                                                          // the append-only function of the first audit migration is NOT replaced
        Assert.DoesNotMatch(new Regex(@"\b(DROP|ALTER|GRANT|REVOKE|UPDATE|DELETE|DISABLE|ENABLE|SECURITY\s+DEFINER|search_path|set_config|current_setting|TEMP|TEMPORARY|pg_temp|dblink)\b|\bTRUNCATE\b(?!')", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(@"\bINSERT\b(?!\s+ON\b)", sql);                                                      // INSERT only inside "BEFORE INSERT ON"
        Assert.Contains($"CREATE TRIGGER {tr} BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION {fn}()", sql);
        Assert.Contains("RAISE EXCEPTION 'audit_link_violation'", sql);
        Assert.Contains(string.Format(GenesisExpression, stream), sql);                                          // exactly the expression of the plan (Y2)
        // Y1: every mention of the audit table is public.<table>, except the table named by CREATE TRIGGER ... ON; the variable types come from the qualified columns
        var body = sql[sql.IndexOf("$$", StringComparison.Ordinal)..sql.LastIndexOf("$$", StringComparison.Ordinal)];
        Assert.DoesNotMatch($@"(?<!public\.){Regex.Escape(table)}", body);
        Assert.Contains($"head_seq public.{table}.{seq}%TYPE", body);
        Assert.Contains($"head_hash public.{table}.{hash}%TYPE", body);
        Assert.DoesNotMatch(@"\b(bytea|bigint|text|varchar|uuid)\b", body);                                      // no unqualified type name left in the function (a pg_temp type could shadow it)
        Assert.DoesNotMatch(@"\bpg_\w+", sql);                                                                   // no catalog name at all
        Assert.Contains($"NEW.{prev} IS DISTINCT FROM head_hash", body);
        Assert.Contains($"NEW.{seq} <> head_seq + 1", body);
        // the migration guard accepts it on its own and in its set
        MigrationGuard.EnsureAcceptable(script.Name, script.Sql);
    }

    [Fact]
    public void The_first_audit_migrations_and_their_append_only_guards_are_untouched()
    {
        var m5 = MigrationScript.LoadEmbedded().Single(s => s.Version == 5).Sql;
        Assert.Contains("audit events are append-only", m5);
        var m2 = MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 2).Sql;
        Assert.Contains("audit events are append-only", m2);
        // the new migrations create their own function and trigger names only
        Assert.DoesNotContain(SystemSchema.AuditHeadLink.FnSystemLink, m5);
        Assert.DoesNotContain(CompanySchema.AuditHeadLink.FnBusinessLink, m2);
    }
}
