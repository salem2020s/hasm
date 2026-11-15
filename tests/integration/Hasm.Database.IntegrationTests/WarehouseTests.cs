using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Wh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using Wr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.WarehouseRevision;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>Test-only edition with configurable branch and warehouse limits (never registered in the API).</summary>
internal sealed class WarehouseLimitPolicy(int maxWarehouses, int maxBranches = 50) : IEntitlementPolicy
{
    public string Edition => "test";
    public int MaxEnabledCurrencies => 1;
    public int MaxBranches => maxBranches;
    public int MaxWarehouses => maxWarehouses;
    public IReadOnlyList<TrialRestriction> Restrictions => [];
}

/// <summary>Warehouses (company migration 0005, Phase 3.1C) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class WarehouseTestBase : BranchTestBase
{
    protected CompanyWarehouseService Warehouses(int max = 50, CompanyRecord? company = null)
    {
        var resolver = SinglePrincipalResolver(company ?? Company);
        return new CompanyWarehouseService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), new WarehouseLimitPolicy(max), Clock);
    }

    protected CompanyWarehouseService TrialWarehouses()
    {
        var resolver = SinglePrincipalResolver(Company);
        return new CompanyWarehouseService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), TrialEntitlementPolicy.Instance, Clock);
    }

    protected static WarehouseInput NewWarehouse(string code, Guid branchId, string? ar = "مستودع", string? en = "Warehouse", string? phone = null, string? city = null) =>
        new(code, branchId, ar, en, null, null, city, null, null, phone, null, null);

    protected async Task<WarehouseView> MakeWarehouse(string code, Guid? branchId = null, int max = 50) =>
        Ok(await Warehouses(max: max).CreateAsync(Admin.Id, Company.Id, NewWarehouse(code, branchId ?? (await Main()).Id)));

    protected static string InsertWh(string code, Guid branchId, bool active = true, string name = "'x'", long epoch = 1) =>
        $"INSERT INTO {Wh.Table} ({Wh.Code}, {Wh.BranchId}, {Wh.NameEn}, {Wh.IsActive}, {Wh.ActivationEpoch}, {Wh.Revision}, {Wh.CreatedAtUtc}, {Wh.UpdatedAtUtc}) " +
        $"VALUES ('{code}', '{branchId}', {name}, {(active ? "true" : "false")}, {epoch}, 1, now(), now())";

    protected static string UpdateWh(string code, string set) =>
        $"UPDATE {Wh.Table} SET {set}, {Wh.Revision} = {Wh.Revision} + 1 WHERE {Wh.Code} = '{code}'";

    protected Task<long> WarehouseRows() => Sql<long>($"SELECT count(*) FROM {Wh.Table}");
}


public class WarehouseMigrationTests : WarehouseTestBase
{
    [PostgresFact]
    public async Task A_freshly_provisioned_company_has_the_warehouse_tables_and_no_warehouse_and_no_fabricated_audit_event()
    {
        await Prepare();
        Assert.Equal(15L, await Count(Company, SystemSchema.MigrationJournalTable));
        Assert.Empty(await Warehouses().ListAsync(Company.Id));
        Assert.Equal(0L, await Count(Company, Wh.Table));
        Assert.Equal(0L, await Count(Company, Wr.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task An_existing_company_at_0004_gets_0005_without_touching_its_data_and_the_migration_is_idempotent()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 4)]);
        await Sql("CREATE TABLE zz_existing (a int); INSERT INTO zz_existing VALUES (7)", legacy);
        var branchRowsBefore = await Count(legacy, Br.Table);
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], outcome.Applied);
        Assert.Equal(7, await Sql<int>("SELECT a FROM zz_existing", legacy));
        Assert.Equal(branchRowsBefore, await Count(legacy, Br.Table));
        Assert.Equal(0L, await Count(legacy, Wh.Table));                          // nothing is seeded
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
    }

    [PostgresFact]
    public async Task A_failing_migration_0005_rolls_back_completely_and_the_company_stays_at_0004()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 4)]);
        var good = all.Single(s => s.Version == 5);
        var broken = MigrationScript.Parse("0005_company_warehouses.sql", good.Sql + "\nDO $$ BEGIN RAISE EXCEPTION 'boom'; END; $$;");
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry, [.. all.Where(s => s.Version <= 4), broken]).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Failed, outcome.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name IN ('{Wh.Table}', '{Wr.Table}')", legacy));
        Assert.Equal(4L, await Count(legacy, SystemSchema.MigrationJournalTable));
        // The branch triggers of 0004 are untouched by the failed attempt (the deferred link trigger was never attached to the branch table).
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM pg_trigger WHERE tgname = '{Wh.LinkBranchTrigger}'", legacy));
    }

    [PostgresFact]
    public async Task A_company_edited_after_migration_0005_is_detected_by_the_checksum_gate()
    {
        await Prepare();
        var resolver = SinglePrincipalResolver(Company);
        (await resolver.OpenAsync(Company.Id)).Dispose();
        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'edited' WHERE version = 5");
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(Company.Id))).Code);
    }
}

