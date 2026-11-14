using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Ad = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using Ap = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicy;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Lh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;

namespace Hasm.Database.IntegrationTests;

/// <summary>Approval policies and document approval decisions (company migration 0008 slice S4, Phase 3.2B) against real PostgreSQL; disposable cluster only.</summary>
public abstract class ApprovalTestBase : ChartAccessTestBase
{
    protected ApprovalPolicyService Policies { get; private set; } = null!;
    protected ApprovalPolicyManagementService PMg { get; private set; } = null!;
    protected CompanyAuditedTransactions Tx { get; private set; } = null!;
    protected long Baseline;
    protected const string Doc = DocumentTypes.JournalEntry;

    protected void BuildApprovalFacade()
    {
        Tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Policies = new ApprovalPolicyService(Resolver, Tx, Clock);
        PMg = new ApprovalPolicyManagementService(Rbac, Policies);
    }

    protected async Task PrepareApprovals()
    {
        await PrepareRbac();
        BuildApprovalFacade();
        Baseline = await Count(Company, Audit_.Table);
    }

    protected static ApprovalPolicyInput In(string source = "any", decimal min = 0m, int required = 1, Guid? branch = null, string? ar = "سياسة", string? en = "Policy", string doc = Doc) =>
        new(doc, source, branch, min, required, ar, en);

    protected async Task<ApprovalPolicyView> NewPolicy(string source = "any", decimal min = 0m, int required = 1, Guid? branch = null) =>
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, In(source, min, required, branch)));

    protected Task<int> Required(DocumentSource source, Guid? branch, decimal amount) => Policies.RequiredApprovalsAsync(Company.Id, Doc, source, branch, amount);

    protected Task<Guid> MainBranch() => Sql<Guid>($"SELECT {Br.Id} FROM {Br.Table} WHERE {Br.IsMain}");

    private sealed class Refused(ApprovalRecordResult result) : Exception { public ApprovalRecordResult Result { get; } = result; }

    /// <summary>Runs the decision in an audited transaction the way the journal module will: a refusal aborts the whole transaction (nothing is written, no audit gap).</summary>
    protected async Task<ApprovalRecordResult> Decide(Guid docId, long rev, Guid approver, Guid creator, Guid? submitter, bool approve = true, string? comment = null)
    {
        var now = DateTime.UtcNow;
        try
        {
            return await Tx.ExecuteAsync(Company.Id, async w =>
            {
                var r = await DocumentApprovals.RecordAsync(w, new ApprovalDecisionRequest(Doc, docId, rev, approver, creator, submitter, approve, comment), now, default);
                return r.IsRecorded ? r : throw new Refused(r);
            }, default);
        }
        catch (Refused refused) { return refused.Result; }
    }

    protected async Task<bool> Satisfied(Guid docId, long rev, int required)
    {
        await using var conn = await Resolver.OpenAsync(Company.Id);
        return await DocumentApprovals.SatisfiedAsync(conn, null, Doc, docId, rev, required, default);
    }

    protected Task<string?> RawFails(params string[] sql) => InTx(sql);
}

