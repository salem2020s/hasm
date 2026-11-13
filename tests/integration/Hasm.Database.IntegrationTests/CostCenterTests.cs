using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using Lh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;

namespace Hasm.Database.IntegrationTests;

/// <summary>Cost centers (company migration 0008 slice S3, Phase 3.2B) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class CostCenterTestBase : ChartAccessTestBase
{
    protected CostCenterService Centers { get; private set; } = null!;
    protected CostCenterManagementService CMg { get; private set; } = null!;
    protected long Baseline;

    protected void BuildCostCenterFacade()
    {
        Centers = new CostCenterService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        CMg = new CostCenterManagementService(Rbac, Centers);
    }

    protected async Task PrepareCenters()
    {
        await PrepareRbac();
        BuildCostCenterFacade();
        Baseline = await Count(Company, Audit_.Table);
    }

    protected static CostCenterInput In(string? code, Guid? parent = null, bool group = false, Guid? branch = null, string? ar = "مركز", string? en = "Center") => new(code, parent, ar, en, branch, group);

    protected async Task<CostCenterView> New(string code, Guid? parent = null, bool group = false, Guid? branch = null) => Ok(await Centers.CreateAsync(Admin.Id, Company.Id, In(code, parent, group, branch)));

    protected Task<Guid> MainBranch() => Sql<Guid>($"SELECT {Br.Id} FROM {Br.Table} WHERE {Br.IsMain}");

    protected async Task<Guid> InactiveBranch()
    {
        var id = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{id}', 'OLD', 'Old', false, false, 1, 1, now(), now())");
        return id;
    }

    protected static string Raw(string code, Guid? parent = null, bool group = false, bool active = true, string name = "'x'", string branch = "NULL") =>
        $"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.NameEn}, {Cc.ParentId}, {Cc.BranchId}, {Cc.IsGroup}, {Cc.IsActive}, {Cc.Revision}, {Cc.CreatedAtUtc}, {Cc.UpdatedAtUtc}) " +
        $"VALUES ('{code}', {name}, {(parent is null ? "NULL" : $"'{parent}'")}, {branch}, {(group ? "true" : "false")}, {(active ? "true" : "false")}, 1, now(), now())";

    protected Task<string?> RawFails(params string[] sql) => InTx(sql);
}

