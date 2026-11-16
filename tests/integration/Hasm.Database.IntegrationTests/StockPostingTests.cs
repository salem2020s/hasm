using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using SPL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingLine;
using SPE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingEvent;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.7B: the atomic posting of approved stock documents (company migration 0016, system migration 0018) against real PostgreSQL: stock effect, exact valuation and the balanced draft journal commit together or not at all;
/// idempotency and concurrency; the explicit accounting state; refusals; authorization; append-only records. Disposable cluster only.
/// </summary>
public abstract class StockPostingTestBase : CostingTestBase
{
    protected InventoryAccountingService Acc = null!;
    protected JournalService Journal = null!;
    protected StockPostingService Posting = null!;
    protected StockPostingManagementService PostMgmt = null!;
    protected AccountView StockAcc = null!, GoodsAcc = null!, CogsAcc = null!;

    protected async Task PreparePosting(bool configure = true)
    {
        await PrepareCosting();
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Acc = new InventoryAccountingService(Resolver, tx, Clock);
        Journal = new JournalService(Resolver, tx, Clock);
        Posting = new StockPostingService(Resolver, tx, Cost, Journal, Clock);
        PostMgmt = new StockPostingManagementService(Rbac, Docs, Posting);
        var chart = new ChartOfAccountsService(Resolver, tx, new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        AccountView Make(string code, string type) => Ok(chart.CreateAsync(Admin.Id, Company.Id, new AccountInput(code, null, "حساب", "Account " + code, type, null, false, null, null)).GetAwaiter().GetResult());
        StockAcc = Make("1301", "asset"); GoodsAcc = Make("2101", "liability"); CogsAcc = Make("5101", "expense");
        if (configure) Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null)));
    }

    protected async Task<StockDocumentView> Approved(StockDocumentInput input)
    {
        var d = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, input));
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        var approver = await Approver();
        return Ok(await Docs.ApproveAsync(approver.Id, Company.Id, d.Id, s.Revision));
    }

    protected static StockPostInput Amounts(params decimal[] amounts) => new([.. amounts.Select((a, i) => new StockPostLineInput(i + 1, a))]);
    protected Task<ConfigResult<StockPostingView>> Post(StockDocumentView d, StockPostInput? input = null, long? rev = null) => Posting.PostAsync(Admin.Id, Company.Id, d.Id, rev ?? d.Revision, input);
    protected async Task<(long Ops, long Postings, long Journals, long Events)> Counts() =>
        (await Sql<long>($"SELECT count(*) FROM {CO.Table}"), await Sql<long>($"SELECT count(*) FROM {SP.Table}"), await Sql<long>($"SELECT count(*) FROM {JE.Table}"), await Sql<long>($"SELECT count(*) FROM {SPE.Table}"));
}

