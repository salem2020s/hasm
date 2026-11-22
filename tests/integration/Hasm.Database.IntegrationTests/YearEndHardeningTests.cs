using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Ad = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using Fc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYearClosing;
using Je = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;

namespace Hasm.Database.IntegrationTests;

/// <summary>Year-end hardening (Phase 3.2C final review) against real PostgreSQL; disposable cluster only.</summary>
public abstract class YearEndHardeningBase : FiscalClosingTestBase
{
    protected async Task<JournalEntryView> SubmitApprovePost(Guid entryId, Guid? approver = null)
    {
        var v = await Reload(entryId);
        var s = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, entryId, v.Header.Revision));
        s = Ok(await Approve(s, approver ?? Checker1));
        return Ok(await Journal.PostAsync(Checker3, Company.Id, entryId, s.Header.Revision, false));
    }

    /// <summary>Closing then carryforward of one year, each through the normal workflow with an independent approver.</summary>
    protected async Task<(FiscalYearClosingView Closing, FiscalYearCarryforwardView Carry)> YearEnd(Guid yearId)
    {
        var closing = await Close(yearId);
        await SubmitApprovePost(closing.ClosingEntryId);
        var carry = await Carry(yearId);
        await SubmitApprovePost(carry.EntryId);
        return (closing, carry);
    }

    protected async Task<TrialBalanceView> TbOf(string from, string to, Guid? branch = null, Guid? cc = null, bool excludeClosing = false) =>
        Ok(await RMg.TrialBalanceAsync(Admin.Id, Company.Id, DateOnly.Parse(from), DateOnly.Parse(to), branch, cc, false, false, excludeClosing: excludeClosing));

    protected async Task<string?> RecordRaw(JournalEntryView v, Guid approver)
    {
        try
        {
            await Tx.ExecuteAsync(Company.Id, async w => await DocumentApprovals.RecordAsync(w,
                new ApprovalDecisionRequest(Doc, v.Header.Id, v.Header.Revision, approver, Guid.Empty, Guid.Empty, true, null, 1), DateTime.UtcNow, default));
            return null;
        }
        catch (Npgsql.PostgresException ex) { return ex.MessageText; }
    }
}

