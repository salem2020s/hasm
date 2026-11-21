using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Rv = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BranchRevision;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>Test-only edition with a configurable branch limit (never registered in the API).</summary>
internal sealed class BranchLimitPolicy(int max) : IEntitlementPolicy
{
    public string Edition => "test";
    public int MaxEnabledCurrencies => 1;
    public int MaxBranches => max;
    public IReadOnlyList<TrialRestriction> Restrictions => [];
}

/// <summary>Branches (company migration 0004, Phase 3.1B) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class BranchTestBase : ConfigurationTestBase
{
    protected const string MainArabic = "الفرع الرئيسي";

    protected CompanyBranchService Branches(IEntitlementPolicy? policy = null, CompanyRecord? company = null)
    {
        var resolver = SinglePrincipalResolver(company ?? Company);
        return new CompanyBranchService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), policy ?? TrialEntitlementPolicy.Instance, Clock);
    }

    protected static BranchInput NewBranch(string code, string? ar = "فرع", string? en = "Branch", string? phone = null, string? address = null, string? city = null) =>
        new(code, ar, en, address, null, city, null, null, phone, null, null);

    protected Task Sql(string sql, CompanyRecord? c = null) => InCompany((c ?? Company).PhysicalDatabaseName, sql);
    protected Task<T> Sql<T>(string sql, CompanyRecord? c = null) => InCompany<T>((c ?? Company).PhysicalDatabaseName, sql);

    /// <summary>Runs the statements in ONE transaction as the owner and returns the SQLSTATE of the failure (null = committed).</summary>
    protected async Task<string?> InTx(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    protected Task<string?> Fails(string sql) => InTx(sql);

    protected static string Insert(string code, bool main = false, bool active = true, string name = "'x'", long epoch = 1) =>
        $"INSERT INTO {Br.Table} ({Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) " +
        $"VALUES ('{code}', {name}, {(active ? "true" : "false")}, {(main ? "true" : "false")}, {epoch}, 1, now(), now())";

    protected static string Update(string code, string set) =>
        $"UPDATE {Br.Table} SET {set}, {Br.Revision} = {Br.Revision} + 1 WHERE {Br.Code} = '{code}'";

    protected Task<long> MainCount() => Sql<long>($"SELECT count(*) FROM {Br.Table} WHERE {Br.IsMain} AND {Br.IsActive}");
    protected Task<long> BranchRows() => Sql<long>($"SELECT count(*) FROM {Br.Table}");

    protected async Task<BranchView> Main() => (await Branches(new BranchLimitPolicy(50)).ListAsync(Company.Id)).Single(b => b.IsMain);

    protected async Task<BranchView> Make(string code, IEntitlementPolicy? policy = null) =>
        Ok(await Branches(policy ?? new BranchLimitPolicy(50)).CreateAsync(Admin.Id, Company.Id, NewBranch(code)));
}

public class BranchMigrationTests : BranchTestBase
{
    [PostgresFact]
    public async Task A_freshly_provisioned_company_has_exactly_one_active_main_branch_with_recorded_provenance()
    {
        await Prepare();
        Assert.Equal(31L, await Count(Company, SystemSchema.MigrationJournalTable));
        var main = Assert.Single(await Branches().ListAsync(Company.Id));
        Assert.Equal("MAIN", main.Data.Code);
        Assert.Equal(MainArabic, main.Data.NameAr);                      // logical Unicode order
        Assert.Equal("Main Branch", main.Data.NameEn);
        Assert.True(main.IsActive && main.IsMain);
        Assert.Equal(1, main.ActivationEpoch);
        Assert.Equal(1, main.Revision);
        Assert.Equal((short)4, main.SeedMigration);                      // provenance: created by migration 0004, not by a user
        Assert.Null(main.Data.AddressLine1);
        Assert.Equal(0L, await Count(Company, Audit_.Table));            // no fabricated business audit event
        Assert.Equal(0L, await Count(Company, Rv.Table));                // revision 1 of the seed has no history row
        Assert.Equal(MainArabic.Length, await Sql<int>($"SELECT char_length({Br.NameAr}) FROM {Br.Table}"));
        Assert.Equal(MainArabic, await Sql<string>($"SELECT {Br.NameAr} FROM {Br.Table}"));
    }

