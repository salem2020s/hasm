using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

public class CompanyRuntimeRoleTests
{
    [Fact]
    public void Role_name_is_deterministic_valid_opaque_and_unique_per_database()
    {
        var a = CompanyRuntimeRole.NameFor("d0123456789abcdef0123456789abcdef");
        Assert.Equal(a, CompanyRuntimeRole.NameFor("d0123456789abcdef0123456789abcdef"));
        Assert.NotEqual(a, CompanyRuntimeRole.NameFor("d0123456789abcdef0123456789abcdee"));
        Assert.True(PhysicalIdentifier.IsValid(a));
        Assert.True(CompanyRuntimeRole.IsRuntimeRoleName(a));
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", a);   // not reversible to the database name
        Assert.StartsWith("hasm_rt_", a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("postgres")]
    [InlineData("hasm_rt_short")]
    [InlineData("hasm_rt_0123456789abcdef0123456789abcdeg")]
    [InlineData("other_rt_0123456789abcdef0123456789abcdef")]
    public void Foreign_names_are_not_runtime_role_names(string? name) => Assert.False(CompanyRuntimeRole.IsRuntimeRoleName(name));

    [Fact]
    public void Role_name_refuses_invalid_database_names() =>
        Assert.Throws<ArgumentException>(() => CompanyRuntimeRole.NameFor("bad name; drop"));

    [Fact]
    public void Passwords_are_random_url_safe_and_long()
    {
        var set = Enumerable.Range(0, 50).Select(_ => CompanyRuntimeRole.GeneratePassword()).ToList();
        Assert.Equal(50, set.Distinct().Count());
        Assert.All(set, p => { Assert.True(p.Length >= 43); Assert.Matches("^[A-Za-z0-9_-]+$", p); });
    }

    /// <summary>RFC 7677 section 3 example (user "user", password "pencil"): the stored verifier must authenticate the published proof.</summary>
    [Fact]
    public void Scram_verifier_matches_the_rfc7677_exchange()
    {
        var salt = Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==");
        var verifier = CompanyRuntimeRole.ScramVerifier("pencil", salt, 4096);
        var m = Regex.Match(verifier, @"^SCRAM-SHA-256\$4096:([^$]+)\$([^:]+):(.+)$");
        Assert.True(m.Success);
        Assert.Equal("W22ZaJ0SNY7soEsUEjb6gQ==", m.Groups[1].Value);
        var storedKey = Convert.FromBase64String(m.Groups[2].Value);
        var serverKey = Convert.FromBase64String(m.Groups[3].Value);

        const string nonce = "rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";
        var authMessage = Encoding.UTF8.GetBytes($"n=user,r=rOprNGfwEbeRWgbNEkqO,r={nonce},s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,c=biws,r={nonce}");
        var proof = Convert.FromBase64String("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=");
        var signature = HMACSHA256.HashData(storedKey, authMessage);
        var clientKey = proof.Zip(signature, (p, s) => (byte)(p ^ s)).ToArray();
        Assert.Equal(storedKey, SHA256.HashData(clientKey));
        Assert.Equal("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=", Convert.ToBase64String(HMACSHA256.HashData(serverKey, authMessage)));
    }

    [Fact]
    public void Verifier_never_contains_the_password_or_a_quote_and_is_salted()
    {
        var pw = CompanyRuntimeRole.GeneratePassword();
        var v1 = CompanyRuntimeRole.ScramVerifier(pw);
        Assert.DoesNotContain(pw, v1);
        Assert.DoesNotContain("'", v1);
        Assert.NotEqual(v1, CompanyRuntimeRole.ScramVerifier(pw));
    }
}

public class CompanyRuntimeOptionsTests
{
    private static HasmDatabaseOptions Options(Action<Dictionary<string, string?>>? tweak = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["Hasm:Database:Host"] = "db.internal", ["Hasm:Database:SystemDatabaseName"] = "dsystem0000000000000000000000000001",
            ["Hasm:Database:Username"] = "api", ["Hasm:Database:Password"] = "api-secret",
            ["Hasm:Database:AdminUsername"] = "admin", ["Hasm:Database:AdminPassword"] = "admin-secret",
        };
        tweak?.Invoke(d);
        return HasmDatabaseOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(d).Build());
    }

    private const string Db = "d0123456789abcdef0123456789abcdef";

    [Fact]
    public void Runtime_connection_string_uses_the_company_role_a_bounded_pool_and_only_that_database()
    {
        var o = Options(d => d["Hasm:Database:CompanyMaxPoolSize"] = "7");
        var b = new NpgsqlConnectionStringBuilder(o.BuildCompanyRuntimeConnectionString(Db, CompanyRuntimeRole.NameFor(Db), "pw-1"));
        Assert.Equal(Db, b.Database);
        Assert.Equal(CompanyRuntimeRole.NameFor(Db), b.Username);
        Assert.NotEqual("api", b.Username);                 // never the shared runtime account
        Assert.NotEqual("admin", b.Username);               // never the provisioning account
        Assert.True(b.Pooling);
        Assert.Equal(7, b.MaxPoolSize);
        Assert.Equal(0, b.MinPoolSize);
        Assert.True(b.ConnectionIdleLifetime > 0 && b.Timeout > 0 && b.CommandTimeout > 0);
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
    }

    [Fact]
    public void Runtime_connection_refuses_system_maintenance_and_invalid_databases()
    {
        var o = Options();
        var role = CompanyRuntimeRole.NameFor(Db);
        Assert.Throws<ArgumentException>(() => o.BuildCompanyRuntimeConnectionString("dsystem0000000000000000000000000001", role, "pw"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyRuntimeConnectionString("postgres", role, "pw"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyRuntimeConnectionString("x; DROP DATABASE y", role, "pw"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyRuntimeConnectionString(Db, "Bad Role", "pw"));
        Assert.Throws<ArgumentException>(() => o.BuildCompanyRuntimeConnectionString(Db, role, ""));
    }

    [Fact]
    public void Runtime_connection_keeps_transport_validation() =>
        Assert.Throws<DatabaseConfigurationException>(() => Options(d => d["Hasm:Database:SslMode"] = "Disable")
            .BuildCompanyRuntimeConnectionString(Db, CompanyRuntimeRole.NameFor(Db), "pw"));

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("abc")]
    public void Pool_size_is_bounded(string value) =>
        Assert.Throws<DatabaseConfigurationException>(() => Options(d => d["Hasm:Database:CompanyMaxPoolSize"] = value));

    [Fact]
    public void Defaults_are_safe()
    {
        var o = Options();
        Assert.Equal(HasmDatabaseOptions.DefaultCompanyMaxPoolSize, o.CompanyMaxPoolSize);
        Assert.False(o.AllowOverprivilegedCompanyRuntime);
        Assert.Null(o.CompanyCredentialDirectory);
    }

    [Fact]
    public void Role_admin_connection_falls_back_to_admin_and_uses_the_role_admin_when_configured()
    {
        var plain = new NpgsqlConnectionStringBuilder(Options().BuildRoleAdminConnectionString());
        Assert.Equal("admin", plain.Username);
        var separate = new NpgsqlConnectionStringBuilder(Options(d => { d["Hasm:Database:RoleAdminUsername"] = "roles"; d["Hasm:Database:RoleAdminPassword"] = "roles-secret"; }).BuildRoleAdminConnectionString());
        Assert.Equal("roles", separate.Username);
        Assert.Equal("postgres", separate.Database);
        Assert.Throws<DatabaseConfigurationException>(() => Options(d => d["Hasm:Database:RoleAdminUsername"] = "roles").BuildRoleAdminConnectionString());
    }

    [Fact]
    public void Secrets_are_scrubbed_and_not_in_tostring()
    {
        var o = Options(d => { d["Hasm:Database:RoleAdminUsername"] = "roles"; d["Hasm:Database:RoleAdminPassword"] = "roles-secret"; });
        Assert.DoesNotContain("roles-secret", o.Scrub("failed: roles-secret admin-secret api-secret"));
        Assert.DoesNotContain("secret", o.ToString());
        Assert.DoesNotContain("secret", new CompanyCredential("u", "super-secret").ToString());
    }
}

public class CompanyRuntimePrivilegeCatalogTests
{
    private const string Db = "d0123456789abcdef0123456789abcdef";
    private static readonly string Role = CompanyRuntimeRole.NameFor(Db);

    [Fact]
    public void The_audit_table_is_select_and_insert_only()
    {
        var audit = CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.BusinessAudit.Table).Privileges;
        Assert.Equal(["INSERT", "SELECT"], audit.OrderBy(x => x));
        Assert.Empty(audit.Intersect(CompanyRuntimePrivileges.ForbiddenOnAudit));
    }

    [Fact]
    public void Schema_state_tables_are_read_only()
    {
        foreach (var table in new[] { CompanySchema.Identity.Table, SystemSchema.MigrationJournalTable })
            Assert.Equal(["SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges);
    }

    [Fact]
    public void No_catalog_entry_grants_a_dangerous_privilege()
    {
        var all = CompanyRuntimePrivileges.Tables.SelectMany(t => t.Privileges).Distinct();
        Assert.Empty(all.Intersect(["ALL", "TRUNCATE", "REFERENCES", "TRIGGER"]));
        // DELETE exists on exactly two tables: the journal LINES and ALLOCATIONS, whose rows PostgreSQL lets change only while the parent entry is a Draft (a draft edit rewrites its lines).
        Assert.Equal(new[] { CompanySchema.JournalLine.Table, CompanySchema.JournalAllocation.Table }.Order(), CompanyRuntimePrivileges.Tables.Where(t => t.Privileges.Contains("DELETE")).Select(t => t.Table).Order());
        // UPDATE is granted ONLY on the optimistic-concurrency configuration tables (revision-fenced by triggers); never on audit, history, reference or state.
        var updatable = CompanyRuntimePrivileges.Tables.Where(t => t.Privileges.Contains("UPDATE")).Select(t => t.Table).Order().ToArray();
        Assert.Equal(new[] { CompanySchema.Profile.Table, CompanySchema.Settings.Table, CompanySchema.CompanyCurrency.Table, CompanySchema.Branch.Table, CompanySchema.Warehouse.Table, CompanySchema.FiscalYear.Table, CompanySchema.FiscalPeriod.Table, CompanySchema.FiscalReopenRequest.Table, CompanySchema.Account.Table, CompanySchema.AccountSettings.Table, CompanySchema.NumberingRule.Table, CompanySchema.NumberCounter.Table, CompanySchema.NumberIssue.Table, CompanySchema.CostCenter.Table, CompanySchema.ApprovalPolicy.Table, CompanySchema.LedgerSettings.Table, CompanySchema.JournalEntry.Table, CompanySchema.JournalLine.Table }.Order(), updatable);
        Assert.Equal(["INSERT", "SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.BranchRevision.Table).Privileges.Order());
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.Branch.Table).Privileges.Order());
        Assert.Equal(["INSERT", "SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.WarehouseRevision.Table).Privileges.Order());
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.Warehouse.Table).Privileges.Order());
        Assert.Equal(["INSERT", "SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.ConfigRevision.Table).Privileges.Order());
        Assert.Equal(["SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.Currency.Table).Privileges);
    }

    [Fact]
    public void Database_statements_confine_connect_to_one_database_and_strip_public()
    {
        var sql = CompanyRuntimePrivileges.DatabaseStatements(Db, Role);
        Assert.Contains(sql, s => s == $"REVOKE ALL ON DATABASE \"{Db}\" FROM PUBLIC");
        Assert.Contains(sql, s => s == $"GRANT CONNECT ON DATABASE \"{Db}\" TO \"{Role}\"");
        Assert.Single(sql, s => s.StartsWith("GRANT"));
        Assert.All(sql, s => Assert.DoesNotContain("CREATE", s));
    }

    [Fact]
    public void Schema_statements_strip_first_then_grant_exactly_the_catalog()
    {
        var sql = CompanyRuntimePrivileges.SchemaStatements(Role);
        var firstGrant = sql.ToList().FindIndex(s => s.StartsWith("GRANT"));
        Assert.All(sql.Take(firstGrant), s => Assert.StartsWith("REVOKE", s));
        var grants = sql.Where(s => s.StartsWith("GRANT")).ToList();
        Assert.Equal(1 + CompanyRuntimePrivileges.Tables.Count, grants.Count);
        Assert.Contains($"GRANT USAGE ON SCHEMA public TO \"{Role}\"", grants);
        Assert.All(grants, g => { Assert.DoesNotContain("ALL", g); Assert.DoesNotContain("CREATE", g); Assert.DoesNotContain("PUBLIC", g); Assert.DoesNotContain("WITH GRANT OPTION", g); });
        Assert.All(grants, g => Assert.EndsWith($"TO \"{Role}\"", g));
    }

    [Fact]
    public void Statements_name_only_the_selected_role()
    {
        var other = CompanyRuntimeRole.NameFor("dfedcba9876543210fedcba9876543210");
        Assert.All(CompanyRuntimePrivileges.SchemaStatements(Role).Concat(CompanyRuntimePrivileges.DatabaseStatements(Db, Role)), s => Assert.DoesNotContain(other, s));
    }
}

public class BusinessAuditModelTests
{
    private static readonly Guid Company = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Business_stream_is_independent_of_the_company_access_stream()
    {
        var biz = AuditStream.ForBusiness(Company);
        var access = AuditStream.ForCompany(Company);
        Assert.Equal("business:11111111222233334444555555555555", biz.Key);
        Assert.True(biz.IsBusiness);
        Assert.False(access.IsBusiness);
        Assert.Equal(Company, biz.CompanyId);
        Assert.NotEqual(biz.Key, access.Key);
        Assert.NotEqual(AuditCanonical.GenesisHash(biz.Key), AuditCanonical.GenesisHash(access.Key));
        Assert.NotEqual(FileCheckpointStore.FileNameFor(biz.Key), FileCheckpointStore.FileNameFor(access.Key));
        Assert.NotEqual(AuditCheckpoint.GenesisPrevMac(biz.Key), AuditCheckpoint.GenesisPrevMac(access.Key));
        Assert.Throws<ArgumentException>(() => AuditStream.ForBusiness(Guid.Empty));
    }

    [Theory]
    [InlineData("business:11111111-2222-3333-4444-555555555555", true, true)]
    [InlineData("BUSINESS:11111111222233334444555555555555", true, true)]
    [InlineData("company:11111111-2222-3333-4444-555555555555", true, false)]
    [InlineData("11111111-2222-3333-4444-555555555555", true, false)]
    [InlineData("business:", false, false)]
    [InlineData("business:not-a-guid", false, false)]
    [InlineData("business:00000000-0000-0000-0000-000000000000", false, false)]
    public void Streams_parse(string text, bool ok, bool business)
    {
        Assert.Equal(ok, AuditStream.TryParse(text, out var s));
        if (ok) Assert.Equal(business, s.IsBusiness);
    }

    [Fact]
    public void System_table_map_keeps_the_established_storage_format()
    {
        Assert.Equal(SystemSchema.AuditEvent.Table, AuditTableMap.System.Table);
        Assert.Contains(SystemSchema.AuditEvent.LegacyRef, AuditStorage.Columns);
        Assert.Equal(AuditTableMap.System.SelectColumns, AuditStorage.Columns);
        Assert.Contains("@l", AuditTableMap.System.InsertSql);
        Assert.Same(AuditTableMap.System, AuditTableMap.For(AuditStream.System));
        Assert.Same(AuditTableMap.System, AuditTableMap.For(AuditStream.ForCompany(Company)));
    }

    [Fact]
    public void Business_table_map_targets_the_company_table_and_has_no_legacy_reference()
    {
        var map = AuditTableMap.For(AuditStream.ForBusiness(Company));
        Assert.Same(AuditTableMap.Business, map);
        Assert.Equal(CompanySchema.BusinessAudit.Table, map.Table);
        Assert.NotEqual(AuditTableMap.System.Table, map.Table);
        Assert.False(map.HasLegacyRef);
        Assert.Contains("NULL::bigint", map.SelectColumns);          // keeps the reader's column positions
        Assert.DoesNotContain("@l", map.InsertSql);
        Assert.Equal(15, Regex.Matches(map.InsertSql, "@").Count);
        Assert.Equal(16, Regex.Matches(AuditTableMap.System.InsertSql, "@").Count);
    }

    [Fact]
    public void A_business_record_cannot_be_replayed_into_another_stream()
    {
        var ring = new InMemoryAuditKeyRing();
        var key = ring.GetActive();
        var biz = AuditStream.ForBusiness(Company);
        var record = AuditCanonical.Seal(new AuditRecord(biz.Key, 1, Guid.NewGuid(), 1_000_000, 1000, Company, null, null, null, null, "x", 1, key.Id,
            AuditCanonical.GenesisHash(biz.Key), [], []), key);
        Assert.Equal(RecordCheck.Ok, AuditVerifier.CheckRecord(record, ring));
        var relabeled = record with { StreamKey = AuditStream.ForCompany(Company).Key };
        Assert.Equal(RecordCheck.HashMismatch, AuditVerifier.CheckRecord(relabeled, ring));
    }

    [Fact]
    public async Task Business_appends_are_restricted_to_the_business_event_range_without_legacy_references()
    {
        var trail = new AuditTrail(() => throw new InvalidOperationException("not reached"), TimeProvider.System);
        var stream = AuditStream.ForBusiness(Company);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => trail.AppendAsync(null!, null!, stream, DateTimeOffset.UtcNow, new AuditEventData(AuditEventTypes.RoleCreated)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => trail.AppendAsync(null!, null!, stream, DateTimeOffset.UtcNow, new AuditEventData(999)));
        await Assert.ThrowsAsync<ArgumentException>(() => trail.AppendAsync(null!, null!, stream, DateTimeOffset.UtcNow, new AuditEventData(1000, LegacyRef: 5)));
    }

}

public class CompanyBusinessAuditSchemaTests
{
    private static string Sql => MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 2).Sql;

    [Fact]
    public void Migration_is_additive_append_only_and_company_bound()
    {
        Assert.Contains($"CREATE TABLE {CompanySchema.BusinessAudit.Table}", Sql);
        Assert.Contains("BEFORE UPDATE OR DELETE", Sql);
        Assert.Contains("BEFORE TRUNCATE", Sql);
        Assert.Contains($"REVOKE ALL ON TABLE {CompanySchema.BusinessAudit.Table} FROM PUBLIC", Sql);
        Assert.Contains($"FROM {CompanySchema.Identity.Table}", Sql);                  // inserts must match the bound company identity
        Assert.Contains("'business:'", Sql);
        Assert.Contains(">= 1000", Sql);
        Assert.DoesNotContain("GRANT ", Regex.Replace(Sql, @"--[^\n]*", ""), StringComparison.OrdinalIgnoreCase);   // runtime grants are never part of a migration
    }

    [Fact]
    public void Migration_stays_checksummed()
    {
        var one = MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 2);
        Assert.Equal(64, one.Checksum.Length);
        Assert.NotEqual(one.Checksum, MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 1).Checksum);
    }
}

public sealed class CompanyCredentialStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "hasm-cred-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    [Fact]
    public async Task Credentials_round_trip_through_an_owner_only_file_named_by_company_id()
    {
        var store = new FileCompanyCredentialStore(dir);
        var id = Guid.NewGuid();
        Assert.Null(await store.GetAsync(id));
        Assert.False(store.Exists(id));

        var staged = store.Stage(id, new CompanyCredential("hasm_rt_x", "pw-one"));
        Assert.False(store.Exists(id));                       // nothing installed until committed
        store.Commit(id, staged);
        Assert.True(store.Exists(id));
        Assert.Equal(id.ToString("N") + ".hcred", Path.GetFileName(store.PathFor(id)));
        var read = await store.GetAsync(id);
        Assert.Equal(("hasm_rt_x", "pw-one"), (read!.Username, read.Password));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        store.Commit(id, store.Stage(id, new CompanyCredential("hasm_rt_x", "pw-two")));    // rotation replaces atomically
        Assert.Equal("pw-two", (await store.GetAsync(id))!.Password);
    }

    [Fact]
    public async Task Damaged_or_empty_files_fail_closed_with_a_code_only()
    {
        var store = new FileCompanyCredentialStore(dir);
        var id = Guid.NewGuid();
        // Secure directory and file first (as the real store does), so that content validation, not the permission check, is what is exercised.
        AuditFilePermissions.CreateOwnerOnlyDirectory(dir);
        void Write(string content)
        {
            if (File.Exists(store.PathFor(id))) File.Delete(store.PathFor(id));
            AuditFilePermissions.CreateOwnerOnlyFile(store.PathFor(id), System.Text.Encoding.UTF8.GetBytes(content));
        }

        Write("{not json");
        var ex = await Assert.ThrowsAsync<CompanyCredentialException>(() => store.GetAsync(id));
        Assert.Equal("credential_invalid", ex.Code);
        Write("{\"v\":1,\"username\":\"\",\"password\":\"\"}");
        ex = await Assert.ThrowsAsync<CompanyCredentialException>(() => store.GetAsync(id));
        Assert.Equal("credential_invalid", ex.Code);
        Write("{\"v\":2,\"username\":\"a\",\"password\":\"b\"}");
        ex = await Assert.ThrowsAsync<CompanyCredentialException>(() => store.GetAsync(id));
        Assert.Equal("credential_invalid", ex.Code);
        Write("");
        ex = await Assert.ThrowsAsync<CompanyCredentialException>(() => store.GetAsync(id));
        Assert.Equal("credential_invalid", ex.Code);
    }

    [Fact]
    public async Task A_credential_file_or_directory_open_to_other_accounts_is_rejected_before_it_is_read()
    {
        if (OperatingSystem.IsWindows()) return;   // POSIX modes only; Windows ACL handling is shared with the audit-key store and covered there

        var store = new FileCompanyCredentialStore(dir);
        var id = Guid.NewGuid();
        store.Commit(id, store.Stage(id, new CompanyCredential("hasm_rt_x", "pw")));
        Assert.Equal("hasm_rt_x", (await store.GetAsync(id))!.Username);                       // 0600 file in a 0700 directory is accepted

        File.SetUnixFileMode(store.PathFor(id), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Equal("credential_permissions", (await Assert.ThrowsAsync<CompanyCredentialException>(() => store.GetAsync(id))).Code);

        File.SetUnixFileMode(store.PathFor(id), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.Equal("credential_permissions", (await Assert.ThrowsAsync<CompanyCredentialException>(() => store.GetAsync(id))).Code);

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.NotNull(await store.GetAsync(id));
    }

    [Fact]
    public void Discard_removes_a_staged_file()
    {
        var store = new FileCompanyCredentialStore(dir);
        var staged = store.Stage(Guid.NewGuid(), new CompanyCredential("a", "b"));
        FileCompanyCredentialStore.Discard(staged);
        Assert.False(File.Exists(staged));
    }
}
