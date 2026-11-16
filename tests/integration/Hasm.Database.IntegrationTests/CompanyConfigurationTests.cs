using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Cfg = Hasm.Infrastructure.CompanyDatabase.CompanySchema;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Database.IntegrationTests;

/// <summary>A test-only policy that lifts the Trial bound so the multi-currency rules themselves can be exercised. Never registered in the API.</summary>
internal sealed class ThreeCurrencyTestPolicy : IEntitlementPolicy
{
    public string Edition => "test";
    public int MaxEnabledCurrencies => 3;
    public IReadOnlyList<TrialRestriction> Restrictions => [];
}

internal sealed class HistoryProbe(bool history) : IFinancialHistoryProbe
{
    public string Domain => "general_ledger";
    public bool Implemented => true;
    public Task<bool> HasHistoryAsync(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(history);
}

/// <summary>Company configuration (migration 0003, system migration 0006) against real PostgreSQL: schema, services, RBAC, audit, projection.</summary>
public abstract class ConfigurationTestBase : CompanyRuntimeTestBase
{
    protected AccessService Access { get; private set; } = null!;
    protected CompanyRecord Company { get; private set; } = null!;
    protected UserRecord Admin { get; private set; } = null!;
    protected CompanyConfigurationService Svc { get; private set; } = null!;
    private int people;

    protected CompanyDisplayNameProjection Projection => new(new CompanyRegistry(Options.BuildSystemConnectionString(admin: true)));

    protected CompanyConfigurationService MakeService(CompanyRecord forCompany, IEntitlementPolicy? policy = null, FinancialHistoryGuard? guard = null, CompanyDisplayNameProjection? projection = null)
    {
        var resolver = SinglePrincipalResolver(forCompany);
        return new CompanyConfigurationService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), Access,
            policy ?? TrialEntitlementPolicy.Instance, guard ?? new FinancialHistoryGuard(), projection ?? Projection, Clock);
    }

    protected async Task Prepare(IEntitlementPolicy? policy = null, FinancialHistoryGuard? guard = null)
    {
        await StartCompanies();
        Access = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, PermissionCatalog.Default, Audit);
        Company = await ReadyCompany();
        Admin = await NewAdminOf(Company);
        Svc = MakeService(Company, policy, guard);
    }

    protected async Task<UserRecord> NewAdminOf(CompanyRecord company)
    {
        var user = await NewUser($"cfgadmin{Interlocked.Increment(ref people)}");
        Assert.Equal(AccessService.BootstrapStatus.Granted, await Access.BootstrapCompanyAdministratorAsync(user.Login, company.Id));
        return user;
    }

    /// <summary>A member holding exactly the given permissions through a custom company role.</summary>
    protected async Task<UserRecord> MemberWith(CompanyRecord company, UserRecord adminOfCompany, params string[] permissions)
    {
        var user = await NewUser($"cfguser{Interlocked.Increment(ref people)}");
        Assert.True((await Access.GrantMembershipAsync(adminOfCompany.Id, company.Id, user.Login)).IsOk);
        if (permissions.Length > 0)
        {
            var role = await Access.CreateRoleAsync(adminOfCompany.Id, company.Id, $"role{Interlocked.Increment(ref people)}", permissions);
            Assert.True(role.IsOk, role.Code);
            Assert.True((await Access.AssignRoleAsync(adminOfCompany.Id, company.Id, user.Id, role.Value!.Id, ScopeKind.Company, null)).IsOk);
        }
        return user;
    }

    protected static ProfileInput Profile(string? nameAr = "شركة الأمل", string? nameEn = null, string? country = "SA", string? tax = null, string? registration = null, string? city = null) =>
        new(nameAr, nameEn, null, registration, tax, country, null, null, city, null, null, null, null, null);

    protected static T Ok<T>(ConfigResult<T> r) { Assert.True(r.IsOk, $"{r.Status} {r.Code}"); return r.Value!; }

    protected Task<long> Count(CompanyRecord c, string table) => InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {table}");

    protected AuditVerifier Verifier(CompanyRecord c) => new(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName), AuditKeys, AuditCheckpoints);

    public override async Task DisposeAsync()
    {
        if (Access is not null) await Access.DisposeAsync();
        await base.DisposeAsync();
    }
}

