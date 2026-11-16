using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using SPE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingEvent;
using RV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.7C: reversal of executed stock documents, the derived accounting states and the real inventory valuation close probe (company migration 0017, system migration 0019) against real PostgreSQL.
/// Disposable cluster only.
/// </summary>
public abstract class StockReversalTestBase : StockPostingTestBase
{
    protected async Task<UserRecord> Poster() => (await Member([PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalPost], ScopeKind.Company, null)).User;

    /// <summary>Takes the draft journal of a posting through the ordinary workflow to Posted (submitted by the administrator, posted by someone else).</summary>
    protected async Task PostJournal(StockPostingView p) => await PostJournalEntry(p.JournalEntryId!.Value);

    protected async Task PostJournalEntry(Guid id)
    {
        var d = (await Journal.GetAsync(Company.Id, id))!;
        var s = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, d.Header.Id, d.Header.Revision));
        var poster = await Poster();
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(poster.Id, Company.Id, d.Header.Id, s.Header.Revision, false)).Header.Status);
    }

    protected Task<ConfigResult<StockPostingView>> Reverse(StockDocumentView d, string date = "2026-03-20", string reason = "wrong supplier") =>
        Posting.ReverseAsync(Admin.Id, Company.Id, d.Id, new StockReverseInput(date, reason));

    /// <summary>A calendar wired with the REAL close probes, exactly as the application registers them.</summary>
    protected FiscalCalendarService CloseCalendar => new(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock),
        new PeriodCloseGuard([new PendingJournalPeriodCloseProbe(), new PendingAdjustmentsPeriodCloseProbe(), new YearEndClosingPeriodCloseProbe(), new InventoryValuationPeriodCloseProbe()]), new FinancialHistoryGuard(), Clock);

    protected async Task<FiscalPeriodView> March() => (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(x => x.StartDate == new DateOnly(2026, 3, 1));
}

