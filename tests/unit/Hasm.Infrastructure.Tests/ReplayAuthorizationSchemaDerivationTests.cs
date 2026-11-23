using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0037 (T2 slice A1: the replay signer registry, the RSA verifier and the grant shells) are DERIVED, not invented: prefix + the first 8 hex digits of
/// SHA-256("hasm.company.0037.replay-authorization.v1:" + logical token), exactly as <c>tools/schema/company-0037-replay-signer-registry.cjs</c> generates them. The migration is additive and passes the migration guard.
/// </summary>
public class ReplayAuthorizationSchemaDerivationTests
{
    private static readonly string[] Entities = ["ReplaySignerKey", "ReplayExecutionGrant", "ReplayGrantUse", "ReplayAuthorization"];

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0037.replay-authorization.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0]));
    private static string Sql() => Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 37).Sql, "--[^\n]*", "");

    [Fact]
    public void Every_name_is_the_deterministic_derivation_of_its_logical_token_and_none_collides()
    {
        var entries = OwnEntries().Where(e => e.Key != "ReplayAuthorization").ToList();
        Assert.True(entries.Count >= 40);
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_is_additive_names_every_relation_with_its_schema_and_uses_no_unsafe_construct()
    {
        var sql = Sql();
        Assert.DoesNotMatch(@"\bDROP\b|\bTRUNCATE\s+(?!.*BEFORE)|\bDELETE\s+FROM\b|\bALTER\b|\bCREATE\s+OR\s+REPLACE\b|\bGRANT\b|\bDISABLE\b|\bENABLE\b", sql.Replace("BEFORE TRUNCATE", "BEFORE_TRUNCATE"));
        Assert.DoesNotMatch(@"SECURITY\s+DEFINER|search_path|set_config|\bEXECUTE\s+'|CREATE\s+EXTENSION|\bTEMP(?:ORARY)?\b|\bdblink", sql);
        Assert.DoesNotMatch(@"\bpg_\w+", sql);                                                                                    // no catalog or system name at all
        Assert.Equal(3, Regex.Matches(sql, @"\bCREATE\s+TABLE\b").Count);
        // every table, constraint target, trigger target and function is written public.<name>
        foreach (Match m in Regex.Matches(sql, @"\b(?:CREATE\s+TABLE|REFERENCES|REVOKE\s+ALL\s+ON\s+TABLE|CREATE\s+FUNCTION|EXECUTE\s+FUNCTION)\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.IgnoreCase))
            Assert.StartsWith("public.", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(sql, @"\bON\s+(public\.|)(t_[0-9a-f]{8})\b", RegexOptions.IgnoreCase)) Assert.Equal("public.", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(sql, @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+\w+\s+ON\s+(\S+)", RegexOptions.IgnoreCase)) Assert.StartsWith("public.", m.Groups[1].Value);
        // the only functions are the three created here, with invoker rights, in PL/pgSQL, and the verifier uses mod() and never a division
        Assert.Equal(3, Regex.Matches(sql, @"\bCREATE\s+FUNCTION\b").Count);
        Assert.DoesNotMatch(@"\bLANGUAGE\s+(?!plpgsql\b)", sql);
        var verifier = Regex.Match(sql, @"CREATE FUNCTION public\." + CompanySchema.Map["ReplayAuthorization.FnRsaVerify"] + @"\(.*?\$\$;", RegexOptions.Singleline).Value;
        Assert.DoesNotContain(" / ", verifier);
        Assert.Contains("length(p_signature) <> 384", verifier);
        Assert.Contains("v_s >= p_modulus", verifier);
        Assert.Contains("FOR i IN 1..16 LOOP", verifier);
        Assert.Contains("RETURN v_r = v_em;", verifier);
        // nothing calls the verifier inside the migration itself (slice A1 is inert)
        Assert.Single(Regex.Matches(sql, Regex.Escape(CompanySchema.Map["ReplayAuthorization.FnRsaVerify"])));
        // privileges: only the REVOKE from PUBLIC; the runtime role's SELECT comes from the catalog, never from the migration
        Assert.Equal(3, Regex.Matches(sql, @"REVOKE ALL ON TABLE public\.t_[0-9a-f]{8} FROM PUBLIC").Count);
    }

    [Fact]
    public void The_embedded_set_contains_the_migration_as_number_37_and_the_guard_accepts_the_whole_set()
    {
        var company = MigrationScript.LoadEmbeddedCompany();
        Assert.Equal("0037_company_replay_signer_keys.sql", company.Single(s => s.Version == 37).Name);
        MigrationGuard.EnsureAcceptableSet(company);
        foreach (var script in company) MigrationGuard.EnsureAcceptable(script.Name, script.Sql);
    }
}
