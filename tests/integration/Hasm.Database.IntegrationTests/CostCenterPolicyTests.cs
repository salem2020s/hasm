using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Ar = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountCostCenterRule;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Eg = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenterBranchEligibility;
using Ja = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalAllocation;
using Jl = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using Ph = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PolicyHistory;

namespace Hasm.Database.IntegrationTests;

/// <summary>Cost-center accounting policies and cost-center-scoped access (Phase 3.2C S1) against real PostgreSQL; disposable cluster only.</summary>
public abstract class CostCenterPolicyTestBase : LedgerReportTestBase
{
    protected CostCenterPolicyService CcPol { get; private set; } = null!;
    protected CostCenterPolicyManagementService CcPolMg { get; private set; } = null!;

    protected async Task PrepareCostCenters()
    {
        await PrepareReports();
        CcPol = new CostCenterPolicyService(Resolver, Tx, Clock);
        CcPolMg = new CostCenterPolicyManagementService(Rbac, CcPol);
    }

    protected async Task<CostCenterView> Leaf(string code, Guid? parent = null, bool group = false) =>
        Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput(code, parent, "مركز " + code, "CC " + code, null, group)));

    /// <summary>A balanced Cash/Sales entry whose BOTH lines are split across the same cost centers.</summary>
    protected JournalInput Split(string date, decimal amount, params (Guid Cc, decimal Amount)[] split) =>
        Entry(date, Dr(Cash.Id, amount, null, Main, split), Cr(Sales.Id, amount, null, Main, split));

    protected static readonly string[] JournalOps =
    [
        PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalUpdate, PermissionCatalog.Codes.JournalSubmit, PermissionCatalog.Codes.JournalPost,
    ];
}

