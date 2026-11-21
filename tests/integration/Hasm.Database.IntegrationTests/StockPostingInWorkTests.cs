using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 section 6.2 (required change to the merged 3.7B posting code): <c>StockPostingService.PostInWorkAsync</c> runs the existing posting inside a caller-supplied company transaction.
/// The public <c>PostAsync</c> keeps exactly its behaviour (pinned here and by the whole 3.7B/3.7C/3.8* suites), the in-work entry posts within the caller's transaction, a caller rollback leaves nothing
/// (no movement, cost, ledger effect, event or audit row), and the idempotency and the period gates apply inside the ambient work. Disposable cluster only.
/// </summary>
public class StockPostingInWorkTests : StockPostingTestBase
{
    private CompanyAuditedTransactions Tx => new(Resolver, Audit, Clock);

    private async Task<(long Operations, long Entries, long Postings, long Journals, long Events, long Audit)> Everything() =>
        (await Sql<long>($"SELECT count(*) FROM {OP.Table}"), await Sql<long>($"SELECT count(*) FROM {EN.Table}"), await Sql<long>($"SELECT count(*) FROM {SP.Table}"),
         await Sql<long>($"SELECT count(*) FROM {CompanySchema.JournalEntry.Table}"), await Sql<long>($"SELECT count(*) FROM {CompanySchema.StockPostingEvent.Table}"),
         await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockPostingAuditEvents.Posted}"));

