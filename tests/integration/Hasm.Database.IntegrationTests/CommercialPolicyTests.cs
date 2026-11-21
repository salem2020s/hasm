using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using AB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentApprovalBinding;
using AD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using AG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalStaging;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CDA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentApproval;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S5a (decisions D-140, D-141): the ordered approval policies of 3.2C for commercial documents. The governing policy is picked from the SEALED total at submit by type, value and branch; the plan is bound
/// as a floor; every decision is cast for the first open stage by someone independent of the creator, the last editor and the submitter; the document becomes approved only when the plan of its revision is satisfied
/// (service AND PostgreSQL); with no matching policy the single independent approval of S1b stays. The journal's own engine is untouched (its approval test classes run unchanged). Disposable cluster only.
/// </summary>
public class CommercialPolicyTests : CommercialTestBase
{
    private ApprovalPolicyService PolicyService = null!;

    private async Task<(Guid Branch, Guid Party, Guid Item, Guid Unit)> Setup(string branchCode = "BA")
    {
        await Prepare();
        PolicyService = new ApprovalPolicyService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock, TestRegistry());
        var (item, unit) = await NewItemAndUnit("PL1");
        return (await NewBranchId(branchCode), await NewCustomerId("C1"), item, unit);
    }

    private static ApprovalPolicyInput Policy(int[] stages, decimal min = 0m, Guid? branch = null, Guid? role = null, string type = "commercial_test") =>
        new(type, "any", branch, min, null, "سياسة", "Policy", Stages: [.. stages.Select(c => new ApprovalStageInput(c, role))]);

    /// <summary>A document submitted by the administrator: quantity 10 seals a gross of 116 (price 10, tax 16%), quantity 5 seals 58.</summary>
    private async Task<CommercialDocumentView> Submitted(Guid branch, Guid party, Guid item, Guid unit, string key, decimal qty = 10m)
    {
        var d = await NewDraft(branch, party, item, unit, key, qty);
        return Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
    }

    private async Task<(string? State, string Message)> Run(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try { await using var cmd = new NpgsqlCommand(sql, conn, tx); await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync(); return (null, ""); }
        catch (PostgresException ex) { return (ex.SqlState, ex.MessageText); }
    }

    private string RawDecision(Guid doc, long revision, Guid approver, int decision, int stage) =>
        $"INSERT INTO {AD.Table} ({AD.DocType}, {AD.DocumentId}, {AD.DocumentRevision}, {AD.Approver}, {AD.Decision}, {AD.DecidedAtUtc}, {AD.Comment}, {AD.AuditStream}, {AD.AuditSeq}, {AG.DecisionStage}) " +
        $"VALUES ('commercial_test', '{doc}', {revision}, '{approver}', {decision}, now(), {(decision == 2 ? "'no'" : "NULL")}, 'x', 1, {stage})";

    /// <summary>A decision insert that satisfies the foreign key to the business audit: it borrows an audit event no decision uses yet.</summary>
    private string ValidRawDecision(Guid doc, long revision, Guid approver, int stage) =>
        $"INSERT INTO {AD.Table} ({AD.DocType}, {AD.DocumentId}, {AD.DocumentRevision}, {AD.Approver}, {AD.Decision}, {AD.DecidedAtUtc}, {AD.Comment}, {AD.AuditStream}, {AD.AuditSeq}, {AG.DecisionStage}) " +
        $"SELECT 'commercial_test', '{doc}', {revision}, '{approver}', 1, now(), NULL, a.{CompanySchema.BusinessAudit.StreamKey}, a.{CompanySchema.BusinessAudit.Seq}, {stage} FROM {CompanySchema.BusinessAudit.Table} a " +
        $"WHERE NOT EXISTS (SELECT 1 FROM {AD.Table} d WHERE d.{AD.AuditStream} = a.{CompanySchema.BusinessAudit.StreamKey} AND d.{AD.AuditSeq} = a.{CompanySchema.BusinessAudit.Seq}) ORDER BY a.{CompanySchema.BusinessAudit.Seq} LIMIT 1";

    private async Task<IReadOnlyList<ApprovalStageProgress>> Plan(Guid doc) => (await Docs.ApprovalProgressAsync(Company.Id, doc))!.Plan;

    [PostgresFact]
    public async Task The_policy_is_picked_by_type_value_and_branch_from_the_sealed_total_bound_at_submit_and_no_match_keeps_the_single_approval()
    {
        var (branchA, party, item, unit) = await Setup();
        var branchB = await NewBranchId("BB");
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1, 1], min: 100m)));                              // from a base value of 100: two ordered stages
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], branch: branchA)));                           // every document of branch A: one stage
        var bigA = await Submitted(branchA, party, item, unit, "pol-key-000001", 10m);                                     // gross 116, branch A: both policies match, the most demanding wins (2 stages)
        var smallA = await Submitted(branchA, party, item, unit, "pol-key-000002", 5m);                                    // gross 58, branch A: only the branch policy (1 stage)
        var bigB = await Submitted(branchB, party, item, unit, "pol-key-000003", 10m);                                     // gross 116, branch B: the value policy (2 stages)
        var smallB = await Submitted(branchB, party, item, unit, "pol-key-000004", 5m);                                    // gross 58, branch B: no policy at all
        Assert.Equal([1, 1], (await Plan(bigA.Id)).Select(p => p.Required));
        Assert.Equal([1], (await Plan(smallA.Id)).Select(p => p.Required));
        Assert.Equal([1, 1], (await Plan(bigB.Id)).Select(p => p.Required));
        Assert.Empty(await Plan(smallB.Id));                                                                                // D-141: no matching policy, the single independent approval of S1b
        // the plan of every submitted revision is bound as an append-only floor; a document with no plan binds nothing
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {AB.Table} WHERE {AB.DocumentId} = '{bigA.Id}' AND {AB.DocumentRevision} = {bigA.Revision}"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {AB.Table} WHERE {AB.DocumentId} = '{smallB.Id}'"));
        // a document with no policy is approved by ONE independent approver exactly as in S1b
        var approver = await NewApprover();
        var approved = Ok(await Docs.ApproveAsync(approver, Company.Id, smallB.Id, smallB.Revision));
        Assert.Equal((CommercialStatus.Approved, approver), (approved.Status, approved.ApprovedBy));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {AD.Table} WHERE {AD.DocumentId} = '{smallB.Id}'"));       // no decision row: the transition itself is the approval
    }

    [PostgresFact]
    public async Task Ordered_stages_take_independent_decisions_in_order_and_the_document_is_approved_only_when_the_plan_is_complete()
    {
        var (branch, party, item, unit) = await Setup();
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1, 1])));
        var a1 = await NewApprover(); var a2 = await NewApprover();
        var doc = await Submitted(branch, party, item, unit, "ord-key-000001");
        // the creator and the submitter are never independent
        Fails(await Docs.ApproveAsync(Admin.Id, Company.Id, doc.Id, doc.Revision), ConfigStatus.Conflict, "approver_not_independent");
        // the first decision completes stage 1 only: the document stays submitted and its revision does not move
        var first = Ok(await Docs.ApproveAsync(a1, Company.Id, doc.Id, doc.Revision));
        Assert.Equal((CommercialStatus.Submitted, doc.Revision, (string?)null), (first.Status, first.Revision, first.OfficialNumber));
        Assert.Equal([1, 0], (await Plan(doc.Id)).Select(p => p.Approved));
        Fails(await Docs.ApproveAsync(a1, Company.Id, doc.Id, doc.Revision), ConfigStatus.Conflict, "already_decided");        // one decision per approver per revision
        // the second approver completes stage 2: approved, ONE number, approver recorded
        var last = Ok(await Docs.ApproveAsync(a2, Company.Id, doc.Id, doc.Revision));
        Assert.Equal((CommercialStatus.Approved, a2, "2026-000001"), (last.Status, last.ApprovedBy, last.OfficialNumber));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table}"));
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {AD.Table} WHERE {AD.DocumentId} = '{doc.Id}'"));
        var decided = await Sql<short[]>($"SELECT array_agg({AG.DecisionStage} ORDER BY {AD.Id}) FROM {AD.Table} WHERE {AD.DocumentId} = '{doc.Id}'");
        Assert.Equal(new short[] { 1, 2 }, decided);
        Assert.Equal(1L, await Events(CommercialAuditEvents.Approved));
        // an approved document takes no further decision
        Fails(await Docs.ApproveAsync(await NewApprover(), Company.Id, doc.Id, last.Revision), ConfigStatus.Conflict, "document_not_submitted");
    }

    [PostgresFact]
    public async Task A_decision_never_counts_beyond_its_revision_and_a_policy_changed_after_submit_can_only_tighten_the_plan()
    {
        var (branch, party, item, unit) = await Setup();
        var policy = Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1])));
        var a1 = await NewApprover(); var a2 = await NewApprover(); var a3 = await NewApprover();
        // a withdrawn and resubmitted document is a NEW revision: the old decisions are stale and count for nothing
        var doc = await Submitted(branch, party, item, unit, "rev-key-000001");
        var stale = Ok(await Docs.WithdrawAsync(Admin.Id, Company.Id, doc.Id, doc.Revision));
        var again = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, doc.Id, stale.Revision));
        Assert.NotEqual(doc.Revision, again.Revision);
        Assert.Equal([0], (await Plan(again.Id)).Select(p => p.Approved));
        Ok(await Docs.ApproveAsync(a1, Company.Id, again.Id, again.Revision));                                               // approves the CURRENT revision
        // tightening: a pending document submitted under ONE stage is raised to the stricter policy of today
        var pending = await Submitted(branch, party, item, unit, "rev-key-000002");
        Ok(await PolicyService.UpdateAsync(Admin.Id, Company.Id, policy.Id, policy.Revision, new ApprovalPolicyDetailsInput("any", null, 0m, null, "سياسة", "Policy", Stages: [new(1, null), new(1, null)])));
        Assert.Equal([1, 1], (await Plan(pending.Id)).Select(p => p.Required));
        Ok(await Docs.ApproveAsync(a2, Company.Id, pending.Id, pending.Revision));
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, pending.Id))!.Status);                    // the second stage is still open
        // loosening: a document submitted under TWO stages keeps them although the policy is now loosened to one
        var floor = await Submitted(branch, party, item, unit, "rev-key-000003");
        var current = (await PolicyService.GetAsync(Company.Id, policy.Id))!;
        Ok(await PolicyService.UpdateAsync(Admin.Id, Company.Id, policy.Id, current.Revision, new ApprovalPolicyDetailsInput("any", null, 0m, null, "سياسة", "Policy", Stages: [new(1, null)])));
        Assert.Equal([1, 1], (await Plan(floor.Id)).Select(p => p.Required));                                               // the bound floor cannot be lowered
        Ok(await Docs.ApproveAsync(a3, Company.Id, floor.Id, floor.Revision));
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, floor.Id))!.Status);
        // a document submitted AFTER the loosening is governed by the one stage
        var fresh = await Submitted(branch, party, item, unit, "rev-key-000004");
        Assert.Equal([1], (await Plan(fresh.Id)).Select(p => p.Required));
    }

    [PostgresFact]
    public async Task Concurrent_decisions_on_the_same_stage_are_serialized_so_exactly_one_approval_happens_with_one_number()
    {
        var (branch, party, item, unit) = await Setup();
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([2])));                                            // one stage that needs TWO distinct approvers
        var approvers = new List<Guid>(); for (var i = 0; i < 4; i++) approvers.Add(await NewApprover());
        var doc = await Submitted(branch, party, item, unit, "con-key-000001");
        var results = await Task.WhenAll(approvers.Select(a => Docs.ApproveAsync(a, Company.Id, doc.Id, doc.Revision)));
        Assert.Equal(2, results.Count(r => r.IsOk));                                                                        // two decisions complete the stage ...
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Contains(r.Code, new[] { "document_not_submitted", "approval_stage_complete", "revision_conflict" }));   // ... the others find it decided or approved
        var view = (await Docs.GetAsync(Company.Id, doc.Id))!;
        Assert.Equal(CommercialStatus.Approved, view.Status);
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {AD.Table} WHERE {AD.DocumentId} = '{doc.Id}'"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table}"));                                              // exactly one number
        Assert.Equal(1L, await Events(CommercialAuditEvents.Approved));
        // the same at the database: a second decision for a stage that is already complete is refused by the trigger (the document row is locked first)
        var single = await Submitted(branch, party, item, unit, "con-key-000002", 10m);
        var p2 = Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], min: 5000m)));                       // not matching: keeps the earlier policy for this document
        Assert.NotNull(p2);
        Ok(await Docs.ApproveAsync(approvers[0], Company.Id, single.Id, single.Revision));                                 // the plan was [2]: stage 1 holds one of two
        // the same approver again, directly: the decision trigger runs BEFORE the unique key is checked and the document is still on its open stage, so the trigger is what refuses it; the same approver is refused by the
        // "independent of the creator ..." rule only when he is one of those, otherwise the unique key answers. The unique decision key is the deterministic guard here: 23505 (the stage check passes first because stage 1 still needs one more approver).
        var (state, message) = await Run(RawDecision(single.Id, single.Revision, approvers[0], 1, 1));
        Assert.Equal("23505", state);
        Assert.Contains("unique", message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task The_value_threshold_compares_the_base_value_rounded_to_the_base_currency_minor_units_exactly_like_the_journal()
    {
        var (branch, party, item, unit) = await Setup();
        // a USD price of 14.45 and a USD document of one piece, sealed at the USD rate 0.71: 10.2595 raw, 10.260 rounded half away from zero to the three minor units of the base currency JOD
        var usdList = Ok(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, new Hasm.Infrastructure.Pricing.PriceListInput("PL-PL1USD", "قائمة", "List", 11, "USD", new DateOnly(2026, 1, 1), null, true, null)));
        Ok(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, usdList.Id, new Hasm.Infrastructure.Pricing.PriceEntryInput(item, null, unit, 0m, 14.45m, null)));
        var d = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "rnd-key-000001", 1m) with { Currency = "USD" }));
        var one = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
        Assert.Equal((14.45m, 0.71m), (one.TotalGross, one.ExchangeRate));
        Assert.Empty(await Plan(one.Id));                                                                                    // no policy yet
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], min: 10.260m)));                              // 10.2595 raw is BELOW this minimum, 10.260 rounded meets it: the journal compares the rounded amount
        Assert.Equal([1], (await Plan(one.Id)).Select(p => p.Required));
        // a minimum one thousandth above the rounded value does not apply, so the plan stays that of the 10.260 policy
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1, 1], min: 10.261m)));
        Assert.Equal([1], (await Plan(one.Id)).Select(p => p.Required));
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_a_decision_or_an_approval_that_breaks_the_plan_for_a_direct_statement()
    {
        var (branch, party, item, unit) = await Setup();
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1, 1], min: 100m)));
        var a1 = await NewApprover(); var a2 = await NewApprover(); var a3 = await NewApprover();
        var doc = await Submitted(branch, party, item, unit, "sql-key-000001");
        var draft = await NewDraft(branch, party, item, unit, "sql-key-000002", 10m);
        var small = await Submitted(branch, party, item, unit, "sql-key-000003", 5m);                                        // no policy matches: an empty plan
        string Msg(string sql) => Run(sql).Result.Message;
        // the decision rules
        Assert.Contains("current revision of a submitted", Msg(RawDecision(doc.Id, doc.Revision + 1, a1, 1, 1)));            // another revision
        Assert.Contains("current revision of a submitted", Msg(RawDecision(draft.Id, draft.Revision, a1, 1, 1)));            // a draft is not decided
        Assert.Contains("rejected by its status change", Msg(RawDecision(doc.Id, doc.Revision, a1, 2, 1)));                  // a rejection is not a decision row
        Assert.Contains("independent of the creator", Msg(RawDecision(doc.Id, doc.Revision, Admin.Id, 1, 1)));               // creator, last editor and submitter alike
        Assert.Contains("open approval stage", Msg(RawDecision(doc.Id, doc.Revision, a1, 1, 2)));                            // stage 2 before stage 1
        Assert.Contains("no approval stage applies", Msg(RawDecision(small.Id, small.Revision, a1, 1, 1)));                  // no plan: no decision
        // the gate on the document: an otherwise complete approval without the plan is refused, and so is an approver who is not one of the deciders
        string Approve(Guid id, long revision, Guid by) => $"UPDATE {CD.Table} SET {CD.Status} = 3, {CDA.ApprovedBy} = '{by}', {CDA.ApprovedAtUtc} = now(), {CDA.SealedAtUtc} = now(), {CDA.OfficialNumber} = 'X-1', {CD.Revision} = {revision + 1} WHERE {CD.Id} = '{id}'";
        Assert.Contains("required approvals for this revision are missing", Msg(Approve(doc.Id, doc.Revision, a1)));
        Ok(await Docs.ApproveAsync(a1, Company.Id, doc.Id, doc.Revision));                                                    // stage 1 done through the service
        Assert.Contains("required approvals for this revision are missing", Msg(Approve(doc.Id, doc.Revision, a1)));          // stage 2 still open
        Assert.Contains("required approvals for this revision are missing", Msg(Approve(doc.Id, doc.Revision, a3)));
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, doc.Id))!.Status);
        // the approver of the document must be one of the approvers of the revision: complete the plan through the service, then try to approve as a stranger directly
        var done = Ok(await Docs.ApproveAsync(a2, Company.Id, doc.Id, doc.Revision));
        Assert.Equal(CommercialStatus.Approved, done.Status);
        var other = await Submitted(branch, party, item, unit, "sql-key-000004", 10m);
        Ok(await Docs.ApproveAsync(a1, Company.Id, other.Id, other.Revision)); Ok(await Docs.ApproveAsync(a2, Company.Id, other.Id, other.Revision));   // approved: its decisions exist
        var third = await Submitted(branch, party, item, unit, "sql-key-000005", 10m);
        Assert.Contains("required approvals for this revision are missing", Msg(Approve(third.Id, third.Revision, a3)));     // a stranger with no decisions on this revision while the plan is open
        // L1: satisfy the plan with DIRECT-SQL decision inserts (valid audit events, stage order respected), then approve as a stranger: the plan is satisfied, so ONLY the "approver must be a decider" rule can refuse
        Assert.Null((await Run(ValidRawDecision(third.Id, third.Revision, a1, 1))).State);
        Assert.Null((await Run(ValidRawDecision(third.Id, third.Revision, a2, 2))).State);
        Assert.Contains("approver of a document must be one of the approvers of its revision", Msg(Approve(third.Id, third.Revision, a3)));
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, third.Id))!.Status);
    }

    [PostgresFact]
    public async Task The_policy_service_accepts_registered_commercial_types_refuses_journal_only_conditions_and_leaves_the_journal_unchanged()
    {
        await Setup();
        var created = Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1])));
        Assert.Equal("commercial_test", created.DocType);
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], type: "commercial_test_other")));
        // an unregistered type, a type with a digit (the policy table's shape has none: a limitation recorded for the owner) and journal-only conditions are refused
        Assert.Equal("unknown_document_type", Assert.Single((await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], type: "not_a_type"))).Fields!).Code);
        Assert.Equal("unknown_document_type", Assert.Single((await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], type: "sales_invoice2"))).Fields!).Code);
        var conditions = await PolicyService.CreateAsync(Admin.Id, Company.Id, new("commercial_test", "any", null, 0m, null, "س", "P", "normal", true, true, [new(1, null)], "closing"));
        Assert.Equal(ConfigStatus.Invalid, conditions.Status);
        Assert.Equal(["closingKind", "journalType", "rateOverride", "softClosed"], conditions.Fields!.Select(f => f.Field).Order());
        Assert.All(conditions.Fields!, f => Assert.Equal("not_applicable", f.Code));
        var update = await PolicyService.UpdateAsync(Admin.Id, Company.Id, created.Id, created.Revision, new ApprovalPolicyDetailsInput("any", null, 0m, null, "س", "P", SoftClosed: true, Stages: [new(1, null)]));
        Assert.Equal(ConfigStatus.Invalid, update.Status);                                                                   // an update cannot add one either
        // the journal path is unchanged: its own conditions are still accepted for journal_entry
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, new("journal_entry", "any", null, 0m, null, "س", "P", "normal", true, false, [new(1, null)])));
        // the production composition (no commercial type registered) refuses a commercial policy altogether
        var production = new ApprovalPolicyService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        Assert.Equal("unknown_document_type", Assert.Single((await production.CreateAsync(Admin.Id, Company.Id, Policy([1]))).Fields!).Code);
    }

    [PostgresFact]
    public async Task A_stage_bound_to_a_role_takes_decisions_only_from_its_holders_and_the_plan_is_readable_over_the_management_layer()
    {
        var (branch, party, item, unit) = await Setup();
        var stranger = Guid.NewGuid();                                                                                         // a role nobody holds
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], role: stranger)));
        var approver = await NewApprover();
        var doc = await Submitted(branch, party, item, unit, "rol-key-000001");
        // through the authorized entry point the roles come from the authorization store: the preset approver does not hold the stage role
        Fails(await DocMgmt.ApproveAsync(approver, Company.Id, doc.Id, doc.Revision), ConfigStatus.Forbidden, "approver_role_required");
        var progress = Ok(await DocMgmt.ApprovalProgressAsync(approver, Company.Id, doc.Id));
        Assert.Equal((1, 1, 0, (Guid?)stranger), (progress.Plan.Single().Stage, progress.Plan.Single().Required, progress.Plan.Single().Approved, progress.Plan.Single().RoleId));
        Assert.Empty(progress.Decisions);
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, doc.Id))!.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {AD.Table} WHERE {AD.DocumentId} = '{doc.Id}'"));          // nothing was written by the refused call
        // a viewer without the approve permission may read the plan; a user with no commercial permission may not
        var (viewer, _) = await Member([PermissionCatalog.Codes.CommercialView], ScopeKind.Company, null);
        Assert.Single(Ok(await DocMgmt.ApprovalProgressAsync(viewer.Id, Company.Id, doc.Id)).Plan);
        var (nobody, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Fails(await DocMgmt.ApprovalProgressAsync(nobody.Id, Company.Id, doc.Id), ConfigStatus.Forbidden, "forbidden");
        // the same role bound to the PRESET role of the approver is satisfied by its holder
        var preset = PresetRoles.CommercialApproverId;
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1], role: preset, min: 1000m)));                  // does not match this document (value), so the stranger role stays in force
        Assert.Single((await Plan(doc.Id)));
    }

    [PostgresFact]
    public async Task The_approvals_route_shows_the_plan_and_the_decisions_over_HTTP_with_the_view_permission_only()
    {
        await StartApi(configure: b => b.ConfigureServices(RegisterTestType));
        await ComposeAsync();
        PolicyService = new ApprovalPolicyService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock, TestRegistry());
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("PH1");
        Ok(await PolicyService.CreateAsync(Admin.Id, Company.Id, Policy([1, 1])));
        var a1 = await NewApprover();
        var doc = await Submitted(branch, party, item, unit, "http-key-00001");
        Ok(await Docs.ApproveAsync(a1, Company.Id, doc.Id, doc.Revision));
        var path = $"/api/v1/company/commercial-documents/{doc.Id}/approvals";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, path)).Status);
        var r = await Send(HttpMethod.Get, path, cookie: await SignIn(Admin));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal([1, 1], r.Json.GetProperty("plan").EnumerateArray().Select(p => p.GetProperty("required").GetInt32()));
        Assert.Equal([1, 0], r.Json.GetProperty("plan").EnumerateArray().Select(p => p.GetProperty("approved").GetInt32()));
        Assert.Equal(a1, Assert.Single(r.Json.GetProperty("decisions").EnumerateArray()).GetProperty("approver").GetGuid());
        var (nobody, _) = await Member([PermissionCatalog.Codes.WarehousesView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, path, cookie: await SignIn(nobody))).Status);
    }
}
