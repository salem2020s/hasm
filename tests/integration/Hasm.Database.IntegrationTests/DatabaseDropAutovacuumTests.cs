using System.Security.Cryptography;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// A kept trial-restore database is dropped although an autovacuum worker is working in it and the administrative account is not a superuser (the cause of a rare failure of the acceptance suite: the forced
/// drop is refused with 42501 because nobody but a superuser may terminate a session that has no role).
/// <para>
/// DURATION, one to two minutes (one to two autovacuum naptimes), on purpose: the worker cannot be started on request. The launcher visits a new database once per <c>autovacuum_naptime</c> (60 seconds by default) and that setting can only
/// be changed by a superuser, which the test account is not and must not be. So the test loads a table large enough that the worker stays for several seconds, waits until the worker is seen in the
/// database, proves that the forced drop alone IS refused, and only then calls the product, which must have taken its fallback (the account's own sessions terminated, then the plain drop). Nothing is
/// retried and nothing is skipped: if no worker appears the test fails. The second test needs no worker and no waiting for one: a session of a role the account does not hold refuses the forced drop
/// at once, every time.
/// </para>
/// <para>
/// ALONE, in the serial collection, for the same reason: the launcher starts one worker per <c>autovacuum_naptime</c> divided by the number of databases and always prefers a database no worker has visited
/// yet. While the other classes run in parallel they create new databases faster than that, so this database can wait far longer than one naptime (observed on the hosted runner: no worker in 4 minutes in
/// the middle of the suite). The serial collection runs after the parallel classes, when no other class is creating databases, and the worker arrives within one to two naptimes. Nothing else runs at
/// the same time as a class of this collection, which is also what makes the fallback counter of the process exact here.
/// </para>
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class DatabaseDropAutovacuumTests : PostgresTestBase
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-dda-" + Guid.NewGuid().ToString("N"));

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    [PostgresFact]
    public async Task A_kept_trial_database_is_dropped_while_an_autovacuum_worker_that_the_non_superuser_account_may_not_terminate_is_working_in_it()
    {
        Directory.CreateDirectory(work);
        await new SystemDatabaseInitializer(Options).InitializeAsync();                          // the registry table DropTrialRestoreAsync locks and checks
        var name = NewTestName();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await using var maint = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await maint.OpenAsync();
        Assert.False((bool)(await new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", maint).ExecuteScalarAsync())!);   // the account is not a superuser
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", maint).ExecuteNonQueryAsync();
        await new NpgsqlCommand($"COMMENT ON DATABASE \"{name}\" IS '{CompanyBackupService.CommentPrefix}{token}'", maint).ExecuteNonQueryAsync();
        await using (var load = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(name)))
        {
            await load.OpenAsync();
            // large on purpose: the worker must still be vacuuming it when the drop is attempted
            await using var big = new NpgsqlCommand("CREATE TABLE zz_load AS SELECT g AS id, md5(g::text) AS a, md5((g * 7)::text) AS b FROM generate_series(1, 6000000) g", load) { CommandTimeout = 300 };
            await big.ExecuteNonQueryAsync();
        }
        // a session of the account stays in the database, as the pooled sessions of a real trial restore do: the plain drop alone would wait five seconds for it and be refused with 55006. Held open
        // explicitly (not left to a connection pool) until the drop: the product must end it itself.
        await using var held = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Options.BuildCompanyAdminConnectionString(name)) { Pooling = false }.ConnectionString);
        await held.OpenAsync();
        var heldPid = held.ProcessID;

        // wait for the session that has no role (a client session always has one; the account sees the row but not its details)
        var seen = false;
        var started = DateTime.UtcNow;
        while (!seen && DateTime.UtcNow - started < TimeSpan.FromMinutes(4))
        {
            await using var q = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @n AND usesysid IS NULL", maint);
            q.Parameters.AddWithValue("n", name);
            seen = (long)(await q.ExecuteScalarAsync())! > 0;
            if (!seen) await Task.Delay(50);
        }
        Assert.True(seen, "no autovacuum worker entered the database within 4 minutes (is autovacuum switched off on this server?)");

        // the precondition, proved and not assumed: the forced drop alone is refused, and the database is still there
        var refused = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", maint).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Equal("permission denied to terminate process", refused.MessageText);

        // the refused forced drop ended no session: the account's own one is still in the database
        await using (var own = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @n AND pid = @p AND usesysid = (SELECT oid FROM pg_roles WHERE rolname = current_user)", maint))
        {
            own.Parameters.AddWithValue("n", name);
            own.Parameters.AddWithValue("p", heldPid);
            Assert.Equal(1L, (long)(await own.ExecuteScalarAsync())!);
        }
        var fallbacks = DatabaseDrop.Fallbacks;

        // the product: the same kept file a real trial restore writes, every check of DropTrialRestoreAsync in force
        var kept = CompanyBackupService.WriteKeptFile(Path.Combine(work, "set"), new CompanyBackupService.KeptTrial(1, Guid.NewGuid(), name, token));
        var service = new CompanyBackupService(Options, new AttachmentOptions { StorageDirectory = Path.Combine(work, "storage") },
            new EncryptionOptions { KeyDirectory = Path.Combine(work, "keys"), ActiveKeyId = "k1", AllowInsecureFilePermissions = true });
        await service.DropTrialRestoreAsync(kept);

        // the drop took the fallback: had the worker left between the refusal above and the product's own forced drop, that one would have succeeded and proved nothing
        Assert.Equal(fallbacks + 1, DatabaseDrop.Fallbacks);
        Assert.Equal(0L, (long)(await new NpgsqlCommand($"SELECT count(*) FROM pg_database WHERE datname = '{name}'", maint).ExecuteScalarAsync())!);
        Assert.False(File.Exists(kept));
    }

    /// <summary>
    /// Deterministic (no autovacuum involved): a session of a role the account does not hold refuses the forced drop with the same 42501. The fallback ends the account's OWN session in that database and
    /// nothing else; the foreign session still blocks the drop, the refusal reaches the caller as it is and the database stays. Once that session is gone the same call drops the database.
    /// </summary>
    [PostgresRoleFact]
    public async Task A_session_of_a_role_the_account_does_not_hold_still_blocks_the_drop_after_the_accounts_own_sessions_were_terminated()
    {
        var name = NewTestName();
        await using var maint = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await maint.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", maint).ExecuteNonQueryAsync();

        async Task<long> Sessions(string where)
        {
            await using var q = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @n AND " + where, maint);
            q.Parameters.AddWithValue("n", name);
            return (long)(await q.ExecuteScalarAsync())!;
        }
        async Task<long> Databases() => (long)(await new NpgsqlCommand($"SELECT count(*) FROM pg_database WHERE datname = '{name}'", maint).ExecuteScalarAsync())!;

        // two sessions in the database, neither from a pool: one of the account itself and one of the role-administration account, a role the account holds no membership of
        await using var own = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Options.BuildCompanyAdminConnectionString(name)) { Pooling = false }.ConnectionString);
        await own.OpenAsync();
        var ownPid = own.ProcessID;
        await using var foreign = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)) { Database = name, Pooling = false }.ConnectionString);
        await foreign.OpenAsync();
        var foreignPid = foreign.ProcessID;
        Assert.False((bool)(await new NpgsqlCommand("SELECT pg_has_role(current_user, usesysid, 'USAGE') FROM pg_stat_activity WHERE pid = " + foreignPid, maint).ExecuteScalarAsync())!);

        // the precondition, proved and not assumed: the forced drop alone is refused and ends no session
        var refused = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", maint).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Equal("permission denied to terminate process", refused.MessageText);
        Assert.Equal(1L, await Sessions($"pid = {ownPid}"));

        var fallbacks = DatabaseDrop.Fallbacks;
        var thrown = await Assert.ThrowsAsync<PostgresException>(() => DatabaseDrop.ExecuteAsync(maint, name, ifExists: false, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, thrown.SqlState);                 // the refusal of the last statement, unchanged
        Assert.Equal("permission denied to terminate process", thrown.MessageText);
        Assert.Equal(fallbacks + 1, DatabaseDrop.Fallbacks);
        Assert.Equal(1L, await Databases());                                                     // nothing was dropped past the foreign session
        Assert.Equal(0L, await Sessions($"pid = {ownPid}"));                                     // the account's own session was terminated
        Assert.Equal(1L, await Sessions($"pid = {foreignPid}"));                                 // the foreign one was not
        Assert.Equal(1, await new NpgsqlCommand("SELECT 1", foreign).ExecuteScalarAsync());      // and it still works
        Assert.Equal(1, await new NpgsqlCommand("SELECT 1", maint).ExecuteScalarAsync());        // as does the connection that issued the drop (never the current backend)

        await foreign.CloseAsync();
        await DatabaseDrop.ExecuteAsync(maint, name, ifExists: false, CancellationToken.None);
        Assert.Equal(0L, await Databases());
    }
}
