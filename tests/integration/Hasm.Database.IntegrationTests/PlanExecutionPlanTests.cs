using System.Data;
using Hasm.Infrastructure.Access;
using Npgsql;
using Hasm.Infrastructure.CompanyDatabase;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanPosition;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanEvent;
using CJ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecutionJournal;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanLine;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using EL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochEntryLineage;
using EM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The plan, its closure, its approval and its status transitions (design 3.1 and section 4; reviews M3, L1 and L2): segregation of duties, fingerprint binding, immutability and the closure hash.
/// Tests N3, N5, N24 and N32 of design section 8, plus the byte-exact closure hash and the one-transaction rule.
/// </summary>
public class PlanExecutionPlanTests : PlanExecutionTestBase
{
    private Task<string> Hash(Plan p) => Sql<string>($"SELECT {CP.ClosureHash}::text FROM {CP.Table} WHERE {CP.Id} = '{p.Id}'");
    private Task<short> Status(Plan p) => Sql<short>($"SELECT {CP.Status} FROM {CP.Table} WHERE {CP.Id} = '{p.Id}'");

    [PostgresRoleFact]
    public async Task L1_L2_the_closure_hash_is_computed_by_PostgreSQL_only_in_the_byte_exact_form_and_the_plan_is_written_with_its_closure_in_one_transaction()
    {
        await PrepareRuntime();
        var p = new Plan { Doc = await Approved(Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m))), Item = Avg.Id, Warehouse = W1.Id };
        (Guid, Guid)[] pairs = [(Avg.Id, W2.Id), (Fifo.Id, W1.Id), (Avg.Id, W1.Id), (Avg2.Id, W2.Id), (Fifo.Id, W2.Id)];
        // the application supplies a closure hash and a creating transaction: both are ignored, PostgreSQL assigns them; the closure rows are written in no particular order
        Assert.Null(await Rt([PlanInsert(p, extraColumns: $", {CP.ClosureHash}, {CP.CreatedXact}", extraValues: $", {Fp('f')}, '1'"), .. pairs.Select(c => Closure(p, c.Item1, c.Item2))]));
        Assert.Equal(ExpectedClosureHash(pairs), await Hash(p));
        Assert.Matches("^[0-9a-f]{64}$", await Hash(p));
        Assert.NotEqual(1L, await Sql<long>($"SELECT {CP.CreatedXact}::text::bigint FROM {CP.Table} WHERE {CP.Id} = '{p.Id}'"));
        // the same closure in another order hashes the same; an empty closure hashes the empty string
        Assert.Equal(ExpectedClosureHash(pairs.Reverse()), await Hash(p));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", await Sql<string>($"SELECT {CompanySchema.EpochGuard.FnClosureHash}(gen_random_uuid())::text"));
        // the hash is frozen once the creating transaction is over: no later statement may change it, not even to the value it has
        RefusedWith("the closure hash of a compensation plan is immutable", await Rt($"UPDATE {CP.Table} SET {CP.ClosureHash} = {Fp('f')} WHERE {CP.Id} = '{p.Id}'"));

        // L2: a plan without its closure, or whose closure misses its root position, does not commit
        var alone = new Plan { Doc = await Approved(Receipt(W1, "2026-03-11", L(Avg2, Pcs, 10m))), Item = Avg2.Id, Warehouse = W1.Id };
        RefusedWith("the closure of a compensation plan contains its root position and is written with the plan", await Rt(PlanInsert(alone)));
        RefusedWith("the closure of a compensation plan contains its root position and is written with the plan", await Rt(PlanInsert(alone), Closure(alone, Avg2.Id, W2.Id)));
        Assert.Equal(1L, await Rows(CP.Table));
        // one live plan per document (X7)
        var twin = new Plan { Doc = p.Doc, Item = Avg.Id, Warehouse = W1.Id };
        Assert.StartsWith("23505", await Rt(PlanInsert(twin), Closure(twin, Avg.Id, W1.Id)));
    }

    [PostgresRoleFact]
    public async Task N03_an_execution_needs_a_plan_approved_by_someone_independent_of_the_plan_and_of_the_event_document_and_is_never_run_by_the_creator()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        await ReceiveDoc(Avg, 10m, 100m, "2026-03-10");
        // the event document: created by the administrator, edited by a second user, submitted by a third, approved by a fourth
        var editor = (await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsUpdate], ScopeKind.Company, null)).User;
        var submitter = (await Member([PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.StockDocsSubmit], ScopeKind.Company, null)).User;
        var documentApprover = await Approver();
        var created = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m))));
        var edited = Ok(await Docs.UpdateAsync(editor.Id, Company.Id, created.Id, created.Revision, Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m)) with { Notes = "edited" }));
        var submitted = Ok(await Docs.SubmitAsync(submitter.Id, Company.Id, created.Id, edited.Revision));
        var p = new Plan { Doc = Ok(await Docs.ApproveAsync(documentApprover.Id, Company.Id, created.Id, submitted.Revision)), Item = Avg.Id, Warehouse = W1.Id };
        Assert.Null(await Rt(PlanInsert(p), Closure(p, Avg.Id, W1.Id)));

        // not approved: no execution
        RefusedWith("only an approved compensation plan is executed", await Rt(ExecutionInsert(p, audit: "gen_random_uuid()")));
        // approved by its creator, or by the creator, the editor or the submitter of the event document: refused at the approval row, whoever tries
        foreach (var who in new[] { Planner, Admin.Id, editor.Id, submitter.Id })
            RefusedWith("approver_not_independent", await Rt(ApprovalInsert(p, who), Approve(p, who)));
        // the status cannot be moved to approved around the approval row: without one, with one of another plan revision, or naming another approver
        RefusedWith("an approved plan names its approval", await Rt(SetStatus(p, 2)));
        RefusedWith("a plan is approved by the approval of its current revision", await Rt(Approve(p)));
        RefusedWith("an approval is given to the current revision of a proposed plan", await Rt(ApprovalInsert(p, revision: 2), Approve(p)));
        RefusedWith("a plan is approved by the approval of its current revision", await Rt(ApprovalInsert(p), Approve(p, Guid.NewGuid())));
        // an approval never stays behind without the status change of its plan
        RefusedWith("an approval is committed together with the approval of its plan", await Rt(ApprovalInsert(p)));
        Assert.Equal((0L, (short)1), (await Rows(CA.Table), await Status(p)));

        // an independent approver (here: the person who approved the document, who is neither its creator, editor nor submitter)
        Assert.Null(await Rt(ApprovalInsert(p, documentApprover.Id), Approve(p, documentApprover.Id)));
        Assert.Equal((short)2, await Status(p));
        // a second approval of the same plan is refused; the approval of a plan is set once
        RefusedWith("an approval is given to the current revision of a proposed plan", await Rt(ApprovalInsert(p, id: Guid.NewGuid(), revision: 2)));
        RefusedWith("this status transition of a compensation plan is not allowed", await Rt(SetStatus(p, 4)));
        // the executor differs from the creator (the approver may execute)
        RefusedWith("a compensation plan is never executed by its creator", await Rt(ExecutionInsert(p, Planner, "gen_random_uuid()")));
        // an approved plan whose event document is no longer approved at the planned revision is not executed
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, p.Doc.Id, p.Doc.Revision, "withdrawn"));
        RefusedWith("document_not_approved", await Rt(ExecutionInsert(p, audit: "gen_random_uuid()")));
        Assert.Equal(0L, await Rows(CX.Table));
    }

    [PostgresRoleFact]
    public async Task N03_a_plan_is_approved_only_for_an_approved_event_document_at_the_planned_revision()
    {
        await PrepareRuntime();
        var draft = await Draft(Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m)));
        var p = new Plan { Doc = draft, Item = Avg.Id, Warehouse = W1.Id };
        Assert.Null(await Rt(PlanInsert(p), Closure(p, Avg.Id, W1.Id)));
        RefusedWith("document_not_approved", await Rt(ApprovalInsert(p), Approve(p)));                                           // a draft
        var submitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, draft.Id, draft.Revision));
        Ok(await Docs.ApproveAsync((await Approver()).Id, Company.Id, draft.Id, submitted.Revision));
        RefusedWith("document_not_approved", await Rt(ApprovalInsert(p), Approve(p)));                                           // approved, but at a later revision than the plan was made for
    }

    [PostgresRoleFact]
    public async Task L1_a_plan_is_never_written_or_approved_for_a_document_that_already_has_a_posting_also_when_the_posting_was_made_under_a_repeatable_read_snapshot_older_than_the_plan()
    {
        // review L1 (probe ODB5)
        await PrepareRuntime();
        await ReceiveDoc(Avg, 10m, 100m, "2026-03-10");
        // a plan written for a document that is already posted is refused (at the insert, and again at the commit)
        var posted = await Approved(Receipt(W1, "2026-03-12", L(Avg, Pcs, 1m)));
        Ok(await Post(posted, Amounts(5m)));
        var late = new Plan { Doc = posted, Item = Avg.Id, Warehouse = W1.Id };
        RefusedWith("document_already_posted", await Rt(PlanInsert(late), Closure(late, Avg.Id, W1.Id)));
        Assert.Equal(0L, await Rows(CP.Table));
        // a REPEATABLE READ posting whose snapshot is older than the plan does not see the plan (X7 reads the snapshot): the posting commits (a posting is unique per document, so nothing is posted twice), but the plan
        // for the now posted document can then neither be approved nor executed
        var doc = await Approved(Receipt(W1, "2026-03-12", L(Avg, Pcs, 10m)));
        var p = new Plan { Doc = doc, Item = Avg.Id, Warehouse = W1.Id };
        await using (var conn = await AsRuntime())
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await using (var snapshot = new NpgsqlCommand($"SELECT count(*) FROM {CP.Table}", conn, tx)) await snapshot.ExecuteScalarAsync();
            Assert.Null(await Rt(PlanInsert(p), Closure(p, Avg.Id, W1.Id)));                                                      // another session, after the snapshot
            var op = Guid.NewGuid();
            foreach (var q in (string[])[Op(op, 1, D3), .. Move(op, Avg.Id, W1.Id, 1, 1, D3, 10m, 50m), PostingRow(p, op, D3, 50m)]) { await using var cmd = new NpgsqlCommand(q, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
        }
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {CompanySchema.StockPosting.Table} WHERE {CompanySchema.StockPosting.DocumentId} = '{doc.Id}'"));
        RefusedWith("document_already_posted", await Rt(ApprovalInsert(p), Approve(p)));
        Assert.Equal((short)1, await Status(p));
        Assert.Equal(0L, await Rows(CX.Table));
    }

    [PostgresRoleFact]
    public async Task N05_update_delete_and_truncate_are_refused_on_every_new_table_for_the_runtime_role_and_for_the_owner()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        var s = await SimpleEpoch();
        // every new table holds at least one row: a plan line and a plan event are written with a second plan, a journal row with the execution
        s.Script.After("execution", "journal", $"INSERT INTO {CJ.Table} ({CJ.ExecutionId}, {CJ.BucketNo}, {CJ.JournalEntryId}) VALUES ('{s.Plan.Execution}', 1, '{s.Receipt.JournalEntryId}')");
        Assert.Null(await Run(s.Script));
        var other = new Plan { Doc = await Approved(Receipt(W1, "2026-03-11", L(Avg2, Pcs, 10m))), Item = Avg2.Id, Warehouse = W1.Id };
        var period = await Sql<Guid>($"SELECT {FP.Id} FROM {FP.Table} WHERE {FP.StartDate} = DATE '2026-03-01'");
        var branch = await Sql<Guid>($"SELECT {CompanySchema.Branch.Id} FROM {CompanySchema.Branch.Table} WHERE {CompanySchema.Branch.IsMain}");
        Assert.Null(await Rt(PlanInsert(other), Closure(other, Avg2.Id, W1.Id),
            $"INSERT INTO {CL.Table} ({CL.PlanId}, {CL.LineNo}, {CL.EntryDate}, {CL.PeriodId}, {CL.BranchId}, {CL.AccountId}, {CL.Debit}, {CL.Credit}) VALUES ('{other.Id}', 1, DATE '2026-03-12', '{period}', '{branch}', '{CogsAcc.Id}', 20, 0)",
            $"INSERT INTO {CE.Table} ({CE.Id}, {CE.PlanId}, {CE.Kind}, {CE.ActorUserId}, {CE.AtUtc}) VALUES (gen_random_uuid(), '{other.Id}', 1, '{Planner}', now())"));

        (string Table, string Set)[] tables = [(CP.Table, $"{CP.Payload} = {CP.Payload} || 'x'"), (CC.Table, $"{CC.ItemId} = {CC.ItemId}"), (CL.Table, $"{CL.Debit} = {CL.Debit}"), (CE.Table, $"{CE.Note} = 'x'"), (CA.Table, $"{CA.Note} = 'x'"),
            (CX.Table, $"{CX.ExecutedBy} = {CX.ExecutedBy}"), (CJ.Table, $"{CJ.BucketNo} = {CJ.BucketNo}"), (EM.Table, $"{EM.Role} = {EM.Role}"), (EL.Table, $"{EL.ExecutionId} = {EL.ExecutionId}")];
        var digests = new List<string>();
        foreach (var (table, _) in tables) { Assert.NotEqual(0L, await Rows(table)); digests.Add(await Digest(table)); }
        foreach (var (table, set) in tables)
        {
            // the runtime role: refused (the plan's update by its trigger, everything else already by the missing privilege)
            Assert.StartsWith(table == CP.Table ? Refusal : "42501", await Rt($"UPDATE {table} SET {set}"));
            Assert.StartsWith("42501", await Rt($"DELETE FROM {table}"));
            Assert.StartsWith("42501", await Rt($"TRUNCATE {table}"));
            Assert.StartsWith("42501", await Rt($"TRUNCATE {table} CASCADE"));
            // the owner, who holds every privilege: refused by the guards themselves
            Assert.Equal(Refusal, await Raw($"UPDATE {table} SET {set}"));
            Assert.Equal(Refusal, await Raw($"DELETE FROM {table}"));
            Assert.NotNull(await Raw($"TRUNCATE {table}"));
            Assert.Equal(Refusal, await Raw($"TRUNCATE {table} CASCADE"));
        }
        // the plan's own immutable parts, one by one, as the runtime role (which holds UPDATE on the plan for the status transition only)
        foreach (var set in new[] { $"{CP.Payload} = '{{}}'", $"{CP.PlanFingerprint} = {Fp('9')}", $"{CP.AccountingFingerprint} = {Fp('9')}", $"{CP.DocumentRevision} = 99", $"{CP.CreatedBy} = gen_random_uuid()", $"{CP.ItemId} = '{Avg.Id}'", $"{CP.CreatedXact} = '1'" })
            RefusedWith("the payload, the fingerprints and the origin of a compensation plan are immutable", await Rt($"UPDATE {CP.Table} SET {set} WHERE {CP.Id} = '{other.Id}'"));
        RefusedWith("a compensation plan changes only by a status transition", await Rt($"UPDATE {CP.Table} SET {CP.Revision} = {CP.Revision} + 1 WHERE {CP.Id} = '{other.Id}'"));
        RefusedWith("this status transition of a compensation plan is not allowed", await Rt(SetStatus(s.Plan, 2)));                  // executed is final
        RefusedWith("a plan is marked executed only in the transaction of its execution", await Rt(ApprovalInsert(other), Approve(other), SetStatus(other, 3)));
        for (var i = 0; i < tables.Length; i++) Assert.Equal(digests[i], await Digest(tables[i].Table));
    }

    [PostgresRoleFact]
    public async Task N24_an_approval_whose_fingerprints_differ_from_the_plan_is_refused_for_each_fingerprint_and_for_the_closure_hash()
    {
        await PrepareRuntime();
        var p = await ProposedPlan(Avg, "2026-03-11");
        foreach (var column in ApprovalFingerprints.Append(CA.ClosureHash))
            RefusedWith("approval_fingerprint_mismatch", await Rt(ApprovalInsert(p, differs: column), Approve(p)));
        Assert.Equal((0L, (short)1), (await Rows(CA.Table), await Status(p)));
        Assert.Null(await Rt(ApprovalInsert(p), Approve(p)));                                                                    // the exact fingerprints: approved
        Assert.Equal((1L, (short)2), (await Rows(CA.Table), await Status(p)));
    }

    [PostgresRoleFact]
    public async Task N32_the_closure_cannot_be_widened_after_the_creating_transaction_and_a_changed_closure_fails_the_hash_at_approval_and_at_execution()
    {
        await PrepareRuntime(executorGrant: true);   // writes execution rows by SQL as the runtime role (a database rule against a writer that CAN insert): needs the test-only executor grant
        await ReceiveDoc(Avg, 10m, 100m, "2026-03-10"); await ReceiveDoc(Avg, 5m, 50m, "2026-03-12", W2);
        const string with = "the closure and the lines of a compensation plan are written with the plan, in its creating transaction";
        var p = await ProposedPlan(Avg, "2026-03-11");
        var hash = await Hash(p);
        // a closure row in a later transaction while the plan is still proposed: refused (COPY fires the same row trigger)
        RefusedWith(with, await Rt(Closure(p, Avg.Id, W2.Id)));
        RefusedWith(with, await Rt($"INSERT INTO {CC.Table} SELECT '{p.Id}', '{Avg2.Id}', '{W1.Id}'"));
        // a closure row after the approval: refused, also inside the approving transaction itself
        RefusedWith(with, await Rt(ApprovalInsert(p), Approve(p), Closure(p, Avg.Id, W2.Id)));
        Assert.Null(await Rt(ApprovalInsert(p), Approve(p)));
        RefusedWith(with, await Rt(Closure(p, Avg.Id, W2.Id)));
        Assert.Equal((1L, hash), (await Rows(CC.Table), await Hash(p)));
        // an entry or a member on the position that could not be added: refused by the closure binding inside the live execution
        var op = Guid.NewGuid();
        RefusedWith("an entry of an epoch operation lies on a position of the approved closure of its plan",
            await Rt([ExecutionInsert(p, audit: "gen_random_uuid()"), Op(op, 1, D3), Member(op, p.Execution, 2, null), .. Move(op, Avg.Id, W2.Id, 1, 1, D3, 1m, 10m)]));
        RefusedWith(with, await Rt(ExecutionInsert(p, audit: "gen_random_uuid()"), Closure(p, Avg.Id, W2.Id)));                  // nor inside the executing transaction

        // A closure that changed all the same (only possible outside the database's own protection: the owner pausing the guard, a manipulated restore) no longer hashes to the plan's closure hash,
        // and the approval and the execution re-check it. The test pauses the closure guard as the owner to construct exactly that state.
        var q = await ProposedPlan(Avg2, "2026-03-11");
        async Task Tamper(Plan plan, Guid item, Guid warehouse) => await Sql(
            $"ALTER TABLE {CC.Table} DISABLE TRIGGER USER; INSERT INTO {CC.Table} ({CC.PlanId}, {CC.ItemId}, {CC.WarehouseId}) VALUES ('{plan.Id}', '{item}', '{warehouse}'); ALTER TABLE {CC.Table} ENABLE TRIGGER USER");
        await Tamper(q, Avg2.Id, W2.Id);
        RefusedWith("closure_hash_mismatch", await Rt(ApprovalInsert(q), Approve(q)));                                           // at approval
        await Tamper(p, Avg.Id, W2.Id);
        RefusedWith("closure_hash_mismatch", await Rt(ExecutionInsert(p, audit: "gen_random_uuid()")));                         // at execution
        Assert.Equal((0L, (short)2, (short)1), (await Rows(CX.Table), await Status(p), await Status(q)));
    }
}
