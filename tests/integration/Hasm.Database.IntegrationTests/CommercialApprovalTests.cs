using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CDA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentApproval;
using CDS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentSnapshot;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S1b, approval (SRS 4.3, 4.4; owner decisions D-050, D-051, D-052): an INDEPENDENT approver (never the creator, the last editor or the submitter), bound to the exact revision and to the approve permission of
/// the document's branch; rejection and reopening; re-approval on a material edit (the old approval never counts, the number is kept); a note edit in place; cancel of an approved document voids its number; and the PostgreSQL
/// guards behind each rule for a direct statement. Disposable cluster only.
/// </summary>
public class CommercialApprovalTests : CommercialTestBase
{
    private async Task<Guid> User(params string[] permissions) => (await Member(permissions, ScopeKind.Company, null)).User.Id;
    private string Approve => PermissionCatalog.Codes.CommercialApprove;

    [PostgresFact]
    public async Task Approval_needs_an_independent_approver_with_the_branch_permission_bound_to_the_revision_and_rejection_reopens_through_a_draft()
    {
        await Prepare();
        var branchA = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("AP1");
        var approver = await NewApprover();
        var editor = await User(PermissionCatalog.Codes.CommercialUpdate, PermissionCatalog.Codes.CommercialView);
        var submitter = await User(PermissionCatalog.Codes.CommercialSubmit, PermissionCatalog.Codes.CommercialView);
        var viewer = await User(PermissionCatalog.Codes.CommercialView);

        var d = await NewDraft(branchA, party, item, unit, "appr-key-00001");                                                    // created (and last edited) by the administrator
        var edited = Ok(await Docs.UpdateAsync(editor, Company.Id, d.Id, d.Revision, Doc(branchA, party, item, unit, "ignored-key-xx", qty: 3m)));   // the last editor is now `editor`
        var s = Ok(await Docs.SubmitAsync(submitter, Company.Id, d.Id, edited.Revision));
        Assert.Equal(submitter, s.SubmittedBy);

        // the approver is never the creator, the last editor or the submitter: refused AT the approval with a clear reason
        foreach (var notIndependent in new[] { Admin.Id, editor, submitter })
            Fails(await Docs.ApproveAsync(notIndependent, Company.Id, d.Id, s.Revision), ConfigStatus.Conflict, "approver_not_independent");
        // the approve permission is separate, branch-scoped and not held by the Company Administrator preset
        Fails(await DocMgmt.ApproveAsync(Admin.Id, Company.Id, d.Id, s.Revision), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.ApproveAsync(submitter, Company.Id, d.Id, s.Revision), ConfigStatus.Forbidden, "forbidden");
        Fails(await DocMgmt.ApproveAsync(viewer, Company.Id, d.Id, s.Revision), ConfigStatus.Forbidden, "forbidden");           // viewing is not approving
        Fails(await DocMgmt.RejectAsync(viewer, Company.Id, d.Id, s.Revision, "no"), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, d.Id))!.Status);                               // nothing changed
        // PostgreSQL refuses the same for a direct statement: an approver who submitted, or an approval without number and seal
        string Raw(string set) => $"UPDATE {CD.Table} SET {CD.Status} = 3, {set}, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d.Id}'";
        Assert.Equal("P0001", await Fails(Raw($"{CDA.ApprovedBy} = '{submitter}', {CDA.ApprovedAtUtc} = now(), {CDA.SealedAtUtc} = now(), {CDA.OfficialNumber} = 'X-1'")));   // approver = submitter
        Assert.Equal("P0001", await Fails(Raw($"{CDA.ApprovedBy} = '{Admin.Id}', {CDA.ApprovedAtUtc} = now(), {CDA.SealedAtUtc} = now(), {CDA.OfficialNumber} = 'X-1'")));   // approver = creator
        Assert.Equal("P0001", await Fails(Raw($"{CDA.ApprovedBy} = '{approver}', {CDA.ApprovedAtUtc} = now(), {CDA.SealedAtUtc} = now()")));                                  // no number
        Assert.Equal("P0001", await Fails(Raw($"{CDA.ApprovedBy} = '{approver}', {CDA.ApprovedAtUtc} = now(), {CDA.OfficialNumber} = 'X-1'")));                                 // no seal

        // bound to the exact revision
        Fails(await Docs.ApproveAsync(approver, Company.Id, d.Id, null), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Docs.ApproveAsync(approver, Company.Id, d.Id, s.Revision - 1), ConfigStatus.PreconditionFailed, "revision_conflict");
        var approved = Ok(await DocMgmt.ApproveAsync(approver, Company.Id, d.Id, s.Revision));
        Assert.Equal((CommercialStatus.Approved, approver), (approved.Status, approved.ApprovedBy ?? Guid.Empty));
        Assert.Equal(s.Revision + 1, approved.Revision);
        Assert.Equal(CommercialEventKind.Approved, approved.Events[^1].Kind);
        Fails(await Docs.ApproveAsync(approver, Company.Id, d.Id, approved.Revision), ConfigStatus.Conflict, "document_not_submitted");   // an approved document is not approved twice
        Assert.Equal(1L, await Events(CommercialAuditEvents.Approved));

        // rejection: a reason, an independent decider, then a rejected document can be edited (reopened as a draft) or cancelled
        var d2 = await NewDraft(branchA, party, item, unit, "appr-key-00002");
        var s2 = Ok(await Docs.SubmitAsync(submitter, Company.Id, d2.Id, d2.Revision));
        Invalid(await Docs.RejectAsync(approver, Company.Id, d2.Id, s2.Revision, "  "), "reason", "reason_required");
        Fails(await Docs.RejectAsync(submitter, Company.Id, d2.Id, s2.Revision, "mine"), ConfigStatus.Conflict, "approver_not_independent");
        var rejected = Ok(await DocMgmt.RejectAsync(approver, Company.Id, d2.Id, s2.Revision, "price is wrong"));
        Assert.Equal(CommercialStatus.Rejected, rejected.Status);
        Assert.Equal("price is wrong", rejected.Events[^1].Reason);
        Assert.Null(rejected.OfficialNumber);                                                                                      // a rejected document never got a number
        Fails(await Docs.ApproveAsync(approver, Company.Id, d2.Id, rejected.Revision), ConfigStatus.Conflict, "document_not_submitted");
        Fails(await Docs.SubmitAsync(submitter, Company.Id, d2.Id, rejected.Revision), ConfigStatus.Conflict, "document_not_draft");
        var reopened = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d2.Id, rejected.Revision, Doc(branchA, party, item, unit, "ignored-key-xx", qty: 4m)));
        Assert.Equal(CommercialStatus.Draft, reopened.Status);
        Assert.Equal([CommercialEventKind.Created, CommercialEventKind.Submitted, CommercialEventKind.Rejected, CommercialEventKind.Reopened, CommercialEventKind.Updated], reopened.Events.Select(e => e.Kind));
        Assert.Equal(1L, await Events(CommercialAuditEvents.Rejected));
        Assert.Equal(1L, await Events(CommercialAuditEvents.Reopened));
    }

    [PostgresFact]
    public async Task A_material_edit_sends_an_approved_document_back_a_note_does_not_the_mode_is_frozen_and_cancel_needs_a_reason()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("AP2");
        var approver = await NewApprover();
        var d = await NewDraft(branch, party, item, unit, "appr-key-00011");
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        var a = Ok(await Docs.ApproveAsync(approver, Company.Id, d.Id, s.Revision));
        var number = a.OfficialNumber;
        Assert.NotNull(number);

        // a non-material edit (the note) changes the approved document in place: still approved, same number, same approver
        var noted = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, a.Revision, Doc(branch, party, item, unit, "ignored-key-xx", note: "ring before delivery")));
        Assert.Equal(CommercialStatus.Approved, noted.Status);
        Assert.Equal((number, approver, "ring before delivery"), (noted.OfficialNumber, noted.ApprovedBy ?? Guid.Empty, noted.Note));
        Assert.Equal(a.Revision + 1, noted.Revision);
        Assert.Equal((CommercialEventKind.Updated, CommercialStatus.Approved), (noted.Events[^1].Kind, noted.Events[^1].ToStatus));
        // the mode is frozen once the document is numbered
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, noted.Revision, Doc(branch, party, item, unit, "ignored-key-xx", note: "x", mode: 2)), "mode", "mode_frozen");
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CD.Mode} = 2, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d.Id}'"));
        // PostgreSQL: an approved document changes only its note (the party, date and everything sealed stay)
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CD.DocumentDate} = '2026-06-16', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CDA.Fingerprint} = '{new string('b', 64)}', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {CL.Table} SET {CL.Quantity} = 99 WHERE {CL.DocumentId} = '{d.Id}'"));      // the lines of an approved document are locked
        Assert.Equal("P0001", await Fails($"DELETE FROM {CL.Table} WHERE {CL.DocumentId} = '{d.Id}'"));

        // a MATERIAL edit sends it back to draft through re-approval: the approval, the seal and the snapshots are dropped, the number is kept
        var material = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, noted.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 7m, note: "ring before delivery")));
        Assert.Equal(CommercialStatus.Draft, material.Status);
        Assert.Equal(number, material.OfficialNumber);
        Assert.Null(material.ApprovedBy); Assert.Null(material.SealedAtUtc); Assert.Null(material.Fingerprint); Assert.Null(material.TotalGross);
        Assert.All(material.Lines, l => { Assert.Null(l.LineNet); Assert.Null(l.PricingHash); });
        Assert.Equal([CommercialEventKind.Created, CommercialEventKind.Submitted, CommercialEventKind.Approved, CommercialEventKind.Updated, CommercialEventKind.ReapprovalRequested, CommercialEventKind.Updated], material.Events.Select(e => e.Kind));
        Fails(await Docs.ApproveAsync(approver, Company.Id, d.Id, material.Revision), ConfigStatus.Conflict, "document_not_submitted");   // the OLD approval does not count: it must be submitted and approved again
        var s2 = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, material.Revision));
        var again = Ok(await Docs.ApproveAsync(approver, Company.Id, d.Id, s2.Revision));
        Assert.Equal((number, CommercialStatus.Approved), (again.OfficialNumber, again.Status));
        Assert.Equal(1L, await Events(CommercialAuditEvents.ReapprovalRequested));

        // cancel: a reason is mandatory; an approved document with no effect may be cancelled (D-051) and its number is voided
        Invalid(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, again.Revision, " "), "reason", "reason_required");
        var cancelled = Ok(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, again.Revision, "order withdrawn by the customer"));
        Assert.Equal(CommercialStatus.Cancelled, cancelled.Status);
        Assert.Equal(number, cancelled.OfficialNumber);
        Assert.Equal("order withdrawn by the customer", cancelled.Events[^1].Reason);
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CD.Note} = 'x', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d.Id}'"));   // a cancelled document is final
        Fails(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, cancelled.Revision, "again"), ConfigStatus.Conflict, "document_not_cancellable");
    }

    [PostgresFact]
    public async Task Events_are_bound_to_the_transition_they_describe_and_the_branch_permissions_of_approval_are_enforced_over_the_service()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("AP3");
        var d = await NewDraft(branch, party, item, unit, "appr-key-00021");
        string Event(int kind, int from, int to, long revision) => $"INSERT INTO {CE.Table} ({CE.Id}, {CE.DocumentId}, {CE.Kind}, {CE.FromStatus}, {CE.ToStatus}, {CE.Revision}, {CE.ActorUserId}, {CE.AtUtc}) VALUES (gen_random_uuid(), '{d.Id}', {kind}, {from}, {to}, {revision}, '{Admin.Id}', now())";
        // the document is a draft at revision 1: an event with its current revision and status must still be a transition the guard allows
        Assert.Equal("P0001", await Fails(Event(2, 3, 1, 1)));                                                                  // an "updated" event may not come from approved
        Assert.Equal("P0001", await Fails(Event(8, 3, 1, 1)));                                                                  // a re-approval event needs the previous event to have left the document approved
        Assert.Equal("P0001", await Fails(Event(9, 4, 1, 1)));                                                                  // so does a reopening
        Assert.Equal("P0001", await Fails(Event(4, 2, 1, 1)));                                                                  // and a withdrawal
        Assert.Equal("P0001", await Fails(Event(6, 2, 3, 1)));                                                                  // an approval event for a draft (status mismatch)
        Assert.Equal("P0001", await Fails(Event(1, 1, 1, 1)));                                                                  // a "created" event has no from-status
        // an event continues the status the previous one left: after a real submit, a fake event from draft is refused
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        Assert.Equal(CommercialStatus.Submitted, s.Status);
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CD.Status} = 1, {CD.SubmittedBy} = NULL, {CDA.Fingerprint} = NULL, {CDA.TaxSnapshot} = NULL, {CDA.TaxHash} = NULL, {CDA.ExchangeRate} = NULL, {CDA.ExchangeRateRef} = NULL, {CDA.TotalNet} = NULL, {CDA.TotalTax} = NULL, {CDA.TotalGross} = NULL, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{d.Id}'" +
            $"; INSERT INTO {CE.Table} ({CE.Id}, {CE.DocumentId}, {CE.Kind}, {CE.FromStatus}, {CE.ToStatus}, {CE.Revision}, {CE.ActorUserId}, {CE.AtUtc}) VALUES (gen_random_uuid(), '{d.Id}', 3, 1, 1, {s.Revision + 1}, '{Admin.Id}', now())"));   // a "submitted" event that ends in draft
        var approver = await NewApprover();
        // listing and reading stay branch-scoped for the approver as for everybody else
        Assert.Equal(CommercialStatus.Approved, Ok(await Docs.ApproveAsync(approver, Company.Id, d.Id, s.Revision)).Status);
        var (viewerOther, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Branch, await NewBranchId("BX"));
        Fails(await DocMgmt.GetAsync(viewerOther.Id, Company.Id, d.Id), ConfigStatus.Forbidden, "forbidden");
    }

}