public class ApprovalPolicyTests : ApprovalTestBase
{
    [PostgresFact]
    public async Task With_no_policy_nothing_requires_approval_and_a_policy_is_created_audited_and_revisioned()
    {
        await PrepareApprovals();
        Assert.Equal(0, await Required(DocumentSource.Manual, null, 1_000_000m));
        Assert.Empty(Ok(await PMg.ListAsync(Admin.Id, Company.Id)));
        var p = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, In("manual", 1000.5m, 2, ar: "سياسة", en: null)));
        Assert.Equal((Doc, PolicySource.Manual, 1000.5m, 2, true, 1L), (p.DocType, p.Source, p.MinAmount, p.RequiredApprovals, p.IsActive, p.Revision));
        Assert.Equal(1L, await Count(Company, Ap.Table));
        Assert.Equal(1L, await Count(Company, Lh.Table));
        Assert.Equal(1L, await Count(Company, Audit_.Table) - Baseline);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Lh.Table} WHERE {Lh.Entity} = 4 AND {Lh.EntityKey} = '{p.Id}'"));
    }

    [PostgresFact]
    public async Task Policies_match_by_type_source_branch_and_amount_and_the_strictest_one_wins()
    {
        await PrepareApprovals();
        var main = await MainBranch();
        await NewPolicy("manual", 1000m, 1);                  // manual >= 1000: 1 approver
        await NewPolicy("manual", 10000m, 2);                 // manual >= 10000: 2 approvers
        await NewPolicy("automatic", 0m, 1);                  // every automatic entry: 1 approver
        await NewPolicy("any", 50000m, 3, main);              // any source, main branch, >= 50000: 3 approvers
        Assert.Equal(0, await Required(DocumentSource.Manual, null, 999.9999m));
        Assert.Equal(1, await Required(DocumentSource.Manual, null, 1000m));                       // the threshold is inclusive
        Assert.Equal(1, await Required(DocumentSource.Manual, null, 9999.9999m));
        Assert.Equal(2, await Required(DocumentSource.Manual, null, 10000m));
        Assert.Equal(2, await Required(DocumentSource.Manual, null, 49999m));
        Assert.Equal(2, await Required(DocumentSource.Manual, Guid.NewGuid(), 60000m));            // another branch: the branch policy does not apply
        Assert.Equal(3, await Required(DocumentSource.Manual, main, 60000m));                      // the strictest matching policy
        Assert.Equal(1, await Required(DocumentSource.Automatic, null, 0m));                       // automatic entries are subject to policies too
        Assert.Equal(3, await Required(DocumentSource.Automatic, main, 50000m));
        Assert.Equal(0, await Policies.RequiredApprovalsAsync(Company.Id, "other_type", DocumentSource.Manual, null, 1_000_000m));
        var all = Ok(await PMg.ListAsync(Admin.Id, Company.Id));
        Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, all.Single(x => x.RequiredApprovals == 3).Id, 1));
        Assert.Equal(2, await Required(DocumentSource.Manual, main, 60000m));                      // an inactive policy is ignored
    }

    [PostgresFact]
    public async Task Validation_branch_checks_revisions_and_unchanged_saves()
    {
        await PrepareApprovals();
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(doc: "nope"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(source: "weird"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(min: -1m))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(min: 1.00001m))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(required: 0))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(required: 6))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(ar: null, en: null))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Policies.CreateAsync(Admin.Id, Company.Id, In(branch: Guid.NewGuid()))).Status);
        Assert.Equal(0L, await Count(Company, Ap.Table));
        var p = await NewPolicy("manual", 100m, 1);
        var details = new ApprovalPolicyDetailsInput("manual", null, 100m, 1, "سياسة", "Policy");
        Assert.Equal(1L, Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, p.Id, 1, details)).Revision);               // unchanged: no revision, no audit
        var u = Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, p.Id, 1, details with { MinAmount = 500m, RequiredApprovals = 2 }));
        Assert.Equal((2L, 500m, 2), (u.Revision, u.MinAmount, u.RequiredApprovals));
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Policies.UpdateAsync(Admin.Id, Company.Id, p.Id, 1, details with { MinAmount = 1m })).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Policies.UpdateAsync(Admin.Id, Company.Id, p.Id, null, details)).Status);
        Assert.Equal(ConfigStatus.NotFound, (await Policies.UpdateAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, details)).Status);
        Assert.Contains("\"minAmount\"", await Sql<string>($"SELECT {Lh.Changes}::text FROM {Lh.Table} WHERE {Lh.EntityKey} = '{p.Id}' AND {Lh.Revision} = 2"));
        Assert.Equal(2L, await Count(Company, Lh.Table));
        Assert.Equal(2L, await Count(Company, Audit_.Table) - Baseline);
        var off = Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, p.Id, 2));
        Assert.False(off.IsActive);
        Assert.True(Ok(await Policies.ReactivateAsync(Admin.Id, Company.Id, p.Id, 3)).IsActive);
        Assert.Equal(1L, await Count(Company, Ap.Table));                                                                      // never deleted
    }

    [PostgresFact]
    public async Task Concurrent_policy_updates_lose_nothing_silently()
    {
        await PrepareApprovals();
        var p = await NewPolicy();
        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => Policies.UpdateAsync(Admin.Id, Company.Id, p.Id, 1, new ApprovalPolicyDetailsInput("any", null, i * 10m, 1, "س", "P"))));
        Assert.Single(results, r => r.IsOk);
        Assert.Equal(5, results.Count(r => r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(2L, (await Policies.GetAsync(Company.Id, p.Id))!.Revision);
    }
}

public class ApprovalDecisionTests : ApprovalTestBase
{
    private static readonly Guid Creator = Guid.NewGuid(), Submitter = Guid.NewGuid(), A1 = Guid.NewGuid(), A2 = Guid.NewGuid(), A3 = Guid.NewGuid();