    [PostgresFact]
    public async Task An_existing_company_gets_its_main_branch_through_an_idempotent_backfill_that_keeps_its_data()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 3)]);
        await Sql("CREATE TABLE zz_existing (a int); INSERT INTO zz_existing VALUES (7)", legacy);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{Br.Table}'", legacy));

        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29], outcome.Applied);
        Assert.Equal(7, await Sql<int>("SELECT a FROM zz_existing", legacy));
        Assert.Equal(1L, await Count(legacy, Br.Table));
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
        Assert.Equal(1L, await Count(legacy, Br.Table));

        // Running the seed statement again (as an interrupted re-run would) changes nothing.
        var script = MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 4).Sql;
        var seed = Regex.Match(script, $@"INSERT INTO {Br.Table} \(.*?WHERE NOT EXISTS \(SELECT 1 FROM {Br.Table}\);", RegexOptions.Singleline).Value;
        Assert.NotEmpty(seed);
        await Sql(seed, legacy);
        Assert.Equal(1L, await Count(legacy, Br.Table));
    }

    [PostgresFact]
    public async Task A_database_two_versions_behind_applies_0003_and_0004_in_order()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 2)]);
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29], outcome.Applied);
        Assert.Equal(1L, await Count(legacy, Br.Table));
        Assert.Equal(31L, await Count(legacy, SystemSchema.MigrationJournalTable));
    }

    [PostgresFact]
    public async Task A_failing_migration_0004_rolls_back_completely_and_the_company_stays_at_0003()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 3)]);
        var good = all.Single(s => s.Version == 4);
        var broken = MigrationScript.Parse("0004_company_branches.sql", good.Sql + "\nDO $$ BEGIN RAISE EXCEPTION 'boom'; END; $$;");
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry, [.. all.Where(s => s.Version <= 3), broken]).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Failed, outcome.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name IN ('{Br.Table}', '{Rv.Table}')", legacy));
        Assert.Equal(3L, await Count(legacy, SystemSchema.MigrationJournalTable));
        // ... and the real migration still applies afterwards.
        Assert.Equal(CompanyMigrationStatus.Migrated, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
        Assert.Equal(1L, await Count(legacy, Br.Table));
    }

    [PostgresFact]
    public async Task The_initialization_guard_rolls_the_migration_back_when_no_main_branch_would_exist()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 3)]);
        var good = all.Single(s => s.Version == 4);
        var noSeed = MigrationScript.Parse("0004_company_branches.sql", good.Sql.Replace($"WHERE NOT EXISTS (SELECT 1 FROM {Br.Table})", "WHERE false"));
        Assert.NotEqual(good.Sql, noSeed.Sql);
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry, [.. all.Where(s => s.Version <= 3), noSeed]).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Failed, outcome.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{Br.Table}'", legacy));   // never operational without a main branch
    }

    [PostgresFact]
    public async Task Migration_checksum_and_version_handling_refuses_an_edited_or_unknown_journal()
    {
        await Prepare();
        var resolver = SinglePrincipalResolver(Company);
        (await resolver.OpenAsync(Company.Id)).Dispose();
        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'edited' WHERE version = 4");
        var ex = await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(Company.Id));
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible, ex.Code);
        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = '{MigrationScript.LoadEmbeddedCompany().Single(s => s.Version == 4).Checksum}' WHERE version = 4");
        (await resolver.OpenAsync(Company.Id)).Dispose();
        await Sql($"DELETE FROM {SystemSchema.MigrationJournalTable} WHERE version = 4");                       // a database that lacks 0004 is unmigrated, not usable
        Assert.Equal(CompanyDatabaseException.SchemaUnmigrated, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(Company.Id))).Code);
    }

    [PostgresFact]
    public async Task The_schema_gate_refuses_a_company_without_exactly_one_active_main_branch()
    {
        await Prepare();
        var resolver = SinglePrincipalResolver(Company);
        (await resolver.OpenAsync(Company.Id)).Dispose();
        // Corrupt the database outside the application (owner, triggers disabled): no main branch.
        await Sql($"ALTER TABLE {Br.Table} DISABLE TRIGGER USER; UPDATE {Br.Table} SET {Br.IsMain} = false; ALTER TABLE {Br.Table} ENABLE TRIGGER USER");
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(Company.Id))).Code);
    }
}

