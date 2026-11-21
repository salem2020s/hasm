using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Npgsql;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S1a, state machine: every legal transition through the service (draft, update, submit, withdraw, cancel) with its revision fence, event row and audit event, and every illegal transition and
/// forbidden edit refused by PostgreSQL itself for a direct statement (approval, rejection and cancel-of-approved are NOT in the S1a guard). Disposable cluster only.
/// </summary>
public class CommercialStateMachineTests : CommercialTestBase
{
    [PostgresFact]
    public async Task The_service_walks_draft_submit_withdraw_and_cancel_with_fences_events_and_audit()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("SM1");
        var d = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "key-state-0001", note: " first ")));
        Assert.Equal(CommercialStatus.Draft, d.Status); Assert.Equal(1L, d.Revision); Assert.Equal("JOD", d.Currency); Assert.Equal("first", d.Note); Assert.Equal(CommercialMode.Direct, d.Mode);
        Assert.Single(d.Lines);
        Assert.Equal(CommercialEventKind.Created, Assert.Single(d.Events).Kind);

        Fails(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, null, Doc(branch, party, item, unit, "ignored-key-xx")), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, 9, Doc(branch, party, item, unit, "ignored-key-xx")), ConfigStatus.PreconditionFailed, "revision_conflict");
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, 1, Doc(branch, party, item, unit, "ignored-key-xx") with { DocType = "other_type" }), "docType", "doc_type_immutable");
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, 1, Doc(await NewBranchId("BB"), party, item, unit, "ignored-key-xx")), "branchId", "branch_immutable");
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, 1, Doc(branch, party, item, unit, "ignored-key-xx", qty: 0m)), "lines[0].quantity", "out_of_range");
        var edited = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, 1, Doc(branch, party, item, unit, "ignored-key-xx", qty: 5m, note: "second", mode: 2)));
        Assert.Equal(2L, edited.Revision); Assert.Equal(5m, edited.Lines.Single().Quantity); Assert.Equal(CommercialMode.Separate, edited.Mode); Assert.Equal("second", edited.Note);

        var noLines = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "key-state-0002") with { Lines = [] }));
        Invalid(await Docs.SubmitAsync(Admin.Id, Company.Id, noLines.Id, 1), "lines", "lines_required");                 // a document needs at least one line to be submitted
        Fails(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, null), ConfigStatus.PreconditionRequired, "precondition_required");
        var submitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, 2));
        Assert.Equal(CommercialStatus.Submitted, submitted.Status); Assert.Equal(3L, submitted.Revision); Assert.Equal(Admin.Id, submitted.SubmittedBy);
        Fails(await Docs.UpdateAsync(Admin.Id, Company.Id, d.Id, 3, Doc(branch, party, item, unit, "ignored-key-xx")), ConfigStatus.Conflict, "document_not_draft");
        Fails(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, 3), ConfigStatus.Conflict, "document_not_draft");

        var withdrawn = Ok(await Docs.WithdrawAsync(Admin.Id, Company.Id, d.Id, 3));
        Assert.Equal(CommercialStatus.Draft, withdrawn.Status); Assert.Equal(4L, withdrawn.Revision);
        Assert.Null(withdrawn.SubmittedBy);
        Fails(await Docs.WithdrawAsync(Admin.Id, Company.Id, d.Id, 4), ConfigStatus.Conflict, "document_not_submitted");
        Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, 4));
        Invalid(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, 5, "  "), "reason", "reason_required");
        var cancelled = Ok(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, 5, "customer withdrew"));
        Assert.Equal(CommercialStatus.Cancelled, cancelled.Status); Assert.Equal(6L, cancelled.Revision);
        Fails(await Docs.CancelAsync(Admin.Id, Company.Id, d.Id, 6, "again"), ConfigStatus.Conflict, "document_not_cancellable");
        Fails(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, 6), ConfigStatus.Conflict, "document_not_draft");
        Fails(await Docs.UpdateAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, Doc(branch, party, item, unit, "ignored-key-xx")), ConfigStatus.NotFound, "commercial_document_not_found");

        var reloaded = (await Docs.GetAsync(Company.Id, d.Id))!;
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], reloaded.Events.Select(e => e.Revision));                                  // exactly one event per revision
        Assert.Equal([CommercialEventKind.Created, CommercialEventKind.Updated, CommercialEventKind.Submitted, CommercialEventKind.Withdrawn, CommercialEventKind.Submitted, CommercialEventKind.Cancelled], reloaded.Events.Select(e => e.Kind));
        Assert.Equal("customer withdrew", reloaded.Events[^1].Reason);
        Assert.Equal(2L, await Events(CommercialAuditEvents.Created));                                                        // this document and the one without lines
        Assert.Equal(1L, await Events(CommercialAuditEvents.Updated));
        Assert.Equal(2L, await Events(CommercialAuditEvents.Submitted));
        Assert.Equal(1L, await Events(CommercialAuditEvents.Withdrawn));
        Assert.Equal(1L, await Events(CommercialAuditEvents.Cancelled));
    }

    [PostgresFact]
    public async Task PostgreSQL_allows_only_the_s1a_transitions_and_refuses_edits_of_submitted_and_cancelled_documents_even_for_a_direct_statement()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("SM2");
        var draft = await NewDraft(branch, party, item, unit, "key-guard-0001");
        var submitted = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, (await NewDraft(branch, party, item, unit, "key-guard-0002")).Id, 1));
        var cancelled = Ok(await Docs.CancelAsync(Admin.Id, Company.Id, (await NewDraft(branch, party, item, unit, "key-guard-0003")).Id, 1, "no longer needed"));
        var other = await NewBranchId("BX");
        string Up(Guid id, string set) => $"UPDATE {CD.Table} SET {set}, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{id}'";
        string RawInsert(string key, int mode, int status) => $"INSERT INTO {CD.Table} ({CD.Id}, {CD.DocType}, {CD.BranchId}, {CD.PartyId}, {CD.Currency}, {CD.DocumentDate}, {CD.Mode}, {CD.Status}, {CD.IdempotencyKey}, {CD.PayloadHash}, {CD.Revision}, {CD.CreatedBy}, {CD.CreatedAtUtc}, {CD.UpdatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), 'commercial_test', '{branch}', '{party}', 'JOD', '2026-06-15', {mode}, {status}, '{key}', '{new string('a', 64)}', 1, '{Admin.Id}', now(), now())";

        // approval, rejection and cancel-of-approved are NOT in the S1a guard (they arrive with S1b through a new migration)
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.Status} = 3")));
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.Status} = 4")));
        Assert.Equal("P0001", await Fails(Up(submitted.Id, $"{CD.Status} = 3")));
        Assert.Equal("P0001", await Fails(Up(submitted.Id, $"{CD.Status} = 4")));
        Assert.Equal("P0001", await Fails(Up(cancelled.Id, $"{CD.Status} = 1")));                                          // a cancelled document is final
        Assert.Equal("P0001", await Fails(Up(cancelled.Id, $"{CD.Status} = 2, {CD.SubmittedBy} = '{Admin.Id}'")));
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.Status} = 2")));                                              // a submitter is required
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.Status} = 2, {CD.SubmittedBy} = '{Admin.Id}', {CD.Note} = 'edited while submitting'")));   // a status change edits nothing else
        Assert.Equal("P0001", await Fails(Up(submitted.Id, $"{CD.Note} = 'edited while submitted'")));                     // a submitted document is not edited
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CD.Note} = 'x' WHERE {CD.Id} = '{draft.Id}'"));          // the revision must advance by one
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.DocType} = 'other_type'")));                                  // identity is immutable
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.BranchId} = '{other}'")));
        Assert.Equal("P0001", await Fails(Up(draft.Id, $"{CD.IdempotencyKey} = 'another-key-01'")));
        Assert.Null(await Fails(Up(draft.Id, $"{CD.Note} = 'a draft may change'")));                                        // a draft may be edited (rolled back by the helper)
        Assert.Equal("P0001", await Fails($"DELETE FROM {CD.Table} WHERE {CD.Id} = '{draft.Id}'"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {CD.Table} CASCADE"));
        Assert.Equal("P0001", await Fails(RawInsert("raw-key-0001", 1, 2)));                                               // never created submitted
        Assert.Equal("23514", await Fails(RawInsert("raw-key-0002", 3, 1)));                                               // mode must be 1 or 2

        // lines: editable only while the document is a draft
        Assert.Equal("P0001", await Fails($"UPDATE {CL.Table} SET {CL.Quantity} = 9 WHERE {CL.DocumentId} = '{submitted.Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {CL.Table} WHERE {CL.DocumentId} = '{submitted.Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {CL.Table} WHERE {CL.DocumentId} = '{cancelled.Id}'"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {CL.Table} ({CL.Id}, {CL.DocumentId}, {CL.LineNo}, {CL.ItemId}, {CL.UnitId}, {CL.Quantity}) VALUES (gen_random_uuid(), '{submitted.Id}', 2, '{item}', '{unit}', 1)"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {CL.Table}"));
        Assert.Equal("23514", await Fails($"UPDATE {CL.Table} SET {CL.Quantity} = 0 WHERE {CL.DocumentId} = '{draft.Id}'"));
        Assert.Equal("23505", await Fails($"INSERT INTO {CL.Table} ({CL.Id}, {CL.DocumentId}, {CL.LineNo}, {CL.ItemId}, {CL.UnitId}, {CL.Quantity}) VALUES (gen_random_uuid(), '{draft.Id}', 1, '{item}', '{unit}', 1)"));   // one line number per document

        // events: append-only, one per revision
        Assert.Equal("P0001", await Fails($"UPDATE {CE.Table} SET {CE.Reason} = 'x'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {CE.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {CE.Table}"));
        string RawEvent(Guid doc, int toStatus, long revision) => $"INSERT INTO {CE.Table} ({CE.Id}, {CE.DocumentId}, {CE.Kind}, {CE.ToStatus}, {CE.Revision}, {CE.ActorUserId}, {CE.AtUtc}) VALUES (gen_random_uuid(), '{doc}', 2, {toStatus}, {revision}, '{Admin.Id}', now())";
        Assert.Equal("P0001", await Fails(RawEvent(draft.Id, 3, 1)));                                                      // a forged "approved" event is refused: it must carry the document's CURRENT status
        Assert.Equal("P0001", await Fails(RawEvent(draft.Id, 1, 2)));                                                      // so is an event pre-inserted for a future revision
        Assert.Equal("P0001", await Fails(RawEvent(Guid.NewGuid(), 1, 1)));                                                // and one for no document at all
        Assert.Equal("23505", await Fails($"INSERT INTO {CE.Table} ({CE.Id}, {CE.DocumentId}, {CE.Kind}, {CE.ToStatus}, {CE.Revision}, {CE.ActorUserId}, {CE.AtUtc}) VALUES (gen_random_uuid(), '{draft.Id}', 2, 1, 1, '{Admin.Id}', now())"));
    }

    [PostgresFact]
    public async Task A_line_write_can_never_commit_beside_a_concurrent_submit()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("SM3");
        var draft = await NewDraft(branch, party, item, unit, "key-race-0001");
        var connectionString = Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName);
        await using var submitter = new NpgsqlConnection(connectionString); await submitter.OpenAsync();
        await using var writer = new NpgsqlConnection(connectionString); await writer.OpenAsync();
        await using var tx1 = await submitter.BeginTransactionAsync();
        await using (var up = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.Status} = 2, {CD.SubmittedBy} = '{Admin.Id}', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{draft.Id}'", submitter, tx1)) await up.ExecuteNonQueryAsync();   // the submit is in flight, not yet committed
        var write = Task.Run(async () =>
        {
            try
            {
                await using var tx2 = await writer.BeginTransactionAsync();
                await using var ins = new NpgsqlCommand($"INSERT INTO {CL.Table} ({CL.Id}, {CL.DocumentId}, {CL.LineNo}, {CL.ItemId}, {CL.UnitId}, {CL.Quantity}) VALUES (gen_random_uuid(), '{draft.Id}', 2, '{item}', '{unit}', 1)", writer, tx2);
                await ins.ExecuteNonQueryAsync();
                await tx2.CommitAsync();
                return (string?)null;
            }
            catch (PostgresException ex) { return ex.SqlState; }
        });
        await Task.Delay(1500);
        Assert.False(write.IsCompleted, "the line write must wait for the in-flight status change (it reads the status with a share lock)");
        await tx1.CommitAsync();
        Assert.Equal("P0001", await write);                                                                                 // after the submit committed, the draft rule refuses the line
        Assert.Single((await Docs.GetAsync(Company.Id, draft.Id))!.Lines);                                                  // no line committed beside the submit
    }
}
