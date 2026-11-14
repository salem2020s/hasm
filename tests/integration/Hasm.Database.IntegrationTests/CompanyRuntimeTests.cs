using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Runtime resolution of company databases against real PostgreSQL. These suites use the single disposable test principal (an ordinary
/// test server has no per-company roles; that is covered by <see cref="CompanyRuntimeIsolationTests"/> on an isolated role-capable
/// service). They prove registry resolution, state handling, the schema gate and the absence of leaks.
/// </summary>
public class CompanyRuntimeResolutionTests : CompanyRuntimeTestBase
{
    private static MigrationScript Extra(string sql) => MigrationScript.Parse("0011_extra.sql", sql);
    private static IReadOnlyList<MigrationScript> OnlyFirst => [.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version == 1)];

    [PostgresFact]
    public async Task A_ready_company_opens_and_passes_the_schema_gate()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await using var conn = await SinglePrincipalResolver(c).OpenAsync(c.Id);
        await using var cmd = new NpgsqlCommand($"SELECT {I.CompanyId} FROM {I.Table}", conn);
        Assert.Equal(c.Id, (Guid)(await cmd.ExecuteScalarAsync())!);
    }

    [PostgresFact]
    public async Task An_unknown_company_is_refused_with_a_safe_code()
    {
        await StartCompanies();
        var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => SinglePrincipalResolver().OpenAsync(Guid.NewGuid()));
        Assert.Equal(CompanyDatabaseException.NotFound, ex.Code);
    }

    [PostgresFact]
    public async Task Companies_that_are_not_ready_are_refused_without_touching_their_database()
    {
        await StartCompanies();
        var pending = await Registry.RegisterPendingAsync();
        var provisioning = await Registry.RegisterPendingAsync();
        Assert.True(await Registry.TransitionAsync(provisioning.Id, ProvisioningState.Provisioning));
        var failed = await Registry.RegisterPendingAsync();
        Assert.True(await Registry.TransitionAsync(failed.Id, ProvisioningState.Provisioning));
        Assert.True(await Registry.TransitionAsync(failed.Id, ProvisioningState.Failed));

        foreach (var c in new[] { pending, provisioning, failed })
        {
            var resolver = SinglePrincipalResolver(c);
            var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(c.Id));
            Assert.Equal(CompanyDatabaseException.NotReady, ex.Code);
            Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", c.PhysicalDatabaseName)));   // nothing was created either
        }
    }

    [PostgresFact]
    public async Task A_missing_or_misplaced_credential_is_refused_before_any_connection()
    {
        await StartCompanies();
        var a = await ReadyCompany();
        var b = await ReadyCompany();
        var resolver = SinglePrincipalResolver();                                   // no credential at all
        Assert.Equal(CompanyDatabaseException.RuntimeNotProvisioned, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(a.Id))).Code);

        // Company B's role credential placed under company A: refused, so A can never be opened with B's login.
        Credentials.Set(a.Id, new CompanyCredential(RoleOf(b), "pw"));
        Assert.Equal(CompanyDatabaseException.RuntimeNotProvisioned, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(a.Id))).Code);
        // An ordinary (shared) account name is not a company role either.
        Credentials.Set(a.Id, new CompanyCredential(Options.Username!, "pw"));
        Assert.Equal(CompanyDatabaseException.RuntimeNotProvisioned, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(a.Id))).Code);
    }

    [PostgresFact]
    public async Task An_unusable_credential_store_fails_closed()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var resolver = new CompanyDatabaseResolver(Configured(true), new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)),
            new ThrowingCredentials(), Clock, Logs.CreateLogger("Hasm.Company")) { ResolutionLifetime = TimeSpan.Zero };
        Assert.Equal(CompanyDatabaseException.RuntimeNotProvisioned, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(c.Id))).Code);
    }

    private sealed class ThrowingCredentials : ICompanyCredentialProvider
    {
        public Task<CompanyCredential?> GetAsync(Guid companyId, CancellationToken ct = default) => throw new CompanyCredentialException("credential_permissions");
    }

    [PostgresFact]
    public async Task An_unavailable_database_is_a_safe_operational_error_that_leaks_nothing()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var resolver = SinglePrincipalResolver(c);
        await using (var ok = await resolver.OpenAsync(c.Id)) { }
        NpgsqlConnection.ClearAllPools();
        await Exec($"DROP DATABASE {c.PhysicalDatabaseName} WITH (FORCE)");         // the test's own hasm_test_ database

        var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(c.Id));

        Assert.Equal(CompanyDatabaseException.Unavailable, ex.Code);
        Assert.Null(ex.InnerException);
        AssertNoLeak([ex.Message, ex.ToString(), .. Logs.Entries], c);
        Assert.Contains(Logs.Entries, l => l.Contains("company_database_unavailable"));
    }

    [PostgresFact]
    public async Task Invalid_identifiers_cannot_enter_the_registry_at_all()
    {
        await StartCompanies();
        foreach (var name in new[] { "Bad;Name", "x\"; DROP DATABASE y; --", "pg_catalog", "has space", "UPPER" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                Exec($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,@n,2,now(),now())",
                    ("i", Guid.NewGuid()), ("n", name)));
            Assert.Equal("23514", ex.SqlState);
        }
    }

    [PostgresFact]
    public async Task Protected_database_names_in_the_registry_are_never_connected_to()
    {
        await StartCompanies();
        // Valid identifiers that nevertheless must never be treated as a company database, even with a matching role credential.
        foreach (var name in new[] { Options.SystemDatabaseName!, Options.MaintenanceDatabase })
        {
            var id = Guid.NewGuid();
            await Exec($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,@n,2,now(),now())",
                ("i", id), ("n", name));
            Credentials.Set(id, new CompanyCredential(CompanyRuntimeRole.NameFor(name), "pw"));
            var resolver = new CompanyDatabaseResolver(Configured(true), new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock,
                Logs.CreateLogger("Hasm.Company")) { ResolutionLifetime = TimeSpan.Zero };       // no connection-string override: the real builder decides
            var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(id));
            Assert.Equal(CompanyDatabaseException.Unavailable, ex.Code);
            Assert.DoesNotContain(name, ex.ToString());
        }
    }

    [PostgresFact]
    public async Task An_unmigrated_company_is_rejected_then_accepted_after_migration()
    {
        await StartCompanies();
        var legacy = await ReadyCompany(OnlyFirst);                                   // a company database created before Phase 3.1A-1
        var resolver = SinglePrincipalResolver(legacy);
        var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(legacy.Id));
        Assert.Equal(CompanyDatabaseException.SchemaUnmigrated, ex.Code);
        AssertNoLeak([ex.Message, ex.ToString(), .. Logs.Entries], legacy);
        Assert.DoesNotContain("CREATE TABLE", ex.ToString(), StringComparison.OrdinalIgnoreCase);       // no migration SQL

        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(legacy.Id));
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);

        await using var conn = await resolver.OpenAsync(legacy.Id);
    }

    [PostgresFact]
    public async Task A_database_newer_than_the_backend_is_rejected()
    {
        await StartCompanies();
        var newer = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany(), Extra("CREATE TABLE zz_future (a int);")]);
        var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => SinglePrincipalResolver(newer).OpenAsync(newer.Id));
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible, ex.Code);
    }

    [PostgresFact]
    public async Task A_modified_migration_checksum_or_a_foreign_identity_is_incompatible()
    {
        await StartCompanies();
        var tampered = await ReadyCompany();
        await InCompany(tampered.PhysicalDatabaseName, $"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = repeat('0', 64) WHERE version = 2");
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible,
            (await Assert.ThrowsAsync<CompanyDatabaseException>(() => SinglePrincipalResolver(tampered).OpenAsync(tampered.Id))).Code);

        var foreign = await ReadyCompany();
        await InCompany(foreign.PhysicalDatabaseName,
            $"ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; UPDATE {I.Table} SET {I.CompanyId} = gen_random_uuid(); ALTER TABLE {I.Table} ENABLE TRIGGER {I.GuardTrigger}");
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible,
            (await Assert.ThrowsAsync<CompanyDatabaseException>(() => SinglePrincipalResolver(foreign).OpenAsync(foreign.Id))).Code);
    }

    [PostgresFact]
    public async Task A_database_without_a_migration_journal_is_unmigrated()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await InCompany(c.PhysicalDatabaseName, $"ALTER TABLE {SystemSchema.MigrationJournalTable} RENAME TO zz_renamed");
        Assert.Equal(CompanyDatabaseException.SchemaUnmigrated,
            (await Assert.ThrowsAsync<CompanyDatabaseException>(() => SinglePrincipalResolver(c).OpenAsync(c.Id))).Code);
    }

    [PostgresFact]
    public async Task An_over_privileged_runtime_is_refused_unless_explicitly_allowed()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        // The single test principal owns the database and its tables, which is exactly what the API runtime must never do.
        var strict = SinglePrincipalResolver(c, allowOverprivileged: false);
        Assert.Equal(CompanyDatabaseException.RuntimeOverprivileged, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => strict.OpenAsync(c.Id))).Code);
        await using var relaxed = await SinglePrincipalResolver(c, allowOverprivileged: true).OpenAsync(c.Id);
    }

    [PostgresFact]
    public async Task A_successful_schema_check_is_reused_for_its_lifetime_and_forgotten_on_demand()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var resolver = new CompanyDatabaseResolver(Configured(true), new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)), Credentials, Clock,
            Logs.CreateLogger("Hasm.Company"))
        {
            ConnectionStringOverride = (db, _, _) => Options.BuildCompanyAdminConnectionString(db),
            ResolutionLifetime = TimeSpan.FromMinutes(5),
            SchemaCheckLifetime = TimeSpan.FromMinutes(5),
        };
        Credentials.Set(c.Id, new CompanyCredential(RoleOf(c), "pw"));
        await using (var first = await resolver.OpenAsync(c.Id)) { }
        await InCompany(c.PhysicalDatabaseName, $"ALTER TABLE {SystemSchema.MigrationJournalTable} RENAME TO zz_renamed");
        await using (var cached = await resolver.OpenAsync(c.Id)) { }                  // within the lifetime: not re-checked
        resolver.Forget(c.Id);
        await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(c.Id));
    }
}

