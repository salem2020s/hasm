using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Owner decision 2026-11-22 (Q3), against a real PostgreSQL cluster whose administrative account is NOT a superuser (the disposable test cluster has no superuser login at all):
/// every supported deployment, provisioning, upgrade, grant, backup and restore workflow runs end to end through the real Hasm.Admin commands with only that account (CREATEDB, owner of the
/// databases it creates) plus the separate CREATEROLE-only account for the runtime roles. Nothing here widens a privilege; if a workflow failed without superuser rights this test would fail.
/// It also proves the enforcement: the migrating commands refuse a superuser unless the explicit, logged override is given. Needs pg_dump/pg_restore of the server's major version on PATH.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class NonSuperuserMigrationAccountTests : CompanyRuntimeTestBase
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-nsu-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> loginRoles = [];
    protected override bool RolesRequired => true;

    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private static string RoleAdminConnection => Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)!;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
        await using var conn = new NpgsqlConnection(RoleAdminConnection);
        await conn.OpenAsync();
        foreach (var role in loginRoles)
        {
            if (!role.StartsWith("hasm_nsu_", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a role this test did not create.");
            await using var cmd = new NpgsqlCommand($"DROP ROLE IF EXISTS {Q(role)}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<(bool Super, bool CreateRole, bool CreateDb, bool Replication, bool BypassRls, bool ReadsAllData, bool WritesAllData)> Attributes(string role)
    {
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT r.rolsuper, r.rolcreaterole, r.rolcreatedb, r.rolreplication, r.rolbypassrls, pg_has_role(r.oid, 'pg_read_all_data', 'MEMBER'), pg_has_role(r.oid, 'pg_write_all_data', 'MEMBER') FROM pg_roles r WHERE r.rolname = @r", conn);
        cmd.Parameters.AddWithValue("r", role);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6));
    }

    private async Task<bool> OwnsDatabase(string database, string role)
    {
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) = @r FROM pg_database WHERE datname = @d", conn);
        cmd.Parameters.AddWithValue("r", role); cmd.Parameters.AddWithValue("d", database);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresRoleFact]
    public async Task The_supported_workflows_run_end_to_end_with_a_non_superuser_administrative_account_and_a_separate_role_account()
    {
        await StartCompanies();
        Directory.CreateDirectory(work);
        var storage = Path.Combine(work, "storage"); Directory.CreateDirectory(storage);
        var keys = Path.Combine(work, "keys"); FileKekRing.Generate(keys, "k1");
        var auditKeys = Path.Combine(work, "audit-keys"); Hasm.Infrastructure.Audit.FileAuditKeyRing.Generate(auditKeys, "a1");
        var cfg = Configured(useRoleAdmin: true);
        var roleAdminName = new NpgsqlConnectionStringBuilder(RoleAdminConnection).Username!;
        var env = new Dictionary<string, string?>
        {
            ["Hasm__Attachments__StorageDirectory"] = storage, ["Hasm__Encryption__KeyDirectory"] = keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
            ["Hasm__Backup__UnencryptedDevTestOnly"] = BackupOptions.UnencryptedAcknowledgement,
            ["Hasm__Audit__KeyDirectory"] = auditKeys, ["Hasm__Audit__ActiveKeyId"] = "a1", ["Hasm__Audit__CheckpointDirectory"] = Path.Combine(work, "checkpoints"),
        };

        // ---- the accounts: the administrator is NOT a superuser and holds nothing beyond CREATEDB; the role account is separate, CREATEROLE only
        var admin = await Attributes(cfg.AdminUsername!);
        Assert.False(admin.Super); Assert.False(admin.CreateRole); Assert.False(admin.Replication); Assert.False(admin.BypassRls); Assert.False(admin.ReadsAllData); Assert.False(admin.WritesAllData);
        Assert.True(admin.CreateDb);
        Assert.NotEqual(cfg.AdminUsername, roleAdminName);
        var roleAdmin = await Attributes(roleAdminName);
        Assert.False(roleAdmin.Super); Assert.True(roleAdmin.CreateRole); Assert.False(roleAdmin.CreateDb); Assert.False(roleAdmin.Replication); Assert.False(roleAdmin.BypassRls);
        Assert.Equal("off", await Scalar<string>("SELECT current_setting('is_superuser')"));

        // ---- init-system-db creates a NEW system database as that account (CREATEDB) and is idempotent; the account owns what it created
        var freshSystem = NewTestName();
        var freshEnv = new Dictionary<string, string?>(env) { ["Hasm__Database__SystemDatabaseName"] = freshSystem };
        var init = await AdminTool.RunWith(cfg, freshEnv, "init-system-db");
        Assert.True(init.Exit == 0, init.All);
        Assert.Contains("System database created.", init.Out);
        Assert.True(await OwnsDatabase(freshSystem, cfg.AdminUsername!));
        var again = await AdminTool.RunWith(cfg, freshEnv, "init-system-db");
        Assert.True(again.Exit == 0, again.All);
        Assert.Contains("already exists", again.Out);
        Assert.Equal(0, (await AdminTool.RunWith(cfg, env, "init-system-db")).Exit);                       // the harness' own system database: nothing pending

        // ---- the override is accepted by the three migrating commands only, and an unused override (this account is not a superuser) logs nothing
        var withOverride = await AdminTool.RunWith(cfg, env, "init-system-db", MigrationAccountPolicy.OverrideSwitch);
        Assert.True(withOverride.Exit == 0, withOverride.All);
        Assert.DoesNotContain("WARNING", withOverride.All);
        Assert.Equal(2, (await AdminTool.RunWith(cfg, env, "register-company", MigrationAccountPolicy.OverrideSwitch)).Exit);   // any other command: usage error

        // ---- provision-company through the CLI (a Pending company registered with a disposable-prefixed name)
        var pending = await Registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, pending.PhysicalDatabaseName);
        var provision = await AdminTool.RunWith(cfg, env, "provision-company", pending.Id.ToString());
        Assert.True(provision.Exit == 0, provision.All);
        Assert.Contains("Company is Ready", provision.Out);
        Assert.True(await OwnsDatabase(pending.PhysicalDatabaseName, cfg.AdminUsername!));

        // ---- migrate-companies: an UPGRADE of an existing company (built at the schema before the newest company migration)
        var all = MigrationScript.LoadEmbeddedCompany();
        var older = all.Where(s => s.Version < all.Max(m => m.Version)).ToList();
        var existing = await ReadyCompany(older);
        Assert.Equal(older.Count, await InCompany<long>(existing.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        var upgrade = await AdminTool.RunWith(cfg, env, "migrate-companies", "--company", existing.Id.ToString());
        Assert.True(upgrade.Exit == 0, upgrade.All);
        Assert.Contains($"Migrated (applied {all.Max(m => m.Version):0000})", upgrade.Out);
        Assert.Equal(all.Count, await InCompany<long>(existing.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        var upToDate = await AdminTool.RunWith(cfg, env, "migrate-companies");
        Assert.True(upToDate.Exit == 0, upToDate.All);

        // ---- grant-system-runtime (a real runtime login created through the role account) and grant-company-runtime (the role account creates the role, the owner grants)
        var runtimeRole = "hasm_nsu_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var runtimePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        loginRoles.Add(runtimeRole);
        await using (var rc = new NpgsqlConnection(RoleAdminConnection))
        {
            await rc.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {Q(runtimeRole)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{runtimePassword}'", rc);
            await create.ExecuteNonQueryAsync();
        }
        var systemGrantEnv = new Dictionary<string, string?>(env) { ["Hasm__Database__Username"] = runtimeRole, ["Hasm__Database__Password"] = runtimePassword };
        var grantSystem = await AdminTool.RunWith(cfg, systemGrantEnv, "grant-system-runtime");
        Assert.True(grantSystem.Exit == 0, grantSystem.All);
        var grantCompany = await AdminTool.RunWith(cfg, env, "grant-company-runtime", existing.Id.ToString());
        Assert.True(grantCompany.Exit == 0, grantCompany.All);
        // the real runtime login reaches the upgraded company through the API's own resolver
        await using (var conn = await RealResolver().OpenAsync(existing.Id))
        {
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync())!);
        }

        // ---- backup-run (system database and every Ready company), a single-company backup and a trial restore of it (restore into a NEW database as the same account)
        var runs = Path.Combine(work, "runs");
        var run = await AdminTool.RunWith(cfg, env, "backup-run", runs);
        Assert.True(run.Exit == 0, run.All);
        Assert.Single(Directory.GetDirectories(runs));
        var set = Path.Combine(work, "set");
        var backup = await AdminTool.RunWith(cfg, env, "backup-company", existing.Id.ToString(), set);
        Assert.True(backup.Exit == 0, backup.All);
        var trial = await AdminTool.RunWith(cfg, env, "trial-restore-company", set, existing.Id.ToString(), Path.Combine(work, "trial-attachments"));
        Assert.True(trial.Exit == 0, trial.All);
        Assert.Contains("OK", trial.Out);

        // nothing in any output names a database or a role or prints a secret (several commands print their options description: host and port only)
        var secrets = new List<string?>
        {
            Options.AdminPassword, Options.Password, runtimePassword, runtimeRole, Options.SystemDatabaseName, freshSystem, RoleOf(pending), RoleOf(existing),
            pending.PhysicalDatabaseName, existing.PhysicalDatabaseName, "Password=", "Username=",
        };
        foreach (var text in new[] { init.All, provision.All, upgrade.All, grantSystem.All, grantCompany.All, run.All, backup.All, trial.All })
            foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)))
                Assert.DoesNotContain(secret!, text, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresRoleFact]
    public async Task The_runner_refuses_a_superuser_account_unless_the_logged_override_is_set_and_refuses_before_changing_anything()
    {
        await StartCompanies();
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        var scripts = MigrationScript.LoadEmbedded();
        var journalBefore = await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}");
        var connection = Options.BuildSystemConnectionString(admin: true);

        // the real reading: this account is not a superuser, so the runner proceeds with no override and logs nothing
        var quiet = new List<string>();
        Assert.Empty((await new MigrationRunner(connection) { Log = quiet.Add }.ApplyAsync(scripts)).Applied);
        Assert.Empty(quiet);

        // a superuser reading (the test seam can only ADD a refusal; the test cluster has no superuser login to connect with)
        var refused = await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(connection) { AdditionalSuperuserReading = () => true }.ApplyAsync(scripts));
        Assert.Contains("superuser", refused.Message);
        Assert.Contains("--allow-superuser-for-development", refused.Message);
        Assert.Equal(journalBefore, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"));

        // the explicit override lets it run, and the use is logged
        var log = new List<string>();
        var allowed = await new MigrationRunner(connection) { AdditionalSuperuserReading = () => true, AllowSuperuser = true, Log = log.Add }.ApplyAsync(scripts);
        Assert.Empty(allowed.Applied);
        var line = Assert.Single(log);
        Assert.Contains("WARNING", line);
        Assert.Contains("--allow-superuser-for-development", line);

        // the early check of the commands that create a database before the runner starts
        await MigrationAccountPolicy.EnsureAsync(connection, allowSuperuser: false);                                    // non-superuser: fine
        var early = await Assert.ThrowsAsync<MigrationException>(async () =>
        {
            await using var conn = new NpgsqlConnection(connection);
            await conn.OpenAsync();
            await MigrationAccountPolicy.EnsureAsync(conn, allowSuperuser: false, log: null, additionalReading: () => true, CancellationToken.None);
        });
        Assert.Contains("Nothing was changed", early.Message);

        // ---- the three commands refuse BEFORE anything is created or changed (the seam supplies the superuser reading the test cluster cannot produce)
        var freshName = NewTestName();
        var freshOptions = new HasmDatabaseOptions
        {
            Host = Options.Host, Port = Options.Port, SystemDatabaseName = freshName, Username = Options.Username, Password = Options.Password,
            AdminUsername = Options.AdminUsername, AdminPassword = Options.AdminPassword, MaintenanceDatabase = Options.MaintenanceDatabase, SslMode = Options.SslMode, AllowInsecureTransport = true,
        };
        var initRefused = await Assert.ThrowsAsync<MigrationException>(() => new SystemDatabaseInitializer(freshOptions) { AdditionalSuperuserReading = () => true }.InitializeAsync());
        Assert.Contains("superuser", initRefused.Message);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", freshName)));            // refused before CREATE DATABASE

        var registry = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), NewTestName);
        var pendingCompany = await registry.RegisterPendingAsync();
        var provisionRefused = await Assert.ThrowsAsync<MigrationException>(() => new CompanyProvisioner(Options, registry) { AdditionalSuperuserReading = () => true }.ProvisionAsync(pendingCompany.Id));
        Assert.Contains("superuser", provisionRefused.Message);
        Assert.Equal(ProvisioningState.Pending, (await registry.GetAsync(pendingCompany.Id))!.State);                          // not even started, not marked Failed
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", pendingCompany.PhysicalDatabaseName)));

        var readyCompany = await ReadyCompany();
        var migrate = Assert.Single(await new CompanyMigrator(Options, Registry) { AdditionalSuperuserReading = () => true }.MigrateAsync(readyCompany.Id));
        Assert.Equal(CompanyMigrationStatus.Failed, migrate.Status);
        Assert.Contains("superuser", migrate.Detail);

        // with the explicit override (set by the tool from its switch) the same refusals become a logged run: the provisioner creates the database
        var overridden = Configured();
        overridden.AllowSuperuserMigrationAccountForDevelopment();
        var provisioned = await new CompanyProvisioner(overridden, registry) { AdditionalSuperuserReading = () => true }.ProvisionAsync(pendingCompany.Id);
        Assert.Equal(ProvisioningOutcome.Provisioned, provisioned.Outcome);
    }
}
