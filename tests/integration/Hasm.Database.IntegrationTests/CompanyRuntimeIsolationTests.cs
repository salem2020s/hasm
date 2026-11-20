using System.Diagnostics;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Per-company PostgreSQL principals, tested with REAL distinct login identities. These tests create cluster-wide roles, so they run only on
/// a separately prepared, isolated, disposable PostgreSQL service that offers a CREATEROLE account (see <see cref="PostgresRoleTestEnvironment"/>);
/// they are skipped on an ordinary developer server and are required (never skipped) in CI. The ordinary test role keeps CREATEDB only.
/// </summary>
public class CompanyRuntimeIsolationTests : CompanyRuntimeTestBase
{
    protected override bool RolesRequired => true;

    private CompanyRuntimeProvisioner Provisioner() => new(Configured(useRoleAdmin: true), Registry);
    private FileCompanyCredentialStore CredentialFiles => new(CredentialDirectory);

    private async Task<(CompanyRecord Company, RuntimeGrantResult Result)> GrantedCompany(IReadOnlyList<MigrationScript>? scripts = null)
    {
        var c = await ReadyCompany(scripts);
        return (c, await Provisioner().GrantAsync(c.Id));
    }

    /// <summary>A direct, unpooled connection as the company's own runtime role, built from its installed credential file.</summary>
    private async Task<NpgsqlConnection> AsRuntime(CompanyRecord c, string? database = null, string? password = null)
    {
        var credential = (await CredentialFiles.GetAsync(c.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(c.PhysicalDatabaseName, credential.Username, password ?? credential.Password))
        {
            Pooling = false,
        };
        if (database is not null) cs.Database = database;
        var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<T?> Run<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T?)await cmd.ExecuteScalarAsync();
    }

    private static async Task Denied(NpgsqlConnection conn, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    // ---- provisioning ----------------------------------------------------------------------

    [PostgresRoleFact]
    public async Task Granting_creates_a_least_privilege_role_and_an_owner_only_credential_that_the_api_checks_accept()
    {
        await StartCompanies();
        var (c, result) = await GrantedCompany();

        Assert.Equal(RuntimeGrantOutcome.Applied, result.Outcome);
        Assert.True(result.RoleCreated);
        Assert.True(result.CredentialWritten);
        Assert.True(result.CredentialPresent);
        Assert.True(result.LoginVerified);

        var role = RoleOf(c);
        Assert.True(CompanyRuntimeRole.IsRuntimeRoleName(role));
        var attrs = await Scalar<string>(
            "SELECT concat_ws(',', rolcanlogin, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolinherit, rolconnlimit) FROM pg_roles WHERE rolname = @r", ("r", role));
        Assert.Equal($"t,f,f,f,f,f,f,{Configured().CompanyMaxPoolSize + 2}", attrs);          // login, no super/createdb/createrole/replication/bypassrls/inherit; bounded connections
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = @r", ("r", role)));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE r.rolname = @r", ("r", role)));
        Assert.Equal(0L, await InCompany<long>(c.PhysicalDatabaseName, "SELECT count(*) FROM pg_class k JOIN pg_roles r ON r.oid = k.relowner WHERE r.rolname = @r", ("r", role)));

        var credential = (await CredentialFiles.GetAsync(c.Id))!;
        Assert.Equal(role, credential.Username);
        Assert.True(credential.Password.Length >= 43);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(CredentialFiles.PathFor(c.Id)) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
        Assert.Empty(Directory.GetFiles(CredentialDirectory, "*.tmp"));

