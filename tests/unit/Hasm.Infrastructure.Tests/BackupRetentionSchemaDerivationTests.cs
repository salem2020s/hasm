using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The opaque names of the slice 6 system tables (backup legal holds, recovery-chain pins; D-134, D-135) are DERIVED: prefix + the first 8 hex digits of SHA-256("hasm.system.backup-retention.v1:" +
/// logical token). The seed carries no migration number, so the names survive the renumbering at merge. The migration is additive and append-only guarded.
/// </summary>
public class BackupRetentionSchemaDerivationTests
{
    private static readonly string[] Entities = ["BackupLegalHold", "BackupRetentionPin"];

    private static string Derive(string token)
    {
        var last = token.Split('.')[^1];
        var prefix = !token.Contains('.') ? "t_" : Regex.IsMatch(last, "^Fn[A-Z]") ? "f_" : Regex.IsMatch(last, "^Tr[A-Z]") ? "r_" : Regex.IsMatch(last, "^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)") ? "k_" : "c_";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm.system.backup-retention.v1:" + token)))[..8].ToLowerInvariant();
    }

    private static List<KeyValuePair<string, string>> Entries() => [.. SystemSchema.Map.Where(kv => Entities.Contains(kv.Key.Split('.')[0]))];

    private static string Sql() => MigrationScript.LoadEmbedded().Single(s => s.Name.EndsWith("_backup_retention.sql", StringComparison.Ordinal)).Sql;

    [Fact]
    public void Every_name_is_the_deterministic_derivation_of_its_logical_token_and_collides_with_nothing()
    {
        var entries = Entries();
        Assert.Equal(32, entries.Count);
        foreach (var (token, physical) in entries) Assert.Equal(Derive(token), physical);
        var names = entries.Select(e => e.Value).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Empty(names.Where(SystemSchema.Map.Where(kv => !Entities.Contains(kv.Key.Split('.')[0])).Select(kv => kv.Value).ToHashSet().Contains));
        Assert.Empty(names.Where(CompanySchema.Map.Values.ToHashSet().Contains));
    }

    [Fact]
    public void The_migration_is_additive_append_only_guarded_and_never_grants()
    {
        var sql = Regex.Replace(Sql(), "--[^\n]*", "");
        Assert.DoesNotMatch(@"\bDROP\b", sql);
        Assert.DoesNotMatch(@"\bGRANT\b", sql);
        Assert.DoesNotMatch(@"\b(INSERT|UPDATE|DELETE)\s+(INTO|FROM)?\s*t_", Regex.Replace(sql, @"CREATE FUNCTION.*?\$\$;", "", RegexOptions.Singleline));
        Assert.Contains($"CREATE TRIGGER {SystemSchema.BackupLegalHold.TrRow} BEFORE INSERT OR UPDATE OR DELETE ON {SystemSchema.BackupLegalHold.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.BackupLegalHold.TrTruncate} BEFORE TRUNCATE ON {SystemSchema.BackupLegalHold.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.BackupRetentionPin.TrRow} BEFORE UPDATE OR DELETE ON {SystemSchema.BackupRetentionPin.Table}", sql);
        Assert.Contains($"CREATE TRIGGER {SystemSchema.BackupRetentionPin.TrTruncate} BEFORE TRUNCATE ON {SystemSchema.BackupRetentionPin.Table}", sql);
        foreach (var t in new[] { SystemSchema.BackupLegalHold.Table, SystemSchema.BackupRetentionPin.Table }) Assert.Contains($"REVOKE ALL ON TABLE {t} FROM PUBLIC", sql);
        // the migration is the LAST system migration: it takes the next free number at merge (allocation rule), so no test pins its number
        Assert.Equal(MigrationScript.LoadEmbedded().Max(s => s.Version), MigrationScript.LoadEmbedded().Single(s => s.Name.EndsWith("_backup_retention.sql", StringComparison.Ordinal)).Version);
    }
}