public class BranchConstraintTests : BranchTestBase
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;
    private const string Raised = PostgresErrorCodes.RaiseException;

    [PostgresFact]
    public async Task At_most_one_main_branch_is_enforced_immediately()
    {
        await Prepare();
        Assert.Equal(Unique, await Fails(Insert("B2", main: true)));
        Assert.Null(await Fails(Insert("B2")));
        Assert.Equal(Unique, await Fails(Update("B2", $"{Br.IsMain} = true")));
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task At_least_one_main_branch_is_enforced_at_commit_and_a_swap_is_atomic()
    {
        await Prepare();
        Assert.Null(await Fails(Insert("B2")));
        Assert.Equal(Raised, await Fails(Update("MAIN", $"{Br.IsMain} = false")));                                         // no main at commit
        Assert.Equal(1L, await MainCount());
        Assert.Equal(Unique, await Fails(Update("B2", $"{Br.IsMain} = true")));                                            // two mains: refused immediately
        Assert.Null(await InTx(Update("MAIN", $"{Br.IsMain} = false"), Update("B2", $"{Br.IsMain} = true")));              // swap in one transaction
        Assert.Equal("B2", await Sql<string>($"SELECT {Br.Code} FROM {Br.Table} WHERE {Br.IsMain}"));
        Assert.Equal(1L, await MainCount());
        // A swap that fails halfway leaves the previous main unchanged.
        Assert.NotNull(await InTx(Update("B2", $"{Br.IsMain} = false"), Update("MAIN", $"{Br.IsMain} = true, {Br.Code} = 'X'")));
        Assert.Equal("B2", await Sql<string>($"SELECT {Br.Code} FROM {Br.Table} WHERE {Br.IsMain}"));
    }

    [PostgresFact]
    public async Task A_main_branch_is_always_active()
    {
        await Prepare();
        Assert.Equal(CheckViolation, await Fails(Update("MAIN", $"{Br.IsActive} = false, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1")));
        Assert.Equal(CheckViolation, await Fails(Insert("B3", main: true, active: false)));
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task Codes_are_unique_case_insensitively_normalised_and_immutable()
    {
        await Prepare();
        Assert.Equal(Unique, await Fails(Insert("MAIN")));
        Assert.Equal(CheckViolation, await Fails(Insert("main")));                                                         // upper case only: no case variants exist
        Assert.Equal(CheckViolation, await Fails(Insert("A B")));
        Assert.Equal(CheckViolation, await Fails(Insert("-A")));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, await Fails(Insert("A23456789012345678901")));   // 21 characters: over the column length
        Assert.Equal(Raised, await Fails(Update("MAIN", $"{Br.Code} = 'ROOT'")));
        Assert.Equal(Raised, await Fails(Update("MAIN", $"{Br.Id} = gen_random_uuid()")));
        Assert.Equal(Raised, await Fails(Update("MAIN", $"{Br.SeedMigration} = NULL")));
        Assert.Equal(Raised, await Fails(Update("MAIN", $"{Br.CreatedAtUtc} = now() + interval '1 day'")));
        Assert.Equal("MAIN", await Sql<string>($"SELECT {Br.Code} FROM {Br.Table}"));
    }

    [PostgresFact]
    public async Task Names_need_at_least_one_non_empty_value_and_either_language_alone_is_valid()
    {
        await Prepare();
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Br.Table} ({Br.Code}, {Br.IsActive}, {Br.IsMain}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('N0', true, false, now(), now())"));
        Assert.Equal(CheckViolation, await Fails(Insert("N1", name: "''")));
        Assert.Equal(CheckViolation, await Fails(Insert("N1", name: "'   '")));
        Assert.Null(await Fails($"INSERT INTO {Br.Table} ({Br.Code}, {Br.NameAr}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('N2', 'فرع', now(), now())"));
        Assert.Null(await Fails($"INSERT INTO {Br.Table} ({Br.Code}, {Br.NameEn}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('N3', 'Branch', now(), now())"));
        Assert.Equal(CheckViolation, await Fails(Update("N3", $"{Br.NameEn} = NULL")));                                  // would leave no name
    }

    [PostgresFact]
    public async Task Branches_are_never_deleted_or_truncated_not_even_by_the_owner()
    {
        await Prepare();
        Assert.Equal(Raised, await Fails($"DELETE FROM {Br.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Br.Table} CASCADE"));
        Assert.Equal(1L, await BranchRows());
    }

    [PostgresFact]
    public async Task The_revision_fence_and_the_activation_epoch_rule_are_enforced_by_the_database()
    {
        await Prepare();
        Assert.Null(await Fails(Insert("E1")));
        Assert.Equal(Raised, await Fails($"UPDATE {Br.Table} SET {Br.NameEn} = 'y' WHERE {Br.Code} = 'E1'"));                                 // revision not advanced
        Assert.Equal(Raised, await Fails($"UPDATE {Br.Table} SET {Br.NameEn} = 'y', {Br.Revision} = {Br.Revision} + 2 WHERE {Br.Code} = 'E1'"));
        Assert.Equal(Raised, await Fails(Update("E1", $"{Br.IsActive} = false")));                                                         // deactivation without epoch bump
        Assert.Equal(Raised, await Fails(Update("E1", $"{Br.ActivationEpoch} = 5")));                                                       // epoch change without a state change
        Assert.Equal(Raised, await Fails(Update("E1", $"{Br.IsActive} = false, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 2")));
        Assert.Null(await Fails(Update("E1", $"{Br.IsActive} = false, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1")));
        Assert.Null(await Fails(Update("E1", $"{Br.IsActive} = true, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1")));
        Assert.Equal(3L, await Sql<long>($"SELECT {Br.ActivationEpoch} FROM {Br.Table} WHERE {Br.Code} = 'E1'"));
    }

    [PostgresFact]
    public async Task Branch_history_is_append_only_and_bound_to_a_branch_and_an_audit_event()
    {
        await Prepare();
        var created = await Make("H1");
        Assert.Equal(1L, await Count(Company, Rv.Table));
        Assert.Equal(Raised, await Fails($"UPDATE {Rv.Table} SET {Rv.Revision} = 9"));
        Assert.Equal(Raised, await Fails($"DELETE FROM {Rv.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Rv.Table}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails(
            $"INSERT INTO {Rv.Table} ({Rv.BranchId}, {Rv.Revision}, {Rv.ActorUserId}, {Rv.ChangedAtUtc}, {Rv.Changes}, {Rv.AuditStream}, {Rv.AuditSeq}) " +
            $"VALUES ('{created.Id}', 7, gen_random_uuid(), now(), '{{}}', 'business:none', 999)"));                                         // no audit event, no row
    }
}

