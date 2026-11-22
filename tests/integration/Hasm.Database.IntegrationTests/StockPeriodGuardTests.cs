using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPeriodGuard;
using LY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using NR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligationReversal;
using NX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeSettlement;
using RV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;
using SAP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPosting;
using SAR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentReversal;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The STOCK-PERIOD-DB guard (owner decision D-220, option b): PostgreSQL itself refuses to insert a stock or cost posting record whose
/// effective date lies in a Soft Closed period, a Final Closed period or no fiscal period. Every negative test writes with plain SQL,
/// bypassing the services, which already refuse such a posting on their own.
/// </summary>
public abstract class StockPeriodGuardTestBase : NegativeStockTestBase
{
    protected sealed record Guarded(string Name, string Table, string DateColumn, string Trigger);

    /// <summary>The nine guarded tables with their date column and their guard trigger.</summary>
    protected static readonly Guarded[] All =
    [
        new("cost operation", CO.Table, CO.EffectiveDate, G.TrCostOperation),
        new("cost entry", EN.Table, EN.EffectiveDate, G.TrCostEntry),
        new("stock posting", SP.Table, SP.EffectiveDate, G.TrStockPosting),
        new("stock posting reversal", RV.Table, RV.EffectiveDate, G.TrStockReversal),
        new("adjustment posting", SAP.Table, SAP.EffectiveDate, G.TrAdjustmentPosting),
        new("adjustment reversal", SAR.Table, SAR.EffectiveDate, G.TrAdjustmentReversal),
        new("negative obligation", NO.Table, NO.EffectiveDate, G.TrNegativeObligation),
        new("negative settlement", NX.Table, NX.EffectiveDate, G.TrNegativeSettlement),
        new("negative obligation reversal", NR.Table, NR.EffectiveDate, G.TrNegativeObligationReversal),
    ];

    // FY2026 (monthly) is the only fiscal year. January is Soft Closed and February Final Closed by ClosePeriods; everything real is dated in March.
    protected const string SoftDate = "2026-01-15", FinalDate = "2026-02-15", MissingDate = "2025-06-15", OpenDate = "2026-05-15";
    protected const string SoftMessage = "the fiscal period is soft closed", FinalMessage = "the fiscal period is final closed", MissingMessage = "no fiscal period covers the posting date";

    /// <summary>At least one real row in every guarded table, written by the services, all dated in March 2026 (Open).</summary>
    protected async Task Populate()
    {
        await PrepareNegative();
        // an adjustment (through a stocktake) and its reversal
        await Receive(Fifo2, 10m, 100m);
        var take = await ApprovedTake(W1, C(Fifo2, Pcs, 7m));
        var adjustment = Ok(await Generate(take));
        Ok(await Adj.PostAsync(Admin.Id, Company.Id, adjustment.Id, null));
        Ok(await Adj.ReverseAsync(Admin.Id, Company.Id, adjustment.Id, new StockReverseInput("2026-03-20", "recount")));
        // a receipt and its reversal
        var receipt = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        Ok(await Post(receipt, Amounts(50m)));
        Ok(await Reverse(receipt, "2026-03-20"));
        // an obligation and its settlement
        await Emptied(Avg, 3m, 15m);
        Ok(await Post(await Exceptional(Avg, 7m, "2026-03-12")));
        await ReceiveDoc(Avg, 4m, 24m, "2026-03-13");
        // an obligation and its reversal
        await Emptied(Avg2, 3m, 15m);
        var issue = await Exceptional(Avg2, 2m, "2026-03-12");
        Ok(await Post(issue));
        Ok(await Reverse(issue, "2026-03-20"));
        foreach (var g in All) Assert.True(await Sql<long>($"SELECT count(*) FROM {g.Table}") > 0, $"no {g.Name} row was prepared");
    }

