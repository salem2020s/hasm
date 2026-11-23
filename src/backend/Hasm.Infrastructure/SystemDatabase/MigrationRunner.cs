using Npgsql;

namespace Hasm.Infrastructure.SystemDatabase;

public sealed record MigrationResult(IReadOnlyList<int> Applied, int AlreadyApplied);

/// <summary>
/// Applies versioned SQL scripts exactly once, in order, each in its own transaction, recording
/// a checksum in the journal. Re-running is a no-op. Applied scripts that were edited, or a database
/// newer than this build, abort the run. Serialised across processes with a PostgreSQL advisory lock.
/// After every script, inside its transaction, the runner verifies that no trigger or event trigger is switched off, that every
/// trigger that existed before the script still exists and is still bound to the same function, and that session_replication_role
/// is unchanged; a script that fails this is rolled back and the run fails. The switched-off check runs once more at the end of
/// every run (also when nothing was applied), and a run does not start when session_replication_role is not "origin".
/// </summary>
public sealed class MigrationRunner(string connectionString)
{
    // Arbitrary constant key identifying "HASM system migrations" for pg_advisory_lock.
    private const long AdvisoryLockKey = 0x4841534D_0001;

    /// <summary>
    /// TEST SEAM. An ADDITIONAL reading of session_replication_role taken after a script, checked with the same rule as the real
    /// reading. It can only add a refusal, never remove one: the real reading is always taken and always checked. It exists because
    /// only a superuser can change the setting, and the test clusters deliberately have none.
    /// </summary>
    internal Func<string>? AdditionalReplicationRoleReading { get; init; }

    /// <summary>TEST SEAM, same rule as above: an additional reading of "is a superuser" that can only add a refusal (see <see cref="MigrationAccountPolicy"/>).</summary>
    internal Func<bool>? AdditionalSuperuserReading { get; init; }

    /// <summary>The explicit development/test override of owner decision Q3: allows a superuser migration account (logged through <see cref="Log"/>). Never set in production.</summary>
    public bool AllowSuperuser { get; init; }

    /// <summary>Where the override warning goes (default: the standard error stream of the administrative tool).</summary>
    public Action<string>? Log { get; init; }