public class CostCenterServiceTests : CostCenterTestBase
{
    [PostgresFact]
    public async Task A_tree_of_any_depth_is_built_with_normalised_codes_bilingual_names_audit_and_history()
    {
        await PrepareCenters();
        Assert.Empty(Ok(await CMg.ListAsync(Admin.Id, Company.Id)));
        var parent = await New("OPS", group: true);
        Assert.True(parent.IsGroup && parent.IsActive && !parent.CanReceiveAllocations);
        var node = parent;
        for (var i = 1; i <= 12; i++) node = await New($"OPS-{i}", node.Id, group: true);          // no fixed depth
        var leaf = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, In(" ٢٠٤ ", node.Id, ar: "قسم", en: null)));   // Arabic-Indic digits normalised
        Assert.Equal("204", leaf.Code);
        Assert.Null(leaf.NameEn);
        Assert.True(leaf.CanReceiveAllocations);
        Assert.Equal(node.Id, leaf.ParentId);
        Assert.Equal(14, (await Centers.ListAsync(Company.Id)).Count);
        Assert.Equal(14L, await Count(Company, Lh.Table));
        Assert.Equal(14L, await Count(Company, Audit_.Table) - Baseline);                             // one authenticated audit event per change
        Assert.Equal(14L, await Sql<long>($"SELECT count(DISTINCT ({Lh.AuditStream}, {Lh.AuditSeq})) FROM {Lh.Table} WHERE {Lh.Entity} = 3"));
    }

    [PostgresFact]
    public async Task Creation_validates_codes_names_parents_and_branches_and_codes_are_unique_case_insensitively()
    {
        await PrepareCenters();
        var group = await New("G1", group: true);
        var leaf = await New("L1", group.Id);
        Assert.Equal("code_in_use", (await Centers.CreateAsync(Admin.Id, Company.Id, In("l1"))).Code);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.CreateAsync(Admin.Id, Company.Id, In(null))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.CreateAsync(Admin.Id, Company.Id, In("bad code!"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.CreateAsync(Admin.Id, Company.Id, In("N1", ar: null, en: null))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.CreateAsync(Admin.Id, Company.Id, In("N2", Guid.NewGuid()))).Status);        // unknown parent
        Assert.Equal("parent_not_group", (await Centers.CreateAsync(Admin.Id, Company.Id, In("N3", leaf.Id))).Code);                   // a leaf cannot have children
        var g2 = await New("G2", group: true);
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, g2.Id, 1));
        Assert.Equal(ConfigStatus.Conflict, (await Centers.CreateAsync(Admin.Id, Company.Id, In("N4", g2.Id))).Status);              // inactive parent
        var main = await MainBranch();
        Assert.Equal(main, (await New("B1", branch: main)).BranchId);                                                                   // optional, informational branch link
        Assert.Equal(ConfigStatus.Invalid, (await Centers.CreateAsync(Admin.Id, Company.Id, In("B2", branch: Guid.NewGuid()))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.CreateAsync(Admin.Id, Company.Id, In("B3", branch: await InactiveBranch()))).Status);
        Assert.Equal(4L, await Count(Company, Cc.Table));                                                                               // nothing else was written
    }

    [PostgresFact]
    public async Task Details_change_names_branch_and_group_with_revision_control_and_the_id_stays_the_same()
    {
        await PrepareCenters();
        var g = await New("G", group: true);
        var c = await New("C", g.Id);
        var u = Ok(await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, 1, new CostCenterDetailsInput("اسم", "Name", await MainBranch(), null)));
        Assert.Equal((c.Id, "C", 2L, "Name"), (u.Id, u.Code, u.Revision, u.NameEn));
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, 1, new CostCenterDetailsInput("x", "y", null, null))).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, null, new CostCenterDetailsInput("x", "y", null, null))).Status);
        Assert.Equal(2L, Ok(await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, 2, new CostCenterDetailsInput("اسم", "Name", u.BranchId, null))).Revision);   // unchanged: no new revision
        Assert.Equal("has_children", (await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, g.Id, 1, new CostCenterDetailsInput("g", "g", null, false))).Code);
        var asGroup = Ok(await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, 2, new CostCenterDetailsInput("اسم", "Name", u.BranchId, true)));
        Assert.True(asGroup.IsGroup && !asGroup.CanReceiveAllocations);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, 3, new CostCenterDetailsInput("اسم", "Name", await InactiveBranch(), true))).Status);
        Assert.Equal(ConfigStatus.NotFound, (await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, new CostCenterDetailsInput("x", "y", null, null))).Status);
    }

    [PostgresFact]
    public async Task Recoding_keeps_the_identity_refuses_duplicates_and_keeps_the_old_code_in_history()
    {
        await PrepareCenters();
        var a = await New("A");
        var b = await New("B");
        var r = Ok(await Centers.RecodeAsync(Admin.Id, Company.Id, a.Id, 1, new CostCenterCodeInput("a-new")));
        Assert.Equal((a.Id, "A-NEW", 2L), (r.Id, r.Code, r.Revision));
        Assert.Equal("code_in_use", (await Centers.RecodeAsync(Admin.Id, Company.Id, b.Id, 1, new CostCenterCodeInput("A-NEW"))).Code);
        Assert.Equal(ConfigStatus.Invalid, (await Centers.RecodeAsync(Admin.Id, Company.Id, b.Id, 1, new CostCenterCodeInput(" "))).Status);
        Assert.Equal(2L, Ok(await Centers.RecodeAsync(Admin.Id, Company.Id, a.Id, 2, new CostCenterCodeInput("A-NEW"))).Revision);
        var history = await Sql<string>($"SELECT {Lh.Changes}::text FROM {Lh.Table} WHERE {Lh.EntityKey} = '{a.Id}' AND {Lh.Revision} = 2");
        Assert.Contains("\"old\": \"A\"", history);                                                                                       // the earlier code stays reproducible
        Assert.Contains("\"new\": \"A-NEW\"", history);
    }

    [PostgresFact]
    public async Task Moving_restructures_whole_subtrees_and_refuses_cycles_leaf_parents_and_inactive_parents()
    {
        await PrepareCenters();
        var a = await New("A", group: true);
        var b = await New("B", a.Id, group: true);
        var c = await New("C", b.Id, group: true);
        var leaf = await New("L", c.Id);
        var other = await New("O", group: true);
        Assert.Equal("cost_center_cycle", (await Centers.MoveAsync(Admin.Id, Company.Id, a.Id, 1, new CostCenterMoveInput(c.Id))).Code);       // under its own descendant
        Assert.Equal("cost_center_cycle", (await Centers.MoveAsync(Admin.Id, Company.Id, a.Id, 1, new CostCenterMoveInput(a.Id))).Code);
        Assert.Equal("parent_not_group", (await Centers.MoveAsync(Admin.Id, Company.Id, c.Id, 1, new CostCenterMoveInput(leaf.Id))).Code);
        var moved = Ok(await Centers.MoveAsync(Admin.Id, Company.Id, b.Id, 1, new CostCenterMoveInput(other.Id)));                           // the whole subtree moves with it
        Assert.Equal(other.Id, moved.ParentId);
        Assert.Equal(c.Id, (await Centers.GetAsync(Company.Id, leaf.Id))!.ParentId);
        Assert.Null(Ok(await Centers.MoveAsync(Admin.Id, Company.Id, b.Id, 2, new CostCenterMoveInput(null))).ParentId);                      // back to a root
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Centers.MoveAsync(Admin.Id, Company.Id, b.Id, 1, new CostCenterMoveInput(a.Id))).Status);
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, other.Id, 1));
        Assert.Equal("parent_inactive", (await Centers.MoveAsync(Admin.Id, Company.Id, b.Id, 3, new CostCenterMoveInput(other.Id))).Code);
    }

    [PostgresFact]
    public async Task Deactivation_keeps_history_stops_new_allocations_and_respects_the_tree()
    {
        await PrepareCenters();
        var g = await New("G", group: true);
        var l = await New("L", g.Id);
        Assert.Equal("has_active_children", (await Centers.DeactivateAsync(Admin.Id, Company.Id, g.Id, 1)).Code);
        var off = Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, l.Id, 1));
        Assert.True(!off.IsActive && !off.CanReceiveAllocations);                                                                       // inactive: cannot receive new allocations
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, g.Id, 1));
        Assert.Equal("parent_inactive", (await Centers.ReactivateAsync(Admin.Id, Company.Id, l.Id, 2)).Code);
        Ok(await Centers.ReactivateAsync(Admin.Id, Company.Id, g.Id, 2));
        Assert.True(Ok(await Centers.ReactivateAsync(Admin.Id, Company.Id, l.Id, 2)).CanReceiveAllocations);
        Assert.Equal(2L, await Count(Company, Cc.Table));                                                                               // nothing is ever deleted
        Assert.Equal(1L, Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, l.Id, 3)).Revision - 3);
        Assert.Equal(4L, (await Centers.GetAsync(Company.Id, l.Id))!.Revision);
    }

    [PostgresFact]
    public async Task Only_active_leaves_can_receive_allocations_so_rolling_up_the_tree_never_double_counts()
    {
        await PrepareCenters();
        var root = await New("R", group: true);
        var mid = await New("M", root.Id, group: true);
        var l1 = await New("L1", mid.Id);
        var l2 = await New("L2", mid.Id);
        var off = await New("L3", root.Id);
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, off.Id, 1));
        var all = await Centers.ListAsync(Company.Id);
        Assert.Equal(["L1", "L2"], all.Where(c => c.CanReceiveAllocations).Select(c => c.Code).Order());
        Assert.All(all.Where(c => c.IsGroup), c => Assert.False(c.CanReceiveAllocations));
        // Every allocatable leaf has exactly one ancestor chain, so a roll-up sums each leaf once.
        var chains = await Sql<long>($"WITH RECURSIVE up(leaf, id, parent) AS (SELECT {Cc.Id}, {Cc.Id}, {Cc.ParentId} FROM {Cc.Table} WHERE NOT {Cc.IsGroup} UNION ALL SELECT up.leaf, a.{Cc.Id}, a.{Cc.ParentId} FROM {Cc.Table} a JOIN up ON a.{Cc.Id} = up.parent) SELECT count(*) FROM up WHERE id = '{root.Id}'");
        Assert.Equal(3L, chains);                                                                                                       // L1, L2 and the inactive L3 each reach the root once
        Assert.NotEqual(l1.Id, l2.Id);
    }

    [PostgresFact]
    public async Task Concurrent_changes_never_duplicate_a_code_or_lose_an_update()
    {
        await PrepareCenters();
        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Centers.CreateAsync(Admin.Id, Company.Id, In("SAME"))));
        Assert.Single(created, r => r.IsOk);
        Assert.Equal(5, created.Count(r => r.Code == "code_in_use"));
        var c = created.Single(r => r.IsOk).Value!;
        var updates = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Centers.UpdateDetailsAsync(Admin.Id, Company.Id, c.Id, 1, new CostCenterDetailsInput($"n{i}", $"n{i}", null, null))));
        Assert.Single(updates, r => r.IsOk);
        Assert.Equal(5, updates.Count(r => r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(2L, (await Centers.GetAsync(Company.Id, c.Id))!.Revision);
        Assert.Equal(1L, await Count(Company, Cc.Table));
    }
}

