using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Release 1 accounting acceptance (gap analysis items 4 and 5): the fiscal-period lock on the inventory sources that the per-module suites covered only partly. A stock posting racing a period close has exactly
/// one winner (never a posting inside a closed period, never a close over an unposted movement), shown both as a race and deterministically with the posting held open; and a stocktake is never approved inside a
/// soft- or final-closed period. Item 6 (numbering rollback of stocktake and adjustment) is NOT here: it exposed an unmapped refusal and was reported instead. Only merged, approved behaviour. Disposable cluster only.
/// </summary>
public class InventoryPeriodInvariantTests : StockAdjustmentTestBase
{
    private async Task<StocktakeView> SubmittedTake(string date = CountDate)
    {
        var d = Ok(await Takes.CreateAsync(Admin.Id, Company.Id, new StocktakeInput(W1.Id, date, "COUNT", null, [C(Fifo, Pcs, 1m)])));
        return Ok(await Takes.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
    }

    private async Task<Guid> TakeApprover() => (await Member([PermissionCatalog.Codes.StocktakeView, PermissionCatalog.Codes.StocktakeApprove], ScopeKind.Company, null)).User.Id;

    private Task<long> Issued(string docType) => Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = '{docType}'");
    private Task<long> Counters(string docType) => Sql<long>($"SELECT count(*) FROM {NC.Table} WHERE {NC.DocType} = '{docType}'");


    [PostgresFact]
    public async Task A_stock_posting_racing_a_period_close_has_exactly_one_winner_and_never_leaves_a_movement_in_a_closed_period()
    {
        await PrepareAdjustments();
        for (var round = 0; round < 8; round++)
        {
            var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1)));
            var march = await March();
            Assert.Equal(FiscalPeriodState.Open, march.State);
            var post = Post(doc, Amounts(10m));
            var close = CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision);                         // the REAL close probes, as the application registers them
            await Task.WhenAll(post, close);
            var p = await post; var c = await close;
            Assert.True(p.IsOk != c.IsOk, $"round {round}: posting {p.Status}/{p.Code}, close {c.Status}/{c.Code}");                // exactly one winner
            if (p.IsOk)
            {
                // the posting won: its unposted journal and stock effect block the close, and March stays open
                // why the close is blocked: the posting left a DRAFT journal dated in March (the pending-journal probe) and a stock effect the ledger does not show yet (the valuation probe)
                Assert.Equal(JournalStatus.Draft, (await Journal.GetAsync(Company.Id, p.Value!.JournalEntryId!.Value))!.Header.Status);
                Assert.Equal((ConfigStatus.Conflict, "period_close_blocked"), (c.Status, c.Code));
                Assert.Equal(FiscalPeriodState.Open, (await March()).State);
                await PostJournal(p.Value!);                                                                                   // complete it, so the next round starts clean
            }
            else
            {
                // the close won: the posting is refused by the period gate and nothing of it exists
                Assert.Equal((ConfigStatus.Conflict, PostingPeriodCheck.SoftClosed), (p.Status, p.Code));
                Assert.Null(await Posting.GetAsync(Company.Id, doc.Id));
                Ok(await CloseCalendar.ReopenAsync(Admin.Id, Company.Id, c.Value!.Id, c.Value.Revision));                     // the routine reopening of a soft close
            }
        }
        // whatever the interleavings, the stock value equals the inventory ledger and the period can now close
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        var final = await March();
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, final.Id, final.Revision));
    }

    /// <summary>Waits (polling, bounded) until exactly <paramref name="n"/> backends of this company database wait for a lock; fails if that never happens.</summary>
    private async Task WaitForLockWaiters(int n)
    {
        for (var i = 0; i < 600; i++)
        {
            if (await Sql<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") >= n) return;
            await Task.Delay(50);
        }
        Assert.Fail($"expected {n} backend(s) waiting for a lock in the company database");
    }

    /// <summary>
    /// The deterministic form of the race (review M1): the posting is held open AFTER it took the period gate (FOR SHARE) by a lock on the cost position it must update next; the close is started and must WAIT for the
    /// period row (proven through pg_stat_activity, no timing assumption); once the posting commits, the close sees its draft journal and refuses. If the posting did not hold the period, the close would not wait
    /// and would succeed over an uncommitted movement, and this test would fail. No lock cycle exists: the holder locks only the position, the posting holds the period and waits for the position, the close holds
    /// only the calendar lock and waits for the period, so 40P01 cannot occur; the exact outcomes below would fail if it did.
    /// </summary>
    [PostgresFact]
    public async Task A_close_waits_for_a_stock_posting_that_holds_the_period_and_then_refuses_because_of_it()
    {
        await PrepareAdjustments();
        var first = Ok(await Post(await Approved(Receipt(W1, "2026-03-05", L(Fifo, Pcs, 1))), Amounts(10m)));
        await PostJournal(first);                                                                                              // the position exists and the ledger agrees
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1)));
        var march = await March();
        Assert.Equal(FiscalPeriodState.Open, march.State);

        await using var holder = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lk = new NpgsqlCommand($"SELECT {PO.Id} FROM {PO.Table} WHERE {PO.ItemId} = @i AND {PO.WarehouseId} = @w FOR UPDATE", holder, tx))
        {
            lk.Parameters.AddWithValue("i", Fifo.Id); lk.Parameters.AddWithValue("w", W1.Id);
            Assert.NotNull(await lk.ExecuteScalarAsync());
        }
        var post = Task.Run(() => Post(doc, Amounts(10m)));
        await WaitForLockWaiters(1);                                                                                           // the posting passed the period gate and waits for the position
        Assert.False(post.IsCompleted);
        var close = Task.Run(() => CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        await WaitForLockWaiters(2);                                                                                           // the close waits for the period row the posting holds
        Assert.False(close.IsCompleted);
        await tx.RollbackAsync();                                                                                              // release the position: the posting completes and commits

        var p = Ok(await post);
        var c = await close;
        Assert.Equal(JournalStatus.Draft, (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!.Header.Status);
        Assert.Equal((ConfigStatus.Conflict, "period_close_blocked"), (c.Status, c.Code));                                     // the close saw the committed posting
        Assert.Equal(FiscalPeriodState.Open, (await March()).State);
    }

    [PostgresFact]
    public async Task A_stocktake_is_never_approved_in_a_soft_or_final_closed_period_and_the_refusal_issues_no_number()
    {
        await PrepareAdjustments();
        var approver = await TakeApprover();
        var take = await SubmittedTake();                                                                                      // counted on 2026-03-11, submitted while March is open
        async Task AssertUntouched()
        {
            var after = (await Takes.GetAsync(Company.Id, take.Id))!;
            Assert.Equal((take.Status, take.Revision, (string?)null), (after.Status, after.Revision, after.Number));
            Assert.Equal((0L, 0L), (await Issued(DocumentTypes.Stocktake), await Counters(DocumentTypes.Stocktake)));
        }
        var march = await March();
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        Fails(await Takes.ApproveAsync(approver, Company.Id, take.Id, take.Revision), ConfigStatus.Conflict, PostingPeriodCheck.SoftClosed);
        await AssertUntouched();
        Ok(await Calendar.FinalCloseAsync(Admin.Id, Company.Id, march.Id, soft.Revision));
        Fails(await Takes.ApproveAsync(approver, Company.Id, take.Id, take.Revision), ConfigStatus.Conflict, PostingPeriodCheck.FinalClosed);
        await AssertUntouched();
        // control: a count in the open April is approved and receives the FIRST number
        Assert.Equal(FiscalPeriodState.Open, (await Calendar.FindPeriodAsync(Company.Id, new DateOnly(2026, 4, 10)))!.State);
        var april = await SubmittedTake("2026-04-10");
        Assert.Equal("STK-2026-000001", Ok(await Takes.ApproveAsync(approver, Company.Id, april.Id, april.Revision)).Number);
    }
}