public class CompanyConfigurationMigrationTests : ConfigurationTestBase
{
    [PostgresFact]
    public async Task Fresh_provisioning_creates_the_configuration_schema_empty_with_the_currency_reference_seeded()
    {
        await Prepare();
        Assert.Equal(18L, await Count(Company, SystemSchema.MigrationJournalTable));   // 0001-0005 (0004 branches 3.1B, 0005 warehouses 3.1C)
        foreach (var t in new[] { Cfg.Profile.Table, Cfg.Settings.Table, Cfg.CompanyCurrency.Table, Cfg.ConfigRevision.Table })
            Assert.Equal(0L, await Count(Company, t));                                         // nothing is seeded or created by a read
        Assert.True(await Count(Company, Cfg.Currency.Table) >= 150);
        Assert.Equal(1L, await InCompany<long>(Company.PhysicalDatabaseName, $"SELECT count(*) FROM {Cfg.Currency.Table} WHERE {Cfg.Currency.Code} = 'XDR' AND {Cfg.Currency.MinorUnits} IS NULL")); // undefined exponent is NULL, never invented
        Assert.Equal(3, await InCompany<short>(Company.PhysicalDatabaseName, $"SELECT {Cfg.Currency.MinorUnits} FROM {Cfg.Currency.Table} WHERE {Cfg.Currency.Code} = 'KWD'"));
        Assert.Equal(0L, await InCompany<long>(Company.PhysicalDatabaseName, "SELECT count(*) FROM information_schema.columns WHERE column_name ILIKE '%decimal%' OR column_name ILIKE '%round%'"));
    }

    [PostgresFact]
    public async Task A_company_provisioned_before_this_phase_upgrades_and_keeps_its_data()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 2)]);
        await InCompany(legacy.PhysicalDatabaseName, "CREATE TABLE zz_existing (a int); INSERT INTO zz_existing VALUES (7)");
        Assert.Equal(0L, await InCompany<long>(legacy.PhysicalDatabaseName, "SELECT count(*) FROM information_schema.tables WHERE table_name = '" + Cfg.Profile.Table + "'"));

        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18], outcome.Applied);
        Assert.Equal(7, await InCompany<int>(legacy.PhysicalDatabaseName, "SELECT a FROM zz_existing"));
        Assert.True(await Count(legacy, Cfg.Currency.Table) >= 150);
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
    }

    [PostgresFact]
    public async Task System_migration_0006_adds_the_projection_columns_and_the_preset_permissions()
    {
        await Prepare();
        Assert.Equal(0L, await Scalar<long>($"SELECT {Co.DisplayNameRevision} FROM {Co.Table} WHERE {Co.Id} = @i", ("i", Company.Id)));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Co.Table} WHERE {Co.Id} = @i AND {Co.DisplayNameAr} IS NULL AND {Co.DisplayNameEn} IS NULL", ("i", Company.Id)) - 1);
        var roles = (await Access.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        string[] company = ["company.profile.view", "company.profile.update", "company.settings.view", "company.settings.update", "company.currencies.view", "company.currencies.update", "company.config.view"];
        Assert.True(company.All(roles.Single(r => r.PresetKey == "company-administrator").Permissions.Contains));
        Assert.Equal(["company.config.view"], roles.Single(r => r.PresetKey == "access-auditor").Permissions.Where(p => p.StartsWith("company.")));
        Assert.DoesNotContain(roles.Single(r => r.PresetKey == "access-manager").Permissions, p => p.StartsWith("company."));
    }

    [PostgresFact]
    public async Task The_database_itself_refuses_to_alter_history_delete_rows_or_skip_revisions()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        string[] forbidden =
        [
            $"UPDATE {Cfg.ConfigRevision.Table} SET {Cfg.ConfigRevision.EntityKey} = 'x'",
            $"DELETE FROM {Cfg.ConfigRevision.Table}",
            $"TRUNCATE {Cfg.ConfigRevision.Table}",
            $"DELETE FROM {Cfg.Profile.Table}",
            $"TRUNCATE {Cfg.Profile.Table}",
            $"UPDATE {Cfg.Profile.Table} SET {Cfg.Profile.Revision} = {Cfg.Profile.Revision} + 2",
            $"UPDATE {Cfg.Profile.Table} SET {Cfg.Profile.NameAr} = NULL, {Cfg.Profile.NameEn} = NULL, {Cfg.Profile.Revision} = {Cfg.Profile.Revision} + 1",
            $"DELETE FROM {Cfg.CompanyCurrency.Table}",
            $"UPDATE {Cfg.CompanyCurrency.Table} SET {Cfg.CompanyCurrency.Code} = 'USD'",
            $"UPDATE {Cfg.CompanyCurrency.Table} SET {Cfg.CompanyCurrency.Enabled} = false, {Cfg.CompanyCurrency.Revision} = {Cfg.CompanyCurrency.Revision} + 1",   // the base cannot be disabled
            $"INSERT INTO {Cfg.CompanyCurrency.Table} VALUES ('USD', true, true, 1, now())",                                                                        // a second base
            $"DELETE FROM {Cfg.Currency.Table} WHERE {Cfg.Currency.Code} = 'USD'",
            $"UPDATE {Cfg.Currency.Table} SET {Cfg.Currency.Code} = 'ZZZ' WHERE {Cfg.Currency.Code} = 'USD'",
            $"INSERT INTO {Cfg.ConfigRevision.Table} ({Cfg.ConfigRevision.Entity}, {Cfg.ConfigRevision.EntityRevision}, {Cfg.ConfigRevision.ActorUserId}, {Cfg.ConfigRevision.ChangedAtUtc}, {Cfg.ConfigRevision.Changes}, {Cfg.ConfigRevision.AuditStream}, {Cfg.ConfigRevision.AuditSeq}) " +
                "VALUES ('profile', 9, gen_random_uuid(), now(), '{}', 'business:x', 999)",                                                                          // no such audit event
        ];
        foreach (var sql in forbidden)
            await Assert.ThrowsAnyAsync<PostgresException>(() => InCompany(Company.PhysicalDatabaseName, sql));
        Assert.Equal(1L, await Count(Company, Cfg.Profile.Table));
        Assert.Equal(2L, await Count(Company, Cfg.ConfigRevision.Table));
    }
}