public class YearEndApprovalTests : YearEndHardeningBase
{
    [PostgresFact]
    public async Task Independent_approval_is_mandatory_cannot_be_configured_away_and_binds_automatic_entries_policies_only_add_to_it()
    {
        await PrepareClosing();
        // Not configurable: the service refuses it, PostgreSQL refuses it for every writer, and the stored value is true.
        Assert.Equal("approval_mandatory", (await Closing.PutSettingsAsync(Admin.Id, Company.Id, 1, new ClosingSettingsInput(Equity.Id, false))).Fields!.Single().Code);
        Assert.True((await Closing.GetSettingsAsync(Company.Id)).RequireApproval);
        Assert.Equal("23514", await Fails($"UPDATE {CompanySchema.ClosingSettings.Table} SET {CompanySchema.ClosingSettings.RequireApproval} = false, {CompanySchema.ClosingSettings.Revision} = {CompanySchema.ClosingSettings.Revision} + 1"));
        // The generated closing is an AUTOMATIC entry and still cannot reach Approved or Posted without an independent approval.
        var closing = await Close();
        var e = await Reload(closing.ClosingEntryId);
        Assert.Equal(DocumentSource.Automatic, e.Header.Source);
        Assert.Equal("entry_not_approved", (await Journal.PostAsync(Checker3, Company.Id, e.Header.Id, e.Header.Revision, false)).Code);
        Assert.Contains("required approvals", await MessageOf($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{e.Header.Id}'"));   // a direct jump to Approved is refused as well
        var sub = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, e.Header.Id, e.Header.Revision));
        Assert.Equal((JournalStatus.PendingApproval, 1), (sub.Header.Status, sub.RequiredApprovals));
        // A policy can raise the requirement; deactivating it never lowers the floor.
        var policy = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(Doc, "any", null, 0m, null, "سياسة", "Closing", ClosingKind: "closing", Stages: [new(1, null), new(1, null)])));
        Assert.Equal(2, (await Reload(e.Header.Id)).RequiredApprovals);
        Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, policy.Id, policy.Revision));
        Assert.Equal(1, (await Reload(e.Header.Id)).RequiredApprovals);
        // The same holds for the opening entry and for both reversals: each needs its own independent approval.
        var approved = Ok(await Approve(sub, Checker1));
        Ok(await Journal.PostAsync(Checker3, Company.Id, e.Header.Id, approved.Header.Revision, false));
        var carry = await Carry();
        await SubmitApprovePost(carry.EntryId);
        foreach (var reversal in new[] { Ok(await CMg.ReverseOpeningAsync(Admin.Id, Company.Id, Year.Id, new YearEndReversalInput(new DateOnly(2027, 1, 1)))), Ok(await CMg.ReverseClosingAsync(Admin.Id, Company.Id, Year.Id, new YearEndReversalInput(new DateOnly(2026, 12, 31)))) })
        {
            var rs = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, reversal.Header.Id, reversal.Header.Revision));
            Assert.Equal((JournalStatus.PendingApproval, 1), (rs.Header.Status, rs.RequiredApprovals));
            Assert.Equal("separation_of_duties", (await Approve(rs, Admin.Id)).Code);
            Ok(await Approve(rs, Checker1));
            if (reversal.Header.Description!.Contains("opening")) Ok(await Journal.PostAsync(Checker3, Company.Id, reversal.Header.Id, rs.Header.Revision, false));
        }
    }

    [PostgresFact]
    public async Task The_creator_and_the_submitter_can_never_decide_on_a_year_end_document_in_the_service_or_in_PostgreSQL()
    {
        await PrepareClosing();
        var closing = await Close();                                                                                       // created by the administrator
        var e = await Reload(closing.ClosingEntryId);
        var sub = Ok(await Journal.SubmitAsync(Maker, Company.Id, e.Header.Id, e.Header.Revision));                       // submitted by somebody else
        Assert.Equal((Admin.Id, Maker), (sub.Header.CreatedBy, sub.Header.SubmittedBy));
        Assert.Equal("separation_of_duties", (await Approve(sub, Admin.Id)).Code);
        Assert.Equal("separation_of_duties", (await Approve(sub, Maker)).Code);
        // Even a writer that skips the service check cannot record such a decision: the database trigger refuses it.
        Assert.Contains("independent approver", await RecordRaw(sub, Admin.Id));
        Assert.Contains("independent approver", await RecordRaw(sub, Maker));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{e.Header.Id}'"));
        Assert.Equal(JournalStatus.PendingApproval, (await Reload(e.Header.Id)).Header.Status);
        // An independent approver succeeds, and the entry posts only afterwards.
        Assert.Equal(JournalStatus.Approved, Ok(await Approve(sub, Checker1)).Header.Status);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{e.Header.Id}'"));
    }

    [PostgresFact]
    public async Task A_missing_or_revoked_approval_blocks_posting_and_the_entry_must_be_approved_again_by_a_currently_authorized_approver()
    {
        await PrepareClosing();
        var closing = await Close();
        var e = await Reload(closing.ClosingEntryId);
        var sub = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, e.Header.Id, e.Header.Revision));
        var (poster, _) = await Member([PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalPost], ScopeKind.Company, null);
        // No approval yet.
        var missing = await JMg.PostAsync(poster.Id, Company.Id, e.Header.Id, sub.Header.Revision);
        Assert.Equal((ConfigStatus.Conflict, "entry_not_approved"), (missing.Status, missing.Code));
        // The approver approves while authorized, then loses the delegation before the entry is posted.
        var a = await NewUser("yearendapprover1");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, a.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(a.Login, Company.Id));
        var approved = Ok(await JMg.DecideAsync(a.Id, Company.Id, e.Header.Id, sub.Header.Revision, new ApprovalDecisionInput(true, null)));
        Assert.Equal(JournalStatus.Approved, approved.Header.Status);
        Assert.Equal(AccessService.FiscalApproverStatus.Revoked, await Rbac.RevokeJournalApproverOfflineAsync(a.Login, Company.Id));
        var denied = await JMg.PostAsync(poster.Id, Company.Id, e.Header.Id, approved.Header.Revision);
        Assert.Equal((ConfigStatus.Conflict, "approver_no_longer_authorized"), (denied.Status, denied.Code));
        Assert.Equal(JournalStatus.Approved, (await Reload(e.Header.Id)).Header.Status);                                  // nothing posted, nothing numbered
        Assert.Null((await Reload(e.Header.Id)).Header.Number);
        // The entry goes back to draft (a new revision: the old approval stops counting) and a currently authorized approver approves it again.
        var w = Ok(await Journal.WithdrawAsync(Admin.Id, Company.Id, e.Header.Id, approved.Header.Revision));
        var again = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, e.Header.Id, w.Header.Revision));
        var b = await NewUser("yearendapprover2");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, b.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(b.Login, Company.Id));
        var approvedAgain = Ok(await JMg.DecideAsync(b.Id, Company.Id, e.Header.Id, again.Header.Revision, new ApprovalDecisionInput(true, null)));
        var posted = Ok(await JMg.PostAsync(poster.Id, Company.Id, e.Header.Id, approvedAgain.Header.Revision));
        Assert.Equal(JournalStatus.Posted, posted.Header.Status);
        Assert.NotNull(posted.Header.Number);
        // the same re-check for a ROLE-BOUND stage, on the opening entry of the next year
        var carry = await Carry();
        await The_stage_role_of_a_year_end_approval_must_still_cover_the_entry_at_posting(await Reload(carry.EntryId), poster.Id);
    }

    /// <summary>
    /// The posting re-check also covers the STAGE ROLE and its scope (owner decision D-208): an approval of a role-bound stage counts at posting only while the approver still holds the stage role through an
    /// assignment that covers the entry. The same role held for another branch does not count, although the approver keeps the approve permission.
    /// </summary>
    private async Task The_stage_role_of_a_year_end_approval_must_still_cover_the_entry_at_posting(JournalEntryView e, Guid posterId)
    {
        var (delegated, _) = await Member([PermissionCatalog.Codes.RolesView, PermissionCatalog.Codes.RolesCreate, PermissionCatalog.Codes.AssignmentsView, PermissionCatalog.Codes.AssignmentsCreate, PermissionCatalog.Codes.AssignmentsDelete,
            PermissionCatalog.Codes.JournalView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(delegated.Login, Company.Id));
        var created = await Rbac.CreateRoleAsync(delegated.Id, Company.Id, "year-end-stage", [PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalApprove]);
        Assert.True(created.IsOk, created.Code);
        var stageRole = created.Value!.Id;
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(Doc, "any", null, 0m, null, "سياسة", "Year-end role stage", ClosingKind: "opening", Stages: [new(1, stageRole)])));
        var sub = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, e.Header.Id, e.Header.Revision));
        Assert.Equal(stageRole, Assert.Single(sub.Plan!).RoleId);
        var approver = await NewUser("yearendstagerole");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, approver.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantJournalApproverOfflineAsync(approver.Login, Company.Id));
        var touched = new[] { e.Header.BranchId }.Concat(e.Lines.Select(l => l.BranchId)).Distinct().ToList();
        var here = new List<Guid>();
        foreach (var b in touched) { var a = await Rbac.AssignRoleAsync(delegated.Id, Company.Id, approver.Id, stageRole, ScopeKind.Branch, b); Assert.True(a.IsOk, a.Code); here.Add(a.Value!.Id); }
        var approved = Ok(await JMg.DecideAsync(approver.Id, Company.Id, e.Header.Id, sub.Header.Revision, new ApprovalDecisionInput(true, null)));
        Assert.Equal(JournalStatus.Approved, approved.Header.Status);
        // the role moves to ANOTHER branch: the approver still holds the approve permission (the preset) and still holds the role somewhere, but no longer for this entry
        var elsewhere = Guid.NewGuid();
        await Sql($"INSERT INTO {CompanySchema.Branch.Table} ({CompanySchema.Branch.Id}, {CompanySchema.Branch.Code}, {CompanySchema.Branch.NameEn}, {CompanySchema.Branch.IsActive}, {CompanySchema.Branch.IsMain}, {CompanySchema.Branch.ActivationEpoch}, {CompanySchema.Branch.Revision}, {CompanySchema.Branch.CreatedAtUtc}, {CompanySchema.Branch.UpdatedAtUtc}) VALUES ('{elsewhere}', 'ELSE', 'Elsewhere', true, false, 1, 1, now(), now())");
        Assert.DoesNotContain(elsewhere, touched);
        Assert.True((await Rbac.AssignRoleAsync(delegated.Id, Company.Id, approver.Id, stageRole, ScopeKind.Branch, elsewhere)).IsOk);
        foreach (var id in here) { var r = await Rbac.RevokeAssignmentAsync(delegated.Id, Company.Id, id); Assert.True(r.IsOk, r.Code); }
        var denied = await JMg.PostAsync(posterId, Company.Id, e.Header.Id, approved.Header.Revision);
        Assert.Equal((ConfigStatus.Conflict, "approver_no_longer_authorized"), (denied.Status, denied.Code));
        Assert.Equal(JournalStatus.Approved, (await Reload(e.Header.Id)).Header.Status);
        Assert.Null((await Reload(e.Header.Id)).Header.Number);
        // with the role for the entry's branches again, the same approval counts and the entry posts
        foreach (var b in touched) Assert.True((await Rbac.AssignRoleAsync(delegated.Id, Company.Id, approver.Id, stageRole, ScopeKind.Branch, b)).IsOk);
        Assert.Equal(JournalStatus.Posted, Ok(await JMg.PostAsync(posterId, Company.Id, e.Header.Id, approved.Header.Revision)).Header.Status);
    }

    [PostgresFact]
    public async Task Concurrent_decisions_and_posts_record_one_decision_one_posting_and_one_number()
    {
        await PrepareClosing();
        var closing = await Close();
        await SubmitApprovePost(closing.ClosingEntryId);
        var carry = await Carry();
        var e = await Reload(carry.EntryId);
        var sub = Ok(await Journal.SubmitAsync(Admin.Id, Company.Id, e.Header.Id, e.Header.Revision));
        // Several approvers (some of them racing against themselves) compete for the single required approval: exactly one decision is recorded and the entry becomes Approved once.
        Guid p = Guid.NewGuid(), q = Guid.NewGuid(), r3 = Guid.NewGuid();
        var results = await Task.WhenAll(new[] { p, p, p, q, q, r3 }.Select(x => Approve(sub, x)));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ad.Table} WHERE {Ad.DocumentId} = '{e.Header.Id}'"));
        // Parallel posting attempts of the approved opening entry: one posting, one official number, no gaps.
        var approved = await Reload(e.Header.Id);
        Assert.Equal(JournalStatus.Approved, approved.Header.Status);
        var posts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Journal.PostAsync(Checker3, Company.Id, e.Header.Id, approved.Header.Revision, false)));
        Assert.True(posts.Count(p => p.IsOk && p.Value is { Header.Status: JournalStatus.Posted }) >= 1);
        var final = await Reload(e.Header.Id);
        Assert.Equal(JournalStatus.Posted, final.Header.Status);
        Assert.NotNull(final.Header.Number);
        Assert.Equal(await Sql<long>($"SELECT count(*) FROM {Je.Table} WHERE {Je.Number} IS NOT NULL"), await Sql<long>($"SELECT count(DISTINCT {Je.Number}) FROM {Je.Table} WHERE {Je.Number} IS NOT NULL"));
    }
}