    [PostgresFact]
    public async Task Approvals_are_bound_to_the_exact_document_revision_and_a_new_revision_invalidates_them()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        Assert.False(await Satisfied(doc, 1, 1));
        Assert.True((await Decide(doc, 1, A1, Creator, Submitter)).IsRecorded);
        Assert.True(await Satisfied(doc, 1, 1));
        Assert.False(await Satisfied(doc, 1, 2));                       // two approvers are required: one is not enough
        Assert.False(await Satisfied(doc, 2, 1));                       // the document was edited (revision 2): the earlier approval no longer counts
        Assert.True(await Satisfied(doc, 2, 0));                        // nothing required: always satisfied
        Assert.True((await Decide(doc, 1, A2, Creator, Submitter)).IsRecorded);
        Assert.True(await Satisfied(doc, 1, 2));
        Assert.True((await Decide(doc, 2, A1, Creator, Submitter)).IsRecorded);   // the same approver must decide again on the new revision
        Assert.True(await Satisfied(doc, 2, 1));
        Assert.False(await Satisfied(Guid.NewGuid(), 1, 1));            // approvals never leak to another document
    }

    [PostgresFact]
    public async Task Separation_of_duties_duplicates_and_uncommented_rejections_are_refused_without_writing()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        Assert.Equal(ApprovalRecordStatus.SeparationOfDuties, (await Decide(doc, 1, Creator, Creator, null)).Status);          // the creator cannot approve
        Assert.Equal(ApprovalRecordStatus.SeparationOfDuties, (await Decide(doc, 1, Submitter, Creator, Submitter)).Status);   // nor the submitter
        Assert.Equal(ApprovalRecordStatus.CommentRequired, (await Decide(doc, 1, A1, Creator, Submitter, approve: false, comment: "  ")).Status);
        Assert.Equal(0L, await Count(Company, Ad.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table) - Baseline);
        Assert.True((await Decide(doc, 1, A1, Creator, Submitter)).IsRecorded);
        Assert.Equal(ApprovalRecordStatus.AlreadyDecided, (await Decide(doc, 1, A1, Creator, Submitter)).Status);              // one decision per approver per revision
        Assert.Equal(ApprovalRecordStatus.AlreadyDecided, (await Decide(doc, 1, A1, Creator, Submitter, approve: false, comment: "changed my mind")).Status);
        Assert.Equal(1L, await Count(Company, Ad.Table));
        Assert.Equal(1L, await Count(Company, Audit_.Table) - Baseline);                                                       // exactly one audit event per recorded decision
    }

    [PostgresFact]
    public async Task A_rejection_blocks_the_revision_and_distinct_approvers_are_counted_once()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        Assert.True((await Decide(doc, 1, A1, Creator, null)).IsRecorded);
        Assert.True((await Decide(doc, 1, A2, Creator, null)).IsRecorded);
        Assert.True(await Satisfied(doc, 1, 2));
        var rej = await Decide(doc, 1, A3, Creator, null, approve: false, comment: "wrong account");
        Assert.True(rej.IsRecorded);
        Assert.False(rej.Decision!.Approved);
        Assert.False(await Satisfied(doc, 1, 2));                       // any rejection of this revision blocks it
        Assert.False(await Satisfied(doc, 1, 1));
        await using var conn = await Resolver.OpenAsync(Company.Id);
        var list = await DocumentApprovals.ListAsync(conn, null, Doc, doc, default);
        Assert.Equal([true, true, false], list.Select(d => d.Approved));
        Assert.Equal("wrong account", list[2].Comment);
    }

    [PostgresFact]
    public async Task Concurrent_decisions_by_one_approver_record_exactly_one()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { return (await Decide(doc, 1, A1, Creator, Submitter)).Status; }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") { return ApprovalRecordStatus.AlreadyDecided; }   // the database is the last line of defence
        }));
        Assert.Equal(1, results.Count(r => r == ApprovalRecordStatus.Recorded));
        Assert.Equal(1L, await Count(Company, Ad.Table));
    }

    [PostgresFact]
    public async Task A_decision_rolls_back_with_its_transaction_leaving_no_gap_in_the_audit_chain()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tx.ExecuteAsync<int>(Company.Id, async w =>
        {
            await DocumentApprovals.RecordAsync(w, new ApprovalDecisionRequest(Doc, doc, 1, A1, Creator, null, true, null), DateTime.UtcNow, default);
            throw new InvalidOperationException("the document change failed after the decision");
        }, default));
        Assert.Equal(0L, await Count(Company, Ad.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table) - Baseline);
        Assert.True((await Decide(doc, 1, A1, Creator, null)).IsRecorded);
        Assert.Equal(1L, await Count(Company, Audit_.Table) - Baseline);
    }
}