public class CompanyProfileTests : ConfigurationTestBase
{
    [PostgresFact]
    public async Task Reading_never_creates_a_profile_and_reports_revision_zero()
    {
        await Prepare();
        var view = Ok(await Svc.GetProfileAsync(Admin.Id, Company.Id));
        Assert.Null(view.Data);
        Assert.Equal(0, view.Revision);
        Assert.Equal(0L, await Count(Company, Cfg.Profile.Table));
        Assert.Equal(0L, await Count(Company, Cfg.ConfigRevision.Table));
    }

    [PostgresFact]
    public async Task The_first_write_creates_the_profile_with_a_revision_and_an_authenticated_audit_event_in_one_transaction()
    {
        await Prepare();
        var view = Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(city: "Riyadh")));
        Assert.Equal(1, view.Revision);
        Assert.Equal("SA", view.Data!.CountryCode);
        Assert.Equal(1L, await Count(Company, Cfg.ConfigRevision.Table));
        // The revision is linked to exactly the audit event written by the same transaction.
        Assert.Equal(1L, await InCompany<long>(Company.PhysicalDatabaseName,
            $"SELECT count(*) FROM {Cfg.ConfigRevision.Table} r JOIN {CompanySchema.BusinessAudit.Table} a ON a.{CompanySchema.BusinessAudit.StreamKey} = r.{Cfg.ConfigRevision.AuditStream} AND a.{CompanySchema.BusinessAudit.Seq} = r.{Cfg.ConfigRevision.AuditSeq} " +
            $"WHERE a.{CompanySchema.BusinessAudit.EventType} = {ConfigAuditEvents.ProfileCreated} AND a.{CompanySchema.BusinessAudit.ActorUserId} = @u", ("u", Admin.Id)));
        var report = await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.Equal(AuditVerdict.Verified, report.Verdict);
        Assert.Equal(1, report.Events);
    }

    [PostgresFact]
    public async Task Updates_use_optimistic_concurrency()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Svc.PutProfileAsync(Admin.Id, Company.Id, null, Profile(city: "A"))).Status);
        var stale = await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(city: "A"));               // creating again: it already exists
        Assert.Equal(ConfigStatus.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.CurrentRevision);
        var updated = Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile(city: "Jeddah")));
        Assert.Equal(2, updated.Revision);
        var behind = await Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile(city: "Dammam"));
        Assert.Equal(ConfigStatus.PreconditionFailed, behind.Status);
        Assert.Equal(2, behind.CurrentRevision);
        Assert.Equal("Jeddah", Ok(await Svc.GetProfileAsync(Admin.Id, Company.Id)).Data!.City);              // the failed update changed nothing
        Assert.Equal(2L, await Count(Company, Cfg.ConfigRevision.Table));
    }

    [PostgresFact]
    public async Task An_identical_update_changes_nothing_and_writes_no_revision_or_audit_event()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        var same = Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile()));
        Assert.Equal(1, same.Revision);
        Assert.Equal(1L, await Count(Company, Cfg.ConfigRevision.Table));
        Assert.Equal(1L, await Count(Company, CompanySchema.BusinessAudit.Table));
    }

    [PostgresFact]
    public async Task Concurrent_first_writes_create_exactly_one_profile()
    {
        await Prepare();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(city: "c" + i))));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
        Assert.Equal(1L, await Count(Company, Cfg.Profile.Table));
        Assert.Equal(1L, await Count(Company, Cfg.ConfigRevision.Table));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Concurrent_updates_never_lose_a_write_silently()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile(city: "c" + i))));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(2L, await Count(Company, Cfg.ConfigRevision.Table));
    }

    [PostgresFact]
    public async Task Validation_rejects_bad_input_and_requires_one_name_and_a_country()
    {
        await Prepare();
        var none = await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(nameAr: null, nameEn: " "));
        Assert.Equal(ConfigStatus.Invalid, none.Status);
        Assert.Contains(none.Fields!, f => f.Field == "nameAr" && f.Code == "name_required");
        Assert.Contains((await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(country: null))).Fields!, f => f.Field == "countryCode" && f.Code == "required");
        Assert.Contains((await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(country: "ZZ"))).Fields!, f => f.Code == "unknown_country");
        Assert.Equal(0L, await Count(Company, Cfg.Profile.Table));                                           // nothing was stored
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(nameAr: null, nameEn: "Only English", country: "de")));   // one name is enough; country normalised
        Assert.Equal("DE", Ok(await Svc.GetProfileAsync(Admin.Id, Company.Id)).Data!.CountryCode);
    }

    [PostgresFact]
    public async Task International_identifiers_are_accepted_unchanged()
    {
        await Prepare();
        const string tax = "DE 123.456.789-ÄÖ/x", reg = "HRB 12345 B (München)";
        var v = Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(tax: tax, registration: reg, country: "DE")));
        Assert.Equal(tax, v.Data!.TaxRegistrationNo);
        Assert.Equal(reg, v.Data.CommercialRegistrationNo);
    }

    [PostgresFact]
    public async Task Sensitive_values_never_enter_the_audit_event_or_the_history_while_permitted_fields_keep_before_and_after()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(city: "Riyadh", tax: "TAX-SECRET-111")));
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile(city: "Jeddah", tax: "TAX-SECRET-222", registration: "REG-SECRET-333")));
        var history = Ok(await Svc.GetHistoryAsync(Admin.Id, Company.Id, null, 50, null));
        Assert.Equal(2, history.Count);
        var all = string.Join(" ", history.Select(h => h.ChangesJson));
        Assert.DoesNotContain("SECRET", all);
        Assert.Contains("\"Riyadh\"", all);
        Assert.Contains("\"Jeddah\"", all);
        Assert.Contains("taxRegistrationNo", all);                                                          // the fact of the change is recorded
        var details = await InCompany<string>(Company.PhysicalDatabaseName, $"SELECT string_agg(coalesce({CompanySchema.BusinessAudit.Detail}, ''), ' ') FROM {CompanySchema.BusinessAudit.Table}");
        Assert.DoesNotContain("SECRET", details);
        Assert.DoesNotContain("Riyadh", details);
        Assert.Contains("taxRegistrationNo", details);
    }

    [PostgresFact]
    public async Task When_the_audit_cannot_be_written_the_profile_is_not_changed()
    {
        await Prepare();
        AuditKeys.Unavailable = true;
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        AuditKeys.Unavailable = false;
        Assert.Equal(0L, await Count(Company, Cfg.Profile.Table));
        Assert.Equal(0L, await Count(Company, Cfg.ConfigRevision.Table));
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));                                  // and the next attempt works
        Assert.Equal(1L, await Count(Company, Cfg.Profile.Table));
    }

    [PostgresFact]
    public async Task Permissions_are_denied_by_default_and_separate_view_from_update()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        var nobody = await MemberWith(Company, Admin);
        var viewer = await MemberWith(Company, Admin, "company.profile.view");
        var stranger = await NewUser("stranger");

        foreach (var user in new[] { nobody, stranger })
        {
            Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetProfileAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Svc.PutProfileAsync(user.Id, Company.Id, 1, Profile(city: "x"))).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetSettingsAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetCurrenciesAsync(user.Id, Company.Id)).Status);
            Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetHistoryAsync(user.Id, Company.Id, null, 10, null)).Status);
        }
        Assert.True((await Svc.GetProfileAsync(viewer.Id, Company.Id)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.PutProfileAsync(viewer.Id, Company.Id, 1, Profile(city: "x"))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetHistoryAsync(viewer.Id, Company.Id, null, 10, null)).Status);      // history needs its own permission
        Assert.Equal(1, Ok(await Svc.GetProfileAsync(Admin.Id, Company.Id)).Revision);
    }

    [PostgresFact]
    public async Task One_company_never_sees_or_changes_another_companys_data()
    {
        await Prepare();
        var other = await ReadyCompany();
        var otherAdmin = await NewAdminOf(other);
        var otherSvc = MakeService(other);
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(nameAr: "الأولى", city: "A")));
        Ok(await otherSvc.PutProfileAsync(otherAdmin.Id, other.Id, 0, Profile(nameAr: "الثانية", city: "B")));

        // An administrator of one company is not authorized against the other company, whatever id is supplied.
        Assert.Equal(ConfigStatus.Forbidden, (await otherSvc.GetProfileAsync(Admin.Id, other.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetProfileAsync(otherAdmin.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.PutProfileAsync(otherAdmin.Id, Company.Id, 1, Profile(city: "hijack"))).Status);
        Assert.Equal("A", Ok(await Svc.GetProfileAsync(Admin.Id, Company.Id)).Data!.City);
        Assert.Equal("B", Ok(await otherSvc.GetProfileAsync(otherAdmin.Id, other.Id)).Data!.City);
        Assert.Equal(1L, await Count(Company, Cfg.Profile.Table));
        Assert.Equal(1L, await Count(other, Cfg.Profile.Table));
    }
}

