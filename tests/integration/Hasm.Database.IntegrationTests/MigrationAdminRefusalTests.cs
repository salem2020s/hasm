using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;
using RP = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Owner decision 2026-11-22 (Q2), against a real PostgreSQL: the administrative migration commands (init-system-db, provision-company, migrate-companies) refuse a database with a
/// switched-off non-internal trigger even when no migration is pending; the diagnostic names every affected trigger (table.trigger and its state); nothing is ever re-enabled by the
/// command; the company state stays Ready; and the running application (which never runs the runner) is unaffected by a rejected command.
/// </summary>
public class MigrationAdminRefusalTests : CompanyRuntimeTestBase
{
    private Task<InitializationResult> Init() => new SystemDatabaseInitializer(Options).InitializeAsync();

    private async Task<T> InCompany<T>(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private Task<string> SystemState(string table, string trigger) =>
        Scalar<string>($"SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = '{table}'::regclass AND t.tgname = '{trigger}'");
    private Task<string> CompanyState(string database, string table, string trigger) =>
        InCompany<string>(database, $"SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = '{table}'::regclass AND t.tgname = '{trigger}'");
    private Task<long> Journal() => Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}");
    private Task<long> AdvisoryLocks() => Scalar<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");

    [PostgresFact]
    public async Task Init_system_db_with_nothing_pending_names_every_switched_off_trigger_and_enables_none()
    {
        await StartCompanies();
        await Init();
        var before = await Journal();
        await Exec($"ALTER TABLE {RP.Table} DISABLE TRIGGER {RP.GuardTrigger}");
        await Exec($"ALTER TABLE {UT.Table} DISABLE TRIGGER {SystemSchema.TotpGuard.TrOwnerOnly}");

        var ex = await Assert.ThrowsAsync<MigrationException>(Init);
        Assert.Contains($"{RP.Table}.{RP.GuardTrigger} (D)", ex.Message);
        Assert.Contains($"{UT.Table}.{SystemSchema.TotpGuard.TrOwnerOnly} (D)", ex.Message);
        Assert.Contains("were not changed", ex.Message);

        // never re-enabled, no migration recorded, no lock left behind; a second attempt refuses again
        Assert.Equal("D", await SystemState(RP.Table, RP.GuardTrigger));
        Assert.Equal("D", await SystemState(UT.Table, SystemSchema.TotpGuard.TrOwnerOnly));
        Assert.Equal(before, await Journal());
        Assert.Equal(0L, await AdvisoryLocks());
        await Assert.ThrowsAsync<MigrationException>(Init);
        Assert.Equal("D", await SystemState(RP.Table, RP.GuardTrigger));

        // enabling by hand (after an investigation) is the only way back
        await Exec($"ALTER TABLE {RP.Table} ENABLE TRIGGER {RP.GuardTrigger}");
        await Exec($"ALTER TABLE {UT.Table} ENABLE TRIGGER {SystemSchema.TotpGuard.TrOwnerOnly}");
        Assert.Empty((await Init()).AppliedMigrations);
    }

    [PostgresFact]
    public async Task Provision_company_refuses_a_ready_company_with_a_switched_off_trigger_and_changes_nothing()
    {
        await StartCompanies();
        var company = await ReadyCompany();
        var provisioner = new CompanyProvisioner(Options, Registry);
        Assert.Equal(ProvisioningOutcome.AlreadyReady, (await provisioner.ProvisionAsync(company.Id)).Outcome);

        await InCompany<object?>(company.PhysicalDatabaseName, $"ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; SELECT NULL");
        var ex = await Assert.ThrowsAsync<MigrationException>(() => provisioner.ProvisionAsync(company.Id));
        Assert.Contains($"{I.Table}.{I.GuardTrigger} (D)", ex.Message);
        Assert.DoesNotContain(company.PhysicalDatabaseName, ex.Message);
        Assert.Equal("D", await CompanyState(company.PhysicalDatabaseName, I.Table, I.GuardTrigger));
        Assert.Equal(ProvisioningState.Ready, (await Registry.GetAsync(company.Id))!.State);          // the company is not marked Failed by the refusal

        await InCompany<object?>(company.PhysicalDatabaseName, $"ALTER TABLE {I.Table} ENABLE TRIGGER {I.GuardTrigger}; SELECT NULL");
        Assert.Equal(ProvisioningOutcome.AlreadyReady, (await provisioner.ProvisionAsync(company.Id)).Outcome);
    }

    [PostgresFact]
    public async Task Migrate_companies_with_nothing_pending_fails_only_the_tampered_company_and_the_diagnostic_names_the_trigger()
    {
        await StartCompanies();
        var tampered = await ReadyCompany();
        var intact = await ReadyCompany();
        var migrator = new CompanyMigrator(Options, Registry);
        await InCompany<object?>(tampered.PhysicalDatabaseName, $"ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; SELECT NULL");

        var outcomes = await migrator.MigrateAsync();
        var bad = Assert.Single(outcomes, o => o.CompanyId == tampered.Id);
        Assert.Equal(CompanyMigrationStatus.Failed, bad.Status);
        Assert.Equal("migration", bad.ErrorCode);
        Assert.Contains($"{I.Table}.{I.GuardTrigger} (D)", bad.Detail);
        Assert.DoesNotContain(tampered.PhysicalDatabaseName, bad.Detail);
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(outcomes, o => o.CompanyId == intact.Id).Status);   // one failure never stops the others

        // what the tool prints: the status line with the code, never a database name
        Assert.Equal($"Company {tampered.Id}: Failed [migration]", bad.Line());
        Assert.DoesNotContain(tampered.PhysicalDatabaseName, bad.Line());

        Assert.Equal("D", await CompanyState(tampered.PhysicalDatabaseName, I.Table, I.GuardTrigger));       // never re-enabled
        await InCompany<object?>(tampered.PhysicalDatabaseName, $"ALTER TABLE {I.Table} ENABLE TRIGGER {I.GuardTrigger}; SELECT NULL");
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await migrator.MigrateAsync(tampered.Id), o => o.CompanyId == tampered.Id).Status);
    }

    [PostgresFact]
    public async Task A_rejected_administrative_command_leaves_the_running_application_unaffected()
    {
        await StartCompanies();
        var company = await ReadyCompany();
        await InCompany<object?>(company.PhysicalDatabaseName, $"ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; SELECT NULL");

        Assert.Equal(CompanyMigrationStatus.Failed, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(company.Id)).Status);
        await Assert.ThrowsAsync<MigrationException>(() => new CompanyProvisioner(Options, Registry).ProvisionAsync(company.Id));

        // no lock is left behind and nothing changed: the company stays Ready, the journal is complete, the tampered trigger is exactly as it was
        Assert.Equal(0L, await InCompany<long>(company.PhysicalDatabaseName, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"));
        Assert.Equal((long)MigrationScript.LoadEmbeddedCompany().Count, await InCompany<long>(company.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        Assert.Equal(ProvisioningState.Ready, (await Registry.GetAsync(company.Id))!.State);

        // the application path (the resolver the API uses; it never migrates) still opens and reads the company
        await using var conn = await SinglePrincipalResolver(company).OpenAsync(company.Id);
        await using var cmd = new NpgsqlCommand($"SELECT {I.CompanyId} FROM {I.Table}", conn);
        Assert.Equal(company.Id, (Guid)(await cmd.ExecuteScalarAsync())!);
    }
}