public class CostCenterConstraintTests : CostCenterTestBase
{
    [PostgresFact]
    public async Task PostgreSQL_refuses_deletion_identity_change_and_revision_jumps()
    {
        await PrepareCenters();
        await New("A");
        Assert.Equal("P0001", await RawFails($"DELETE FROM {Cc.Table}"));
        Assert.Equal("0A000", await RawFails($"TRUNCATE {Cc.Table}"));   // referenced by journal allocations: PostgreSQL itself refuses
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.Id} = gen_random_uuid(), {Cc.Revision} = {Cc.Revision} + 1"));
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.CreatedAtUtc} = now() + interval '1 day', {Cc.Revision} = {Cc.Revision} + 1"));
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.NameEn} = 'z'"));                                              // revision must advance
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.NameEn} = 'z', {Cc.Revision} = {Cc.Revision} + 2"));
        Assert.Null(await RawFails($"UPDATE {Cc.Table} SET {Cc.NameEn} = 'z', {Cc.Revision} = {Cc.Revision} + 1"));
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_codes_names_branches_and_self_parenting()
    {
        await PrepareCenters();
        var a = await New("A", group: true);
        Assert.Equal("23505", await RawFails(Raw("A")));                                                                                // case-insensitive unique code
        Assert.Equal("23514", await RawFails(Raw("bad code")));
        Assert.Equal("23514", await RawFails(Raw("lower-ok-not", name: "NULL").Replace("lower-ok-not", "OK1")));                         // a name is required
        Assert.Equal("23514", await RawFails(Raw("N1", name: "'   '")));
        Assert.Equal("23503", await RawFails(Raw("N2", branch: $"'{Guid.NewGuid()}'")));                                                // foreign key to a real branch
        Assert.Equal("23514", await RawFails($"UPDATE {Cc.Table} SET {Cc.ParentId} = {Cc.Id}, {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Id} = '{a.Id}'"));
        Assert.Null(await RawFails(Raw("OK2", a.Id)));
    }

    [PostgresFact]
    public async Task PostgreSQL_judges_the_whole_tree_at_commit_parents_cycles_groups_and_activity()
    {
        await PrepareCenters();
        var g = await New("G", group: true);
        var l = await New("L", g.Id);
        var off = await New("OFF", group: true);
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, off.Id, 1));
        Assert.Equal("P0001", await RawFails(Raw("X1", l.Id)));                                                                         // parent must be a group
        Assert.Equal("P0001", await RawFails(Raw("X2", off.Id)));                                                                       // active child under inactive parent
        Assert.Null(await RawFails(Raw("X3", off.Id, active: false)));                                                                  // inactive child under inactive parent is fine
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.IsGroup} = false, {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Id} = '{g.Id}'"));   // a node with children must stay a group
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.IsActive} = false, {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Id} = '{g.Id}'")); // active child
        // A cycle created behind the service's back is still refused (judged at commit for the changed node and its children).
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.ParentId} = '{l.Id}', {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Id} = '{g.Id}'"));
        var h = await New("H", g.Id, group: true);
        Assert.Equal("P0001", await RawFails($"UPDATE {Cc.Table} SET {Cc.ParentId} = '{h.Id}', {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Id} = '{g.Id}'"));
        // A restructuring that is only valid as a whole commits (two statements, one transaction).
        Assert.Null(await RawFails($"UPDATE {Cc.Table} SET {Cc.ParentId} = '{off.Id}', {Cc.IsActive} = false, {Cc.Revision} = {Cc.Revision} + 1 WHERE {Cc.Id} = '{h.Id}'"));
    }
}