public class CompanySettingsTests : ConfigurationTestBase
{
    [PostgresFact]
    public async Task Defaults_are_explicit_and_no_time_zone_is_invented()
    {
        await Prepare();
        var v = Ok(await Svc.GetSettingsAsync(Admin.Id, Company.Id));
        Assert.False(v.Exists);
        Assert.False(v.SetupComplete);
        Assert.Equal(0, v.Revision);
        Assert.Equal(new SettingsData("ar", "yyyy-MM-dd", "latin", null), v.Data);
        Assert.Equal(0L, await Count(Company, Cfg.Settings.Table));
        await Assert.ThrowsAsync<CompanyTimeZoneNotConfiguredException>(() => Svc.RequireBusinessTimeZoneAsync(Company.Id));
    }

    [PostgresFact]
    public async Task Saving_without_a_time_zone_keeps_setup_incomplete_and_a_selected_zone_enables_business_dates()
    {
        await Prepare();
        var first = Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "dd/MM/yyyy", "arabic-indic", null)));
        Assert.False(first.SetupComplete);
        Assert.Equal("en", first.Data.DefaultLanguage);
        await Assert.ThrowsAsync<CompanyTimeZoneNotConfiguredException>(() => Svc.RequireBusinessTimeZoneAsync(Company.Id));
        var second = Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 1, new SettingsInput("en", "dd/MM/yyyy", "arabic-indic", "UTC")));
        Assert.True(second.SetupComplete);
        Assert.Equal("UTC", (await Svc.RequireBusinessTimeZoneAsync(Company.Id)).Id);
        Assert.Equal(2L, await Count(Company, Cfg.ConfigRevision.Table));
    }

    [PostgresFact]
    public async Task Invalid_settings_are_rejected_and_stale_revisions_conflict()
    {
        await Prepare();
        var bad = await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("fr", "d/m/y", "roman", "Mars/Phobos"));
        Assert.Equal(ConfigStatus.Invalid, bad.Status);
        Assert.Equal(4, bad.Fields!.Count);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Svc.PutSettingsAsync(Admin.Id, Company.Id, null, new SettingsInput(null, null, null, null))).Status);
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput(null, null, null, "UTC")));      // missing values take the documented defaults
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput(null, null, null, "UTC"))).Status);
        Assert.Equal("ar", Ok(await Svc.GetSettingsAsync(Admin.Id, Company.Id)).Data.DefaultLanguage);
    }

    [PostgresFact]
    public async Task Changing_an_established_time_zone_is_refused_once_a_financial_history_probe_reports_history()
    {
        await Prepare(guard: new FinancialHistoryGuard([new HistoryProbe(true)]));
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput(null, null, null, null)));
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 1, new SettingsInput(null, null, null, "UTC")));    // first selection is allowed
        var locked = await Svc.PutSettingsAsync(Admin.Id, Company.Id, 2, new SettingsInput(null, null, null, "Europe/Paris"));
        Assert.Equal(ConfigStatus.Conflict, locked.Status);
        Assert.Equal("setting_locked", locked.Code);
        Assert.Equal("UTC", Ok(await Svc.GetSettingsAsync(Admin.Id, Company.Id)).Data.TimeZone);
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 2, new SettingsInput("en", null, null, "UTC")));    // unrelated fields stay editable
    }

    [PostgresFact]
    public async Task Without_financial_history_an_established_time_zone_can_change_and_is_recorded()
    {
        await Prepare();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput(null, null, null, "UTC")));
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 1, new SettingsInput(null, null, null, "Europe/Paris")));
        var history = Ok(await Svc.GetHistoryAsync(Admin.Id, Company.Id, "settings", 10, null));
        Assert.Contains("\"Europe/Paris\"", history[0].ChangesJson);
        Assert.Contains("\"UTC\"", history[0].ChangesJson);
    }
}