public class YearEndScaleTests : YearEndHardeningBase
{
    [PostgresFact]
    public async Task A_year_with_more_than_500_balances_closes_and_carries_forward_atomically_retries_safely_and_reverses_and_closes_again()
    {
        await PrepareClosing(ledger: false);
        // 260 revenue and 260 asset accounts in two branches: 520 profit-and-loss and 520 balance-sheet balances (each year-end entry needs more than 500 lines).
        var revenue = new List<AccountView>(); var assets = new List<AccountView>();
        for (var i = 0; i < 260; i++) { revenue.Add(await NewAccount($"4{i + 100:0000}", "revenue")); assets.Add(await NewAccount($"1{i + 100:0000}", "asset")); }
        foreach (var branch in new[] { Main, B2 })
            for (var chunk = 0; chunk < 2; chunk++)
                await Posted(Entry("2026-03-10", [.. Enumerable.Range(chunk * 130, 130).SelectMany(i => new[] { Dr(assets[i].Id, 10m, null, branch), Cr(revenue[i].Id, 10m, null, branch) })]));
        // Ordinary manual entries keep their 500-line limit.
        var tooMany = await Journal.CreateDraftAsync(Maker, Company.Id, Entry("2026-03-11", [.. Enumerable.Range(0, 501).Select(_ => Dr(Cash.Id, 1m))]), false);
        Assert.Equal("too_many", tooMany.Fields!.Single().Code);

        var preview = await Preview();
        Assert.True(preview.CanClose);
        Assert.Equal(522, preview.ClosingLines.Count);                                                                     // 520 balances + the result of each branch on the equity account
        Assert.Equal(preview.EvidenceHash, (await Preview()).EvidenceHash);                                               // reproducible
        // A failed start leaves nothing behind: a squatter on the idempotency key (or any blocker) refuses the whole operation, and a retry after the cause is gone succeeds.
        var squat = Ok(await Journal.CreateAutomaticAsync(Maker, Company.Id, new AutomaticJournalInput($"yearclose:{Year.Id}:1", Entry("2026-12-31", Dr(Cash.Id, 1m), Cr(Sales.Id, 1m))), false));
        var entriesBefore = await EntryRows();
        var failed = await Closing.StartClosingAsync(Admin.Id, Company.Id, Year.Id);
        Assert.False(failed.IsOk);
        Assert.Equal(entriesBefore, await EntryRows());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Fc.Table}"));
        Ok(await Journal.CancelAsync(Maker, Company.Id, squat.Header.Id, squat.Header.Revision));
        // Four parallel starts: exactly one succeeds, so there is one closing entry and one record.
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Closing.StartClosingAsync(Admin.Id, Company.Id, Year.Id)));
        Assert.Equal(1, starts.Count(r => r.IsOk));
        var closing = starts.Single(r => r.IsOk).Value!;
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Fc.Table}"));
        var closingEntry = await Reload(closing.ClosingEntryId);
        Assert.Equal(522, closingEntry.Lines.Count);
        Assert.True(closingEntry.Balanced);
        Assert.Equal(closingEntry.BaseDebitTotal, closingEntry.BaseCreditTotal);
        Assert.Contains((await Closing.StartClosingAsync(Admin.Id, Company.Id, Year.Id)).Fields!, f => f.Code == "no_active_closing");   // a retry never duplicates
        await SubmitApprovePost(closing.ClosingEntryId);

        var carryPreview = await Preview();
        Assert.True(carryPreview.CanCarryForward);
        Assert.Equal(522, carryPreview.OpeningLines.Count);                                                               // nothing is truncated: 520 assets + the equity result per branch
        Assert.Equal(5200m, carryPreview.OpeningLines.Where(l => assets.Any(a => a.Id == l.AccountId)).Sum(l => l.Debit));
        var carry = await Carry();
        var opening = await Reload(carry.EntryId);
        Assert.Equal(522, opening.Lines.Count);
        Assert.Equal(opening.BaseDebitTotal, opening.BaseCreditTotal);
        Assert.Contains((await Closing.CarryForwardAsync(Admin.Id, Company.Id, Year.Id)).Fields!, f => f.Code == "not_carried_forward");
        var posted = await SubmitApprovePost(carry.EntryId);
        Assert.Equal(JournalStatus.Posted, posted.Header.Status);
        var tb = await TbOf("2027-01-01", "2027-12-31");
        Assert.Equal((5200m, -5200m, true), (tb.Rows.Where(r => assets.Any(a => a.Id == r.AccountId)).Sum(r => r.OpeningBalance), Row(tb, Equity.Id).OpeningBalance, tb.Balanced));

        // Reversal and re-closing of the large entries: opening first, then the closing, then a new version.
        var revOpening = Ok(await CMg.ReverseOpeningAsync(Admin.Id, Company.Id, Year.Id, new YearEndReversalInput(new DateOnly(2027, 1, 1))));
        Assert.Equal(522, revOpening.Lines.Count);
        await SubmitApprovePost(revOpening.Header.Id);
        var revClosing = Ok(await CMg.ReverseClosingAsync(Admin.Id, Company.Id, Year.Id, new YearEndReversalInput(new DateOnly(2026, 12, 31))));
        Assert.Equal(522, revClosing.Lines.Count);
        await SubmitApprovePost(revClosing.Header.Id);
        Assert.Equal("reversed", (await Closing.StatusAsync(Company.Id, Year.Id))!.Closings.Single().State);
        var (c2, k2) = await YearEnd(Year.Id);
        Assert.Equal(2, c2.Version);
        Assert.Equal(522, (await Reload(c2.ClosingEntryId)).Lines.Count);
        Assert.Equal(522, (await Reload(k2.EntryId)).Lines.Count);
        Assert.Equal("carried_forward", (await Closing.StatusAsync(Company.Id, Year.Id))!.Closings.Last().State);
    }
}

