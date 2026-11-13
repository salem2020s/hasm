using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.1C: warehouse validation, entitlement, event types, permission model and the static shape of the migrations (no database needed).</summary>
public class WarehouseUnitTests
{
    private static readonly Guid BranchId = Guid.NewGuid();

    private static WarehouseInput Input(string? code = "WH1", Guid? branch = null, string? ar = "مستودع", string? en = "Warehouse") =>
        new(code, branch ?? BranchId, ar, en, null, null, null, null, null, null, null, null);

    private static string CompanySql => MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 5).Sql;
    private static string CompanyCode => Regex.Replace(CompanySql, "--[^\n]*", "");
    private static string SystemCode => Regex.Replace(MigrationScript.LoadEmbedded().Single(s => s.Version == 8).Sql, "--[^\n]*", "");

    // ---- validation ----

    [Fact]
    public void A_valid_warehouse_is_normalised_and_keeps_its_branch()
    {
        Assert.True(ConfigurationValidation.TryNormalizeWarehouse(Input("  wh-1 "), null, null, out var d, out var branch, out var errors), string.Join(',', errors));
        Assert.Equal("WH-1", d.Code);
        Assert.Equal(BranchId, branch);
    }

    [Fact]
    public void The_branch_is_required_on_creation()
    {
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(new WarehouseInput("WH1", null, "م", "W", null, null, null, null, null, null, null, null), null, null, out _, out _, out var e1));
        Assert.Contains(e1, e => e.Field == "branchId");
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(Input(branch: Guid.Empty), null, null, out _, out _, out var e2));
        Assert.Contains(e2, e => e.Field == "branchId");
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(null, null, null, out _, out _, out var e3));
        Assert.Contains(e3, e => e.Field == "body");
    }

    [Fact]
    public void On_update_the_branch_and_the_code_are_immutable_but_may_be_repeated()
    {
        Assert.True(ConfigurationValidation.TryNormalizeWarehouse(Input("wh1"), "WH1", BranchId, out _, out var same, out _));
        Assert.Equal(BranchId, same);
        Assert.True(ConfigurationValidation.TryNormalizeWarehouse(new WarehouseInput(null, null, "م", "W", null, null, null, null, null, null, null, null), "WH1", BranchId, out _, out _, out _));
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(Input("OTHER"), "WH1", BranchId, out _, out _, out var codeErrors));
        Assert.Contains(codeErrors, e => e is { Field: "code", Code: "immutable" });
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(Input("WH1", Guid.NewGuid()), "WH1", BranchId, out _, out _, out var branchErrors));
        Assert.Contains(branchErrors, e => e is { Field: "branchId", Code: "immutable" });
    }

    [Theory]
    [InlineData("A B")] [InlineData("-A")] [InlineData("")] [InlineData(null)] [InlineData("123456789012345678901")]
    public void Invalid_codes_are_rejected(string? code)
    {
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(Input(code), null, null, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Field == "code");
    }

    [Fact]
    public void At_least_one_name_is_required_and_either_language_alone_is_valid()
    {
        Assert.False(ConfigurationValidation.TryNormalizeWarehouse(Input(ar: null, en: null), null, null, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Code == "name_required");
        Assert.True(ConfigurationValidation.TryNormalizeWarehouse(Input(ar: "مستودع", en: null), null, null, out _, out _, out _));
        Assert.True(ConfigurationValidation.TryNormalizeWarehouse(Input(ar: null, en: "Warehouse"), null, null, out _, out _, out _));
    }

    // ---- entitlement, events ----

    [Fact]
    public void Trial_allows_one_warehouse_it_is_enforced_and_the_events_are_in_the_business_range()
    {
        Assert.Equal(1, TrialEntitlementPolicy.Instance.MaxWarehouses);
        Assert.Equal(Enforcement.Enforced, TrialEntitlementPolicy.Instance.Restrictions.Single(r => r.Key == "warehouses").Enforcement);
        short[] events = [ConfigAuditEvents.WarehouseCreated, ConfigAuditEvents.WarehouseUpdated, ConfigAuditEvents.WarehouseDeactivated, ConfigAuditEvents.WarehouseReactivated];
        Assert.Equal(events.Length, events.Distinct().Count());
        Assert.All(events, e => Assert.InRange(e, (short)1013, (short)1016));
    }

    // ---- permission model ----

    [Fact]
    public void Warehouse_view_and_update_are_warehouse_level_and_structure_changes_are_company_wide_only()
    {
        foreach (var code in new[] { PermissionCatalog.Codes.WarehousesView, PermissionCatalog.Codes.WarehousesUpdate })
            Assert.Equal(ScopeKind.Warehouse, PermissionCatalog.Default.Get(code).Resource.Scope);
        foreach (var code in new[] { PermissionCatalog.Codes.WarehouseCreate, PermissionCatalog.Codes.WarehouseActivate })
            Assert.Equal(ScopeKind.Company, PermissionCatalog.Default.Get(code).Resource.Scope);
    }

    private static EffectivePermissions Eff(params GrantedRole[] grants) => new(PermissionCatalog.Default, grants);
    private static GrantedRole Grant(ScopeKind kind, Guid? id, params string[] codes) => new(Guid.NewGuid(), Guid.NewGuid(), kind, id, codes.ToHashSet());

    [Fact]
    public void A_warehouse_grant_covers_only_its_own_warehouse_and_branch_scope_never_reaches_warehouses()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var view = PermissionCatalog.Codes.WarehousesView;
        var wh = Eff(Grant(ScopeKind.Warehouse, a, view, PermissionCatalog.Codes.WarehouseCreate));
        Assert.True(wh.Has(view, ScopeTarget.Warehouse(a)));
        Assert.False(wh.Has(view, ScopeTarget.Warehouse(b)));
        Assert.False(wh.Has(view, ScopeTarget.Company));
        Assert.False(wh.Has(view, ScopeTarget.Branch(a)));                                              // the same id as a BRANCH is a different target
        Assert.False(wh.Has(PermissionCatalog.Codes.WarehouseCreate, ScopeTarget.Company));             // company-level permissions are inert under a narrower grant
        var branch = Eff(Grant(ScopeKind.Branch, a, view));
        Assert.False(branch.Has(view, ScopeTarget.Warehouse(a)));
        Assert.True(Eff(Grant(ScopeKind.Company, null, view)).Has(view, ScopeTarget.Warehouse(b)));
    }

    // ---- migrations and schema ----

    [Fact]
    public void Company_migration_0005_creates_exactly_the_warehouse_tables_seeds_nothing_and_grants_nothing()
    {
        var created = Regex.Matches(CompanyCode, @"CREATE\s+TABLE\s+(\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(new[] { CompanySchema.Warehouse.Table, CompanySchema.WarehouseRevision.Table }.Order(), created);
        Assert.DoesNotMatch(@"\bINSERT\s+INTO\b", CompanyCode);                                          // no data is created or touched
        Assert.DoesNotMatch(@"bDROPb", CompanyCode);
        Assert.DoesNotMatch(@"\bGRANT\b", CompanyCode);
        Assert.Contains($"FOREIGN KEY ({CompanySchema.Warehouse.BranchId}) REFERENCES {CompanySchema.Branch.Table}", CompanyCode);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", CompanyCode);
    }

    [Fact]
    public void All_warehouse_physical_names_are_opaque_unique_and_present_in_the_migration()
    {
        var names = typeof(CompanySchema.Warehouse).GetFields().Concat(typeof(CompanySchema.WarehouseRevision).GetFields())
            .Select(f => (string)f.GetRawConstantValue()!).ToArray();
        // Table/column/constraint names are unique within their kind; the two revision/guard names never collide with branch names.
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[tcfrk]_[0-9a-f]{8}$", n));
        Assert.All(names, n => Assert.Contains(n, CompanyCode));
        Assert.Empty(names.Intersect(typeof(CompanySchema.Branch).GetFields().Select(f => (string)f.GetRawConstantValue()!)));
    }

    [Fact]
    public void System_migration_0008_adds_a_separate_warehouse_epoch_never_alters_existing_constraints_and_restores_the_preset_guard()
    {
        Assert.Contains(SystemSchema.RoleAssignment.WarehouseScopeEpoch, SystemCode);
        Assert.DoesNotMatch(@"\bDROP\b", SystemCode);                                                    // the migration rules forbid it; the branch CHECK stays as is
        Assert.DoesNotContain(SystemSchema.RoleAssignment.CheckScopeEpoch, SystemCode);
        Assert.Contains("ENABLE TRIGGER", SystemCode);
        Assert.Contains("refuses to continue", SystemCode);                                              // aborts when warehouse-scoped assignments already exist
        foreach (var code in new[] { "company.warehouses.view", "company.warehouses.update", "company.warehousestructure.create", "company.warehousestructure.activate" })
            Assert.Contains($"'{code}'", SystemCode);
    }

    [Fact]
    public void Runtime_privileges_for_warehouses_never_include_delete_truncate_or_history_updates()
    {
        var wh = CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.Warehouse.Table).Privileges.Order();
        var hist = CompanyRuntimePrivileges.Tables.Single(t => t.Table == CompanySchema.WarehouseRevision.Table).Privileges.Order();
        Assert.Equal(["INSERT", "SELECT", "UPDATE"], wh);
        Assert.Equal(["INSERT", "SELECT"], hist);
    }
}