public class CompanyCurrencyTests : ConfigurationTestBase
{
    [PostgresFact]
    public async Task No_base_currency_exists_until_one_is_selected_and_the_first_enabled_currency_must_be_the_base()
    {
        await Prepare();
        var empty = Ok(await Svc.GetCurrenciesAsync(Admin.Id, Company.Id));
        Assert.False(empty.BaseCurrencySelected);
        Assert.Empty(empty.Currencies);
        Assert.False(empty.BaseCurrencyLock.ChecksImplemented);                                             // honestly reported: no financial-history checks exist yet
        Assert.Contains("general_ledger", empty.BaseCurrencyLock.UnimplementedDomains);
        var nonBase = await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, false);
        Assert.Equal(ConfigStatus.Invalid, nonBase.Status);
        Assert.Equal(0L, await Count(Company, Cfg.CompanyCurrency.Table));

        var view = Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        Assert.Equal("SAR", view.BaseCurrency);
        Assert.True(view.Currencies.Single().Enabled);
        Assert.Equal(1L, await Count(Company, Cfg.ConfigRevision.Table));
    }

    [PostgresFact]
    public async Task Trial_denies_enabling_an_additional_currency_and_changes_nothing()
    {
        await Prepare();
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        var denied = await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, false);
        Assert.Equal(ConfigStatus.EntitlementDenied, denied.Status);
        Assert.Equal(1L, await Count(Company, Cfg.CompanyCurrency.Table));
        Assert.Equal(1L, await Count(Company, Cfg.ConfigRevision.Table));
        Assert.Equal(1L, await Count(Company, CompanySchema.BusinessAudit.Table));
    }

    [PostgresFact]
    public async Task Under_Trial_replacing_the_base_currency_disables_the_old_one_and_the_base_is_never_disabled_or_unset()
    {
        await Prepare();
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        Assert.Equal(ConfigStatus.Invalid, (await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 1, false, false)).Status);   // cannot unset/disable
        Assert.Equal(ConfigStatus.Invalid, (await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 1, true, false)).Status);
        var view = Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, true));
        Assert.Equal("USD", view.BaseCurrency);
        var sar = view.Currencies.Single(c => c.Code == "SAR");
        Assert.False(sar.Enabled);
        Assert.False(sar.IsBase);
        Assert.Equal(1L, await InCompany<long>(Company.PhysicalDatabaseName, $"SELECT count(*) FROM {Cfg.CompanyCurrency.Table} WHERE {Cfg.CompanyCurrency.IsBase}"));
        Assert.Equal(3L, await Count(Company, Cfg.ConfigRevision.Table));                                       // SAR set, SAR demoted, USD set
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Additional_currencies_can_be_enabled_and_disabled_only_where_the_entitlement_allows_it()
    {
        await Prepare(policy: new ThreeCurrencyTestPolicy());
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        var usd = Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, false));
        Assert.Equal(2, usd.Currencies.Count(c => c.Enabled));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "EUR", 0, true, false));
        Assert.Equal(ConfigStatus.EntitlementDenied, (await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "GBP", 0, true, false)).Status);   // the policy bound still holds
        var off = Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 1, false, false));
        Assert.False(off.Currencies.Single(c => c.Code == "USD").Enabled);
        Assert.Equal("SAR", off.BaseCurrency);
    }

    [PostgresFact]
    public async Task A_stale_currency_revision_conflicts_and_unknown_codes_are_invalid()
    {
        await Prepare(policy: new ThreeCurrencyTestPolicy());
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, false));
        var stale = await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, false, false);
        Assert.Equal(ConfigStatus.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.CurrentRevision);
        Assert.Equal(ConfigStatus.Invalid, (await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "QQQ", 0, true, false)).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "usd", 0, true, false)).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", null, true, false)).Status);
    }

    [PostgresFact]
    public async Task The_base_currency_cannot_change_once_a_financial_history_probe_reports_history_but_can_be_selected_first()
    {
        await Prepare(guard: new FinancialHistoryGuard([new HistoryProbe(true)]));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));                           // establishing the base is not a change
        var locked = await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, true);
        Assert.Equal(ConfigStatus.Conflict, locked.Status);
        Assert.Equal("base_currency_locked", locked.Code);
        Assert.Equal("SAR", Ok(await Svc.GetCurrenciesAsync(Admin.Id, Company.Id)).BaseCurrency);
        Assert.True(Ok(await Svc.GetCurrenciesAsync(Admin.Id, Company.Id)).BaseCurrencyLock.Locked);
    }

    [PostgresFact]
    public async Task An_empty_company_with_no_financial_data_does_not_lock_the_base_and_unimplemented_checks_are_reported()
    {
        await Prepare();
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        var view = Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "USD", 0, true, true));
        Assert.Equal("USD", view.BaseCurrency);
        Assert.False(view.BaseCurrencyLock.Locked);
        Assert.False(view.BaseCurrencyLock.ChecksImplemented);
    }

    [PostgresFact]
    public async Task The_reference_list_and_permissions()
    {
        await Prepare();
        var reference = Ok(await Svc.GetCurrencyReferenceAsync(Admin.Id, Company.Id));
        Assert.Contains(reference, c => c.Code == "SAR" && c.MinorUnits == 2);
        Assert.Contains(reference, c => c.Code == "XDR" && c.MinorUnits is null);
        var viewer = await MemberWith(Company, Admin, "company.currencies.view");
        Assert.True((await Svc.GetCurrenciesAsync(viewer.Id, Company.Id)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.SetCurrencyAsync(viewer.Id, Company.Id, "SAR", 0, true, true)).Status);
        Assert.Equal(0L, await Count(Company, Cfg.CompanyCurrency.Table));
    }
}