public class BranchServiceTests : BranchTestBase
{
    [PostgresFact]
    public async Task Creating_a_branch_is_audited_in_the_same_transaction_and_the_new_branch_starts_active_and_not_main()
    {
        await Prepare();
        var b = await Make("NORTH");
        Assert.True(b.IsActive);
        Assert.False(b.IsMain);
        Assert.Equal(1, b.ActivationEpoch);
        Assert.Equal(1, b.Revision);
        Assert.Null(b.SeedMigration);
        Assert.Equal(1L, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigAuditEvents.BranchCreated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Rv.Table} WHERE {Rv.BranchId} = '{b.Id}' AND {Rv.Revision} = 1 AND {Rv.AuditSeq} = (SELECT {Audit_.Seq} FROM {Audit_.Table})"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task The_seeded_main_branch_advances_from_revision_1_to_2_on_its_first_audited_change()
    {
        await Prepare();
        var main = await Main();
        var updated = Ok(await Branches().UpdateAsync(Admin.Id, Company.Id, main.Id, 1, NewBranch("MAIN", ar: main.Data.NameAr, en: "Head Office")));
        Assert.Equal(2, updated.Revision);
        Assert.Equal("Head Office", updated.Data.NameEn);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Rv.Table} WHERE {Rv.BranchId} = '{main.Id}' AND {Rv.Revision} = 2"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Rv.Table} WHERE {Rv.Revision} = 1"));                  // the seed itself has no history row
        Assert.Equal((short)4, updated.SeedMigration);                                                                    // provenance is permanent
        Assert.Equal(ConfigAuditEvents.BranchUpdated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table}"));
        Assert.Contains("Head Office", await Sql<string>($"SELECT {Rv.Changes}::text FROM {Rv.Table}"));
        Assert.Contains("\"old\"", await Sql<string>($"SELECT {Rv.Changes}::text FROM {Rv.Table}"));
    }

    [PostgresFact]
    public async Task Optimistic_concurrency_requires_and_checks_the_revision()
    {
        await Prepare();
        var b = await Make("OC");
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, null, NewBranch("OC", en: "A"))).Status);
        Ok(await Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("OC", en: "A")));
        var stale = await Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("OC", en: "B"));
        Assert.Equal(ConfigStatus.PreconditionFailed, stale.Status);
        Assert.Equal(2, stale.CurrentRevision);
        Assert.Equal("A", (await Branches().GetAsync(Company.Id, b.Id))!.Data.NameEn);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Branches().DeactivateAsync(Admin.Id, Company.Id, b.Id, 1)).Status);
        Assert.Equal(ConfigStatus.NotFound, (await Branches().UpdateAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, NewBranch("ZZ"))).Status);
    }

    [PostgresFact]
    public async Task An_update_that_changes_nothing_writes_no_audit_event_and_no_revision()
    {
        await Prepare();
        var b = await Make("NC");
        var before = await Count(Company, Audit_.Table);
        var same = Ok(await Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("NC")));
        Assert.Equal(1, same.Revision);
        Assert.Equal(before, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task The_code_cannot_change_and_a_duplicate_code_is_refused_in_any_case()
    {
        await Prepare();
        var b = await Make("DUP");
        Assert.Equal("code", Assert.Single((await Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("OTHER"))).Fields!).Field);
        var taken = await Branches(new BranchLimitPolicy(9)).CreateAsync(Admin.Id, Company.Id, NewBranch("dup"));       // normalised to DUP
        Assert.Equal(ConfigStatus.Conflict, taken.Status);
        Assert.Equal("branch_code_taken", taken.Code);
        Assert.Equal(ConfigStatus.Conflict, (await Branches(new BranchLimitPolicy(9)).CreateAsync(Admin.Id, Company.Id, NewBranch("main"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Branches(new BranchLimitPolicy(9)).CreateAsync(Admin.Id, Company.Id, NewBranch("bad code"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Branches(new BranchLimitPolicy(9)).CreateAsync(Admin.Id, Company.Id, NewBranch("NONAME", ar: null, en: null))).Status);
        Assert.Equal(2L, await BranchRows());
    }

    [PostgresFact]
    public async Task Sensitive_branch_fields_are_stored_but_never_appear_in_the_audit_or_history()
    {
        await Prepare();
        var b = Ok(await Branches(new BranchLimitPolicy(9)).CreateAsync(Admin.Id, Company.Id,
            NewBranch("PRIV", en: "Visible Name", phone: "+963 11 5550199", address: "Secret Street 12", city: "Aleppo")));
        Assert.Equal("Secret Street 12", b.Data.AddressLine1);
        Ok(await Branches(new BranchLimitPolicy(9)).UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("PRIV", en: "Visible Name", phone: "+963 11 5550200", address: "Other Secret 9", city: "Aleppo")));
        var history = await Sql<string>($"SELECT string_agg({Rv.Changes}::text, ' ') FROM {Rv.Table}");
        var details = await Sql<string>($"SELECT string_agg(coalesce({Audit_.Detail}, ''), ' ') FROM {Audit_.Table}");
        foreach (var secret in new[] { "5550199", "5550200", "Secret Street", "Other Secret" })
        { Assert.DoesNotContain(secret, history); Assert.DoesNotContain(secret, details); }
        Assert.Contains("Visible Name", history);                  // permitted fields keep their values
        Assert.Contains("Aleppo", history);
        Assert.Contains("phone", history);                         // the fact of the change is recorded
        Assert.Contains("addressLine1", history);
        Assert.Contains("addressLine1", details);
    }

    [PostgresFact]
    public async Task Trial_allows_only_the_automatically_created_main_branch()
    {
        await Prepare();
        var denied = await Branches().CreateAsync(Admin.Id, Company.Id, NewBranch("SECOND"));                              // real Trial policy
        Assert.Equal(ConfigStatus.EntitlementDenied, denied.Status);
        Assert.Equal(1L, await BranchRows());
        Assert.Equal(0L, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task Inactive_branches_keep_counting_so_deactivating_never_frees_a_trial_slot()
    {
        await Prepare();
        var two = new BranchLimitPolicy(2);                                                                                   // main + one more
        var b = await Make("SLOT", two);
        Assert.Equal(ConfigStatus.EntitlementDenied, (await Branches(two).CreateAsync(Admin.Id, Company.Id, NewBranch("THIRD"))).Status);
        Ok(await Branches(two).DeactivateAsync(Admin.Id, Company.Id, b.Id, 1));
        var replacement = await Branches(two).CreateAsync(Admin.Id, Company.Id, NewBranch("REPLACEMENT"));
        Assert.Equal(ConfigStatus.EntitlementDenied, replacement.Status);                                                     // the inactive branch still counts
        Assert.Equal(2L, await BranchRows());
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Br.Table} WHERE NOT {Br.IsActive}"));
        // A Trial company cannot escape by deactivating its only branch either.
        var main = await Main();
        Assert.Equal("main_branch_deactivation_denied", (await Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1)).Code);
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task Concurrent_creations_cannot_exceed_the_limit()
    {
        await Prepare();
        var policy = new BranchLimitPolicy(3);                                                                               // main + two more
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Branches(policy).CreateAsync(Admin.Id, Company.Id, NewBranch($"C{i}"))));
        Assert.Equal(2, results.Count(r => r.IsOk));
        Assert.Equal(6, results.Count(r => r.Status == ConfigStatus.EntitlementDenied));
        Assert.Equal(3L, await BranchRows());
        Assert.Equal(1L, await MainCount());
    }

    [PostgresFact]
    public async Task The_main_branch_cannot_be_deactivated_without_an_active_successor_and_a_refusal_changes_nothing()
    {
        await Prepare();
        var main = await Main();
        var other = await Make("OTHER");
        var before = await Count(Company, Audit_.Table);
        Assert.Equal("main_branch_deactivation_denied", (await Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1)).Code);
        Assert.Equal("main_branch_deactivation_denied", (await Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1, main.Id)).Code);   // itself
        Assert.Equal("branch_not_found", (await Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1, Guid.NewGuid())).Code);
        Ok(await Branches().DeactivateAsync(Admin.Id, Company.Id, other.Id, 1));                                                              // an inactive candidate
        Assert.Equal("branch_inactive", (await Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1, other.Id)).Code);
        var unchanged = await Main();
        Assert.Equal(main.Id, unchanged.Id);
        Assert.Equal(1, unchanged.Revision);
        Assert.True(unchanged.IsActive);
        Assert.Equal(before + 1, await Count(Company, Audit_.Table));                                                                          // only the successful deactivation of OTHER
    }

    [PostgresFact]
    public async Task Deactivating_the_main_branch_with_a_successor_is_atomic_and_audited()
    {
        await Prepare();
        var main = await Main();
        var next = await Make("NEXT");
        var old = Ok(await Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1, next.Id));
        Assert.False(old.IsActive);
        Assert.False(old.IsMain);
        Assert.Equal(2, old.ActivationEpoch);
        Assert.Equal(2, old.Revision);
        var now = await Main();
        Assert.Equal(next.Id, now.Id);
        Assert.Equal(2, now.Revision);
        Assert.Equal(1L, await MainCount());
        var events = await Sql<string>($"SELECT string_agg({Audit_.EventType}::text, ',' ORDER BY {Audit_.Seq}) FROM {Audit_.Table}");
        Assert.Equal($"{ConfigAuditEvents.BranchCreated},{ConfigAuditEvents.BranchDeactivated},{ConfigAuditEvents.BranchMainDesignated}", events);
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Make_main_swaps_the_role_atomically_with_two_events_and_refuses_inactive_or_current_targets()
    {
        await Prepare();
        var main = await Main();
        var b = await Make("SWAP");
        Assert.Equal("branch_already_main", (await Branches().MakeMainAsync(Admin.Id, Company.Id, main.Id, 1)).Code);
        var swapped = Ok(await Branches().MakeMainAsync(Admin.Id, Company.Id, b.Id, 1));
        Assert.True(swapped.IsMain);
        Assert.Equal(2, swapped.Revision);
        Assert.False((await Branches().GetAsync(Company.Id, main.Id))!.IsMain);
        Assert.Equal(1L, await MainCount());
        var events = await Sql<string>($"SELECT string_agg({Audit_.EventType}::text, ',' ORDER BY {Audit_.Seq}) FROM {Audit_.Table}");
        Assert.Equal($"{ConfigAuditEvents.BranchCreated},{ConfigAuditEvents.BranchMainRevoked},{ConfigAuditEvents.BranchMainDesignated}", events);

        var c = await Make("INACT");
        Ok(await Branches().DeactivateAsync(Admin.Id, Company.Id, c.Id, 1));
        Assert.Equal("branch_inactive", (await Branches().MakeMainAsync(Admin.Id, Company.Id, c.Id, 2)).Code);
        Assert.Equal(b.Id, (await Main()).Id);
    }

    [PostgresFact]
    public async Task Concurrent_main_changes_always_leave_exactly_one_main_branch()
    {
        await Prepare();
        var b = await Make("B"); var c = await Make("C"); var d = await Make("D");
        var tasks = new[] { b, c, d, b, c, d }.Select(x => Branches().MakeMainAsync(Admin.Id, Company.Id, x.Id, 1)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Contains(results, r => r.IsOk);
        Assert.All(results, r => Assert.Contains(r.Status, new[] { ConfigStatus.Ok, ConfigStatus.PreconditionFailed, ConfigStatus.Conflict }));
        Assert.Equal(1L, await MainCount());
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Br.Table} WHERE {Br.IsMain}"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Concurrent_deactivation_of_the_main_branch_and_a_swap_keep_the_invariant()
    {
        await Prepare();
        var main = await Main();
        var b = await Make("B"); var c = await Make("C");
        var results = await Task.WhenAll(
            Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1, b.Id),
            Branches().MakeMainAsync(Admin.Id, Company.Id, c.Id, 1),
            Branches().DeactivateAsync(Admin.Id, Company.Id, main.Id, 1, c.Id));
        Assert.Contains(results, r => r.IsOk);
        Assert.Equal(1L, await MainCount());
        Assert.True((await Main()).IsActive);
    }

    [PostgresFact]
    public async Task Activation_epoch_advances_on_every_deactivation_and_reactivation()
    {
        await Prepare();
        var b = await Make("EPOCH");
        Assert.Equal(1, b.ActivationEpoch);
        var off = Ok(await Branches().DeactivateAsync(Admin.Id, Company.Id, b.Id, 1));
        Assert.Equal(2, off.ActivationEpoch);
        Assert.Equal("branch_already_inactive", (await Branches().DeactivateAsync(Admin.Id, Company.Id, b.Id, 2)).Code);
        var on = Ok(await Branches().ReactivateAsync(Admin.Id, Company.Id, b.Id, 2));
        Assert.Equal(3, on.ActivationEpoch);
        Assert.True(on.IsActive);
        Assert.Equal("branch_already_active", (await Branches().ReactivateAsync(Admin.Id, Company.Id, b.Id, 3)).Code);
        var off2 = Ok(await Branches().DeactivateAsync(Admin.Id, Company.Id, b.Id, 3));
        Assert.Equal(4, off2.ActivationEpoch);
        Assert.Equal(4, off2.Revision);
        var types = await Sql<string>($"SELECT string_agg({Audit_.EventType}::text, ',' ORDER BY {Audit_.Seq}) FROM {Audit_.Table}");
        Assert.Equal($"{ConfigAuditEvents.BranchCreated},{ConfigAuditEvents.BranchDeactivated},{ConfigAuditEvents.BranchReactivated},{ConfigAuditEvents.BranchDeactivated}", types);
        Assert.Equal(3L, await Sql<long>($"SELECT count(*) FROM {Rv.Table} WHERE {Rv.BranchId} = '{b.Id}' AND {Rv.Revision} > 1"));
    }

    [PostgresFact]
    public async Task When_the_audit_cannot_be_written_no_branch_change_is_saved()
    {
        await Prepare();
        var b = await Make("ATOMIC");
        var rows = await BranchRows(); var events = await Count(Company, Audit_.Table); var history = await Count(Company, Rv.Table);
        AuditKeys.Unavailable = true;
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Branches(new BranchLimitPolicy(9)).CreateAsync(Admin.Id, Company.Id, NewBranch("LOST")));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("ATOMIC", en: "Changed")));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Branches().DeactivateAsync(Admin.Id, Company.Id, b.Id, 1));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Branches().MakeMainAsync(Admin.Id, Company.Id, b.Id, 1));
        AuditKeys.Unavailable = false;
        Assert.Equal(rows, await BranchRows());
        Assert.Equal(events, await Count(Company, Audit_.Table));
        Assert.Equal(history, await Count(Company, Rv.Table));
        var unchanged = (await Branches().GetAsync(Company.Id, b.Id))!;
        Assert.Equal((1L, "Branch", true, false), (unchanged.Revision, unchanged.Data.NameEn, unchanged.IsActive, unchanged.IsMain));
        Assert.Equal(1L, await MainCount());
        Ok(await Branches().MakeMainAsync(Admin.Id, Company.Id, b.Id, 1));                                                   // and the next attempt works
    }

    [PostgresFact]
    public async Task Branch_operations_on_a_company_never_touch_another_company()
    {
        await Prepare();
        var other = await ReadyCompany();
        var mine = await Make("MINE");
        Assert.Equal(1L, await Count(other, Br.Table));
        Assert.Null(await Branches(company: other).GetAsync(other.Id, mine.Id));
        Assert.Equal(ConfigStatus.NotFound, (await Branches(company: other).UpdateAsync(Admin.Id, other.Id, mine.Id, 1, NewBranch("MINE", en: "x"))).Status);
        Assert.Equal(1L, await Count(other, Br.Table));
        Assert.Equal(0L, await Count(other, Audit_.Table));
    }
}

