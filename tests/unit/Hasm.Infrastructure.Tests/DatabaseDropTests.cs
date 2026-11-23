using Hasm.Infrastructure.Backup;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

/// <summary>The statement sequence of <see cref="DatabaseDrop"/> over a fake executor: FORCE; on 42501 the plain drop; on 55006 from that, FORCE once more; everything else is thrown unchanged.</summary>
public class DatabaseDropTests
{
    private const string Name = "\"hasm_t_x\"";
    private const string Forced = "DROP DATABASE \"hasm_t_x\" WITH (FORCE)";
    private const string Plain = "DROP DATABASE \"hasm_t_x\"";

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
    public async Task A_forced_drop_refused_with_42501_is_followed_by_the_plain_drop()
    {
        var (log, thrown) = await Run(false, Pg("42501"), null);
        Assert.Null(thrown);
        Assert.Equal([Forced, Plain], log);
        Assert.Equal(["DROP DATABASE IF EXISTS \"hasm_t_x\" WITH (FORCE)", "DROP DATABASE IF EXISTS \"hasm_t_x\""], (await Run(true, Pg("42501"), null)).Log);
    }

    [Fact]
    public async Task A_plain_drop_refused_with_55006_is_followed_by_one_more_forced_drop_and_never_a_fourth_statement()
    {
        var (log, thrown) = await Run(false, Pg("42501"), Pg("55006"), null);
        Assert.Null(thrown);
        Assert.Equal([Forced, Plain, Forced], log);

        var again = Pg("42501");                                                                 // the third statement is refused again: thrown as it is, no loop
        var (log2, thrown2) = await Run(false, Pg("42501"), Pg("55006"), again);
        Assert.Same(again, thrown2);
        Assert.Equal([Forced, Plain, Forced], log2);
        var busy = Pg("55006");
        Assert.Same(busy, (await Run(false, Pg("42501"), Pg("55006"), busy)).Thrown);
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
        // the plain drop: only 55006 leads on; a second 42501 (the account may not drop this database at all) is thrown as before
        foreach (var second in new Exception[] { Pg("42501"), Pg("3D000"), new NpgsqlException("connection lost"), new OperationCanceledException() })
        {
            var (log, thrown) = await Run(false, Pg("42501"), second);
            Assert.Same(second, thrown);
            Assert.Equal([Forced, Plain], log);
        }
    }
}
