using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Database.IntegrationTests;

public class SystemDatabaseTests : PostgresTestBase
{
    private Task<InitializationResult> Init() => new SystemDatabaseInitializer(Options).InitializeAsync();

    [PostgresFact]
    public async Task First_initialization_creates_database_and_applies_migrations()
    {
        var result = await Init();
        Assert.True(result.DatabaseCreated);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31], result.AppliedMigrations);
        Assert.Equal(31L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    [PostgresFact]
    public async Task Repeated_initialization_is_a_safe_noop()
    {
        await Init();
        await Exec($"INSERT INTO {S.Table} ({S.Id},{S.PhysicalDatabaseName},{S.ProvisioningState},{S.CreatedAtUtc},{S.StateChangedAtUtc}) VALUES (@i,'dkeep',0,now(),now())", ("i", Guid.NewGuid()));

        var second = await Init();

        Assert.False(second.DatabaseCreated);
        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(31, second.AlreadyApplied);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {S.Table}")); // data preserved
        Assert.Equal(31L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    [PostgresFact]
    public async Task Concurrent_initializations_apply_each_migration_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Init()));
        Assert.Equal(31, results.Sum(r => r.AppliedMigrations.Count)); // each migration applied exactly once overall
        Assert.Equal(31L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    [PostgresFact]
    public async Task Physical_schema_matches_the_logical_map()
    {
        await Init();
        var columns = new[] { S.Id, S.PhysicalDatabaseName, S.ProvisioningState, S.CreatedAtUtc, S.StateChangedAtUtc, S.ProvisioningAttempts, S.LastErrorCode, S.DisplayNameAr, S.DisplayNameEn, S.DisplayNameRevision };
        foreach (var c in columns)
            Assert.Equal(1L, await Scalar<long>(
                "SELECT count(*) FROM information_schema.columns WHERE table_name=@t AND column_name=@c", ("t", S.Table), ("c", c)));
        Assert.Equal(10L, await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t", ("t", S.Table)));
        foreach (var k in new[] { S.PrimaryKey, S.UniqueDatabaseName, S.CheckState, S.CheckDatabaseName, S.CheckDisplayName })
            Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_constraint WHERE conname=@n", ("n", k)));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE tgname=@n", ("n", S.GuardTrigger)));
    }

    [PostgresFact]
    public async Task Modified_applied_migration_is_detected()
    {
        await Init();
        await Exec($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum='tampered' WHERE version=1");
        var ex = await Assert.ThrowsAsync<MigrationException>(Init);
        Assert.Contains("modified", ex.Message);
    }

    [PostgresFact]
    public async Task Database_newer_than_application_is_refused()
    {
        await Init();
        await Exec($"INSERT INTO {SystemSchema.MigrationJournalTable} VALUES (99,'0099_future.sql','x',now())");
        await Assert.ThrowsAsync<MigrationException>(Init);
    }

    [PostgresFact]
    public async Task Failed_migration_rolls_back_completely()
    {
        await Init();
        var bad = MigrationScript.Parse("0032_bad.sql", "CREATE TABLE zz_partial (a int); SELECT 1/0;");
        var scripts = MigrationScript.LoadEmbedded().Append(bad).ToList();
        var runner = new MigrationRunner(Options.BuildSystemConnectionString(admin: true));

        await Assert.ThrowsAsync<PostgresException>(() => runner.ApplyAsync(scripts));

        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM information_schema.tables WHERE table_name='zz_partial'"));
        Assert.Equal(31L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
    }

    [PostgresFact]
    public async Task Database_constraints_protect_registry_integrity()
    {
        await Init();
        var registry = new CompanyRegistry(Options.BuildSystemConnectionString());
        var c = await registry.RegisterPendingAsync();

        string Insert(string name, int state) =>
            $"INSERT INTO {S.Table} VALUES (gen_random_uuid(), '{name}', {state}, now(), now())";

        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("Bad Name", 0)));     // format check
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("pg_x", 0)));         // reserved prefix
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert("dgood", 9)));        // state check
        await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert(c.PhysicalDatabaseName, 0))); // unique
        await Assert.ThrowsAsync<PostgresException>(() =>                                    // immutable name
            Exec($"UPDATE {S.Table} SET {S.PhysicalDatabaseName}='dother' WHERE {S.Id}=@i", ("i", c.Id)));
        await Assert.ThrowsAsync<PostgresException>(() =>                                    // no deletes
            Exec($"DELETE FROM {S.Table} WHERE {S.Id}=@i", ("i", c.Id)));
    }

    [PostgresFact]
    public async Task Registry_registers_reads_and_transitions_companies()
    {
        await Init();
        var registry = new CompanyRegistry(Options.BuildSystemConnectionString());

        var a = await registry.RegisterPendingAsync();
        var b = await registry.RegisterPendingAsync();
        Assert.NotEqual(a.PhysicalDatabaseName, b.PhysicalDatabaseName);

        var loaded = await registry.GetAsync(a.Id);
        Assert.Equal(a.PhysicalDatabaseName, loaded!.PhysicalDatabaseName);
        Assert.Equal(ProvisioningState.Pending, loaded.State);
        Assert.Null(await registry.GetAsync(Guid.NewGuid()));
        Assert.Equal(2, (await registry.ListAsync()).Count);

        Assert.True(await registry.TransitionAsync(a.Id, ProvisioningState.Provisioning));
        Assert.Equal(ProvisioningState.Provisioning, (await registry.GetAsync(a.Id))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TransitionAsync(a.Id, ProvisioningState.Pending));
        Assert.False(await registry.TransitionAsync(Guid.NewGuid(), ProvisioningState.Provisioning));
    }

    [PostgresFact]
    public async Task Initialization_does_not_create_any_company_databases()
    {
        await Init();
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {S.Table}"));
    }
}