public class CompanyNameProjectionTests : ConfigurationTestBase
{
    private Task<(string? Ar, string? En, long Rev)> Projected(Guid id) => Task.Run(async () =>
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {Co.DisplayNameAr}, {Co.DisplayNameEn}, {Co.DisplayNameRevision} FROM {Co.Table} WHERE {Co.Id} = @i", conn);
        cmd.Parameters.AddWithValue("i", id);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2));
    });

    [PostgresFact]
    public async Task A_profile_change_projects_both_names_with_its_revision()
    {
        await Prepare();
        var v = Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(nameAr: "الأمل", nameEn: "Amal")));
        Assert.Equal("synced", v.Projection);
        Assert.Equal(("الأمل", "Amal", 1L), await Projected(Company.Id));
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile(nameAr: "الأمل الجديد", nameEn: null)));
        Assert.Equal(("الأمل الجديد", null, 2L), await Projected(Company.Id));
    }

    [PostgresFact]
    public async Task A_delayed_projection_can_never_overwrite_newer_information()
    {
        await Prepare();
        var projection = Projection;
        Assert.True(await projection.TryProjectAsync(Company.Id, "جديد", "New", 5));
        Assert.True(await projection.TryProjectAsync(Company.Id, "قديم", "Old", 3));          // late arrival: accepted as a no-op
        Assert.True(await projection.TryProjectAsync(Company.Id, "قديم", "Old", 5));          // same revision: no-op
        Assert.Equal(("جديد", "New", 5L), await Projected(Company.Id));
        Assert.True(await projection.TryProjectAsync(Company.Id, "أحدث", "Newer", 6));
        Assert.Equal(("أحدث", "Newer", 6L), await Projected(Company.Id));
    }

    [PostgresFact]
    public async Task The_registry_itself_refuses_to_roll_a_projection_back_or_change_names_without_a_newer_revision()
    {
        await Prepare();
        Assert.True(await Projection.TryProjectAsync(Company.Id, "جديد", "New", 5));
        string upd(string set) => $"UPDATE {Co.Table} SET {set} WHERE {Co.Id} = '{Company.Id}'";
        await Assert.ThrowsAnyAsync<PostgresException>(() => Exec(upd($"{Co.DisplayNameRevision} = 4")));
        await Assert.ThrowsAnyAsync<PostgresException>(() => Exec(upd($"{Co.DisplayNameEn} = 'Tampered'")));            // same revision, different name
        await Assert.ThrowsAnyAsync<PostgresException>(() => Exec(upd($"{Co.DisplayNameEn} = 'Tampered', {Co.DisplayNameRevision} = 4")));
        await Exec(upd($"{Co.DisplayNameEn} = 'Newer', {Co.DisplayNameRevision} = 6"));                                  // a newer revision is fine
        Assert.Equal(("جديد", "Newer", 6L), await Projected(Company.Id));
    }

    [PostgresFact]
    public async Task A_projection_failure_is_reported_never_rolls_back_the_profile_and_is_recovered_later()
    {
        await Prepare();
        var broken = new CompanyDisplayNameProjection(new CompanyRegistry("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1;Pooling=false"));
        var failing = MakeService(Company, projection: broken);

        var saved = Ok(await failing.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(nameAr: "الأمل")));
        Assert.Equal("pending", saved.Projection);                                              // reported
        Assert.Equal(1L, await Count(Company, Cfg.Profile.Table));                              // the company change stands
        Assert.Equal(("", "", 0L), ((await Projected(Company.Id)).Ar ?? "", (await Projected(Company.Id)).En ?? "", (await Projected(Company.Id)).Rev));

        var healed = Ok(await Svc.GetProfileAsync(Admin.Id, Company.Id));                      // any later authorized read recovers it
        Assert.Equal("synced", healed.Projection);
        Assert.Equal(("الأمل", null, 1L), await Projected(Company.Id));

        var viaEndpoint = await failing.SyncDisplayNameAsync(Admin.Id, Company.Id);             // the explicit path reports pending while still broken
        Assert.Equal("pending", Ok(viaEndpoint).Projection);
        Assert.Equal("synced", Ok(await Svc.SyncDisplayNameAsync(Admin.Id, Company.Id)).Projection);
    }

    [PostgresFact]
    public async Task Only_members_see_the_company_name_in_the_selection_list()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile(nameAr: "الأمل", nameEn: "Amal Co")));
        var member = await MemberWith(Company, Admin);
        var outsider = await NewUser("outsider");

        var mine = await Access.ListAccessibleCompanySummariesAsync(member.Id);
        Assert.Equal(new CompanySummary(Company.Id, "الأمل", "Amal Co"), Assert.Single(mine));
        Assert.Empty(await Access.ListAccessibleCompanySummariesAsync(outsider.Id));
        Assert.Equal("Amal Co", CompanyNames.Display(mine[0].NameAr, mine[0].NameEn, "en"));
        Assert.Equal("الأمل", CompanyNames.Display(mine[0].NameAr, mine[0].NameEn, "ar"));
    }
}

