using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The ONE way a disposable database (a trial restore, a recovery-test copy, a restore that failed) is dropped. It decides nothing about WHICH database may be dropped: every caller keeps its own checks and
/// passes the name it validated, exactly as before.
/// <para>
/// <c>DROP DATABASE ... WITH (FORCE)</c> first terminates every other session of the database, and PostgreSQL lets a role terminate only sessions of roles it holds. An autovacuum worker has no role, so
/// while one is working in the database (about one <c>autovacuum_naptime</c> after a large load, which is what a restore is) FORCE is refused with 42501 for every account that is not a superuser. The
/// administrative account is deliberately not a superuser. A PLAIN <c>DROP DATABASE</c> makes autovacuum workers leave by itself and needs no such right, but it only WAITS (five seconds, in the server)
/// for ordinary sessions and is refused with 55006 while one is still there; and a FORCE repeated after that can meet the next worker (one that waited for the lock the plain drop held) and be refused
/// again. So the ordinary sessions go first: FORCE; on 42501 the account terminates ITS OWN sessions in that one database (<c>pg_terminate_backend</c> on backends of the same role needs no additional
/// privilege; never the current backend, never another role, never another database, and none at all in a database the account could not drop) and issues the plain drop, which removes the workers and
/// waits for the terminated sessions to end; only when that is refused with 55006 (a session of another role is still there) FORCE once more, which ends sessions of roles the account holds and is refused
/// for every other one. At most four statements, no loop, no additional privilege. Every other error, and the last refusal, is thrown to the caller unchanged: a session the account may not terminate
/// still blocks the drop.
/// </para>
/// </summary>
internal static class DatabaseDrop
{
    /// <summary>
    /// Terminates the sessions of the CURRENT role in the database <c>@n</c>, except the current one. Rows of other roles and of sessions without a role are never selected, and nothing is selected when
    /// the account does not hold the owner of that database (the drop itself is then refused: a 42501 that is a genuine permission problem ends no session).
    /// </summary>
    internal const string TerminateOwnSessions =
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @n AND usesysid = (SELECT oid FROM pg_roles WHERE rolname = current_user) AND pid <> pg_backend_pid()"
        + " AND (SELECT pg_has_role(datdba, 'USAGE') FROM pg_database WHERE datname = @n)";

    private static long fallbacks;

    /// <summary>How many drops of this process left the first statement with 42501 and took the fallback (diagnostic; the tests prove with it which path a drop took).</summary>
    internal static long Fallbacks => Interlocked.Read(ref fallbacks);

    /// <summary>Drops <paramref name="database"/> (quoted here; a parameter in <see cref="TerminateOwnSessions"/>) on an open maintenance connection.</summary>
    public static Task ExecuteAsync(NpgsqlConnection maintenance, string database, bool ifExists, CancellationToken ct) =>
        RunAsync(async sql =>
        {
            await using var cmd = new NpgsqlCommand(sql, maintenance);
            if (sql == TerminateOwnSessions) cmd.Parameters.AddWithValue("n", database);
            await cmd.ExecuteNonQueryAsync(ct);
        }, new NpgsqlCommandBuilder().QuoteIdentifier(database), ifExists);

    /// <summary>The statement sequence over an executor (the unit tests give a fake one).</summary>
    internal static async Task RunAsync(Func<string, Task> execute, string quotedName, bool ifExists)
    {
        var plain = $"DROP DATABASE {(ifExists ? "IF EXISTS " : "")}{quotedName}";
        var forced = plain + " WITH (FORCE)";
        try { await execute(forced); return; }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }       // a session FORCE may not terminate (an autovacuum worker), or no right to drop at all: the plain drop tells
        Interlocked.Increment(ref fallbacks);
        await execute(TerminateOwnSessions);                                                                  // the plain drop would only wait for them
        try { await execute(plain); return; }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse) { }                 // the workers are gone, a session of another role remains
        await execute(forced);
    }
}
