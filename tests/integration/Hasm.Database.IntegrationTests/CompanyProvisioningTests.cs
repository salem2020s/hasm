using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Company database provisioning against a real PostgreSQL server. Every database created here is named
/// "hasm_test_" + random hex (injected name generator) and only those names are ever dropped.
/// </summary>
public class CompanyProvisioningTests : PostgresTestBase
{
    private CompanyRegistry registry = null!;

    private async Task<CompanyRegistry> Ready()
    {
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        return registry = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), NewTestName);
    }

    private CompanyProvisioner Provisioner(IReadOnlyList<MigrationScript>? scripts = null,
        Func<ProvisioningStep, CancellationToken, Task>? hook = null) =>
        new(Options, registry, scripts) { AfterStep = hook };

    private async Task<CompanyRecord> Register()
    {
        var c = await registry.RegisterPendingAsync();
        // Safety: never provision anything that is not a disposable test database.
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);
        return c;
    }

    private static MigrationScript Extra(string sql) => MigrationScript.Parse("0013_extra.sql", sql);
    private static IReadOnlyList<MigrationScript> WithExtra(string sql) => [.. MigrationScript.LoadEmbeddedCompany(), Extra(sql)];

    private async Task<bool> DatabaseExists(string name)
    {
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname=@n", conn);
        cmd.Parameters.AddWithValue("n", name);
        return (long)(await cmd.ExecuteScalarAsync())! == 1;
    }

    private async Task<T> InCompany<T>(string name, string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(name));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InCompany(string name, string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(name));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- first provisioning -------------------------------------------------------------

    [PostgresFact]
    public async Task First_provisioning_creates_a_separate_migrated_database_and_marks_ready()
    {
        await Ready();
        var c = await Register();

        var result = await Provisioner().ProvisionAsync(c.Id);

        Assert.Equal(ProvisioningOutcome.Provisioned, result.Outcome);
        Assert.True(result.DatabaseCreated);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], result.AppliedMigrations);
        var after = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Ready, after.State);
        Assert.Equal(1, after.ProvisioningAttempts);
        Assert.Null(after.LastErrorCode);
        Assert.True(await DatabaseExists(c.PhysicalDatabaseName));
        Assert.Equal(c.Id, await InCompany<Guid>(c.PhysicalDatabaseName, $"SELECT {I.CompanyId} FROM {I.Table}"));
        Assert.Equal(12L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    // ---- repeated provisioning -----------------------------------------------------------

    [PostgresFact]
    public async Task Repeated_provisioning_is_a_verified_noop()
    {
        await Ready();
        var c = await Register();
        await Provisioner().ProvisionAsync(c.Id);

        var second = await Provisioner().ProvisionAsync(c.Id);

        Assert.Equal(ProvisioningOutcome.AlreadyReady, second.Outcome);
        Assert.False(second.DatabaseCreated);
        Assert.Empty(second.AppliedMigrations);
        var after = (await registry.GetAsync(c.Id))!;
        Assert.Equal(1, after.ProvisioningAttempts); // no new attempt counted
        Assert.Equal(1L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {I.Table}"));
    }

    [PostgresFact]
    public async Task Unknown_company_is_rejected_and_creates_nothing()
    {
        await Ready();
        await Assert.ThrowsAsync<CompanyNotFoundException>(() => Provisioner().ProvisionAsync(Guid.NewGuid()));
    }

    // ---- concurrency ---------------------------------------------------------------------

    [PostgresFact]
    public async Task Concurrent_requests_provision_exactly_once()
    {
        await Ready();
        var c = await Register();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Provisioner().ProvisionAsync(c.Id)));

        Assert.Equal(1, results.Count(r => r.Outcome == ProvisioningOutcome.Provisioned));
        Assert.All(results.Where(r => r.Outcome != ProvisioningOutcome.Provisioned),
            r => Assert.True(r.Outcome is ProvisioningOutcome.InProgressElsewhere or ProvisioningOutcome.AlreadyReady));
        var after = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Ready, after.State);
        Assert.Equal(1, after.ProvisioningAttempts);
        Assert.Equal(1L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {I.Table}"));
    }

    [PostgresFact]
    public async Task A_second_provisioner_backs_off_while_the_first_is_mid_flight()
    {
        await Ready();
        var c = await Register();
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = Provisioner(hook: async (step, _) =>
        {
            if (step != ProvisioningStep.DatabaseEnsured) return;
            reached.SetResult();
            await release.Task;
        }).ProvisionAsync(c.Id);
        await reached.Task;

        var second = await Provisioner().ProvisionAsync(c.Id);
        Assert.Equal(ProvisioningOutcome.InProgressElsewhere, second.Outcome);
        Assert.Equal(ProvisioningState.Provisioning, (await registry.GetAsync(c.Id))!.State);

        release.SetResult();
        Assert.Equal(ProvisioningOutcome.Provisioned, (await first).Outcome);
        Assert.Equal(ProvisioningState.Ready, (await registry.GetAsync(c.Id))!.State);
    }

    [PostgresFact]
    public async Task Different_companies_provision_concurrently_without_interference()
    {
        await Ready();
        var companies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Register()));

        var results = await Task.WhenAll(companies.Select(c => Provisioner().ProvisionAsync(c.Id)));

        Assert.All(results, r => Assert.Equal(ProvisioningOutcome.Provisioned, r.Outcome));
        Assert.Equal(3, companies.Select(c => c.PhysicalDatabaseName).Distinct().Count());
    }

    // ---- migration failure and retry -----------------------------------------------------

    [PostgresFact]
    public async Task Migration_failure_marks_failed_keeps_the_database_and_retry_succeeds()
    {
        await Ready();
        var c = await Register();

        await Assert.ThrowsAsync<PostgresException>(() =>
            Provisioner(WithExtra("CREATE TABLE zz_partial (a int); SELECT 1/0;")).ProvisionAsync(c.Id));

        var failed = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Failed, failed.State);
        Assert.Equal("pg_22012", failed.LastErrorCode);       // sanitized code only, no message
        Assert.True(await DatabaseExists(c.PhysicalDatabaseName)); // never auto-deleted
        Assert.Equal(0L, await InCompany<long>(c.PhysicalDatabaseName, "SELECT count(*) FROM information_schema.tables WHERE table_name='zz_partial'"));

        var retry = await Provisioner(WithExtra("CREATE TABLE zz_marker (a int);")).ProvisionAsync(c.Id);

        Assert.Equal(ProvisioningOutcome.Provisioned, retry.Outcome);
        Assert.False(retry.DatabaseCreated);
        Assert.Equal([12], retry.AppliedMigrations); // migrations 1-11 already applied before the failure
        var ready = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Ready, ready.State);
        Assert.Equal(2, ready.ProvisioningAttempts);
        Assert.Null(ready.LastErrorCode);
    }

    [PostgresFact]
    public async Task Retry_after_a_late_failure_preserves_existing_company_data()
    {
        await Ready();
        var c = await Register();
        var scripts = WithExtra("CREATE TABLE zz_marker (a int);");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provisioner(scripts, (step, _) =>
            step == ProvisioningStep.IdentityBound ? throw new InvalidOperationException("boom") : Task.CompletedTask).ProvisionAsync(c.Id));
        Assert.Equal(ProvisioningState.Failed, (await registry.GetAsync(c.Id))!.State);
        await InCompany(c.PhysicalDatabaseName, "INSERT INTO zz_marker VALUES (42)");

        await Provisioner(scripts).ProvisionAsync(c.Id);

        Assert.Equal(1L, await InCompany<long>(c.PhysicalDatabaseName, "SELECT count(*) FROM zz_marker WHERE a=42"));
        Assert.Equal(1L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {I.Table}"));
        Assert.Equal(ProvisioningState.Ready, (await registry.GetAsync(c.Id))!.State);
    }

    // ---- interrupted provisioning --------------------------------------------------------

    [PostgresTheory]
    [InlineData(ProvisioningStep.DatabaseEnsured)]
    [InlineData(ProvisioningStep.Migrated)]
    [InlineData(ProvisioningStep.IdentityBound)]
    [InlineData(ProvisioningStep.AccessRestricted)]
    public async Task Interruption_at_any_step_leaves_provisioning_and_the_next_run_resumes(ProvisioningStep interruptedAfter)
    {
        await Ready();
        var c = await Register();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provisioner(hook: (step, _) =>
            step == interruptedAfter ? throw new OperationCanceledException() : Task.CompletedTask).ProvisionAsync(c.Id));

        var interrupted = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Provisioning, interrupted.State); // not Failed, not Ready
        Assert.True(await DatabaseExists(c.PhysicalDatabaseName));       // work kept

        var resumed = await Provisioner().ProvisionAsync(c.Id);

        Assert.Equal(ProvisioningOutcome.Provisioned, resumed.Outcome);
        Assert.False(resumed.DatabaseCreated);
        var ready = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Ready, ready.State);
        Assert.Equal(2, ready.ProvisioningAttempts);
        Assert.Equal(c.Id, await InCompany<Guid>(c.PhysicalDatabaseName, $"SELECT {I.CompanyId} FROM {I.Table}"));
        Assert.Equal(12L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    // ---- consistency and safety ----------------------------------------------------------

    [PostgresFact]
    public async Task A_foreign_database_with_the_registered_name_is_refused_and_left_untouched()
    {
        await Ready();
        var c = await Register();
        await using (var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString()))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(c.PhysicalDatabaseName)}", conn);
            await create.ExecuteNonQueryAsync();
        }
        await InCompany(c.PhysicalDatabaseName, "CREATE TABLE foreign_data (a int); INSERT INTO foreign_data VALUES (7)");

        await Assert.ThrowsAsync<CompanyConsistencyException>(() => Provisioner().ProvisionAsync(c.Id));

        var after = (await registry.GetAsync(c.Id))!;
        Assert.Equal(ProvisioningState.Failed, after.State);
        Assert.Equal("consistency", after.LastErrorCode);
        Assert.Equal(1L, await InCompany<long>(c.PhysicalDatabaseName, "SELECT count(*) FROM foreign_data WHERE a=7"));
        Assert.True(await InCompany<bool>(c.PhysicalDatabaseName, $"SELECT to_regclass('{I.Table}') IS NULL"));
    }

    [PostgresFact]
    public async Task Mapping_verification_detects_wrong_identity_and_missing_database()
    {
        await Ready();
        var a = await Register();
        await Provisioner().ProvisionAsync(a.Id);
        var provisioner = Provisioner();

        var wrongId = new CompanyRecord(Guid.NewGuid(), a.PhysicalDatabaseName, ProvisioningState.Ready, a.CreatedAtUtc, a.CreatedAtUtc);
        await Assert.ThrowsAsync<CompanyConsistencyException>(() => provisioner.VerifyMappingAsync(wrongId));

        var missing = new CompanyRecord(Guid.NewGuid(), PostgresTestEnvironment.NewTestDatabaseName(), ProvisioningState.Ready, a.CreatedAtUtc, a.CreatedAtUtc);
        await Assert.ThrowsAsync<CompanyConsistencyException>(() => provisioner.VerifyMappingAsync(missing));
        Assert.False(await DatabaseExists(missing.PhysicalDatabaseName)); // verification never creates anything
    }

    [PostgresFact]
    public async Task Protected_databases_can_never_be_a_provisioning_target()
    {
        await Ready();
        // A registry entry that (wrongly) names the system database must be refused before anything is touched.
        var rogue = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), () => DatabaseName);
        var c = await rogue.RegisterPendingAsync();

        await Assert.ThrowsAsync<CompanyConsistencyException>(() => Provisioner().ProvisionAsync(c.Id));

        Assert.Equal(ProvisioningState.Pending, (await registry.GetAsync(c.Id))!.State);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.Company.Table}")); // system database intact
    }

    // ---- isolation -----------------------------------------------------------------------

    [PostgresFact]
    public async Task Company_databases_are_isolated_from_each_other_and_from_the_system_database()
    {
        await Ready();
        var a = await Register();
        var b = await Register();
        await Provisioner().ProvisionAsync(a.Id);
        await Provisioner().ProvisionAsync(b.Id);
        Assert.NotEqual(a.PhysicalDatabaseName, b.PhysicalDatabaseName);

        // Each database holds exactly its own identity and nothing of the other company or the registry.
        Assert.Equal(a.Id, await InCompany<Guid>(a.PhysicalDatabaseName, $"SELECT {I.CompanyId} FROM {I.Table}"));
        Assert.Equal(b.Id, await InCompany<Guid>(b.PhysicalDatabaseName, $"SELECT {I.CompanyId} FROM {I.Table}"));
        Assert.Equal(1L, await InCompany<long>(a.PhysicalDatabaseName, $"SELECT count(*) FROM {I.Table}"));
        Assert.True(await InCompany<bool>(a.PhysicalDatabaseName, $"SELECT to_regclass('{SystemSchema.Company.Table}') IS NULL"));
        Assert.True(await Scalar<bool>($"SELECT to_regclass('{I.Table}') IS NULL"));

        // Data written to one company is invisible in the other.
        await InCompany(a.PhysicalDatabaseName, "CREATE TABLE zz_private (a int); INSERT INTO zz_private VALUES (1)");
        Assert.True(await InCompany<bool>(b.PhysicalDatabaseName, "SELECT to_regclass('zz_private') IS NULL"));

        // The identity cannot be rewritten or removed, even by the owner.
        await Assert.ThrowsAsync<PostgresException>(() => InCompany(a.PhysicalDatabaseName, $"UPDATE {I.Table} SET {I.CompanyId} = gen_random_uuid()"));
        await Assert.ThrowsAsync<PostgresException>(() => InCompany(a.PhysicalDatabaseName, $"DELETE FROM {I.Table}"));
        await Assert.ThrowsAsync<PostgresException>(() => InCompany(a.PhysicalDatabaseName, $"INSERT INTO {I.Table} ({I.Singleton}, {I.CompanyId}, {I.CreatedAtUtc}) VALUES (false, gen_random_uuid(), now())"));
    }

    [PostgresFact]
    public async Task Company_databases_do_not_grant_connect_to_public()
    {
        await Ready();
        var c = await Register();
        await Provisioner().ProvisionAsync(c.Id);

        // grantee 0 = PUBLIC
        var publicGrants = await Scalar<long>(
            "SELECT count(*) FROM pg_database d, aclexplode(d.datacl) a WHERE d.datname=@n AND a.grantee=0", ("n", c.PhysicalDatabaseName));
        Assert.Equal(0L, publicGrants);
    }
}