public class WarehouseConstraintTests : WarehouseTestBase
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;
    private const string Raised = PostgresErrorCodes.RaiseException;

    [PostgresFact]
    public async Task Codes_are_unique_case_insensitively_upper_case_only_and_immutable()
    {
        await Prepare();
        var main = (await Main()).Id;
        Assert.Null(await Fails(InsertWh("W1", main)));
        Assert.Equal(Unique, await Fails(InsertWh("W1", main)));
        Assert.Equal(CheckViolation, await Fails(InsertWh("w2", main)));
        Assert.Equal(CheckViolation, await Fails(InsertWh("A B", main)));
        Assert.Equal(CheckViolation, await Fails(InsertWh("-A", main)));
        Assert.Equal(Raised, await Fails(UpdateWh("W1", $"{Wh.Code} = 'W9'")));
        Assert.Equal(Raised, await Fails(UpdateWh("W1", $"{Wh.Id} = gen_random_uuid()")));
        Assert.Equal(Raised, await Fails(UpdateWh("W1", $"{Wh.CreatedAtUtc} = now() + interval '1 day'")));
    }

    [PostgresFact]
    public async Task A_warehouse_needs_a_real_branch_and_can_never_change_branch()
    {
        await Prepare();
        var main = (await Main()).Id;
        var other = await Make("OTHER");
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails(InsertWh("W1", Guid.NewGuid())));
        Assert.Null(await Fails(InsertWh("W1", main)));
        Assert.Equal(Raised, await Fails(UpdateWh("W1", $"{Wh.BranchId} = '{other.Id}'")));
        Assert.Equal(main, await Sql<Guid>($"SELECT {Wh.BranchId} FROM {Wh.Table} WHERE {Wh.Code} = 'W1'"));
    }

    [PostgresFact]
    public async Task Names_need_at_least_one_non_empty_value()
    {
        await Prepare();
        var main = (await Main()).Id;
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Wh.Table} ({Wh.Code}, {Wh.BranchId}, {Wh.CreatedAtUtc}, {Wh.UpdatedAtUtc}) VALUES ('N0', '{main}', now(), now())"));
        Assert.Equal(CheckViolation, await Fails(InsertWh("N1", main, name: "'   '")));
        Assert.Null(await Fails($"INSERT INTO {Wh.Table} ({Wh.Code}, {Wh.BranchId}, {Wh.NameAr}, {Wh.CreatedAtUtc}, {Wh.UpdatedAtUtc}) VALUES ('N2', '{main}', 'مستودع', now(), now())"));
        Assert.Equal(CheckViolation, await Fails(UpdateWh("N2", $"{Wh.NameAr} = NULL")));
    }

    [PostgresFact]
    public async Task Warehouses_are_never_deleted_or_truncated_not_even_by_the_owner_and_history_is_append_only()
    {
        await Prepare();
        var w = await MakeWarehouse("D1");
        Assert.Equal(Raised, await Fails($"DELETE FROM {Wh.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Wh.Table} CASCADE"));
        Assert.Equal(1L, await WarehouseRows());
        Assert.Equal(Raised, await Fails($"UPDATE {Wr.Table} SET {Wr.Revision} = 9"));
        Assert.Equal(Raised, await Fails($"DELETE FROM {Wr.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Wr.Table}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails(
            $"INSERT INTO {Wr.Table} ({Wr.WarehouseId}, {Wr.Revision}, {Wr.ActorUserId}, {Wr.ChangedAtUtc}, {Wr.Changes}, {Wr.AuditStream}, {Wr.AuditSeq}) " +
            $"VALUES ('{w.Id}', 7, gen_random_uuid(), now(), '{{}}', 'business:none', 999)"));
    }

    [PostgresFact]
    public async Task The_revision_fence_and_the_activation_epoch_rule_are_enforced_by_the_database()
    {
        await Prepare();
        var main = (await Main()).Id;
        Assert.Null(await Fails(InsertWh("E1", main)));
        Assert.Equal(Raised, await Fails($"UPDATE {Wh.Table} SET {Wh.NameEn} = 'y' WHERE {Wh.Code} = 'E1'"));
        Assert.Equal(Raised, await Fails($"UPDATE {Wh.Table} SET {Wh.NameEn} = 'y', {Wh.Revision} = {Wh.Revision} + 2 WHERE {Wh.Code} = 'E1'"));
        Assert.Equal(Raised, await Fails(UpdateWh("E1", $"{Wh.IsActive} = false")));
        Assert.Equal(Raised, await Fails(UpdateWh("E1", $"{Wh.ActivationEpoch} = 5")));
        Assert.Equal(Raised, await Fails(UpdateWh("E1", $"{Wh.IsActive} = false, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 2")));
        Assert.Null(await Fails(UpdateWh("E1", $"{Wh.IsActive} = false, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 1")));
        Assert.Null(await Fails(UpdateWh("E1", $"{Wh.IsActive} = true, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 1")));
        Assert.Equal(3L, await Sql<long>($"SELECT {Wh.ActivationEpoch} FROM {Wh.Table} WHERE {Wh.Code} = 'E1'"));
    }

    [PostgresFact]
    public async Task An_active_warehouse_never_lives_in_an_inactive_branch_in_either_direction_at_commit()
    {
        await Prepare();
        var b = await Make("BR");
        Assert.Null(await Fails(InsertWh("W1", b.Id)));
        // Deactivating the branch while it has an active warehouse is refused at commit …
        Assert.Equal(Raised, await Fails(Update("BR", $"{Br.IsActive} = false, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1")));
        // … a warehouse cannot be created in an inactive branch, nor reactivated there …
        Assert.Equal(Raised, await InTx(UpdateWh("W1", $"{Wh.IsActive} = false, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 1"),
            Update("BR", $"{Br.IsActive} = false, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1"), InsertWh("W2", b.Id)));
        Assert.Null(await InTx(UpdateWh("W1", $"{Wh.IsActive} = false, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 1"),
            Update("BR", $"{Br.IsActive} = false, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1")));                // both inactive together: fine
        Assert.Equal(Raised, await Fails(InsertWh("W3", b.Id)));
        Assert.Equal(Raised, await Fails(UpdateWh("W1", $"{Wh.IsActive} = true, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 1")));
        // … but reactivating the branch first and then the warehouse works.
        Assert.Null(await InTx(Update("BR", $"{Br.IsActive} = true, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1"),
            UpdateWh("W1", $"{Wh.IsActive} = true, {Wh.ActivationEpoch} = {Wh.ActivationEpoch} + 1")));
    }
}