    public async Task<MigrationResult> ApplyAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken ct = default)
    {
        MigrationScript.Validate(scripts);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await MigrationAccountPolicy.EnsureAsync(conn, AllowSuperuser, Log, AdditionalSuperuserReading, ct);

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

            // Triggers do not fire unless session_replication_role is "origin": such a session never runs a migration.
            var roleAtStart = await ReplicationRole(conn, null, ct);
            if (!string.Equals(roleAtStart, "origin", StringComparison.Ordinal))
                throw new MigrationException($"The session has session_replication_role = '{roleAtStart}' (triggers do not fire); refusing to run migrations.");

            var newlyApplied = new List<int>();
            foreach (var script in scripts.Where(s => !applied.ContainsKey(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                var roleBefore = await ReplicationRole(conn, tx, ct);
                var triggersBefore = await Triggers(conn, tx, ct);
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
                var problem = PostMigrationProblem(triggersBefore, await Triggers(conn, tx, ct), await SwitchedOffEventTriggers(conn, tx, ct))
                              ?? ReplicationRoleProblem(roleBefore, await ReplicationRole(conn, tx, ct))
                              ?? (AdditionalReplicationRoleReading is { } extra ? ReplicationRoleProblem(roleBefore, extra()) : null);
                if (problem is not null)
                    throw new MigrationException($"Migration {script.Name} {problem} The migration was rolled back.");
                await tx.CommitAsync(ct);
                newlyApplied.Add(script.Version);
            }
            // Also when nothing was applied: a database whose safeguard trigger was switched off by any means is not accepted as migrated.
            await EnsureNoSwitchedOffAsync(conn, ct);
            return new MigrationResult(newlyApplied, applied.Count);
        }
        finally
        {
            await Execute(conn, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", AdvisoryLockKey));
        }
    }

    /// <summary>
    /// Owner decision 2026-11-22 (Q2): refuses a database that holds a switched-off non-internal trigger or event trigger, whether or not a migration is pending. The message
    /// names every affected trigger as table.trigger (state letter: D disabled, R replica only); the triggers are NEVER re-enabled here (that needs an investigation of the data
    /// written while they were off, and a restored trigger does not make that data valid). The API never runs this; only the administrative commands do.
    /// </summary>
    private static async Task EnsureNoSwitchedOffAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var off = SwitchedOff(await Triggers(conn, null, ct));
        off.AddRange(await SwitchedOffEventTriggers(conn, null, ct));
        if (off.Count > 0)
            throw new MigrationException($"The database has trigger(s) switched off: {string.Join(", ", off)}. Every trigger must be enabled; refusing to continue. " +
                                         "The triggers were not changed: find out why they were switched off and check the data written meanwhile before enabling them by hand.");
    }

    /// <summary>The same refusal on a fresh connection, for a command that would otherwise run no migration at all (a company that is already Ready).</summary>
    public static async Task EnsureNoSwitchedOffAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureNoSwitchedOffAsync(conn, ct);
    }

    /// <summary>One non-internal trigger: where it is, what it is called, which function it runs, and its firing state (pg_trigger.tgenabled).</summary>
    internal sealed record TriggerState(uint TableOid, string Table, string Name, uint FunctionOid, char Enabled);

    /// <summary>
    /// Every non-internal trigger of the database. The statement uses no operator and no unqualified name on purpose, so nothing a
    /// migration creates or sets (an operator named "=" in another schema, a changed search_path) can change what it returns; the
    /// filtering and comparing are done here, not in SQL.
    /// </summary>
    private static async Task<List<TriggerState>> Triggers(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        const string sql =
            "SELECT t.tgrelid::pg_catalog.oid, t.tgrelid::pg_catalog.regclass::pg_catalog.text, t.tgname::pg_catalog.text, t.tgfoid::pg_catalog.oid, t.tgenabled::pg_catalog.text " +
            "FROM pg_catalog.pg_trigger t WHERE NOT t.tgisinternal";
        var found = new List<TriggerState>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found.Add(new TriggerState(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<uint>(3), reader.GetString(4)[0]));
        return found;
    }

    /// <summary>Event triggers that do not fire in a normal session (evtenabled 'D' or 'R'); same construction as <see cref="Triggers"/>.</summary>
    private static async Task<List<string>> SwitchedOffEventTriggers(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var found = new List<string>();
        await using var cmd = new NpgsqlCommand("SELECT e.evtname::pg_catalog.text, e.evtenabled::pg_catalog.text FROM pg_catalog.pg_event_trigger e", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (FiresInNormalSession(reader.GetString(1)[0]) is false) found.Add($"event trigger {reader.GetString(0)} ({reader.GetString(1)})");
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>'O' (origin, the default) and 'A' (always) fire in a normal session; 'D' (disabled) and 'R' (replica only) do not. Any other value is treated as not firing.</summary>
    internal static bool FiresInNormalSession(char enabled) => enabled is 'O' or 'A';

    internal static List<string> SwitchedOff(IEnumerable<TriggerState> triggers)
    {
        var off = triggers.Where(t => !FiresInNormalSession(t.Enabled)).Select(t => $"{t.Table}.{t.Name} ({t.Enabled})").ToList();
        off.Sort(StringComparer.Ordinal);
        return off;
    }

    /// <summary>
    /// What a script did to the triggers that it must not do, or null: left a trigger or event trigger switched off, removed a trigger
    /// that existed before it, or bound an existing trigger to another function.
    /// </summary>
    internal static string? PostMigrationProblem(IReadOnlyList<TriggerState> before, IReadOnlyList<TriggerState> after, IReadOnlyList<string> eventTriggersOff)
    {
        var off = SwitchedOff(after);
        off.AddRange(eventTriggersOff);
        if (off.Count > 0) return $"leaves trigger(s) switched off: {string.Join(", ", off)}. A migration must re-enable every trigger it disables.";

        var now = after.ToDictionary(t => (t.TableOid, t.Name));
        var removed = before.Where(t => !now.ContainsKey((t.TableOid, t.Name))).Select(t => $"{t.Table}.{t.Name}").Order(StringComparer.Ordinal).ToList();
        if (removed.Count > 0) return $"removes trigger(s) that existed before it: {string.Join(", ", removed)}.";
        var rebound = before.Where(t => now[(t.TableOid, t.Name)].FunctionOid != t.FunctionOid).Select(t => $"{t.Table}.{t.Name}").Order(StringComparer.Ordinal).ToList();
        if (rebound.Count > 0) return $"binds existing trigger(s) to another function: {string.Join(", ", rebound)}.";
        return null;
    }

    /// <summary>The setting must be "origin" before the script and unchanged after it.</summary>
    internal static string? ReplicationRoleProblem(string before, string after) =>
        string.Equals(before, "origin", StringComparison.Ordinal) && string.Equals(after, before, StringComparison.Ordinal)
            ? null
            : $"runs with or ends with session_replication_role = '{after}' (it was '{before}' before the script; triggers do not fire unless it is 'origin').";

    private static async Task<string> ReplicationRole(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
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