public class CostCenterPolicyTests : CostCenterPolicyTestBase
{
    [PostgresFact]
    public async Task A_cost_center_is_optional_by_default_required_when_configured_enforced_on_submit_and_post_and_never_blocks_a_reversal()
    {
        await PrepareCostCenters();
        var a = await Leaf("A");
        Assert.Equal(JournalStatus.Posted, (await Posted(Simple())).Header.Status);                                     // optional by default
        var early = await Posted(Simple("2026-03-11", 7m));                                                              // posted before any rule exists
        var rule = Ok(await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Sales.Id, 0, new AccountCostCenterRuleInput(true)));
        Assert.Equal((true, 1L), (rule.Required, rule.Revision));
        var d = await Draft(Simple());                                                                                   // saving a draft without allocation is allowed
        var refused = await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, d.Header.Revision);
        Assert.Equal((ConfigStatus.Conflict, "cost_center_required"), (refused.Status, refused.Code));
        Assert.Equal(JournalStatus.Draft, (await Reload(d.Header.Id)).Header.Status);                                   // nothing moved
        var fixedDraft = Ok(await Journal.UpdateDraftAsync(Maker, Company.Id, d.Header.Id, d.Header.Revision, Entry("2026-03-10", Dr(Cash.Id, 100m), Cr(Sales.Id, 100m, allocations: [(a.Id, 100m)])), false));
        var ok = Ok(await Journal.SubmitAsync(Maker, Company.Id, fixedDraft.Header.Id, fixedDraft.Header.Revision));
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Checker3, Company.Id, ok.Header.Id, ok.Header.Revision, false)).Header.Status);
        // Reversals mirror an earlier posting and stay possible although the rule was introduced later.
        var r = Ok(await Journal.ReverseAsync(Maker, Company.Id, early.Header.Id, early.Header.Revision, new ReversalInput(new DateOnly(2026, 6, 1), null)));
        var rs = Ok(await Journal.SubmitAsync(Maker, Company.Id, r.Header.Id, 1));
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Maker, Company.Id, r.Header.Id, rs.Header.Revision, false)).Header.Status);
        // Switching the rule off makes entries without allocation valid again.
        Assert.False(Ok(await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Sales.Id, 1, new AccountCostCenterRuleInput(false))).Required);
        Assert.Equal(JournalStatus.Posted, (await Posted(Simple("2026-03-12"))).Header.Status);
    }

    [PostgresFact]
    public async Task Rule_changes_are_revisioned_audited_idempotent_and_refuse_group_accounts_unknown_accounts_and_stale_revisions()
    {
        await PrepareCostCenters();
        var before = await Count(Company, Audit_.Table);
        var set = Ok(await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, 0, new AccountCostCenterRuleInput(true)));
        Assert.Equal(1L, set.Revision);
        Assert.Equal(before + 1, await Count(Company, Audit_.Table));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ph.Table} WHERE {Ph.Entity} = 1 AND {Ph.EntityKey} = '{Cash.Id}'"));
        var same = Ok(await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, 1, new AccountCostCenterRuleInput(true)));   // unchanged: no revision, no audit event
        Assert.Equal(1L, same.Revision);
        Assert.Equal(before + 1, await Count(Company, Audit_.Table));
        var stale = await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, 0, new AccountCostCenterRuleInput(false));
        Assert.Equal((ConfigStatus.PreconditionFailed, 1L), (stale.Status, stale.CurrentRevision));
        Assert.Equal(ConfigStatus.PreconditionRequired, (await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, null, new AccountCostCenterRuleInput(false))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, 1, new AccountCostCenterRuleInput(null))).Status);
        var group = await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, GroupAcc.Id, 0, new AccountCostCenterRuleInput(true));
        Assert.Equal((ConfigStatus.Conflict, "account_is_group"), (group.Status, group.Code));
        Assert.Equal(ConfigStatus.NotFound, (await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Guid.NewGuid(), 0, new AccountCostCenterRuleInput(true))).Status);
        Assert.Equal(["1101"], (await CcPol.ListRequiredAccountsAsync(Company.Id)).Select(x => x.Code));
        // Switching off keeps the row and advances the revision (history is never rewritten or deleted).
        Assert.Equal(2L, Ok(await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Cash.Id, 1, new AccountCostCenterRuleInput(false))).Revision);
        Assert.Empty(await CcPol.ListRequiredAccountsAsync(Company.Id));
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {Ph.Table} WHERE {Ph.Entity} = 1"));
        var report = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.True(report.Verdict == AuditVerdict.Verified || report.Findings.All(x => x.Severity == FindingSeverity.Note));   // refused and rolled-back changes left no gap in the chain
        Assert.Equal(await Count(Company, Audit_.Table), report.Events);
    }

    [PostgresFact]
    public async Task Concurrent_rule_changes_keep_exactly_one_winner_and_a_consistent_history()
    {
        await PrepareCostCenters();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Sales.Id, 0, new AccountCostCenterRuleInput(true))));
        Assert.Equal(1, results.Count(r => r.IsOk && r.Value!.Revision == 1 && r.Status == ConfigStatus.Ok) );
        Assert.True(results.All(r => r.IsOk || r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ph.Table} WHERE {Ph.Entity} = 1"));
        var centers = await Leaf("C1");
        var branchResults = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, centers.Id, 0, new CostCenterBranchRuleInput(true, [Main]))));
        Assert.True(branchResults.Count(r => r.IsOk) >= 1 && branchResults.All(r => r.IsOk || r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ph.Table} WHERE {Ph.Entity} = 2"));
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_the_requirement_and_keeps_rules_undeletable_well_formed_and_leaf_only_even_for_direct_writes()
    {
        await PrepareCostCenters();
        Ok(await CcPol.SetAccountRuleAsync(Admin.Id, Company.Id, Sales.Id, 0, new AccountCostCenterRuleInput(true)));
        var d = await Draft(Simple());
        // A direct status change cannot bypass the requirement (the trigger runs for every writer).
        Assert.Equal("P0001", await Fails($"UPDATE {CompanySchema.JournalEntry.Table} SET {CompanySchema.JournalEntry.Status} = 3, {CompanySchema.JournalEntry.Rev} = {CompanySchema.JournalEntry.Rev} + 1 WHERE {CompanySchema.JournalEntry.Id} = '{d.Header.Id}'"));
        Assert.Equal(JournalStatus.Draft, (await Reload(d.Header.Id)).Header.Status);
        Assert.Equal("P0001", await Fails($"DELETE FROM {Ar.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Ar.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ar.Table} SET {Ar.Revision} = {Ar.Revision} + 5"));                // revision advances by exactly one
        Assert.Equal("P0001", await Fails($"UPDATE {Ar.Table} SET {Ar.AccountId} = '{Cash.Id}'"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {Ar.Table} ({Ar.AccountId}, {Ar.IsRequired}, {Ar.Revision}, {Ar.CreatedAtUtc}, {Ar.UpdatedAtUtc}) VALUES ('{GroupAcc.Id}', true, 1, now(), now())"));   // group account
        Ok(await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, (await Leaf("DB")).Id, 0, new CostCenterBranchRuleInput(true, [Main])));   // gives the delete guards a row to refuse
        Assert.Equal("P0001", await Fails($"DELETE FROM {Eg.Table}"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {CompanySchema.CostCenterBranchRule.Table}"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Ph.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ph.Table} SET {Ph.Revision} = 9"));
        Assert.Equal("23514", await Fails($"INSERT INTO {Ph.Table} ({Ph.Entity}, {Ph.EntityKey}, {Ph.Revision}, {Ph.ActorUserId}, {Ph.ChangedAtUtc}, {Ph.Changes}, {Ph.AuditStream}, {Ph.AuditSeq}) VALUES (99, 'x', 1, '{Admin.Id}', now(), '{{}}', 'business:{Company.Id}', 1)"));
    }

    [PostgresFact]
    public async Task Branch_eligibility_is_optional_enforced_per_line_branch_validated_and_never_deletes_rows()
    {
        await PrepareCostCenters();
        var cc = await Leaf("E1");
        var group = await Leaf("G", group: true);
        // No restriction by default: any branch may use the cost center (cost centers are independent of branches).
        Assert.Equal(JournalStatus.Posted, (await Posted(Entry("2026-03-10", Dr(Cash.Id, 10m, null, B2, (cc.Id, 10m)), Cr(Sales.Id, 10m, null, B2, (cc.Id, 10m))))).Header.Status);
        var rule = Ok(await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 0, new CostCenterBranchRuleInput(true, [Main])));
        Assert.Equal((true, 1L), (rule.Restricted, rule.Revision));
        Assert.Equal([Main], rule.BranchIds);
        var wrong = await Draft(Entry("2026-03-11", Dr(Cash.Id, 5m, null, B2, (cc.Id, 5m)), Cr(Sales.Id, 5m, null, B2, (cc.Id, 5m))));
        var refused = await Journal.SubmitAsync(Maker, Company.Id, wrong.Header.Id, wrong.Header.Revision);
        Assert.Equal((ConfigStatus.Conflict, "cost_center_not_eligible"), (refused.Status, refused.Code));
        Assert.Equal(JournalStatus.Posted, (await Posted(Entry("2026-03-11", Dr(Cash.Id, 5m, null, Main, (cc.Id, 5m)), Cr(Sales.Id, 5m, null, Main, (cc.Id, 5m))))).Header.Status);
        // Widening the set to the second branch makes the refused draft submittable; the eligibility rows are toggled, never deleted.
        var widened = Ok(await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 1, new CostCenterBranchRuleInput(true, [Main, B2])));
        Assert.Equal(2L, widened.Revision);
        Ok(await Journal.SubmitAsync(Maker, Company.Id, wrong.Header.Id, wrong.Header.Revision));
        var narrowed = Ok(await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 2, new CostCenterBranchRuleInput(true, [B2])));
        Assert.Equal([B2], narrowed.BranchIds);
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {Eg.Table} WHERE {Eg.CostCenterId} = '{cc.Id}'"));             // Main switched off, not deleted
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Eg.Table} WHERE {Eg.CostCenterId} = '{cc.Id}' AND {Eg.IsEligible}"));
        var open = Ok(await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 3, new CostCenterBranchRuleInput(false, [])));
        Assert.Equal((false, 4L), (open.Restricted, open.Revision));
        Assert.Equal(JournalStatus.Posted, (await Posted(Entry("2026-03-12", Dr(Cash.Id, 3m, null, Main, (cc.Id, 3m)), Cr(Sales.Id, 3m, null, Main, (cc.Id, 3m))))).Header.Status);
        // Validation.
        Assert.Equal("branches_required", (await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 4, new CostCenterBranchRuleInput(true, []))).Fields!.Single().Code);
        Assert.Equal("branch_not_found", (await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 4, new CostCenterBranchRuleInput(true, [Guid.NewGuid()]))).Fields!.Single().Code);
        var onGroup = await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, group.Id, 0, new CostCenterBranchRuleInput(true, [Main]));
        Assert.Equal((ConfigStatus.Conflict, "cost_center_is_group"), (onGroup.Status, onGroup.Code));
        Assert.Equal(ConfigStatus.NotFound, (await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, Guid.NewGuid(), 0, new CostCenterBranchRuleInput(false, []))).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await CcPol.SetBranchRuleAsync(Admin.Id, Company.Id, cc.Id, 1, new CostCenterBranchRuleInput(false, []))).Status);
        Assert.Equal("P0001", await Fails($"DELETE FROM {CompanySchema.CostCenterBranchRule.Table}"));
    }

    [PostgresFact]
    public async Task Percentage_allocations_are_distributed_deterministically_reconcile_exactly_and_invalid_modes_are_refused()
    {
        await PrepareCostCenters();
        var c1 = await Leaf("P1"); var c2 = await Leaf("P2"); var c3 = await Leaf("P3");
        JournalLineInput Line(params (Guid Cc, decimal Percent)[] parts) =>
            new(Cash.Id, null, null, 100.001m, null, Main, null, null, [.. parts.Select(p => new JournalAllocationInput(p.Cc, null, p.Percent))]);
        var credit = new JournalLineInput(Sales.Id, null, null, null, 100.001m, Main, null, null, null);
        var view = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "pct", Main, [Line((c1.Id, 33.3333m), (c2.Id, 33.3333m), (c3.Id, 33.3334m)), credit], null), false));
        var shares = view.Lines[0].Allocations.OrderBy(a => a.CostCenterId).Select(a => a.Amount).ToArray();
        Assert.Equal(100.001m, shares.Sum());                                                                           // reconciles to the line exactly
        Assert.Equal(100.001m, view.Lines[0].Allocations.Sum(a => a.BaseAmount));
        // The SAME percentages entered in another order give the SAME amounts per cost center.
        var again = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "pct2", Main, [Line((c3.Id, 33.3334m), (c1.Id, 33.3333m), (c2.Id, 33.3333m)), credit], null), false));
        Assert.Equal(view.Lines[0].Allocations.OrderBy(a => a.CostCenterId).Select(a => (a.CostCenterId, a.Amount)), again.Lines[0].Allocations.OrderBy(a => a.CostCenterId).Select(a => (a.CostCenterId, a.Amount)));
        // Percentages must total exactly 100, not be mixed with amounts, be distinct and have at most four decimals.
        async Task<string> Code(params JournalAllocationInput[] allocs) => (await Journal.CreateDraftAsync(Maker, Company.Id, new JournalInput(new DateOnly(2026, 3, 10), "bad", Main,
            [new(Cash.Id, null, null, 100m, null, Main, null, null, allocs), new(Sales.Id, null, null, null, 100m, Main, null, null, null)], null), false)).Fields!.Single().Code;
        Assert.Equal("percent_sum_mismatch", await Code(new(c1.Id, null, 60m), new(c2.Id, null, 30m)));
        Assert.Equal("percent_and_amount_mixed", await Code(new(c1.Id, 50m, null), new(c2.Id, null, 50m)));
        Assert.Equal("duplicate_cost_center", await Code(new(c1.Id, null, 50m), new(c1.Id, null, 50m)));
        Assert.Equal("invalid_percent", await Code(new(c1.Id, null, 50.00001m), new(c2.Id, null, 49.99999m)));
        Assert.Equal(await EntryRows(), 2L);                                                                              // nothing partial was written
        Assert.Equal(await Sql<long>($"SELECT count(*) FROM {Ja.Table}"), 6L);
    }

    [PostgresFact]
    public async Task Posted_allocations_stay_historically_correct_after_cost_center_recode_move_and_deactivation()
    {
        await PrepareCostCenters();
        var parentA = await Leaf("PA", group: true); var parentB = await Leaf("PB", group: true);
        var leaf = await Leaf("LF", parentA.Id);
        var p = await Posted(Split("2026-03-10", 80m, (leaf.Id, 80m)));
        var snapshot = (await Reload(p.Header.Id)).Lines.Select(l => l.Allocations.Select(a => (a.CostCenterId, a.Amount, a.BaseAmount)).ToList()).ToList();
        var tbBefore = await Tb("2026-01-01", "2026-12-31", cc: leaf.Id);
        Ok(await Centers.RecodeAsync(Admin.Id, Company.Id, leaf.Id, 1, new CostCenterCodeInput("LF2")));
        Ok(await Centers.MoveAsync(Admin.Id, Company.Id, leaf.Id, 2, new CostCenterMoveInput(parentB.Id)));
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, leaf.Id, 3));
        Assert.Equal(snapshot, (await Reload(p.Header.Id)).Lines.Select(l => l.Allocations.Select(a => (a.CostCenterId, a.Amount, a.BaseAmount)).ToList()).ToList());
        var tbAfter = await Tb("2026-01-01", "2026-12-31", cc: leaf.Id);
        Assert.Equal(tbBefore.Rows.Select(r => (r.AccountId, r.PeriodDebit, r.PeriodCredit)), tbAfter.Rows.Select(r => (r.AccountId, r.PeriodDebit, r.PeriodCredit)));
        Assert.Equal(80m, Row(tbAfter, Cash.Id).PeriodDebit);
        // The leaf's stable id keeps feeding the new parent's roll-up, never the old one (no double counting after the move).
        Assert.Equal(80m, Row(await Tb("2026-01-01", "2026-12-31", cc: parentB.Id), Cash.Id).PeriodDebit);
        Assert.Empty((await Tb("2026-01-01", "2026-12-31", cc: parentA.Id)).Rows);
    }

    [PostgresFact]
    public async Task The_policy_endpoints_need_authentication_permissions_and_if_match_and_work_end_to_end()
    {
        await StartApi();
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        var sales = await NewAccount("4101", "revenue");
        var cc = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("EP", null, "م", "C", null, false)));
        const string Base = "/api/v1/company/cost-center-policies";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/accounts")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Put, $"{Base}/accounts/{sales.Id}", new { required = true }, cookie: "forged", ifMatch: "\"0\"")).Status);
        var cookie = await SignIn(Admin);
        var list = await Send(HttpMethod.Get, $"{Base}/accounts", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        Assert.True(list.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/accounts/{sales.Id}", new { required = true }, cookie)).Status);
        Assert.Equal((HttpStatusCode)412, (await Send(HttpMethod.Put, $"{Base}/accounts/{sales.Id}", new { required = true }, cookie, ifMatch: "\"5\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/accounts/{sales.Id}", new { }, cookie, ifMatch: "\"0\"")).Status);
        var put = await Send(HttpMethod.Put, $"{Base}/accounts/{sales.Id}", new { required = true }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("\"1\"", Etag(put));
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Base}/accounts", cookie: cookie)).Json.GetProperty("accounts").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/accounts/{Guid.NewGuid()}", cookie: cookie)).Status);
        var branch = await Send(HttpMethod.Put, $"{Base}/branches/{cc.Id}", new { restricted = true, branchIds = new[] { await MainBranch() } }, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, branch.Status);
        Assert.True(branch.Json.GetProperty("restricted").GetBoolean());
        Assert.Equal(1, (await Send(HttpMethod.Get, $"{Base}/branches", cookie: cookie)).Json.GetProperty("costCenters").GetArrayLength());
        // A member without the permission (a branch grant never reaches configuration) is refused.
        var (viewer, _) = await Member([PermissionCatalog.Codes.JournalView], ScopeKind.Branch, await MainBranch());
        var viewerCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/accounts", cookie: viewerCookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"{Base}/accounts/{sales.Id}", new { required = false }, viewerCookie, ifMatch: "\"1\"")).Status);
        // The journal editor's lookup is separate from the administration permission.
        var lookup = await Send(HttpMethod.Get, "/api/v1/company/journal/cost-centers", cookie: viewerCookie);
        Assert.Equal(HttpStatusCode.OK, lookup.Status);
        Assert.Equal(1, lookup.Json.GetProperty("costCenters").GetArrayLength());
    }
}