public class CostCenterAccessTests : CostCenterTestBase
{
    private const string Base = "/api/v1/company/cost-centers";

    [PostgresFact]
    public async Task Every_cost_center_operation_is_a_separate_company_wide_permission_held_only_by_the_administrator_preset()
    {
        await PrepareCenters();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        foreach (var c in new[] { "view", "create", "update", "activate", "recode", "restructure" }) Assert.Contains($"company.costcenters.{c}", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.costcenters"));
        var g = await New("G", group: true);
        var l = await New("L", g.Id);
        var o = await New("O", group: true);
        var (viewer, _) = await Holder(PermissionCatalog.Codes.CostCentersView);
        var (creator, _) = await Holder(PermissionCatalog.Codes.CostCentersCreate);
        var (updater, _) = await Holder(PermissionCatalog.Codes.CostCentersUpdate);
        var (activator, _) = await Holder(PermissionCatalog.Codes.CostCentersActivate);
        var (recoder, _) = await Holder(PermissionCatalog.Codes.CostCentersRecode);
        var (mover, _) = await Holder(PermissionCatalog.Codes.CostCentersRestructure);
        Assert.Equal(3, Ok(await CMg.ListAsync(viewer.Id, Company.Id)).Count);
        Assert.Equal(l.Id, Ok(await CMg.GetAsync(viewer.Id, Company.Id, l.Id)).Id);
        foreach (var u in new[] { creator, updater, activator, recoder, mover })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await CMg.ListAsync(u.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await CMg.GetAsync(u.Id, Company.Id, l.Id)).Status);
        }
        var details = new CostCenterDetailsInput("اسم", "Name", null, null);
        Assert.Equal(ConfigStatus.Forbidden, (await CMg.CreateAsync(viewer.Id, Company.Id, In("Z"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMg.UpdateAsync(creator.Id, Company.Id, l.Id, 1, details)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMg.RecodeAsync(updater.Id, Company.Id, l.Id, 1, new CostCenterCodeInput("Q"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMg.MoveAsync(recoder.Id, Company.Id, l.Id, 1, new CostCenterMoveInput(o.Id))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMg.DeactivateAsync(mover.Id, Company.Id, l.Id, 1)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await CMg.ReactivateAsync(mover.Id, Company.Id, l.Id, 1)).Status);
        Assert.Equal("Z", Ok(await CMg.CreateAsync(creator.Id, Company.Id, In("Z"))).Code);
        Assert.Equal("Name", Ok(await CMg.UpdateAsync(updater.Id, Company.Id, l.Id, 1, details)).NameEn);
        Assert.Equal("Q", Ok(await CMg.RecodeAsync(recoder.Id, Company.Id, l.Id, 2, new CostCenterCodeInput("Q"))).Code);
        Assert.Equal(o.Id, Ok(await CMg.MoveAsync(mover.Id, Company.Id, l.Id, 3, new CostCenterMoveInput(o.Id))).ParentId);
        Assert.False(Ok(await CMg.DeactivateAsync(activator.Id, Company.Id, l.Id, 4)).IsActive);
    }

    [PostgresFact]
    public async Task The_endpoints_need_authentication_and_work_end_to_end_with_etags_and_capabilities()
    {
        await StartApi();
        BuildCostCenterFacade();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, Base, new { })).Status);
        var cookie = await SignIn(Admin);
        var empty = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Contains("no-store", empty.Headers.CacheControl!.ToString());
        Assert.Equal(0, empty.Json.GetProperty("costCenters").GetArrayLength());
        foreach (var c in new[] { "create", "update", "activate", "recode", "restructure" }) Assert.True(empty.Json.GetProperty("can").GetProperty(c).GetBoolean(), c);

        var root = await Send(HttpMethod.Post, Base, new { code = "ops", nameAr = "التشغيل", nameEn = "Operations", isGroup = true }, cookie);
        Assert.Equal(HttpStatusCode.Created, root.Status);
        Assert.Equal("\"1\"", Etag(root));
        Assert.Equal("OPS", root.Json.GetProperty("code").GetString());
        var rootId = root.Json.GetProperty("id").GetGuid();
        var child = await Send(HttpMethod.Post, Base, new { code = "OPS-1", nameEn = "Ops one", parentId = rootId }, cookie);
        Assert.Equal(HttpStatusCode.Created, child.Status);
        Assert.True(child.Json.GetProperty("canReceiveAllocations").GetBoolean());
        var childId = child.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, Base, new { code = "ops-1", nameEn = "dup" }, cookie)).Status);
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/{childId}", new { nameEn = "Renamed" }, cookie)).Status);
        var put = await Send(HttpMethod.Put, $"{Base}/{childId}", new { nameEn = "Renamed" }, cookie, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("\"2\"", Etag(put));
        Assert.Equal((HttpStatusCode)412, (await Send(HttpMethod.Put, $"{Base}/{childId}", new { nameEn = "Again" }, cookie, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"{Base}/{rootId}/move", new { parentId = childId }, cookie, ifMatch: "\"1\"")).Status);     // cycle / leaf parent
        Assert.Equal("NEW", (await Send(HttpMethod.Post, $"{Base}/{childId}/recode", new { code = "new" }, cookie, ifMatch: "\"2\"")).Json.GetProperty("code").GetString());
        Assert.False((await Send(HttpMethod.Post, $"{Base}/{childId}/deactivate", null, cookie, ifMatch: "\"3\"")).Json.GetProperty("isActive").GetBoolean());
        Assert.True((await Send(HttpMethod.Post, $"{Base}/{childId}/activate", null, cookie, ifMatch: "\"4\"")).Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie)).Status);
        Assert.Equal(2, (await Send(HttpMethod.Get, Base, cookie: cookie)).Json.GetProperty("costCenters").GetArrayLength());
    }
}
