using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using AU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using EL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochEntryLineage;
using EM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The replay epoch at database level (design 3.2 to 3.4): a complete epoch written by SQL by a writer holding the (test-only) executor grant commits and leaves history untouched, and every way of writing an incomplete, misbound, repeated or
/// unauthorized one is refused. Tests N2, N8, N9, N10, N11, N13, N14, N15, N19, N20, N21, N26, N27, N29, N30 and N31 of design section 8. Where the executor (step 5) adds something to a case, the test says so.
/// </summary>
public class PlanExecutionEpochTests : PlanExecutionTestBase
{
    private const string Random = "gen_random_uuid()";
    private Task<short> Status(Plan p) => Sql<short>($"SELECT {CP.Status} FROM {CP.Table} WHERE {CP.Id} = '{p.Id}'");
    private async Task<(long Executions, long Members, long Lineage)> EpochRows() => (await Rows(CX.Table), await Rows(EM.Table), await Rows(EL.Table));

    private static Script Copy(Script s)
    {
        var c = new Script { AuditSubject = s.AuditSubject, AuditTarget = s.AuditTarget, GrantPlan = s.GrantPlan, GrantExecution = s.GrantExecution, AutoGrant = s.AutoGrant, GrantNotAfterIn = s.GrantNotAfterIn, GrantEpochOverride = s.GrantEpochOverride, DryRunSql = s.DryRunSql };
        c.AddRange(s);
        return c;
    }

    private static async Task<string> Outcome<T>(Func<Task<ConfigResult<T>>> action)
    {
        try { var r = await action(); return r.IsOk ? "ok" : $"{r.Status} {r.Code}"; }
        catch (Exception ex) { return Describe(ex); }
    }

    [PostgresRoleFact]
    public async Task A_complete_epoch_written_by_a_writer_holding_the_executor_grant_commits_leaves_every_earlier_row_untouched_and_ends_at_the_replayed_figures()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var lastEntry = await Sql<long>($"SELECT max({EN.Id}) FROM {EN.Table}");
        var before = (await Digest(EN.Table), await Digest(OP.Table), await Digest(JE.Table), await Digest(JL.Table), await Digest(SP.Table));
        Assert.Null(await Run(s.Script));