public class ApprovalDatabaseGuardTests : ApprovalTestBase
{
    [PostgresFact]
    public async Task PostgreSQL_keeps_decisions_append_only_and_policies_undeletable_and_well_formed()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        Assert.True((await Decide(doc, 1, Guid.NewGuid(), Guid.NewGuid(), null)).IsRecorded);
        var p = await NewPolicy();
        Assert.Equal("P0001", await RawFails($"UPDATE {Ad.Table} SET {Ad.Decision} = 2"));
        Assert.Equal("P0001", await RawFails($"DELETE FROM {Ad.Table}"));
        Assert.Equal("P0001", await RawFails($"TRUNCATE {Ad.Table}"));
        Assert.Equal("P0001", await RawFails($"DELETE FROM {Ap.Table}"));
        Assert.Equal("0A000", await RawFails($"TRUNCATE {Ap.Table}"));   // referenced by the stage table: PostgreSQL itself refuses to truncate it
        Assert.Equal("P0001", await RawFails($"UPDATE {Ap.Table} SET {Ap.DocType} = 'other_type', {Ap.Revision} = {Ap.Revision} + 1"));
        Assert.Equal("P0001", await RawFails($"UPDATE {Ap.Table} SET {Ap.Id} = gen_random_uuid(), {Ap.Revision} = {Ap.Revision} + 1"));
        Assert.Equal("P0001", await RawFails($"UPDATE {Ap.Table} SET {Ap.RequiredApprovals} = 2"));                                 // revision must advance
        Assert.Equal("23514", await RawFails($"UPDATE {Ap.Table} SET {Ap.RequiredApprovals} = 9, {Ap.Revision} = {Ap.Revision} + 1"));
        Assert.Equal("23514", await RawFails($"UPDATE {Ap.Table} SET {Ap.MinAmount} = -1, {Ap.Revision} = {Ap.Revision} + 1"));
        Assert.Equal("23514", await RawFails($"UPDATE {Ap.Table} SET {Ap.Source} = 4, {Ap.Revision} = {Ap.Revision} + 1"));
        Assert.Equal("23503", await RawFails($"UPDATE {Ap.Table} SET {Ap.BranchId} = gen_random_uuid(), {Ap.Revision} = {Ap.Revision} + 1"));
        Assert.Equal(p.Id, (await Policies.GetAsync(Company.Id, p.Id))!.Id);
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_malformed_decisions_even_when_written_directly()
    {
        await PrepareApprovals();
        var doc = Guid.NewGuid();
        Assert.True((await Decide(doc, 1, Guid.NewGuid(), Guid.NewGuid(), null)).IsRecorded);
        string Ins(string decision, string rev, string comment, string approver = "gen_random_uuid()") =>
            $"INSERT INTO {Ad.Table} ({Ad.DocType}, {Ad.DocumentId}, {Ad.DocumentRevision}, {Ad.Approver}, {Ad.Decision}, {Ad.DecidedAtUtc}, {Ad.Comment}, {Ad.AuditStream}, {Ad.AuditSeq}) " +
            $"SELECT 'journal_entry', gen_random_uuid(), {rev}, {approver}, {decision}, now(), {comment}, gen_random_uuid()::text, 1";
        Assert.Equal("23514", await RawFails(Ins("3", "1", "NULL")));
        Assert.Equal("23514", await RawFails(Ins("1", "0", "NULL")));
        Assert.Equal("23514", await RawFails(Ins("2", "1", "NULL")));                                                  // a rejection needs a comment
        Assert.Equal("23503", await RawFails(Ins("1", "1", "NULL")));                                                  // every decision belongs to a real audit event
    }

    [PostgresFact]
    public async Task The_sql_evaluation_functions_match_the_service_and_never_invent_requirements()
    {
        await PrepareApprovals();
        await NewPolicy("manual", 100m, 2);
        Assert.Equal(2, await Sql<int>($"SELECT {Ap.RequiredFunction}('journal_entry', 1::smallint, NULL, 100.0000)"));
        Assert.Equal(0, await Sql<int>($"SELECT {Ap.RequiredFunction}('journal_entry', 2::smallint, NULL, 100.0000)"));
        Assert.Equal(0, await Sql<int>($"SELECT {Ap.RequiredFunction}('journal_entry', 1::smallint, NULL, 99.9999)"));
        Assert.True(await Sql<bool>($"SELECT {Ap.SatisfiedFunction}('journal_entry', gen_random_uuid(), 1, 0)"));
        Assert.False(await Sql<bool>($"SELECT {Ap.SatisfiedFunction}('journal_entry', gen_random_uuid(), 1, 1)"));
    }
}