public class StockReversalTests : StockReversalTestBase
{
    [PostgresFact]
    public async Task Reversing_an_executed_receipt_takes_the_stock_back_and_cancels_the_journal_that_was_never_posted()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4)));
        var p = Ok(await Post(doc, Amounts(100m, 40m)));
        var r = Ok(await Reverse(doc));
        Assert.Equal(((int)StockAccountingState.Reversed, 1, p.JournalEntryId), (r.AccountingState, r.Reversal!.JournalAction, r.Reversal.JournalEntryId));
        Assert.Equal(("2026-03-20", "wrong supplier"), (r.Reversal.EffectiveDate, r.Reversal.Reason));
        Assert.Equal((0m, 0m, 0m, 0m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value, (await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        Assert.Equal(JournalStatus.Cancelled, (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!.Header.Status);       // never posted: cancelled, not reversed
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {CO.Table}"));                                                       // the receipt and its compensating operation
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SPE.Table} WHERE {SPE.Kind} = 3"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockPostingAuditEvents.Reversed}"));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        Assert.Equal(StockDocStatus.Approved, (StockDocStatus)(await Docs.GetAsync(Company.Id, doc.Id))!.Status);                    // the document is never edited or cancelled
        Fails(await Reverse(doc, "2026-03-21", "other"), ConfigStatus.Conflict, "already_reversed");
        var again = Ok(await Reverse(doc));                                                                                        // the identical request is a harmless retry
        Assert.True(again.Replayed);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SPE.Table} WHERE {SPE.Kind} = 3"));
    }

    [PostgresFact]
    public async Task The_accounting_state_follows_the_journal_and_a_posted_journal_is_reversed_by_a_draft_reversal_entry_never_edited()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var p = Ok(await Post(doc, Amounts(100m)));
        Assert.Equal((int)StockAccountingState.Pending, p.AccountingState);
        await PostJournal(p);
        Assert.Equal((int)StockAccountingState.Posted, (await Posting.GetAsync(Company.Id, doc.Id))!.AccountingState);
        var r = Ok(await Reverse(doc));
        Assert.Equal((2, (int)StockAccountingState.Reversed), (r.Reversal!.JournalAction, r.AccountingState));
        Assert.NotEqual(p.JournalEntryId, r.Reversal.JournalEntryId);
        var original = (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!;
        var reversal = (await Journal.GetAsync(Company.Id, r.Reversal.JournalEntryId!.Value))!;
        Assert.Equal(JournalStatus.Posted, original.Header.Status);                                                                 // the posted entry is untouched
        Assert.Equal((JournalStatus.Draft, JournalType.Reversal, true), (reversal.Header.Status, reversal.Header.Type, reversal.Balanced));
        Assert.Equal(0m, (await Pos(Fifo)).Quantity);
    }

    [PostgresFact]
    public async Task Reversing_an_issue_restores_the_exact_layers_and_value()
    {
        await PreparePosting();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10))), Amounts(100m)));
        Ok(await Post(await Approved(Receipt(W1, "2026-03-11", L(Fifo, Pcs, 10))), Amounts(150m)));
        var before = await Pos(Fifo);
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 15)));
        Ok(await Post(issue));
        var r = Ok(await Reverse(issue, "2026-03-13", "issued from the wrong warehouse"));
        Assert.Equal(1, r.Reversal!.JournalAction);
        var after = await Pos(Fifo);
        Assert.Equal((before.Quantity, before.Value), (after.Quantity, after.Value));
        Assert.Equal(before.OpenLayers.Select(l => (l.Id, l.RemainingQuantity, l.RemainingAmount)).ToList(), after.OpenLayers.Select(l => (l.Id, l.RemainingQuantity, l.RemainingAmount)).ToList());
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresFact]
    public async Task A_reversal_the_engine_cannot_do_is_refused_and_changes_nothing_and_so_are_invalid_requests()
    {
        await PreparePosting();
        var receipt = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        Ok(await Post(receipt, Amounts(100m)));
        Ok(await Post(await Approved(Issue(W1, "2026-03-11", L(Fifo, Pcs, 4)))));                                                   // the goods are partly consumed
        var counts = await Counts();
        var refused = await Reverse(receipt, "2026-03-12");
        Assert.Equal(ConfigStatus.Conflict, refused.Status);
        Assert.StartsWith("reversal_not_possible", refused.Code);
        Assert.Equal(counts, await Counts());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {RV.Table}"));
        var notExecuted = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1)));
        Fails(await Reverse(notExecuted), ConfigStatus.Conflict, "document_not_executed");
        Invalid(await Posting.ReverseAsync(Admin.Id, Company.Id, receipt.Id, new StockReverseInput("2026-03-12", "  ")), "reason", "required");
        Invalid(await Posting.ReverseAsync(Admin.Id, Company.Id, receipt.Id, new StockReverseInput("garbage", "x")), "effectiveDate", "invalid_date");
        Invalid(await Posting.ReverseAsync(Admin.Id, Company.Id, receipt.Id, new StockReverseInput("2026-03-05", "x")), "effectiveDate", "before_posting_date");
        Fails(await Posting.ReverseAsync(Admin.Id, Company.Id, Guid.NewGuid(), new StockReverseInput("2026-03-12", "x")), ConfigStatus.NotFound, "document_not_found");
        var march = await March();
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
        Fails(await Reverse(receipt, "2026-03-25"), ConfigStatus.Conflict, "fiscal_period_soft_closed");                             // the reversal date must lie in an open period
    }

    [PostgresFact]
    public async Task Simultaneous_reversal_requests_reverse_exactly_once()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 6)));
        Ok(await Post(doc, Amounts(60m)));
        var race = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Reverse(doc)));
        Assert.All(race, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        Assert.Equal(1, race.Count(r => !r.Value!.Replayed));
        Assert.Equal((1L, 1L), (await Sql<long>($"SELECT count(*) FROM {RV.Table}"), await Sql<long>($"SELECT count(*) FROM {SPE.Table} WHERE {SPE.Kind} = 3")));
        Assert.Equal(0m, (await Pos(Avg)).Quantity);
    }

    [PostgresFact]
    public async Task The_inventory_valuation_probe_blocks_the_period_close_until_the_ledger_agrees_with_the_stock_history()
    {
        await PreparePosting();
        var march = await March();
        var first = Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));                           // nothing posted yet: the module is not in use, no difference can exist
        Ok(await CloseCalendar.ReopenAsync(Admin.Id, Company.Id, march.Id, first.Revision));
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var p = Ok(await Post(doc, Amounts(100m)));
        var period = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, period.Id, period.Revision), ConfigStatus.Conflict, "period_close_blocked");   // the journal entry is still a draft
        await PostJournal(p);
        var closed = Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, period.Id, period.Revision));                          // the ledger now agrees with the costing ledger
        Ok(await CloseCalendar.ReopenAsync(Admin.Id, Company.Id, period.Id, closed.Revision));
        // a manual entry on an inventory account makes the ledger differ from the stock history
        var manual = Ok(await Journal.CreateAutomaticAsync(Admin.Id, Company.Id, new AutomaticJournalInput("test:manual-inventory-entry", new JournalInput(new DateOnly(2026, 3, 15), "manual", doc.BranchId,
            [new JournalLineInput(StockAcc.Id, null, null, 5m, null, null, null, null, null), new JournalLineInput(CogsAcc.Id, null, null, null, 5m, null, null, null, null)], null)), false));
        await PostJournalEntry(manual.Header.Id);
        var again = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, again.Id, again.Revision), ConfigStatus.Conflict, "period_close_blocked");
    }

    [PostgresFact]
    public async Task A_posting_whose_journal_was_cancelled_outside_the_reversal_is_failed_and_blocks_the_close_until_the_stock_is_reversed()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var p = Ok(await Post(doc, Amounts(100m)));
        var j = (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!;
        Ok(await Journal.CancelAsync(Admin.Id, Company.Id, j.Header.Id, j.Header.Revision));
        Assert.Equal((int)StockAccountingState.Failed, (await Posting.GetAsync(Company.Id, doc.Id))!.AccountingState);
        var march = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision), ConfigStatus.Conflict, "period_close_blocked");
        Ok(await Reverse(doc));                                                                                                    // reversing the stock effect resolves it
        Assert.Equal((int)StockAccountingState.Reversed, (await Posting.GetAsync(Company.Id, doc.Id))!.AccountingState);
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
    }

    [PostgresFact]
    public async Task Reversal_needs_its_own_warehouse_scoped_permission_and_the_record_is_append_only()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        Ok(await Post(doc, Amounts(50m)));
        var (poster, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsPost], ScopeKind.Company, null);
        Fails(await PostMgmt.ReverseAsync(poster.Id, Company.Id, doc.Id, new StockReverseInput("2026-03-12", "x")), ConfigStatus.Forbidden, "forbidden");   // posting does not imply reversing
        var (reverser, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsReverse], ScopeKind.Company, null);
        Ok(await PostMgmt.ReverseAsync(reverser.Id, Company.Id, doc.Id, new StockReverseInput("2026-03-12", "customer returned the goods")));
        foreach (var sql in new[] { $"UPDATE {RV.Table} SET {RV.Reason} = 'x'", $"DELETE FROM {RV.Table}", $"TRUNCATE {RV.Table}" }) Assert.Equal("P0001", await Raw(sql));
        var admin = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains(PermissionCatalog.Codes.StockDocsReverse, admin.Permissions);
    }
}