public class CompanyHistoryTests : ConfigurationTestBase
{
    [PostgresFact]
    public async Task History_lists_newest_first_filters_by_entity_and_every_revision_verifies_against_the_audit_chain()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput(null, null, null, "UTC")));
        Ok(await Svc.SetCurrencyAsync(Admin.Id, Company.Id, "SAR", 0, true, true));
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 1, Profile(city: "Riyadh")));

        var all = Ok(await Svc.GetHistoryAsync(Admin.Id, Company.Id, null, 50, null));
        Assert.Equal(["profile", "currency", "settings", "profile"], all.Select(h => h.Entity));
        Assert.Equal([2L, 1L], Ok(await Svc.GetHistoryAsync(Admin.Id, Company.Id, "profile", 50, null)).Select(h => h.EntityRevision));
        Assert.Single(Ok(await Svc.GetHistoryAsync(Admin.Id, Company.Id, "currency", 50, null)));
        Assert.Equal(ConfigStatus.Invalid, (await Svc.GetHistoryAsync(Admin.Id, Company.Id, "bogus", 10, null)).Status);
        Assert.Single(Ok(await Svc.GetHistoryAsync(Admin.Id, Company.Id, null, 1, all[1].Id)), h => h.Entity == "settings");   // paging by id

        var report = await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.Equal(AuditVerdict.Verified, report.Verdict);
        Assert.Equal(4, report.Events);
        Assert.Equal(all.Count, all.Select(h => h.AuditSeq).Distinct().Count());                    // one audit event per revision
    }

    [PostgresFact]
    public async Task An_auditor_can_read_the_history_but_not_change_anything()
    {
        await Prepare();
        Ok(await Svc.PutProfileAsync(Admin.Id, Company.Id, 0, Profile()));
        var auditor = await NewUser("auditor");
        Assert.True((await Access.GrantMembershipAsync(Admin.Id, Company.Id, auditor.Login)).IsOk);
        var role = Ok2(await Access.ListRolesAsync(Admin.Id, Company.Id)).Single(r => r.PresetKey == "access-auditor");
        Assert.True((await Access.AssignRoleAsync(Admin.Id, Company.Id, auditor.Id, role.Id, ScopeKind.Company, null)).IsOk);
        Assert.Single(Ok(await Svc.GetHistoryAsync(auditor.Id, Company.Id, null, 10, null)));
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.GetProfileAsync(auditor.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await Svc.PutProfileAsync(auditor.Id, Company.Id, 1, Profile(city: "x"))).Status);
    }

    private static T Ok2<T>(AccessResult<T> r) { Assert.True(r.IsOk, r.Code); return r.Value!; }
}
