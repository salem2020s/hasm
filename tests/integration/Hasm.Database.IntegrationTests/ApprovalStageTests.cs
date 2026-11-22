using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using Ab = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentApprovalBinding;
using Ad = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using Aps = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicyStage;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Je = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;

namespace Hasm.Database.IntegrationTests;

/// <summary>Risk-based and ordered approval policies (Phase 3.2C S2) against real PostgreSQL; disposable cluster only.</summary>
public class ApprovalStageTests : JournalTestBase
{
    private static ApprovalPolicyInput Staged(int[] counts, string source = "any", decimal min = 0m, string? jtype = null, bool? rate = null, bool? soft = null, string en = "Staged", Guid? role = null) =>
        new(Doc, source, null, min, null, "سياسة", en, jtype, rate, soft, [.. counts.Select(c => new ApprovalStageInput(c, role))]);

    private async Task<ApprovalPolicyView> NewStaged(params int[] counts) => Ok(await Policies.CreateAsync(Admin.Id, Company.Id, Staged(counts)));

    private async Task<string?> MessageOf(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try { foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); } await tx.CommitAsync(); return null; }
        catch (PostgresException ex) { return ex.MessageText; }
    }

    private async Task<ConfigResult<ApprovalDecisionView?>> RecordAtStage(JournalEntryView v, Guid approver, int stage)
    {
        try
        {
            var r = await Tx.ExecuteAsync(Company.Id, async w => await DocumentApprovals.RecordAsync(w, new ApprovalDecisionRequest(Doc, v.Header.Id, v.Header.Revision, approver, v.Header.CreatedBy, v.Header.SubmittedBy, true, null, stage), DateTime.UtcNow, default));
            return ConfigResult<ApprovalDecisionView?>.Ok(r.Decision);
        }
        catch (PostgresException ex) { return ConfigResult<ApprovalDecisionView?>.Fail(ConfigStatus.Conflict, ex.MessageText); }
    }

    [PostgresFact]
    public async Task Classic_policies_keep_their_exact_meaning_and_staged_policies_are_versioned_with_every_revision()
    {
        await PrepareJournal();
        var classic = await NewPolicy(required: 2);                                                                       // the 3.2B shape: N distinct approvers, one stage
        Assert.False(classic.IsStaged);
        var d = await Draft();
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, d.Header.Revision));
        Assert.Equal([(1, 2, 0)], s.Plan!.Select(p => (p.Stage, p.Required, p.Approved)));                              // exactly one stage needing the same N
        // Editing the classic policy into stages is a new revision; the classic count disappears from its meaning.
        var staged = Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, classic.Id, classic.Revision, new ApprovalPolicyDetailsInput("any", null, 0m, null, "سياسة", "Staged", Stages: [new(1, null), new(3, null)])));
        Assert.Equal((true, 4, 2L), (staged.IsStaged, staged.RequiredApprovals, staged.Revision));
        Assert.Equal([(1, 1), (2, 3)], staged.Stages!.Select(x => (x.StageNo, x.Required)));
        // Deactivating and reactivating never lose the stage set (the set follows every revision), and the change is audited with its stages.
        var off = Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, staged.Id, staged.Revision));
        var on = Ok(await Policies.ReactivateAsync(Admin.Id, Company.Id, staged.Id, off.Revision));
        Assert.Equal([(1, 1), (2, 3)], on.Stages!.Select(x => (x.StageNo, x.Required)));
        Assert.Equal(3L, await Sql<long>($"SELECT count(DISTINCT {Aps.PolicyRevision}) FROM {Aps.Table} WHERE {Aps.PolicyId} = '{classic.Id}'"));   // revisions 2, 3, 4 each hold their set
        Assert.Equal(6L, await Sql<long>($"SELECT count(*) FROM {Aps.Table} WHERE {Aps.PolicyId} = '{classic.Id}'"));
        var back = Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, on.Id, on.Revision, new ApprovalPolicyDetailsInput("any", null, 0m, 2, "سياسة", "Classic again")));
        Assert.Equal((false, 2), (back.IsStaged, back.RequiredApprovals));                                                // no stage rows for the new revision: classic again
        Assert.Equal(2, (await Reload(d.Header.Id)).RequiredApprovals);                                                    // the pending entry still needs what it was submitted under
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {CompanySchema.LedgerHistory.Table} WHERE {CompanySchema.LedgerHistory.Entity} = 4 AND {CompanySchema.LedgerHistory.EntityKey} = '{classic.Id}' AND {CompanySchema.LedgerHistory.Changes}::text LIKE '%stages%' AND {CompanySchema.LedgerHistory.Revision} = 2"));
        // Conditions and stages are validated.
        Assert.Equal("out_of_range", (await Policies.CreateAsync(Admin.Id, Company.Id, Staged([0, 1]))).Fields!.Single().Code);
        Assert.Equal("invalid_value", (await Policies.CreateAsync(Admin.Id, Company.Id, Staged([1], jtype: "bogus"))).Fields!.Single().Code);
        Assert.Equal(ConfigStatus.Ok, (await Policies.CreateAsync(Admin.Id, Company.Id, Staged([1, 1, 1, 1, 1, 1, 1, 1, 1, 1], en: "Ten levels"))).Status);   // no business cap on the number of levels
    }

    [PostgresFact]
    public async Task Stages_complete_strictly_in_order_each_needs_its_count_of_distinct_approvers_and_posting_needs_all_of_them()
    {
        await PrepareJournal();
        await NewStaged(1, 2);
        var d = await Draft();
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, d.Header.Revision));
        Assert.Equal((JournalStatus.PendingApproval, 3), (s.Header.Status, s.RequiredApprovals));
        // A decision for stage 2 cannot be recorded while stage 1 is incomplete (enforced by PostgreSQL for every writer).
        var early = await RecordAtStage(s, Checker2, 2);
        Assert.Contains("open approval stage", early.Code);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ad.Table}"));
        // The service casts each decision for the first incomplete stage.
        var a1 = Ok(await Approve(s, Checker1));
        Assert.Equal([(1, 1, 1), (2, 2, 0)], a1.Plan!.Select(p => (p.Stage, p.Required, p.Approved)));
        Assert.Equal(JournalStatus.PendingApproval, a1.Header.Status);
        Assert.Equal(ConfigStatus.Conflict, (await Journal.PostAsync(Checker1, Company.Id, a1.Header.Id, a1.Header.Revision, false)).Status);   // not approved yet
        // A complete stage takes no more decisions; the same approver cannot decide twice across the whole chain.
        Assert.Contains("open approval stage", (await RecordAtStage(a1, Checker3, 1)).Code);
        Assert.Equal("already_decided", (await Approve(a1, Checker1)).Code);
        // Direct writes cannot skip the plan: the entry cannot move to Approved or Posted with a stage missing.
        Assert.Contains("required approvals", await MessageOf($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{a1.Header.Id}'"));
        var a2 = Ok(await Approve(a1, Checker2));
        Assert.Equal(JournalStatus.PendingApproval, a2.Header.Status);
        Assert.Equal((0, 1), (a2.Plan![0].Approved - a2.Plan[0].Required, a2.Plan[1].Approved));
        var a3 = Ok(await Approve(a2, Checker3));
        Assert.Equal(JournalStatus.Approved, a3.Header.Status);
        Assert.Equal([1, 2, 2], a3.Approvals.Select(a => a.Stage));                                                       // each decision records the stage it was cast for
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Checker3, Company.Id, a3.Header.Id, a3.Header.Revision, false)).Header.Status);
        // Stages and bindings are append-only and stage numbers must be contiguous.
        Assert.Equal("P0001", await Fails($"DELETE FROM {Aps.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ab.Table} SET {Ab.Required} = 1"));
        Assert.Contains("without gaps", await MessageOf($"INSERT INTO {Aps.Table} ({Aps.PolicyId}, {Aps.PolicyRevision}, {Aps.StageNo}, {Aps.Required}) SELECT {CompanySchema.ApprovalPolicy.Id}, {CompanySchema.ApprovalPolicy.Revision} + 100, 1, 1 FROM {CompanySchema.ApprovalPolicy.Table} LIMIT 1",
            $"INSERT INTO {Aps.Table} ({Aps.PolicyId}, {Aps.PolicyRevision}, {Aps.StageNo}, {Aps.Required}) SELECT {CompanySchema.ApprovalPolicy.Id}, {CompanySchema.ApprovalPolicy.Revision} + 100, 3, 1 FROM {CompanySchema.ApprovalPolicy.Table} LIMIT 1"));
    }

    [PostgresFact]
    public async Task Risk_conditions_select_the_governing_policy_deterministically_and_the_most_demanding_match_wins()
    {
        await PrepareJournal();
        var posted = await Posted(Simple("2026-03-12", 5m));                                                              // posted before any policy exists
        await NewPolicy(required: 1);                                                                                     // matches everything
        var rev = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, Staged([2], jtype: "reversal", en: "Reversals")));
        var rate = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, Staged([1, 1], rate: true, en: "Manual rate")));
        var soft = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, Staged([1, 1, 1], soft: true, en: "Soft closed")));
        async Task<IReadOnlyList<(int Stage, int Required)>> PlanOf(JournalEntryView v) => (await Reload(v.Header.Id)).Plan!.Select(p => (p.Stage, p.Required)).ToList();
        // A normal entry matches only the unconditional policy.
        Assert.Equal([(1, 1)], await PlanOf(await Draft(Simple())));
        // A manual exchange-rate override matches the rate policy (2 stages beat 1).
        var withRate = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "usd", Main,
            [new(AnyAcc.Id, null, "USD", 10m, null, Main, 0.72m, "manual quote", null), new(Sales.Id, null, null, null, 7.2m, Main, null, null, null)], null), true));
        Assert.Equal([(1, 1), (2, 1)], await PlanOf(withRate));
        // A reversal matches the reversal policy; an ordinary effective-rate entry does not match the rate policy.
        var reversal = Ok(await Journal.ReverseAsync(Maker, Company.Id, posted.Header.Id, posted.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 2), null)));
        Assert.Equal([(1, 2)], await PlanOf(reversal));
        var effectiveRate = await Draft(Entry("2026-03-13", Dr(AnyAcc.Id, 10m, "USD"), Cr(Sales.Id, 7.1m)));
        Assert.Equal([(1, 1)], await PlanOf(effectiveRate));
        // A soft-closed period raises the requirement to the three-stage policy.
        var mar = Year.Periods[2];
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, mar.Id, mar.Revision));
        Assert.Equal([(1, 1), (2, 1), (3, 1)], await PlanOf(await Draft(Simple("2026-03-14"))));
        // Deactivating a policy removes it from the evaluation; the choice never depends on row order (oldest wins an exact tie).
        Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, soft.Id, soft.Revision));
        Assert.Equal([(1, 1)], await PlanOf(await Draft(Simple("2026-03-15"))));
        var tieA = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, Staged([1, 1], jtype: "adjusting", en: "Tie A")));
        // (an IDENTICAL second policy is refused since company migration 0033, so the tie is between two policies that differ only in their branch scope: all branches and the entry's own branch)
        var tieB = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(Doc, "any", Main, 0m, null, "سياسة", "Tie B", "adjusting", null, null, [new(1, null), new(1, null)])));
        var adj = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, Simple("2026-04-05") with { AdjustsId = posted.Header.Id }, false));
        var expected = await Sql<Guid>($"SELECT {CompanySchema.ApprovalPolicy.Id} FROM {CompanySchema.ApprovalPolicy.Table} WHERE {CompanySchema.ApprovalPolicy.Id} IN ('{tieA.Id}', '{tieB.Id}') ORDER BY {CompanySchema.ApprovalPolicy.CreatedAtUtc}, {CompanySchema.ApprovalPolicy.Id} LIMIT 1");
        for (var i = 0; i < 3; i++)
            Assert.Equal(expected, await Sql<Guid>($"SELECT policy_id FROM {CompanySchema.ApprovalStaging.PickFunction}('{adj.Header.Id}')"));      // the oldest policy, then the lowest id: the same every time
        Assert.NotEqual(rev.Id, tieA.Id);
        Assert.NotEqual(rate.Id, tieB.Id);
    }

    [PostgresFact]
    public async Task A_policy_changed_after_submission_can_only_add_requirements_and_a_tightened_policy_needs_a_resubmission()
    {
        await PrepareJournal();
        var policy = await NewStaged(1, 1);
        var d = await Draft();
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, d.Header.Revision));
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {Ab.Table} WHERE {Ab.DocumentId} = '{d.Header.Id}'"));      // what the revision was submitted under is snapshotted
        // The owner deactivates the policy while the entry is pending: the entry still needs what it was submitted under.
        Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, policy.Id, policy.Revision));
        var a1 = Ok(await Approve(s, Checker1));
        Assert.Equal(JournalStatus.PendingApproval, a1.Header.Status);
        Assert.Equal(2, a1.RequiredApprovals);
        Assert.Contains("required approvals", await MessageOf($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{d.Header.Id}'"));
        var a2 = Ok(await Approve(a1, Checker2));
        Assert.Equal(JournalStatus.Approved, a2.Header.Status);
        // A new policy tightened after approval blocks posting; withdrawing and resubmitting binds the NEW plan to the new revision.
        await NewStaged(1, 1, 1);
        var blocked = await Journal.PostAsync(Checker3, Company.Id, a2.Header.Id, a2.Header.Revision, false);
        Assert.Equal((ConfigStatus.Conflict, "approval_required"), (blocked.Status, blocked.Code));
        Assert.Equal(JournalStatus.Approved, (await Reload(d.Header.Id)).Header.Status);
        var back = Ok(await Journal.WithdrawAsync(Maker, Company.Id, a2.Header.Id, a2.Header.Revision));
        Assert.Equal(a2.Header.Revision + 1, back.Header.Revision);
        var again = Ok(await Journal.SubmitAsync(Maker, Company.Id, back.Header.Id, back.Header.Revision));
        Assert.Equal(3, again.RequiredApprovals);
        Assert.All(again.Plan!, p => Assert.Equal(0, p.Approved));                                                        // the old revision's decisions never count
        Assert.Equal(3L, await Sql<long>($"SELECT count(*) FROM {Ab.Table} WHERE {Ab.DocumentId} = '{d.Header.Id}' AND {Ab.DocumentRevision} = {back.Header.Revision}"));
        // A rejection returns the entry to draft with a new revision, so nothing approved before can authorize the rejected content.
        var rejected = Ok(await Journal.DecideAsync(Checker1, Company.Id, again.Header.Id, again.Header.Revision, new ApprovalDecisionInput(false, "wrong account")));
        Assert.Equal((JournalStatus.Draft, again.Header.Revision + 1), (rejected.Header.Status, rejected.Header.Revision));
    }

    [PostgresFact]
    public async Task Automatic_entries_follow_the_same_staged_enforcement_as_manual_entries_and_cannot_skip_a_stage()
    {
        await PrepareJournal();
        await NewStaged(1, 1);
        var auto = Ok(await Journal.CreateAutomaticAsync(Maker, Company.Id, new AutomaticJournalInput("sales:inv-1", Simple("2026-03-20")), false));
        Assert.Equal(DocumentSource.Automatic, auto.Header.Source);
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, auto.Header.Id, auto.Header.Revision));
        Assert.Equal((JournalStatus.PendingApproval, 2), (s.Header.Status, s.RequiredApprovals));
        Assert.Contains("required approvals", await MessageOf($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{auto.Header.Id}'"));
        var a2 = Ok(await Approve(Ok(await Approve(s, Checker1)), Checker2));
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Maker, Company.Id, a2.Header.Id, a2.Header.Revision, false)).Header.Status);
        // A policy limited to automatic entries does not touch manual ones.
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, Staged([1, 1, 1], source: "automatic", en: "Auto only")));
        Assert.Equal(2, (await Submitted(Simple("2026-03-21"))).RequiredApprovals);
    }

    [PostgresFact]
    public async Task Stage_roles_are_enforced_on_every_decision_validated_when_configured_and_revocation_applies_before_the_next_decision()
    {
        await PrepareJournal();
        // Two approvers holding the approval permission through DIFFERENT roles: the Journal Approver preset (offline delegation) and a custom role made by a delegate who holds it.
        var preset = await NewUser("stagepreset");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, preset.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(preset.Login, Company.Id));
        // A role that cannot approve (or does not exist) is refused when the policy is configured.
        var plainRole = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "viewers", [PermissionCatalog.Codes.JournalView])).Value!;
        foreach (var bad in new[] { plainRole.Id, Guid.NewGuid() })
        {
            var refused = await PMg.CreateAsync(Admin.Id, Company.Id, Staged([1], role: bad));
            Assert.Equal((ConfigStatus.Invalid, "role_cannot_approve"), (refused.Status, refused.Fields!.Single().Code));
        }
        var policy = Ok(await PMg.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(Doc, "any", null, 0m, null, "سياسة", "Role stage", Stages: [new(1, PresetRoles.JournalApproverId), new(1, null)])));
        Assert.Equal(PresetRoles.JournalApproverId, policy.Stages![0].RoleId);
        var (maker, _) = await Holder(PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalSubmit, PermissionCatalog.Codes.JournalPost);
        var d = Ok(await JMg.CreateAsync(maker.Id, Company.Id, Simple()));
        var s = Ok(await JMg.SubmitAsync(maker.Id, Company.Id, d.Header.Id, d.Header.Revision));
        // A decision for the role-bound first stage from someone who does not hold the role (here: no role information at all) is refused before anything is recorded (fail closed).
        var noRole = await Journal.DecideAsync(Checker1, Company.Id, s.Header.Id, s.Header.Revision, new ApprovalDecisionInput(true, null));
        Assert.Equal((ConfigStatus.Forbidden, "approver_role_required"), (noRole.Status, noRole.Code));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ad.Table}"));
        // The preset holder decides stage 1; stage 2 (any approver) is then open to the next distinct approver.
        var first = Ok(await JMg.DecideAsync(preset.Id, Company.Id, s.Header.Id, s.Header.Revision, new ApprovalDecisionInput(true, null)));
        Assert.Equal((1, 1), (first.Plan![0].Approved, first.Plan[0].Required));
        // Revocation applies before the next decision: after the delegation is revoked the same user is refused outright and nothing is recorded.
        var second = await NewUser("stagesecond");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, second.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(second.Login, Company.Id));
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeJournalApproverOfflineAsync(second.Login, Company.Id));
        var refusedAfterRevoke = await JMg.DecideAsync(second.Id, Company.Id, s.Header.Id, s.Header.Revision, new ApprovalDecisionInput(true, null));
        Assert.Equal(ConfigStatus.Forbidden, refusedAfterRevoke.Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ad.Table}"));
        // The creator and the submitter can never approve, whatever role they hold.
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(maker.Login, Company.Id));
        Assert.Equal("separation_of_duties", (await JMg.DecideAsync(maker.Id, Company.Id, s.Header.Id, s.Header.Revision, new ApprovalDecisionInput(true, null))).Code);
        // Stage 1 is complete, so the open stage is the unbound stage 2: any other distinct approver may decide it.
        var done = Ok(await Journal.DecideAsync(Checker2, Company.Id, d.Header.Id, d.Header.Revision, new ApprovalDecisionInput(true, null)));
        Assert.Equal(JournalStatus.Approved, done.Header.Status);
    }

    [PostgresFact]
    public async Task Parallel_decisions_never_overfill_a_stage_or_record_one_approver_twice_and_the_audit_chain_stays_whole()
    {
        await PrepareJournal();
        await NewStaged(2, 1);
        var s = await Submitted();
        var approvers = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var results = await Task.WhenAll(approvers.Select(a => Journal.DecideAsync(a, Company.Id, s.Header.Id, s.Header.Revision, new ApprovalDecisionInput(true, null))));
        Assert.True(results.Count(r => r.IsOk) >= 3);                                                                     // 3 approvals complete both stages; later ones find the entry no longer pending
        var decisions = await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{s.Header.Id}'");
        Assert.Equal(3L, decisions);
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{s.Header.Id}' AND {CompanySchema.ApprovalStaging.DecisionStage} = 1"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{s.Header.Id}' AND {CompanySchema.ApprovalStaging.DecisionStage} = 2"));
        Assert.Equal(JournalStatus.Approved, (await Reload(s.Header.Id)).Header.Status);
        // The same approver racing against himself records exactly one decision.
        var t = await Submitted(Simple("2026-03-11"));
        var me = Guid.NewGuid();
        var twin = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Journal.DecideAsync(me, Company.Id, t.Header.Id, t.Header.Revision, new ApprovalDecisionInput(true, null))));
        Assert.Equal(1, twin.Count(r => r.IsOk));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{t.Header.Id}'"));
        var report = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.True(report.Verdict == AuditVerdict.Verified || report.Findings.All(f => f.Severity == FindingSeverity.Note));
        Assert.Equal(await Count(Company, Audit_.Table), report.Events);
    }

    [PostgresFact]
    public async Task The_staged_policy_api_creates_lists_and_reports_the_plan_and_stage_of_every_decision()
    {
        await StartApi();
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset"); Sales = await NewAccount("4101", "revenue");
        const string Policies_ = "/api/v1/company/approval-policies";
        var cookie = await SignIn(Admin);
        var bad = await Send(HttpMethod.Post, Policies_, new { docType = Doc, source = "any", minAmount = 0, nameEn = "x", stages = new[] { new { required = 0 } } }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var created = await Send(HttpMethod.Post, Policies_, new { docType = Doc, source = "any", minAmount = 0, nameEn = "Two stages", journalType = "normal", rateOverride = false, stages = new[] { new { required = 1 }, new { required = 2 } } }, cookie);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.True(created.Json.GetProperty("isStaged").GetBoolean());
        Assert.Equal(3, created.Json.GetProperty("requiredApprovals").GetInt32());
        Assert.Equal("normal", created.Json.GetProperty("journalType").GetString());
        Assert.False(created.Json.GetProperty("rateOverride").GetBoolean());
        Assert.Equal(2, created.Json.GetProperty("stages").GetArrayLength());
        var list = await Send(HttpMethod.Get, Policies_, cookie: cookie);
        Assert.Equal(2, list.Json.GetProperty("policies")[0].GetProperty("stages").GetArrayLength());
        var id = created.Json.GetProperty("id").GetGuid();
        var upd = await Send(HttpMethod.Put, $"{Policies_}/{id}", new { source = "any", minAmount = 0, nameEn = "Classic now", requiredApprovals = 2 }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, upd.Status);
        Assert.False(upd.Json.GetProperty("isStaged").GetBoolean());
        Assert.Equal(0, upd.Json.GetProperty("stages").GetArrayLength());
        var restaged = await Send(HttpMethod.Put, $"{Policies_}/{id}", new { source = "any", minAmount = 0, nameEn = "Again", stages = new[] { new { required = 1 }, new { required = 1 } } }, cookie, ifMatch: "\"2\"");
        Assert.Equal(2, restaged.Json.GetProperty("stages").GetArrayLength());
        // A pending entry reports its plan with the progress of every stage.
        var entry = await Send(HttpMethod.Post, "/api/v1/company/journal", new { entryDate = "2026-03-10", branchId = await MainBranch(), lines = new object[] { new { accountId = Cash.Id, debit = 10 }, new { accountId = Sales.Id, credit = 10 } } }, cookie);
        var eid = entry.Json.GetProperty("entry").GetProperty("id").GetGuid();
        var sub = await Send(HttpMethod.Post, $"/api/v1/company/journal/{eid}/submit", null, cookie, ifMatch: "\"1\"");
        Assert.Equal("pending_approval", sub.Json.GetProperty("entry").GetProperty("status").GetString());
        var plan = sub.Json.GetProperty("plan");
        Assert.Equal(2, plan.GetArrayLength());
        Assert.Equal((1, 1, 0), (plan[0].GetProperty("stage").GetInt32(), plan[0].GetProperty("required").GetInt32(), plan[0].GetProperty("approved").GetInt32()));
    }
}