public class StockReversalApiTests : StockReversalTestBase
{
    [PostgresFact]
    public async Task The_reversal_endpoint_reverses_once_and_reports_the_accounting_state()
    {
        await StartApi();
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var url = $"/api/v1/company/stock-documents/{doc.Id}/reversal";
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, url, new { })).Status);
        var cookie = await SignIn(Admin);
        var body = new { effectiveDate = "2026-03-12", reason = "entered twice" };
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await Send(HttpMethod.Post, url, body, cookie)).Status);                    // not executed yet
        Ok(await Post(doc, Amounts(50m)));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, url, new { effectiveDate = "2026-03-12", reason = "" }, cookie)).Status);
        var done = await Send(HttpMethod.Post, url, body, cookie);
        Assert.Equal(System.Net.HttpStatusCode.OK, done.Status);
        Assert.Equal(((int)StockAccountingState.Reversed, 1), (done.Json.GetProperty("accountingState").GetInt32(), done.Json.GetProperty("reversal").GetProperty("journalAction").GetInt32()));
        Assert.True((await Send(HttpMethod.Post, url, body, cookie)).Json.GetProperty("replayed").GetBoolean());
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, url, body, await SignIn(viewer))).Status);
        Assert.Equal(2, (await Send(HttpMethod.Get, "/api/v1/company/stock-documents/capabilities", cookie: cookie)).Json.GetProperty("reverse").GetArrayLength());
    }
}

