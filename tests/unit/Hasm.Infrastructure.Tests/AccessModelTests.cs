using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

public class AccessModelTests
{
    // Test catalog: the production catalog only has company-level administrative resources, so branch/warehouse
    // behavior is exercised with extra resources that exist only in tests.
    private static readonly PermissionCatalog Catalog = new(
    [
        .. PermissionCatalog.Default.Resources,
        new PermissionResource("docs.sample", ScopeKind.Branch, [PermissionAction.View, PermissionAction.Create, PermissionAction.Approve, PermissionAction.Post]),
        new PermissionResource("stock.sample", ScopeKind.Warehouse, [PermissionAction.View, PermissionAction.Update]),
    ]);

    private static readonly Guid BranchA = Guid.NewGuid(), BranchB = Guid.NewGuid(), WarehouseX = Guid.NewGuid(), WarehouseY = Guid.NewGuid();

    private static GrantedRole Grant(ScopeKind kind, Guid? scopeId, params string[] permissions) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, scopeId, permissions.ToHashSet());

    private static EffectivePermissions Eff(params GrantedRole[] grants) => new(Catalog, grants);

    [Fact]
    public void Catalog_codes_are_unique_valid_and_match_the_database_check()
    {
        var pattern = new Regex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*){1,3}$");
        Assert.All(PermissionCatalog.Default.All, p => Assert.Matches(pattern, p.Code));
        Assert.Equal(PermissionCatalog.Default.All.Count, PermissionCatalog.Default.All.Select(p => p.Code).Distinct().Count());
        Assert.Throws<ArgumentException>(() => new PermissionCatalog([new("a.b", ScopeKind.Company, [PermissionAction.View]), new("a.b", ScopeKind.Company, [PermissionAction.View])]));
        Assert.Throws<ArgumentException>(() => new PermissionCatalog([new("Bad Key", ScopeKind.Company, [PermissionAction.View])]));
    }

    [Fact]
    public void Preset_roles_only_reference_catalog_permissions_and_administrator_holds_everything_except_the_approval_of_fiscal_reopenings()
    {
        foreach (var (_, (_, permissions)) in PresetRoles.Definitions)
            Assert.All(permissions, code => Assert.True(PermissionCatalog.Default.TryGet(code, out _), code));
        // Segregation of duties: the APPROVAL of exceptional fiscal reopenings, of journal entries and of commercial documents is in the catalog but never in the Company Administrator preset; only Fiscal Approver / Journal Approver hold them.
        string[] approval = [PermissionCatalog.Codes.FiscalReopenApprove, PermissionCatalog.Codes.JournalApprove, PermissionCatalog.Codes.ImportsApprove, PermissionCatalog.Codes.CommercialApprove];
        Assert.Equal(PermissionCatalog.Default.All.Select(p => p.Code).Except(approval).Order(), PresetRoles.Definitions[PresetRoles.CompanyAdministratorId].Permissions.Order());
        Assert.Equal(PresetRoles.FiscalApproverId, Assert.Single(PresetRoles.Definitions, d => d.Value.Permissions.Contains(PermissionCatalog.Codes.FiscalReopenApprove)).Key);
        Assert.Equal(PresetRoles.JournalApproverId, Assert.Single(PresetRoles.Definitions, d => d.Value.Permissions.Contains(PermissionCatalog.Codes.JournalApprove)).Key);
    }

    [Fact]
    public void Migration_seed_equals_the_code_defined_preset_roles()
    {
        // The preset roles are seeded by migration 0004 and extended by the versioned migrations 0006 (company administration), 0007 (branches) 0008 (warehouses) and 0009 (fiscal calendar), 0010 (chart) and 0011 (general ledger foundation).
        var sql = string.Join('\n', MigrationScript.LoadEmbedded().Where(s => s.Version is 4 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29).Select(s => s.Sql));
        var seeded = Regex.Matches(sql, @"^\s*\('(?<id>5c1b0f6e-[0-9a-f-]+)', '(?<perm>[a-z.]+)'\)", RegexOptions.Multiline)
            .GroupBy(m => Guid.Parse(m.Groups["id"].Value))
            .ToDictionary(g => g.Key, g => g.Select(m => m.Groups["perm"].Value).Order().ToArray());
        Assert.Equal(PresetRoles.Definitions.Keys.Order(), seeded.Keys.Order());
        foreach (var (id, (_, permissions)) in PresetRoles.Definitions)
            Assert.Equal(permissions.Order(), seeded[id]);
    }

    [Fact]
    public void No_grants_means_everything_is_denied()
    {
        var eff = Eff();
        foreach (var p in Catalog.All) Assert.False(eff.Has(p.Code, ScopeTarget.Company), p.Code);
        Assert.False(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchA)));
    }

    [Fact]
    public void Unknown_permission_codes_are_denied_even_when_a_role_lists_them()
    {
        var eff = Eff(Grant(ScopeKind.Company, null, "made.up.permission"));
        Assert.False(eff.Has("made.up.permission", ScopeTarget.Company));
        Assert.False(eff.Has("", ScopeTarget.Company));
        Assert.False(eff.Has(null!, ScopeTarget.Company));
    }

    [Fact]
    public void Company_wide_grant_covers_every_branch_and_warehouse()
    {
        var eff = Eff(Grant(ScopeKind.Company, null, "docs.sample.view", "stock.sample.view"));
        Assert.True(eff.Has("docs.sample.view", ScopeTarget.Company));
        Assert.True(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchA)));
        Assert.True(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchB)));
        Assert.True(eff.Has("stock.sample.view", ScopeTarget.Warehouse(WarehouseX)));
    }

    [Fact]
    public void Branch_grant_covers_only_its_own_branch()
    {
        var eff = Eff(Grant(ScopeKind.Branch, BranchA, "docs.sample.view", "docs.sample.approve"));
        Assert.True(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchA)));
        Assert.True(eff.Has("docs.sample.approve", ScopeTarget.Branch(BranchA)));
        Assert.False(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchB)));
        Assert.False(eff.Has("docs.sample.post", ScopeTarget.Branch(BranchA)));       // not in the role
        Assert.False(eff.Has("docs.sample.view", ScopeTarget.Company));               // "everywhere" needs a company-wide grant
        Assert.False(eff.Has("stock.sample.view", ScopeTarget.Warehouse(WarehouseX))); // branch grants do not cross into warehouses
    }

    [Fact]
    public void Warehouse_grant_covers_only_its_own_warehouse()
    {
        var eff = Eff(Grant(ScopeKind.Warehouse, WarehouseX, "stock.sample.view"));
        Assert.True(eff.Has("stock.sample.view", ScopeTarget.Warehouse(WarehouseX)));
        Assert.False(eff.Has("stock.sample.view", ScopeTarget.Warehouse(WarehouseY)));
        Assert.False(eff.Has("stock.sample.view", ScopeTarget.Branch(BranchA)));
    }

    [Fact]
    public void Company_level_resources_are_never_exercisable_through_a_narrower_assignment()
    {
        var eff = Eff(Grant(ScopeKind.Branch, BranchA, PermissionCatalog.Codes.RolesUpdate, "docs.sample.view"));
        Assert.False(eff.Has(PermissionCatalog.Codes.RolesUpdate, ScopeTarget.Company));
        Assert.False(eff.Has(PermissionCatalog.Codes.RolesUpdate, ScopeTarget.Branch(BranchA)));
        Assert.True(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchA)));
    }

    [Fact]
    public void Wrong_target_kind_or_malformed_target_is_denied()
    {
        var eff = Eff(Grant(ScopeKind.Company, null, "docs.sample.view", PermissionCatalog.Codes.MembersView));
        Assert.False(eff.Has("docs.sample.view", ScopeTarget.Warehouse(WarehouseX)));            // branch-level resource, warehouse target
        Assert.False(eff.Has(PermissionCatalog.Codes.MembersView, ScopeTarget.Branch(BranchA))); // company-level resource, branch target
        Assert.False(eff.Has("docs.sample.view", new ScopeTarget(ScopeKind.Branch, null)));
        Assert.False(eff.Has("docs.sample.view", new ScopeTarget(ScopeKind.Branch, Guid.Empty)));
        Assert.False(eff.Has("docs.sample.view", new ScopeTarget(ScopeKind.Company, BranchA)));
        Assert.False(eff.Has("docs.sample.view", new ScopeTarget((ScopeKind)99, null)));
    }

    [Fact]
    public void Multiple_roles_combine_additively_but_each_keeps_its_own_scope()
    {
        var eff = Eff(
            Grant(ScopeKind.Branch, BranchA, "docs.sample.view"),
            Grant(ScopeKind.Branch, BranchB, "docs.sample.create"),
            Grant(ScopeKind.Warehouse, WarehouseX, "stock.sample.update"));
        Assert.True(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchA)));
        Assert.False(eff.Has("docs.sample.create", ScopeTarget.Branch(BranchA)));
        Assert.True(eff.Has("docs.sample.create", ScopeTarget.Branch(BranchB)));
        Assert.False(eff.Has("docs.sample.view", ScopeTarget.Branch(BranchB)));
        Assert.True(eff.Has("stock.sample.update", ScopeTarget.Warehouse(WarehouseX)));
    }

    [Fact]
    public void CanGrant_requires_possession_at_the_scope_being_granted()
    {
        var branchManager = Eff(Grant(ScopeKind.Branch, BranchA, "docs.sample.view", "docs.sample.approve"));
        Assert.True(branchManager.CanGrant(["docs.sample.view"], ScopeTarget.Branch(BranchA)));
        Assert.False(branchManager.CanGrant(["docs.sample.view"], ScopeTarget.Branch(BranchB)));
        Assert.False(branchManager.CanGrant(["docs.sample.view"], ScopeTarget.Company));
        Assert.False(branchManager.CanGrant(["docs.sample.post"], ScopeTarget.Branch(BranchA)));
        Assert.False(branchManager.CanGrant(["made.up.permission"], ScopeTarget.Branch(BranchA)));

        var company = Eff(Grant(ScopeKind.Company, null, "docs.sample.view"));
        Assert.True(company.CanGrant(["docs.sample.view"], ScopeTarget.Branch(BranchB)));
        Assert.True(company.CanGrant(["docs.sample.view"], ScopeTarget.Company));
        Assert.False(company.CanGrant(["docs.sample.view", "docs.sample.create"], ScopeTarget.Company));
        Assert.False(company.CanGrant(["made.up.permission"], ScopeTarget.Company));
    }

    [Fact]
    public void CanGrant_treats_coarser_permissions_as_inert_under_a_narrow_scope_but_not_company_wide()
    {
        var branchManager = Eff(Grant(ScopeKind.Branch, BranchA, "docs.sample.view"));
        // roles.update is company-level: inert inside a branch assignment, so it escalates nothing there...
        Assert.True(branchManager.CanGrant(["docs.sample.view", PermissionCatalog.Codes.RolesUpdate], ScopeTarget.Branch(BranchA)));
        // ...but granting it company-wide would be real power that the actor lacks.
        Assert.False(branchManager.CanGrant([PermissionCatalog.Codes.RolesUpdate], ScopeTarget.Company));
    }

    [Fact]
    public void Scope_target_well_formedness()
    {
        Assert.True(ScopeTarget.Company.IsWellFormed);
        Assert.True(ScopeTarget.Branch(BranchA).IsWellFormed);
        Assert.False(new ScopeTarget(ScopeKind.Warehouse, null).IsWellFormed);
        Assert.False(new ScopeTarget(ScopeKind.Company, BranchA).IsWellFormed);
    }
}

public class ScopeSafetyTests
{
    private static readonly PermissionCatalog Cat = new([new PermissionResource("docs.sample", ScopeKind.Branch, [PermissionAction.View]), new PermissionResource("stock.sample", ScopeKind.Warehouse, [PermissionAction.View])]);

    [Fact]
    public void The_same_id_value_used_as_branch_and_warehouse_never_crosses_kinds()
    {
        var id = Guid.NewGuid();
        var branchOnly = new EffectivePermissions(Cat, [new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), ScopeKind.Branch, id, new HashSet<string> { "docs.sample.view", "stock.sample.view" })]);
        Assert.True(branchOnly.Has("docs.sample.view", ScopeTarget.Branch(id)));
        Assert.False(branchOnly.Has("stock.sample.view", ScopeTarget.Warehouse(id)));
        Assert.False(branchOnly.Has("docs.sample.view", ScopeTarget.Warehouse(id)));
        Assert.False(branchOnly.Has("docs.sample.view", ScopeTarget.Company));
    }

    [Fact]
    public void The_default_scope_validator_refuses_everything()
        => Assert.False(DenyAllScopeTargetValidator.Instance.ExistsAsync(Guid.NewGuid(), ScopeKind.Branch, Guid.NewGuid(), default).Result);
}