    protected async Task<FiscalPeriodView> Period(int month) => (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(x => x.StartDate == new DateOnly(2026, month, 1));

    /// <summary>January becomes Soft Closed and February Final Closed (the plain calendar: no close probe is under test here).</summary>
    protected async Task ClosePeriods()
    {
        var january = await Period(1);
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, january.Id, january.Revision));
        var february = await Period(2);
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, february.Id, february.Revision));
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, february.Id, soft.Revision));
    }

    /// <summary>An existing row of the table, inserted again unchanged except for its date. The BEFORE INSERT triggers see it first; a row that passes them all ends at the primary key (23505).</summary>
    protected static string Clone(Guarded g, string date) =>
        $"INSERT INTO {g.Table} OVERRIDING SYSTEM VALUE SELECT (jsonb_populate_record(NULL::{g.Table}, to_jsonb(s) || jsonb_build_object('{g.DateColumn}', '{date}'::date))).* FROM {g.Table} s LIMIT 1";

    /// <summary>Only the guard trigger of the table fires: every other user trigger is switched off for the (rolled back) transaction, so the answer is the guard's alone.</summary>
    protected static string[] OnlyTheGuard(Guarded g) => [$"ALTER TABLE {g.Table} DISABLE TRIGGER USER", $"ALTER TABLE {g.Table} ENABLE TRIGGER {g.Trigger}"];

    protected async Task<NpgsqlConnection> Owner()
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)) { Pooling = false, CommandTimeout = 30 }.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Runs the statements in one transaction that is ALWAYS rolled back; returns the PostgreSQL error, or null when every statement succeeded.</summary>
    protected static async Task<PostgresException?> Attempt(NpgsqlConnection conn, params string[] statements)
    {
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            return null;
        }
        catch (PostgresException ex) { return ex; }
        finally { await tx.RollbackAsync(); }
    }

    /// <summary>The refusal came from the guard function of this migration (PostgreSQL names the functions on the call stack), with the expected reason.</summary>
    protected static void AssertGuard(PostgresException? ex, string message, string what, string? function = null)
    {
        function ??= G.FnGuard;
        Assert.True(ex is not null, $"{what}: the insert was accepted");
        Assert.True(ex!.SqlState == "P0001" && ex.MessageText == message && (ex.Where ?? "").Contains($"function {function}()", StringComparison.Ordinal),
            $"{what}: expected the guard's '{message}', got {ex.SqlState} '{ex.MessageText}' at {ex.Where}");
    }

    protected static bool FromGuard(PostgresException? ex, string? function = null) => ex is not null && (ex.Where ?? "").Contains($"function {function ?? G.FnGuard}()", StringComparison.Ordinal);

    /// <summary>The two records without a date of their own: they reference a cost entry, and the entry's date decides.</summary>
    protected sealed record Referenced(string Name, string Table, string EntryColumn, string Trigger);

    protected static readonly Referenced[] Refs =
    [
        new("cost layer", LY.Table, LY.EntryId, G.TrCostLayer),
        new("cost consumption", CS.Table, CS.EntryId, G.TrCostConsumption),
    ];

    /// <summary>A new row copied from an existing one (same cost entry, so same entry date); a layer gets a new id and sequence number, a consumption a new generated id.</summary>
    protected static string CloneRef(Referenced r) => r.Table == LY.Table
        ? $"INSERT INTO {r.Table} SELECT (jsonb_populate_record(NULL::{r.Table}, to_jsonb(s) || jsonb_build_object('{LY.Id}', gen_random_uuid(), '{LY.Seq}', 999999999))).* FROM {r.Table} s LIMIT 1"
        : $"INSERT INTO {r.Table} ({CS.EntryId}, {CS.LayerId}, {CS.Quantity}, {CS.Amount}) SELECT {CS.EntryId}, {CS.LayerId}, 1, 1 FROM {r.Table} LIMIT 1";

    protected static string[] OnlyTheEntryGuard(Referenced r) => [$"ALTER TABLE {r.Table} DISABLE TRIGGER USER", $"ALTER TABLE {r.Table} ENABLE TRIGGER {r.Trigger}"];

    protected static string NewCostOperation(string date) =>
        $"INSERT INTO {CO.Table} ({CO.Key}, {CO.Kind}, {CO.RequestHash}, {CO.EffectiveDate}, {CO.CreatedBy}, {CO.CreatedAtUtc}) VALUES ('raw-' || gen_random_uuid(), 1, repeat('a', 64), '{date}', gen_random_uuid(), now())";

    /// <summary>Awaits a task that must finish; when it does not within 40 s the failure names every session of the database and what it waits for (never a silent hang).</summary>
    protected async Task<T> Within<T>(Task<T> task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(40))) == task) return await task;
        var sessions = await Sql<string>("SELECT coalesce(string_agg(usename || '|' || state || '|' || coalesce(wait_event_type, '') || '|' || left(query, 70), E'\\n'), '') FROM pg_stat_activity WHERE datname = current_database()");
        throw new Xunit.Sdk.XunitException($"{what} did not finish in 40 s. Sessions:\n{sessions}");
    }

    /// <summary>Waits until at least <paramref name="sessions"/> sessions are blocked on a lock (read from pg_locks, which every role sees in full; pg_stat_activity hides the wait of another role's session from the runtime role's peers).</summary>
    protected async Task<bool> Blocked(int sessions = 1)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await Sql<long>("SELECT count(DISTINCT pid) FROM pg_locks WHERE NOT granted AND (database = (SELECT oid FROM pg_database WHERE datname = current_database()) OR locktype IN ('transactionid', 'tuple'))") >= sessions) return true;
            await Task.Delay(100);
        }
        return false;
    }
}

public class StockPeriodGuardTests : StockPeriodGuardTestBase
{
    [PostgresFact]
    public async Task The_guard_is_one_function_and_nine_enabled_insert_triggers_each_bound_to_its_date_column()
    {
        await PrepareNegative();
        var rows = await Sql<string>(
            "SELECT string_agg(c.relname || '|' || t.tgname || '|' || t.tgenabled::text || '|' || t.tgtype::text || '|' || p.proname || '|' || replace(encode(t.tgargs, 'escape'), '\\000', ''), ';' ORDER BY c.relname) " +
            $"FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_proc p ON p.oid = t.tgfoid WHERE p.proname = '{G.FnGuard}'");
        // tgtype 7 = ROW (1) + BEFORE (2) + INSERT (4); tgargs is the argument followed by a NUL, removed by the query
        var expected = string.Join(';', All.OrderBy(g => g.Table, StringComparer.Ordinal).Select(g => $"{g.Table}|{g.Trigger}|O|7|{G.FnGuard}|{g.DateColumn}"));
        Assert.Equal(expected, rows);
        // not SECURITY DEFINER, no settings of its own (no search_path clause), plpgsql
        Assert.Equal("false|true|plpgsql", await Sql<string>(
            $"SELECT p.prosecdef::text || '|' || (p.proconfig IS NULL)::text || '|' || l.lanname FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang WHERE p.proname = '{G.FnGuard}'"));
    }

