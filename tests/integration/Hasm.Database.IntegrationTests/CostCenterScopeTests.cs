using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Database.IntegrationTests;

/// <summary>Cost-center-scoped RBAC for journal entries and ledger reports (Phase 3.2C S1): one authorization rule for listing, details, operations and reports.</summary>
public class CostCenterScopeTests : CostCenterPolicyTestBase
{
    private async Task<(Hasm.Infrastructure.Identity.UserRecord User, Guid RoleId)> OnCostCenter(Guid cc, params string[] permissions) => await Member(permissions, ScopeKind.CostCenter, cc);

    private async Task<IReadOnlyList<Guid>> Visible(Guid user, JournalFilter? filter = null) =>
        Ok(await JMg.ListAsync(user, Company.Id, filter ?? new JournalFilter(null, null, null, null, null, 500, 0))).Select(e => e.Id).ToList();

    [PostgresFact]
    public async Task Cost_center_scoped_users_see_and_act_on_entries_wholly_inside_their_cost_centers_only()
    {
        await PrepareCostCenters();
        var a = await Leaf("A"); var b = await Leaf("B");
        var (pa, _) = await OnCostCenter(a.Id, [.. JournalOps]);
        var e1 = await Draft(Split("2026-03-10", 100m, (a.Id, 100m)));
        var e2 = await Draft(Split("2026-03-10", 100m, (b.Id, 100m)));
        var e3 = await Draft(Entry("2026-03-10", Dr(Cash.Id, 100m, null, Main, (a.Id, 100m)), Cr(Sales.Id, 100m, null, Main, (b.Id, 100m))));   // one line inside, one outside
        var e4 = await Draft(Simple());                                                                                  // no allocation at all
        Assert.Equal([e1.Header.Id], await Visible(pa.Id));
        Assert.Equal(e1.Header.Id, Ok(await JMg.GetAsync(pa.Id, Company.Id, e1.Header.Id)).Header.Id);
        foreach (var hidden in new[] { e2, e3, e4 }) Assert.Equal(ConfigStatus.Forbidden, (await JMg.GetAsync(pa.Id, Company.Id, hidden.Header.Id)).Status);
        // Creation: only entries wholly inside the cost center.
        var own = Ok(await JMg.CreateAsync(pa.Id, Company.Id, Split("2026-03-11", 40m, (a.Id, 40m))));
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(pa.Id, Company.Id, Split("2026-03-11", 40m, (b.Id, 40m)))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(pa.Id, Company.Id, Simple("2026-03-11", 40m))).Status);   // no allocation: not covered
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(pa.Id, Company.Id, Entry("2026-03-11", Dr(Cash.Id, 5m, null, Main, (a.Id, 5m)), Cr(Sales.Id, 5m)))).Status);   // one line unallocated
        // Update: neither moving an own entry outside the grant nor touching a foreign one.
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.UpdateAsync(pa.Id, Company.Id, own.Header.Id, own.Header.Revision, Split("2026-03-11", 40m, (b.Id, 40m)))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.UpdateAsync(pa.Id, Company.Id, e2.Header.Id, e2.Header.Revision, Split("2026-03-10", 100m, (a.Id, 100m)))).Status);
        var upd = Ok(await JMg.UpdateAsync(pa.Id, Company.Id, own.Header.Id, own.Header.Revision, Split("2026-03-11", 45m, (a.Id, 45m))));
        // Submit and post: own entry yes, foreign entries no (and a refusal changes nothing).
        var sub = Ok(await JMg.SubmitAsync(pa.Id, Company.Id, upd.Header.Id, upd.Header.Revision));
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.SubmitAsync(pa.Id, Company.Id, e2.Header.Id, e2.Header.Revision)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.PostAsync(pa.Id, Company.Id, e2.Header.Id, e2.Header.Revision)).Status);
        Assert.Equal(JournalStatus.Draft, (await Reload(e2.Header.Id)).Header.Status);
        Assert.Equal(JournalStatus.Posted, Ok(await JMg.PostAsync(pa.Id, Company.Id, sub.Header.Id, sub.Header.Revision)).Header.Status);
        // A user holding only VIEW at the cost center cannot create, whatever the entry looks like.
        var (viewer, _) = await OnCostCenter(a.Id, PermissionCatalog.Codes.JournalView);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.CreateAsync(viewer.Id, Company.Id, Split("2026-03-11", 1m, (a.Id, 1m)))).Status);
        Assert.Contains(upd.Header.Id, await Visible(viewer.Id));
        // The unrestricted path is untouched: company-wide sees everything, a Main-branch grant sees the Main entries regardless of cost centers.
        Assert.Equal(5, (await Visible(Admin.Id)).Count);
        var (branchUser, _) = await Member([PermissionCatalog.Codes.JournalView], ScopeKind.Branch, Main);
        Assert.Equal(5, (await Visible(branchUser.Id)).Count);
    }

    [PostgresFact]
    public async Task A_group_grant_covers_its_descendants_only_and_company_branch_and_cost_center_access_remain_distinct()
    {
        await PrepareCostCenters();
        var g = await Leaf("G", group: true); var g1 = await Leaf("G1", g.Id); var g2 = await Leaf("G2", g.Id); var other = await Leaf("OT");
        var inG1 = await Draft(Split("2026-03-10", 10m, (g1.Id, 10m)));
        var inBoth = await Draft(Split("2026-03-10", 20m, (g1.Id, 12m), (g2.Id, 8m)));
        var inOther = await Draft(Split("2026-03-10", 30m, (other.Id, 30m)));
        var onB2 = await Draft(new JournalInput(new DateOnly(2026, 3, 10), "b2", B2, [Dr(Cash.Id, 5m, null, B2), Cr(Sales.Id, 5m, null, B2)], null));
        var (group, _) = await OnCostCenter(g.Id, PermissionCatalog.Codes.JournalView);
        Assert.Equal(new[] { inG1.Header.Id, inBoth.Header.Id }.Order(), (await Visible(group.Id)).Order());
        var (leafOnly, _) = await OnCostCenter(g1.Id, PermissionCatalog.Codes.JournalView);
        Assert.Equal([inG1.Header.Id], await Visible(leafOnly.Id));                                                      // a leaf grant never reaches its sibling; the mixed entry needs both
        // A branch grant for B2 and a cost-center grant are ALTERNATIVE paths: union, never intersection, never narrowing.
        var (both, _) = await OnCostCenter(g1.Id, PermissionCatalog.Codes.JournalView);
        var viewRole = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "extra-view", [PermissionCatalog.Codes.JournalView])).Value!;
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, both.Id, viewRole.Id, ScopeKind.Branch, B2)).IsOk);
        Assert.Equal(new[] { inG1.Header.Id, onB2.Header.Id }.Order(), (await Visible(both.Id)).Order());
        // Company-wide sees all four; an explicit branch filter cannot widen a cost-center grant.
        Assert.Equal(4, (await Visible(Admin.Id)).Count);
        Assert.Empty(Ok(await JMg.ListAsync(group.Id, Company.Id, new JournalFilter(null, null, null, B2, null, 500, 0))));
        // A user with no journal permission anywhere is still refused outright.
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ListAsync((await NewUser("ccnobody")).Id, Company.Id, new JournalFilter(null, null, null, null, null))).Status);
    }

    [PostgresFact]
    public async Task Cost_center_grants_end_with_deactivation_revocation_and_never_reach_company_level_permissions()
    {
        await PrepareCostCenters();
        var a = await Leaf("DA");
        var (pa, roleId) = await OnCostCenter(a.Id, PermissionCatalog.Codes.JournalView);
        var e = await Draft(Split("2026-03-10", 10m, (a.Id, 10m)));
        Assert.Equal([e.Header.Id], await Visible(pa.Id));
        // Revocation applies before the next call.
        var assignment = (await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, pa.Id)).Value!.Single(x => x.Scope == ScopeKind.CostCenter);
        Assert.Equal((a.Id, 1L), (assignment.ScopeId!.Value, assignment.ScopeEpoch!.Value));
        Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Id)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ListAsync(pa.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Status);
        Assert.Empty((await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, pa.Id)).Value!);
        // Re-grant, then deactivate: the grant is inert; reactivation (a new activation epoch) does NOT silently restore it.
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, pa.Id, roleId, ScopeKind.CostCenter, a.Id)).IsOk);
        Assert.Equal([e.Header.Id], await Visible(pa.Id));
        Assert.Equal((AccessStatus.Conflict, "already_assigned"), ((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, pa.Id, roleId, ScopeKind.CostCenter, a.Id)) is var dup ? (dup.Status, dup.Code) : default));
        var deactivated = Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, a.Id, 1));
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ListAsync(pa.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Status);
        var reactivated = Ok(await Centers.ReactivateAsync(Admin.Id, Company.Id, a.Id, deactivated.Revision));
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ListAsync(pa.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Status);
        Assert.Equal(2L, await Sql<long>($"SELECT {CompanySchema.CostCenterPolicy.ActivationEpoch} FROM {CompanySchema.CostCenter.Table} WHERE {CompanySchema.CostCenter.Id} = '{a.Id}'"));
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, pa.Id, roleId, ScopeKind.CostCenter, a.Id)).IsOk);   // the stale assignment is retired, the new one recorded under epoch 2
        Assert.Equal([e.Header.Id], await Visible(pa.Id));
        Assert.Equal(reactivated.Revision, (await Centers.GetAsync(Company.Id, a.Id))!.Revision);
        // The epoch is maintained by the database only.
        Assert.Equal("P0001", await Fails($"UPDATE {CompanySchema.CostCenter.Table} SET {CompanySchema.CostCenterPolicy.ActivationEpoch} = 9, {CompanySchema.CostCenter.Revision} = {CompanySchema.CostCenter.Revision} + 1 WHERE {CompanySchema.CostCenter.Id} = '{a.Id}'"));
        // Unknown ids and other scopes are refused; company-level permissions are inert under a cost-center scope.
        Assert.Equal((AccessStatus.Invalid, "scope_not_verifiable"), ((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, pa.Id, roleId, ScopeKind.CostCenter, Guid.NewGuid())) is var unk ? (unk.Status, unk.Code) : default));
        var inert = (await NewUser("inert"));
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, inert.Login)).IsOk);
        var role = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "cc-admin-ish", [PermissionCatalog.Codes.CostCentersView, PermissionCatalog.Codes.JournalView])).Value!;
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, inert.Id, role.Id, ScopeKind.CostCenter, a.Id)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await new CostCenterManagementService(Rbac, Centers).ListAsync(inert.Id, Company.Id)).Status);
        Assert.Equal([e.Header.Id], await Visible(inert.Id));
        // Removing the membership ends every cost-center assignment too.
        Assert.True((await Rbac.RevokeMembershipAsync(Admin.Id, Company.Id, pa.Id)).IsOk);
        Assert.Empty((await Rbac.ListAssignmentsAsync(Admin.Id, Company.Id, pa.Id)).Value!);
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.ListAsync(pa.Id, Company.Id, new JournalFilter(null, null, null, null, null))).Status);
    }

    [PostgresFact]
    public async Task Reports_expose_only_the_granted_shares_never_double_count_and_hierarchies_roll_up_exactly_once()
    {
        await PrepareCostCenters();
        var g = await Leaf("RG", group: true); var g1 = await Leaf("RG1", g.Id); var g2 = await Leaf("RG2", g.Id); var other = await Leaf("RO");
        await Posted(Split("2026-03-10", 100m, (g1.Id, 60m), (g2.Id, 40m)));
        await Posted(Split("2026-03-11", 50m, (other.Id, 50m)));
        await Posted(Entry("2026-03-12", Dr(Cash.Id, 7m, null, Main), Cr(Sales.Id, 7m, null, Main)));                   // not allocated at all
        string[] view = [PermissionCatalog.Codes.LedgerReportsView];
        var (viewG1, _) = await OnCostCenter(g1.Id, view);
        async Task<TrialBalanceView> TbFor(Guid user, Guid? cc = null, Guid? branch = null) => Ok(await RMg.TrialBalanceAsync(user, Company.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), branch, cc, false, false));
        // The G1 viewer sees ONLY its share (60), never the line total (100), the sibling's 40, the other center's 50 or the unallocated 7.
        var tb = await TbFor(viewG1.Id);
        Assert.Equal(60m, Row(tb, Cash.Id).PeriodDebit);
        Assert.Equal(60m, Row(tb, Sales.Id).PeriodCredit);
        Assert.True(tb.Balanced);
        Assert.Equal(60m, tb.TotalDebit);
        // The general ledger shows the share in base AND original amounts (never the full line), still with a running balance.
        var gl = Ok(await RMg.GeneralLedgerAsync(viewG1.Id, Company.Id, Cash.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null, 100, 0, false));
        var line = Assert.Single(gl.Lines);
        Assert.Equal((60m, 60m, 60m), (line.Debit, line.BaseDebit, line.RunningBalance));
        // Asking for another cost center explicitly yields nothing (no leak through the filter).
        Assert.Empty((await TbFor(viewG1.Id, other.Id)).Rows);
        // A group grant sums its leaves once; filtering to the group or to a leaf inside it never double counts.
        var (viewG, _) = await OnCostCenter(g.Id, view);
        Assert.Equal(100m, Row(await TbFor(viewG.Id), Cash.Id).PeriodDebit);
        Assert.Equal(100m, Row(await TbFor(viewG.Id, g.Id), Cash.Id).PeriodDebit);
        Assert.Equal(40m, Row(await TbFor(viewG.Id, g2.Id), Cash.Id).PeriodDebit);
        // Company-wide sees everything; filtered by the group it sees the roll-up once.
        Assert.Equal(157m, Row(await TbFor(Admin.Id), Cash.Id).PeriodDebit);
        Assert.Equal(100m, Row(await TbFor(Admin.Id, g.Id), Cash.Id).PeriodDebit);
        // Branch path and cost-center path together: lines of the granted branch count in FULL exactly once (no double counting with the share).
        var (mixed, _) = await OnCostCenter(g1.Id, view);
        var branchRole = (await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "rep-branch", view)).Value!;
        Assert.True((await Rbac.AssignRoleAsync(Admin.Id, Company.Id, mixed.Id, branchRole.Id, ScopeKind.Branch, Main)).IsOk);
        Assert.Equal(157m, Row(await TbFor(mixed.Id), Cash.Id).PeriodDebit);
        // A branch-only viewer of the OTHER branch sees none of it, and export is a separate permission.
        var (b2, _) = await Member(view, ScopeKind.Branch, B2);
        Assert.Empty((await TbFor(b2.Id)).Rows);
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.TrialBalanceAsync(viewG1.Id, Company.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null, false, true)).Status);
    }

    [PostgresFact]
    public async Task The_allocatable_lookup_and_the_journal_paging_follow_the_grants_for_cost_center_users()
    {
        await PrepareCostCenters();
        var g = await Leaf("LG", group: true); var g1 = await Leaf("LG1", g.Id); var g2 = await Leaf("LG2", g.Id); var other = await Leaf("LO");
        var dead = await Leaf("LD"); Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, dead.Id, 1));
        // Company-wide and branch users may allocate to every active leaf (groups and inactive centers never qualify).
        Assert.Equal(new[] { "LG1", "LG2", "LO" }, Ok(await JMg.AllocatableCostCentersAsync(Admin.Id, Company.Id)).Select(c => c.Code).Order());
        var (branchUser, _) = await Member([PermissionCatalog.Codes.JournalCreate], ScopeKind.Branch, Main);
        Assert.Equal(3, Ok(await JMg.AllocatableCostCentersAsync(branchUser.Id, Company.Id)).Count);
        // A cost-center user gets exactly the active leaves inside the granted subtree.
        var (pg, _) = await OnCostCenter(g.Id, PermissionCatalog.Codes.JournalCreate);
        Assert.Equal(new[] { "LG1", "LG2" }, Ok(await JMg.AllocatableCostCentersAsync(pg.Id, Company.Id)).Select(c => c.Code).Order());
        Assert.Equal(ConfigStatus.Forbidden, (await JMg.AllocatableCostCentersAsync((await NewUser("lookupnobody")).Id, Company.Id)).Status);
        // Paging walks only permitted entries even when newer entries of other cost centers exist.
        var (viewer, _) = await OnCostCenter(g1.Id, PermissionCatalog.Codes.JournalView);
        var mine = new List<Guid>();
        for (var i = 0; i < 9; i++) mine.Add((await Draft(Split("2026-01-05", 1m + i, (g1.Id, 1m + i)))).Header.Id);
        for (var i = 0; i < 25; i++) await Draft(Split("2026-03-10", 1m + i, (other.Id, 1m + i)));
        var paged = new List<Guid>();
        for (var off = 0; off < 30; off += 4) paged.AddRange(Ok(await JMg.ListAsync(viewer.Id, Company.Id, new JournalFilter(null, null, null, null, null, 4, off))).Select(e => e.Id));
        Assert.Equal(mine.Order(), paged.Order());
        Assert.Equal(paged.Count, paged.Distinct().Count());
        Assert.Equal(9, (await Visible(viewer.Id)).Count);
    }
}