public class YearEndDimensionTests : YearEndHardeningBase
{
    [PostgresFact]
    public async Task Opening_balances_carry_cost_center_balances_exactly_never_fabricate_allocations_and_survive_rename_deactivation_and_restructuring()
    {
        await PrepareClosing(ledger: false);
        var ccPol = new CostCenterPolicyService(Resolver, Tx, Clock);
        var cc1 = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("CC1", null, "مركز 1", "Center 1", null, false)));
        var cc2 = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("CC2", null, "مركز 2", "Center 2", null, false)));
        await Posted(Entry("2026-01-10", Dr(Cash.Id, 50m), Cr(Equity.Id, 50m)));                                          // before any rule: stays unallocated
        Ok(await ccPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, 0, new AccountCostCenterRuleInput(true)));      // the balance-sheet account and a profit-and-loss account now REQUIRE a cost center
        Ok(await ccPol.SetAccountRuleAsync(Admin.Id, Company.Id, Sales.Id, 0, new AccountCostCenterRuleInput(true)));
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 600m, null, Main, (cc1.Id, 600m)), Cr(Sales.Id, 600m, null, Main, (cc1.Id, 600m))));
        await Posted(Entry("2026-04-10", Dr(Cash.Id, 400m, null, B2, (cc2.Id, 400m)), Cr(Sales.Id, 400m, null, B2, (cc2.Id, 400m))));
        await Posted(Entry("2026-05-10", Dr(Expense.Id, 150m, null, Main), Cr(Cash.Id, 150m, null, Main, (cc1.Id, 150m))));
        // The closing entry carries no cost center (its profit-and-loss lines are exempt from the requirement) and still posts.
        var closing = await Close();
        await SubmitApprovePost(closing.ClosingEntryId);
        Assert.DoesNotContain((await Reload(closing.ClosingEntryId)).Lines, l => l.Allocations.Count > 0);
        // History is restructured BEFORE the opening entry exists: a cost center is deactivated, another one becomes a group and one is renamed.
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, cc2.Id, cc2.Revision));
        Ok(await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, cc1.Id, cc1.Revision, new CostCenterDetailsInput("اسم جديد", "Renamed", null, true)));
        var preview = await Preview();
        var dims = preview.OpeningLines.Select(l => (l.AccountId, l.BranchId, l.CostCenterId, l.Debit, l.Credit)).ToHashSet();
        Assert.Contains((Cash.Id, Main, (Guid?)cc1.Id, 450m, 0m), dims);                                                  // the balance history holds for the cost center, exactly
        Assert.Contains((Cash.Id, B2, (Guid?)cc2.Id, 400m, 0m), dims);
        Assert.Contains((Cash.Id, Main, (Guid?)null, 50m, 0m), dims);                                                     // the pre-rule remainder stays unallocated: nothing is fabricated
        Assert.DoesNotContain(preview.OpeningLines, l => l.AccountId == Equity.Id && l.CostCenterId is not null);
        Assert.DoesNotContain(preview.OpeningLines, l => l.AccountId == Sales.Id || l.AccountId == Expense.Id);          // revenue and expense activity is never carried
        var carry = await Carry();
        var opening = await Reload(carry.EntryId);
        Assert.Equal(dims, opening.Lines.Select(l => (l.AccountId, l.BranchId, l.Allocations.SingleOrDefault()?.CostCenterId, l.BaseDebit, l.BaseCredit)).ToHashSet());
        // The opening entry posts although its allocations point at a deactivated cost center and a group, and although it holds an unallocated line on a requiring account.
        Assert.Equal(JournalStatus.Posted, (await SubmitApprovePost(carry.EntryId)).Header.Status);
        // The balances reconcile with the year they come from at every reporting dimension: company, branch and cost center.
        var y26 = await TbOf("2026-01-01", "2026-12-31"); var y27 = await TbOf("2027-01-01", "2027-12-31");
        Assert.Equal((Row(y26, Cash.Id).ClosingBalance, Row(y26, Equity.Id).ClosingBalance), (Row(y27, Cash.Id).OpeningBalance, Row(y27, Equity.Id).OpeningBalance));
        Assert.Equal(900m, Row(y27, Cash.Id).OpeningBalance);
        Assert.Equal(900m, Row(y27, Cash.Id).ClosingBalance);                                                              // the opening entry is never counted twice
        foreach (var (branch, expected) in new[] { (Main, 500m), (B2, 400m) })
            Assert.Equal(expected, Row(await TbOf("2027-01-01", "2027-12-31", branch), Cash.Id).OpeningBalance);
        Assert.Equal(450m, Row(await TbOf("2027-01-01", "2027-12-31", null, cc1.Id), Cash.Id).OpeningBalance);
        Assert.Equal(Row(await TbOf("2026-01-01", "2026-12-31", null, cc1.Id), Cash.Id).ClosingBalance, Row(await TbOf("2027-01-01", "2027-12-31", null, cc1.Id), Cash.Id).OpeningBalance);
        Assert.Equal(400m, Row(await TbOf("2027-01-01", "2027-12-31", null, cc2.Id), Cash.Id).OpeningBalance);
        // The database reproduces exactly the same figures it verified (dimension-aware comparison).
        Assert.True(await Sql<bool>($"SELECT {CompanySchema.ClosingLogic.MatchOpeningFunction}('{carry.EntryId}')"));
        // The reversal of the opening balances stays possible after the restructuring.
        var reversal = Ok(await CMg.ReverseOpeningAsync(Admin.Id, Company.Id, Year.Id, new YearEndReversalInput(new DateOnly(2027, 1, 1))));
        Assert.Equal(JournalStatus.Posted, (await SubmitApprovePost(reversal.Header.Id)).Header.Status);
    }
}