/// <summary>Scenarios added after the independent review of Phase 3.7C: every journal state at reversal time, transfers, journals reversed outside the stock reversal, and the period rule for unposted journals.</summary>
public class StockReversalScenarioTests : StockReversalTestBase
{
    [PostgresFact]
    public async Task A_transfer_is_reversed_without_any_journal_and_the_costs_return_exactly()
    {
        await PreparePosting();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10))), Amounts(100m)));
        var before = (await Pos(Avg)).Value;
        var transfer = await Approved(Transfer(W1, W2, "2026-03-11", L(Avg, Pcs, 4)));
        var t = Ok(await Post(transfer));
        Assert.Equal(40m, (await Pos(Avg, W2)).Value);
        var r = Ok(await Reverse(transfer, "2026-03-12", "wrong destination"));
        Assert.Equal((0, (int)StockAccountingState.Reversed, (Guid?)null), (r.Reversal!.JournalAction, r.AccountingState, r.Reversal.JournalEntryId));
        Assert.Equal((before, 0m), ((await Pos(Avg)).Value, (await Pos(Avg, W2)).Quantity));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        Assert.Equal(t.Id, r.Id);
    }

    [PostgresFact]
    public async Task A_journal_entry_that_is_submitted_or_approved_is_cancelled_by_the_reversal()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var p = Ok(await Post(doc, Amounts(100m)));
        var d = (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!;
        var submitted = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, d.Header.Id, d.Header.Revision));
        Assert.NotEqual(JournalStatus.Draft, submitted.Header.Status);
        var r = Ok(await Reverse(doc));
        Assert.Equal(1, r.Reversal!.JournalAction);
        Assert.Equal(JournalStatus.Cancelled, (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!.Header.Status);
    }

    [PostgresFact]
    public async Task Once_the_reversal_entry_is_posted_the_original_is_reversed_and_the_period_can_close()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var p = Ok(await Post(doc, Amounts(100m)));
        await PostJournal(p);
        var r = Ok(await Reverse(doc, "2026-03-20", "customer returned the goods"));
        var march = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision), ConfigStatus.Conflict, "period_close_blocked");     // the reversal entry is still a draft
        await PostJournalEntry(r.Reversal!.JournalEntryId!.Value);
        Assert.Equal(JournalStatus.Reversed, (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!.Header.Status);
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));                                                         // costing and ledger agree again
    }

    [PostgresFact]
    public async Task A_journal_reversed_outside_the_stock_reversal_fails_the_posting_until_the_stock_is_reversed_too()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var p = Ok(await Post(doc, Amounts(100m)));
        await PostJournal(p);
        var original = (await Journal.GetAsync(Company.Id, p.JournalEntryId!.Value))!;
        var draft = Ok(await Journal.ReverseAsync(Admin.Id, Company.Id, original.Header.Id, original.Header.Revision, new ReversalInput(new DateOnly(2026, 3, 15), null)));
        await PostJournalEntry(draft.Header.Id);
        Assert.Equal((int)StockAccountingState.Failed, (await Posting.GetAsync(Company.Id, doc.Id))!.AccountingState);
        var march = await March();
        Fails(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision), ConfigStatus.Conflict, "period_close_blocked");
        var r = Ok(await Reverse(doc, "2026-03-20", "repair"));                                                                                          // the stock side can still be repaired
        Assert.Equal((1, (int)StockAccountingState.Reversed), (r.Reversal!.JournalAction, r.AccountingState));
        Ok(await CloseCalendar.SoftCloseAsync(Admin.Id, Company.Id, march.Id, march.Revision));
    }

    [PostgresFact]
    public async Task An_unposted_journal_needs_the_reversal_in_the_same_period_while_a_posted_one_may_be_reversed_later()
    {
        await PreparePosting();
        var unposted = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        Ok(await Post(unposted, Amounts(50m)));
        var counts = await Counts();
        Invalid(await Reverse(unposted, "2026-04-05", "later"), "effectiveDate", "must_stay_in_posting_period");
        Assert.Equal(counts, await Counts());
        var posted = await Approved(Receipt(W1, "2026-03-11", L(Avg, Pcs, 5)));
        var p = Ok(await Post(posted, Amounts(70m)));
        await PostJournal(p);
        var r = Ok(await Reverse(posted, "2026-04-05", "later is fine for a posted entry"));
        Assert.Equal(2, r.Reversal!.JournalAction);
        Ok(await Reverse(unposted, "2026-03-25", "same period"));                                                                          // the same-period reversal of the unposted entry works
    }
}
