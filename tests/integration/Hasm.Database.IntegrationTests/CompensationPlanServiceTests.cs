using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;

namespace Hasm.Database.IntegrationTests;

/// <summary>The composed read-only planning service (one snapshot: history, comparison, classification evidence, compensation, fiscal dates, live re-authorization)
/// against real PostgreSQL. Nothing is written.</summary>
public class CompensationPlanServiceTests : StockReversalTestBase
{
    private CostFlowSnapshotService Service() => new(Resolver, Rbac, null);
    private async Task<StockPostingView> Posted(StockDocumentInput input, StockPostInput? amounts = null)
    { var p = Ok(await Post(await Approved(input), amounts)); await PostJournal(p); return p; }
    private static ProposedFlowOperation Backdated(string date, WarehouseView warehouse, decimal quantity, decimal amount) =>
        new(Guid.NewGuid(), DateOnly.Parse(date), CostOperationKind.Receipt, [new ProposedFlowLine(warehouse.Id, quantity, amount)]);
    private Task<ConfigResult<CompensationPlanResult>> Plan(CostFlowSnapshotService service, ProposedFlowOperation proposal, Guid? user = null) =>
        service.LoadCompensationPlanAsync(user ?? Admin.Id, Company.Id, Avg.Id, [W1.Id], proposal);
    private static CompensationPlanResult Value(ConfigResult<CompensationPlanResult> r) { Assert.True(r.IsOk, r.Code); return r.Value!; }
    private static void Reject(ConfigResult<CompensationPlanResult> r, ConfigStatus status, string code)
    { Assert.Equal((status, code), (r.Status, r.Code)); Assert.Null(r.Value); Assert.Null(r.Fields); }
    private async Task<FiscalPeriodView> Period(int month) => Assert.Single(Assert.Single(await Calendar.ListAsync(Company.Id)).Periods, p => p.Number == month);
    private async Task SetState(FiscalPeriodView period, int state)
    {
        if (state == 3) await Sql($"UPDATE {P.Table} SET {P.Status}=2,{P.Revision}={P.Revision}+1 WHERE {P.Id}='{period.Id}'");
        await Sql($"UPDATE {P.Table} SET {P.Status}={state},{P.FinalClosedBy}={(state == 3 ? $"'{Admin.Id}'" : "NULL")},{P.Revision}={P.Revision}+1 WHERE {P.Id}='{period.Id}'");
    }
    private async Task<long[]> Footprint() =>
        [await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.CostOperation.Table), await Count(CompanySchema.JournalEntry.Table),
         await Count(CompanySchema.JournalLine.Table), await Count(CompanySchema.StockPosting.Table), await Count(CompanySchema.BusinessAudit.Table)];
    private async Task Prepare()
    {
        await PreparePosting();
        await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        await Posted(Issue(W1, "2026-03-12", L(Avg, Pcs, 4)));
    }
    private static string[] AllPermissions => [PermissionCatalog.Codes.StockBalanceView, PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.AccountsView,
        PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.CostCentersView];

    [PostgresFact]
    public async Task One_snapshot_produces_the_plan_with_bound_periods_a_stable_fingerprint_and_writes_nothing()
    {
        await Prepare(); var march = await Period(3);
        var before = await Footprint();
        var proposal = Backdated("2026-03-11", W1, 5m, 80m);
        var result = Value(await Plan(Service(), proposal));
        var debit = Assert.Single(result.Plan.Lines, l => l.Debit > 0); var credit = Assert.Single(result.Plan.Lines, l => l.Credit > 0);
        Assert.Equal((CogsAcc.Id, 8m, StockAcc.Id, 8m), (debit.AccountId, debit.Debit, credit.AccountId, credit.Credit));
        Assert.Equal((march.Id, FiscalPeriodState.Open), (Assert.Single(result.Periods).Id, result.Periods[0].State));
        Assert.Equal(result.PlanFingerprint, Value(await Plan(Service(), proposal)).PlanFingerprint);
        Assert.Equal(before, await Footprint());
        await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(2, PostingPeriodCheck.SoftClosed)]
    [InlineData(3, PostingPeriodCheck.FinalClosed)]
    public async Task A_closed_period_of_a_compensation_date_refuses_the_whole_plan(int state, string code)
    {
        await Prepare(); await SetState(await Period(3), state);
        Reject(await Plan(Service(), Backdated("2026-03-11", W1, 5m, 80m)), ConfigStatus.Conflict, code);
    }

    [PostgresFact]
    public async Task The_proposed_events_own_date_must_also_be_in_an_open_period()
    {
        await Prepare(); await SetState(await Period(2), 2);   // February Soft Closed: the back-dated receipt would land there
        Reject(await Plan(Service(), Backdated("2026-02-15", W1, 5m, 80m)), ConfigStatus.Conflict, PostingPeriodCheck.SoftClosed);
    }

    [PostgresFact]
    public async Task An_issue_whose_journal_is_still_a_draft_refuses_the_plan()
    {
        await PreparePosting();
        await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        Ok(await Post(await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 4)))));
        Reject(await Plan(Service(), Backdated("2026-03-11", W1, 5m, 80m)), ConfigStatus.Conflict, "unposted_effect");
    }

    [PostgresFact]
    public async Task Callers_need_both_the_stock_roots_and_the_ledger_visibility_and_get_one_indistinguishable_forbidden()
    {
        await Prepare();
        var proposal = Backdated("2026-03-11", W1, 5m, 80m);
        var stockOnly = (await Member([PermissionCatalog.Codes.StockBalanceView, PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null)).User;
        var ledgerOnly = (await Member([PermissionCatalog.Codes.AccountsView, PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.StockDocsView], ScopeKind.Company, null)).User;
        var both = (await Member(AllPermissions, ScopeKind.Company, null)).User;
        Reject(await Plan(Service(), proposal, stockOnly.Id), ConfigStatus.Forbidden, "forbidden");
        Reject(await Plan(Service(), proposal, ledgerOnly.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True((await Plan(Service(), proposal, both.Id)).IsOk);
    }

    [PostgresFact]
    public async Task A_revocation_during_the_read_wins_over_the_snapshot()
    {
        await Prepare();
        var member = await Member(AllPermissions, ScopeKind.Company, null);
        var proposal = Backdated("2026-03-11", W1, 5m, 80m);
        Assert.True((await Plan(Service(), proposal, member.User.Id)).IsOk);
        var service = Service();
        service.BeforeFinalAuthorization = async _ => Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, member.User.Id)).IsOk);
        Reject(await Plan(service, proposal, member.User.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task A_proposal_that_changes_nothing_is_an_empty_balanced_plan()
    {
        await PreparePosting();
        await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        var result = Value(await Plan(Service(), Backdated("2026-03-11", W1, 5m, 80m)));      // no later issue: nothing is re-costed
        Assert.Empty(result.Plan.Lines); Assert.Empty(result.Comparison.Changes);
    }

    private int scopedUsers;
    /// <summary>A member with every non-ledger permission company-wide and JournalView ONLY at the given branches; returns the assignment ids of the branch grants.</summary>
    private async Task<(Hasm.Infrastructure.Identity.UserRecord User, List<Guid> BranchAssignments)> BranchScopedMember(params Guid[] branches)
    {
        var user = await NewUser($"scoped{Interlocked.Increment(ref scopedUsers)}");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, user.Login)).IsOk);
        var broad = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, $"broad{scopedUsers}", [PermissionCatalog.Codes.StockBalanceView, PermissionCatalog.Codes.StockDocsView,
            PermissionCatalog.Codes.AccountsView, PermissionCatalog.Codes.CostCentersView]);
        Assert.True(broad.IsOk, broad.Code);
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, user.Id, broad.Value!.Id, ScopeKind.Company, null)).IsOk);
        var ledger = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, $"ledger{scopedUsers}", [PermissionCatalog.Codes.JournalView]);
        Assert.True(ledger.IsOk, ledger.Code);
        var ids = new List<Guid>();
        foreach (var branch in branches)
        {
            var assigned = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, user.Id, ledger.Value!.Id, ScopeKind.Branch, branch);
            Assert.True(assigned.IsOk, assigned.Code); ids.Add(assigned.Value!.Id);
        }
        return (user, ids);
    }
    private async Task<Guid> OtherBranch() =>
        Ok(await Domain.CreateAsync(Admin.Id, Company.Id, new BranchInput("B2", "فرع", "Second", null, null, null, null, null, null, null, null))).Id;

    [PostgresFact]
    public async Task A_scoped_revocation_during_the_read_is_caught_by_the_live_member_recheck_not_only_by_the_bootstrap()
    {
        await Prepare();
        var proposal = Backdated("2026-03-11", W1, 5m, 80m);
        var mainBranch = Value(await Plan(Service(), proposal)).Evidence.Journals.First().BranchId;
        var (user, assignments) = await BranchScopedMember(mainBranch, await OtherBranch());
        Assert.True((await Plan(Service(), proposal, user.Id)).IsOk);
        var service = Service();
        // revoking only the main-branch grant keeps the bootstrap true (the other branch grant remains) but the journal member is no longer visible
        service.BeforeFinalAuthorization = async _ => Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignments[0])).IsOk);
        Reject(await Plan(service, proposal, user.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task A_caller_who_cannot_see_the_closure_gets_forbidden_never_a_diagnostic()
    {
        await PreparePosting();
        await Posted(Receipt(W1, "2026-03-10", L(Avg, Pcs, 10)), Amounts(100m));
        Ok(await Post(await Approved(Issue(W1, "2026-03-12", L(Avg, Pcs, 4)))));          // draft journal: an authorized caller would see unposted_effect
        var proposal = Backdated("2026-03-11", W1, 5m, 80m);
        Reject(await Plan(Service(), proposal), ConfigStatus.Conflict, "unposted_effect");
        var (blind, _) = await BranchScopedMember(await OtherBranch());                      // ledger visibility only at ANOTHER branch
        Reject(await Plan(Service(), proposal, blind.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task The_plan_fingerprint_changes_with_the_proposal_and_with_a_period_revision()
    {
        await Prepare();
        var proposal = Backdated("2026-03-11", W1, 5m, 80m);
        var baseline = Value(await Plan(Service(), proposal)).PlanFingerprint;
        Assert.NotEqual(baseline, Value(await Plan(Service(), proposal with { Lines = [new ProposedFlowLine(W1.Id, 5m, 85m)] })).PlanFingerprint);
        await Sql($"UPDATE {P.Table} SET {P.Revision}={P.Revision}+1 WHERE {P.Id}='{(await Period(3)).Id}'");
        Assert.NotEqual(baseline, Value(await Plan(Service(), proposal)).PlanFingerprint);   // a period revision after the snapshot makes a stale plan detectable
    }

    [PostgresFact]
    public async Task A_trajectory_only_change_still_brings_its_posting_into_the_evidence()
    {
        await Prepare();
        // the back-dated receipt has the same unit cost (10), so the issue keeps its cost 40 but its quantity/value trajectory changes
        var result = Value(await Plan(Service(), Backdated("2026-03-11", W1, 5m, 50m)));
        Assert.Empty(result.Plan.Lines);
        Assert.NotEmpty(result.Comparison.Changes);
        Assert.All(result.Comparison.Changes, c => Assert.Equal(c.OriginalAmount, c.ProposedAmount));
        Assert.Contains(result.Evidence.Postings, p => p.DocType == 2);                   // the issue's evidence (obligations/reversals would be seen) is loaded
    }
}