    [PostgresFact]
    public async Task The_entry_guard_is_one_function_and_two_enabled_insert_triggers_bound_to_the_cost_entry_column()
    {
        await PrepareNegative();
        var rows = await Sql<string>(
            "SELECT string_agg(c.relname || '|' || t.tgname || '|' || t.tgenabled::text || '|' || t.tgtype::text || '|' || p.proname || '|' || replace(encode(t.tgargs, 'escape'), '\\000', ''), ';' ORDER BY c.relname) " +
            $"FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_proc p ON p.oid = t.tgfoid WHERE p.proname = '{G.FnEntryGuard}'");
        Assert.Equal(string.Join(';', Refs.OrderBy(r => r.Table, StringComparer.Ordinal).Select(r => $"{r.Table}|{r.Trigger}|O|7|{G.FnEntryGuard}|{r.EntryColumn}")), rows);
        Assert.Equal("false|true|plpgsql", await Sql<string>(
            $"SELECT p.prosecdef::text || '|' || (p.proconfig IS NULL)::text || '|' || l.lanname FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang WHERE p.proname = '{G.FnEntryGuard}'"));
    }

    [PostgresTheory]
    [InlineData(SoftDate, SoftMessage)]
    [InlineData(FinalDate, FinalMessage)]
    [InlineData(MissingDate, MissingMessage)]
    public async Task A_row_dated_in_a_period_that_is_not_open_is_refused_in_every_guarded_table(string date, string message)
    {
        await Populate();
        await ClosePeriods();
        await using var owner = await Owner();
        foreach (var g in All)
        {
            var before = await Sql<long>($"SELECT count(*) FROM {g.Table}");
            AssertGuard(await Attempt(owner, [.. OnlyTheGuard(g), Clone(g, date)]), message, $"{g.Name} dated {date}");
            Assert.Equal(before, await Sql<long>($"SELECT count(*) FROM {g.Table}"));
        }
    }

    [PostgresFact]
    public async Task The_same_row_dated_in_an_open_period_passes_the_guard_in_every_guarded_table()
    {
        await Populate();
        await ClosePeriods();
        await using var owner = await Owner();
        foreach (var g in All)
        {
            // Past every BEFORE INSERT trigger, the unchanged copy of an existing row ends at its primary key: the guard let it through.
            var ex = await Attempt(owner, [.. OnlyTheGuard(g), Clone(g, OpenDate)]);
            Assert.True(ex is { SqlState: PostgresErrorCodes.UniqueViolation } && !FromGuard(ex), $"{g.Name}: expected to pass the guard and stop at the primary key, got {ex?.SqlState} '{ex?.MessageText}'");
        }
        // And a new, valid cost operation dated in an Open period is simply accepted.
        Assert.Null(await Attempt(owner, NewCostOperation("2026-03-15")));
    }

    [PostgresFact]
    public async Task Mutation_proof_without_its_guard_trigger_each_table_accepts_the_closed_period_row_as_far_as_the_primary_key()
    {
        await Populate();
        await ClosePeriods();
        await using var owner = await Owner();
        foreach (var g in All)
            foreach (var date in new[] { SoftDate, FinalDate, MissingDate })
            {
                // every user trigger off, the guard included: nothing else in the database refuses the date
                var without = await Attempt(owner, $"ALTER TABLE {g.Table} DISABLE TRIGGER USER", Clone(g, date));
                Assert.True(without is { SqlState: PostgresErrorCodes.UniqueViolation }, $"{g.Name} dated {date} without the guard: got {without?.SqlState} '{without?.MessageText}'");
                // only the guard off, every older trigger on: the refusal, if any, is never the guard's
                var others = await Attempt(owner, $"ALTER TABLE {g.Table} DISABLE TRIGGER {g.Trigger}", Clone(g, date));
                Assert.False(FromGuard(others), $"{g.Name}: the guard answered although its trigger is switched off");
            }
        // the switches were rolled back: the guard is in force again
        AssertGuard(await Attempt(owner, NewCostOperation(SoftDate)), SoftMessage, "cost operation after the proof");
    }

