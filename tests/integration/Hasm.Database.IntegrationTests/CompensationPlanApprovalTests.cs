using System.Data;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanEvent;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.8D step 5, slice 2: the plan APPROVAL service against real PostgreSQL through the real runtime role. Approval re-runs the read-only planner under the APPROVER's permissions and compares every fingerprint
/// (a mismatch marks the plan stale in its own transaction); the approval row and the status change are written together; the database refusals (approver_not_independent, document_not_approved,
/// document_already_posted, ...) come back as result codes; reject and cancel are history events. Service-level variants of N3, N5, N24 and N32 and of the X7 and L1 refusals. Nothing executes; no endpoint.
/// </summary>
public class CompensationPlanApprovalTests : CompensationPlanTestBase
{
    [PostgresRoleFact]
    public async Task An_independent_approver_approves_the_plan_with_its_approval_row_event_and_audit_event_in_one_transaction()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        var (_, _, plan) = await Proposed(creator);
        var cost = (await Rows(EN.Table), await Rows(OP.Table), await Rows(SP.Table));
        var approved = await Approval().ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision, "checked the lines");
        Assert.True(approved.IsOk, approved.Code);
        var stored = approved.Value!;
        Assert.Equal(((short)2, 2L, approver.Id), (stored.Status, stored.Revision, stored.ApprovedBy));
        Assert.NotNull(stored.ApprovalId);
        // the approval row repeats every fingerprint of the plan and its closure hash; the plan names it
        Assert.Equal(1L, await Rows(CA.Table));
        Assert.Equal((plan.Revision, plan.ClosureHash, approver.Id, "checked the lines"),
            (await Sql<long>($"SELECT {CA.PlanRevision} FROM {CA.Table}"), (await Sql<string>($"SELECT {CA.ClosureHash}::text FROM {CA.Table}")).Trim(), await Sql<Guid>($"SELECT {CA.Approver} FROM {CA.Table}"), await Sql<string>($"SELECT {CA.Note} FROM {CA.Table}")));
        for (var i = 0; i < 7; i++) Assert.Equal(plan.Fingerprints.All[i], (await Sql<string>($"SELECT {ApprovalFingerprints[i]}::text FROM {CA.Table}")).Trim());
        Assert.Equal((1L, 1L), (await EventCount(plan.Id, 1), await EventCount(plan.Id, 2)));
        Assert.Equal(1L, await AuditCount(CostingAuditEvents.PlanApproved, plan.Id));
        Assert.Equal(cost, (await Rows(EN.Table), await Rows(OP.Table), await Rows(SP.Table)));      // approving writes no cost entry, operation or posting
        Assert.Empty(await Cost.ReconcileAsync(Company.Id));
        // an approved plan is approved once: a second approval (even with the new revision) and a reject are refused
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, plan.Id, stored.Revision), ConfigStatus.Conflict, "plan_not_proposed");
        Refused(await Approval().RejectAsync(approver.Id, Company.Id, plan.Id, stored.Revision), ConfigStatus.Conflict, "plan_not_proposed");
        Assert.Equal(1L, await Rows(CA.Table));
    }

    [PostgresRoleFact]
    public async Task N03_the_approver_is_never_the_creator_or_the_editor_or_the_submitter_of_the_event_document()
    {
        await Prepare();
        var editor = await PlanMember([.. PlannerPermissions, PermissionCatalog.Codes.StockDocsUpdate], [PermissionCatalog.Codes.CompensationPlansApprove]);
        var submitter = await PlanMember([.. PlannerPermissions, PermissionCatalog.Codes.StockDocsSubmit], [PermissionCatalog.Codes.CompensationPlansApprove]);
        var creator = await PlanMember(PlannerPermissions, [PermissionCatalog.Codes.CompensationPlansCreate, PermissionCatalog.Codes.CompensationPlansApprove]);   // may also approve in general
        var independent = await ApproverUser();
        // the event document: created by the administrator, edited by one person, submitted by another, approved by a third
        var input = Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m));
        var created = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, input));
        var edited = Ok(await Docs.UpdateAsync(editor.Id, Company.Id, created.Id, created.Revision, input with { Notes = "edited" }));
        var submitted = Ok(await Docs.SubmitAsync(submitter.Id, Company.Id, created.Id, edited.Revision));
        var doc = Ok(await Docs.ApproveAsync((await Approver()).Id, Company.Id, created.Id, submitted.Revision));
        var made = await Service().CreateAsync(creator.Id, Company.Id, new(Avg.Id, W1.Id, doc.Id, new ProposedFlowOperation(Guid.NewGuid(), DateOnly.Parse("2026-03-11"), CostOperationKind.Receipt, [new ProposedFlowLine(W1.Id, 10m, 200m)])));
        Assert.True(made.IsOk, made.Code);
        var plan = made.Value!;
        foreach (var who in new[] { creator, editor, submitter })
            Refused(await Approval().ApproveAsync(who.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Conflict, "approver_not_independent");
        Assert.Equal(((short)1, 0L, 0L), (await PlanStatus(plan.Id), await Rows(CA.Table), await AuditCount(CostingAuditEvents.PlanApproved, plan.Id)));
        // the person who approved the document is neither its creator, editor nor submitter: independent
        Assert.True((await Approval().ApproveAsync(independent.Id, Company.Id, plan.Id, plan.Revision)).IsOk);
    }

    [PostgresRoleFact]
    public async Task Approving_needs_the_approve_permission_on_the_root_warehouse_an_existing_proposed_plan_and_the_revision_the_caller_saw()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        var (_, _, plan) = await Proposed(creator);
        var service = Approval();
        var other = await PlanMember(PlannerPermissions, [PermissionCatalog.Codes.CompensationPlansApprove], ScopeKind.Warehouse, W2.Id);   // approves only on another warehouse
        var onlyCreate = await PlanUser();
        var blind = await PlanMember([PermissionCatalog.Codes.StockDocsView], [PermissionCatalog.Codes.CompensationPlansApprove]);         // may approve but cannot see the stock
        Refused(await service.ApproveAsync(other.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Forbidden, "forbidden");
        Refused(await service.ApproveAsync(onlyCreate.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Forbidden, "forbidden");
        Refused(await service.ApproveAsync(Admin.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Forbidden, "forbidden");          // no preset holds the code
        Refused(await service.ApproveAsync(blind.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Forbidden, "forbidden");          // the planner's own refusal under the approver's permissions
        Refused(await service.ApproveAsync(approver.Id, Company.Id, Guid.NewGuid(), 1), ConfigStatus.NotFound, "plan_not_found");
        Refused(await service.ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision + 1), ConfigStatus.PreconditionFailed, "revision_mismatch");
        Assert.Equal(plan.Revision, (await service.ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision + 1)).CurrentRevision);
        Refused(await service.ApproveAsync(approver.Id, Company.Id, plan.Id, 0), ConfigStatus.Invalid, "invalid_plan_input");
        Refused(await service.ApproveAsync(Guid.Empty, Company.Id, plan.Id, plan.Revision), ConfigStatus.Invalid, "invalid_plan_input");
        Refused(await service.ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision, new string('x', 501)), ConfigStatus.Invalid, "invalid_plan_input");
        Assert.Equal(((short)1, plan.Revision, 0L), (await PlanStatus(plan.Id), await PlanRevision(plan.Id), await Rows(CA.Table)));
    }

    [PostgresRoleFact]
    public async Task N24_a_plan_whose_history_or_accounting_changed_is_marked_stale_in_its_own_transaction_and_is_never_approved()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        var (_, _, byHistory) = await Proposed(creator);
        // the position's history changes after the plan was written: another ordinary receipt
        await PostJournal(await ReceiveDoc(Avg, 3m, 30m, "2026-03-12"));
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, byHistory.Id, byHistory.Revision), ConfigStatus.Conflict, "plan_stale");
        Assert.Equal(((short)6, byHistory.Revision + 1, 1L, 1L, 0L), (await PlanStatus(byHistory.Id), await PlanRevision(byHistory.Id), await EventCount(byHistory.Id, 6), await AuditCount(CostingAuditEvents.PlanStaled, byHistory.Id), await Rows(CA.Table)));
        // a stale plan is final: it is neither approved, rejected nor cancelled again, and its document is released for a new plan
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, byHistory.Id, byHistory.Revision + 1), ConfigStatus.Conflict, "plan_not_proposed");

        // the accounting evidence changes (an account of the booked effects is revised): a different plan, the same rule
        var (_, _, byAccount) = await Proposed(creator, "2026-03-11");
        await Sql($"UPDATE {AC.Table} SET {AC.Revision} = {AC.Revision} + 1 WHERE {AC.Id} = '{CogsAcc.Id}'");
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, byAccount.Id, byAccount.Revision), ConfigStatus.Conflict, "plan_stale");
        Assert.Equal(((short)6, 0L), (await PlanStatus(byAccount.Id), await Rows(CA.Table)));
    }

    [PostgresRoleFact]
    public async Task A_person_who_is_not_independent_cannot_drive_the_stale_transition_of_a_plan_whose_history_changed()
    {
        // review L2 of the slices 1 and 2: the database also refuses a non-independent approver at the approval row, but only AFTER the planner ran; without the service pre-check such a person could mark a plan stale
        await Prepare();
        var creator = await PlanMember(PlannerPermissions, [PermissionCatalog.Codes.CompensationPlansCreate, PermissionCatalog.Codes.CompensationPlansApprove]);   // may approve plans in general, never his own
        var independent = await ApproverUser();
        var (_, _, plan) = await Proposed(creator);
        await PostJournal(await ReceiveDoc(Avg, 3m, 30m, "2026-03-12"));      // the history changed: the planner would now disagree with the plan
        Refused(await Approval().ApproveAsync(creator.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Conflict, "approver_not_independent");
        Assert.Equal(((short)1, plan.Revision, 0L, 0L, 0L), (await PlanStatus(plan.Id), await PlanRevision(plan.Id), await EventCount(plan.Id, 6), await AuditCount(CostingAuditEvents.PlanStaled, plan.Id), await Rows(CA.Table)));
        // an independent approver does reach the planner, and the very same plan is then stale: the refused call above is the only reason it was still proposed
        Refused(await Approval().ApproveAsync(independent.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Conflict, "plan_stale");
        Assert.Equal(((short)6, 1L), (await PlanStatus(plan.Id), await EventCount(plan.Id, 6)));
    }

    [PostgresRoleFact]
    public async Task A_cancelled_event_document_is_refused_before_the_planner_so_a_changed_history_does_not_mark_the_plan_stale()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        var (doc, _, plan) = await Proposed(creator);
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, doc.Id, doc.Revision, "withdrawn"));
        await PostJournal(await ReceiveDoc(Avg, 3m, 30m, "2026-03-12"));
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Conflict, "document_not_approved");
        Assert.Equal(((short)1, plan.Revision, 0L, 0L), (await PlanStatus(plan.Id), await PlanRevision(plan.Id), await EventCount(plan.Id, 6), await AuditCount(CostingAuditEvents.PlanStaled, plan.Id)));
    }

    [PostgresRoleFact]
    public async Task A_planner_refusal_at_approval_is_returned_unchanged_and_does_not_mark_the_plan_stale()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        var (_, _, plan) = await Proposed(creator);
        await Sql(PeriodStatus("2026-03-12", 2));   // March Soft Closed after the plan was written
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Conflict, PostingPeriodCheck.SoftClosed);
        Assert.Equal(((short)1, plan.Revision, 0L, 0L), (await PlanStatus(plan.Id), await PlanRevision(plan.Id), await EventCount(plan.Id, 6), await Rows(CA.Table)));
    }

    [PostgresRoleFact]
    public async Task X7_and_L1_the_event_document_must_still_be_approved_at_the_planned_revision_and_not_posted()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        // the document is cancelled after the plan was written
        var (cancelledDoc, _, cancelled) = await Proposed(creator);
        Ok(await Docs.CancelAsync(Admin.Id, Company.Id, cancelledDoc.Id, cancelledDoc.Revision, "withdrawn"));
        Refused(await Approval().ApproveAsync(approver.Id, Company.Id, cancelled.Id, cancelled.Revision), ConfigStatus.Conflict, "document_not_approved");
        Assert.Equal((short)1, await PlanStatus(cancelled.Id));

        // the document was posted by a session on a REPEATABLE READ snapshot older than the plan (the plan is invisible to it): the posting commits, the plan can then never be approved
        var (doc, input) = await Event("2026-03-11");     // the document exists before the snapshot (a posting row needs it); the plan does not
        await using (var conn = await AsRuntime())
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await using (var snapshot = new NpgsqlCommand($"SELECT count(*) FROM {CP.Table}", conn, tx)) await snapshot.ExecuteScalarAsync();
            var plan = (await Service().CreateAsync(creator.Id, Company.Id, input)).Value!;      // another session, after the snapshot
            var op = Guid.NewGuid();
            foreach (var q in (string[])[Op(op, 1, D3), .. Move(op, Avg.Id, W1.Id, 1, 1, D3, 10m, 50m), PostingRow(new Plan { Doc = doc, Item = Avg.Id, Warehouse = W1.Id }, op, D3, 50m)])
            { await using var cmd = new NpgsqlCommand(q, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            Refused(await Approval().ApproveAsync(approver.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Conflict, "document_already_posted");
            Assert.Equal(((short)1, 0L), (await PlanStatus(plan.Id), await Rows(CA.Table)));
        }
    }

    [PostgresRoleFact]
    public async Task Reject_and_cancel_are_history_events_that_release_the_document_and_end_the_plan()
    {
        await Prepare();
        var creator = await PlanUser();
        var approver = await ApproverUser();
        var service = Approval();

        // reject: an approver only (not the creator, who holds no approve permission), a proposed plan only
        var (doc, input, plan) = await Proposed(creator);
        Refused(await service.RejectAsync(creator.Id, Company.Id, plan.Id, plan.Revision), ConfigStatus.Forbidden, "forbidden");
        Refused(await service.RejectAsync(approver.Id, Company.Id, plan.Id, plan.Revision + 1), ConfigStatus.PreconditionFailed, "revision_mismatch");
        var rejected = await service.RejectAsync(approver.Id, Company.Id, plan.Id, plan.Revision, "wrong amount");
        Assert.True(rejected.IsOk, rejected.Code);
        Assert.Equal(((short)4, plan.Revision + 1, 1L, 1L, 0L), (rejected.Value!.Status, rejected.Value.Revision, await EventCount(plan.Id, 3), await AuditCount(CostingAuditEvents.PlanRejected, plan.Id), await Rows(CA.Table)));
        Refused(await service.RejectAsync(approver.Id, Company.Id, plan.Id, rejected.Value.Revision), ConfigStatus.Conflict, "plan_not_proposed");
        Refused(await service.ApproveAsync(approver.Id, Company.Id, plan.Id, rejected.Value.Revision), ConfigStatus.Conflict, "plan_not_proposed");
        Refused(await service.CancelAsync(approver.Id, Company.Id, plan.Id, rejected.Value.Revision), ConfigStatus.Conflict, "plan_not_open");
        // the document is released: a new plan can be written for it
        Assert.True((await Service().CreateAsync(creator.Id, Company.Id, input with { Proposal = input.Proposal with { Id = Guid.NewGuid() } })).IsOk);

        // cancel: by the creator of a proposed plan, or by an approver, also after the approval; a stranger cannot
        var (_, _, mine) = await Proposed(creator, "2026-03-11");
        var stranger = await PlanMember(PlannerPermissions, [PermissionCatalog.Codes.CompensationPlansCreate]);
        Refused(await service.CancelAsync(stranger.Id, Company.Id, mine.Id, mine.Revision), ConfigStatus.Forbidden, "forbidden");
        var cancelled = await service.CancelAsync(creator.Id, Company.Id, mine.Id, mine.Revision, "not needed");
        Assert.True(cancelled.IsOk, cancelled.Code);
        Assert.Equal(((short)5, 1L, 1L), (cancelled.Value!.Status, await EventCount(mine.Id, 4), await AuditCount(CostingAuditEvents.PlanCancelled, mine.Id)));

        var (_, _, approvedPlan) = await Proposed(creator, "2026-03-11");
        var approved = (await service.ApproveAsync(approver.Id, Company.Id, approvedPlan.Id, approvedPlan.Revision)).Value!;
        Refused(await service.CancelAsync(stranger.Id, Company.Id, approvedPlan.Id, approved.Revision), ConfigStatus.Forbidden, "forbidden");
        var late = await service.CancelAsync(approver.Id, Company.Id, approvedPlan.Id, approved.Revision, "superseded");
        Assert.True(late.IsOk, late.Code);
        Assert.Equal(((short)5, 1L), (late.Value!.Status, await Rows(CA.Table)));          // the approval row stays (history is never removed)
    }
}
