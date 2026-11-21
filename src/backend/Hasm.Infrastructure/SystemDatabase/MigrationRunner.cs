using Npgsql;

namespace Hasm.Infrastructure.SystemDatabase;

public sealed record MigrationResult(IReadOnlyList<int> Applied, int AlreadyApplied);

/// <summary>
/// Applies versioned SQL scripts exactly once, in order, each in its own transaction, recording
/// a checksum in the journal. Re-running is a no-op. Applied scripts that were edited, or a database
/// newer than this build, abort the run. Serialised across processes with a PostgreSQL advisory lock.
/// After every script, inside its transaction, and once more at the end of every run (also when nothing was applied), the runner
/// verifies that no trigger of the database is left switched off; a script that leaves one off is rolled back and the run fails.
/// </summary>
public sealed class MigrationRunner(string connectionString)
{
    // Arbitrary constant key identifying "HASM system migrations" for pg_advisory_lock.
    private const long AdvisoryLockKey = 0x4841534D_0001;

    public async Task<MigrationResult> ApplyAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken ct = default)
    {
        MigrationScript.Validate(scripts);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await Execute(conn, "SELECT pg_advisory_lock(@k)", ct, ("k", AdvisoryLockKey));
        try
        {
            await Execute(conn,
                $"CREATE TABLE IF NOT EXISTS {SystemSchema.MigrationJournalTable} (" +
                "version integer PRIMARY KEY, name text NOT NULL, checksum text NOT NULL, applied_at timestamptz NOT NULL)", ct);

            var applied = new Dictionary<int, string>();
            await using (var cmd = new NpgsqlCommand($"SELECT version, checksum FROM {SystemSchema.MigrationJournalTable}", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) applied[reader.GetInt32(0)] = reader.GetString(1);
            }

            foreach (var version in applied.Keys)
            {
                var script = scripts.FirstOrDefault(s => s.Version == version)
                    ?? throw new MigrationException($"Database has migration {version:0000} that this build does not know; refusing to continue (database is newer than the application).");
                if (applied[version] != script.Checksum)
                    throw new MigrationException($"Migration {script.Name} was modified after it was applied (checksum mismatch). Applied migrations are immutable; add a new migration instead.");
            }

            var newlyApplied = new List<int>();
            foreach (var script in scripts.Where(s => !applied.ContainsKey(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var run = new NpgsqlCommand(script.Sql, conn, tx)) await run.ExecuteNonQueryAsync(ct);
                await using (var journal = new NpgsqlCommand(
                    $"INSERT INTO {SystemSchema.MigrationJournalTable} (version, name, checksum, applied_at) VALUES (@v, @n, @c, now())", conn, tx))
                {
                    journal.Parameters.AddWithValue("v", script.Version);
                    journal.Parameters.AddWithValue("n", script.Name);
                    journal.Parameters.AddWithValue("c", script.Checksum);
                    await journal.ExecuteNonQueryAsync(ct);
                }
                // Post-migration safeguard check, before the commit: leaving the "await using" scope by exception rolls the script back.
                var left = await SwitchedOffTriggers(conn, tx, ct);
                if (left.Count > 0)
                    throw new MigrationException($"Migration {script.Name} leaves trigger(s) switched off: {string.Join(", ", left)}. The migration was rolled back; a migration must re-enable every trigger it disables.");
                var role = await ReplicationRole(conn, tx, ct);
                if (!string.Equals(role, "origin", StringComparison.Ordinal))
                    throw new MigrationException($"Migration {script.Name} ends with session_replication_role = '{role}' (triggers do not fire). The migration was rolled back.");
                await tx.CommitAsync(ct);
                newlyApplied.Add(script.Version);
            }
            // Also when nothing was applied: a database whose safeguard trigger was switched off by any means is not accepted as migrated.
            var off = await SwitchedOffTriggers(conn, null, ct);
            if (off.Count > 0)
                throw new MigrationException($"The database has trigger(s) switched off: {string.Join(", ", off)}. Every trigger must be enabled; refusing to continue.");
            return new MigrationResult(newlyApplied, applied.Count);
        }
        finally
        {
            await Execute(conn, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", AdvisoryLockKey));
        }
    }

    /// <summary>
    /// Every non-internal trigger of the database that does not fire in a normal session: tgenabled 'D' (disabled) or 'R' (fires only
    /// when session_replication_role = replica). 'O' (origin, the default) and 'A' (always) fire and are accepted.
    /// </summary>
    private static async Task<List<string>> SwitchedOffTriggers(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        const string sql =
            "SELECT n.nspname || '.' || c.relname || '.' || t.tgname || ' (' || t.tgenabled::text || ')' " +
            "FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE NOT t.tgisinternal AND t.tgenabled IN ('D', 'R') ORDER BY 1";
        var found = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add(reader.GetString(0));
        return found;
    }

    private static async Task<string> ReplicationRole(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_catalog.current_setting('session_replication_role')", conn, tx);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task Execute(NpgsqlConnection conn, string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
