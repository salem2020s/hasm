using Hasm.Infrastructure.Backup;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The statement sequence of <see cref="DatabaseDrop"/> over a fake executor: FORCE; on 42501 the account's own sessions in that database are terminated and the plain drop follows; on 55006 from that,
/// FORCE once more; everything else is thrown unchanged.
/// <para>
/// In the serial collection because of the counter test: <see cref="DatabaseDrop.Fallbacks"/> is one counter for the whole process, and the test asserts its EXACT steps. Parallelization is disabled for
/// this collection, so no other class of the assembly runs while this one does (xUnit runs such collections after the parallel ones, one class at a time): a later unit class that reaches the
/// helper's fallback cannot move the counter between two assertions here (review L5 of PR 241). The assertions are unchanged.
/// </para>
/// </summary>
[Collection(SerialMeasurementCollection.Name)]
public class DatabaseDropTests
{
    private const string Name = "\"hasm_t_x\"";
    private const string Forced = "DROP DATABASE \"hasm_t_x\" WITH (FORCE)";
    private const string Plain = "DROP DATABASE \"hasm_t_x\"";
    private const string Own = DatabaseDrop.TerminateOwnSessions;

    private static PostgresException Pg(string state) => new("refused", "ERROR", "ERROR", state);

    /// <summary>Runs the sequence; each statement gets the next scripted outcome (null = success).</summary>
    private static async Task<(List<string> Log, Exception? Thrown)> Run(bool ifExists, params Exception?[] outcomes)
    {
        var log = new List<string>();
        Exception? thrown = null;
        try
        {
            await DatabaseDrop.RunAsync(sql =>
            {
                log.Add(sql);
                Assert.True(log.Count <= outcomes.Length, "more statements than scripted: " + sql);
                return outcomes[log.Count - 1] is { } ex ? Task.FromException(ex) : Task.CompletedTask;
            }, Name, ifExists);
        }
        catch (Exception ex) { thrown = ex; }
        return (log, thrown);
    }

    [Fact]
    public async Task A_forced_drop_that_succeeds_is_the_only_statement()
    {
        var (log, thrown) = await Run(false, [null]);
        Assert.Null(thrown);
        Assert.Equal([Forced], log);
        Assert.Equal(["DROP DATABASE IF EXISTS \"hasm_t_x\" WITH (FORCE)"], (await Run(true, [null])).Log);
    }

    [Fact]
    public async Task A_forced_drop_refused_with_42501_terminates_the_accounts_own_sessions_and_is_followed_by_the_plain_drop()
    {
        var (log, thrown) = await Run(false, Pg("42501"), null, null);
        Assert.Null(thrown);
        Assert.Equal([Forced, Own, Plain], log);
        Assert.Equal(["DROP DATABASE IF EXISTS \"hasm_t_x\" WITH (FORCE)", Own, "DROP DATABASE IF EXISTS \"hasm_t_x\""], (await Run(true, Pg("42501"), null, null)).Log);
    }

    [Fact]
    public void Only_sessions_of_the_current_role_in_the_named_database_and_never_the_current_backend_are_terminated()
    {
        // the exact statement, so that no change of it goes unnoticed: the database is a parameter (never text), the role is the current one, the current backend is excluded, and nothing is selected
        // in a database whose owner the account does not hold
        Assert.Equal(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @n AND usesysid = (SELECT oid FROM pg_roles WHERE rolname = current_user) AND pid <> pg_backend_pid()"
            + " AND (SELECT pg_has_role(datdba, 'USAGE') FROM pg_database WHERE datname = @n)", Own);
        Assert.DoesNotContain("hasm_t_x", Own);
    }

    [Fact]
    public async Task A_plain_drop_refused_with_55006_is_followed_by_one_more_forced_drop_and_never_a_fifth_statement()
    {
        var (log, thrown) = await Run(false, Pg("42501"), null, Pg("55006"), null);
        Assert.Null(thrown);
        Assert.Equal([Forced, Own, Plain, Forced], log);

        var again = Pg("42501");                                                                 // the last statement is refused again (a session of a role the account does not hold): thrown as it is, no loop
        var (log2, thrown2) = await Run(false, Pg("42501"), null, Pg("55006"), again);
        Assert.Same(again, thrown2);
        Assert.Equal([Forced, Own, Plain, Forced], log2);
        var busy = Pg("55006");
        Assert.Same(busy, (await Run(false, Pg("42501"), null, Pg("55006"), busy)).Thrown);
    }

    [Fact]
    public async Task Every_other_error_is_thrown_unchanged_and_nothing_else_is_executed()
    {
        // the forced drop: only 42501 leads on (55006, a missing database, a connection failure and a cancellation do not)
        foreach (var first in new Exception[] { Pg("55006"), Pg("3D000"), Pg("57014"), new NpgsqlException("connection lost"), new InvalidOperationException("closed"), new OperationCanceledException() })
        {
            var (log, thrown) = await Run(false, first);
            Assert.Same(first, thrown);
            Assert.Equal([Forced], log);
        }
        // terminating the own sessions: every error is thrown and the plain drop is not attempted
        foreach (var second in new Exception[] { Pg("42501"), Pg("55006"), Pg("57014"), new NpgsqlException("connection lost"), new OperationCanceledException() })
        {
            var (log, thrown) = await Run(false, Pg("42501"), second);
            Assert.Same(second, thrown);
            Assert.Equal([Forced, Own], log);
        }
        // the plain drop: only 55006 leads on; a second 42501 (the account may not drop this database at all) is thrown as before
        foreach (var third in new Exception[] { Pg("42501"), Pg("3D000"), new NpgsqlException("connection lost"), new OperationCanceledException() })
        {
            var (log, thrown) = await Run(false, Pg("42501"), null, third);
            Assert.Same(third, thrown);
            Assert.Equal([Forced, Own, Plain], log);
        }
    }

    [Fact]
    public async Task The_fallback_is_counted_for_a_drop_that_took_it_and_not_for_one_whose_forced_drop_succeeded_or_failed_otherwise()
    {
        // the counter is per process: exact here because this class runs alone (the serial collection, see the class comment) and xunit runs the tests of one class one after the other
        var before = DatabaseDrop.Fallbacks;
        await Run(false, [null]);
        await Run(false, Pg("55006"));
        Assert.Equal(before, DatabaseDrop.Fallbacks);
        await Run(false, Pg("42501"), null, null);
        Assert.Equal(before + 1, DatabaseDrop.Fallbacks);
        await Run(false, Pg("42501"), null, Pg("55006"), Pg("42501"));
        Assert.Equal(before + 2, DatabaseDrop.Fallbacks);
    }
}
