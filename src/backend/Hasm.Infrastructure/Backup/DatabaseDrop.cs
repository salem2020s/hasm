using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The ONE way a disposable database (a trial restore, a recovery-test copy, a restore that failed) is dropped. It decides nothing about WHICH database may be dropped: every caller keeps its own checks and
/// passes the name it validated, exactly as before.
/// <para>
/// <c>DROP DATABASE ... WITH (FORCE)</c> first terminates every other session of the database, and PostgreSQL lets a role terminate only sessions of roles it holds. An autovacuum worker has no role, so
/// while one is working in the database (about one <c>autovacuum_naptime</c> after a large load, which is what a restore is) FORCE is refused with 42501 for every account that is not a superuser. The
/// administrative account is deliberately not a superuser. A PLAIN <c>DROP DATABASE</c> cancels autovacuum workers itself and needs no such right, but is refused with 55006 while ordinary sessions (the
/// account's own pooled connections) are still there. So: FORCE; on 42501 the plain drop; on 55006 from that, FORCE once more. At most three statements, no waiting, no loop, no additional privilege.
/// Every other error, and a second refusal, is thrown to the caller unchanged.
/// </para>
/// </summary>
internal static class DatabaseDrop
{
    /// <summary>Drops <paramref name="database"/> (quoted here) on an open maintenance connection.</summary>
    public static Task ExecuteAsync(NpgsqlConnection maintenance, string database, bool ifExists, CancellationToken ct) =>
        RunAsync(async sql =>
        {
            await using var cmd = new NpgsqlCommand(sql, maintenance);
            await cmd.ExecuteNonQueryAsync(ct);
        }, new NpgsqlCommandBuilder().QuoteIdentifier(database), ifExists);

    /// <summary>The statement sequence over an executor (the unit tests give a fake one).</summary>
    internal static async Task RunAsync(Func<string, Task> execute, string quotedName, bool ifExists)
    {
        var plain = $"DROP DATABASE {(ifExists ? "IF EXISTS " : "")}{quotedName}";
        var forced = plain + " WITH (FORCE)";
        try { await execute(forced); return; }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }       // a session FORCE may not terminate (an autovacuum worker), or no right to drop at all: the plain drop tells
        try { await execute(plain); return; }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse) { }                 // the worker is gone, ordinary sessions remain
        await execute(forced);
    }
}
