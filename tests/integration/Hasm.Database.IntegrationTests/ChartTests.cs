using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using As = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountSettings;
using Ar = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountRevision;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>A test-only account-usage probe standing in for the General Ledger. Never registered in the API.</summary>
internal sealed class PostingsProbe(bool hasPostings) : IAccountUsageProbe
{
    public string Domain => "general_ledger_postings";
    public bool Implemented => true;
    public Task<bool> HasPostingsAsync(Guid companyId, Guid accountId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(hasPostings);
}

/// <summary>Chart of Accounts (company migration 0007, Phase 3.2A) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class ChartTestBase : BranchTestBase
{
    protected long AuditBaseline;

    protected ChartOfAccountsService Chart(AccountUsageGuard? usage = null, FinancialHistoryGuard? history = null, CompanyRecord? company = null)
    {
        var resolver = SinglePrincipalResolver(company ?? Company);
        return new ChartOfAccountsService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), usage ?? new AccountUsageGuard(), history ?? new FinancialHistoryGuard(), Clock);
    }

    protected async Task PrepareChart()
    {
        await Prepare();
        AuditBaseline = await Count(Company, Audit_.Table);
    }

    protected static AccountInput Root(string? code, string type, string? ar = "أصول", string? en = "Assets", bool group = true) =>
        new(code, null, ar, en, type, null, group, null, null);

    protected static AccountInput Child(Guid parent, string? code, string? ar = "حساب", string? en = "Account", bool group = false, string? nature = null, string? policy = null, string? currency = null, string? type = null) =>
        new(code, parent, ar, en, type, nature, group, policy, currency);

    protected async Task<AccountView> NewRoot(string code, string type = "asset", bool group = true) =>
        Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root(code, type, group: group)));

    protected async Task<AccountView> NewChild(AccountView parent, string? code, bool group = false) =>
        Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Child(parent.Id, code, group: group)));

    protected static string InsertAcc(string code, int type = 1, Guid? parent = null, bool group = false, bool active = true, string name = "'x'", int nature = 1, string extra = "") =>
        $"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.ParentId}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.IsGroup}, {Ac.IsActive}, {Ac.Revision}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) " +
        $"VALUES ('{code}', {name}, {(parent is null ? "NULL" : $"'{parent}'")}, {type}, {nature}, {(type <= 3 ? 1 : 2)}, {(group ? "true" : "false")}, {(active ? "true" : "false")}, 1, now(), now())";

    protected static string UpdAcc(string code, string set) => $"UPDATE {Ac.Table} SET {set}, {Ac.Revision} = {Ac.Revision} + 1 WHERE {Ac.Code} = '{code}'";
    protected Task<long> AccountRows() => Sql<long>($"SELECT count(*) FROM {Ac.Table}");
}

public class ChartMigrationTests : ChartTestBase
{
    [PostgresFact]
    public async Task A_freshly_provisioned_company_has_an_empty_chart_with_defaults_and_no_fabricated_audit_event()
    {
        await Prepare();
        Assert.Equal(23L, await Count(Company, SystemSchema.MigrationJournalTable));
        Assert.Empty(await Chart().ListAsync(Company.Id));
        Assert.Equal(ChartSettingsView.Defaults, await Chart().GetSettingsAsync(Company.Id));          // a read never creates the settings row
        Assert.Equal(0L, await Count(Company, Ac.Table));
        Assert.Equal(0L, await Count(Company, As.Table));
        Assert.Equal(0L, await Count(Company, Ar.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task An_existing_company_at_0006_gets_0007_without_touching_its_data_and_the_migration_is_idempotent()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 6)]);
        await Sql("CREATE TABLE zz_existing (a int); INSERT INTO zz_existing VALUES (7)", legacy);
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23], outcome.Applied);
        Assert.Equal(7, await Sql<int>("SELECT a FROM zz_existing", legacy));
        Assert.Equal(0L, await Count(legacy, Ac.Table));                          // nothing is seeded
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
    }

    [PostgresFact]
    public async Task A_failing_migration_0007_rolls_back_completely_and_the_company_stays_at_0006()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 6)]);
        var broken = MigrationScript.Parse("0007_company_chart_of_accounts.sql", all.Single(s => s.Version == 7).Sql + "\nDO $$ BEGIN RAISE EXCEPTION 'boom'; END; $$;");
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry, [.. all.Where(s => s.Version <= 6), broken]).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Failed, outcome.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name IN ('{Ac.Table}', '{As.Table}', '{Ar.Table}')", legacy));
        Assert.Equal(6L, await Count(legacy, SystemSchema.MigrationJournalTable));
    }

    [PostgresFact]
    public async Task A_company_edited_after_migration_0007_is_detected_by_the_checksum_gate()
    {
        await Prepare();
        var resolver = SinglePrincipalResolver(Company);
        (await resolver.OpenAsync(Company.Id)).Dispose();
        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'edited' WHERE version = 7");
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(Company.Id))).Code);
    }
}