public class MultiYearReportingTests : YearEndHardeningBase
{
    [PostgresFact]
    public async Task Balances_are_reproducible_for_every_year_and_range_across_closings_opening_balances_reversals_and_re_closing()
    {
        await PrepareClosing(ledger: false);
        var year28 = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2028", new DateOnly(2028, 1, 1), "monthly")));
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1000m), Cr(Sales.Id, 1000m)));
        await Posted(Entry("2026-05-02", Dr(Cash.Id, 300m, null, B2), Cr(Sales.Id, 300m, null, B2)));
        await Posted(Entry("2026-06-01", Dr(Expense.Id, 200m), Cr(Cash.Id, 200m)));
        var activity26 = await TbOf("2026-01-01", "2026-12-31", excludeClosing: true);                                    // the year before any year-end step
        await YearEnd(Year.Id);
        await Posted(Entry("2027-02-02", Dr(Cash.Id, 500m), Cr(Sales.Id, 500m)));
        await Posted(Entry("2027-03-03", Dr(Expense.Id, 100m, null, B2), Cr(Cash.Id, 100m, null, B2)));
        await YearEnd(Year27.Id);
        await Posted(Entry("2028-01-15", Dr(Cash.Id, 50m), Cr(Sales.Id, 50m)));

        // Each year on its own: opening = the previous closing, period = real activity only (the opening entry is never part of it), closing = opening + period.
        var t26 = await TbOf("2026-01-01", "2026-12-31"); var t27 = await TbOf("2027-01-01", "2027-12-31"); var t28 = await TbOf("2028-01-01", "2028-12-31");
        Assert.Equal((0m, 1300m, 200m, 1100m), (Row(t26, Cash.Id).OpeningBalance, Row(t26, Cash.Id).PeriodDebit, Row(t26, Cash.Id).PeriodCredit, Row(t26, Cash.Id).ClosingBalance));
        Assert.Equal((1100m, 500m, 100m, 1500m), (Row(t27, Cash.Id).OpeningBalance, Row(t27, Cash.Id).PeriodDebit, Row(t27, Cash.Id).PeriodCredit, Row(t27, Cash.Id).ClosingBalance));
        Assert.Equal((1500m, 50m, 0m, 1550m), (Row(t28, Cash.Id).OpeningBalance, Row(t28, Cash.Id).PeriodDebit, Row(t28, Cash.Id).PeriodCredit, Row(t28, Cash.Id).ClosingBalance));
        Assert.Equal((-1100m, -1500m, -1500m), (Row(t26, Equity.Id).ClosingBalance, Row(t27, Equity.Id).ClosingBalance, Row(t28, Equity.Id).ClosingBalance));
        Assert.Equal((0m, -50m), (Row(t28, Sales.Id).OpeningBalance, Row(t28, Sales.Id).ClosingBalance));                 // profit and loss restarts every year
        Assert.True(t26.Balanced && t27.Balanced && t28.Balanced);
        // The earlier year is unchanged by everything that happened afterwards (comparatives are stable).
        var again26 = await TbOf("2026-01-01", "2026-12-31", excludeClosing: true);
        Assert.Equal(activity26.Rows.Select(r => (r.AccountId, r.OpeningBalance, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance)), again26.Rows.Select(r => (r.AccountId, r.OpeningBalance, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance)));
        // A range that spans the carryforward boundary reports the real opening and activity, with nothing counted twice.
        var span = await TbOf("2026-06-01", "2027-06-30");
        Assert.Equal((1300m, 500m, 300m, 1500m), (Row(span, Cash.Id).OpeningBalance, Row(span, Cash.Id).PeriodDebit, Row(span, Cash.Id).PeriodCredit, Row(span, Cash.Id).ClosingBalance));
        // Branch filtering follows the same rules.
        var b2 = await TbOf("2027-01-01", "2027-12-31", B2); var main = await TbOf("2027-01-01", "2027-12-31", Main);
        Assert.Equal((300m, 200m), (Row(b2, Cash.Id).OpeningBalance, Row(b2, Cash.Id).ClosingBalance));
        Assert.Equal((800m, 1300m), (Row(main, Cash.Id).OpeningBalance, Row(main, Cash.Id).ClosingBalance));

        // A late posting into 2027 makes its closing and opening balances stale; unwinding them (opening first, then closing) and closing again changes nothing but the corrected figures.
        await Posted(Entry("2027-12-15", Dr(Expense.Id, 40m, null, B2), Cr(Cash.Id, 40m, null, B2)));
        Assert.True((await Closing.StatusAsync(Company.Id, Year27.Id))!.Closings.Single().Stale);
        await SubmitApprovePost(Ok(await CMg.ReverseOpeningAsync(Admin.Id, Company.Id, Year27.Id, new YearEndReversalInput(new DateOnly(2028, 1, 1)))).Header.Id);
        await SubmitApprovePost(Ok(await CMg.ReverseClosingAsync(Admin.Id, Company.Id, Year27.Id, new YearEndReversalInput(new DateOnly(2027, 12, 31)))).Header.Id);
        var (c2, _) = await YearEnd(Year27.Id);
        Assert.Equal((2, -360m), (c2.Version, c2.NetResult));
        var n26 = await TbOf("2026-01-01", "2026-12-31"); var n27 = await TbOf("2027-01-01", "2027-12-31"); var n28 = await TbOf("2028-01-01", "2028-12-31");
        Assert.Equal((Row(t26, Cash.Id).OpeningBalance, Row(t26, Cash.Id).ClosingBalance), (Row(n26, Cash.Id).OpeningBalance, Row(n26, Cash.Id).ClosingBalance));   // 2026 untouched
        Assert.Equal((1100m, 1460m), (Row(n27, Cash.Id).OpeningBalance, Row(n27, Cash.Id).ClosingBalance));
        Assert.Equal(-1460m, Row(n27, Equity.Id).ClosingBalance);
        Assert.Equal((1460m, 1510m), (Row(n28, Cash.Id).OpeningBalance, Row(n28, Cash.Id).ClosingBalance));              // the reissued opening balances are not counted twice
        Assert.Equal(-1460m, Row(n28, Equity.Id).OpeningBalance);
        Assert.True(n26.Balanced && n27.Balanced && n28.Balanced);
        var wide = await TbOf("2027-01-01", "2028-12-31");
        Assert.Equal((1100m, 1510m), (Row(wide, Cash.Id).OpeningBalance, Row(wide, Cash.Id).ClosingBalance));
        var gl = Ok(await RMg.GeneralLedgerAsync(Admin.Id, Company.Id, Cash.Id, new DateOnly(2028, 1, 1), new DateOnly(2028, 12, 31), null, null, 100, 0, false));
        Assert.Equal((1460m, 1510m, 1), (gl.OpeningBalance, gl.ClosingBalance, gl.Lines.Count));                          // the carried balance is the opening balance, not an extra line
        Assert.NotNull(year28);
    }
}

