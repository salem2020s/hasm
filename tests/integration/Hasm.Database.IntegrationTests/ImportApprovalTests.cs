using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using Ib = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using Ir = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportRow;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// A test-only adapter that writes REAL business rows (cost centers) so that atomicity, rollback and concurrency are proven against PostgreSQL, not an in-memory stand-in. It takes an advisory lock as its
/// domain lock, reads existing rows through the analysis snapshot only and writes only inside the application transaction.
/// </summary>
public sealed class CostCenterTestAdapter : IImportAdapter
{
    public int? FailAfter; public int Applied; public int ApplyCalls;
    public string Code => "ccenters"; public int Version => 1; public string NameAr => "مراكز تكلفة"; public string NameEn => "Cost centers";
    public string UploadPermission => "company.accounts.create"; public string ViewPermission => "company.accounts.view";
    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "الرمز", "Code", ImportFieldType.Code, Required: true, MaxLength: 30, IsKey: true, UpperCase: true),
        new("name", "الاسم", "Name", ImportFieldType.Text, Required: true, MaxLength: 200),
    ];

    public async Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        var existing = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using (var q = new NpgsqlCommand($"SELECT upper({Cc.Code}), {Cc.NameEn} FROM {Cc.Table} ORDER BY upper({Cc.Code})", context.Connection, context.Transaction))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) existing[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        var result = new List<RowAnalysis>(); var creates = 0;
        foreach (var row in rows)
        {
            var code = row.Get("code")!; var name = row.Get("name")!;
            if (existing.TryGetValue(code, out var cur))
                result.Add(cur == name ? new RowAnalysis(row.RowNo, RowOutcome.Exact, Explanation: "exists")
                                       : new RowAnalysis(row.RowNo, RowOutcome.Conflict, [new RowIssue("code", "exists_with_other_name", cur)], [ImportResolutions.Skip], Explanation: "exists with another name"));
            else { creates++; result.Add(new RowAnalysis(row.RowNo, RowOutcome.Valid, Operation: new JsonObject { ["type"] = "create-cost-center", ["code"] = code, ["name"] = name }, Explanation: "will be created")); }
        }
        var baseline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', existing.Select(e => $"{e.Key}={e.Value}"))))).ToLowerInvariant();
        return new AnalysisResult(result, baseline, [new ReconLine("cost_centers_to_create", creates.ToString())]);
    }

    public async Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1212, 1)", work.Connection, work.Transaction);
        await lk.ExecuteNonQueryAsync(ct);
    }

    public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref ApplyCalls);
        var n = 0;
        foreach (var op in context.Operations)
        {
            if (FailAfter is { } limit && n >= limit) throw new ImportApplyException("simulated_failure", "the adapter failed after " + n + " operations");
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {Cc.Table} ({Cc.Id}, {Cc.Code}, {Cc.NameAr}, {Cc.NameEn}, {Cc.IsGroup}, {Cc.IsActive}, {Cc.Revision}, {Cc.CreatedAtUtc}, {Cc.UpdatedAtUtc}) VALUES (gen_random_uuid(), @c, @n, @n, false, true, 1, now(), now())", context.Work.Connection, context.Work.Transaction);
            ins.Parameters.AddWithValue("c", op.Operation["code"]!.GetValue<string>()); ins.Parameters.AddWithValue("n", op.Operation["name"]!.GetValue<string>());
            await ins.ExecuteNonQueryAsync(ct);
            n++;
        }
        Interlocked.Add(ref Applied, n);
        return new ImportApplyResult(new JsonObject { ["created"] = n });
    }
}

/// <summary>Independent approval and controlled, atomic application of imports (Phase 3.2D S3) against real PostgreSQL; disposable cluster only.</summary>
public class ImportApprovalTests : ImportTestBase
{
    private CostCenterTestAdapter Adapter = null!;
    private static readonly JsonObject Map = JsonNode.Parse("""{"fields":{"code":{"column":0},"name":{"column":1}}}""")!.AsObject();

    private async Task Prepare()
    {
        Adapter = new CostCenterTestAdapter();
        await PrepareImports(adapters: [Adapter]);
    }