public class CompanyMigrationTests : CompanyRuntimeTestBase
{
    private static IReadOnlyList<MigrationScript> OnlyFirst => [.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version == 1)];
    private static MigrationScript Extra(string sql) => MigrationScript.Parse("0011_extra.sql", sql);

    private async Task<long> JournalRows(CompanyRecord c) =>
        await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}");

    private async Task<bool> HasAuditTable(CompanyRecord c) =>
        await InCompany<bool>(c.PhysicalDatabaseName, $"SELECT to_regclass('{CompanySchema.BusinessAudit.Table}') IS NOT NULL");

    [PostgresFact]
    public async Task Fresh_provisioning_creates_the_business_audit_table_through_migration_0002()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        Assert.True(await HasAuditTable(c));
        Assert.Equal(10L, await JournalRows(c));
        Assert.Equal(0L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {CompanySchema.BusinessAudit.Table}"));
    }

    [PostgresFact]
    public async Task A_database_created_before_this_phase_upgrades_safely_and_keeps_its_data()
    {
        await StartCompanies();
        var legacy = await ReadyCompany(OnlyFirst);
        Assert.False(await HasAuditTable(legacy));
        await InCompany(legacy.PhysicalDatabaseName, "CREATE TABLE zz_existing (a int); INSERT INTO zz_existing VALUES (7)");

        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());

        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 10], outcome.Applied);
        Assert.True(await HasAuditTable(legacy));
        Assert.Equal(7, await InCompany<int>(legacy.PhysicalDatabaseName, "SELECT a FROM zz_existing"));
        Assert.Equal(legacy.Id, await InCompany<Guid>(legacy.PhysicalDatabaseName, $"SELECT {I.CompanyId} FROM {I.Table}"));
        Assert.Equal(ProvisioningState.Ready, (await Registry.GetAsync(legacy.Id))!.State);
    }

    [PostgresFact]
    public async Task Migrating_twice_is_a_noop()
    {
        await StartCompanies();
        var legacy = await ReadyCompany(OnlyFirst);
        var migrator = new CompanyMigrator(Options, Registry);
        Assert.Equal(CompanyMigrationStatus.Migrated, Assert.Single(await migrator.MigrateAsync()).Status);
        var checksums = await InCompany<string>(legacy.PhysicalDatabaseName, $"SELECT string_agg(checksum, ',' ORDER BY version) FROM {SystemSchema.MigrationJournalTable}");

        var second = Assert.Single(await migrator.MigrateAsync());

        Assert.Equal(CompanyMigrationStatus.UpToDate, second.Status);
        Assert.Empty(second.Applied);
        Assert.Equal(10L, await JournalRows(legacy));
        Assert.Equal(checksums, await InCompany<string>(legacy.PhysicalDatabaseName, $"SELECT string_agg(checksum, ',' ORDER BY version) FROM {SystemSchema.MigrationJournalTable}"));
    }

    [PostgresFact]
    public async Task Only_ready_companies_are_migrated_and_the_others_are_not_touched()
    {
        await StartCompanies();
        var pending = await Registry.RegisterPendingAsync();
        var ready = await ReadyCompany(OnlyFirst);
        var outcomes = await new CompanyMigrator(Options, Registry).MigrateAsync();
        Assert.Equal(CompanyMigrationStatus.NotReady, outcomes.Single(o => o.CompanyId == pending.Id).Status);
        Assert.Equal(CompanyMigrationStatus.Migrated, outcomes.Single(o => o.CompanyId == ready.Id).Status);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", pending.PhysicalDatabaseName)));
        Assert.Equal(ProvisioningState.Pending, (await Registry.GetAsync(pending.Id))!.State);
    }

    [PostgresFact]
    public async Task Selecting_one_company_leaves_the_others_untouched_and_an_unknown_one_is_an_error()
    {
        await StartCompanies();
        var a = await ReadyCompany(OnlyFirst);
        var b = await ReadyCompany(OnlyFirst);
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(a.Id));
        Assert.Equal(a.Id, outcome.CompanyId);
        Assert.True(await HasAuditTable(a));
        Assert.False(await HasAuditTable(b));
        await Assert.ThrowsAsync<CompanyNotFoundException>(() => new CompanyMigrator(Options, Registry).MigrateAsync(Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task A_busy_company_is_reported_not_migrated_and_the_lock_prevents_concurrent_work()
    {
        await StartCompanies();
        var legacy = await ReadyCompany(OnlyFirst);
        await using (var holder = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await holder.OpenAsync();
            Assert.True(await CompanyProvisioner.TryLockAsync(holder, legacy.Id, default));       // e.g. provisioning or another migration

            var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());

            Assert.Equal(CompanyMigrationStatus.Busy, outcome.Status);
            Assert.False(await HasAuditTable(legacy));
        }
        Assert.Equal(CompanyMigrationStatus.Migrated, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
    }

    [PostgresFact]
    public async Task Concurrent_migrations_apply_each_migration_exactly_once()
    {
        await StartCompanies();
        var legacy = await ReadyCompany(OnlyFirst);
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new CompanyMigrator(Options, Registry).MigrateAsync(legacy.Id)));
        var statuses = runs.Select(r => Assert.Single(r).Status).ToList();
        Assert.All(statuses, s => Assert.Contains(s, new[] { CompanyMigrationStatus.Migrated, CompanyMigrationStatus.UpToDate, CompanyMigrationStatus.Busy }));
        Assert.Equal(1, statuses.Count(s => s == CompanyMigrationStatus.Migrated));
        Assert.Equal(10L, await JournalRows(legacy));
        Assert.True(await HasAuditTable(legacy));
    }

    [PostgresFact]
    public async Task A_failure_in_one_company_is_reported_exactly_and_does_not_stop_or_undo_the_others()
    {
        await StartCompanies();
        var good = await ReadyCompany();
        var bad = await ReadyCompany();
        // the extra (0005) migration fails only in the company database named below; elsewhere it succeeds.
        var script = Extra($"DO $$ BEGIN IF current_database() = '{bad.PhysicalDatabaseName}' THEN RAISE EXCEPTION 'boom'; END IF; END $$; CREATE TABLE zz_added (a int);");
        IReadOnlyList<MigrationScript> scripts = [.. MigrationScript.LoadEmbeddedCompany(), script];

        var outcomes = await new CompanyMigrator(Options, Registry, scripts).MigrateAsync();

        var g = outcomes.Single(o => o.CompanyId == good.Id);
        var b = outcomes.Single(o => o.CompanyId == bad.Id);
        Assert.Equal(CompanyMigrationStatus.Migrated, g.Status);
        Assert.Equal([11], g.Applied);
        Assert.Equal(CompanyMigrationStatus.Failed, b.Status);
        Assert.Equal("pg_P0001", b.ErrorCode);                  // a classification code only, never a message
        Assert.Empty(b.Applied);
        Assert.Equal(11L, await JournalRows(good));
        Assert.Equal(10L, await JournalRows(bad));
        Assert.Equal(0L, await InCompany<long>(bad.PhysicalDatabaseName, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'zz_added'"));
        Assert.Equal(ProvisioningState.Ready, (await Registry.GetAsync(bad.Id))!.State);       // never marked Failed or dropped
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", bad.PhysicalDatabaseName)));
    }

    [PostgresFact]
    public async Task A_foreign_database_or_a_newer_or_edited_journal_is_refused_and_left_unchanged()
    {
        await StartCompanies();
        var foreign = await ReadyCompany(OnlyFirst);
        await InCompany(foreign.PhysicalDatabaseName,
            $"ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; UPDATE {I.Table} SET {I.CompanyId} = gen_random_uuid(); ALTER TABLE {I.Table} ENABLE TRIGGER {I.GuardTrigger}");
        var newer = await ReadyCompany(OnlyFirst);
        await InCompany(newer.PhysicalDatabaseName, $"INSERT INTO {SystemSchema.MigrationJournalTable} VALUES (99, '0099_unknown.sql', repeat('a', 64), now())");
        var edited = await ReadyCompany(OnlyFirst);
        await InCompany(edited.PhysicalDatabaseName, $"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = repeat('0', 64) WHERE version = 1");

        var outcomes = await new CompanyMigrator(Options, Registry).MigrateAsync();

        Assert.Equal("consistency", outcomes.Single(o => o.CompanyId == foreign.Id).ErrorCode);
        Assert.Equal("migration", outcomes.Single(o => o.CompanyId == newer.Id).ErrorCode);
        Assert.Equal("migration", outcomes.Single(o => o.CompanyId == edited.Id).ErrorCode);
        Assert.All(outcomes, o => Assert.Equal(CompanyMigrationStatus.Failed, o.Status));
        foreach (var c in new[] { foreign, newer, edited }) Assert.False(await HasAuditTable(c));
    }

    [PostgresFact]
    public async Task Migration_refuses_to_touch_a_protected_database_name()
    {
        await StartCompanies();
        var id = Guid.NewGuid();
        await Exec($"INSERT INTO {Co.Table} ({Co.Id},{Co.PhysicalDatabaseName},{Co.ProvisioningState},{Co.CreatedAtUtc},{Co.StateChangedAtUtc}) VALUES (@i,@n,2,now(),now())",
            ("i", id), ("n", Options.MaintenanceDatabase));
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(id));
        Assert.Equal(CompanyMigrationStatus.Failed, outcome.Status);
        Assert.Equal("consistency", outcome.ErrorCode);
        // Nothing was created in the protected (maintenance) database.
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var check = new NpgsqlCommand($"SELECT to_regclass('{SystemSchema.MigrationJournalTable}') IS NOT NULL", conn);
        Assert.False((bool)(await check.ExecuteScalarAsync())!);
    }
}

