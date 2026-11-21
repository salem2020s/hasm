using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of company migration 0031 (attachment retention policy and legal holds, Phase R slice R1) are DERIVED, not invented: prefix + the first 8 hex digits of
/// SHA-256("hasm.company.0031.attachment-retention.v1:" + logical token), exactly <c>tools/schema/company-0031-attachment-retention.cjs</c> generates them. These tests re-derive every name from the committed
/// dictionary, prove uniqueness against every other name, and pin the additive, delete-free shape of the migration (R1 deletes nothing; the guarded Attachment table is not touched).
/// </summary>
public class AttachmentRetentionSchemaDerivationTests
{
    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ix|Ck)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.company.0031.attachment-retention.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static readonly string[] Tables = ["RetentionPolicy", "RetentionRule", "LegalHold"];

    private static IEnumerable<KeyValuePair<string, string>> OwnEntries() => CompanySchema.Map.Where(kv => Tables.Contains(kv.Key.Split('.')[0]));

    [Fact]
    public void Every_attachment_retention_name_is_the_deterministic_derivation_of_its_logical_token()
    {
        var entries = OwnEntries().ToList();
        Assert.True(entries.Count > 50);
        Assert.Equal(Tables.Length, entries.Count(e => !e.Key.Contains('.')));
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
    }

    [Fact]
    public void Attachment_retention_names_are_unique_and_collide_with_no_other_company_or_system_name()
    {
        var names = OwnEntries().Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var others = CompanySchema.Map.Where(kv => !Tables.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet();
        Assert.DoesNotContain(names, others.Contains);
        var system = Regex.Matches(string.Join("\n", MigrationScript.LoadEmbedded().Select(s => s.Sql)), @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.DoesNotContain(names, system.Contains);
    }

    [Fact]
    public void The_migration_is_additive_forward_only_deletes_nothing_never_touches_the_attachment_table_and_guards_every_table()
    {
        var sql = Regex.Replace(MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 31).Sql, "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\bTRUNCATE\s+TABLE\b", sql);
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bALTER\s+TABLE\b", sql);                                                       // no existing table is altered (the guarded Attachment table stays exactly as it is)
        Assert.DoesNotMatch(@"\bUPDATE\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));
        Assert.DoesNotMatch(@"\b(INSERT\s+INTO|DELETE\s+FROM)\s+t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));   // nothing is seeded or rewritten outside the guards
        foreach (var table in Tables)
        {
            Assert.Contains($"REVOKE ALL ON TABLE {CompanySchema.Map[table]} FROM PUBLIC", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[table + ".TrRow"]} BEFORE INSERT OR UPDATE OR DELETE ON {CompanySchema.Map[table]}", sql);
            Assert.Contains($"CREATE TRIGGER {CompanySchema.Map[table + ".TrTruncate"]} BEFORE TRUNCATE ON {CompanySchema.Map[table]}", sql);
        }
        // the independence rules are database CHECKs, not only service rules
        Assert.Contains($"{CompanySchema.Map["RetentionPolicy.ApprovedBy"]} <> {CompanySchema.Map["RetentionPolicy.DraftedBy"]}", sql);
        Assert.Contains($"{CompanySchema.Map["LegalHold.ReleasedBy"]} <> {CompanySchema.Map["LegalHold.PlacedBy"]}", sql);
    }
    [Fact]
    public void The_runtime_role_gets_exact_least_privilege_on_the_retention_tables_and_never_delete_or_truncate()
    {
        string[] Privileges(string table) => [.. CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges.Order()];
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.RetentionPolicy.Table));
        Assert.Equal(["INSERT", "SELECT"], Privileges(CompanySchema.RetentionRule.Table));
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], Privileges(CompanySchema.LegalHold.Table));
        foreach (var table in new[] { CompanySchema.RetentionPolicy.Table, CompanySchema.RetentionRule.Table, CompanySchema.LegalHold.Table })
        {
            var all = CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges;
            Assert.DoesNotContain("DELETE", all);
            Assert.DoesNotContain("TRUNCATE", all);
            Assert.DoesNotContain("REFERENCES", all);
            Assert.DoesNotContain("TRIGGER", all);
        }
    }
}