public class ApprovalAccessTests : ApprovalTestBase
{
    private const string Base = "/api/v1/company/approval-policies";

    [PostgresFact]
    public async Task Policy_permissions_are_separate_company_wide_and_only_in_the_administrator_preset_and_revocation_applies_immediately()
    {
        await PrepareApprovals();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "view", "create", "update", "activate" }) Assert.Contains($"company.approvalpolicies.{c}", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.approvalpolicies"));
        var p = await NewPolicy();
        var (viewer, _) = await Holder(PermissionCatalog.Codes.ApprovalPoliciesView);
        var (creator, _) = await Holder(PermissionCatalog.Codes.ApprovalPoliciesCreate);
        var (updater, _) = await Holder(PermissionCatalog.Codes.ApprovalPoliciesUpdate);
        var (activator, _) = await Holder(PermissionCatalog.Codes.ApprovalPoliciesActivate);
        Assert.Single(Ok(await PMg.ListAsync(viewer.Id, Company.Id)));
        foreach (var u in new[] { creator, updater, activator }) Assert.Equal(ConfigStatus.Forbidden, (await PMg.ListAsync(u.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await PMg.CreateAsync(viewer.Id, Company.Id, In())).Status);
        var details = new ApprovalPolicyDetailsInput("any", null, 5m, 1, "س", "P");
        Assert.Equal(ConfigStatus.Forbidden, (await PMg.UpdateAsync(creator.Id, Company.Id, p.Id, 1, details)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await PMg.DeactivateAsync(updater.Id, Company.Id, p.Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await PMg.ReactivateAsync(updater.Id, Company.Id, p.Id, 1)).Status);
        Assert.Equal(2, Ok(await PMg.CreateAsync(creator.Id, Company.Id, In(required: 2))).RequiredApprovals);
        Assert.Equal(5m, Ok(await PMg.UpdateAsync(updater.Id, Company.Id, p.Id, 1, details)).MinAmount);
        Assert.False(Ok(await PMg.DeactivateAsync(activator.Id, Company.Id, p.Id, 2)).IsActive);
        var assignment = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, activator.Id)).Value!.Single();
        Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Id)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await PMg.ReactivateAsync(activator.Id, Company.Id, p.Id, 3)).Status);                  // revocation is effective at once
    }

    [PostgresFact]
    public async Task The_endpoints_need_authentication_and_work_end_to_end()
    {
        await StartApi();
        BuildApprovalFacade();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, Base, new { })).Status);
        var cookie = await SignIn(Admin);
        var empty = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Contains("no-store", empty.Headers.CacheControl!.ToString());
        Assert.Equal(0, empty.Json.GetProperty("policies").GetArrayLength());
        Assert.Equal("journal_entry", empty.Json.GetProperty("documentTypes")[0].GetString());
        var body = new { docType = "journal_entry", source = "manual", minAmount = 1000.5m, requiredApprovals = 2, nameEn = "Large manual entries" };
        var created = await Send(HttpMethod.Post, Base, body, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", Etag(created));
        Assert.Equal("1000.5000", created.Json.GetProperty("minAmount").GetString());
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base, body with { requiredApprovals = 0 }, cookie)).Status);
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{id}", new { source = "any", minAmount = 5m, requiredApprovals = 1, nameEn = "x" }, cookie)).Status);
        var put = await Send(HttpMethod.Put, $"{Base}/{id}", new { source = "any", minAmount = 5m, requiredApprovals = 1, nameEn = "x" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal((HttpStatusCode)412, (await Send(HttpMethod.Put, $"{Base}/{id}", new { source = "any", minAmount = 6m, requiredApprovals = 1, nameEn = "x" }, cookie, ifMatch: "\"1\"")).Status);
        Assert.False((await Send(HttpMethod.Post, $"{Base}/{id}/deactivate", null, cookie, ifMatch: "\"2\"")).Json.GetProperty("isActive").GetBoolean());
        Assert.True((await Send(HttpMethod.Post, $"{Base}/{id}/activate", null, cookie, ifMatch: "\"3\"")).Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie)).Status);
    }
}