    [PostgresFact]
    public async Task A_new_cost_operation_and_a_new_cost_entry_follow_the_state_of_their_period_through_close_and_reopen()
    {
        await Populate();
        await using var owner = await Owner();
        var entry = All.Single(g => g.Table == EN.Table);
        Assert.Null(await Attempt(owner, NewCostOperation(SoftDate)));                               // January is still Open

        var january = await Period(1);
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, january.Id, january.Revision));
        AssertGuard(await Attempt(owner, NewCostOperation(SoftDate)), SoftMessage, "cost operation in a Soft Closed period");
        AssertGuard(await Attempt(owner, [.. OnlyTheGuard(entry), Clone(entry, SoftDate)]), SoftMessage, "cost entry in a Soft Closed period");

        var final = Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, january.Id, soft.Revision));
        Assert.Equal(FiscalPeriodState.FinalClosed, final.State);
        AssertGuard(await Attempt(owner, NewCostOperation(SoftDate)), FinalMessage, "cost operation in a Final Closed period");

        // A Soft Closed period that is reopened accepts postings again: the guard reads the state at the insert, it keeps nothing.
        var february = await Period(2);
        var closed = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, february.Id, february.Revision));
        AssertGuard(await Attempt(owner, NewCostOperation(FinalDate)), SoftMessage, "cost operation in Soft Closed February");
        Ok(await Calendar.ReopenAsync(Admin.Id, Company.Id, february.Id, closed.Revision));
        Assert.Null(await Attempt(owner, NewCostOperation(FinalDate)));
    }

    [PostgresFact]
    public async Task The_services_keep_working_in_open_periods_and_a_reversal_dated_in_an_open_period_of_an_original_in_a_closed_period_succeeds()
    {
        await PreparePosting();
        var receipt = await Approved(Receipt(W1, "2026-03-11", L(Avg, Pcs, 5)));
        var posting = Ok(await Post(receipt, Amounts(70m)));
        await PostJournal(posting);                                                                  // a posted journal may be reversed in a later period (3.7C)
        var late = await Approved(Receipt(W1, "2026-03-25", L(Avg, Pcs, 1)));                        // approved while March is still Open (the service refuses to approve into a closed period too)
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));

        // March is closed for stock: the service refuses to post there, and nothing is written
        var counts = await Counts();
        Fails(await Post(late, Amounts(10m)), ConfigStatus.Conflict, "fiscal_period_soft_closed");
        Assert.Equal(counts, await Counts());

        // ...but the original of March is reversed with a reversal dated in April (Open), exactly as before the guard
        var reversed = Ok(await Reverse(receipt, "2026-04-05", "returned to the supplier"));
        Assert.Equal("2026-04-05", reversed.Reversal!.EffectiveDate);
        Assert.Equal(0m, (await Pos(Avg)).Quantity);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {RV.Table} WHERE {RV.EffectiveDate} = '2026-04-05'"));

        // the same holds once March is Final Closed
        var again = await Approved(Receipt(W1, "2026-04-06", L(Fifo, Pcs, 2)));
        Ok(await Post(again, Amounts(20m)));
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
        Ok(await Reverse(again, "2026-04-07", "wrong item"));
        // and receipts, transfers and issues dated in the open period go on as before
        Ok(await Post(await Approved(Receipt(W1, "2026-04-08", L(Fifo, Pcs, 3))), Amounts(30m)));
        Ok(await Post(await Approved(Transfer(W1, W2, "2026-04-09", L(Fifo, Pcs, 1)))));
        Ok(await Post(await Approved(Issue(W1, "2026-04-10", L(Fifo, Pcs, 1)))));
        Assert.Equal((1m, 1m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo, W2)).Quantity));
    }

    [PostgresFact]
    public async Task A_final_closed_period_reopened_to_soft_closed_still_refuses_and_accepts_again_only_once_it_is_open()
    {
        await Populate();
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        var final = Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
        await using var owner = await Owner();
        AssertGuard(await Attempt(owner, NewCostOperation("2026-03-15")), FinalMessage, "Final Closed");

        // exceptional reopening: a request approved by a different user lands in Soft Closed, never straight in Open
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        var request = Ok(await Calendar.RequestPeriodReopenAsync(requester.Id, Company.Id, march.Id, final.Revision, new ReasonInput("Correction approved by finance")));
        AssertGuard(await Attempt(owner, NewCostOperation("2026-03-15")), FinalMessage, "Final Closed with a pending request");
        Ok(await Calendar.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, true));
        var reopened = await March();
        Assert.Equal(FiscalPeriodState.SoftClosed, reopened.State);
        AssertGuard(await Attempt(owner, NewCostOperation("2026-03-15")), SoftMessage, "reopened to Soft Closed");

        Ok(await Calendar.ReopenAsync(Admin.Id, Company.Id, reopened.Id, reopened.Revision));
        Assert.Null(await Attempt(owner, NewCostOperation("2026-03-15")));
    }

    [PostgresFact]
    public async Task The_services_keep_their_own_refusal_codes_for_adjustments_and_transfers_dated_in_a_closed_period()
    {
        await PrepareNegative();
        await Receive(Fifo2, 10m, 100m);
        var take = await ApprovedTake(W1, C(Fifo2, Pcs, 7m));
        var adjustment = Ok(await Generate(take));
        await Receive(Fifo, 5m, 50m);
        var transfer = await Approved(Transfer(W1, W2, "2026-03-20", L(Fifo, Pcs, 1)));
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        var counts = await Counts();

        Fails(await Adj.PostAsync(Admin.Id, Company.Id, adjustment.Id, null), ConfigStatus.Conflict, "fiscal_period_soft_closed");
        Fails(await Post(transfer), ConfigStatus.Conflict, "fiscal_period_soft_closed");
        Assert.Equal(counts, await Counts());
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
        Fails(await Adj.PostAsync(Admin.Id, Company.Id, adjustment.Id, null), ConfigStatus.Conflict, "fiscal_period_final_closed");
        Fails(await Post(transfer), ConfigStatus.Conflict, "fiscal_period_final_closed");
        Assert.Equal(counts, await Counts());
    }

    // ---------------------------------------------------------------- concurrency: a period being closed

    [PostgresFact]
    public async Task A_close_waits_for_an_insert_in_flight_and_the_period_is_then_closed_for_the_next_insert()
    {
        await Populate();
        await using var writer = await Owner();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var ins = new NpgsqlCommand(NewCostOperation("2026-03-15"), writer, tx)) await ins.ExecuteNonQueryAsync();     // the guard holds the period row FOR SHARE

        var march = await March();
        var close = Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision);
        Assert.True(await Blocked(), "the close did not wait for the insert in flight");
        Assert.False(close.IsCompleted);

        // a second writer into the same Open period is not held up by the first (share locks do not conflict), nor by the close that is waiting for the row
        await using (var second = await Owner())
        await using (var tx2 = await second.BeginTransactionAsync())
        {
            await using (var q = new NpgsqlCommand("SET LOCAL lock_timeout = '3s'", second, tx2)) await q.ExecuteNonQueryAsync();
            await using (var ins2 = new NpgsqlCommand(NewCostOperation("2026-03-16"), second, tx2)) Assert.Equal(1, await ins2.ExecuteNonQueryAsync());
            await tx2.RollbackAsync();
        }
        Assert.False(close.IsCompleted);

        await tx.RollbackAsync();
        Ok(await close);
        await using var later = await Owner();
        AssertGuard(await Attempt(later, NewCostOperation("2026-03-15")), SoftMessage, "insert after the close");
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_insert_waits_for_a_close_in_flight_and_follows_its_outcome(bool closeCommits)
    {
        await Populate();
        var march = await March();
        await using var closer = await Owner();
        await using var closing = await closer.BeginTransactionAsync();
        // the statement of the calendar service, uncommitted: the period row is locked for update
        await using (var upd = new NpgsqlCommand(
            $"UPDATE {FP.Table} SET {FP.Status} = 2, {FP.StatusChangedAtUtc} = now(), {FP.StatusChangedBy} = '{Admin.Id}', {FP.Revision} = {FP.Revision} + 1 WHERE {FP.Id} = '{march.Id}'", closer, closing))
            Assert.Equal(1, await upd.ExecuteNonQueryAsync());

        await using var writer = await Owner();
        var insert = Attempt(writer, NewCostOperation("2026-03-15"));
        Assert.True(await Blocked(), "the insert did not wait for the close in flight");
        Assert.False(insert.IsCompleted);

        if (closeCommits) await closing.CommitAsync(); else await closing.RollbackAsync();
        var outcome = await insert;
        if (closeCommits) AssertGuard(outcome, SoftMessage, "insert that waited for a committed close");       // it sees the closed state, never the one it started with
        else Assert.Null(outcome);
    }

    [PostgresFact]
    public async Task A_posting_that_already_holds_the_period_lock_is_not_blocked_by_a_queued_close_and_nothing_deadlocks()
    {
        await Populate();
        var march = await March();
        await using var poster = await Owner();
        await using var tx = await poster.BeginTransactionAsync();
        // what every posting service does before its first insert: FiscalPeriodGate locks the period row FOR SHARE
        var gate = await FiscalPeriodGate.CheckAsync(poster, tx, new DateOnly(2026, 3, 15), false);
        Assert.True(gate.Allowed);

        var close = Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision);
        Assert.True(await Blocked(), "the close did not wait for the posting");

        // the guard asks for the same share lock again while the close is queued behind it: it must return at once
        await using (var t = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", poster, tx)) await t.ExecuteNonQueryAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using (var ins = new NpgsqlCommand(NewCostOperation("2026-03-15"), poster, tx)) Assert.Equal(1, await ins.ExecuteNonQueryAsync());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"the insert waited {watch.Elapsed}");
        Assert.False(close.IsCompleted);                                                             // the close is still waiting for the posting, not the other way round

        await tx.RollbackAsync();
        Ok(await close);
    }

    [PostgresFact]
    public async Task A_repeatable_read_transaction_that_started_before_the_close_never_gets_its_insert_accepted()
    {
        await Populate();
        var march = await March();
        await using var writer = await Owner();
        await using var tx = await writer.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using (var snap = new NpgsqlCommand($"SELECT count(*) FROM {FP.Table}", writer, tx)) await snap.ExecuteScalarAsync();     // the snapshot is taken: March is Open in it
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));

        var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var ins = new NpgsqlCommand(NewCostOperation("2026-03-15"), writer, tx); await ins.ExecuteNonQueryAsync(); });
        // either PostgreSQL refuses to lock a row changed after the snapshot (40001), or the guard sees the closed state: never an accepted row
        Assert.True(ex.SqlState == PostgresErrorCodes.SerializationFailure || (ex.SqlState == "P0001" && ex.MessageText == SoftMessage), $"{ex.SqlState} {ex.MessageText}");
        await tx.RollbackAsync();
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CO.Table} WHERE {CO.Key} LIKE 'raw-%'"));
    }

    // ---------------------------------------------------------------- upgrade and catalog

    private const string Catalog =
        "SELECT coalesce(string_agg(x, E'\\n' ORDER BY x), '') FROM (" +
        "SELECT 'function|' || p.oid::regprocedure::text || '|' || md5(pg_get_functiondef(p.oid)) FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.prokind = 'f' " +
        "UNION ALL SELECT 'trigger|' || t.tgrelid::regclass::text || '|' || t.tgname || '|' || t.tgenabled::text || '|' || pg_get_triggerdef(t.oid) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE NOT t.tgisinternal AND c.relnamespace = 'public'::regnamespace " +
        "UNION ALL SELECT 'constraint|' || c.conrelid::regclass::text || '|' || c.conname || '|' || pg_get_constraintdef(c.oid) FROM pg_constraint c WHERE c.connamespace = 'public'::regnamespace " +
        "UNION ALL SELECT 'index|' || indexname || '|' || indexdef FROM pg_indexes WHERE schemaname = 'public' " +
        "UNION ALL SELECT 'column|' || table_name || '|' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '') FROM information_schema.columns WHERE table_schema = 'public' " +
        "UNION ALL SELECT 'grant|' || table_name || '|' || grantee || '|' || privilege_type FROM information_schema.role_table_grants WHERE table_schema = 'public') s(x)";

    [PostgresFact]
    public async Task The_migration_only_adds_its_two_functions_and_its_eleven_triggers_and_leaves_every_existing_object_byte_identical()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var mine = all.Single(s => s.Sql.Contains($"CREATE FUNCTION {G.FnGuard}()", StringComparison.Ordinal));
        var legacy = await ReadyCompany([.. all.Where(s => s.Version < mine.Version)]);
        var before = (await InCompany<string>(legacy.PhysicalDatabaseName, Catalog)).Split('\n');

        var result = await new MigrationRunner(Options.BuildCompanyAdminConnectionString(legacy.PhysicalDatabaseName)).ApplyAsync([.. all.Where(s => s.Version <= mine.Version)]);
        Assert.Equal([mine.Version], result.Applied);

        var after = (await InCompany<string>(legacy.PhysicalDatabaseName, Catalog)).Split('\n');
        Assert.Empty(before.Except(after));                                                           // nothing that existed changed or disappeared: functions, triggers, constraints, indexes, columns, grants
        var added = after.Except(before).ToList();
        Assert.Equal(13, added.Count);
        Assert.Single(added, a => a.StartsWith($"function|{G.FnGuard}()|", StringComparison.Ordinal));
        Assert.Single(added, a => a.StartsWith($"function|{G.FnEntryGuard}()|", StringComparison.Ordinal));
        foreach (var r in Refs)
            Assert.Single(added, a => a.StartsWith($"trigger|{r.Table}|{r.Trigger}|O|", StringComparison.Ordinal)
                && a.EndsWith($"BEFORE INSERT ON public.{r.Table} FOR EACH ROW EXECUTE FUNCTION {G.FnEntryGuard}('{r.EntryColumn}')", StringComparison.Ordinal));
        foreach (var g in All)
            Assert.Single(added, a => a.StartsWith($"trigger|{g.Table}|{g.Trigger}|O|", StringComparison.Ordinal)
                && a.EndsWith($"BEFORE INSERT ON public.{g.Table} FOR EACH ROW EXECUTE FUNCTION {G.FnGuard}('{g.DateColumn}')", StringComparison.Ordinal));
        Assert.True(before.Length > 500, "the catalog snapshot is implausibly small");
    }

    [PostgresFact]
    public async Task An_upgrade_leaves_historical_rows_of_periods_that_are_now_closed_untouched_and_puts_the_guard_in_force()
    {
        await Populate();
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));              // every stock row of this company now lies in a Final Closed period
        var all = MigrationScript.LoadEmbeddedCompany();
        var mine = all.Single(s => s.Sql.Contains($"CREATE FUNCTION {G.FnGuard}()", StringComparison.Ordinal));
        // the guard need not be the newest migration: only its own journal row is removed below and re-applied; later migrations stay applied (the cash migration 0035 follows it)

        async Task<string> Fingerprint() => string.Join(';', await Task.WhenAll(All.Select(g => Sql<string>($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {g.Table} t"))));
        var before = await Fingerprint();

        // Back to the database as it was before this migration, history included: the migration created exactly these objects and its journal row.
        foreach (var g in All) await Sql($"DROP TRIGGER {g.Trigger} ON {g.Table}");
        foreach (var r in Refs) await Sql($"DROP TRIGGER {r.Trigger} ON {r.Table}");
        await Sql($"DROP FUNCTION {G.FnGuard}()");
        await Sql($"DROP FUNCTION {G.FnEntryGuard}()");
        await Sql($"DELETE FROM {SystemSchema.MigrationJournalTable} WHERE version = {mine.Version}");
        await using var owner = await Owner();
        var entry = All.Single(g => g.Table == EN.Table);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Attempt(owner, $"ALTER TABLE {entry.Table} DISABLE TRIGGER USER", Clone(entry, "2026-03-10")))?.SqlState);   // the old state: nothing refuses the closed date

        var result = await new MigrationRunner(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)).ApplyAsync(all);
        Assert.Equal([mine.Version], result.Applied);                                                 // applied over rows in a Final Closed period: the migration reads and validates no existing row
        Assert.Equal(before, await Fingerprint());
        foreach (var g in All) AssertGuard(await Attempt(owner, [.. OnlyTheGuard(g), Clone(g, "2026-03-10")]), FinalMessage, $"{g.Name} after the upgrade");
    }

    // ---------------------------------------------------------------- cost layers and consumptions (dated by their cost entry)

    private async Task CloseMarch(bool final)
    {
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        if (final) Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
    }

    [PostgresFact]
    public async Task A_new_layer_or_consumption_follows_the_period_of_its_cost_entry_and_is_refused_once_that_period_is_closed()
    {
        await Populate();
        await using var owner = await Owner();
        foreach (var r in Refs) Assert.True(await Sql<long>($"SELECT count(*) FROM {r.Table}") > 0, $"{r.Name}: no row to copy");
        foreach (var r in Refs) Assert.Null(await Attempt(owner, CloneRef(r)));                      // the entry's period (March) is Open: accepted

        await CloseMarch(final: false);
        foreach (var r in Refs)
        {
            var before = await Sql<long>($"SELECT count(*) FROM {r.Table}");
            AssertGuard(await Attempt(owner, [.. OnlyTheEntryGuard(r), CloneRef(r)]), SoftMessage, $"{r.Name} of an entry in a Soft Closed period", G.FnEntryGuard);
            AssertGuard(await Attempt(owner, CloneRef(r)), SoftMessage, $"{r.Name} of an entry in a Soft Closed period, every trigger on", G.FnEntryGuard);
            Assert.Equal(before, await Sql<long>($"SELECT count(*) FROM {r.Table}"));
        }
        var march = await March();
        var final = Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        foreach (var r in Refs) AssertGuard(await Attempt(owner, CloneRef(r)), FinalMessage, $"{r.Name} of an entry in a Final Closed period", G.FnEntryGuard);

        // the exceptional reopening lands in Soft Closed (still refused), the routine reopening makes it Open (accepted again)
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        var request = Ok(await Calendar.RequestPeriodReopenAsync(requester.Id, Company.Id, final.Id, final.Revision, new ReasonInput("Correction approved by finance")));
        Ok(await Calendar.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, true));
        var reopened = await March();
        foreach (var r in Refs) AssertGuard(await Attempt(owner, CloneRef(r)), SoftMessage, $"{r.Name} after the exceptional reopening", G.FnEntryGuard);
        Ok(await Calendar.ReopenAsync(Admin.Id, Company.Id, reopened.Id, reopened.Revision));
        foreach (var r in Refs) Assert.Null(await Attempt(owner, CloneRef(r)));
    }

    [PostgresFact]
    public async Task Mutation_proof_without_the_entry_guard_trigger_layers_and_consumptions_of_a_closed_entry_are_accepted()
    {
        await Populate();
        await CloseMarch(final: true);
        await using var owner = await Owner();
        foreach (var r in Refs)
        {
            Assert.Null(await Attempt(owner, $"ALTER TABLE {r.Table} DISABLE TRIGGER {r.Trigger}", CloneRef(r)));       // the old state: the closed period is not noticed
            Assert.Null(await Attempt(owner, $"ALTER TABLE {r.Table} DISABLE TRIGGER USER", CloneRef(r)));
            AssertGuard(await Attempt(owner, CloneRef(r)), FinalMessage, $"{r.Name} with the guard back on", G.FnEntryGuard);
        }
    }

    [PostgresFact]
    public async Task A_layer_or_consumption_without_a_cost_entry_still_ends_at_its_own_constraint_and_an_issue_in_an_open_period_may_draw_on_a_layer_of_a_closed_one()
    {
        await Populate();
        await using var owner = await Owner();
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Attempt(owner, $"INSERT INTO {CS.Table} ({CS.EntryId}, {CS.LayerId}, {CS.Quantity}, {CS.Amount}) SELECT 9223372036854775807, {LY.Id}, 1, 1 FROM {LY.Table} LIMIT 1"))?.SqlState);
        Assert.Equal(PostgresErrorCodes.NotNullViolation, (await Attempt(owner, $"INSERT INTO {CS.Table} ({CS.EntryId}, {CS.LayerId}, {CS.Quantity}, {CS.Amount}) SELECT NULL, {LY.Id}, 1, 1 FROM {LY.Table} LIMIT 1"))?.SqlState);
        Assert.Equal(PostgresErrorCodes.NotNullViolation, (await Attempt(owner, $"INSERT INTO {LY.Table} SELECT (jsonb_populate_record(NULL::{LY.Table}, to_jsonb(s) || jsonb_build_object('{LY.Id}', gen_random_uuid(), '{LY.Seq}', 999999999, '{LY.EntryId}', NULL))).* FROM {LY.Table} s LIMIT 1"))?.SqlState);
        // The engine draws an issue on a layer received in an earlier period and records it with its own new entry: the consumption is dated by that entry (Open), never by the old layer.
        await CloseMarch(final: true);
        var issue = await Approved(Issue(W1, "2026-04-10", L(Fifo2, Pcs, 2)));
        Ok(await Post(issue));
        Assert.True(await Sql<long>($"SELECT count(*) FROM {CS.Table} c JOIN {EN.Table} e ON e.{EN.Id} = c.{CS.EntryId} JOIN {LY.Table} l ON l.{LY.Id} = c.{CS.LayerId} JOIN {EN.Table} le ON le.{EN.Id} = l.{LY.EntryId} " +
            $"WHERE e.{EN.EffectiveDate} = '2026-04-10' AND le.{EN.EffectiveDate} < '2026-04-01'") > 0, "no consumption of a layer of the closed March was written by the April issue");
    }
}