public class ChartConstraintTests : ChartTestBase
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;
    private const string Raised = PostgresErrorCodes.RaiseException;

    [PostgresFact]
    public async Task Codes_are_unique_case_insensitively_upper_case_only_and_may_be_changed()
    {
        await PrepareChart();
        Assert.Null(await Fails(InsertAcc("A1")));
        Assert.Equal(Unique, await Fails(InsertAcc("A1")));
        Assert.Equal(CheckViolation, await Fails(InsertAcc("a2")));
        Assert.Equal(CheckViolation, await Fails(InsertAcc("A 2")));
        Assert.Equal(CheckViolation, await Fails(InsertAcc("-A")));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, await Fails(InsertAcc(new string('A', 31))));
        Assert.Null(await Fails(InsertAcc("1.2-3_4")));
        Assert.Null(await Fails(UpdAcc("A1", $"{Ac.Code} = 'A9'")));                                                   // codes are NOT immutable
        Assert.Equal(Unique, await Fails(UpdAcc("A9", $"{Ac.Code} = '1.2-3_4'")));
        Assert.Equal(Raised, await Fails(UpdAcc("A9", $"{Ac.Id} = gen_random_uuid()")));                                // the stable id is
        Assert.Equal(Raised, await Fails(UpdAcc("A9", $"{Ac.CreatedAtUtc} = now() + interval '1 day'")));
        Assert.Equal(Raised, await Fails(UpdAcc("A9", $"{Ac.TemplateKey} = 'generic'")));
    }

    [PostgresFact]
    public async Task Names_type_statement_nature_and_currency_policy_are_constrained()
    {
        await PrepareChart();
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('N0', 1, 1, 1, now(), now())"));   // no name
        Assert.Equal(CheckViolation, await Fails(InsertAcc("N1", name: "'   '")));
        Assert.Null(await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameAr}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('N2', 'حساب', 1, 1, 1, now(), now())"));
        Assert.Equal(CheckViolation, await Fails(InsertAcc("T0", type: 0)));
        Assert.Equal(CheckViolation, await Fails(InsertAcc("T6", type: 6)));
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('S1', 'x', 1, 1, 2, now(), now())"));   // an asset is never Profit and Loss
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('S2', 'x', 4, 1, 1, now(), now())"));   // revenue is never Balance Sheet
        Assert.Equal(CheckViolation, await Fails(InsertAcc("NA", nature: 3)));
        Assert.Null(await Fails(InsertAcc("CONTRA", type: 1, nature: 2)));                                              // a contra account: asset with credit nature
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CurrencyPolicy}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('C1', 'x', 1, 1, 1, 3, now(), now())"));   // specific without a code
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CurrencyPolicy}, {Ac.CurrencyCode}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('C2', 'x', 1, 1, 1, 1, 'USD', now(), now())"));   // a code without 'specific'
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CurrencyPolicy}, {Ac.CurrencyCode}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('C3', 'x', 1, 1, 1, 3, 'ZZZ', now(), now())"));   // an unknown currency
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Ac.Table} ({Ac.Code}, {Ac.NameEn}, {Ac.Type}, {Ac.Nature}, {Ac.Statement}, {Ac.CurrencyPolicy}, {Ac.CreatedAtUtc}, {Ac.UpdatedAtUtc}) VALUES ('C4', 'x', 1, 1, 1, 9, now(), now())"));
    }

    [PostgresFact]
    public async Task The_hierarchy_is_real_parents_that_are_groups_of_the_same_type_without_cycles_and_without_a_fixed_depth()
    {
        await PrepareChart();
        var root = await NewRoot("1");
        var leaf = await NewChild(root, "101");
        Assert.Equal(Raised, await InTx(InsertAcc("UNDERLEAF", parent: leaf.Id)));                                           // a non-group has no children
        Assert.Equal(Raised, await InTx(InsertAcc("WRONGTYPE", type: 2, parent: root.Id)));                                  // a liability under an asset parent
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await InTx(InsertAcc("ORPHAN", parent: Guid.NewGuid())));
        Assert.Equal(CheckViolation, await Fails(UpdAcc("1", $"{Ac.ParentId} = {Ac.Id}")));                                  // not its own parent
        // A deep chain of groups: no depth limit.
        var parent = root;
        for (var i = 0; i < 40; i++) parent = await NewChild(parent, $"1{i:00}X", group: true);
        Assert.Equal(42L, await AccountRows());
        // A cycle (a group under its own descendant) is refused at commit.
        Assert.Equal(Raised, await Fails(UpdAcc("1", $"{Ac.ParentId} = '{parent.Id}'")));
        Assert.Equal(Raised, await InTx(UpdAcc("100X", $"{Ac.ParentId} = '{parent.Id}'")));
        // Making a parent a non-group, or changing its type under its children, is refused at commit.
        Assert.Equal(Raised, await Fails(UpdAcc("1", $"{Ac.IsGroup} = false")));
        Assert.Equal(Raised, await Fails($"UPDATE {Ac.Table} SET {Ac.Type} = 2, {Ac.Statement} = 1, {Ac.Revision} = {Ac.Revision} + 1 WHERE {Ac.Code} = '1'"));
    }

    [PostgresFact]
    public async Task An_active_account_never_sits_under_an_inactive_parent_in_either_direction_at_commit()
    {
        await PrepareChart();
        var root = await NewRoot("1");
        var leaf = await NewChild(root, "101");
        Assert.Equal(Raised, await Fails(UpdAcc("1", $"{Ac.IsActive} = false")));                                            // an active child
        Assert.Null(await InTx(UpdAcc("101", $"{Ac.IsActive} = false"), UpdAcc("1", $"{Ac.IsActive} = false")));             // both together: fine
        Assert.Equal(Raised, await Fails(InsertAcc("102", parent: root.Id)));                                                // a new active child under an inactive parent
        Assert.Equal(Raised, await Fails(UpdAcc("101", $"{Ac.IsActive} = true")));                                           // reactivating under an inactive parent
        Assert.Null(await InTx(UpdAcc("1", $"{Ac.IsActive} = true"), UpdAcc("101", $"{Ac.IsActive} = true")));
        Assert.NotNull(leaf);
    }

    [PostgresFact]
    public async Task Accounts_are_never_deleted_or_truncated_and_the_revision_fence_holds_and_history_is_append_only()
    {
        await PrepareChart();
        await NewRoot("1");
        Assert.Equal(Raised, await Fails($"DELETE FROM {Ac.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Ac.Table} CASCADE"));
        Assert.Equal(Raised, await Fails($"UPDATE {Ac.Table} SET {Ac.NameEn} = 'y' WHERE {Ac.Code} = '1'"));                 // revision not advanced
        Assert.Equal(Raised, await Fails($"UPDATE {Ac.Table} SET {Ac.NameEn} = 'y', {Ac.Revision} = {Ac.Revision} + 2 WHERE {Ac.Code} = '1'"));
        Assert.Equal(1L, await AccountRows());
        Assert.Equal(1L, await Count(Company, Ar.Table));
        Assert.Equal(Raised, await Fails($"UPDATE {Ar.Table} SET {Ar.Revision} = 9"));
        Assert.Equal(Raised, await Fails($"DELETE FROM {Ar.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Ar.Table}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails(
            $"INSERT INTO {Ar.Table} ({Ar.Entity}, {Ar.EntityId}, {Ar.Revision}, {Ar.ActorUserId}, {Ar.ChangedAtUtc}, {Ar.Changes}, {Ar.AuditStream}, {Ar.AuditSeq}) VALUES (1, gen_random_uuid(), 7, gen_random_uuid(), now(), '{{}}', 'business:none', 999)"));
        Assert.Equal(CheckViolation, await Fails(
            $"INSERT INTO {Ar.Table} ({Ar.Entity}, {Ar.EntityId}, {Ar.Revision}, {Ar.ActorUserId}, {Ar.ChangedAtUtc}, {Ar.Changes}, {Ar.AuditStream}, {Ar.AuditSeq}) VALUES (9, gen_random_uuid(), 7, gen_random_uuid(), now(), '{{}}', 'x', 1)"));
    }

    [PostgresFact]
    public async Task The_chart_settings_are_a_bounded_singleton_that_is_never_deleted()
    {
        await PrepareChart();
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {As.Table} ({As.SegmentWidth}, {As.UpdatedAtUtc}) VALUES (0, now())"));
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {As.Table} ({As.SegmentWidth}, {As.UpdatedAtUtc}) VALUES (5, now())"));
        Assert.Null(await Fails($"INSERT INTO {As.Table} ({As.SegmentWidth}, {As.UpdatedAtUtc}) VALUES (3, now())"));
        Assert.Equal(Unique, await Fails($"INSERT INTO {As.Table} ({As.SegmentWidth}, {As.UpdatedAtUtc}) VALUES (3, now())"));       // a single row only
        Assert.Equal(Raised, await Fails($"DELETE FROM {As.Table}"));
        Assert.Equal(Raised, await Fails($"UPDATE {As.Table} SET {As.SegmentWidth} = 4"));                                             // revision fence
    }
}

