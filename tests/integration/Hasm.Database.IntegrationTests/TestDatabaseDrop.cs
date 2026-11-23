using Hasm.Infrastructure.Backup;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The ONE way a test drops a disposable database itself (a cleanup, or a "the database is lost" arrangement): through the product's <see cref="DatabaseDrop"/>, so that an autovacuum worker in the database
/// cannot refuse it with 42501 for the non-superuser test account (review L3 of PR 241; a bare <c>DROP DATABASE ... WITH (FORCE)</c> can). Only a database with the disposable prefix is dropped, unless
/// the caller states that the name is one the product generated in this test (<c>productGenerated</c>: a trial restore made by the <c>Hasm.Admin</c> binary, which takes no injected test name). Such a
/// name is bounded too (review L1 of PR 243): never a built-in database or the one the connection is in, and only a database that carries the product's trial-restore marker (its comment starts with
/// <see cref="CompanyBackupService.CommentPrefix"/>); a database that no longer exists is left alone.
/// <para>
/// A drop that meets a worker takes the helper's fallback and so raises <see cref="DatabaseDrop.Fallbacks"/>, the counter of the whole process. <c>DatabaseDropAutovacuumTests</c> asserts an exact step of
/// that counter around one product call; that stays exact because the class is in the serial collection (parallelization disabled: no other test, and so no other test's cleanup, runs while one of its
/// tests does) and because no call of this method is left running in the background: every caller awaits it.
/// </para>
/// </summary>
internal static class TestDatabaseDrop
{
    public static async Task ExecuteAsync(string maintenanceConnectionString, string database, bool ifExists = false, bool productGenerated = false)
    {
        await using var conn = new NpgsqlConnection(maintenanceConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, database, ifExists, productGenerated);
    }

    public static async Task ExecuteAsync(NpgsqlConnection maintenance, string database, bool ifExists = false, bool productGenerated = false)
    {
        if (!database.StartsWith(PostgresTestEnvironment.DatabasePrefix, StringComparison.Ordinal))
        {
            if (!productGenerated) throw new InvalidOperationException("Refusing to drop a database that is not a test database.");
            if (database is "postgres" or "template0" or "template1" || string.Equals(database, maintenance.Database, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to drop a built-in or maintenance database.");
            await using var q = new NpgsqlCommand("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = @n", maintenance);
            q.Parameters.AddWithValue("n", database);
            await using var reader = await q.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                if (ifExists) return;                                                            // already gone: nothing to drop
                throw new InvalidOperationException("The database to drop does not exist.");
            }
            if (await reader.IsDBNullAsync(0) || !reader.GetString(0).StartsWith(CompanyBackupService.CommentPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to drop a database that does not carry the trial-restore marker.");
        }
        await DatabaseDrop.ExecuteAsync(maintenance, database, ifExists, CancellationToken.None);
    }
}