        Assert.Equal((16m, 240m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));                                       // 20 / 300.00 after the event, less 4 at 15.00
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // history is not edited: every earlier entry, every earlier operation (no reversed-by mark is set), every journal and journal line is byte-identical; the epoch is cost-only
        Assert.Equal(before.Item1, await Digest(EN.Table, $"x.{EN.Id} <= {lastEntry}"));
        Assert.Equal(before.Item2, await Digest(OP.Table, $"x.{OP.Id} NOT IN ('{s.Storno}', '{s.Event}', '{s.Rebook}')"));
        Assert.Equal((before.Item3, before.Item4), (await Digest(JE.Table), await Digest(JL.Table)));
        Assert.Equal(before.Item5, await Digest(SP.Table, $"x.{SP.Id} <> '{s.Plan.Posting}'"));
        // the three epoch entries: storno and rebook on the ORIGINAL date (12 March), the event on its own earlier date (11 March)
        var rows = new List<(Guid Op, int Dir, string Date, decimal Qty, decimal Amount, decimal QtyAfter, decimal ValueAfter)>();
        foreach (var op in new[] { s.Storno, s.Event, s.Rebook })
            rows.Add((op, await Sql<short>($"SELECT {EN.Direction} FROM {EN.Table} WHERE {EN.OperationId} = '{op}'"), await Sql<string>($"SELECT {EN.EffectiveDate}::text FROM {EN.Table} WHERE {EN.OperationId} = '{op}'"),
                await Sql<decimal>($"SELECT {EN.Quantity} FROM {EN.Table} WHERE {EN.OperationId} = '{op}'"), await Sql<decimal>($"SELECT {EN.Amount} FROM {EN.Table} WHERE {EN.OperationId} = '{op}'"),
                await Sql<decimal>($"SELECT {EN.QuantityAfter} FROM {EN.Table} WHERE {EN.OperationId} = '{op}'"), await Sql<decimal>($"SELECT {EN.ValueAfter} FROM {EN.Table} WHERE {EN.OperationId} = '{op}'")));
        Assert.Equal([(s.Storno, 1, "2026-03-12", 4m, 40m, 10m, 100m), (s.Event, 1, "2026-03-11", 10m, 200m, 20m, 300m), (s.Rebook, -1, "2026-03-12", 4m, 60m, 16m, 240m)], rows);
        Assert.Equal(((short)3, (1L, 3L, 1L)), (await Status(s.Plan), await EpochRows()));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AU.Table} WHERE {AU.EventType} = {CostingAuditEvents.PlanExecuted} AND {AU.SubjectId} = '{s.Plan.Id}' AND {AU.TargetUserId} = '{s.Plan.Execution}'"));
        // ordinary work continues on the position afterwards
        Ok(await Post(await Approved(Receipt(W1, "2026-03-13", L(Avg, Pcs, 2m))), Amounts(30m)));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // EXECUTOR-LEVEL VARIANT (step 5): the same figures produced by the executor from the read-only replay (E1/E2/E6, FIFO layers and consumptions, the compensation journals).
    }

    [PostgresRoleFact]
    public async Task N02_a_member_or_an_entry_bound_to_an_execution_of_an_earlier_transaction_is_refused()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var rows = await EpochRows(); var entries = await Digest(EN.Table);
        var op = Guid.NewGuid();
        // a new member for the committed execution, in a later transaction
        RefusedWith("an epoch member belongs to the execution, written in this transaction", await Rt(Op(op, 1, D2), Member(op, s.Plan.Execution, 2, null)));
        // a further (back-dated) entry for an operation that IS a member of that execution, in a later transaction
        RefusedWith("an entry of an epoch operation is written only inside the execution, in this transaction", await Rt(Move(s.Event, Avg.Id, W1.Id, 1, 1, D2, 1m, 10m)));
        RefusedWith("an entry of an epoch operation is written only inside the execution, in this transaction", await Rt(Move(s.Rebook, Avg.Id, W1.Id, 2, -1, D3, 1m, 15m)));
        // a lineage row or a journal row for it, in a later transaction
        RefusedWith("an epoch lineage row belongs to the execution, written in this transaction", await Rt(Lineage(s.IssueEntry, s.Storno, s.Rebook, s.Plan.Execution)));
        RefusedWith("a compensation journal is recorded inside the execution", await Rt($"INSERT INTO {CompanySchema.CompensationPlanExecutionJournal.Table} VALUES ('{s.Plan.Execution}', 1, '{s.Receipt.JournalEntryId}')"));
        Assert.Equal((rows, entries), (await EpochRows(), await Digest(EN.Table)));
    }

    [PostgresRoleFact]
    public async Task N08_a_plan_is_executed_exactly_once_a_second_execution_in_the_same_or_in_a_concurrent_transaction_is_refused()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        // in the same transaction: the unique key on the plan
        Assert.StartsWith("23505", await Rt(ExecutionInsert(s.Plan, audit: Random), ExecutionInsert(s.Plan, audit: Random, id: Guid.NewGuid(), posting: Guid.NewGuid())));
        // concurrently: the second writer waits on the plan row and is refused once the first one has committed
        Task<string?> second = null!;
        s.Script.Hooks["execution"] = async () =>
        {
            second = Task.Run(() => Rt(ExecutionInsert(s.Plan, audit: Random, id: Guid.NewGuid(), posting: Guid.NewGuid())));
            await Task.Delay(1500);
            Assert.False(second.IsCompleted);                                                                                   // it is waiting for the plan row, not racing
        };
        Assert.Null(await Run(s.Script));
        RefusedWith("only an approved compensation plan is executed, once", await second);
        Assert.Equal((1L, 3L, 1L), await EpochRows());
    }

    [PostgresRoleFact]
    public async Task N09_epoch_rows_written_inside_a_savepoint_are_accepted_and_a_rolled_back_savepoint_leaves_nothing_and_fails_the_commit()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var t = await SimpleEpoch(Avg2);
        // the execution row and the event are written inside savepoints (subtransactions, as the event's normal posting does): the xid8 binding is the TOP-LEVEL transaction, so they are accepted
        s.Script.Set("execution", ["SAVEPOINT x", .. s.Script.Of("execution"), "RELEASE SAVEPOINT x"]);
        s.Script.Set("event", ["SAVEPOINT ev", .. s.Script.Of("event"), "RELEASE SAVEPOINT ev"]);
        Assert.Null(await Run(s.Script));
        Assert.Equal((16m, 240m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));

        // the event's savepoint is rolled back: its operation, member, entry and posting are gone inside the transaction, and the epoch without its event fails at the commit
        var gone = $"DO $$ BEGIN IF EXISTS (SELECT 1 FROM {EM.Table} WHERE {EM.OperationId} = '{t.Event}') OR EXISTS (SELECT 1 FROM {EN.Table} WHERE {EN.OperationId} = '{t.Event}') " +
                   $"OR EXISTS (SELECT 1 FROM {OP.Table} WHERE {OP.Id} = '{t.Event}') OR EXISTS (SELECT 1 FROM {SP.Table} WHERE {SP.Id} = '{t.Plan.Posting}') THEN RAISE EXCEPTION 'rows of a rolled-back savepoint are still visible'; END IF; END $$";
        t.Script.Set("event", ["SAVEPOINT ev", .. t.Script.Of("event"), "ROLLBACK TO SAVEPOINT ev", gone]);
        t.Script.Set("rebook", [Op(t.Rebook, 2, D3), Member(t.Rebook, t.Plan.Execution, 3, t.Issue.CostOperationId), .. Move(t.Rebook, Avg2.Id, W1.Id, 2, -1, D3, 4m, 40m)]);   // without the event the issue costs 40.00 again
        var before = (await EpochRows(), await Digest(EN.Table));
        // (the first deferred rule to answer is the execution's own deferred reference to its posting, which the rollback removed; without that reference the deferred epoch check answers "exactly one event")
        var incomplete = await Run(t.Script);
        Assert.StartsWith("23503", incomplete);
        Assert.Contains(CX.FkPosting, incomplete);
        Assert.Equal(before, (await EpochRows(), await Digest(EN.Table)));
        Assert.Equal((short)2, await Status(t.Plan));                                                                           // still approved: nothing of the failed execution persisted
    }

    [PostgresRoleFact]
    public async Task N09_a_rolled_back_event_savepoint_with_a_posting_that_still_exists_is_answered_by_the_epoch_check_not_by_the_deferred_reference()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var t = await SimpleEpoch(Avg2);
        // the member event is rolled back, but the document's posting names an ORDINARY operation written outside the savepoint, so the execution's deferred reference to its posting is satisfied and the epoch check answers
        var ordinary = Guid.NewGuid();
        t.Script.Set("event", ["SAVEPOINT ev", .. t.Script.Of("event")[..^1], "ROLLBACK TO SAVEPOINT ev", Op(ordinary, 1, D3), .. Move(ordinary, Avg2.Id, W1.Id, 1, 1, D3, 10m, 200m), PostingRow(t.Plan, ordinary, D3, 200m)]);
        var before = (await EpochRows(), await Digest(EN.Table));
        RefusedWith("an execution has members and exactly one event", await Run(t.Script));
        Assert.Equal(before, (await EpochRows(), await Digest(EN.Table)));
        Assert.Equal((short)2, await Status(t.Plan));
    }

    [PostgresRoleFact]
    public async Task N10_an_epoch_entry_in_a_soft_closed_final_closed_or_missing_period_is_refused()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var eventSql = s.Script.Of("event");
        var before = (await EpochRows(), await Digest(EN.Table));
        await Sql(PeriodStatus("2026-03-12", 2));                                                                               // March is Soft Closed
        RefusedByPeriod(SoftClosedMessage, await Run(s.Script));
        await Sql(PeriodStatus("2026-03-12", 1));                                                                               // reopened
        // a date for which no fiscal period exists (the calendar starts on 1 January 2026)
        var missing = new DateOnly(2025, 12, 31);
        s.Script.Set("event", [Op(s.Event, 1, missing), Member(s.Event, s.Plan.Execution, 2, null), .. Move(s.Event, Avg.Id, W1.Id, 1, 1, missing, 10m, 200m), PostingRow(s.Plan, s.Event, missing, 200m)]);
        RefusedByPeriod(NoPeriodMessage, await Run(s.Script));
        s.Script.Set("event", eventSql);
        await Sql(PeriodStatus("2026-03-12", 2)); await Sql(PeriodStatus("2026-03-12", 3, $", {FP.FinalClosedBy} = gen_random_uuid()"));   // March is Final Closed
        RefusedByPeriod(FinalClosedMessage, await Run(s.Script));
        Assert.Equal(before, (await EpochRows(), await Digest(EN.Table)));
        Assert.Equal((short)2, await Status(s.Plan));
        // (the same script commits while every date lies in an Open period: the first test of this class)
    }

    [PostgresRoleFact]
    public async Task N11_an_epoch_on_a_position_with_an_open_negative_stock_obligation_is_refused()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        await ReceiveDoc(Avg, 3m, 15m, "2026-03-10");
        var issue = Ok(await Post(await Exceptional(Avg, 10m, "2026-03-12")));                                                  // 3 covered at cost, 7 into an open obligation
        Assert.Equal(7m, (await Obligations(openOnly: true)).Single().OpenQuantity);
        var entry = await EntryOf(issue.CostOperationId);
        var p = await ApprovedPlan(Avg, "2026-03-11");
        var storno = Guid.NewGuid();
        var script = new Script { AuditSubject = p.Id, AuditTarget = p.Execution, GrantPlan = p.Id, GrantExecution = p.Execution };
        script.Add(("audit", [])); script.Add(("execution", [ExecutionInsert(p)]));
        script.Add(("storno", [Op(storno, 2, D3), Member(storno, p.Execution, 1, issue.CostOperationId), .. Move(storno, Avg.Id, W1.Id, 2, 1, D3, 3m, 15m, entry)]));
        RefusedWith("an epoch never touches a position with an open negative-stock obligation", await Run(script));
        Assert.Equal((0L, 0L, 0L), await EpochRows());
    }

    [PostgresRoleFact]
    public async Task N13_a_document_bound_to_a_proposed_or_approved_plan_is_not_posted_by_the_normal_posting_path_and_is_released_when_the_plan_is_cancelled()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        await ReceiveDoc(Avg, 10m, 100m, "2026-03-10");
        var p = await ProposedPlan(Avg, "2026-03-12");                                                                          // dated on the last date: an unbound document of this date posts normally
        var postings = await Rows(SP.Table); var entries = await Digest(EN.Table);
        // the REAL normal posting service: refused by the database rule while the plan is proposed, and while it is approved
        Assert.Contains("document_bound_to_plan", await Outcome(() => Post(p.Doc, Amounts(50m))));
        Assert.Null(await Rt(ApprovalInsert(p), Approve(p)));
        Assert.Contains("document_bound_to_plan", await Outcome(() => Post(p.Doc, Amounts(50m))));
        // direct SQL as the runtime role: refused without an execution, and also inside an execution that names a DIFFERENT posting
        var op = Guid.NewGuid();
        RefusedWith("document_bound_to_plan", await Rt([Op(op, 1, D3), .. Move(op, Avg.Id, W1.Id, 1, 1, D3, 10m, 50m), PostingRow(p, op, D3, 50m)]));
        RefusedWith("document_bound_to_plan", await Rt([ExecutionInsert(p, audit: Random), Op(op, 1, D3), Member(op, p.Execution, 2, null), .. Move(op, Avg.Id, W1.Id, 1, 1, D3, 10m, 50m), PostingRow(p, op, D3, 50m, Guid.NewGuid())]));
        Assert.Equal((postings, entries), (await Rows(SP.Table), await Digest(EN.Table)));
        // cancelling (or rejecting) the plan releases the document: the normal posting path works again
        Assert.Null(await Rt(SetStatus(p, 5)));
        Ok(await Post(p.Doc, Amounts(50m)));
        Assert.Equal((20m, 150m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value));
        // EXECUTOR-LEVEL VARIANT (step 5): the posting service answers the code document_bound_to_plan as a Conflict result before the database rule is reached; today the database rule answers.
    }

    [PostgresRoleFact]
    public async Task N14_the_reversal_of_a_document_whose_cost_operation_was_rewritten_by_an_epoch_is_refused()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var before = (await Digest(EN.Table), await Digest(OP.Table));
        // the database rule: a reversal operation of the rewritten original (its entry was stornoed) is refused for the runtime role
        RefusedWith("operation_rewritten_by_epoch", await Rt(Op(Guid.NewGuid(), 4, D3, s.Issue.CostOperationId)));
        // the REAL reversal service on the rewritten issue document: nothing is reversed
        var issueDocument = (await Docs.GetAsync(Company.Id, s.Issue.DocumentId))!;
        Assert.Contains("operation_rewritten_by_epoch", await Outcome(() => Reverse(issueDocument)));
        Assert.Equal(before, (await Digest(EN.Table), await Digest(OP.Table)));
        // an operation the epoch did not touch is not caught by this rule (the receipt of 10 March lies before the event): its reversal operation is accepted here and fails only for the unrelated reason that this
        // test writes it without entries
        var untouched = await Rt(Op(Guid.NewGuid(), 4, D3, s.Receipt.CostOperationId));
        Assert.DoesNotContain("operation_rewritten_by_epoch", untouched);
        Assert.Contains("a costing operation without entries is allowed only for a negative-stock obligation or settlement", untouched);
        // the additional backstop of 0014 is unchanged: an entry is taken back at most once
        Assert.Contains("UNIQUE", await Sql<string>($"SELECT indexdef FROM pg_indexes WHERE indexname = '{EN.UniqueReverses}'"));
        // EXECUTOR-LEVEL VARIANT (step 5): the reversal service answers operation_rewritten_by_epoch as a Conflict result before any statement; today the database rule answers through the service.
    }

    [PostgresRoleFact]
    public async Task N15_a_second_plan_on_a_position_of_an_executed_epoch_is_not_executed_and_an_epoch_operation_is_never_rewritten_by_another_epoch()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var t = await SimpleEpoch(Avg2);
        Assert.Null(await Run(s.Script));
        var again = await ApprovedPlan(Avg, "2026-03-11");                                                                      // a second plan whose closure is the rewritten position
        RefusedWith("plan_on_epoch_position", await Rt(ExecutionInsert(again, audit: Random)));
        // no epoch on an epoch: inside the live execution of a plan on ANOTHER position, an operation of the first epoch cannot be named as the original of a storno or a rebook
        foreach (var (role, original) in new[] { (1, s.Rebook), (3, s.Rebook), (1, s.Event), (1, s.Storno) })
        {
            var op = Guid.NewGuid();
            RefusedWith("plan_on_epoch_position", await Rt(ExecutionInsert(t.Plan, audit: Random), Op(op, 2, D3), Member(op, t.Plan.Execution, role, original)));
        }
        Assert.Equal((1L, 3L, 1L), await EpochRows());
        // EXECUTOR-LEVEL VARIANT (step 5): the planner refuses such a plan with plan_on_epoch_position before it is stored, and the snapshot readers refuse histories that contain epoch members.
    }

    [PostgresRoleFact]
    public async Task N19_an_execution_without_its_business_audit_event_in_the_same_transaction_is_refused_at_the_commit()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var before = (await EpochRows(), await Digest(EN.Table), await Rows(AU.Table));
        // the transaction does write an audit event, but the execution does not name it: every statement succeeds and the COMMIT is refused
        s.Script.Set("execution", [ExecutionInsert(s.Plan, audit: Random)]);
        RefusedWith("an execution is committed together with its business audit event", await Run(s.Script));
        // an audit event of an EARLIER transaction (here: the posting event of the original receipt) does not vouch for the execution either
        var old = await Sql<Guid>($"SELECT {AU.EventId} FROM {AU.Table} ORDER BY {AU.Seq} DESC LIMIT 1");
        s.Script.Set("execution", [ExecutionInsert(s.Plan, audit: $"'{old}'")]);
        RefusedWith("an execution is committed together with its business audit event", await Run(s.Script));
        Assert.Equal(before, (await EpochRows(), await Digest(EN.Table), await Rows(AU.Table)));                                // the audit event of the refused transaction was rolled back with it
        Assert.Equal((short)2, await Status(s.Plan));
    }

    [PostgresRoleFact]
    public async Task N20_a_member_cannot_adopt_an_operation_that_has_entries_or_that_belongs_to_an_obligation_or_a_settlement()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var receipt = await ReceiveDoc(Avg, 10m, 100m, "2026-03-10");
        await Emptied(Avg2, 5m, 50m);
        var obligation = Ok(await Post(await Exceptional(Avg2, 3m, "2026-03-12")));                                             // nothing on hand: the whole issue is an obligation, its operation has no entry
        var settlement = await ReceiveDoc(Avg2, 3m, 33m, "2026-03-13");                                                         // settles the obligation completely: its operation has no entry either
        foreach (var op in new[] { obligation.CostOperationId, settlement.CostOperationId }) Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.OperationId} = '{op}'"));
        var p = await ApprovedPlan(Avg, "2026-03-11");
        var execution = ExecutionInsert(p, audit: Random);
        RefusedWith("an epoch member is an operation without entries yet", await Rt(execution, Member(receipt.CostOperationId, p.Execution, 2, null)));
        RefusedWith("an operation of a negative-stock obligation or settlement is never an epoch member", await Rt(execution, Member(obligation.CostOperationId, p.Execution, 2, null)));
        RefusedWith("an operation of a negative-stock obligation or settlement is never an epoch member", await Rt(execution, Member(settlement.CostOperationId, p.Execution, 2, null)));
        Assert.Equal((0L, 0L, 0L), await EpochRows());
    }

    [PostgresRoleFact]
    public async Task N21_re_executing_an_executed_plan_writes_nothing()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var before = (await EpochRows(), await Digest(EN.Table), await Digest(OP.Table), await Digest(CP.Table), await Rows(AU.Table));
        RefusedWith("only an approved compensation plan is executed, once", await Run(s.Script));                               // the whole script again, with its own audit event
        RefusedWith("only an approved compensation plan is executed, once", await Rt(ExecutionInsert(s.Plan, audit: Random, id: Guid.NewGuid(), posting: Guid.NewGuid())));
        Assert.Equal(before, (await EpochRows(), await Digest(EN.Table), await Digest(OP.Table), await Digest(CP.Table), await Rows(AU.Table)));
        // EXECUTOR-LEVEL VARIANT (step 5): the executor first looks for the execution row of the plan and RETURNS it (idempotent success) without sending any write; the database refusal above is its backstop.
    }

    [PostgresRoleFact]
    public async Task N26_every_epoch_entry_is_bound_to_the_approved_closure_and_a_storno_to_its_own_position_and_original_operation()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();                                                                                            // closure: AVG1 in W1 only
        var receiptEntry = await EntryOf(s.Receipt.CostOperationId);
        var elsewhere = await ReceiveDoc(Avg, 5m, 50m, "2026-03-12", W2);
        var elsewhereEntry = await EntryOf(elsewhere.CostOperationId);
        await ReceiveDoc(Avg2, 5m, 50m, "2026-03-12");
        var execution = ExecutionInsert(s.Plan, audit: Random);
        var before = (await EpochRows(), await Digest(EN.Table));
        Guid x = Guid.NewGuid();
        string[] Event(params string[] move) => [execution, Op(x, 1, D3), Member(x, s.Plan.Execution, 2, null), .. move];
        string[] Storno(params string[] move) => [execution, Op(x, 2, D3), Member(x, s.Plan.Execution, 1, s.Issue.CostOperationId), .. move];
        const string closure = "an entry of an epoch operation lies on a position of the approved closure of its plan";

        // (a) a member operation whose entry lies outside the closure: another item, another warehouse; back-dated or not
        RefusedWith(closure, await Rt(Event(Move(x, Avg2.Id, W1.Id, 1, 1, D3, 1m, 10m))));
        RefusedWith(closure, await Rt(Event(Move(x, Avg.Id, W2.Id, 1, 1, D3, 1m, 10m))));
        RefusedWith(closure, await Rt(Event(Move(x, Avg.Id, W2.Id, 1, 1, D2, 1m, 10m))));
        // the position row decides, not the item and warehouse copied into the entry
        RefusedWith("an entry of an epoch operation names the item and warehouse of its position", await Rt(Event(Move(x, Avg2.Id, W1.Id, 1, 1, D3, 1m, 10m, writeItem: Avg.Id))));
        // (b) a storno entry whose reversed entry belongs to another position, to another operation, or that does not mirror its original
        RefusedWith("a storno entry takes back an entry of its own position", await Rt(Storno(Move(x, Avg.Id, W1.Id, 2, 1, D3, 4m, 40m, elsewhereEntry))));
        RefusedWith("a storno entry takes back an entry of the original operation of its member", await Rt(Storno(Move(x, Avg.Id, W1.Id, 2, 1, D3, 4m, 40m, receiptEntry))));
        RefusedWith("a storno entry mirrors its original entry exactly, on the original date", await Rt(Storno(Move(x, Avg.Id, W1.Id, 2, 1, D3, 4m, 41m, s.IssueEntry))));
        RefusedWith("a storno entry mirrors its original entry exactly, on the original date", await Rt(Storno(Move(x, Avg.Id, W1.Id, 2, 1, D2, 4m, 40m, s.IssueEntry))));
        RefusedWith("a storno entry mirrors its original entry exactly, on the original date", await Rt(Storno(Move(x, Avg.Id, W1.Id, 2, -1, D3, 4m, 40m, s.IssueEntry))));
        RefusedWith("a storno entry names the entry it takes back", await Rt(Storno(Move(x, Avg.Id, W1.Id, 2, 1, D3, 4m, 40m))));
        // (c) a member whose original operation has no entry in the closure, or whose kind differs from its original
        RefusedWith("the original operation of an epoch member has no entry in the closure of the plan", await Rt(execution, Op(x, 1, D3), Member(x, s.Plan.Execution, 1, elsewhere.CostOperationId)));
        RefusedWith("a storno or rebook operation keeps the kind of its original operation", await Rt(execution, Op(x, 1, D3), Member(x, s.Plan.Execution, 3, s.Issue.CostOperationId)));
        // a lineage row whose rebook entry does not repeat its original (here: the storno step is right, the rebook is dated one day early)
        s.Script.Set("rebook", [Op(s.Rebook, 2, D2), Member(s.Rebook, s.Plan.Execution, 3, s.Issue.CostOperationId), .. Move(s.Rebook, Avg.Id, W1.Id, 2, -1, D2, 4m, 60m)]);
        RefusedWith("a rebook entry repeats its original entry on the same position and date", await Run(s.Script));
        Assert.Equal(before, (await EpochRows(), await Digest(EN.Table)));
    }

    [PostgresRoleFact]
    public async Task N27_the_reversal_of_any_epoch_operation_storno_event_or_rebook_is_refused()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var before = (await Digest(EN.Table), await Digest(OP.Table));
        // the database rule 3.3(4): a direct kind-4 operation naming the storno, the event's own operation or the rebook (by id, which is what "by key" resolves to)
        foreach (var target in new[] { s.Storno, s.Event, s.Rebook })
            RefusedWith("operation_rewritten_by_epoch", await Rt(Op(Guid.NewGuid(), 4, D3, target)));
        // the REAL reversal service on the back-dated event's own stock document (it has a posting: the execution's)
        // the REAL reversal service on the back-dated event's own stock document: refused. (It answers operation_not_found here, before the database rule, only because this test's hand-written event operation
        // does not carry the key the posting service derives for the document; with the executor's event posting the request reaches the kind-4 insert, which is the rule proven above and through the engine below.)
        Assert.NotEqual("ok", await Outcome(() => Reverse(s.Plan.Doc)));
        // the engine's reversal BY KEY of the event's own operation, of the rebook and of the storno operation: the request passes every application check and is refused by the database rule
        foreach (var target in new[] { s.Event, s.Rebook, s.Storno })
            Assert.Contains("operation_rewritten_by_epoch", Describe(await Assert.ThrowsAnyAsync<Exception>(() => Undo($"epoch-{target:N}", D3))));
        Assert.Equal(before, (await Digest(EN.Table), await Digest(OP.Table)));
        // EXECUTOR-LEVEL VARIANT (step 5): the services answer operation_rewritten_by_epoch as a result code before any statement; today the database rule answers through them.
    }

    [PostgresRoleFact]
    public async Task N27_an_entry_level_reversal_of_an_epoch_entry_through_an_unrelated_reversal_operation_is_refused_for_the_storno_event_and_rebook_entry()
    {
        // review M1 (probe ODB1): a kind-4 operation of an UNTOUCHED operation, whose entry names an entry of the epoch, used to commit. Each variant has its own fresh reversed operation, so the unique reversal
        // index cannot answer first.
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        Assert.Null(await Run(s.Script));
        var targets = new (string Name, long Entry)[] { ("rebook", await EntryOf(s.Rebook)), ("event", await EntryOf(s.Event)), ("storno", await EntryOf(s.Storno)) };
        var fresh = new[] { (await ReceiveDoc(Avg2, 5m, 50m, "2026-03-12")).CostOperationId, (await ReceiveDoc(Fifo, 5m, 50m, "2026-03-12")).CostOperationId, (await ReceiveDoc(Avg, 5m, 50m, "2026-03-12", W2)).CostOperationId };
        var before = (await Digest(EN.Table), await Digest(OP.Table), await Digest(PO.Table));
        for (var i = 0; i < targets.Length; i++)
        {
            var reversal = Guid.NewGuid();
            var direction = targets[i].Name == "event" ? -1 : 1;
            RefusedWith("a reversal entry takes back an entry of the operation that its reversal operation names",
                await Rt([Op(reversal, 4, D3, fresh[i]), .. Move(reversal, Avg.Id, W1.Id, 2, direction, D3, 4m, 40m, targets[i].Entry)]));
        }
        Assert.Equal(before, (await Digest(EN.Table), await Digest(OP.Table), await Digest(PO.Table)));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {EN.Table} WHERE {EN.ReversesEntryId} IN ({string.Join(",", targets.Select(t => t.Entry))}) AND {EN.OperationId} NOT IN ('{s.Storno}')"));
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
    }

    [PostgresRoleFact]
    public async Task N29_a_two_way_equal_date_transfer_chain_is_stornoed_and_rebooked_in_its_original_sequence_and_ends_at_the_replayed_figures()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        // AVG1: 10 for 100.00 into W1 on 10 March; on 11 March 4 go W1 -> W2 (40.00) and 2 come back W2 -> W1 (20.00): both directions on the SAME date. W1 holds 8 / 80.00, W2 holds 2 / 20.00.
        await Receive(Avg, 10m, 100m, date: D1);
        var t1 = await Move(Avg, 4m, W1, W2, D2);
        var t2 = await Move(Avg, 2m, W2, W1, D2);
        long e2 = t1.Lines.Single(l => l.Direction < 0).EntryId, e3 = t1.Lines.Single(l => l.Direction > 0).EntryId, e4 = t2.Lines.Single(l => l.Direction < 0).EntryId, e5 = t2.Lines.Single(l => l.Direction > 0).EntryId;
        Assert.True(e2 < e3 && e3 < e4 && e4 < e5);
        // the back-dated event: a receipt of 10 for 200.00 into W1 on 10 March. Replay: W1 20 / 300.00 (average 15.00); the first transfer carries 4 at 15.00 = 60.00, the second 2 at 15.00 = 30.00.
        var p = await ApprovedPlan(Avg, "2026-03-10", (Avg.Id, W2.Id));
        Guid s2 = Guid.NewGuid(), s1 = Guid.NewGuid(), ev = Guid.NewGuid(), b1 = Guid.NewGuid(), b2 = Guid.NewGuid();
        var script = new Script { AuditSubject = p.Id, AuditTarget = p.Execution, GrantPlan = p.Id, GrantExecution = p.Execution };
        script.Add(("audit", [])); script.Add(("execution", [ExecutionInsert(p)]));
        // storno in GLOBAL descending entry order: e5, e4 (second transfer), then e3, e2 (first transfer); every storno carries the original date
        script.Add(("storno", [Op(s2, 3, D2), Member(s2, p.Execution, 1, t2.OperationId), .. Move(s2, Avg.Id, W1.Id, 4, -1, D2, 2m, 20m, e5), .. Move(s2, Avg.Id, W2.Id, 3, 1, D2, 2m, 20m, e4),
                               Op(s1, 3, D2), Member(s1, p.Execution, 1, t1.OperationId), .. Move(s1, Avg.Id, W2.Id, 4, -1, D2, 4m, 40m, e3), .. Move(s1, Avg.Id, W1.Id, 3, 1, D2, 4m, 40m, e2)]));
        script.Add(("event", [Op(ev, 1, D1), Member(ev, p.Execution, 2, null), .. Move(ev, Avg.Id, W1.Id, 1, 1, D1, 10m, 200m), PostingRow(p, ev, D1, 200m)]));
        // rebook in GLOBAL ascending (date, original entry id) order: e2, e3, e4, e5: the equal-date legs keep their original sequence
        script.Add(("rebook", [Op(b1, 3, D2), Member(b1, p.Execution, 3, t1.OperationId), .. Move(b1, Avg.Id, W1.Id, 3, -1, D2, 4m, 60m), .. Move(b1, Avg.Id, W2.Id, 4, 1, D2, 4m, 60m),
                               Op(b2, 3, D2), Member(b2, p.Execution, 3, t2.OperationId), .. Move(b2, Avg.Id, W2.Id, 3, -1, D2, 2m, 30m), .. Move(b2, Avg.Id, W1.Id, 4, 1, D2, 2m, 30m)]));
        script.Add(("lineage", [Lineage(e2, s1, b1, p.Execution), Lineage(e3, s1, b1, p.Execution), Lineage(e4, s2, b2, p.Execution), Lineage(e5, s2, b2, p.Execution)]));
        script.Add(("executed", [SetStatus(p, 3)]));
        Assert.Null(await Run(script));

        Assert.Equal((18m, 270m, 2m, 30m), ((await Pos(Avg)).Quantity, (await Pos(Avg)).Value, (await Pos(Avg, W2)).Quantity, (await Pos(Avg, W2)).Value));   // 300.00 in total, as replayed
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // the rebooked legs follow the original order globally and on each position (the same-date legs were not swapped)
        var rebooked = new List<long>();
        foreach (var original in new[] { e2, e3, e4, e5 }) rebooked.Add(await Sql<long>($"SELECT {EL.RebookEntryId} FROM {EL.Table} WHERE {EL.OriginalEntryId} = {original}"));
        Assert.Equal(rebooked.Order(), rebooked);
        Assert.Equal(4L, await Sql<long>($"SELECT count(*) FROM {EL.Table} l JOIN {EN.Table} o ON o.{EN.Id} = l.{EL.OriginalEntryId} JOIN {EN.Table} r ON r.{EN.Id} = l.{EL.RebookEntryId} JOIN {EN.Table} s ON s.{EN.Id} = l.{EL.StornoEntryId} " +
            $"WHERE r.{EN.PositionId} = o.{EN.PositionId} AND r.{EN.EffectiveDate} = o.{EN.EffectiveDate} AND s.{EN.EffectiveDate} = o.{EN.EffectiveDate} AND r.{EN.Quantity} = o.{EN.Quantity}"));
        // EXECUTOR-LEVEL VARIANT (step 5): the executor derives these rows itself from the read-only replay (also for FIFO, with layers and consumptions) and N29 compares them with CostReplay's proposed state.
    }

    [PostgresRoleFact]
    public async Task N30_a_reversal_operation_is_never_an_epoch_member()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var elsewhere = await ReceiveDoc(Avg, 5m, 50m, "2026-03-12", W2);
        var execution = ExecutionInsert(s.Plan, audit: Random);
        foreach (var (role, original) in new (int, Guid?)[] { (2, null), (1, s.Issue.CostOperationId), (3, s.Issue.CostOperationId) })
        {
            var op = Guid.NewGuid();
            // a kind-4 operation (which by the CHECK of 0014 is exactly an operation with reverses_operation_id set), still without entries
            RefusedWith("a reversal operation is never an epoch member", await Rt(execution, Op(op, 4, D3, elsewhere.CostOperationId), Member(op, s.Plan.Execution, role, original)));
        }
        Assert.Equal((0L, 0L, 0L), await EpochRows());
    }

    [PostgresRoleFact]
    public async Task N31_one_audit_event_vouches_for_one_execution_and_it_must_be_the_plan_executed_event_of_this_plan_and_this_execution()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        var t = await SimpleEpoch(Avg2);
        Assert.Null(await Run(s.Script));
        var used = await Sql<Guid>($"SELECT {CX.AuditEventId} FROM {CX.Table} WHERE {CX.Id} = '{s.Plan.Execution}'");
        const string refused = "an execution is committed together with its business audit event";
        // two executions referencing one audit event: the unique key answers at once
        Assert.StartsWith("23505", await Rt(ExecutionInsert(t.Plan, audit: $"'{used}'")));
        // an audit event of another type
        t.Script.AuditType = CostingAuditEvents.Received;
        RefusedWith(refused, await Run(t.Script));
        t.Script.AuditType = CostingAuditEvents.PlanExecuted;
        // the plan-executed event of ANOTHER plan, of no plan, of another execution, of no execution
        foreach (var (subject, target) in new (Guid?, Guid?)[] { (s.Plan.Id, t.Plan.Execution), (null, t.Plan.Execution), (t.Plan.Id, s.Plan.Execution), (t.Plan.Id, Guid.NewGuid()), (t.Plan.Id, null) })
        {
            (t.Script.AuditSubject, t.Script.AuditTarget) = (subject, target);
            RefusedWith(refused, await Run(t.Script));
        }
        Assert.Equal((1L, 3L, 1L), await EpochRows());
        // the right event: its own plan as subject, its own execution as target
        (t.Script.AuditSubject, t.Script.AuditTarget) = (t.Plan.Id, t.Plan.Execution);
        Assert.Null(await Run(t.Script));
        Assert.Equal((2L, 6L, 2L), await EpochRows());
    }

    // ------------------------------------------------------------------------------------------------------------ design 3.4: one negative test per end-of-transaction completeness rule (review M3)
    // Each case starts from the complete epoch script that commits (the first test of this class) and removes or breaks exactly one thing; the text asserted is the rule's own message.

    private async Task<(Simple S, long Before, (long, long, long) Rows)> Incomplete()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        return (s, await Sql<long>($"SELECT count(*) FROM {EN.Table}"), await EpochRows());
    }

    private async Task NothingPersisted((Simple S, long Before, (long, long, long) Rows) c)
    {
        Assert.Equal(c.Rows, await EpochRows());
        Assert.Equal(c.Before, await Sql<long>($"SELECT count(*) FROM {EN.Table}"));
        Assert.Equal((short)2, await Status(c.S.Plan));
    }

    [PostgresRoleFact]
    public async Task M3_an_execution_whose_plan_is_not_marked_executed_in_the_same_transaction_is_refused_at_the_commit()
    {
        var c = await Incomplete();
        RefusedWith("an execution is committed together with the executed status of its plan", await Run(Copy(c.S.Script).Without("executed")));
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_a_storno_member_without_its_rebook_member_is_refused_at_the_commit()
    {
        var c = await Incomplete();
        // no rebook member, entry or lineage row: the pairing rule answers before the lineage rule
        RefusedWith("every storno member has its rebook member for the same original operation", await Run(Copy(c.S.Script).Without("rebook").Without("lineage")));
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_a_storno_or_rebook_entry_without_its_lineage_row_is_refused_at_the_commit()
    {
        var c = await Incomplete();
        RefusedWith("every storno and rebook entry has its lineage row", await Run(Copy(c.S.Script).Without("lineage")));
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_an_execution_without_an_event_member_is_refused_even_when_the_document_is_posted_by_an_ordinary_operation()
    {
        var c = await Incomplete();
        var e = c.S.Event;
        RefusedWith("an execution has members and exactly one event", await Run(Copy(c.S.Script).Set("event", [Op(e, 1, D3), .. Move(e, Avg.Id, W1.Id, 1, 1, D3, 10m, 200m), PostingRow(c.S.Plan, e, D3, 200m)])));
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_the_event_of_an_execution_must_be_the_posting_of_the_document_of_its_plan()
    {
        var c = await Incomplete();
        Guid e = c.S.Event, other = Guid.NewGuid();
        // the event member exists, but the plan's document is posted by another, ordinary operation
        RefusedWith("the event of an execution is the posting of the document of its plan",
            await Run(Copy(c.S.Script).Set("event", [Op(e, 1, D2), Member(e, c.S.Plan.Execution, 2, null), .. Move(e, Avg.Id, W1.Id, 1, 1, D2, 10m, 200m), Op(other, 1, D3), .. Move(other, Avg.Id, W1.Id, 1, 1, D3, 1m, 10m), PostingRow(c.S.Plan, other, D3, 10m)])));
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_a_paired_storno_and_rebook_member_without_any_entry_is_refused_at_the_commit()
    {
        var c = await Incomplete();
        Guid z = Guid.NewGuid(), z2 = Guid.NewGuid();
        RefusedWith("every epoch member has at least one entry",
            await Run(Copy(c.S.Script).After("event", "empty", Op(z, 1, D1), Member(z, c.S.Plan.Execution, 1, c.S.Receipt.CostOperationId), Op(z2, 1, D1), Member(z2, c.S.Plan.Execution, 3, c.S.Receipt.CostOperationId))));
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_a_lineage_row_that_names_the_wrong_entries_is_refused()
    {
        var c = await Incomplete();
        var receiptEntry = await EntryOf(c.S.Receipt.CostOperationId);
        string Row(long original, Guid storno, Guid rebook) => $"INSERT INTO {EL.Table} ({EL.OriginalEntryId}, {EL.StornoEntryId}, {EL.RebookEntryId}, {EL.ExecutionId}) VALUES ({original}, (SELECT {EN.Id} FROM {EN.Table} WHERE {EN.OperationId} = '{storno}'), (SELECT {EN.Id} FROM {EN.Table} WHERE {EN.OperationId} = '{rebook}'), '{c.S.Plan.Execution}')";
        var wrongOriginal = await Run(Copy(c.S.Script).Set("lineage", Row(receiptEntry, c.S.Storno, c.S.Rebook)));
        var wrongRebook = await Run(Copy(c.S.Script).Set("lineage", Row(c.S.IssueEntry, c.S.Storno, c.S.Event)));
        RefusedWith("the storno entry of a lineage row takes back its original entry", wrongOriginal);
        RefusedWith("the storno and rebook entries of a lineage row belong to the storno and rebook members of the same execution and original operation", wrongRebook);
        await NothingPersisted(c);
    }

    [PostgresRoleFact]
    public async Task M3_an_execution_row_alone_and_an_execution_in_a_rolled_back_savepoint_do_not_commit()
    {
        var c = await Incomplete();
        // the execution row with its genuine audit event and nothing else: its deferred reference to the (missing) posting answers
        Assert.StartsWith("23503", await Run(Copy(c.S.Script).Until("execution")));
        // the execution row is written inside a savepoint that is rolled back: the epoch rows that follow have no live execution
        RefusedWith("an epoch member belongs to the execution, written in this transaction, of an approved compensation plan",
            await Run(Copy(c.S.Script).Set("execution", ["SAVEPOINT x", .. c.S.Script.Of("execution"), "ROLLBACK TO SAVEPOINT x"])));
        await NothingPersisted(c);
        // control: the unmodified script commits
        Assert.Null(await Run(Copy(c.S.Script)));
    }

    // ------------------------------------------------------------------------------------------------------------ X5 at entry level (review L5)
    [PostgresRoleFact]
    public async Task L5_the_entry_level_check_refuses_an_epoch_entry_on_a_position_already_rewritten_by_another_executed_plan_for_a_raw_writer()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var a = await SimpleEpoch();
        var b = await ApprovedPlan(Avg, "2026-03-11");   // a second approved plan over the same position, with its own document
        await using var conn = await AsRuntime();
        await using var tx = await conn.BeginTransactionAsync();
        async Task Exec(string sql) { await using var cmd = new Npgsql.NpgsqlCommand(sql, conn, tx); await cmd.ExecuteNonQueryAsync(); }
        // the raw writer inserts its execution row BEFORE the first plan's execution exists, so the execution-insert check cannot see an executed plan; the first plan then executes and commits (the audited path)
        await Exec(ExecutionInsert(b, audit: "gen_random_uuid()"));
        Assert.Null(await Run(a.Script));
        // its first entry is now refused by the entry-level check, which sees the committed execution of the other plan
        var e = Guid.NewGuid();
        await Exec(Op(e, 1, D2));
        await Exec(Member(e, b.Execution, 2, null));
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Exec(Move(e, Avg.Id, W1.Id, 1, 1, D2, 10m, 200m)[0]));
        Assert.Equal(Refusal, ex.SqlState);
        Assert.Contains("plan_on_epoch_position: the position was already rewritten by another executed plan", ex.MessageText);
    }
}