public class ChartServiceTests : ChartTestBase
{
    [PostgresFact]
    public async Task Creating_an_account_is_audited_in_the_same_transaction_and_starts_active_with_derived_classification()
    {
        await PrepareChart();
        var root = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("1", "asset", "الأصول", "Assets")));
        Assert.Equal((AccountType.Asset, AccountNature.Debit, AccountStatement.BalanceSheet), (root.Type, root.Nature, root.Statement));
        Assert.True(root.IsGroup && root.IsActive);
        Assert.False(root.IsPostable);                                                                                              // a group is never postable
        var exp = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("5", "expense", null, "Expenses")));
        Assert.Equal((AccountNature.Debit, AccountStatement.ProfitAndLoss), (exp.Nature, exp.Statement));
        var rev = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("4", "revenue", null, "Revenue")));
        Assert.Equal(AccountNature.Credit, rev.Nature);
        var leaf = await NewChild(root, "101");
        Assert.True(leaf.IsPostable);
        Assert.Equal(root.Id, leaf.ParentId);
        Assert.Equal(AccountType.Asset, leaf.Type);                                                                                  // inherited
        Assert.Equal(AuditBaseline + 4, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigAuditEvents.AccountCreated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ar.Table} WHERE {Ar.Entity} = 1 AND {Ar.EntityId} = '{leaf.Id}' AND {Ar.Revision} = 1"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        Assert.Equal(leaf, await Chart().GetAsync(Company.Id, leaf.Id));
    }

    [PostgresFact]
    public async Task Manual_codes_are_normalised_consistently_and_must_be_unique_and_valid()
    {
        await PrepareChart();
        var a = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("  ab-1 ", "asset")));
        Assert.Equal("AB-1", a.Code);
        var arabic = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("٢٠٠١", "liability")));                                // Arabic-Indic digits
        Assert.Equal("2001", arabic.Code);
        Assert.Equal("1234", Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("۱۲۳۴", "equity"))).Code);                    // Extended Arabic-Indic digits
        var dup = await Chart().CreateAsync(Admin.Id, Company.Id, Root("ab-1", "asset"));
        Assert.Equal((ConfigStatus.Conflict, "account_code_taken"), (dup.Status, dup.Code));
        foreach (var bad in new[] { "A B", "-A", "A/B", "فرع", new string('X', 31) })
        {
            var r = await Chart().CreateAsync(Admin.Id, Company.Id, Root(bad, "asset"));
            Assert.Equal(ConfigStatus.Invalid, r.Status);
            Assert.Contains(r.Fields!, e => e.Field == "code");
        }
        Assert.Equal(3L, await AccountRows());
    }

    [PostgresFact]
    public async Task Automatic_codes_follow_the_parent_with_the_configured_segment_width_and_never_collide()
    {
        await PrepareChart();
        var assets = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root(null, "asset")));
        Assert.Equal("1", assets.Code);                                                                                              // the default root of the type
        Assert.Equal("2", Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root(null, "liability"))).Code);
        Assert.Equal("1", assets.Code);
        var second = Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root(null, "asset")));
        Assert.Equal("6", second.Code);                                                                                              // the default is taken: the next free number
        var c1 = await NewChild(assets, null, group: true);
        var c2 = await NewChild(assets, null);
        Assert.Equal(["101", "102"], new[] { c1.Code, c2.Code });                                                                    // width 2 by default
        Assert.Equal("10101", (await NewChild(c1, null)).Code);
        Ok(await Chart().PutSettingsAsync(Admin.Id, Company.Id, 0, new ChartSettingsInput(3, true)));
        Assert.Equal("1003", (await NewChild(assets, null)).Code);                                                                   // width 3 from now on: "1" + "003"
        // A manual code that occupies the next automatic number is skipped, never duplicated.
        Ok(await Chart().PutSettingsAsync(Admin.Id, Company.Id, 1, new ChartSettingsInput(2, true)));
        var group = await NewChild(second, null, group: true);
        await NewChild(group, "60101X");
        Assert.NotEqual("60101X", (await NewChild(group, null)).Code);
    }

    [PostgresFact]
    public async Task Automatic_assignment_can_be_switched_off_and_then_a_code_is_required()
    {
        await PrepareChart();
        Ok(await Chart().PutSettingsAsync(Admin.Id, Company.Id, 0, new ChartSettingsInput(2, false)));
        var r = await Chart().CreateAsync(Admin.Id, Company.Id, Root(null, "asset"));
        Assert.Equal(ConfigStatus.Invalid, r.Status);
        Assert.Contains(r.Fields!, e => e is { Field: "code", Code: "required" });
        Assert.Equal("77", Ok(await Chart().CreateAsync(Admin.Id, Company.Id, Root("77", "asset"))).Code);                          // manual codes always work
    }

    [PostgresFact]
    public async Task Parents_types_and_currency_policies_are_validated_against_the_stored_chart()
    {
        await PrepareChart();
        var assets = await NewRoot("1");
        var leaf = await NewChild(assets, "101");
        var chart = Chart();
        Assert.Equal("parent_not_group", (await chart.CreateAsync(Admin.Id, Company.Id, Child(leaf.Id, "10101"))).Code);
        Assert.Equal("validation_failed", (await chart.CreateAsync(Admin.Id, Company.Id, Child(Guid.NewGuid(), "X1"))).Code);
        var mismatch = await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X2", type: "liability"));
        Assert.Equal(ConfigStatus.Invalid, mismatch.Status);
        Assert.Contains(mismatch.Fields!, e => e is { Field: "type", Code: "parent_type_mismatch" });
        Assert.Contains((await chart.CreateAsync(Admin.Id, Company.Id, Root("X3", null!))).Fields!, e => e is { Field: "type", Code: "required" });   // a root needs a type
        Assert.Equal(ConfigStatus.Invalid, (await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X4", policy: "specific"))).Status);          // specific needs a code
        Assert.Equal(ConfigStatus.Invalid, (await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X5", policy: "base", currency: "USD"))).Status);
        var notEnabled = await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X6", policy: "specific", currency: "USD"));
        Assert.Contains(notEnabled.Fields!, e => e is { Field: "currencyCode", Code: "currency_not_enabled" });
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        var sar = Ok(await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X7", policy: "specific", currency: "sar")));
        Assert.Equal((AccountCurrencyPolicy.Specific, "SAR"), (sar.CurrencyPolicy, sar.CurrencyCode));
        Assert.Equal(AccountCurrencyPolicy.Any, Ok(await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X8", policy: "any"))).CurrencyPolicy);
        Assert.Equal(AccountCurrencyPolicy.Base, Ok(await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X9"))).CurrencyPolicy);          // the default
        Assert.Equal(AccountNature.Credit, Ok(await chart.CreateAsync(Admin.Id, Company.Id, Child(assets.Id, "X10", nature: "credit"))).Nature);      // a contra account
    }

    [PostgresFact]
    public async Task Update_changes_names_and_attributes_with_revision_audit_and_optimistic_concurrency()
    {
        await PrepareChart();
        var a = await NewChild(await NewRoot("1"), "101");
        var chart = Chart();
        Assert.Equal(ConfigStatus.PreconditionRequired, (await chart.UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, null, new AccountDetailsInput("ا", "A", null, null, null, null))).Status);
        var renamed = Ok(await chart.UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, new AccountDetailsInput("الصندوق", "Cash", "debit", false, null, null)));
        Assert.Equal((2L, "Cash"), (renamed.Revision, renamed.NameEn));
        var stale = await chart.UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, new AccountDetailsInput("ا", "B", null, null, null, null));
        Assert.Equal((ConfigStatus.PreconditionFailed, 2L), (stale.Status, stale.CurrentRevision));
        var before = await Count(Company, Audit_.Table);
        Assert.Equal(2L, Ok(await chart.UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 2, new AccountDetailsInput("الصندوق", "Cash", "debit", false, null, null))).Revision);   // unchanged: nothing written
        Assert.Equal(before, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigStatus.Invalid, (await chart.UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 2, new AccountDetailsInput(null, null, null, null, null, null))).Status);
        Assert.Equal(ConfigStatus.NotFound, (await chart.UpdateDetailsAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, new AccountDetailsInput("ا", "A", null, null, null, null))).Status);
        Assert.Equal(ConfigAuditEvents.AccountUpdated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        // A group with children cannot become a non-group.
        var root = (await chart.ListAsync(Company.Id)).Single(x => x.Code == "1");
        Assert.Equal("account_has_children", (await chart.UpdateDetailsAsync(Admin.Id, Company.Id, root.Id, 1, new AccountDetailsInput("أ", "Assets", null, false, null, null))).Code);
    }

    [PostgresFact]
    public async Task Attributes_that_could_invalidate_history_are_frozen_by_posting_history_and_by_unverifiable_history()
    {
        await PrepareChart();
        var a = await NewChild(await NewRoot("1"), "101");
        var change = new AccountDetailsInput("ا", "A", "credit", null, null, null);
        // A ledger with data whose usage probe is still a placeholder: nobody can tell, so the protected change is refused.
        var ledgerWithData = new FinancialHistoryGuard([new LedgerHistoryProbe("general_ledger", true)]);
        var unverifiable = await Chart(history: ledgerWithData).UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, change);
        Assert.Equal((ConfigStatus.Conflict, "account_usage_checks_not_implemented"), (unverifiable.Status, unverifiable.Code));
        // With a real probe: postings freeze it; none leaves it free. Names (not protected) are never frozen.
        var withPostings = new AccountUsageGuard([new PostingsProbe(true)]);
        var frozen = await Chart(withPostings, ledgerWithData).UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, change);
        Assert.Equal("account_has_postings", frozen.Code);
        Ok(await Chart(withPostings, ledgerWithData).UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, new AccountDetailsInput("اسم جديد", "Renamed", null, null, null, null)));
        var free = Ok(await Chart(new AccountUsageGuard([new PostingsProbe(false)]), ledgerWithData).UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 2, change));
        Assert.Equal(AccountNature.Credit, free.Nature);
        // Without any financial history the placeholder is harmless.
        Assert.Equal(AccountNature.Debit, Ok(await Chart().UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 3, new AccountDetailsInput("ا", "A", "debit", null, null, null))).Nature);
    }

    [PostgresFact]
    public async Task Recoding_is_a_controlled_audited_operation_that_keeps_the_stable_id_and_the_history()
    {
        await PrepareChart();
        var a = await NewChild(await NewRoot("1"), "101");
        var other = await NewChild((await Chart().ListAsync(Company.Id)).Single(x => x.Code == "1"), "102");
        var chart = Chart();
        Assert.Equal(ConfigStatus.PreconditionRequired, (await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, null, new AccountCodeInput("200"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, 1, new AccountCodeInput("A B"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, 1, new AccountCodeInput(" "))).Status);
        var taken = await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, 1, new AccountCodeInput(other.Code.ToLowerInvariant()));
        Assert.Equal((ConfigStatus.Conflict, "account_code_taken"), (taken.Status, taken.Code));
        var recoded = Ok(await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, 1, new AccountCodeInput("٢٠٠")));
        Assert.Equal((a.Id, "200", 2L), (recoded.Id, recoded.Code, recoded.Revision));
        Assert.Equal(ConfigStatus.PreconditionFailed, (await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, 1, new AccountCodeInput("300"))).Status);
        Assert.Equal(2L, Ok(await chart.RecodeAsync(Admin.Id, Company.Id, a.Id, 2, new AccountCodeInput("200"))).Revision);          // same code: no-op
        Assert.Contains("101", await Sql<string>($"SELECT {Ar.Changes}::text FROM {Ar.Table} WHERE {Ar.EntityId} = '{a.Id}' AND {Ar.Revision} = 2"));   // the old code stays in the history
        Assert.Equal(ConfigAuditEvents.AccountCodeChanged, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Moving_an_account_keeps_the_tree_valid_and_refuses_cycles_wrong_types_and_non_groups()
    {
        await PrepareChart();
        var assets = await NewRoot("1");
        var current = await NewChild(assets, "11", group: true);
        var fixedAssets = await NewChild(assets, "12", group: true);
        var cash = await NewChild(current, "1101");
        var liabilities = await NewRoot("2", "liability");
        var chart = Chart();
        var moved = Ok(await chart.MoveAsync(Admin.Id, Company.Id, cash.Id, 1, new AccountMoveInput(fixedAssets.Id)));
        Assert.Equal((fixedAssets.Id, 2L), (moved.ParentId, moved.Revision));
        Assert.Equal("parent_not_group", (await chart.MoveAsync(Admin.Id, Company.Id, assets.Id, 1, new AccountMoveInput(cash.Id))).Code);   // a non-group cannot be a parent at all
        Assert.Equal("account_cycle", (await chart.MoveAsync(Admin.Id, Company.Id, assets.Id, 1, new AccountMoveInput(assets.Id))).Code);
        Assert.Equal("parent_not_group", (await chart.MoveAsync(Admin.Id, Company.Id, current.Id, 1, new AccountMoveInput(cash.Id))).Code);
        Assert.Equal("parent_type_mismatch", (await chart.MoveAsync(Admin.Id, Company.Id, current.Id, 1, new AccountMoveInput(liabilities.Id))).Code);
        Assert.Equal("account_cycle", (await chart.MoveAsync(Admin.Id, Company.Id, assets.Id, 1, new AccountMoveInput(fixedAssets.Id))).Code);   // a group under its own child
        Assert.Equal(ConfigStatus.Invalid, (await chart.MoveAsync(Admin.Id, Company.Id, current.Id, 1, new AccountMoveInput(Guid.NewGuid()))).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await chart.MoveAsync(Admin.Id, Company.Id, cash.Id, 1, new AccountMoveInput(current.Id))).Status);
        var toRoot = Ok(await chart.MoveAsync(Admin.Id, Company.Id, fixedAssets.Id, 1, new AccountMoveInput(null)));                   // to the top level (its subtree moves with it)
        Assert.Null(toRoot.ParentId);
        Assert.Equal(fixedAssets.Id, (await chart.GetAsync(Company.Id, cash.Id))!.ParentId);
        Assert.Equal(2L, Ok(await chart.MoveAsync(Admin.Id, Company.Id, cash.Id, 2, new AccountMoveInput(fixedAssets.Id))).Revision);   // unchanged: no-op returns the current account
        Assert.Equal(ConfigAuditEvents.AccountMoved, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Deactivation_keeps_history_and_the_activity_rules_follow_the_tree()
    {
        await PrepareChart();
        var root = await NewRoot("1");
        var leaf = await NewChild(root, "101");
        var chart = Chart();
        Assert.Equal("account_has_active_children", (await chart.DeactivateAsync(Admin.Id, Company.Id, root.Id, 1)).Code);
        var off = Ok(await chart.DeactivateAsync(Admin.Id, Company.Id, leaf.Id, 1));
        Assert.False(off.IsActive);
        Assert.False(off.IsPostable);                                                                                                // an inactive account is never postable
        Assert.Equal("account_already_inactive", (await chart.DeactivateAsync(Admin.Id, Company.Id, leaf.Id, off.Revision)).Code);
        Ok(await chart.DeactivateAsync(Admin.Id, Company.Id, root.Id, 1));
        Assert.Equal("parent_inactive", (await chart.ReactivateAsync(Admin.Id, Company.Id, leaf.Id, off.Revision)).Code);
        Assert.Equal("parent_inactive", (await chart.CreateAsync(Admin.Id, Company.Id, Child(root.Id, "102"))).Code);
        var rootOn = Ok(await chart.ReactivateAsync(Admin.Id, Company.Id, root.Id, 2));
        Assert.Equal("account_already_active", (await chart.ReactivateAsync(Admin.Id, Company.Id, root.Id, rootOn.Revision)).Code);
        Assert.True(Ok(await chart.ReactivateAsync(Admin.Id, Company.Id, leaf.Id, off.Revision)).IsPostable);
        Assert.Equal(2L, await AccountRows());                                                                                       // nothing was deleted
        Assert.Equal(ConfigStatus.PreconditionRequired, (await chart.DeactivateAsync(Admin.Id, Company.Id, leaf.Id, null)).Status);
    }

    [PostgresFact]
    public async Task The_chart_settings_use_optimistic_concurrency_and_are_validated_and_audited()
    {
        await PrepareChart();
        var chart = Chart();
        Assert.Equal(ConfigStatus.PreconditionRequired, (await chart.PutSettingsAsync(Admin.Id, Company.Id, null, new ChartSettingsInput(2, true))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await chart.PutSettingsAsync(Admin.Id, Company.Id, 0, new ChartSettingsInput(0, true))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await chart.PutSettingsAsync(Admin.Id, Company.Id, 0, new ChartSettingsInput(5, true))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await chart.PutSettingsAsync(Admin.Id, Company.Id, 0, new ChartSettingsInput(2, null))).Status);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await chart.PutSettingsAsync(Admin.Id, Company.Id, 4, new ChartSettingsInput(2, true))).Status);
        var created = Ok(await chart.PutSettingsAsync(Admin.Id, Company.Id, 0, new ChartSettingsInput(3, false)));
        Assert.Equal(new ChartSettingsView(3, false, 1, true), created);
        Assert.Equal(new ChartSettingsView(4, false, 2, true), Ok(await chart.PutSettingsAsync(Admin.Id, Company.Id, 1, new ChartSettingsInput(4, false))));
        Assert.Equal(2L, Ok(await chart.PutSettingsAsync(Admin.Id, Company.Id, 2, new ChartSettingsInput(4, false))).Revision);       // unchanged: no-op
        Assert.Equal(ConfigAuditEvents.ChartSettingsChanged, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        Assert.Equal(AuditBaseline + 2, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task The_generic_template_creates_a_valid_bilingual_chart_once_on_an_empty_chart_with_one_audit_event()
    {
        await PrepareChart();
        var chart = Chart();
        Assert.Equal(ConfigStatus.NotFound, (await chart.ApplyTemplateAsync(Admin.Id, Company.Id, "restaurant")).Status);               // business-type templates are not delivered yet
        var all = Ok(await chart.ApplyTemplateAsync(Admin.Id, Company.Id, " Generic "));
        Assert.Equal(ChartTemplates.Find("generic")!.Accounts.Count, all.Count);
        Assert.All(all, a => Assert.Equal("generic", a.TemplateKey));
        Assert.All(all, a => Assert.True(a.NameAr is not null && a.NameEn is not null));                                              // bilingual
        Assert.Equal(["1", "2", "3", "4", "5"], all.Where(a => a.ParentId is null).Select(a => a.Code).Order());
        Assert.Equal([AccountType.Asset, AccountType.Liability, AccountType.Equity, AccountType.Revenue, AccountType.Expense], all.Where(a => a.ParentId is null).OrderBy(a => a.Code).Select(a => a.Type));
        var byId = all.ToDictionary(a => a.Id);
        Assert.All(all.Where(a => a.ParentId is not null), a => { Assert.True(byId[a.ParentId!.Value].IsGroup); Assert.Equal(byId[a.ParentId.Value].Type, a.Type); });
        Assert.All(all.Where(a => !a.IsGroup), a => Assert.True(a.IsPostable));
        Assert.Equal(AccountNature.Credit, all.Single(a => a.Code == "1202").Nature);                                                 // accumulated depreciation: a contra asset
        Assert.Equal(AccountNature.Debit, all.Single(a => a.Code == "4102").Nature);                                                  // sales returns: a contra revenue
        Assert.Equal(AuditBaseline + 1, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigAuditEvents.ChartTemplateApplied, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table}"));
        Assert.Equal(1L, await Count(Company, Ar.Table));
        var again = await chart.ApplyTemplateAsync(Admin.Id, Company.Id, "generic");
        Assert.Equal((ConfigStatus.Conflict, "chart_not_empty"), (again.Status, again.Code));
        Assert.Equal(all.Count, (int)await AccountRows());
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task A_template_is_refused_on_a_chart_that_already_has_accounts_and_applied_accounts_remain_ordinary()
    {
        await PrepareChart();
        await NewRoot("9");
        Assert.Equal("chart_not_empty", (await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "generic")).Code);
        Assert.Equal(1L, await AccountRows());
    }

    [PostgresFact]
    public async Task Template_accounts_can_be_renamed_recoded_moved_and_deactivated_like_any_other()
    {
        await PrepareChart();
        var all = Ok(await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "generic"));
        var cash = all.Single(a => a.Code == "1101");
        var renamed = Ok(await Chart().UpdateDetailsAsync(Admin.Id, Company.Id, cash.Id, 1, new AccountDetailsInput("نقدية", "Cash", null, null, null, null)));
        Ok(await Chart().RecodeAsync(Admin.Id, Company.Id, cash.Id, renamed.Revision, new AccountCodeInput("1110")));
        var fixedAssets = all.Single(a => a.Code == "12");
        Ok(await Chart().MoveAsync(Admin.Id, Company.Id, cash.Id, 3, new AccountMoveInput(fixedAssets.Id)));
        Ok(await Chart().DeactivateAsync(Admin.Id, Company.Id, cash.Id, 4));
        var final = (await Chart().GetAsync(Company.Id, cash.Id))!;
        Assert.Equal(("1110", fixedAssets.Id, false, "generic"), (final.Code, final.ParentId, final.IsActive, final.TemplateKey));
    }

    [PostgresFact]
    public async Task Concurrent_requests_resolve_to_one_consistent_tree_and_one_winner_per_code()
    {
        await PrepareChart();
        var a = await NewRoot("1");
        var b = await NewChild(a, "11", group: true);
        // The same manual code created concurrently: exactly one wins.
        var creates = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Chart().CreateAsync(Admin.Id, Company.Id, Child(a.Id, "DUP"))));
        Assert.Single(creates, r => r.IsOk);
        Assert.All(creates.Where(r => !r.IsOk), r => Assert.Equal("account_code_taken", r.Code));
        // Automatic codes assigned concurrently never collide.
        var autos = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Chart().CreateAsync(Admin.Id, Company.Id, Child(b.Id, null))));
        Assert.All(autos, r => Assert.True(r.IsOk, r.Code));
        Assert.Equal(6, autos.Select(r => r.Value!.Code).Distinct().Count());
        // Two opposite moves racing (each would put one group under the other): the lock serialises them and exactly one cycle-free outcome remains.
        var x = await NewChild(a, "X", group: true);
        var y = await NewChild(a, "Y", group: true);
        var moves = await Task.WhenAll(
            Chart().MoveAsync(Admin.Id, Company.Id, x.Id, 1, new AccountMoveInput(y.Id)),
            Chart().MoveAsync(Admin.Id, Company.Id, y.Id, 1, new AccountMoveInput(x.Id)));
        Assert.Single(moves, r => r.IsOk);
        var cycles = await Sql<long>($"WITH RECURSIVE t(id, root, depth) AS (SELECT {Ac.Id}, {Ac.Id}, 0 FROM {Ac.Table} UNION ALL SELECT a.{Ac.Id}, t.root, t.depth + 1 FROM {Ac.Table} a JOIN t ON a.{Ac.ParentId} = t.id WHERE t.depth < 100) SELECT count(*) FROM t WHERE t.id = t.root AND t.depth > 0");
        Assert.Equal(0L, cycles);
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Creating_and_organizing_accounts_never_activates_the_financial_history_locks()
    {
        await PrepareChart();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        Ok(await Chart().ApplyTemplateAsync(Admin.Id, Company.Id, "generic"));
        // The base currency and the business time zone remain changeable: accounts are master data, not posted financial history.
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 1, new SettingsInput("en", "yyyy-MM-dd", "latin", "Asia/Riyadh")));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, true));
        var locks = Ok(await Svc.GetCurrenciesAsync(Admin.Id, Company.Id)).BaseCurrencyLock;
        Assert.False(locks.Locked);
        Assert.Contains("general_ledger", locks.UnimplementedDomains);                                                                 // still reported as unverified, never as safe
    }
}