/// <summary>Guards of <c>grant-company-runtime</c> that need no role-creation privilege (so they run on every disposable server).</summary>
public class CompanyRuntimeGrantGuardTests : CompanyRuntimeTestBase
{
    private static IReadOnlyList<MigrationScript> OnlyFirst => [.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version == 1)];

    private CompanyRuntimeProvisioner Provisioner(HasmDatabaseOptions? options = null) => new(options ?? Configured(), Registry);

    [PostgresFact]
    public async Task Without_a_role_creation_privilege_it_fails_with_the_documented_prerequisite_and_leaves_nothing_behind()
    {
        await StartCompanies();
        var c = await ReadyCompany();

        var ex = await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => Provisioner().GrantAsync(c.Id));

        Assert.Equal(CompanyRuntimeProvisioningException.RolePrivilegeRequired, ex.Code);
        Assert.Contains("CREATEROLE", ex.Message);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_roles WHERE rolname = @r", ("r", RoleOf(c))));
        Assert.True(!Directory.Exists(CredentialDirectory) || Directory.GetFileSystemEntries(CredentialDirectory).Length == 0);   // no credential, no temp file
        // Nothing was granted either (the owner still reaches PUBLIC defaults only through ownership).
        AssertNoLeak([ex.Message], c);
    }

    [PostgresFact]
    public async Task It_requires_a_ready_fully_migrated_registered_company_and_a_credential_directory()
    {
        await StartCompanies();
        var pending = await Registry.RegisterPendingAsync();
        Assert.Equal(CompanyRuntimeProvisioningException.NotReady,
            (await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => Provisioner().GrantAsync(pending.Id))).Code);

        var legacy = await ReadyCompany(OnlyFirst);
        Assert.Equal(CompanyRuntimeProvisioningException.SchemaUnmigrated,
            (await Assert.ThrowsAsync<CompanyRuntimeProvisioningException>(() => Provisioner().GrantAsync(legacy.Id))).Code);

        await Assert.ThrowsAsync<CompanyNotFoundException>(() => Provisioner().GrantAsync(Guid.NewGuid()));
        var ready = await ReadyCompany();
        await Assert.ThrowsAsync<DatabaseConfigurationException>(() => Provisioner(Configured(credentialDirectory: false)).GrantAsync(ready.Id));
    }

    [PostgresFact]
    public async Task A_company_being_worked_on_elsewhere_is_reported_and_not_changed()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await using var holder = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await holder.OpenAsync();
        Assert.True(await CompanyProvisioner.TryLockAsync(holder, c.Id, default));
        var result = await Provisioner().GrantAsync(c.Id);
        Assert.Equal(RuntimeGrantOutcome.InProgressElsewhere, result.Outcome);
        Assert.False(result.RoleCreated);
    }
}
