using Hasm.Infrastructure.CompanyDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Completeness guard of the runtime grants: every company table of the schema dictionary is either granted to the runtime role in <see cref="CompanyRuntimePrivileges.Tables"/> or named on an explicit, JUSTIFIED exclusion list.
/// A table that is not listed is not accessible to the runtime (the owner connection of the service tests hides it): company 0030 (document links) shipped without its grant and the runtime role got 42501 on every
/// link call. This test fails for a new table until its grant (or a reasoned exclusion) is added.
/// </summary>
public class CompanyRuntimePrivilegesCompletenessTests
{
    /// <summary>Tables the runtime role is deliberately NOT granted, each with the reason. Empty today: every company table is used by a service.</summary>
    private static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>();

    private static IEnumerable<KeyValuePair<string, string>> TableTokens() => CompanySchema.Map.Where(kv => !kv.Key.Contains('.'));

    [Fact]
    public void Every_company_table_is_granted_to_the_runtime_role_or_explicitly_excluded_with_a_reason()
    {
        var tables = TableTokens().ToList();
        Assert.True(tables.Count > 90, "the schema dictionary was not read");                                                  // guards against an empty enumeration passing vacuously
        var granted = CompanyRuntimePrivileges.Tables.Select(t => t.Table).ToHashSet();
        var missing = tables.Where(t => !granted.Contains(t.Value) && !Excluded.ContainsKey(t.Key)).Select(t => $"{t.Key} ({t.Value})").Order().ToList();
        Assert.True(missing.Count == 0, "company tables without a runtime grant or a justified exclusion: " + string.Join(", ", missing));
    }

    [Fact]
    public void An_exclusion_names_an_existing_table_has_a_reason_and_is_really_not_granted()
    {
        var tables = TableTokens().ToDictionary(t => t.Key, t => t.Value);
        var granted = CompanyRuntimePrivileges.Tables.Select(t => t.Table).ToHashSet();
        foreach (var (token, reason) in Excluded)
        {
            Assert.True(tables.ContainsKey(token), $"{token} is excluded but is not a company table");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"the exclusion of {token} needs a reason");
            Assert.DoesNotContain(tables[token], granted);                                                                      // an exclusion that is also granted is a stale entry
        }
    }

    [Fact]
    public void Every_grant_names_a_real_table_and_never_gives_a_destructive_privilege_to_an_append_only_record()
    {
        var known = TableTokens().Select(t => t.Value).Append(Hasm.Infrastructure.SystemDatabase.SystemSchema.MigrationJournalTable).ToHashSet();
        foreach (var (table, privileges) in CompanyRuntimePrivileges.Tables)
        {
            Assert.Contains(table, known);
            Assert.Equal(privileges.Length, privileges.Distinct().Count());
            Assert.All(privileges, p => Assert.Contains(p, new[] { "SELECT", "INSERT", "UPDATE", "DELETE" }));                   // never TRUNCATE, REFERENCES or TRIGGER
        }
        // the append-only commercial records: documents are never deleted, events and links are only ever inserted
        string[] Of(string table) => [.. CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges];
        Assert.Equal(["SELECT", "INSERT"], Of(CompanySchema.CommercialDocumentEvent.Table));
        Assert.Equal(["SELECT", "INSERT"], Of(CompanySchema.DocumentLink.Table));
        Assert.DoesNotContain("DELETE", Of(CompanySchema.CommercialDocument.Table));
    }

    /// <summary>Every table name a migration creates, in any spelling PostgreSQL accepts: UNLOGGED/TEMP[ORARY], IF NOT EXISTS, an optional (quoted) <c>public.</c> prefix, a quoted name. Line comments are ignored.</summary>
    private static readonly System.Text.RegularExpressions.Regex CreateTable = new(
        "CREATE\\s+(?:UNLOGGED\\s+|TEMP(?:ORARY)?\\s+)?TABLE\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?(?:\"?public\"?\\.)?\"?([A-Za-z_][A-Za-z0-9_]*)\"?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static IEnumerable<string> CreatedTableNames(string sql) =>
        CreateTable.Matches(System.Text.RegularExpressions.Regex.Replace(sql, @"--[^\r\n]*", "")).Select(m => m.Groups[1].Value);

    [Fact]
    public void The_create_table_scan_recognises_every_spelling_of_a_table_creation()
    {
        // the scan must not miss a creation just because of its spelling (G review L of #132): a miss would let an unregistered table escape the guard
        Assert.Equal(["t_aaaaaaaa"], CreatedTableNames("CREATE TABLE t_aaaaaaaa (a int);"));
        Assert.Equal(["t_bbbbbbbb"], CreatedTableNames("create table if not exists t_bbbbbbbb (a int);"));
        Assert.Equal(["t_cccccccc"], CreatedTableNames("CREATE UNLOGGED TABLE t_cccccccc (a int);"));
        Assert.Equal(["t_dddddddd"], CreatedTableNames("CREATE TEMP TABLE t_dddddddd (a int); "));
        Assert.Equal(["t_eeeeeeee"], CreatedTableNames("CREATE TEMPORARY TABLE IF NOT EXISTS public.t_eeeeeeee (a int);"));
        Assert.Equal(["t_ffffffff"], CreatedTableNames("CREATE TABLE \"public\".\"t_ffffffff\" (a int);"));
        Assert.Equal(["Plain_Name"], CreatedTableNames("CREATE TABLE \"Plain_Name\" (a int);"));                                   // a non-opaque name is captured too, so the guard can refuse it
        Assert.Empty(CreatedTableNames("-- CREATE TABLE t_99999999 would be a comment\nSELECT 1;"));
    }

    [Fact]
    public void Every_table_created_by_a_company_migration_is_registered_in_the_schema_dictionary()
    {
        // the guard above trusts CompanySchema.Map: a table that a migration creates but nobody registered would escape it (F/G review L2), so the migrations themselves are read
        var assembly = typeof(CompanySchema).Assembly;
        var created = new SortedSet<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains("CompanyDatabase.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            foreach (var table in CreatedTableNames(reader.ReadToEnd())) created.Add(table);
        }
        Assert.True(created.Count > 90, "the company migrations were not read");
        var nonOpaque = created.Where(t => !System.Text.RegularExpressions.Regex.IsMatch(t, "^t_[0-9a-f]{8}$")).ToList();
        Assert.True(nonOpaque.Count == 0, "tables created by a company migration with a name that is not an opaque t_xxxxxxxx name: " + string.Join(", ", nonOpaque));
        var registered = TableTokens().Select(t => t.Value).ToHashSet();
        var unregistered = created.Where(t => !registered.Contains(t)).ToList();
        Assert.True(unregistered.Count == 0, "tables created by a company migration but absent from CompanySchema.Map: " + string.Join(", ", unregistered));
    }
}