public class WarehouseServiceTests : WarehouseTestBase
{
    [PostgresFact]
    public async Task Creating_a_warehouse_is_audited_in_the_same_transaction_and_starts_active_in_its_branch()
    {
        await Prepare();
        var main = await Main();
        var w = Ok(await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("wh-1", main.Id, phone: "+966500000000")));
        Assert.Equal("WH-1", w.Data.Code);                                    // normalised
        Assert.Equal(main.Id, w.BranchId);
        Assert.True(w.IsActive);
        Assert.Equal(1, w.ActivationEpoch);
        Assert.Equal(1, w.Revision);
        Assert.Equal(1L, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigAuditEvents.WarehouseCreated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Wr.Table} WHERE {Wr.WarehouseId} = '{w.Id}' AND {Wr.Revision} = 1 AND {Wr.AuditSeq} = (SELECT {Audit_.Seq} FROM {Audit_.Table})"));
        var history = await Sql<string>($"SELECT {Wr.Changes}::text FROM {Wr.Table}");
        Assert.DoesNotContain("+966500000000", history);                      // sensitive values are never copied into history
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        Assert.Equal(w, await Warehouses().GetAsync(Company.Id, w.Id));
        Assert.Single(await Warehouses().ListAsync(Company.Id));
    }

    [PostgresFact]
    public async Task Creation_is_validated_and_the_branch_must_exist_and_be_active()
    {
        await Prepare();
        var main = await Main();
        Assert.Equal(ConfigStatus.Invalid, (await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("A B", main.Id))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("X1", main.Id, ar: null, en: null))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("X1", Guid.Empty))).Status);
        var unknown = await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("X1", Guid.NewGuid()));
        Assert.Equal(ConfigStatus.Invalid, unknown.Status);
        Assert.Contains(unknown.Fields!, e => e.Field == "branchId");
        var b = await Make("BR");
        Ok(await Branches(new BranchLimitPolicy(50)).DeactivateAsync(Admin.Id, Company.Id, b.Id, b.Revision));
        var inactive = await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("X2", b.Id));
        Assert.Equal(ConfigStatus.Conflict, inactive.Status);
        Assert.Equal("branch_inactive", inactive.Code);
        Assert.Equal(0L, await WarehouseRows());
    }

    [PostgresFact]
    public async Task The_trial_limit_counts_every_warehouse_ever_created_and_deactivation_never_frees_a_slot()
    {
        await Prepare();
        var main = await Main();
        var first = Ok(await TrialWarehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("W1", main.Id)));
        var denied = await TrialWarehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("W2", main.Id));
        Assert.Equal(ConfigStatus.EntitlementDenied, denied.Status);
        Ok(await TrialWarehouses().DeactivateAsync(Admin.Id, Company.Id, first.Id, first.Revision));
        Assert.Equal(ConfigStatus.EntitlementDenied, (await TrialWarehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("W2", main.Id))).Status);
        Assert.Equal(1L, await WarehouseRows());
        Assert.Equal(1, TrialWarehouses().MaxWarehouses);
    }

    [PostgresFact]
    public async Task Duplicate_codes_are_refused_case_insensitively()
    {
        await Prepare();
        await MakeWarehouse("DUP");
        var again = await Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse("dup", (await Main()).Id));
        Assert.Equal(ConfigStatus.Conflict, again.Status);
        Assert.Equal("warehouse_code_taken", again.Code);
        Assert.Equal(1L, await WarehouseRows());
        Assert.Equal(1L, await Count(Company, Audit_.Table));               // the failed creation left no audit event
    }

    [PostgresFact]
    public async Task Update_changes_names_and_contacts_with_revision_audit_and_optimistic_concurrency()
    {
        await Prepare();
        var w = await MakeWarehouse("UPD");
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, null, NewWarehouse("UPD", w.BranchId))).Status);
        var updated = Ok(await Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, 1, NewWarehouse("UPD", w.BranchId, en: "Central", city: "Riyadh")));
        Assert.Equal(2, updated.Revision);
        Assert.Equal("Central", updated.Data.NameEn);
        Assert.Equal(ConfigAuditEvents.WarehouseUpdated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        var stale = await Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, 1, NewWarehouse("UPD", w.BranchId, en: "Other"));
        Assert.Equal(ConfigStatus.PreconditionFailed, stale.Status);
        Assert.Equal(2, stale.CurrentRevision);
        // Unchanged input writes nothing; the code and the branch cannot be changed.
        var before = await Count(Company, Audit_.Table);
        Assert.Equal(2, Ok(await Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, 2, NewWarehouse("UPD", w.BranchId, en: "Central", city: "Riyadh"))).Revision);
        Assert.Equal(before, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigStatus.Invalid, (await Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, 2, NewWarehouse("NEWCODE", w.BranchId))).Status);
        var other = await Make("OTHER");
        var moved = await Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, 2, NewWarehouse("UPD", other.Id));
        Assert.Equal(ConfigStatus.Invalid, moved.Status);
        Assert.Contains(moved.Fields!, e => e.Field == "branchId");
        Assert.Equal(ConfigStatus.NotFound, (await Warehouses().UpdateAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, NewWarehouse("UPD", w.BranchId))).Status);
    }

    [PostgresFact]
    public async Task Deactivate_and_reactivate_advance_the_epoch_and_reactivation_needs_an_active_branch()
    {
        await Prepare();
        var b = await Make("BR");
        var w = await MakeWarehouse("LIFE", b.Id);
        var off = Ok(await Warehouses().DeactivateAsync(Admin.Id, Company.Id, w.Id, w.Revision));
        Assert.False(off.IsActive);
        Assert.Equal(2, off.ActivationEpoch);
        Assert.Equal("warehouse_already_inactive", (await Warehouses().DeactivateAsync(Admin.Id, Company.Id, w.Id, off.Revision)).Code);
        // The branch can now be deactivated (no active warehouse); the warehouse cannot be reactivated inside it.
        var branch = (await Branches(new BranchLimitPolicy(50)).GetAsync(Company.Id, b.Id))!;
        var branchOff = Ok(await Branches(new BranchLimitPolicy(50)).DeactivateAsync(Admin.Id, Company.Id, b.Id, branch.Revision));
        var refused = await Warehouses().ReactivateAsync(Admin.Id, Company.Id, w.Id, off.Revision);
        Assert.Equal("branch_inactive", refused.Code);
        Ok(await Branches(new BranchLimitPolicy(50)).ReactivateAsync(Admin.Id, Company.Id, b.Id, branchOff.Revision));
        var on = Ok(await Warehouses().ReactivateAsync(Admin.Id, Company.Id, w.Id, off.Revision));
        Assert.True(on.IsActive);
        Assert.Equal(3, on.ActivationEpoch);
        Assert.Equal("warehouse_already_active", (await Warehouses().ReactivateAsync(Admin.Id, Company.Id, w.Id, on.Revision)).Code);
        Assert.Equal(3L, await Sql<long>($"SELECT count(*) FROM {Wr.Table} WHERE {Wr.WarehouseId} = '{w.Id}'"));
    }

    [PostgresFact]
    public async Task A_branch_with_an_active_warehouse_cannot_be_deactivated_through_the_service()
    {
        await Prepare();
        var b = await Make("BR");
        var w = await MakeWarehouse("KEEP", b.Id);
        var denied = await Branches(new BranchLimitPolicy(50)).DeactivateAsync(Admin.Id, Company.Id, b.Id, b.Revision);
        Assert.Equal(ConfigStatus.Conflict, denied.Status);
        Assert.Equal("branch_has_active_warehouses", denied.Code);
        Assert.True((await Branches(new BranchLimitPolicy(50)).GetAsync(Company.Id, b.Id))!.IsActive);
        Assert.True(w.IsActive);
    }

    [PostgresFact]
    public async Task Concurrent_warehouse_creation_and_branch_deactivation_never_leave_an_active_warehouse_in_an_inactive_branch()
    {
        await Prepare();
        for (var i = 0; i < 6; i++)
        {
            var b = await Make($"RACE{i}");
            var create = Warehouses().CreateAsync(Admin.Id, Company.Id, NewWarehouse($"RW{i}", b.Id));
            var deactivate = Branches(new BranchLimitPolicy(50)).DeactivateAsync(Admin.Id, Company.Id, b.Id, b.Revision);
            await Task.WhenAll(create, deactivate);
            var violations = await Sql<long>($"SELECT count(*) FROM {Wh.Table} w JOIN {Br.Table} b ON b.{Br.Id} = w.{Wh.BranchId} WHERE w.{Wh.IsActive} AND NOT b.{Br.IsActive}");
            Assert.Equal(0L, violations);
        }
    }
}
