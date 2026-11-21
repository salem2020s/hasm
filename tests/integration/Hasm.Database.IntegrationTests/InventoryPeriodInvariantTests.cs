using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using FYr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using NC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using SA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustment;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Release 1 accounting acceptance (gap analysis items 4, 5 and 6): the fiscal-period lock and gapless numbering on the inventory sources that the per-module suites covered only partly. A stock posting racing a period
/// close has exactly one winner (never a posting inside a closed period, never a close over an unposted movement); a stocktake is never approved inside a soft- or final-closed period; and a stocktake approval or an
/// adjustment generation that fails after its number was issued consumes no number. Only merged, approved behaviour; business outcomes only. Disposable cluster only.
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
    private Task<long> LastValue(string docType) => Sql<long>($"SELECT coalesce(max({NC.LastValue}), 0) FROM {NC.Table} WHERE {NC.DocType} = '{docType}'");

    /// <summary>A foreign row already holds the first number of <paramref name="docType"/>: the next issue fails AFTER the counter moved, so the whole operation must roll back.</summary>
    private async Task OccupyFirstNumber(string docType, string number)
    {
        var year = await Sql<Guid>($"SELECT {FYr.Id} FROM {FYr.Table} LIMIT 1");
        await Sql($"INSERT INTO {NI.Table} ({NI.DocType}, {NI.Number}, {NI.BranchId}, {NI.FiscalYearId}, {NI.Value}, {NI.DocumentId}, {NI.IssuedAtUtc}, {NI.IssuedBy}) " +
                  $"VALUES ('{docType}', '{number}', '{(await Main()).Id}', '{year}', 77, '{Guid.NewGuid()}', now(), '{Admin.Id}')");
    }

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
        var april = await SubmittedTake("2026-04-10");
        Assert.Equal("STK-2026-000001", Ok(await Takes.ApproveAsync(approver, Company.Id, april.Id, april.Revision)).Number);
    }

    [PostgresFact]
    public async Task A_stocktake_approval_or_an_adjustment_generation_that_fails_after_the_number_was_issued_consumes_no_number()
    {
        await PrepareAdjustments();
        var approver = await TakeApprover();

        // adjustment generation from an approved stocktake: the counter move and the issue roll back with the refused generation, and no adjustment exists
        var approved = await ApprovedTake(W1, C(Fifo, Pcs, 1m));                                                               // takes STK-2026-000001 normally
        Assert.Equal("STK-2026-000001", approved.Number);
        await OccupyFirstNumber(DocumentTypes.StockAdjustment, "SA-2026-000001");
        var gen = await Generate(approved);
        Assert.False(gen.IsOk, "a generation whose number is already taken must fail");
        Assert.Equal((1L, 0L, 0L), (await Issued(DocumentTypes.StockAdjustment), await Counters(DocumentTypes.StockAdjustment), await Sql<long>($"SELECT count(*) FROM {SA.Table}")));

        // stocktake approval: the next stocktake number (000002) is already taken; the refused approval leaves the counter at 1 and the stocktake submitted
        var take = await SubmittedTake();
        await OccupyFirstNumber(DocumentTypes.Stocktake, "STK-2026-000002");
        var refused = await Takes.ApproveAsync(approver, Company.Id, take.Id, take.Revision);
        Assert.False(refused.IsOk, "an approval whose number is already taken must fail");
        var after = (await Takes.GetAsync(Company.Id, take.Id))!;
        Assert.Equal((take.Status, take.Revision, (string?)null), (after.Status, after.Revision, after.Number));
        Assert.Equal((2L, 1L), (await Issued(DocumentTypes.Stocktake), await LastValue(DocumentTypes.Stocktake)));            // the earlier approval and the foreign row; the counter did not move
    }
}