        // The credential never reached an application table.
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.Company.Table} WHERE {SystemSchema.Company.LastErrorCode} LIKE '%' || @p || '%'", ("p", credential.Password)));
        // The API's own resolver accepts it with the strict (no over-privilege) settings.
        await using var conn = await RealResolver().OpenAsync(c.Id);
        Assert.Equal(c.Id, await Run<Guid>(conn, $"SELECT {I.CompanyId} FROM {I.Table}"));
    }

    [PostgresRoleFact]
    public async Task Granting_again_is_idempotent_and_keeps_the_credential()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        var before = await File.ReadAllBytesAsync(CredentialFiles.PathFor(c.Id));

        var again = await Provisioner().GrantAsync(c.Id);

        Assert.Equal(RuntimeGrantOutcome.Applied, again.Outcome);
        Assert.False(again.RoleCreated);
        Assert.False(again.CredentialWritten);
        Assert.True(again.LoginVerified);
        Assert.Equal(before, await File.ReadAllBytesAsync(CredentialFiles.PathFor(c.Id)));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_roles WHERE rolname = @r", ("r", RoleOf(c))));
    }

    [PostgresRoleFact]
    public async Task Rotation_replaces_the_password_and_the_old_one_stops_working()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        var old = (await CredentialFiles.GetAsync(c.Id))!.Password;

        var rotated = await Provisioner().GrantAsync(c.Id, rotateCredential: true);

        Assert.True(rotated.CredentialWritten);
        Assert.False(rotated.RoleCreated);
        Assert.True(rotated.LoginVerified);
        var fresh = (await CredentialFiles.GetAsync(c.Id))!.Password;
        Assert.NotEqual(old, fresh);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AsRuntime(c, password: old));
        Assert.Equal(PostgresErrorCodes.InvalidPassword, ex.SqlState);
        await using var ok = await AsRuntime(c);
        Assert.Equal(1, await Run<int>(ok, "SELECT 1"));
    }

    // ---- credential install failure --------------------------------------------------------

    private static CompanyRuntimeProvisioner FailingInstall(CompanyRuntimeProvisioner _, HasmDatabaseOptions o, CompanyRegistry r) =>
        new(o, r) { BeforeCredentialInstall = () => throw new CompanyCredentialException("credential_unwritable") };

    [PostgresRoleFact]
    public async Task A_failed_rotation_install_restores_the_previous_password_and_changes_nothing()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        var before = await File.ReadAllBytesAsync(CredentialFiles.PathFor(c.Id));
        var old = (await CredentialFiles.GetAsync(c.Id))!.Password;

        var ex = await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => FailingInstall(Provisioner(), Configured(useRoleAdmin: true), Registry).GrantAsync(c.Id, rotateCredential: true));

        Assert.Equal(CompanyRuntimeProvisioningException.CredentialInstallFailed, ex.Code);
        Assert.Contains("restored", ex.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(CredentialFiles.PathFor(c.Id)));          // the installed credential is untouched...
        await using (var conn = await AsRuntime(c, password: old)) Assert.Equal(1, await Run<int>(conn, "SELECT 1"));   // ...and still logs in
        Assert.Empty(Directory.GetFiles(CredentialDirectory, "*.tmp"));                              // no stray secret left behind
        AssertNoLeak([ex.Message], c);

        Assert.True((await Provisioner().GrantAsync(c.Id, rotateCredential: true)).LoginVerified);   // recovery: simply run again
    }

    [PostgresRoleFact]
    public async Task A_failed_first_install_is_reported_with_the_recovery_step_and_recovers_by_rotation()
    {
        await StartCompanies();
        var c = await ReadyCompany();

        var ex = await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => FailingInstall(Provisioner(), Configured(useRoleAdmin: true), Registry).GrantAsync(c.Id));

        Assert.Equal(CompanyRuntimeProvisioningException.CredentialInstallFailed, ex.Code);
        Assert.Contains("--rotate-credential", ex.Message);                                           // not silently unusable: the operator is told what to do
        Assert.False(CredentialFiles.Exists(c.Id));
        Assert.Empty(Directory.GetFiles(CredentialDirectory, "*.tmp"));

        var recovered = await Provisioner().GrantAsync(c.Id, rotateCredential: true);
        Assert.False(recovered.RoleCreated);
        Assert.True(recovered.CredentialWritten);
        Assert.True(recovered.LoginVerified);
    }

    // ---- privileges, enforced by PostgreSQL --------------------------------------------------

    [PostgresRoleFact]
    public async Task The_runtime_role_cannot_own_alter_or_modify_audit_and_cannot_create_anything()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        await using var conn = await AsRuntime(c);

        // What it may do.
        Assert.Equal(c.Id, await Run<Guid>(conn, $"SELECT {I.CompanyId} FROM {I.Table}"));
        Assert.Equal(27L, await Run<long>(conn, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        Assert.Equal(0L, await Run<long>(conn, $"SELECT count(*) FROM {B.Table}"));

        // What it must not: modify or truncate audit events (a privilege failure, before any trigger runs).
        await Denied(conn, $"UPDATE {B.Table} SET {B.Detail} = 'x'");
        await Denied(conn, $"DELETE FROM {B.Table}");
        await Denied(conn, $"TRUNCATE {B.Table}");
        // Alter the schema or its state.
        await Denied(conn, $"ALTER TABLE {B.Table} DISABLE TRIGGER {B.GuardTrigger}");
        await Denied(conn, $"ALTER TABLE {B.Table} ADD COLUMN zz int");
        await Denied(conn, $"DROP TABLE {B.Table}");
        await Denied(conn, $"DROP TRIGGER {B.GuardTrigger} ON {B.Table}");
        await Denied(conn, $"CREATE TABLE zz_new (a int)");
        await Denied(conn, "CREATE SCHEMA zz_schema");
        await Denied(conn, "CREATE TEMP TABLE zz_temp (a int)");
        await Denied(conn, $"UPDATE {I.Table} SET {I.CompanyId} = gen_random_uuid()");
        await Denied(conn, $"INSERT INTO {SystemSchema.MigrationJournalTable} VALUES (50, 'x', 'y', now())");
        await Denied(conn, $"DELETE FROM {SystemSchema.MigrationJournalTable}");
        // GRANT by a non-owner without grant option is not an error in PostgreSQL (a warning): what matters is that it has no effect.
        await using (var grant = new NpgsqlCommand($"GRANT ALL ON TABLE {B.Table} TO PUBLIC", conn)) await grant.ExecuteNonQueryAsync();
        Assert.Equal(0L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM aclexplode((SELECT relacl FROM pg_class WHERE oid = to_regclass('{B.Table}'))) WHERE grantee = 0"));
        await Denied(conn, $"ALTER TABLE {B.Table} OWNER TO CURRENT_USER");
        await Denied(conn, "CREATE ROLE zz_rogue");
        await Denied(conn, "ALTER SYSTEM SET work_mem = '64MB'");          // no global settings either

        // It cannot drop its own (or any) database: connected to the maintenance database it is not the owner.
        await using var other = await AsRuntime(c, database: Options.MaintenanceDatabase);
        await Denied(other, $"DROP DATABASE \"{c.PhysicalDatabaseName}\"");
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", c.PhysicalDatabaseName)));
    }

    [PostgresRoleFact]
    public async Task A_company_role_cannot_reach_another_company_or_the_system_database()
    {
        await StartCompanies();
        var (a, _) = await GrantedCompany();
        var (b, _) = await GrantedCompany();

        // A's login is refused by PostgreSQL itself on B's database, and the other way around: CONNECT is granted per company.
        var aOnB = await Assert.ThrowsAsync<PostgresException>(() => AsRuntime(a, database: b.PhysicalDatabaseName));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, aOnB.SqlState);
        var bOnA = await Assert.ThrowsAsync<PostgresException>(() => AsRuntime(b, database: a.PhysicalDatabaseName));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, bOnA.SqlState);

        // Each role holds no privilege on the other company's database, and CONNECT only on its own.
        Assert.True(await Scalar<bool>("SELECT has_database_privilege(@r, @d, 'CONNECT')", ("r", RoleOf(a)), ("d", a.PhysicalDatabaseName)));
        Assert.False(await Scalar<bool>("SELECT has_database_privilege(@r, @d, 'CONNECT')", ("r", RoleOf(a)), ("d", b.PhysicalDatabaseName)));
        Assert.False(await Scalar<bool>("SELECT has_database_privilege(@r, @d, 'CREATE')", ("r", RoleOf(a)), ("d", a.PhysicalDatabaseName)));

        // The system database (which may accept the connection through PUBLIC) offers it no table at all.
        Assert.Equal(0L, await Scalar<long>(
            "SELECT count(*) FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r','p','v','m','S') AND " +
            "(has_table_privilege(@r::name, c.oid, 'SELECT') OR has_table_privilege(@r::name, c.oid, 'INSERT') OR has_table_privilege(@r::name, c.oid, 'UPDATE') OR has_table_privilege(@r::name, c.oid, 'DELETE'))",
            ("r", RoleOf(a))));
        try
        {
            await using var system = await AsRuntime(a, database: Options.SystemDatabaseName);
            await Denied(system, $"SELECT * FROM {SystemSchema.Company.Table}");
        }
        catch (PostgresException ex) { Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); }   // an operator may also revoke PUBLIC CONNECT

        // And the application refuses to use one company's credential for another, before connecting.
        Credentials.Set(a.Id, (await CredentialFiles.GetAsync(b.Id))!);
        var resolver = new CompanyDatabaseResolver(Configured(), new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock) { ResolutionLifetime = TimeSpan.Zero };
        Assert.Equal(CompanyDatabaseException.RuntimeNotProvisioned, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(a.Id))).Code);
    }

    [PostgresRoleFact]
    public async Task Extra_privileges_are_detected_by_the_api_blocked_by_the_trigger_and_removed_by_granting_again()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        var role = RoleOf(c);
        await SeedEvent(c);                                                                                       // row-level triggers only fire when a row exists
        await InCompany(c.PhysicalDatabaseName, $"GRANT UPDATE, DELETE, TRUNCATE ON TABLE {B.Table} TO \"{role}\"");   // a mistaken manual grant

        // Defence in depth: even with the privilege, the append-only triggers refuse (the trigger blocks the owner too).
        await using (var conn = await AsRuntime(c))
        {
            foreach (var sql in new[] { $"UPDATE {B.Table} SET {B.Detail} = 'x'", $"DELETE FROM {B.Table}", $"TRUNCATE {B.Table}" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); });
                Assert.Equal(sql.StartsWith("TRUNCATE") ? "0A000" : "P0001", ex.SqlState);   // TRUNCATE hits the ConfigRevision foreign key first (0A000)
            }
        }
        // The API refuses to run against an over-privileged runtime role.
        Assert.Equal(CompanyDatabaseException.RuntimeOverprivileged, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => RealResolver().OpenAsync(c.Id))).Code);
        await using (var relaxed = await RealResolver(allowOverprivileged: true).OpenAsync(c.Id)) { }          // explicit opt-in only

        var again = await Provisioner().GrantAsync(c.Id);                                                        // converges back to the catalog

        Assert.True(again.LoginVerified);
        await using var strict = await RealResolver().OpenAsync(c.Id);
        await using var direct = await AsRuntime(c);
        await Denied(direct, $"UPDATE {B.Table} SET {B.Detail} = 'x'");
    }

    // ---- the audited transaction through the real role ------------------------------------------

    [PostgresRoleFact]
    public async Task Business_events_are_written_through_the_runtime_role_and_verified_without_any_owner_privilege()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        await InCompany(c.PhysicalDatabaseName, "CREATE TABLE zz_ledger (id int PRIMARY KEY, note text)");
        await InCompany(c.PhysicalDatabaseName, $"GRANT SELECT, INSERT ON TABLE zz_ledger TO \"{RoleOf(c)}\"");    // what a later phase's catalog entry would do
        var tx = new CompanyAuditedTransactions(RealResolver(), Audit, Clock);

        for (var i = 1; i <= 3; i++)
            await tx.ExecuteAsync(c.Id, async w =>
            {
                await using (var cmd = new NpgsqlCommand("INSERT INTO zz_ledger VALUES (@i, 'x')", w.Connection, w.Transaction))
                {
                    cmd.Parameters.AddWithValue("i", i);
                    await cmd.ExecuteNonQueryAsync();
                }
                await w.AppendAuditAsync(new AuditEventData(1000, Detail: "entry " + i));
            });

        var credential = (await CredentialFiles.GetAsync(c.Id))!;
        var readOnlyConnection = Configured().BuildCompanyRuntimeConnectionString(c.PhysicalDatabaseName, credential.Username, credential.Password);
        var report = await new AuditVerifier(readOnlyConnection, AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(c.Id));
        Assert.Equal(AuditVerdict.Verified, report.Verdict);
        Assert.Equal(3, report.Events);
        Assert.DoesNotContain(report.Findings, f => f.Code == "verifier_account_can_modify_audit");           // the runtime can neither own nor modify the table

        // Granting again converges on the catalog: the manual table grant that is not in it is removed.
        await Provisioner().GrantAsync(c.Id);
        await using var direct = await AsRuntime(c);
        await Denied(direct, "INSERT INTO zz_ledger VALUES (99, 'x')");
        Assert.Equal(3L, await Run<long>(direct, $"SELECT count(*) FROM {B.Table}"));                          // events remain readable
    }

    // ---- provisioning edge cases -----------------------------------------------------------

    [PostgresRoleFact]
    public async Task An_existing_role_with_dangerous_attributes_is_refused_and_nothing_is_granted()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await RoleAdmin($"CREATE ROLE \"{RoleOf(c)}\" LOGIN CREATEROLE");

        var ex = await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => Provisioner().GrantAsync(c.Id));

        Assert.Equal(CompanyRuntimeProvisioningException.RoleUnsafe, ex.Code);
        Assert.False(await Scalar<bool>("SELECT has_database_privilege(@r, @d, 'CONNECT')", ("r", RoleOf(c)), ("d", c.PhysicalDatabaseName)));
        Assert.False(CredentialFiles.Exists(c.Id));
        AssertNoLeak([ex.Message], c);
    }

    [PostgresRoleFact]
    public async Task A_safe_role_created_by_the_dba_is_adopted_and_gets_its_credential_through_rotation()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await RoleAdmin($"CREATE ROLE \"{RoleOf(c)}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION");

        var first = await Provisioner().GrantAsync(c.Id);
        Assert.False(first.RoleCreated);
        Assert.False(first.CredentialPresent);                       // the tool cannot know the DBA's password and says so
        Assert.False(first.LoginVerified);
        Assert.True(await Scalar<bool>("SELECT has_database_privilege(@r, @d, 'CONNECT')", ("r", RoleOf(c)), ("d", c.PhysicalDatabaseName)));

        var second = await Provisioner().GrantAsync(c.Id, rotateCredential: true);
        Assert.True(second.CredentialWritten);
        Assert.True(second.LoginVerified);
    }

    [PostgresRoleFact]
    public async Task Concurrent_grants_for_one_company_converge_without_error()
    {
        await StartCompanies();
        var c = await ReadyCompany();

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Provisioner().GrantAsync(c.Id)));

        Assert.Contains(results, r => r.Outcome == RuntimeGrantOutcome.Applied);
        Assert.Equal(1, results.Count(r => r.RoleCreated));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_roles WHERE rolname = @r", ("r", RoleOf(c))));
        var final = await Provisioner().GrantAsync(c.Id);
        Assert.True(final.LoginVerified);
    }

    [PostgresRoleFact]
    public async Task Upgrading_a_pre_existing_company_database_is_ordered_migrate_then_grant()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version == 1)]);
        Assert.Equal(CompanyRuntimeProvisioningException.SchemaUnmigrated,
            (await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => Provisioner().GrantAsync(legacy.Id))).Code);

        Assert.Equal(CompanyMigrationStatus.Migrated, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(legacy.Id)).Status);
        var result = await Provisioner().GrantAsync(legacy.Id);

        Assert.True(result.LoginVerified);
        await using var conn = await RealResolver().OpenAsync(legacy.Id);
    }

    [PostgresRoleFact]
    public async Task Failures_reveal_neither_names_nor_secrets()
    {
        await StartCompanies();
        var (c, _) = await GrantedCompany();
        var password = (await CredentialFiles.GetAsync(c.Id))!.Password;
        await Exec($"REVOKE CONNECT ON DATABASE {c.PhysicalDatabaseName} FROM \"{RoleOf(c)}\"");               // the database becomes unreachable for the role

        var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => RealResolver().OpenAsync(c.Id));

        Assert.Equal(CompanyDatabaseException.Unavailable, ex.Code);
        Assert.Null(ex.InnerException);
        AssertNoLeak([ex.Message, ex.ToString(), password, .. Logs.Entries.Select(l => l.Replace(password, ""))], c);
        Assert.DoesNotContain(Logs.Entries, l => l.Contains(password));
    }

    /// <summary>A structurally valid event inserted by the owner (the chain itself is not under test here).</summary>
    private Task SeedEvent(CompanyRecord c) => InCompany(c.PhysicalDatabaseName,
        $"INSERT INTO {B.Table} ({B.StreamKey},{B.Seq},{B.EventId},{B.OccurredAtUtc},{B.EventType},{B.CompanyId},{B.FormatVersion},{B.KeyId},{B.PrevHash},{B.Hash},{B.Mac}) " +
        $"VALUES ('{AuditStream.ForBusiness(c.Id).Key}', 1, gen_random_uuid(), now(), 1000, '{c.Id}', 1, 'k1', decode(repeat('00',32),'hex'), decode(repeat('01',32),'hex'), decode(repeat('02',32),'hex'))");

    private async Task RoleAdmin(string sql)
    {
        await using var conn = new NpgsqlConnection(Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Runs the real <c>Hasm.Admin</c> binary (own process, environment-only configuration) for the command-line suites.</summary>
public static class AdminTool
{
    public sealed record Result(int Exit, string Out, string Err) { public string All => Out + Err; }

    private static string? adminDll;

    private static string AdminDll()
    {
        if (adminDll is not null) return adminDll;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = dir.Parent!.Name;                                  // .../bin/<Configuration>/net10.0/
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "backend", "Hasm.Admin", "bin", configuration, dir.Name, "Hasm.Admin.dll");
            if (File.Exists(candidate)) return adminDll = candidate;
        }
        throw new FileNotFoundException("Hasm.Admin.dll was not built; build the solution first.");
    }

    public static Task<Result> Run(HasmDatabaseOptions o, params string[] args) => RunWith(o, null, args);

    public static async Task<Result> RunWith(HasmDatabaseOptions o, IDictionary<string, string?>? extra, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add(AdminDll());
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Hasm__", StringComparison.OrdinalIgnoreCase)).ToList()) info.Environment.Remove(key);
        var env = new Dictionary<string, string?>
        {
            ["Hasm__Database__Host"] = o.Host, ["Hasm__Database__Port"] = o.Port.ToString(), ["Hasm__Database__SystemDatabaseName"] = o.SystemDatabaseName,
            ["Hasm__Database__Username"] = o.Username, ["Hasm__Database__Password"] = o.Password,
            ["Hasm__Database__AdminUsername"] = o.AdminUsername, ["Hasm__Database__AdminPassword"] = o.AdminPassword,
            ["Hasm__Database__RoleAdminUsername"] = o.RoleAdminUsername, ["Hasm__Database__RoleAdminPassword"] = o.RoleAdminPassword,
            ["Hasm__Database__MaintenanceDatabase"] = o.MaintenanceDatabase, ["Hasm__Database__SslMode"] = o.SslMode.ToString(),
            ["Hasm__Database__AllowInsecureTransport"] = "true", ["Hasm__Database__CompanyCredentialDirectory"] = o.CompanyCredentialDirectory,
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string?>()) env[k] = v;
        foreach (var (k, v) in env) if (v is null) info.Environment.Remove(k); else info.Environment[k] = v;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return new Result(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>The administrative commands as an operator runs them (exit codes and output), on any disposable server.</summary>
public class CompanyAdminToolTests : CompanyRuntimeTestBase
{
    private static IReadOnlyList<MigrationScript> OnlyFirst => [.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version == 1)];

    [PostgresFact]
    public async Task Invalid_usage_exits_2_without_touching_anything()
    {
        await StartCompanies();
        foreach (var args in new[] { new[] { "migrate-companies", "--company" }, ["migrate-companies", "--company", "not-a-guid"], ["migrate-companies", "x"],
                     ["grant-company-runtime"], ["grant-company-runtime", "not-a-guid"], ["grant-company-runtime", Guid.NewGuid().ToString(), "--other"] })
        {
            var run = await AdminTool.Run(Configured(), args);
            Assert.Equal(2, run.Exit);
            Assert.Contains("Usage:", run.Err);
        }
    }

    [PostgresFact]
    public async Task Migrate_companies_reports_each_company_and_is_idempotent()
    {
        await StartCompanies();
        var legacy = await ReadyCompany(OnlyFirst);
        var current = await ReadyCompany();

        var first = await AdminTool.Run(Configured(), "migrate-companies");
        Assert.Equal(0, first.Exit);
        Assert.Contains($"Company {legacy.Id}: Migrated (applied 0002, 0003, 0004, 0005, 0006, 0007, 0008, 0009, 0010, 0011, 0012, 0013, 0014, 0015, 0016, 0017, 0018, 0019, 0020, 0021, 0022, 0023, 0024, 0025, 0026, 0027)", first.Out);
        Assert.Contains($"Company {current.Id}: UpToDate", first.Out);
        Assert.Contains("grant-company-runtime", first.Out);                     // reminder to re-grant after a migration

        var second = await AdminTool.Run(Configured(), "migrate-companies", "--company", legacy.Id.ToString());
        Assert.Equal(0, second.Exit);
        Assert.Contains($"Company {legacy.Id}: UpToDate", second.Out);
        AssertNoLeak([first.All, second.All], legacy, current);
    }

    [PostgresFact]
    public async Task A_partial_failure_exits_8_and_names_exactly_which_company_failed()
    {
        await StartCompanies();
        var good = await ReadyCompany(OnlyFirst);
        var bad = await ReadyCompany(OnlyFirst);
        await InCompany(bad.PhysicalDatabaseName, $"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = repeat('0', 64) WHERE version = 1");

        var run = await AdminTool.Run(Configured(), "migrate-companies");

        Assert.Equal(8, run.Exit);
        Assert.Contains($"Company {good.Id}: Migrated", run.Out);
        Assert.Contains($"Company {bad.Id}: Failed [migration]", run.Out);
        Assert.Contains("1 failed", run.Out);
        AssertNoLeak([run.All], good, bad);
        Assert.Equal(27L, await InCompany<long>(good.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    [PostgresFact]
    public async Task Migrating_only_one_unknown_company_exits_1()
    {
        await StartCompanies();
        var run = await AdminTool.Run(Configured(), "migrate-companies", "--company", Guid.NewGuid().ToString());
        Assert.Equal(1, run.Exit);
        Assert.Contains("not registered", run.Err);
    }

    [PostgresFact]
    public async Task Grant_without_role_creation_rights_exits_1_with_the_prerequisite_and_no_secret()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var run = await AdminTool.Run(Configured(), "grant-company-runtime", c.Id.ToString());
        Assert.Equal(1, run.Exit);
        Assert.Contains("role_privilege_required", run.Err);
        Assert.Contains("CREATEROLE", run.Err);
        AssertNoLeak([run.All], c);
    }
}

/// <summary>The grant and business-audit verification commands end to end with the real binary, on an isolated role-capable service.</summary>
public class CompanyAdminToolRoleTests : CompanyRuntimeTestBase
{
    protected override bool RolesRequired => true;

    [PostgresRoleFact]
    public async Task Grant_company_runtime_provisions_reruns_and_rotates_without_printing_secrets()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var options = Configured(useRoleAdmin: true);

        var first = await AdminTool.Run(options, "grant-company-runtime", c.Id.ToString());
        Assert.Equal(0, first.Exit);
        Assert.Contains("Runtime login role created", first.Out);
        Assert.Contains("Runtime login and schema gate verified", first.Out);
        var password = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(c.Id))!.Password;

        var second = await AdminTool.Run(options, "grant-company-runtime", c.Id.ToString());
        Assert.Equal(0, second.Exit);
        Assert.Contains("already existed", second.Out);
        Assert.Contains("Existing credential kept", second.Out);

        var rotate = await AdminTool.Run(options, "grant-company-runtime", c.Id.ToString(), "--rotate-credential");
        Assert.Equal(0, rotate.Exit);
        Assert.Contains("new credential was generated", rotate.Out);

        AssertNoLeak([first.All, second.All, rotate.All], c);
        Assert.DoesNotContain(password, first.All + second.All + rotate.All);
        Assert.NotEqual(password, (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(c.Id))!.Password);
    }

    [PostgresRoleFact]
    public async Task Business_streams_are_verified_and_anchored_through_the_runtime_role_by_the_tool()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var options = Configured(useRoleAdmin: true);
        var root = Path.Combine(Path.GetTempPath(), "hasm-audit-cli-" + Guid.NewGuid().ToString("N"));
        var keys = Path.Combine(root, "keys");
        var checkpoints = Path.Combine(root, "checkpoints");
        FileAuditKeyRing.Generate(keys, "k1");
        var audit = new Dictionary<string, string?>
        {
            ["Hasm__Audit__KeyDirectory"] = keys, ["Hasm__Audit__ActiveKeyId"] = "k1", ["Hasm__Audit__CheckpointDirectory"] = checkpoints,
        };
        try
        {
        var withoutCredential = await AdminTool.RunWith(options, audit, "audit-verify", "business:" + c.Id);
        Assert.Equal(1, withoutCredential.Exit);                                              // no runtime credential yet: nothing is read with a broader account
        Assert.Contains("grant-company-runtime", withoutCredential.Err);

        Assert.Equal(0, (await AdminTool.Run(options, "grant-company-runtime", c.Id.ToString())).Exit);
        var trail = AuditTrail.FromOptions(new AuditOptions { KeyDirectory = keys, ActiveKeyId = "k1", CheckpointDirectory = checkpoints }, Clock);
        var tx = new CompanyAuditedTransactions(RealResolver(), trail, Clock);
        for (var i = 0; i < 2; i++) await tx.ExecuteAsync(c.Id, w => w.AppendAuditAsync(new AuditEventData(1000, Detail: "secret business detail")));

        var verify = await AdminTool.RunWith(options, audit, "audit-verify", "business:" + c.Id);

        Assert.Equal(0, verify.Exit);
        Assert.Contains("Verdict: VERIFIED", verify.Out);
        Assert.Contains($"Stream: business:{c.Id:N}", verify.Out);
        Assert.Contains("sequence 1..2", verify.Out);
        Assert.DoesNotContain("secret business detail", verify.All);
        Assert.DoesNotContain("verifier_account_can_modify_audit", verify.All);               // the runtime role cannot modify the audit table
        AssertNoLeak([verify.All], c);

        var anchor = await AdminTool.RunWith(options, audit, "audit-anchor", "business:" + c.Id);
        Assert.Equal(0, anchor.Exit);
        Assert.Contains("anchored at sequence 2", anchor.Out);
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