public class StockPostingTests : StockPostingTestBase
{
    [PostgresFact]
    public async Task A_receipt_moves_the_stock_values_it_exactly_and_creates_one_balanced_draft_journal_while_the_document_stays_approved()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4)));
        var r = Ok(await Post(doc, Amounts(100.50m, 40m)));
        Assert.False(r.Replayed);
        Assert.Equal((1, 140.50m, (int)StockAccountingState.Pending), (r.DocType, r.TotalValue, r.AccountingState));
        Assert.Equal([(1, 100.50m), (2, 40m)], r.Lines.Select(l => (l.LineNo, l.Amount)));
        Assert.NotNull(r.JournalEntryId);
        Assert.NotNull(r.AccountingVersionId);
        var fifo = await Pos(Fifo); var avg = await Pos(Avg);
        Assert.Equal((10m, 100.50m, 4m, 40m), (fifo.Quantity, fifo.Value, avg.Quantity, avg.Value));                      // the stock and its exact valuation
        var j = (await Journal.GetAsync(Company.Id, r.JournalEntryId!.Value))!;
        Assert.Equal(JournalStatus.Draft, j.Header.Status);                                                                    // the entry still has to follow the ordinary approval and posting workflow
        Assert.Equal(DocumentSource.Automatic, j.Header.Source);
        Assert.Equal(StockPostingService.KeyOf(doc.Id), j.Header.SourceKey);
        Assert.True(j.Balanced);
        Assert.Equal((140.50m, 140.50m), (j.BaseDebitTotal, j.BaseCreditTotal));
        Assert.Equal([(StockAcc.Id, 140.50m, 0m), (GoodsAcc.Id, 0m, 140.50m)], j.Lines.OrderByDescending(l => l.Debit).Select(l => (l.AccountId, l.BaseDebit, l.BaseCredit)));
        Assert.Equal(StockDocStatus.Approved, (StockDocStatus)(await Docs.GetAsync(Company.Id, doc.Id))!.Status);             // the physical execution and the document status are separate facts
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {SPE.Table} WHERE {SPE.Kind} = 1"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockPostingAuditEvents.Posted}"));
        Assert.Equal(StockPostingService.KeyOf(doc.Id), await Sql<string>($"SELECT {CO.Key} FROM {CO.Table} WHERE {CO.Id} = '{r.CostOperationId}'"));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        Assert.Equal(r.Id, (await Posting.GetAsync(Company.Id, doc.Id))!.Id);
    }

    [PostgresFact]
    public async Task An_issue_is_costed_by_the_engine_at_the_actual_cost_and_books_the_cost_account_against_inventory()
    {
        await PreparePosting();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10))), Amounts(100m)));
        Ok(await Post(await Approved(Receipt(W1, "2026-03-11", L(Fifo, Pcs, 10))), Amounts(150m)));
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 15)));
        var r = Ok(await Post(issue));
        Assert.Equal(175m, r.TotalValue);                                                                                      // 10 x 10.00 + 5 x 15.00, oldest layer first
        var pos = await Pos(Fifo);
        Assert.Equal((5m, 75m), (pos.Quantity, pos.Value));
        var j = (await Journal.GetAsync(Company.Id, r.JournalEntryId!.Value))!;
        Assert.Equal([(CogsAcc.Id, 175m, 0m), (StockAcc.Id, 0m, 175m)], j.Lines.OrderByDescending(l => l.Debit).Select(l => (l.AccountId, l.BaseDebit, l.BaseCredit)));
        Assert.True(j.Balanced);
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresFact]
    public async Task A_transfer_carries_the_actual_cost_creates_no_journal_and_needs_no_accounting_configuration()
    {
        await PreparePosting(configure: false);
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10))), Amounts(0m)));      // free goods: no value, so no accounting and no configuration needed
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest("seed", D1, null, [new CostingReceiptLine(Fifo.Id, W1.Id, 10m, 80m)]));
        var doc = await Approved(Transfer(W1, W2, "2026-03-12", L(Fifo, Pcs, 4)));
        var r = Ok(await Post(doc));
        Assert.Equal(((int)StockAccountingState.NotRequired, 32m, (Guid?)null, (Guid?)null), (r.AccountingState, r.TotalValue, r.JournalEntryId, r.AccountingVersionId));
        Assert.Equal((6m, 48m, 4m, 32m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value, (await Pos(Fifo, W2)).Quantity, (await Pos(Fifo, W2)).Value));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {JE.Table}"));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresFact]
    public async Task A_repeated_request_returns_the_original_posting_and_other_amounts_are_a_conflict_never_a_second_posting()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var first = Ok(await Post(doc, Amounts(50m)));
        var counts = await Counts();
        var again = Ok(await Post(doc, Amounts(50m)));
        Assert.True(again.Replayed);
        Assert.Equal((first.Id, first.CostOperationId, first.JournalEntryId), (again.Id, again.CostOperationId, again.JournalEntryId));
        Fails(await Post(doc, Amounts(51m)), ConfigStatus.Conflict, "already_posted");
        Assert.Equal(counts, await Counts());                                                                                  // nothing was added
        Assert.Equal((5m, 50m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
    }

    [PostgresFact]
    public async Task Simultaneous_requests_for_the_same_document_produce_exactly_one_stock_effect_and_one_journal()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Avg, Pcs, 6)));
        var race = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Post(doc, Amounts(60m))));
        Assert.All(race, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        Assert.Equal(1, race.Count(r => !r.Value!.Replayed));
        Assert.Equal((1L, 1L, 1L, 1L), await Counts());
        Assert.Equal((6m, 60m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
    }

    [PostgresFact]
    public async Task Nothing_is_posted_without_a_usable_accounting_configuration_and_a_failure_after_costing_rolls_everything_back()
    {
        await PreparePosting(configure: false);
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var before = await Counts();
        Fails(await Post(doc, Amounts(50m)), ConfigStatus.Conflict, "inventory_accounting_not_configured");
        Assert.Equal(before, await Counts());
        Assert.Null(await Cost.GetPositionAsync(Company.Id, Fifo.Id, W1.Id));                                                  // no quantity-only posting, not even an empty position
        // configured, with stock in place, then the cost account is deactivated: the issue fails AFTER the engine has costed it, so the engine work must roll back
        var chart = new ChartOfAccountsService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new AccountUsageGuard(), new FinancialHistoryGuard(), Clock);
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null)));
        Ok(await Post(doc, Amounts(50m)));
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 2)));
        var cogs = (await chart.GetAsync(Company.Id, CogsAcc.Id))!;
        Ok(await chart.DeactivateAsync(Admin.Id, Company.Id, CogsAcc.Id, cogs.Revision));
        var counts = await Counts();
        var r = await Post(issue);
        Assert.False(r.IsOk);
        Assert.Equal(counts, await Counts());                                                                                  // no cost operation, no entry, no posting
        var pos = await Pos(Fifo);
        Assert.Equal((5m, 50m), (pos.Quantity, pos.Value));                                                                    // the stock is exactly as before the attempt
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresFact]
    public async Task Insufficient_stock_is_refused_and_changes_nothing()
    {
        await PreparePosting();
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 1)));
        var before = await Counts();
        Fails(await Post(issue), ConfigStatus.Conflict, "insufficient_stock");
        Assert.Equal(before, await Counts());
    }

    [PostgresFact]
    public async Task Amounts_are_required_for_every_receipt_line_and_refused_for_issues_and_transfers_and_the_document_must_be_approved_and_current()
    {
        await PreparePosting();
        var receipt = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5), L(Avg, Pcs, 5)));
        Invalid(await Post(receipt, null), "lines", "amount_required_for_every_line");
        Invalid(await Post(receipt, Amounts(10m)), "lines", "amount_required_for_every_line");
        Invalid(await Post(receipt, new StockPostInput([new(1, 10m), new(1, 5m), new(2, 5m)])), "lines[1].lineNo", "duplicate_line");
        Invalid(await Post(receipt, new StockPostInput([new(1, -1m), new(2, 5m)])), "lines[0].amount", "negative_amount");
        Invalid(await Post(receipt, new StockPostInput([new(9, 1m), new(2, 5m)])), "lines[0].lineNo", "line_not_found");
        Fails(await Posting.PostAsync(Admin.Id, Company.Id, receipt.Id, null, Amounts(1m, 1m)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Post(receipt, Amounts(1m, 1m), receipt.Revision + 5), ConfigStatus.PreconditionFailed, "revision_conflict");
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 1)));
        Invalid(await Post(issue, Amounts(5m)), "lines", "amounts_not_allowed");
        var draft = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1))));
        Fails(await Post(draft, Amounts(1m)), ConfigStatus.Conflict, "document_not_approved");
        Fails(await Posting.PostAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, null), ConfigStatus.NotFound, "document_not_found");
        Assert.Equal((0L, 0L, 0L, 0L), await Counts());
    }

    [PostgresFact]
    public async Task A_period_that_is_no_longer_open_refuses_the_posting_and_an_executed_document_can_no_longer_be_cancelled()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-04-05", L(Fifo, Pcs, 5)));
        var p = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(x => x.StartDate == new DateOnly(2026, 4, 1));
        var soft = Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, p.Id, p.Revision));
        Assert.False((await Post(doc, Amounts(10m))).IsOk);
        Assert.Equal((0L, 0L, 0L, 0L), await Counts());
        Ok(await Calendar.ReopenAsync(Admin.Id, Company.Id, p.Id, soft.Revision));
        Ok(await Post(doc, Amounts(10m)));
        Fails(await Docs.CancelAsync(Admin.Id, Company.Id, doc.Id, doc.Revision, "mistake"), ConfigStatus.Conflict, "document_executed");
        Assert.Equal("P0001", await Raw($"UPDATE {SD.Table} SET {SD.Status} = 5 WHERE {SD.Id} = '{doc.Id}'"));   // PostgreSQL refuses it even for a direct statement
    }

    [PostgresFact]
    public async Task Posting_needs_the_warehouse_scoped_post_permission_on_every_involved_warehouse()
    {
        await PreparePosting();
        Ok(await Post(await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5))), Amounts(50m)));
        var transfer = await Approved(Transfer(W1, W2, "2026-03-12", L(Fifo, Pcs, 1)));
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);
        var (onlyW1, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsPost], ScopeKind.Warehouse, W1.Id);
        var (stranger, _) = await Member([PermissionCatalog.Codes.StockDocsPost], ScopeKind.Company, null);                    // may post, but cannot even see the document
        Fails(await PostMgmt.PostAsync(viewer.Id, Company.Id, transfer.Id, transfer.Revision, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await PostMgmt.PostAsync(onlyW1.Id, Company.Id, transfer.Id, transfer.Revision, null), ConfigStatus.Forbidden, "forbidden");   // one of the two warehouses is not enough
        Fails(await PostMgmt.PostAsync(stranger.Id, Company.Id, Guid.NewGuid(), 1, null), ConfigStatus.Forbidden, "forbidden");              // no existence probing
        Assert.Equal(ConfigStatus.Forbidden, (await PostMgmt.GetAsync(stranger.Id, Company.Id, transfer.Id)).Status);
        Assert.Null(Ok(await PostMgmt.GetAsync(viewer.Id, Company.Id, transfer.Id)));                                                       // may look, nothing posted yet
        Assert.Null(await Posting.GetAsync(Company.Id, transfer.Id));
        var (both, _) = await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsPost], ScopeKind.Company, null);
        Ok(await PostMgmt.PostAsync(both.Id, Company.Id, transfer.Id, transfer.Revision, null));
        var admin = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains(PermissionCatalog.Codes.StockDocsPost, admin.Permissions);
    }

    [PostgresFact]
    public async Task Posting_records_are_append_only_for_everyone_including_direct_statements()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        Ok(await Post(doc, Amounts(50m)));
        foreach (var sql in new[]
        {
            $"UPDATE {SP.Table} SET {SP.TotalValue} = 1", $"DELETE FROM {SP.Table}", $"TRUNCATE {SP.Table} CASCADE",
            $"UPDATE {SPL.Table} SET {SPL.Amount} = 1", $"DELETE FROM {SPL.Table}", $"TRUNCATE {SPL.Table}",
            $"UPDATE {SPE.Table} SET {SPE.Kind} = 2", $"DELETE FROM {SPE.Table}", $"TRUNCATE {SPE.Table}",
        })
            Assert.Equal("P0001", await Raw(sql));
        Assert.Equal((1L, 1L, 1L, 1L), await Counts());
    }
}