/// <summary>The same guard for the company's own runtime role (a real, distinct PostgreSQL principal; role-capable disposable cluster only).</summary>
public class StockPeriodGuardRuntimeRoleTests : StockPeriodGuardTestBase
{
    protected override bool RolesRequired => true;

    private async Task<NpgsqlConnection> AsRuntime()
    {
        await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        var credential = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(Company.Id))!;
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(Company.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // On these two tables an OLDER insert trigger (0020) fires before the guard (triggers fire in name order) and already requires the row's date to equal
    // the date of its stock posting, so a copy with another date is refused there first. The runtime role cannot switch a trigger off to isolate the guard.
    private static bool OlderTriggerFirst(Guarded g) => g.Table == NO.Table || g.Table == NX.Table;

    [PostgresRoleFact]
    public async Task The_runtime_role_is_refused_in_every_guarded_table_and_can_still_write_into_an_open_period()
    {
        await Populate();
        await ClosePeriods();
        await using var runtime = await AsRuntime();
        Assert.NotEqual(await Sql<string>("SELECT current_user::text"), (string)(await new NpgsqlCommand("SELECT current_user::text", runtime).ExecuteScalarAsync())!);
        foreach (var g in All)
        {
            foreach (var (date, message) in new[] { (SoftDate, SoftMessage), (FinalDate, FinalMessage), (MissingDate, MissingMessage) })
            {
                var ex = await Attempt(runtime, Clone(g, date));
                if (OlderTriggerFirst(g)) Assert.True(ex is { SqlState: "P0001" }, $"{g.Name} dated {date}: {ex?.SqlState} {ex?.MessageText}");
                else AssertGuard(ex, message, $"{g.Name} dated {date} as the runtime role");
            }
            // An Open date passes the guard with the runtime role's own rights (EXECUTE on both functions, the share lock on the period row): the copy ends at the primary key.
            if (!OlderTriggerFirst(g))
            {
                var open = await Attempt(runtime, Clone(g, OpenDate));
                Assert.True(open is { SqlState: PostgresErrorCodes.UniqueViolation }, $"{g.Name} in an open period as the runtime role: {open?.SqlState} {open?.MessageText}");
            }
        }
        Assert.Null(await Attempt(runtime, NewCostOperation("2026-03-15")));
        AssertGuard(await Attempt(runtime, NewCostOperation(SoftDate)), SoftMessage, "new cost operation as the runtime role");
    }

    [PostgresRoleFact]
    public async Task With_the_runtime_roles_own_rights_layers_and_consumptions_follow_the_period_of_their_cost_entry()
    {
        await Populate();
        await using var runtime = await AsRuntime();
        foreach (var r in Refs) Assert.Null(await Attempt(runtime, CloneRef(r)));                    // Open: accepted with the role's own insert right and its right to read the entry
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        foreach (var r in Refs) AssertGuard(await Attempt(runtime, CloneRef(r)), SoftMessage, $"{r.Name} as the runtime role, Soft Closed", G.FnEntryGuard);
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
        foreach (var r in Refs) AssertGuard(await Attempt(runtime, CloneRef(r)), FinalMessage, $"{r.Name} as the runtime role, Final Closed", G.FnEntryGuard);
        foreach (var r in Refs)
            Assert.Equal("42501", (await Attempt(runtime, $"ALTER TABLE {r.Table} DISABLE TRIGGER {r.Trigger}"))?.SqlState);   // the role cannot switch the trigger off
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_insert_in_flight_makes_a_close_wait_and_the_next_insert_is_refused()
    {
        await Populate();
        var march = await March();
        await using var runtime = await AsRuntime();
        // an insert in flight (share lock taken with the runtime role's own rights) makes the close wait, and the next insert is refused
        await using (var tx = await runtime.BeginTransactionAsync())
        {
            await using (var ins = new NpgsqlCommand(NewCostOperation("2026-03-15"), runtime, tx)) await ins.ExecuteNonQueryAsync();
            var close = Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision);
            Assert.True(await Blocked(), "the close did not wait for the runtime role's insert");
            await tx.RollbackAsync();
            Ok(await Within(close, "the close"));
        }
        AssertGuard(await Attempt(runtime, NewCostOperation("2026-03-15")), SoftMessage, "runtime insert after the close");
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_waits_for_a_close_in_flight_and_follows_its_outcome()
    {
        await Populate();
        await using var runtime = await AsRuntime();
        var again = await March();
        await using var closer = await Owner();
        await using var closing = await closer.BeginTransactionAsync();
        await using (var upd = new NpgsqlCommand($"UPDATE {FP.Table} SET {FP.Status} = 2, {FP.StatusChangedAtUtc} = now(), {FP.StatusChangedBy} = '{Admin.Id}', {FP.Revision} = {FP.Revision} + 1 WHERE {FP.Id} = '{again.Id}'", closer, closing))
            Assert.Equal(1, await Within(upd.ExecuteNonQueryAsync(), "the close statement"));
        var insert = Attempt(runtime, NewCostOperation("2026-03-16"));
        Assert.True(await Blocked(), "the runtime role's insert did not wait for the close in flight: " + (insert.IsCompleted ? (await insert)?.MessageText ?? "accepted" : "still pending"));
        await closing.CommitAsync();
        AssertGuard(await Within(insert, "the runtime insert"), SoftMessage, "runtime insert that waited for a committed close");
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_cannot_switch_off_drop_or_replace_the_guard()
    {
        await Populate();
        await using var runtime = await AsRuntime();
        foreach (var g in All)
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Attempt(runtime, $"ALTER TABLE {g.Table} DISABLE TRIGGER {g.Trigger}"))?.SqlState);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Attempt(runtime, $"ALTER TABLE {g.Table} DISABLE TRIGGER USER"))?.SqlState);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Attempt(runtime, $"DROP TRIGGER {g.Trigger} ON {g.Table}"))?.SqlState);
        }
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Attempt(runtime, $"CREATE OR REPLACE FUNCTION {G.FnGuard}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$"))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Attempt(runtime, $"DROP FUNCTION {G.FnGuard}()"))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Attempt(runtime, $"CREATE OR REPLACE FUNCTION {CompanySchema.FiscalCalendar.PostableFunction}(posting_date date, allow_soft_closed boolean) RETURNS uuid LANGUAGE sql AS $$ SELECT NULL::uuid $$"))?.SqlState);
    }
}