public class ReportAuthorizationTests : CostCenterPolicyTestBase
{
    [PostgresFact]
    public async Task Exports_and_documents_spanning_cost_centers_or_branches_never_expose_more_than_the_grants_allow()
    {
        await PrepareCostCenters();
        var x1 = await Leaf("X1"); var x2 = await Leaf("X2");
        await Posted(Split("2026-03-10", 20m, (x1.Id, 12m), (x2.Id, 8m)));                                                // one document allocated across two cost centers
        await Posted(Entry("2026-03-11", Dr(Cash.Id, 30m, null, Main), Cr(Sales.Id, 30m, null, B2)));                    // one document across two branches
        await Posted(Split("2026-03-12", 77m, (x2.Id, 77m)));                                                             // somebody else's cost center
        string[] perms = [PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.LedgerReportsView, PermissionCatalog.Codes.LedgerReportsExport];
        var (ccUser, _) = await Member(perms, ScopeKind.CostCenter, x1.Id);
        var listed = Ok(await JMg.ListAsync(ccUser.Id, Company.Id, new JournalFilter(null, null, null, null, null, 100, 0)));
        Assert.Empty(listed);                                                                                              // no document is wholly inside X1
        foreach (var e in Ok(await JMg.ListAsync(Admin.Id, Company.Id, new JournalFilter(null, null, null, null, null, 100, 0))))
            Assert.Equal(ConfigStatus.Forbidden, (await JMg.GetAsync(ccUser.Id, Company.Id, e.Id)).Status);
        // The exports carry only the granted share: not the line total (20), the sibling share (8 / 77) or the cross-branch amount (30).
        var tb = Ok(await RMg.TrialBalanceAsync(ccUser.Id, Company.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null, false, true));
        Assert.Equal(12m, Row(tb, Cash.Id).PeriodDebit);
        var csv = Encoding.UTF8.GetString(LedgerReportService.TrialBalanceCsv(tb));
        Assert.Contains("12.0000", csv);
        foreach (var leak in new[] { "20.0000", "77.0000", "30.0000", "8.0000,", "127.0000" }) Assert.DoesNotContain(leak, csv);
        var gl = Ok(await RMg.GeneralLedgerAsync(ccUser.Id, Company.Id, Cash.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null, 100, 0, true));
        var glCsv = Encoding.UTF8.GetString(LedgerReportService.GeneralLedgerCsv(gl));
        Assert.Equal(1, gl.Lines.Count);
        Assert.Contains("12.0000", glCsv);
        foreach (var leak in new[] { "20.0000", "77.0000", "30.0000" }) Assert.DoesNotContain(leak, glCsv);
        // A Main-branch user sees the documents wholly inside Main and, in the reports, only the lines of Main (the Main side of the cross-branch document, never its B2 side).
        var (mainUser, _) = await Member(perms, ScopeKind.Branch, Main);
        var visible = Ok(await JMg.ListAsync(mainUser.Id, Company.Id, new JournalFilter(null, null, null, null, null, 100, 0)));
        Assert.Equal(2, visible.Count);
        var mainTb = Ok(await RMg.TrialBalanceAsync(mainUser.Id, Company.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null, false, true));
        Assert.Equal((127m, 97m), (Row(mainTb, Cash.Id).PeriodDebit, Row(mainTb, Sales.Id).PeriodCredit));
        Assert.DoesNotContain("30.0000,", Encoding.UTF8.GetString(LedgerReportService.TrialBalanceCsv(mainTb)).Replace("127.0000", ""));
    }
}
