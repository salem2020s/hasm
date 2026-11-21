using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Costing;
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

    /// <summary>The company business audit stream verifies (chain, keyed MAC and external anchor) and is complete: the strongest check that no rolled-back event was kept, committed or anchored.</summary>
    private async Task AssertBusinessAuditSound()
    {
        var report = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.True(report.Verdict == AuditVerdict.Verified || report.Findings.All(f => f.Severity == FindingSeverity.Note),
            $"business audit stream: {report.Verdict}: {string.Join(", ", report.Findings.Select(f => $"{f.Code}@{f.Seq}"))}");
        Assert.Equal(await Sql<long>($"SELECT count(*) FROM {Audit_.Table}"), report.Events);
        Assert.Equal(report.Events, report.VerifiedThroughSeq);
    }

    private const string FailFunction = "CREATE FUNCTION zz_forced_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'forced failure' USING ERRCODE = 'P0001'; END $$;";

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

    [PostgresFact]
    public async Task A_refusal_thrown_after_the_costing_writes_is_undone_by_the_savepoint_so_a_caller_that_catches_it_commits_no_partial_effect()
    {
        // the accounting configuration is missing: the priced issue is costed FIRST (writes) and only then refused with inventory_accounting_not_configured
        await PreparePosting(configure: false);
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest("seed", D1, null, [new CostingReceiptLine(Fifo.Id, W1.Id, 10m, 80m)]));
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 4)));
        var seeded = await Everything();
        Assert.Equal((1L, 1L), (seeded.Operations, seeded.Entries));                                              // only the seed receipt
        var seededValue = await Sql<decimal>($"SELECT coalesce(sum({PO.Value}), 0) FROM {PO.Table}");
        const short CallerEvent = 1999;                                                                           // the caller's own audit event: its work must still commit

        await Tx.ExecuteAsync(Company.Id, async work =>
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Posting.PostInWorkAsync(work, Admin.Id, issue.Id, issue.Revision, null));
            Assert.Equal("inventory_accounting_not_configured", refusal.Message);
            // INSIDE the caller's transaction, after the caller caught the refusal: nothing of the failed attempt remains
            async Task<long> Inside(string sql) { await using var q = new Npgsql.NpgsqlCommand(sql, work.Connection, work.Transaction); return Convert.ToInt64(await q.ExecuteScalarAsync()); }
            Assert.Equal(1L, await Inside($"SELECT count(*) FROM {OP.Table}"));                                   // the seed only: the issue's cost operation was undone
            Assert.Equal(1L, await Inside($"SELECT count(*) FROM {EN.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {SP.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {CompanySchema.JournalEntry.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {CompanySchema.StockPostingEvent.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockPostingAuditEvents.Posted}"));
            Assert.Equal(seededValue, Convert.ToDecimal(await Inside($"SELECT coalesce(sum({PO.Value}), 0) FROM {PO.Table}")));
            // the transaction is usable and the caller carries on with its own work
            await work.AppendAuditAsync(new Hasm.Infrastructure.Audit.AuditEventData(CallerEvent, ActorUserId: Admin.Id, Detail: "caller work after a caught refusal"));
        });

        // after the caller's commit: its own work is there, and nothing of the failed posting
        Assert.Equal(seeded, await Everything());
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {CallerEvent}"));
        Assert.Equal((10m, 80m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));                         // the stock never moved
        // the audit chain stayed sound (the rolled-back receipt was not committed or anchored)
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // nothing is half done: with the configuration in place the same document posts once
        Ok(await Acc.SaveAsync(Admin.Id, Company.Id, 0, new InventoryAccountingInput(StockAcc.Id, GoodsAcc.Id, CogsAcc.Id, [], null)));
        var posted = Ok(await Post(issue));
        Assert.False(posted.Replayed);
        Assert.Equal(32m, posted.TotalValue);
        Assert.Equal((6m, 48m), ((await Pos(Fifo)).Quantity, (await Pos(Fifo)).Value));
        Assert.Equal(1L, (await Everything()).Postings);
        await AssertBusinessAuditSound();                                                                         // chain, MAC and anchor of the business stream after the commit
    }

    [PostgresFact]
    public async Task A_PostgreSQL_error_mid_posting_aborts_the_transaction_but_the_savepoint_restores_it_so_the_caller_commits_its_own_work_and_the_document_then_posts_once()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        var before = await Everything();
        const short CallerEvent = 1999;
        // the posting row insert fails AFTER the cost operation, entries and draft journal were written
        await Sql(FailFunction + $" CREATE TRIGGER zz_forced_failure BEFORE INSERT ON {SP.Table} FOR EACH ROW EXECUTE FUNCTION zz_forced_failure();");

        await Tx.ExecuteAsync(Company.Id, async work =>
        {
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m)));
            Assert.Equal("P0001", ex.SqlState);                                                                   // the error propagated unchanged
            async Task<long> Inside(string sql) { await using var q = new Npgsql.NpgsqlCommand(sql, work.Connection, work.Transaction); return Convert.ToInt64(await q.ExecuteScalarAsync()); }
            // the aborted-transaction state was cleared by ROLLBACK TO: queries work and nothing of the attempt remains
            Assert.Equal(before.Operations, await Inside($"SELECT count(*) FROM {OP.Table}"));
            Assert.Equal(before.Entries, await Inside($"SELECT count(*) FROM {EN.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {SP.Table}"));
            Assert.Equal(before.Journals, await Inside($"SELECT count(*) FROM {CompanySchema.JournalEntry.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {CompanySchema.StockPostingEvent.Table}"));
            Assert.Equal(0L, await Inside($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockPostingAuditEvents.Posted}"));
            await work.AppendAuditAsync(new AuditEventData(CallerEvent, ActorUserId: Admin.Id, Detail: "caller work after a PostgreSQL error"));
        });

        Assert.Equal(before, await Everything());
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {CallerEvent}"));
        await AssertBusinessAuditSound();

        // the error condition is gone: the same document posts once, and replays
        await Sql($"DROP TRIGGER zz_forced_failure ON {SP.Table}; DROP FUNCTION zz_forced_failure();");
        var posted = Ok(await Post(doc, Amounts(100m)));
        Assert.False(posted.Replayed);
        var done = await Everything();
        Assert.Equal((1L, 1L, 1L), (done.Postings, done.Events, done.Audit));
        Assert.True(Ok(await Post(doc, Amounts(100m))).Replayed);
        Assert.Equal(done, await Everything());
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        await AssertBusinessAuditSound();
    }

    /// <summary>
    /// The caller has not touched the business audit stream yet; the posting's own audit append (the stream's first touch in this transaction) fails and is rolled back to the savepoint, which also releases the
    /// stream lock. Whatever happens to the stream head afterwards (a forgery, or a legitimate advance by another writer), the caller's next append must re-take the lock and re-run the first-touch check.
    /// </summary>
    private async Task AfterRefusedFirstTouch(Func<Task> betweenRollbackAndAppend)
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        await Sql(FailFunction + $" CREATE TRIGGER zz_forced_failure BEFORE INSERT ON {Audit_.Table} FOR EACH ROW WHEN (NEW.{Audit_.EventType} = {StockPostingAuditEvents.Posted}) EXECUTE FUNCTION zz_forced_failure();");
        await Tx.ExecuteAsync(Company.Id, async work =>
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m)));
            await betweenRollbackAndAppend();                                                                      // another connection acts on the stream
            await work.AppendAuditAsync(new AuditEventData(1999, ActorUserId: Admin.Id, Detail: "caller append"));
        });
    }

    private Task ForgeHead() =>
        Sql($"INSERT INTO {Audit_.Table} ({Audit_.StreamKey}, {Audit_.Seq}, {Audit_.EventId}, {Audit_.OccurredAtUtc}, {Audit_.EventType}, {Audit_.CompanyId}, {Audit_.ActorUserId}, {Audit_.TargetUserId}, {Audit_.SubjectId}, {Audit_.Detail}, {Audit_.FormatVersion}, {Audit_.KeyId}, {Audit_.PrevHash}, {Audit_.Hash}, {Audit_.Mac}) " +
            $"SELECT {Audit_.StreamKey}, {Audit_.Seq} + 1, gen_random_uuid(), {Audit_.OccurredAtUtc}, 1998, {Audit_.CompanyId}, {Audit_.ActorUserId}, {Audit_.TargetUserId}, {Audit_.SubjectId}, 'forged', {Audit_.FormatVersion}, {Audit_.KeyId}, {Audit_.PrevHash}, {Audit_.Hash}, {Audit_.Mac} " +
            $"FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1");

    [PostgresFact]
    public async Task After_a_rolled_back_first_touch_a_forged_head_is_refused_by_the_callers_next_audit_append()
    {
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => AfterRefusedFirstTouch(ForgeHead));
        Assert.Equal("audit_head_corrupt", ex.Code);                                                              // the full first-touch check ran again
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1999"));
    }

    [PostgresFact]
    public async Task After_a_rolled_back_first_touch_the_callers_next_audit_append_chains_correctly_onto_a_head_another_writer_advanced()
    {
        await AfterRefusedFirstTouch(() => Tx.ExecuteAsync(Company.Id, w => w.AppendAuditAsync(new AuditEventData(1998, ActorUserId: Admin.Id, Detail: "another writer"))));   // a legitimate, committed and anchored advance
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1998"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1999"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {StockPostingAuditEvents.Posted}"));
        await AssertBusinessAuditSound();                                                                         // chained onto the advanced head, anchored, no gap
    }

    /// <summary>
    /// Review rule of #112/#107: Npgsql recycles the transaction object, so the audit first-touch state of an EARLIER transaction can still sit on it. A savepoint mark taken in a later transaction must carry the transaction id
    /// with the streams; restoring a mark whose id is not the current transaction's must count as EMPTY, otherwise the stale streams would be restored as "touched in this transaction" and the caller's next append would skip the head check.
    /// </summary>
    [PostgresFact]
    public async Task A_recycled_transaction_object_never_lets_a_savepoint_restore_the_stale_touched_streams_of_an_earlier_transaction()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        await Sql(FailFunction + $" CREATE TRIGGER zz_forced_failure BEFORE INSERT ON {Audit_.Table} FOR EACH ROW WHEN (NEW.{Audit_.EventType} = {StockPostingAuditEvents.Posted}) EXECUTE FUNCTION zz_forced_failure();");
        await using var conn = await Resolver.OpenAsync(Company.Id);

        // transaction 1 touches the business stream (full first-touch check), then rolls back
        var tx1 = await conn.BeginTransactionAsync();
        await new CompanyAuditedWork(Company.Id, conn, tx1, Audit, Clock).AppendAuditAsync(new AuditEventData(1998, ActorUserId: Admin.Id, Detail: "an earlier transaction touches the business stream"));
        await tx1.RollbackAsync();
        await tx1.DisposeAsync();

        // transaction 2 on the SAME connection: Npgsql hands back the same transaction object, which still carries the stale state of transaction 1
        var tx2 = await conn.BeginTransactionAsync();
        Assert.Same(tx1, tx2);
        var work = new CompanyAuditedWork(Company.Id, conn, tx2, Audit, Clock);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m)));   // fails on its first touch of the stream
        await ForgeHead();                                                                                         // another connection plants a forged head
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => work.AppendAuditAsync(new AuditEventData(1999, ActorUserId: Admin.Id, Detail: "caller append")));
        Assert.Equal("audit_head_corrupt", ex.Code);                                                              // the first-touch check ran again
        await tx2.RollbackAsync();
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1999"));
    }

    /// <summary>Review L-1: a failing release of the savepoint on the replay path poisons the work too (uniform with the failure path).</summary>
    [PostgresFact]
    public async Task A_failed_savepoint_release_on_the_replay_path_poisons_the_work_and_the_commit_is_refused()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        Ok(await Post(doc, Amounts(100m)));                                                                       // posted once (public entry)
        var before = await Everything();

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Tx.ExecuteAsync(Company.Id, async work =>
        {
            work.CleanupProbe = step => step == "release" ? Task.FromException(new InvalidOperationException("injected")) : Task.CompletedTask;
            var replay = await Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m));
            Assert.True(replay.Replayed);                                                                          // the replay itself still answers
            Assert.Equal("audit_state_unknown", work.PoisonCode);
            await work.AppendAuditAsync(new AuditEventData(1999, ActorUserId: Admin.Id, Detail: "caller append"));
        }));
        Assert.Equal("audit_state_unknown", ex.Code);
        Assert.Equal(before, await Everything());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1999"));
    }

    private Task CreateAuditFailureTrigger() =>
        Sql(FailFunction + $" CREATE TRIGGER zz_forced_failure BEFORE INSERT ON {Audit_.Table} FOR EACH ROW WHEN (NEW.{Audit_.EventType} = {StockPostingAuditEvents.Posted}) EXECUTE FUNCTION zz_forced_failure();");

    /// <summary>Review M1 of the adaptation delta: if the cleanup of a failed in-work step itself fails (here: the savepoint rollback), the partial effects may remain, so the work refuses to commit.</summary>
    [PostgresFact]
    public async Task A_failed_savepoint_rollback_poisons_the_work_so_a_caller_that_catches_the_refusal_cannot_commit_the_partial_effects()
    {
        await PreparePosting(configure: false);
        await Cost.ReceiveAsync(Company.Id, Admin.Id, new CostingReceiptRequest("seed", D1, null, [new CostingReceiptLine(Fifo.Id, W1.Id, 10m, 80m)]));
        var issue = await Approved(Issue(W1, "2026-03-12", L(Fifo, Pcs, 4)));
        var seeded = await Everything();

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Tx.ExecuteAsync(Company.Id, async work =>
        {
            work.CleanupProbe = step => step == "rollback" ? Task.FromException(new InvalidOperationException("injected")) : Task.CompletedTask;
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Posting.PostInWorkAsync(work, Admin.Id, issue.Id, issue.Revision, null));
            Assert.Equal("inventory_accounting_not_configured", refusal.Message);                                  // the original refusal still reaches the caller
            Assert.Equal("audit_state_unknown", work.PoisonCode);
            await work.AppendAuditAsync(new AuditEventData(1999, ActorUserId: Admin.Id, Detail: "the caller carries on and would commit"));
        }));
        Assert.Equal("audit_state_unknown", ex.Code);
        Assert.Equal(seeded, await Everything());                                                                 // the costing writes of the failed attempt were NOT committed
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1999"));
    }

    /// <summary>Review M1, restore step: a failure while restoring the audit memory also poisons the work (the commit is refused).</summary>
    [PostgresFact]
    public async Task A_failed_restore_of_the_audit_memory_poisons_the_work_and_the_commit_is_refused()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        await CreateAuditFailureTrigger();
        var before = await Sql<long>($"SELECT count(*) FROM {Audit_.Table}");

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Tx.ExecuteAsync(Company.Id, async work =>
        {
            work.CleanupProbe = step => step == "restore" ? Task.FromException(new InvalidOperationException("injected")) : Task.CompletedTask;
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m)));
            Assert.Equal("audit_state_unknown", work.PoisonCode);
            await work.AppendAuditAsync(new AuditEventData(1999, ActorUserId: Admin.Id, Detail: "legitimate caller append"));   // the rolled-back savepoint left the transaction usable
        }));
        Assert.Equal("audit_state_unknown", ex.Code);
        Assert.Equal(before, await Sql<long>($"SELECT count(*) FROM {Audit_.Table}"));                            // nothing committed
    }

    /// <summary>Review M1(a): when the restore fails, the audit memory is already invalidated, so the caller's next append re-runs the full first-touch check (a forged head is refused) instead of trusting stale state.</summary>
    [PostgresFact]
    public async Task A_failed_restore_still_forces_the_next_audit_append_to_re_run_the_first_touch_check()
    {
        await PreparePosting();
        var doc = await Approved(Receipt(W1, "2026-03-10", L(Fifo, Pcs, 10)));
        await CreateAuditFailureTrigger();

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Tx.ExecuteAsync(Company.Id, async work =>
        {
            work.CleanupProbe = step => step == "restore" ? Task.FromException(new InvalidOperationException("injected")) : Task.CompletedTask;
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Posting.PostInWorkAsync(work, Admin.Id, doc.Id, doc.Revision, Amounts(100m)));
            await ForgeHead();
            await work.AppendAuditAsync(new AuditEventData(1999, ActorUserId: Admin.Id, Detail: "caller append onto a forged head"));
        }));
        Assert.Equal("audit_head_corrupt", ex.Code);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = 1999"));
    }
}