    [PostgresFact]
    public async Task The_public_PostAsync_keeps_exactly_its_behaviour_effects_replay_and_refusal_codes()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        // refusals, with their stable codes, before anything is written
        Fails(await Posting.PostAsync(Admin.Id, Company.Id, doc.Id, null, Amounts(100m)), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Posting.PostAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, Amounts(100m)), ConfigStatus.NotFound, "document_not_found");
        Fails(await Posting.PostAsync(Admin.Id, Company.Id, doc.Id, doc.Revision + 5, Amounts(100m)), ConfigStatus.PreconditionFailed, "revision_conflict");
        var draft = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1))));
        Fails(await Posting.PostAsync(Admin.Id, Company.Id, draft.Id, draft.Revision, Amounts(10m)), ConfigStatus.Conflict, "document_not_approved");
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await Everything());

        // the posting: one cost operation, its entries, one posting with its event, one draft journal, one audit event, the stock and its value
        var posted = Ok(await Post(doc, Amounts(100m)));
        Assert.False(posted.Replayed);
        Assert.Equal((1, 100m, (int)StockAccountingState.Pending), (posted.DocType, posted.TotalValue, posted.AccountingState));
        var after = await Everything();
        Assert.Equal((1L, 1L, 1L, 1L, 1L, 1L), after);
        Assert.Equal((10m, 100m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));

        // the replay returns the original and writes nothing; other amounts are a conflict
        var again = Ok(await Post(doc, Amounts(100m)));
        Assert.True(again.Replayed);
        Assert.Equal((posted.Id, posted.CostOperationId, posted.JournalEntryId), (again.Id, again.CostOperationId, again.JournalEntryId));
        Fails(await Post(doc, Amounts(101m)), ConfigStatus.Conflict, "already_posted");
        Assert.Equal(after, await Everything());
    }

    [PostgresFact]
    public async Task PostInWorkAsync_posts_inside_the_callers_transaction_and_everything_commits_with_it()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10), L(Avg, Pcs, 4)));

        var view = await Tx.ExecuteAsync(Company.Id, async work =>
        {
            var v = await Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100.50m, 40m));
            // inside the ambient transaction the effects exist ...
            await using (var q = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {SP.Table}", work.Connection, work.Transaction)) Assert.Equal(1L, (long)(await q.ExecuteScalarAsync())!);
            // ... but nobody else sees them before the caller commits
            Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await Everything());
            return v;
        });

        Assert.False(view.Replayed);
        Assert.Equal((140.50m, (int)StockAccountingState.Pending), (view.TotalValue, view.AccountingState));
        Assert.Equal((1L, 2L, 1L, 1L, 1L, 1L), (await Everything()) with { Entries = 2L });                  // one operation, one posting, one draft journal, one event, one audit event; two lines of entries
        Assert.Equal((10m, 100.50m, 4m, 40m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value, (await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        var j = (await Journal.GetAsync(Company.Id, view.JournalEntryId!.Value))!;
        Assert.True(j.Balanced);
        Assert.Equal(StockDocStatus.Approved, (StockDocStatus)(await Docs.GetAsync(Company.Id, doc.Id))!.Status);
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // and the public entry sees the committed posting as an ordinary replay
        var replay = Ok(await Post(doc, Amounts(100.50m, 40m)));
        Assert.True(replay.Replayed);
        Assert.Equal((view.Id, view.CostOperationId, view.JournalEntryId), (replay.Id, replay.CostOperationId, replay.JournalEntryId));
    }

    [PostgresFact]
    public async Task A_caller_rollback_leaves_no_movement_no_cost_no_ledger_effect_no_event_and_no_audit_row()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var before = await Everything();

        var abort = await Assert.ThrowsAsync<InvalidOperationException>(() => Tx.ExecuteAsync<object?>(Company.Id, async work =>
        {
            await Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m));
            throw new InvalidOperationException("the caller's own step failed after the posting");              // e.g. the invoice approval is refused later in the same transaction
        }));
        Assert.Equal("the caller's own step failed after the posting", abort.Message);

        Assert.Equal(before, await Everything());
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await Everything());
        var position = await Cost.GetPositionAsync(Company.Id, Fifo.Id, W1.Id);
        Assert.True(position is null || position.Quantity == 0m);                                              // no stock movement
        Assert.Equal(0m, await Sql<decimal>($"SELECT coalesce(sum({PO.Value}), 0) FROM {PO.Table}"));            // no cost
        var untouched = (await Docs.GetAsync(Company.Id, doc.Id))!;
        Assert.Equal((StockDocStatus.Approved, doc.Revision), ((StockDocStatus)untouched.Status, untouched.Revision));
        // nothing is half done: the document can still be posted, once
        Ok(await Post(doc, Amounts(100m)));
        Assert.Equal((1L, 1L, 1L, 1L, 1L, 1L), (await Everything()) with { Entries = 1L });
    }

    [PostgresFact]
    public async Task The_idempotency_key_applies_inside_the_ambient_work_a_second_call_returns_the_original_and_other_amounts_are_refused()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));

        var views = await Tx.ExecuteAsync(Company.Id, async work =>
        {
            var first = await Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(50m));
            var again = await Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(50m));         // the same transaction, the same deterministic key
            var without = await Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, null);                // a lost-response retry need not resend the amounts
            return (first, again, without);
        });
        Assert.False(views.first.Replayed);
        Assert.True(views.again.Replayed && views.without.Replayed);
        Assert.Equal((views.first.Id, views.first.CostOperationId, views.first.JournalEntryId), (views.again.Id, views.again.CostOperationId, views.again.JournalEntryId));
        Assert.Equal((views.first.Id, views.first.CostOperationId), (views.without.Id, views.without.CostOperationId));
        Assert.Equal((1L, 1L, 1L, 1L, 1L), ((await Everything()).Operations, (await Everything()).Postings, (await Everything()).Journals, (await Everything()).Events, (await Everything()).Audit));
        Assert.Equal((5m, 50m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));                          // never a second movement

        // other amounts for an already posted document are a conflict, thrown to the caller, which rolls its transaction back
        var posted = await Everything();
        var conflict = await Assert.ThrowsAsync<StockDocumentService.Abort>(() => Tx.ExecuteAsync(Company.Id, work => Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(51m))));
        Assert.Equal("already_posted", conflict.Message);
        Assert.Equal(posted, await Everything());
    }

    [PostgresFact]
    public async Task The_period_gate_the_status_and_the_revision_checks_apply_inside_the_ambient_work_and_a_refusal_aborts_the_callers_transaction()
    {
        await PreparePosting();
        var closed = await Approved(Receipt(W1, "2026-04-05", L(Fifo, Pcs, 5)));
        var open = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 5)));
        var draft = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-10", L(Fifo, Pcs, 1))));
        var p = (await Calendar.ListAsync(Company.Id)).Single().Periods.Single(x => x.StartDate == new DateOnly(2026, 4, 1));
        Ok(await Calendar.SoftCloseAsync(Admin.Id, Company.Id, p.Id, p.Revision));

        async Task<string> Refused(Guid id, long revision, StockPostInput? input)
        {
            var e = await Assert.ThrowsAsync<StockDocumentService.Abort>(() => Tx.ExecuteAsync(Company.Id, work => Posting.PostInWorkAsync(work, Admin.Id, id, revision, input)));
            return e.Message;
        }
        Assert.Equal("fiscal_period_soft_closed", await Refused(closed.Id, closed.Revision, Amounts(10m)));        // the same gate as PostAsync
        Assert.Equal("revision_conflict", await Refused(open.Id, open.Revision + 3, Amounts(10m)));
        Assert.Equal("document_not_approved", await Refused(draft.Id, draft.Revision, Amounts(10m)));
        Assert.Equal("document_not_found", await Refused(Guid.NewGuid(), 1, Amounts(10m)));
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await Everything());                                                // each refusal rolled the whole ambient transaction back
        // an amounts problem is a costing/validation refusal, also thrown, also leaving nothing
        await Assert.ThrowsAsync<StockDocumentService.Abort>(() => Tx.ExecuteAsync(Company.Id, work => Posting.PostInWorkAsync(work, Admin.Id, open.Id, open.Revision, new StockPostInput([new StockPostLineInput(1, -1m)]))));
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await Everything());
    }
}