    private async Task<ImportBatchView> ValidatedBatch(string rows = "CC1,One\nCC2,Two\nCC3,Three\nCC4,Four\n", Guid? by = null)
    {
        var u = Ok(await Imports.UploadAsync(by ?? Admin.Id, Company.Id, "ccenters", "cc.csv", Bytes("code,name\n" + rows), null));
        var s = Ok(await Imports.StageAsync(by ?? Admin.Id, Company.Id, u.Id, u.Revision, new StageInput(0, 1, null, null)));
        var m = Ok(await Imports.SetMappingAsync(by ?? Admin.Id, Company.Id, s.Id, s.Revision, Map));
        return Ok(await Imports.DryRunAsync(by ?? Admin.Id, Company.Id, m.Id, m.Revision));
    }

    private async Task<ImportBatchView> Submitted(string rows = "CC1,One\nCC2,Two\nCC3,Three\nCC4,Four\n")
    {
        var v = await ValidatedBatch(rows);
        return Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, v.Revision));
    }

    /// <summary>An approver: the approval permission can only be delegated offline (the administrator preset does not hold it), through the Import Approver preset; the right to see the data comes from an ordinary role.</summary>
    private async Task<Hasm.Infrastructure.Identity.UserRecord> Approver(bool preset = true)
    {
        var (user, _) = await Member([PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(user.Login, Company.Id));
        return user;
    }

    private async Task<ImportBatchView> Approved(string rows = "CC1,One\nCC2,Two\nCC3,Three\nCC4,Four\n")
    {
        var s = await Submitted(rows);
        var a = await Approver();
        return Ok(await Imports.DecideAsync(a.Id, Company.Id, s.Id, s.Revision, true, null));
    }

    private Task<long> CenterCount() => Sql<long>($"SELECT count(*) FROM {Cc.Table}");
    private Task InsertCenter(string code, string name) => Sql($"INSERT INTO {Cc.Table} ({Cc.Id}, {Cc.Code}, {Cc.NameAr}, {Cc.NameEn}, {Cc.IsGroup}, {Cc.IsActive}, {Cc.Revision}, {Cc.CreatedAtUtc}, {Cc.UpdatedAtUtc}) VALUES (gen_random_uuid(), '{code}', '{name}', '{name}', false, true, 1, now(), now())");

    private async Task<string?> RecordRaw(Guid batchId, long revision, Guid approver, bool approve = true)
    {
        try
        {
            await Tx.ExecuteAsync(Company.Id, async w => await DocumentApprovals.RecordAsync(w, new ApprovalDecisionRequest(ImportService.DocumentType, batchId, revision, approver, Guid.Empty, Guid.Empty, approve, approve ? null : "no", 1), DateTime.UtcNow, default));
            return null;
        }
        catch (PostgresException ex) { return ex.MessageText; }
    }

    [PostgresFact]
    public async Task Submission_needs_a_validated_plan_without_blockers_that_still_matches_the_data_and_can_be_withdrawn()
    {
        await Prepare();
        var u = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "ccenters", "cc.csv", Bytes("code,name\nCC1,One\n"), null));
        Assert.Equal("batch_not_validated", (await Imports.SubmitAsync(Admin.Id, Company.Id, u.Id, u.Revision)).Code);                                         // only uploaded
        var s = Ok(await Imports.StageAsync(Admin.Id, Company.Id, u.Id, u.Revision, new StageInput(0, 1, null, null)));
        Assert.Equal("batch_not_validated", (await Imports.SubmitAsync(Admin.Id, Company.Id, s.Id, s.Revision)).Code);
        // blockers (a conflicting repeat) and "nothing to do" (everything exists) cannot be submitted
        var blocked = await ValidatedBatch("X1,One\nX1,Different\n");
        Assert.Equal("blocking_rows", (await Imports.SubmitAsync(Admin.Id, Company.Id, blocked.Id, blocked.Revision)).Code);
        await InsertCenter("DONE1", "Done");
        var nothing = await ValidatedBatch("DONE1,Done\n");
        Assert.Equal("nothing_to_apply", (await Imports.SubmitAsync(Admin.Id, Company.Id, nothing.Id, nothing.Revision)).Code);
        // a plan that no longer matches the data is stale
        var v = await ValidatedBatch("S1,Stale\n");
        await InsertCenter("OTHER", "Other");
        Assert.Equal("plan_stale", (await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, v.Revision)).Code);
        Assert.Equal(ImportStatus.Validated, (await Reload(v.Id)).Status);
        var fresh = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, v.Revision));
        Assert.NotEqual(v.PlanHash, fresh.PlanHash);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, null)).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, fresh.Revision - 1)).Status);
        var sub = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, fresh.Revision));
        Assert.Equal((ImportStatus.Submitted, Admin.Id, fresh.PlanHash), (sub.Status, sub.SubmittedBy, sub.PlanHash));
        // while submitted nothing underneath can change
        Assert.Equal("batch_not_mappable", (await Imports.SetMappingAsync(Admin.Id, Company.Id, v.Id, sub.Revision, Map)).Code);
        Assert.Equal("batch_not_stageable", (await Imports.StageAsync(Admin.Id, Company.Id, v.Id, sub.Revision, new StageInput(0, 1, null, null))).Code);
        Assert.Equal("batch_not_validatable", (await Imports.DryRunAsync(Admin.Id, Company.Id, v.Id, sub.Revision)).Code);
        Assert.Equal("batch_not_validated", (await Imports.SetResolutionsAsync(Admin.Id, Company.Id, v.Id, sub.Revision, [new(2, "skip")])).Code);
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.PlanHash} = '{new string('f', 64)}', {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{v.Id}'"));      // PostgreSQL freezes the plan too
        // withdrawing returns it to Validated (plan kept, submission void); it can be submitted again
        var back = Ok(await Imports.WithdrawAsync(Admin.Id, Company.Id, v.Id, sub.Revision));
        Assert.Equal((ImportStatus.Validated, null, fresh.PlanHash), (back.Status, back.SubmittedBy, back.PlanHash));
        Assert.Equal("batch_not_withdrawable", (await Imports.WithdrawAsync(Admin.Id, Company.Id, v.Id, back.Revision)).Code);
        Assert.Equal(ImportStatus.Submitted, Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, back.Revision)).Status);
        Assert.Equal(new[] { ImportEventKind.Uploaded, ImportEventKind.Staged, ImportEventKind.Mapped, ImportEventKind.Validated, ImportEventKind.Validated, ImportEventKind.Submitted, ImportEventKind.Withdrawn, ImportEventKind.Submitted }.Order(),
            (await Imports.EventsAsync(Company.Id, v.Id)).Select(e => e.Kind).Order());
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Only_an_independent_currently_authorized_approver_can_decide_the_decision_is_bound_to_the_revision_and_every_gate_is_in_PostgreSQL()
    {
        await Prepare();
        var (uploader, _) = await Member([PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsSubmit, PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        var (submitter, _) = await Member([PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsSubmit, PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(submitter.Login, Company.Id));      // holds the approval permission too, yet cannot approve what they submitted
        var v = await ValidatedBatch(by: uploader.Id);
        var sub = Ok(await IMg.SubmitAsync(submitter.Id, Company.Id, v.Id, v.Revision));                                                                          // created by one person, submitted by another
        Assert.Equal((uploader.Id, submitter.Id), (sub.CreatedBy, sub.SubmittedBy));
        // the administrator preset cannot approve (it does not hold the permission at all)
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.DecideAsync(Admin.Id, Company.Id, v.Id, sub.Revision, true, null)).Status);
        // creator and submitter are refused by the service ... even though the submitter holds the approval permission
        Assert.Equal("separation_of_duties", (await Imports.DecideAsync(uploader.Id, Company.Id, v.Id, sub.Revision, true, null)).Code);
        Assert.Equal("separation_of_duties", (await IMg.DecideAsync(submitter.Id, Company.Id, v.Id, sub.Revision, true, null)).Code);
        // ... and by PostgreSQL for a writer that skips the service check
        Assert.Contains("independent approver", await RecordRaw(v.Id, sub.Revision, uploader.Id));
        Assert.Contains("independent approver", await RecordRaw(v.Id, sub.Revision, submitter.Id));
        Assert.Contains("submitted revision", await RecordRaw(v.Id, sub.Revision - 1, Guid.NewGuid()));                                                             // wrong revision
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {CompanySchema.ApprovalDecision.Table} WHERE {CompanySchema.ApprovalDecision.DocumentId} = '{v.Id}'"));
        // the batch cannot reach Approved without that decision, whoever writes
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.Status} = 6, {Ib.ApprovalRevision} = {Ib.Revision}, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{v.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.Status} = 10, {Ib.TerminalAtUtc} = now(), {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{v.Id}'"));       // nor to Rejected
        // an approver needs the approval permission AND the right to see the data
        var (noView, _) = await Member([PermissionCatalog.Codes.ImportsView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(noView.Login, Company.Id));
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.DecideAsync(noView.Id, Company.Id, v.Id, sub.Revision, true, null)).Status);
        var (noApprove, _) = await Member([PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.DecideAsync(noApprove.Id, Company.Id, v.Id, sub.Revision, true, null)).Status);
        // validation of the decision itself
        var approver = await Approver(preset: true);                                                                                                               // the delegated preset path
        Assert.Equal("comment", (await IMg.DecideAsync(approver.Id, Company.Id, v.Id, sub.Revision, false, "  ")).Fields!.Single().Field);
        Assert.Equal("approve", (await IMg.DecideAsync(approver.Id, Company.Id, v.Id, sub.Revision, null, null)).Fields!.Single().Field);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await IMg.DecideAsync(approver.Id, Company.Id, v.Id, null, true, null)).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await IMg.DecideAsync(approver.Id, Company.Id, v.Id, sub.Revision + 3, true, null)).Status);
        // an independent approval moves it to Approved and binds it to this revision
        var approved = Ok(await IMg.DecideAsync(approver.Id, Company.Id, v.Id, sub.Revision, true, "reviewed"));
        Assert.Equal(ImportStatus.Approved, approved.Status);
        Assert.Equal("batch_not_submitted", (await IMg.DecideAsync((await Approver()).Id, Company.Id, v.Id, approved.Revision, true, null)).Code);                 // a second decision finds it decided
        Assert.Equal(sub.Revision, await Sql<long>($"SELECT {Ib.ApprovalRevision} FROM {Ib.Table} WHERE {Ib.Id} = '{v.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.ApprovalRevision} = 1, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{v.Id}'"));      // the binding cannot be rewritten
        var decisions = await Imports.DecisionsAsync(Company.Id, v.Id);
        Assert.Equal((1, approver.Id, true, sub.Revision), (decisions.Count, decisions[0].Approver, decisions[0].Approved, decisions[0].DocumentRevision));
        // withdrawing voids the approval; the next submission is a NEW revision that needs a NEW decision (the old one does not count)
        var withdrawn = Ok(await Imports.WithdrawAsync(Admin.Id, Company.Id, v.Id, approved.Revision));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table} WHERE {Ib.Id} = '{v.Id}' AND {Ib.ApprovalRevision} IS NOT NULL"));
        var again = Ok(await Imports.SubmitAsync(submitter.Id, Company.Id, v.Id, withdrawn.Revision));
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.Status} = 6, {Ib.ApprovalRevision} = {Ib.Revision}, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{v.Id}'"));
        // a rejection needs a comment, is final, and is recorded
        var rejected = Ok(await IMg.DecideAsync(approver.Id, Company.Id, v.Id, again.Revision, false, "wrong department"));
        Assert.Equal((ImportStatus.Rejected, true), (rejected.Status, rejected.TerminalAtUtc is not null));
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.Status} = 4, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{v.Id}'"));
        var rejection = (await Imports.EventsAsync(Company.Id, v.Id)).Last();
        Assert.Equal((ImportEventKind.Rejected, "wrong department"), (rejection.Kind, rejection.Detail["comment"]!.GetValue<string>()));
        // the delegation can be withdrawn offline; a revoked approver is refused at once
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeImportApproverOfflineAsync(approver.Login, Company.Id));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Application_is_atomic_a_failure_changes_no_business_data_is_recorded_and_can_be_corrected_and_retried_idempotently()
    {
        await Prepare();
        var approved = await Approved();
        var before = await CenterCount();
        Adapter.FailAfter = 2;                                                                                          // two of the four operations succeed inside the transaction, then the adapter fails
        var failed = await IMg.ApplyAsync(Admin.Id, Company.Id, approved.Id, approved.Revision);
        Assert.Equal((ConfigStatus.Conflict, "simulated_failure"), (failed.Status, failed.Code));
        Assert.Equal(before, await CenterCount());                                                                      // nothing at all was applied: the previous business state stays
        var f = await Reload(approved.Id);
        Assert.Equal(ImportStatus.Failed, f.Status);
        Assert.Equal(("simulated_failure", approved.PlanHash), (f.Result!["code"]!.GetValue<string>(), f.Result["planHash"]!.GetValue<string>()));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{f.Id}' AND {Ir.Status} = 6"));
        Assert.Contains(ImportEventKind.ApplyFailed, (await Imports.EventsAsync(Company.Id, f.Id)).Select(e => e.Kind));
        Assert.Equal(ImportStatus.Failed, (await Reload(f.Id)).Status);                                                 // still recoverable, never auto-purged
        // after the cause is corrected the SAME approved batch is retried (no new approval: the plan is unchanged) and applies completely
        Adapter.FailAfter = null;
        var applied = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, f.Id, f.Revision));
        Assert.Equal((ImportStatus.Applied, before + 4), (applied.Status, await CenterCount()));
        Assert.Equal(4L, await Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{f.Id}' AND {Ir.Status} = 6"));
        Assert.Equal((4, approved.PlanHash), (applied.Result!["operations"]!.GetValue<int>(), applied.Result["planHash"]!.GetValue<string>()));
        Assert.Equal(4, applied.Result["summary"]!["created"]!.GetValue<int>());
        // applying again is a harmless retry: same result, nothing duplicated, one Applied event
        var again = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, f.Id, f.Revision));
        Assert.Equal((ImportStatus.Applied, applied.Revision, before + 4), (again.Status, again.Revision, await CenterCount()));
        Assert.Equal(1, (await Imports.EventsAsync(Company.Id, f.Id)).Count(e => e.Kind == ImportEventKind.Applied));
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.Status} = 3, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{f.Id}'"));          // an applied import is final
        Assert.Equal("batch_finished", (await Imports.CancelAsync(Admin.Id, Company.Id, f.Id, again.Revision)).Code);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Concurrent_applications_apply_the_batch_exactly_once()
    {
        await Prepare();
        var approved = await Approved();
        var before = await CenterCount();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => IMg.ApplyAsync(Admin.Id, Company.Id, approved.Id, approved.Revision)));
        Assert.All(results, r => Assert.True(r.IsOk, r.Code));
        Assert.All(results, r => Assert.Equal(ImportStatus.Applied, r.Value!.Status));
        Assert.Equal(before + 4, await CenterCount());
        Assert.Equal(4, Adapter.Applied);
        Assert.Equal(1, Adapter.ApplyCalls);
        Assert.Equal(1, (await Imports.EventsAsync(Company.Id, approved.Id)).Count(e => e.Kind == ImportEventKind.Applied));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_plan_that_went_stale_an_approver_who_lost_access_or_a_missing_approval_blocks_application_and_nothing_changes()
    {
        await Prepare();
        // not approved: refused, nothing recorded
        var sub = await Submitted();
        Assert.Equal("batch_not_approved", (await IMg.ApplyAsync(Admin.Id, Company.Id, sub.Id, sub.Revision)).Code);
        Assert.Equal(ImportStatus.Submitted, (await Reload(sub.Id)).Status);
        // stale: somebody created one of the targets between approval and application
        var approver = await Approver();
        var approved = Ok(await Imports.DecideAsync(approver.Id, Company.Id, sub.Id, sub.Revision, true, null));
        await InsertCenter("CC2", "Created meanwhile");
        var before = await CenterCount();
        var stale = await IMg.ApplyAsync(Admin.Id, Company.Id, approved.Id, approved.Revision);
        Assert.Equal((ConfigStatus.Conflict, "plan_stale"), (stale.Status, stale.Code));
        Assert.Equal(before, await CenterCount());
        var failed = await Reload(approved.Id);
        Assert.Equal((ImportStatus.Failed, "plan_stale"), (failed.Status, failed.Result!["code"]!.GetValue<string>()));
        // recovery: withdraw (the approval is void), validate again against the new data, submit, approve (a new revision) and apply
        var w = Ok(await Imports.WithdrawAsync(Admin.Id, Company.Id, failed.Id, failed.Revision));
        var re = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, w.Id, w.Revision));
        Assert.Equal(1, re.Plan!["summary"]!["conflict"]!.GetValue<int>());                                              // CC2 now exists with another name: it needs an explicit decision
        var resolved = Ok(await Imports.SetResolutionsAsync(Admin.Id, Company.Id, re.Id, re.Revision, [new(3, "skip")]));
        var again = Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, resolved.Id, resolved.Revision));
        var sub2 = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, again.Id, again.Revision));
        var ok2 = Ok(await Imports.DecideAsync(approver.Id, Company.Id, sub2.Id, sub2.Revision, true, null));           // the same person may approve the new revision
        var applied = Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, ok2.Id, ok2.Revision));
        Assert.Equal((ImportStatus.Applied, before + 3), (applied.Status, await CenterCount()));
        // an approver who lost the right before the application: refused, the batch stays approved and nothing changes
        var next = await Submitted("N1,New one\nN2,New two\n");
        var a2 = await Approver();
        var approvedNext = Ok(await Imports.DecideAsync(a2.Id, Company.Id, next.Id, next.Revision, true, null));
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeImportApproverOfflineAsync(a2.Login, Company.Id));     // the delegation is withdrawn offline before the application
        var count = await CenterCount();
        var refused = await IMg.ApplyAsync(Admin.Id, Company.Id, approvedNext.Id, approvedNext.Revision);
        Assert.Equal((ConfigStatus.Conflict, "approver_no_longer_authorized"), (refused.Status, refused.Code));
        Assert.Equal((ImportStatus.Approved, count), ((await Reload(approvedNext.Id)).Status, await CenterCount()));
        // a different approver on a new revision restores it
        var back = Ok(await Imports.WithdrawAsync(Admin.Id, Company.Id, approvedNext.Id, approvedNext.Revision));
        var resub = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, back.Id, back.Revision));
        var other = await Approver();
        var reapproved = Ok(await Imports.DecideAsync(other.Id, Company.Id, resub.Id, resub.Revision, true, null));
        Assert.Equal(ImportStatus.Applied, Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, reapproved.Id, reapproved.Revision)).Status);
        // application needs its own permission and the adapter's, and revision discipline
        var (noApply, _) = await Member([PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        var third = await Approved("Z1,Zed\n");
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.ApplyAsync(noApply.Id, Company.Id, third.Id, third.Revision)).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await IMg.ApplyAsync(Admin.Id, Company.Id, third.Id, null)).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await IMg.ApplyAsync(Admin.Id, Company.Id, third.Id, third.Revision - 1)).Status);
        Assert.Equal(ImportStatus.Approved, (await Reload(third.Id)).Status);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Changing_anything_after_approval_or_renewing_voids_the_approval_and_an_approved_batch_that_is_not_applied_expires()
    {
        await Prepare();
        var approved = await Approved();
        Assert.Equal("batch_not_mappable", (await Imports.SetMappingAsync(Admin.Id, Company.Id, approved.Id, approved.Revision, Map)).Code);
        Assert.Equal("batch_not_stageable", (await Imports.StageAsync(Admin.Id, Company.Id, approved.Id, approved.Revision, new StageInput(0, 1, null, null))).Code);
        Assert.Equal("batch_not_validatable", (await Imports.DryRunAsync(Admin.Id, Company.Id, approved.Id, approved.Revision)).Code);
        // renewal returns it to Mapped and clears the approval (the dry run and the approval must be repeated)
        var renewed = Ok(await Imports.RenewAsync(Admin.Id, Company.Id, approved.Id, approved.Revision));
        Assert.Equal((ImportStatus.Mapped, null, null), (renewed.Status, renewed.PlanHash, renewed.SubmittedBy));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table} WHERE {Ib.Id} = '{approved.Id}' AND {Ib.ApprovalRevision} IS NOT NULL"));
        // a failed batch that is re-staged, re-mapped or re-validated also loses its approval
        var second = await Approved("F1,Eff\n");
        Adapter.FailAfter = 0;
        var failed = await IMg.ApplyAsync(Admin.Id, Company.Id, second.Id, second.Revision);
        Assert.Equal("simulated_failure", failed.Code);
        var f = await Reload(second.Id);
        var restaged = Ok(await Imports.StageAsync(Admin.Id, Company.Id, f.Id, f.Revision, new StageInput(0, 1, null, null)));
        Assert.Equal(ImportStatus.Staged, restaged.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table} WHERE {Ib.Id} = '{second.Id}' AND {Ib.ApprovalRevision} IS NOT NULL"));
        // an approved batch nobody applies expires like any pending batch (it is not applying), and an expired batch cannot be applied
        var third = await Approved("E1,Eee\n");
        Clock.Advance(TimeSpan.FromDays(31));
        Assert.True((await Imports.MaintainAsync(null, Company.Id)).Expired >= 1);
        Assert.Equal(ImportStatus.Expired, (await Reload(third.Id)).Status);
        Assert.Equal("batch_not_approved", (await IMg.ApplyAsync(Admin.Id, Company.Id, third.Id, (await Reload(third.Id)).Revision)).Code);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task The_approval_and_application_flow_works_end_to_end_over_http_with_the_delegated_approver_role()
    {
        Adapter = new CostCenterTestAdapter();
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Imports:StorageDirectory", StorageDir);
            b.ConfigureServices(s => { s.AddSingleton<Hasm.Infrastructure.Security.KekRing>(new Hasm.Infrastructure.Security.InMemoryKekRing("k1", TestIdentityKeys.Master)); s.AddSingleton(TestEncryption); s.AddSingleton<IImportAdapter>(Adapter); });
        });
        const string Base = "/api/v1/company/imports";
        var cookie = await SignIn(Admin);
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"{Base}?adapter=ccenters&fileName=cc.csv") { Content = new ByteArrayContent(Bytes("code,name\nH1,Http one\nH2,Http two\n")) };
        upload.Headers.Add("X-Hasm-Request", "1"); upload.Headers.Add("Cookie", $"{CookieName}={cookie}");
        using var up = await client.SendAsync(upload);
        var id = System.Text.Json.JsonDocument.Parse(await up.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var staged = await Send(HttpMethod.Post, $"{Base}/{id}/stage", new { sheet = 0, headerRow = 1 }, cookie, ifMatch: up.Headers.ETag!.Tag);
        var mapped = await Send(HttpMethod.Put, $"{Base}/{id}/mapping", Map, cookie, ifMatch: Etag(staged));
        var validated = await Send(HttpMethod.Post, $"{Base}/{id}/dry-run", new { }, cookie, ifMatch: Etag(mapped));
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/{id}/submit", new { }, cookie)).Status);
        var submitted = await Send(HttpMethod.Post, $"{Base}/{id}/submit", new { }, cookie, ifMatch: Etag(validated));
        Assert.Equal("submitted", submitted.Json.GetProperty("status").GetString());
        // the submitter cannot approve; neither can the administrator (no approval permission at all)
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{id}/approve", new { }, cookie, ifMatch: Etag(submitted))).Status);
        var approver = await Approver(preset: true);
        var ac = await SignIn(approver);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"{Base}/{id}/reject", new { comment = " " }, ac, ifMatch: Etag(submitted))).Status);
        var approved = await Send(HttpMethod.Post, $"{Base}/{id}/approve", new { comment = "ok" }, ac, ifMatch: Etag(submitted));
        Assert.Equal("approved", approved.Json.GetProperty("status").GetString());
        var decisions = await Send(HttpMethod.Get, $"{Base}/{id}/decisions", cookie: cookie);
        Assert.Equal(approver.Id, decisions.Json.GetProperty("decisions")[0].GetProperty("approver").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"{Base}/{id}/apply", new { }, ac, ifMatch: Etag(approved))).Status);
        var before = await CenterCount();
        var applied = await Send(HttpMethod.Post, $"{Base}/{id}/apply", new { }, cookie, ifMatch: Etag(approved));
        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Equal(("applied", 2), (applied.Json.GetProperty("status").GetString(), applied.Json.GetProperty("result").GetProperty("operations").GetInt32()));
        Assert.Equal(before + 2, await CenterCount());
        Assert.Equal("applied", (await Send(HttpMethod.Post, $"{Base}/{id}/apply", new { }, cookie, ifMatch: Etag(approved))).Json.GetProperty("status").GetString());   // idempotent retry
        Assert.Equal(before + 2, await CenterCount());
    }
}
