using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.7B hardening after the independent review: per-account journals, mixed zero values, replay without a body, stable refusal codes, exact precision, summed transfer lines. Disposable cluster only.</summary>
public class StockPostingHardeningTests : StockPostingTestBase
{
    [PostgresFact]
    public async Task Items_of_different_groups_are_booked_to_their_own_inventory_accounts_and_the_journal_balances_per_account()
    {
        await PreparePosting();
        var chart = new ChartOfAccountsService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        var second = Ok(await chart.CreateAsync(Admin.Id, Company.Id, new AccountInput("1302", null, "حساب", "Raw stock", "asset", null, false, null, null)));
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 1, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [new InventoryAccountingGroupInput(Fifo.GroupId, second.Id)], null)));
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4)));
        var r = Ok(await Post(doc, Amounts(60m, 40m)));
        var j = (await Journal.GetAsync(Company.Id, r.JournalEntryId!.Value))!;
        Assert.True(j.Balanced);
        Assert.Equal(new[] { (second.Id, 60m, 0m), (StockAcc.Id, 40m, 0m), (GoodsAcc.Id, 0m, 100m) }.OrderBy(x => x.Item1), j.Lines.Select(l => (l.AccountId, l.BaseDebit, l.BaseCredit)).OrderBy(x => x.AccountId));
        Assert.Equal((await Acc.GetAsync(Company.Id)).Version!.Id, r.AccountingVersionId);                                      // the stored version is the one that was in force
    }

    [PostgresFact]
    public async Task A_receipt_with_a_free_line_posts_every_line_but_books_only_the_valued_ones()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4)));
        var r = Ok(await Post(doc, Amounts(100m, 0m)));
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal((100m, 4m, 0m), (r.TotalValue, (await Pos(Avg)).Quantity, (await Pos(Avg)).Value));                        // the free goods are in stock at zero value
        var j = (await Journal.GetAsync(Company.Id, r.JournalEntryId!.Value))!;
        Assert.Equal(2, j.Lines.Count);                                                                                           // one debit, one credit: no zero-amount line
    }

    [PostgresFact]
    public async Task A_retry_without_a_body_returns_the_original_posting_and_wrong_amounts_still_conflict()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var first = Ok(await Post(doc, Amounts(50m)));
        var retry = Ok(await Post(doc, null));
        Assert.True(retry.Replayed);
        Assert.Equal(first.Id, retry.Id);
        Fails(await Post(doc, Amounts(49m)), ConfigStatus.Conflict, "already_posted");
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SP.Table}"));
    }

    [PostgresFact]
    public async Task Closed_periods_are_refused_with_their_stable_codes_and_an_amount_with_too_many_decimals_is_a_validation_error()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-04-05", L(Fifo, Pcs, 5)));
        Invalid(await Post(doc, Amounts(10.005m)), "lines", "amount_precision_exceeded");
        Invalid(await Post(doc, Amounts(2_000_000_000_000_000_000m)), "lines", "invalid_amount");
        var p = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(x => x.StartDate == new DateOnly(2026, 4, 1));
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, p.Id, p.Revision));
        Fails(await Post(doc, Amounts(10m)), ConfigStatus.Conflict, "fiscal_period_soft_closed");
        Assert.Equal((0L, 0L, 0L, 0L), await Counts());
    }

    [PostgresFact]
    public async Task Equal_items_of_a_transfer_are_summed_and_an_issue_that_costs_nothing_needs_no_journal()
    {
        await PreparePosting();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10))), Amounts(0m)));                                  // free goods: zero value
        var transfer = await Approved(Transfer(W1, W2, "2026-03-11", L(Avg, Pcs, 2), L(Avg, Pcs, 3)));
        var t = Ok(await Post(transfer));
        Assert.Equal(((int)StockAccountingState.NotRequired, 5m, 5m), (t.AccountingState, (await Pos(Avg, W2)).Quantity, t.Lines.Sum(l => l.BaseQuantity)));
        var issue = await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 1)));
        var i = Ok(await Post(issue));
        Assert.Equal((0m, (int)StockAccountingState.NotRequired, (Guid?)null), (i.TotalValue, i.AccountingState, i.JournalEntryId));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry.Table}"));
    }

    [PostgresFact]
    public async Task A_document_whose_item_was_deactivated_after_approval_is_refused_and_changes_nothing()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var item = (await Inv.GetItemAsync(Company.Id, Fifo.Id))!;
        Ok(await Inv.SetItemActiveAsync(Admin.Id, Company.Id, Fifo.Id, item.Revision, false));
        var counts = await Counts();
        Invalid(await Post(doc, Amounts(10m)), "lines[0].itemId", "item_inactive");
        Assert.Equal(counts, await Counts());
    }
}
