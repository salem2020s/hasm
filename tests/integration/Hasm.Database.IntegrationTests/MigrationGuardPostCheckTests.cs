using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;
using RP = Hasm.Infrastructure.SystemDatabase.SystemSchema.RolePermission;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Migration guard hardening against a real PostgreSQL (owner decision 2026-11-21, Q5): after every migration, and at the end of every
/// run, no trigger may be left switched off. The static rules of MigrationGuard are covered by the unit suite; here the runner itself is
/// proven, with a migration that the static rules cannot see through (it calls a function that was planted outside the migrations).
/// </summary>
public class MigrationGuardPostCheckTests : PostgresTestBase
{
    private static readonly int Next = MigrationScript.LoadEmbedded().Max(s => s.Version) + 1;

    private Task<InitializationResult> Init() => new SystemDatabaseInitializer(Options).InitializeAsync();
    private MigrationRunner Runner() => new(Options.BuildSystemConnectionString(admin: true));
    private static IReadOnlyList<MigrationScript> WithExtra(string sql) => [.. MigrationScript.LoadEmbedded(), MigrationScript.Parse($"{Next:0000}_extra.sql", sql)];

    private Task<string> State(string table, string trigger) =>
        Scalar<string>($"SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = '{table}'::regclass AND t.tgname = '{trigger}'");
    private Task<long> Journal() => Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}");
    private Task<long> Tables(string name) => Scalar<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{name}'");

    [PostgresFact]
    public async Task A_paired_disable_and_enable_inside_one_migration_is_applied_and_the_trigger_fires_again()
    {
        await Init();
        var result = await Runner().ApplyAsync(WithExtra(
            $"CREATE TABLE zz_marker (a int);\nALTER TABLE {RP.Table} DISABLE TRIGGER {RP.GuardTrigger};\nINSERT INTO zz_marker VALUES (1);\nALTER TABLE {RP.Table} ENABLE TRIGGER {RP.GuardTrigger};"));

        Assert.Equal([Next], result.Applied);
        Assert.Equal("O", await State(RP.Table, RP.GuardTrigger));
        Assert.Equal(1L, await Tables("zz_marker"));
        Assert.Equal((long)Next, await Journal());
    }

    [PostgresTheory]
    [InlineData("DISABLE", "D")]
    [InlineData("ENABLE REPLICA", "R")]         // fires only in replica mode: switched off for every normal session
    public async Task A_migration_that_leaves_a_trigger_switched_off_is_refused_and_rolled_back_completely(string action, string state)
    {
        await Init();
        // Planted outside the migrations (direct SQL), so the migration text below contains nothing the static rules could refuse.
        await Exec($"CREATE FUNCTION zz_switch() RETURNS void LANGUAGE plpgsql AS $$ BEGIN ALTER TABLE {RP.Table} {action} TRIGGER {RP.GuardTrigger}; END; $$");
        var scripts = WithExtra("CREATE TABLE zz_partial (a int);\nSELECT zz_switch();");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Runner().ApplyAsync(scripts));

        Assert.Contains($"{Next:0000}_extra.sql", ex.Message);
        Assert.Contains($"{RP.Table}.{RP.GuardTrigger} ({state})", ex.Message);
        Assert.Contains("rolled back", ex.Message);
        Assert.Equal("O", await State(RP.Table, RP.GuardTrigger));          // the switch itself was rolled back with the migration
        Assert.Equal(0L, await Tables("zz_partial"));
        Assert.Equal((long)Next - 1, await Journal());

        // Nothing is left behind: the same build without the offending migration still runs as a no-op.
        var again = await Init();
        Assert.Empty(again.AppliedMigrations);
    }

    [PostgresFact]
    public async Task A_trigger_enabled_ALWAYS_fires_and_is_accepted_by_the_post_check()
    {
        await Init();
        await Exec($"ALTER TABLE {RP.Table} ENABLE ALWAYS TRIGGER {RP.GuardTrigger}");
        Assert.Equal("A", await State(RP.Table, RP.GuardTrigger));
        Assert.Empty((await Init()).AppliedMigrations);
    }

    [PostgresFact]
    public async Task A_database_whose_trigger_was_switched_off_out_of_band_is_refused_until_it_is_enabled_again()
    {
        await Init();
        await Exec($"ALTER TABLE {RP.Table} DISABLE TRIGGER {RP.GuardTrigger}");

        // Nothing to apply: the run still refuses the database.
        var noop = await Assert.ThrowsAsync<MigrationException>(Init);
        Assert.Contains($"{RP.Table}.{RP.GuardTrigger} (D)", noop.Message);

        // A new, harmless migration is not applied on top of a switched-off safeguard either.
        var ex = await Assert.ThrowsAsync<MigrationException>(() => Runner().ApplyAsync(WithExtra("CREATE TABLE zz_marker (a int);")));
        Assert.Contains($"{RP.Table}.{RP.GuardTrigger} (D)", ex.Message);
        Assert.Equal(0L, await Tables("zz_marker"));
        Assert.Equal((long)Next - 1, await Journal());

        await Exec($"ALTER TABLE {RP.Table} ENABLE TRIGGER {RP.GuardTrigger}");
        var result = await Runner().ApplyAsync(WithExtra("CREATE TABLE zz_marker (a int);"));
        Assert.Equal([Next], result.Applied);
        Assert.Equal(1L, await Tables("zz_marker"));
    }

    [PostgresFact]
    public async Task The_runner_refuses_a_statically_unsafe_migration_before_it_touches_the_database()
    {
        await Init();
        // Built without MigrationScript.Parse: the runner's own validation must still refuse it, and nothing may be executed.
        var unsafeScript = new MigrationScript(Next, $"{Next:0000}_unsafe.sql", $"CREATE TABLE zz_partial (a int);\nALTER TABLE {RP.Table} DISABLE TRIGGER {RP.GuardTrigger};");
        await Assert.ThrowsAsync<MigrationException>(() => Runner().ApplyAsync([.. MigrationScript.LoadEmbedded(), unsafeScript]));

        Assert.Equal(0L, await Tables("zz_partial"));
        Assert.Equal("O", await State(RP.Table, RP.GuardTrigger));
        Assert.Equal((long)Next - 1, await Journal());
    }

    [PostgresFact]
    public async Task A_fresh_system_database_and_a_fresh_company_database_have_no_trigger_switched_off()
    {
        await Init();
        const string off = "SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgenabled <> 'O'";
        Assert.Equal(0L, await Scalar<long>(off));
        Assert.True(await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal") > 0);

        var company = await ProvisionCompany();
        Assert.Equal(0L, await InCompany<long>(company, off));
        Assert.True(await InCompany<long>(company, "SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal") > 0);
    }

    [PostgresFact]
    public async Task The_same_check_protects_a_company_database()
    {
        await Init();
        var company = await ProvisionCompany();
        var runner = new MigrationRunner(Options.BuildCompanyAdminConnectionString(company));
        var all = MigrationScript.LoadEmbeddedCompany();
        var extra = MigrationScript.Parse($"{all.Max(s => s.Version) + 1:0000}_extra.sql", "CREATE TABLE zz_partial (a int);\nSELECT zz_switch();");

        // A migration that leaves the identity guard switched off is rolled back.
        await InCompany<object?>(company, $"CREATE FUNCTION zz_switch() RETURNS void LANGUAGE plpgsql AS $$ BEGIN ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; END; $$; SELECT NULL");
        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync([.. all, extra]));
        Assert.Contains($"{I.Table}.{I.GuardTrigger} (D)", ex.Message);
        Assert.Equal(0L, await InCompany<long>(company, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'zz_partial'"));
        Assert.Equal("O", await InCompany<string>(company, $"SELECT tgenabled::text FROM pg_trigger WHERE tgrelid = '{I.Table}'::regclass AND tgname = '{I.GuardTrigger}'"));
        Assert.Equal((long)all.Count, await InCompany<long>(company, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));

        // A company database tampered with out of band is refused by a no-op run, and accepted again once the trigger is enabled.
        await InCompany<object?>(company, $"ALTER TABLE {I.Table} DISABLE TRIGGER {I.GuardTrigger}; SELECT NULL");
        await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync(all));
        await InCompany<object?>(company, $"ALTER TABLE {I.Table} ENABLE TRIGGER {I.GuardTrigger}; SELECT NULL");
        Assert.Empty((await runner.ApplyAsync(all)).Applied);
    }

    private async Task<string> ProvisionCompany()
    {
        var registry = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), NewTestName);
        var c = await registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);      // never anything but a disposable test database
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        return c.PhysicalDatabaseName;
    }

    private async Task<T> InCompany<T>(string name, string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(name));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }
}
