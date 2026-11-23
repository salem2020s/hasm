using Hasm.Infrastructure.Backup;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The ONE way a test drops a disposable database itself (a cleanup, or a "the database is lost" arrangement): through the product's <see cref="DatabaseDrop"/>, so that an autovacuum worker in the database
/// cannot refuse it with 42501 for the non-superuser test account (review L3 of PR 241; a bare <c>DROP DATABASE ... WITH (FORCE)</c> can). Only a database with the disposable prefix is dropped, unless
/// the caller states that the name is one the product generated in this test (<c>productGenerated</c>: a trial restore made by the <c>Hasm.Admin</c> binary, which takes no injected test name).
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

    public static Task ExecuteAsync(NpgsqlConnection maintenance, string database, bool ifExists = false, bool productGenerated = false)
    {
        if (!productGenerated && !database.StartsWith(PostgresTestEnvironment.DatabasePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to drop a database that is not a test database.");
        return DatabaseDrop.ExecuteAsync(maintenance, database, ifExists, CancellationToken.None);
    }
}