public class StockPostingApiTests : StockPostingTestBase
{
    [PostgresFact]
    public async Task The_posting_endpoints_need_authentication_and_post_once_with_the_document_revision_as_if_match()
    {
        await StartApi();
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var url = $"/api/v1/company/stock-documents/{doc.Id}/posting";
        var etag = $"\"{doc.Revision}\"";
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, url)).Status);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, url, new { })).Status);
        var cookie = await SignIn(Admin);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Send(HttpMethod.Get, url, cookie: cookie)).Status);                    // not executed yet
        var body = new { lines = new[] { new { lineNo = 1, amount = 50.25m } } };
        Assert.Equal(System.Net.HttpStatusCode.PreconditionRequired, (await Send(HttpMethod.Post, url, body, cookie)).Status);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, url, new { lines = new[] { new { lineNo = 1, amount = -1m } } }, cookie, ifMatch: etag)).Status);
        Assert.Equal(System.Net.HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, url, body, cookie, ifMatch: $"\"{doc.Revision + 9}\"")).Status);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, url, body, await SignIn(viewer), ifMatch: etag)).Status);
        var posted = await Send(HttpMethod.Post, url, body, cookie, ifMatch: etag);
        Assert.Equal(System.Net.HttpStatusCode.OK, posted.Status);
        Assert.Equal((50.25m, 1, false), (posted.Json.GetProperty("totalValue").GetDecimal(), posted.Json.GetProperty("accountingState").GetInt32(), posted.Json.GetProperty("replayed").GetBoolean()));
        Assert.NotEqual(Guid.Empty, posted.Json.GetProperty("journalEntryId").GetGuid());
        var again = await Send(HttpMethod.Post, url, body, cookie, ifMatch: etag);
        Assert.True(again.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(posted.Json.GetProperty("id").GetGuid(), (await Send(HttpMethod.Get, url, cookie: cookie)).Json.GetProperty("id").GetGuid());
        Assert.Equal((1L, 1L, 1L, 1L), await Counts());
        var caps = await Send(HttpMethod.Get, "/api/v1/company/stock-documents/capabilities", cookie: cookie);
        Assert.Equal(2, caps.Json.GetProperty("post").GetArrayLength());                                                                 // both warehouses
    }
}