/// <summary>Real per-company PostgreSQL role: branch privileges (role-capable disposable cluster only).</summary>
public class BranchRuntimeRoleTests : CompanyRuntimeTestBase
{
    protected override bool RolesRequired => true;

    private CompanyRuntimeProvisioner Provisioner() => new(Configured(useRoleAdmin: true), Registry);

    private async Task<NpgsqlConnection> AsRuntime(CompanyRecord c)
    {
        var credential = (await new FileCompanyCredentialStore(CredentialDirectory).GetAsync(c.Id))!;
        var cs = new NpgsqlConnectionStringBuilder(Configured().BuildCompanyRuntimeConnectionString(c.PhysicalDatabaseName, credential.Username, credential.Password)) { Pooling = false };
        var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<string?> Try(NpgsqlConnection conn, string sql)
    {
        try { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_can_read_insert_and_update_branches_but_never_delete_truncate_or_rewrite_history()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await Provisioner().GrantAsync(c.Id);
        await using var rt = await AsRuntime(c);
        Assert.Equal(1L, (long)(await new NpgsqlCommand($"SELECT count(*) FROM {Br.Table}", rt).ExecuteScalarAsync())!);
        Assert.Null(await Try(rt, Insert("RT1")));
        Assert.Null(await Try(rt, $"UPDATE {Br.Table} SET {Br.NameEn} = 'y', {Br.Revision} = {Br.Revision} + 1 WHERE {Br.Code} = 'RT1'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"DELETE FROM {Br.Table}"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"TRUNCATE {Br.Table}"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"UPDATE {Rv.Table} SET {Rv.Revision} = 1"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"DELETE FROM {Rv.Table}"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"ALTER TABLE {Br.Table} DISABLE TRIGGER USER"));       // not the owner: cannot switch the guards off
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await Try(rt, $"DROP TABLE {Br.Table}"));
        // The invariants hold for the runtime role too (PostgreSQL enforces them, not the application).
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await Try(rt, Insert("RT2", main: true)));
        Assert.Equal(PostgresErrorCodes.RaiseException, await Try(rt, $"UPDATE {Br.Table} SET {Br.Code} = 'ZZ', {Br.Revision} = {Br.Revision} + 1 WHERE {Br.Code} = 'RT1'"));
        // The API-side gate accepts exactly this privilege set and reports nothing excessive.
        var resolver = RealResolver();
        (await resolver.OpenAsync(c.Id)).Dispose();
    }

    [PostgresRoleFact]
    public async Task The_gate_reports_extra_branch_rights_and_grant_converges_back_to_the_catalog()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await Provisioner().GrantAsync(c.Id);
        var role = CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName);
        await InCompany(c.PhysicalDatabaseName, $"GRANT DELETE ON {Br.Table} TO \"{role}\"; GRANT UPDATE ON {Rv.Table} TO \"{role}\"");
        await using (var rt = await AsRuntime(c))
        {
            var violations = await CompanyRuntimePrivileges.FindViolationsAsync(rt, role);
            Assert.Contains("branch_modifiable", violations);
            Assert.Contains("branch_history_modifiable", violations);
        }
        var overprivileged = await Assert.ThrowsAsync<CompanyDatabaseException>(() => RealResolver().OpenAsync(c.Id));
        Assert.Equal(CompanyDatabaseException.RuntimeOverprivileged, overprivileged.Code);
        await Provisioner().GrantAsync(c.Id);                                                                                    // converges
        (await RealResolver().OpenAsync(c.Id)).Dispose();
    }

    private static string Insert(string code, bool main = false) =>
        $"INSERT INTO {Br.Table} ({Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) " +
        $"VALUES ('{code}', 'x', true, {(main ? "true" : "false")}, 1, 1, now(), now())";
}
