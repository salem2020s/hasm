using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Fy = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using Fp = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using Fh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalHistory;
using Fc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalCalendar;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Rq = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalReopenRequest;

namespace Hasm.Database.IntegrationTests;

/// <summary>A test-only close probe that blocks (stands in for a future module with pending items). Never registered in the API.</summary>
internal sealed class BlockingProbe(string domain, bool blocks) : IPeriodCloseProbe
{
    public string Domain => domain;
    public bool Implemented => true;
    public Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(blocks);
}

/// <summary>A test-only financial-history probe: stands in for a general ledger that already holds data. Never registered in the API.</summary>
internal sealed class LedgerHistoryProbe(string domain, bool has) : IFinancialHistoryProbe
{
    public string Domain => domain;
    public bool Implemented => true;
    public Task<bool> HasHistoryAsync(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(has);
}

/// <summary>Fiscal calendar (company migration 0006, Phase 3.1D) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class FiscalTestBase : BranchTestBase
{
    protected const string Open = "1";

    protected FiscalCalendarService Calendar(PeriodCloseGuard? guard = null, CompanyRecord? company = null, FinancialHistoryGuard? history = null)
    {
        var resolver = SinglePrincipalResolver(company ?? Company);
        return new FiscalCalendarService(resolver, new CompanyAuditedTransactions(resolver, Audit, Clock), guard ?? new PeriodCloseGuard(), history ?? new FinancialHistoryGuard(), Clock);
    }

    /// <summary>Prepares the company and chooses its business time zone (nothing is ever defaulted, and a fiscal year needs one).</summary>
    protected long AuditBaseline;

    protected async Task PrepareFiscal(string timeZone = "UTC")
    {
        await Prepare();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", timeZone)));
        AuditBaseline = await Count(Company, Audit_.Table);   // the settings change is itself audited
    }

    protected static FiscalYearInput Year(string name, int year, int month = 1, string type = "monthly") => new(name, new DateOnly(year, month, 1), type);

    protected async Task<FiscalYearView> NewYear(string name, int year, int month = 1, string type = "monthly") =>
        Ok(await Calendar().CreateYearAsync(Admin.Id, Company.Id, Year(name, year, month, type)));

    protected static string InsertYear(Guid id, string name, string start, string type = "1") =>
        $"INSERT INTO {Fy.Table} ({Fy.Id}, {Fy.Name}, {Fy.StartDate}, {Fy.EndDate}, {Fy.PeriodType}, {Fy.TimeZone}, {Fy.CreatedAtUtc}, {Fy.CreatedBy}) " +
        $"VALUES ('{id}', '{name}', '{start}', ('{start}'::date + interval '1 year' - interval '1 day')::date, {type}, 'UTC', now(), gen_random_uuid())";

    protected static string InsertPeriod(Guid year, int number, string start, string end) =>
        $"INSERT INTO {Fp.Table} ({Fp.YearId}, {Fp.Number}, {Fp.StartDate}, {Fp.EndDate}, {Fp.CreatedAtUtc}) VALUES ('{year}', {number}, '{start}', '{end}', now())";

    /// <summary>All 12 monthly period inserts of a year starting on the given date.</summary>
    protected static IEnumerable<string> MonthlyPeriods(Guid year, DateOnly start) =>
        Enumerable.Range(1, 12).Select(n => InsertPeriod(year, n, start.AddMonths(n - 1).ToString("yyyy-MM-dd"), start.AddMonths(n).AddDays(-1).ToString("yyyy-MM-dd")));

    protected static string SetPeriod(Guid id, string set) => $"UPDATE {Fp.Table} SET {set}, {Fp.Revision} = {Fp.Revision} + 1 WHERE {Fp.Id} = '{id}'";
    protected Task<long> YearRows() => Sql<long>($"SELECT count(*) FROM {Fy.Table}");
    protected Task<long> PeriodRows() => Sql<long>($"SELECT count(*) FROM {Fp.Table}");
}

public class FiscalMigrationTests : FiscalTestBase
{
    [PostgresFact]
    public async Task A_freshly_provisioned_company_has_the_calendar_tables_and_no_data_and_no_fabricated_audit_event()
    {
        await Prepare();
        Assert.Equal(33L, await Count(Company, SystemSchema.MigrationJournalTable));
        Assert.Empty(await Calendar().ListAsync(Company.Id));
        Assert.Equal(0L, await Count(Company, Fy.Table));
        Assert.Equal(0L, await Count(Company, Fp.Table));
        Assert.Equal(0L, await Count(Company, Fh.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task An_existing_company_at_0005_gets_0006_without_touching_its_data_and_the_migration_is_idempotent()
    {
        await StartCompanies();
        var legacy = await ReadyCompany([.. MigrationScript.LoadEmbeddedCompany().Where(s => s.Version <= 5)]);
        await Sql("CREATE TABLE zz_existing (a int); INSERT INTO zz_existing VALUES (7)", legacy);
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal([6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33], outcome.Applied);
        Assert.Equal(7, await Sql<int>("SELECT a FROM zz_existing", legacy));
        Assert.Equal(0L, await Count(legacy, Fy.Table));                          // nothing is seeded
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync()).Status);
    }

    [PostgresFact]
    public async Task A_failing_migration_0006_rolls_back_completely_and_the_company_stays_at_0005()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 5)]);
        var broken = MigrationScript.Parse("0006_company_fiscal_calendar.sql", all.Single(s => s.Version == 6).Sql + "\nDO $$ BEGIN RAISE EXCEPTION 'boom'; END; $$;");
        var outcome = Assert.Single(await new CompanyMigrator(Options, Registry, [.. all.Where(s => s.Version <= 5), broken]).MigrateAsync());
        Assert.Equal(CompanyMigrationStatus.Failed, outcome.Status);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name IN ('{Fy.Table}', '{Fp.Table}', '{Fh.Table}')", legacy));
        Assert.Equal(5L, await Count(legacy, SystemSchema.MigrationJournalTable));
    }

    [PostgresFact]
    public async Task A_company_edited_after_migration_0006_is_detected_by_the_checksum_gate()
    {
        await Prepare();
        var resolver = SinglePrincipalResolver(Company);
        (await resolver.OpenAsync(Company.Id)).Dispose();
        await Sql($"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = 'edited' WHERE version = 6");
        Assert.Equal(CompanyDatabaseException.SchemaIncompatible, (await Assert.ThrowsAsync<CompanyDatabaseException>(() => resolver.OpenAsync(Company.Id))).Code);
    }
}

public class FiscalConstraintTests : FiscalTestBase
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string Raised = PostgresErrorCodes.RaiseException;
    private const string Excluded = PostgresErrorCodes.ExclusionViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;

    private async Task<(FiscalYearView Year, FiscalPeriodView P1)> Seed(int year = 2026)
    {
        await PrepareFiscal();
        var y = await NewYear($"FY{year}", year);
        return (y, y.Periods[0]);
    }

    [PostgresFact]
    public async Task A_year_is_exactly_twelve_months_from_the_first_of_a_month()
    {
        await PrepareFiscal();
        var id = Guid.NewGuid();
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Fy.Table} ({Fy.Name}, {Fy.StartDate}, {Fy.EndDate}, {Fy.PeriodType}, {Fy.TimeZone}, {Fy.CreatedAtUtc}, {Fy.CreatedBy}) VALUES ('X', '2026-01-15', '2027-01-14', 1, 'UTC', now(), gen_random_uuid())"));
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Fy.Table} ({Fy.Name}, {Fy.StartDate}, {Fy.EndDate}, {Fy.PeriodType}, {Fy.TimeZone}, {Fy.CreatedAtUtc}, {Fy.CreatedBy}) VALUES ('X', '2026-01-01', '2026-11-30', 1, 'UTC', now(), gen_random_uuid())"));
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Fy.Table} ({Fy.Name}, {Fy.StartDate}, {Fy.EndDate}, {Fy.PeriodType}, {Fy.TimeZone}, {Fy.CreatedAtUtc}, {Fy.CreatedBy}) VALUES ('X', '2026-01-01', '2026-12-31', 3, 'UTC', now(), gen_random_uuid())"));
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Fy.Table} ({Fy.Name}, {Fy.StartDate}, {Fy.EndDate}, {Fy.PeriodType}, {Fy.TimeZone}, {Fy.CreatedAtUtc}, {Fy.CreatedBy}) VALUES ('  ', '2026-01-01', '2026-12-31', 1, 'UTC', now(), gen_random_uuid())"));
        Assert.Equal(0L, await YearRows());
        Assert.NotEqual(Guid.Empty, id);
    }

    [PostgresFact]
    public async Task A_year_without_its_periods_or_with_wrong_periods_is_refused_at_commit()
    {
        await PrepareFiscal();
        var id = Guid.NewGuid();
        Assert.Equal(Raised, await InTx(InsertYear(id, "Bare", "2026-01-01")));                                                     // no periods at all
        var eleven = MonthlyPeriods(id, new DateOnly(2026, 1, 1)).Take(11);
        Assert.Equal(Raised, await InTx([InsertYear(id, "Short", "2026-01-01"), .. eleven]));                                      // 11 of 12
        var wrong = MonthlyPeriods(id, new DateOnly(2026, 1, 1)).Take(11).Append(InsertPeriod(id, 12, "2026-12-01", "2027-01-31"));
        Assert.Equal(Raised, await InTx([InsertYear(id, "Wrong", "2026-01-01"), .. wrong]));                                       // a period spanning too long
        Assert.Equal(Raised, await InTx([InsertYear(id, "Quarterly", "2026-01-01", "2"), .. MonthlyPeriods(id, new DateOnly(2026, 1, 1))]));   // monthly periods in a quarterly year
        Assert.Null(await InTx([InsertYear(id, "Good", "2026-01-01"), .. MonthlyPeriods(id, new DateOnly(2026, 1, 1))]));
        Assert.Equal(12L, await PeriodRows());
    }

    [PostgresFact]
    public async Task Years_never_overlap_never_leave_gaps_and_names_are_unique_case_insensitively()
    {
        var (_, _) = await Seed(2026);
        var id = Guid.NewGuid();
        Assert.Equal(Excluded, await InTx([InsertYear(id, "Overlap", "2026-07-01"), .. MonthlyPeriods(id, new DateOnly(2026, 7, 1))]));
        Assert.Equal(Raised, await InTx([InsertYear(id, "Gap", "2028-01-01"), .. MonthlyPeriods(id, new DateOnly(2028, 1, 1))]));    // 2027 is missing
        Assert.Equal(Unique, await InTx([InsertYear(id, "fy2026", "2027-01-01"), .. MonthlyPeriods(id, new DateOnly(2027, 1, 1))]));   // name taken (any case)
        Assert.Null(await InTx([InsertYear(id, "Next", "2027-01-01"), .. MonthlyPeriods(id, new DateOnly(2027, 1, 1))]));             // adjacent: fine
        var before = Guid.NewGuid();
        Assert.Null(await InTx([InsertYear(before, "Before", "2025-01-01"), .. MonthlyPeriods(before, new DateOnly(2025, 1, 1))]));   // adjacent before the earliest: fine
        Assert.Equal(3L, await YearRows());
    }

    [PostgresFact]
    public async Task Periods_never_overlap_across_the_company_and_keep_their_identity()
    {
        var (y, p1) = await Seed();
        var next = Guid.NewGuid();
        Assert.Equal(Excluded, await InTx(InsertYear(next, "Next", "2027-01-01"), InsertPeriod(next, 1, "2026-03-10", "2026-03-20")));   // a period inside another period of the company
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.StartDate} = '2026-01-02'")));
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.Number} = 2")));
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.YearId} = gen_random_uuid()")));
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.Id} = gen_random_uuid()")));
        Assert.Equal(Raised, await Fails($"UPDATE {Fy.Table} SET {Fy.Name} = 'Renamed', {Fy.Revision} = {Fy.Revision} + 1"));
        Assert.Equal(Raised, await Fails($"UPDATE {Fy.Table} SET {Fy.TimeZone} = 'Asia/Tokyo', {Fy.Revision} = {Fy.Revision} + 1"));
    }

    [PostgresFact]
    public async Task Only_adjacent_state_transitions_exist_and_final_closed_always_records_who_closed_it()
    {
        var (_, p1) = await Seed();
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 3, {Fp.FinalClosedBy} = gen_random_uuid()")));          // Open -> Final Closed
        Assert.Null(await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 2")));                                                         // Open -> Soft
        Assert.Equal(CheckViolation, await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 3")));                                        // Final without a recorded closer
        Assert.Null(await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 3, {Fp.FinalClosedBy} = gen_random_uuid()")));                 // Soft -> Final
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 1")));                                                // Final -> Open
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 4")));
        Assert.Equal(Raised, await Fails(SetPeriod(p1.Id, $"{Fp.Status} = 2")));                                                // Final -> Soft needs an approved request (FiscalApprovalConstraintTests)
        Assert.Equal(Raised, await Fails($"UPDATE {Fp.Table} SET {Fp.StatusChangedAtUtc} = now() WHERE {Fp.Id} = '{p1.Id}'"));  // revision fence
    }

    [PostgresFact]
    public async Task A_closed_year_needs_every_period_final_closed_and_pins_them_there_until_it_is_reopened()
    {
        var (y, _) = await Seed();
        var close = $"UPDATE {Fy.Table} SET {Fy.Status} = 2, {Fy.ClosedAtUtc} = now(), {Fy.ClosedBy} = gen_random_uuid(), {Fy.Revision} = {Fy.Revision} + 1 WHERE {Fy.Id} = '{y.Id}'";
        Assert.Equal(Raised, await Fails(close));                                                                                  // periods still open
        Assert.Equal(CheckViolation, await Fails($"UPDATE {Fy.Table} SET {Fy.Status} = 2, {Fy.Revision} = {Fy.Revision} + 1 WHERE {Fy.Id} = '{y.Id}'"));   // no closer recorded
        await Sql($"ALTER TABLE {Fp.Table} DISABLE TRIGGER USER; UPDATE {Fp.Table} SET {Fp.Status} = 3, {Fp.FinalClosedBy} = gen_random_uuid(); ALTER TABLE {Fp.Table} ENABLE TRIGGER USER");   // owner-only arrangement
        Assert.Null(await Fails(close));
        Assert.Equal(Raised, await Fails(SetPeriod(y.Periods[0].Id, $"{Fp.Status} = 2")));                                         // cannot leave Final Closed while the year is closed
    }

    [PostgresFact]
    public async Task Years_and_periods_are_never_deleted_or_truncated_and_history_is_append_only_with_mandatory_reasons()
    {
        var (y, p1) = await Seed();
        Assert.Equal(Raised, await Fails($"DELETE FROM {Fy.Table}"));
        Assert.Equal(Raised, await Fails($"DELETE FROM {Fp.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Fy.Table} CASCADE"));
        Assert.Equal("0A000", await Fails($"TRUNCATE {Fp.Table}"));   // referenced by journal entries: PostgreSQL itself refuses
        Assert.Equal(1L, await YearRows());
        Assert.Equal(1L, await Count(Company, Fh.Table));
        Assert.Equal(Raised, await Fails($"UPDATE {Fh.Table} SET {Fh.Revision} = 9"));
        Assert.Equal(Raised, await Fails($"DELETE FROM {Fh.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Fh.Table}"));
        // Reopening a Final Closed period (3 -> 2) or a closed year (2 -> 1) needs a reason in its history row.
        string Hist(string reason, int from, int to, short entity) => $"INSERT INTO {Fh.Table} ({Fh.Entity}, {Fh.EntityId}, {Fh.Revision}, {Fh.ActorUserId}, {Fh.ChangedAtUtc}, {Fh.Changes}, {Fh.FromStatus}, {Fh.ToStatus}, {Fh.Reason}, {Fh.AuditStream}, {Fh.AuditSeq}) " +
            $"VALUES ({entity}, '{p1.Id}', 5, gen_random_uuid(), now(), '{{}}', {from}, {to}, {reason}, 'business:none', 999)";
        // PostgreSQL checks the CHECK constraint before the foreign key: a refused reason is a check violation, an accepted one proceeds to the (absent) audit event.
        Assert.Equal(CheckViolation, await Fails(Hist("NULL", 3, 2, 2)));
        Assert.Equal(CheckViolation, await Fails(Hist("'short'", 3, 2, 2)));
        Assert.Equal(CheckViolation, await Fails(Hist("'          '", 3, 2, 2)));
        Assert.Equal(CheckViolation, await Fails(Hist("NULL", 2, 1, 1)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails(Hist("'A documented reason'", 3, 2, 2)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Fails(Hist("NULL", 1, 2, 2)));     // ordinary transitions need no reason
        Assert.NotNull(y);
    }

    [PostgresFact]
    public async Task The_posting_availability_function_accepts_open_refuses_final_closed_and_gates_soft_closed()
    {
        var (_, p1) = await Seed();
        Assert.Null(await Fails("SELECT f_" + Fc.PostableFunction[2..] + "('2026-01-15', false)"));                              // Open
        Assert.Equal(Raised, await Fails("SELECT f_" + Fc.PostableFunction[2..] + "('2027-06-01', true)"));                      // no period covers it
        await Sql(SetPeriod(p1.Id, $"{Fp.Status} = 2"));
        Assert.Equal(Raised, await Fails("SELECT " + Fc.PostableFunction + "('2026-01-15', false)"));                           // Soft Closed, not authorized
        Assert.Null(await Fails("SELECT " + Fc.PostableFunction + "('2026-01-15', true)"));                                      // Soft Closed, authorized
        await Sql(SetPeriod(p1.Id, $"{Fp.Status} = 3, {Fp.FinalClosedBy} = gen_random_uuid()"));
        Assert.Equal(Raised, await Fails("SELECT " + Fc.PostableFunction + "('2026-01-15', true)"));                            // Final Closed: never
    }
}

public class FiscalServiceTests : FiscalTestBase
{
    [PostgresFact]
    public async Task Creating_a_monthly_year_generates_twelve_contiguous_open_periods_and_is_audited_in_the_same_transaction()
    {
        await PrepareFiscal("Asia/Riyadh");
        var y = await NewYear("FY 2026", 2026);
        Assert.Equal(new DateOnly(2026, 1, 1), y.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), y.EndDate);
        Assert.Equal(FiscalPeriodType.Monthly, y.PeriodType);
        Assert.Equal("Asia/Riyadh", y.TimeZone);
        Assert.False(y.IsClosed);
        Assert.Equal(1, y.Revision);
        Assert.Equal(12, y.Periods.Count);
        Assert.All(y.Periods, p => Assert.Equal(FiscalPeriodState.Open, p.State));
        Assert.Equal(Enumerable.Range(1, 12), y.Periods.Select(p => p.Number));
        Assert.Equal(new DateOnly(2026, 2, 28), y.Periods[1].EndDate);
        for (var i = 1; i < 12; i++) Assert.Equal(y.Periods[i - 1].EndDate.AddDays(1), y.Periods[i].StartDate);
        Assert.Equal(AuditBaseline + 1, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigAuditEvents.FiscalYearCreated, await Sql<short>($"SELECT {Audit_.EventType} FROM {Audit_.Table} ORDER BY {Audit_.Seq} DESC LIMIT 1"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Fh.Table} WHERE {Fh.Entity} = 1 AND {Fh.EntityId} = '{y.Id}' AND {Fh.Revision} = 1 AND {Fh.AuditSeq} = (SELECT max({Audit_.Seq}) FROM {Audit_.Table})"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        Assert.Equal(y.Id, (await Calendar().GetAsync(Company.Id, y.Id))!.Id);
    }

    [PostgresFact]
    public async Task A_quarterly_year_has_four_periods_and_a_non_calendar_fiscal_year_is_supported()
    {
        await PrepareFiscal();
        var y = await NewYear("FY July", 2026, 7, "quarterly");
        Assert.Equal(new DateOnly(2027, 6, 30), y.EndDate);
        Assert.Equal(4, y.Periods.Count);
        Assert.Equal([new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 1), new DateOnly(2027, 1, 1), new DateOnly(2027, 4, 1)], y.Periods.Select(p => p.StartDate));
        Assert.Equal(new DateOnly(2026, 9, 30), y.Periods[0].EndDate);
        Assert.Equal(new DateOnly(2027, 6, 30), y.Periods[3].EndDate);
    }

    [PostgresFact]
    public async Task A_fiscal_year_needs_the_company_time_zone_and_valid_input_and_a_contiguous_non_overlapping_calendar()
    {
        await Prepare();                                                                                                           // no time zone chosen yet
        var noZone = await Calendar().CreateYearAsync(Admin.Id, Company.Id, Year("FY", 2026));
        Assert.Equal(ConfigStatus.Conflict, noZone.Status);
        Assert.Equal(CompanyTimeZoneNotConfiguredException.Code, noZone.Code);
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        Assert.Equal(ConfigStatus.Invalid, (await Calendar().CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY", new DateOnly(2026, 1, 15), "monthly"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Calendar().CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput(" ", new DateOnly(2026, 1, 1), "monthly"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Calendar().CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY", new DateOnly(2026, 1, 1), "weekly"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Calendar().CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY", null, "monthly"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Calendar().CreateYearAsync(Admin.Id, Company.Id, null)).Status);
        await NewYear("FY2026", 2026);
        Assert.Equal("fiscal_year_overlap", (await Calendar().CreateYearAsync(Admin.Id, Company.Id, Year("Overlap", 2026, 7))).Code);
        Assert.Equal("fiscal_year_not_contiguous", (await Calendar().CreateYearAsync(Admin.Id, Company.Id, Year("Gap", 2028))).Code);
        Assert.Equal("fiscal_year_name_taken", (await Calendar().CreateYearAsync(Admin.Id, Company.Id, Year("fy2026", 2027))).Code);
        Assert.Equal(1L, await YearRows());
        Assert.Equal(12L, await PeriodRows());
    }

    [PostgresFact]
    public async Task Several_fiscal_years_can_be_open_at_the_same_time_before_and_after_the_first()
    {
        await PrepareFiscal();
        await NewYear("FY2026", 2026);
        await NewYear("FY2027", 2027);
        await NewYear("FY2025", 2025);
        var years = await Calendar().ListAsync(Company.Id);
        Assert.Equal(["FY2025", "FY2026", "FY2027"], years.Select(y => y.Name));
        Assert.All(years, y => Assert.False(y.IsClosed));
        Assert.Equal(36L, await PeriodRows());
        Assert.Equal(AuditBaseline + 3, await Count(Company, Audit_.Table));
    }

    [PostgresFact]
    public async Task The_state_machine_walks_open_soft_final_and_never_skips_a_step()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var p = y.Periods[0];
        var svc = Calendar();
        Assert.Equal("invalid_period_state", (await svc.FinalCloseAsync(Admin.Id, Company.Id, p.Id, 1)).Code);                      // Open -> Final: refused
        Assert.Equal("invalid_period_state", (await svc.ReopenAsync(Admin.Id, Company.Id, p.Id, 1)).Code);                         // Open -> Open: refused
        var soft = Ok(await svc.SoftCloseAsync(Admin.Id, Company.Id, p.Id, 1));
        Assert.Equal((FiscalPeriodState.SoftClosed, 2L), (soft.State, soft.Revision));
        Assert.Equal("invalid_period_state", (await svc.SoftCloseAsync(Admin.Id, Company.Id, p.Id, 2)).Code);
        var back = Ok(await svc.ReopenAsync(Admin.Id, Company.Id, p.Id, 2));
        Assert.Equal(FiscalPeriodState.Open, back.State);
        Ok(await svc.SoftCloseAsync(Admin.Id, Company.Id, p.Id, 3));
        var final = Ok(await svc.FinalCloseAsync(Admin.Id, Company.Id, p.Id, 4));
        Assert.Equal(FiscalPeriodState.FinalClosed, final.State);
        Assert.Equal("invalid_period_state", (await svc.ReopenAsync(Admin.Id, Company.Id, p.Id, 5)).Code);                         // Final -> Open: refused
        Assert.Equal("invalid_period_state", (await svc.SoftCloseAsync(Admin.Id, Company.Id, p.Id, 5)).Code);
        Assert.Equal(5L, await Sql<long>($"SELECT count(*) FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.EntityId} = '{p.Id}' OR {Fh.Entity} = 1"));   // year + 4 period changes
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
        // Other periods are untouched.
        Assert.All((await svc.GetAsync(Company.Id, y.Id))!.Periods.Skip(1), q => Assert.Equal(FiscalPeriodState.Open, q.State));
    }

    [PostgresFact]
    public async Task Changes_need_the_current_revision_and_unknown_periods_are_not_found()
    {
        await PrepareFiscal();
        var p = (await NewYear("FY2026", 2026)).Periods[0];
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Calendar().SoftCloseAsync(Admin.Id, Company.Id, p.Id, null)).Status);
        var stale = await Calendar().SoftCloseAsync(Admin.Id, Company.Id, p.Id, 7);
        Assert.Equal(ConfigStatus.PreconditionFailed, stale.Status);
        Assert.Equal(1, stale.CurrentRevision);
        Assert.Equal(ConfigStatus.NotFound, (await Calendar().SoftCloseAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1)).Status);
        Assert.Equal(FiscalPeriodState.Open, (await Calendar().FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 20)))!.State);
        Assert.Null(await Calendar().FindPeriodAsync(Company.Id, new DateOnly(2030, 1, 1)));
    }

    private const string Why = "Correction of a posting error found in audit";

    /// <summary>Soft closes and final closes period <paramref name="index"/> of the year as <paramref name="closer"/>; returns the period at its current revision.</summary>
    private async Task<FiscalPeriodView> FinalClose(FiscalYearView y, Guid closer, int index = 0)
    {
        var svc = Calendar();
        var soft = Ok(await svc.SoftCloseAsync(closer, Company.Id, y.Periods[index].Id, 1));
        return Ok(await svc.FinalCloseAsync(closer, Company.Id, y.Periods[index].Id, soft.Revision));
    }

    private async Task<FiscalReopenRequestView> RequestPeriod(FiscalPeriodView p, Guid requester) =>
        Ok(await Calendar().RequestPeriodReopenAsync(requester, Company.Id, p.Id, p.Revision, new ReasonInput(Why)));

    [PostgresFact]
    public async Task Reopening_a_final_closed_period_is_a_request_that_a_different_user_approves_and_nothing_changes_until_then()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var svc = Calendar();
        var closer = Admin;
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        var p = await FinalClose(y, closer.Id);

        // The request itself: validated, bound to the exact object and revision, and it changes nothing.
        Assert.Equal(ConfigStatus.Invalid, (await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, p.Id, p.Revision, null)).Status);
        Assert.Equal(ConfigStatus.Invalid, (await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, p.Id, p.Revision, new ReasonInput("too short"))).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, p.Id, null, new ReasonInput(Why))).Status);
        var stale = await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, p.Id, p.Revision - 1, new ReasonInput(Why));
        Assert.Equal((ConfigStatus.PreconditionFailed, p.Revision), (stale.Status, stale.CurrentRevision));
        Assert.Equal("invalid_period_state", (await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, y.Periods[1].Id, 1, new ReasonInput(Why))).Code);   // an Open period needs no exceptional reopening
        Assert.Equal(ConfigStatus.NotFound, (await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, Guid.NewGuid(), 1, new ReasonInput(Why))).Status);
        var request = await RequestPeriod(p, requester.Id);
        Assert.Equal((FiscalReopenStatus.Pending, p.Revision, FiscalReopenTarget.Period), (request.Status, request.ExpectedRevision, request.Target));
        Assert.Equal(FiscalPeriodState.FinalClosed, (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);          // nothing changed
        Assert.Equal("reopen_request_pending", (await svc.RequestPeriodReopenAsync(approver.Id, Company.Id, p.Id, p.Revision, new ReasonInput(Why))).Code);   // one pending request per target

        // Approval re-validates everything and refuses what must be refused.
        var self = await svc.ApproveRequestAsync(requester.Id, Company.Id, request.Id, request.Revision, true);
        Assert.Equal((ConfigStatus.Forbidden, "self_approval_forbidden"), (self.Status, self.Code));
        var byCloser = await svc.ApproveRequestAsync(closer.Id, Company.Id, request.Id, request.Revision, true);
        Assert.Equal((ConfigStatus.Forbidden, "segregation_of_duties"), (byCloser.Status, byCloser.Code));
        Assert.Equal("request_requester_not_authorized", (await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, false)).Code);   // the requester's right was revoked
        Assert.Equal(ConfigStatus.PreconditionFailed, (await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, 9, true)).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, null, true)).Status);
        Assert.Equal(FiscalPeriodState.FinalClosed, (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);          // every refusal left the calendar alone
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Rq.Table} WHERE {Rq.Status} = 1"));

        var approved = Ok(await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, true));
        Assert.Equal(FiscalReopenStatus.Approved, approved.Status);
        var reopened = (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!;
        Assert.Equal(FiscalPeriodState.SoftClosed, reopened.State);                                                                  // never straight to Open
        Assert.Equal(p.Revision + 1, reopened.Revision);
        Assert.Equal(approver.Id, await Sql<Guid>($"SELECT {Rq.DecidedBy} FROM {Rq.Table}"));
        Assert.Equal(Why, await Sql<string>($"SELECT {Fh.Reason} FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.FromStatus} = 3 AND {Fh.ToStatus} = 2"));
        Assert.Contains(request.Id.ToString(), await Sql<string>($"SELECT {Fh.Changes}::text FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.FromStatus} = 3 AND {Fh.ToStatus} = 2"));   // the reopening names its approval
        Assert.DoesNotContain("Correction", await Sql<string>($"SELECT string_agg({Audit_.Detail}, '|') FROM {Audit_.Table}"));      // reason text stays out of the audit detail
        Assert.Equal(new[] { ConfigAuditEvents.FiscalReopenRequested, ConfigAuditEvents.FiscalPeriodFinalReopened, ConfigAuditEvents.FiscalReopenApproved },
            await Sql<short[]>($"SELECT array_agg({Audit_.EventType} ORDER BY {Audit_.Seq}) FROM {Audit_.Table} WHERE {Audit_.EventType} IN (1023, 1024, 1025)"));
        Assert.Equal("request_not_pending", (await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, approved.Revision, true)).Code);   // a request executes once
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task A_denied_or_cancelled_request_changes_nothing_and_only_the_right_person_can_decide_it()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var svc = Calendar();
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        var p = await FinalClose(y, Admin.Id);
        var request = await RequestPeriod(p, requester.Id);

        Assert.Equal("self_approval_forbidden", (await svc.DenyRequestAsync(requester.Id, Company.Id, request.Id, request.Revision, null)).Code);      // the requester withdraws, never denies
        Assert.Equal("not_the_requester", (await svc.CancelRequestAsync(approver.Id, Company.Id, request.Id, request.Revision)).Code);
        Assert.Equal(ConfigStatus.Invalid, (await svc.DenyRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, new string('x', 501))).Status);
        var denied = Ok(await svc.DenyRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, "  Not justified  "));
        Assert.Equal((FiscalReopenStatus.Denied, "Not justified"), (denied.Status, denied.DecisionNote));
        Assert.Equal(FiscalPeriodState.FinalClosed, (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
        Assert.Equal("request_not_pending", (await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, denied.Revision, true)).Code);       // a denied request can never be revived
        Assert.Equal("request_not_pending", (await svc.CancelRequestAsync(requester.Id, Company.Id, request.Id, denied.Revision)).Code);

        var again = await RequestPeriod(p, requester.Id);                                                                           // a new request is possible after a decision
        var cancelled = Ok(await svc.CancelRequestAsync(requester.Id, Company.Id, again.Id, again.Revision));
        Assert.Equal(FiscalReopenStatus.Cancelled, cancelled.Status);
        Assert.Equal("request_not_pending", (await svc.ApproveRequestAsync(approver.Id, Company.Id, again.Id, cancelled.Revision, true)).Code);
        Assert.Equal(FiscalPeriodState.FinalClosed, (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.ToStatus} = 2 AND {Fh.FromStatus} = 3"));      // no reopening was ever recorded
        Assert.Equal(new[] { ConfigAuditEvents.FiscalReopenDenied, ConfigAuditEvents.FiscalReopenCancelled }.Order(),
            (await Sql<short[]>($"SELECT array_agg({Audit_.EventType}) FROM {Audit_.Table} WHERE {Audit_.EventType} IN (1026, 1027)")).Order());
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task A_request_whose_object_changed_meanwhile_is_stale_and_cannot_reopen_anything()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026, type: "quarterly");
        var svc = Calendar();
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        FiscalPeriodView last = default!;
        for (var i = 0; i < 4; i++) last = await FinalClose(y, Admin.Id, i);
        var first = (await svc.GetAsync(Company.Id, y.Id))!.Periods[0];
        var request = await RequestPeriod(first, requester.Id);
        var closed = Ok(await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 1));                                                   // the year is closed while the request waits
        var stale = await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, true);
        Assert.Equal((ConfigStatus.Conflict, "request_stale"), (stale.Status, stale.Code));
        Assert.Equal(FiscalPeriodState.FinalClosed, (await svc.GetAsync(Company.Id, y.Id))!.Periods[0].State);
        Assert.Equal("fiscal_year_closed", (await svc.RequestPeriodReopenAsync(requester.Id, Company.Id, last.Id, last.Revision, new ReasonInput(Why))).Code);   // a closed year freezes its periods
        Assert.Equal(FiscalReopenStatus.Pending, (await svc.GetRequestAsync(Company.Id, request.Id))!.Status);                       // the stale request stays visible until someone denies it
        Ok(await svc.DenyRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, "Superseded by the year closing"));
        Assert.True(closed.IsClosed);
    }

    [PostgresFact]
    public async Task A_closed_year_reopens_only_through_an_approved_request_and_its_periods_stay_final_closed()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026, type: "quarterly");
        var svc = Calendar();
        var requester = await NewAdminOf(Company);
        var approver = await NewAdminOf(Company);
        Assert.Equal("fiscal_year_has_unfinalized_periods", (await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 1)).Code);
        for (var i = 0; i < 4; i++)
        {
            if (i > 0) Assert.Equal("fiscal_year_has_unfinalized_periods", (await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 1)).Code);
            await FinalClose(y, Admin.Id, i);
        }
        Assert.Equal(ConfigStatus.PreconditionFailed, (await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 9)).Status);
        var closed = Ok(await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 1));
        Assert.True(closed.IsClosed);
        Assert.Equal("fiscal_year_already_closed", (await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 2)).Code);
        var next = await NewYear("FY2027", 2027);
        Assert.Equal("fiscal_year_not_closed", (await svc.RequestYearReopenAsync(requester.Id, Company.Id, next.Id, 1, new ReasonInput(Why))).Code);   // an open year needs none
        var request = Ok(await svc.RequestYearReopenAsync(requester.Id, Company.Id, y.Id, closed.Revision, new ReasonInput(Why)));
        Assert.Equal(FiscalReopenTarget.Year, request.Target);
        Assert.True((await svc.GetAsync(Company.Id, y.Id))!.IsClosed);                                                              // still closed
        Assert.Equal("segregation_of_duties", (await svc.ApproveRequestAsync(Admin.Id, Company.Id, request.Id, request.Revision, true)).Code);   // the user who closed the year cannot approve its reopening
        Assert.Equal("self_approval_forbidden", (await svc.ApproveRequestAsync(requester.Id, Company.Id, request.Id, request.Revision, true)).Code);
        Ok(await svc.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision, true));
        var reopened = (await svc.GetAsync(Company.Id, y.Id))!;
        Assert.False(reopened.IsClosed);
        Assert.Equal(closed.Revision + 1, reopened.Revision);
        Assert.All(reopened.Periods, q => Assert.Equal(FiscalPeriodState.FinalClosed, q.State));                                    // each period needs its own approved request
        Assert.Equal(Why, await Sql<string>($"SELECT {Fh.Reason} FROM {Fh.Table} WHERE {Fh.Entity} = 1 AND {Fh.FromStatus} = 2 AND {Fh.ToStatus} = 1"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task Concurrent_requests_and_decisions_can_neither_multiply_nor_bypass_the_approval()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var svc = Calendar();
        var requester = await NewAdminOf(Company);
        var approverA = await NewAdminOf(Company);
        var approverB = await NewAdminOf(Company);
        var p = await FinalClose(y, Admin.Id);

        var requests = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => svc.RequestPeriodReopenAsync(requester.Id, Company.Id, p.Id, p.Revision, new ReasonInput(Why))));
        Assert.Single(requests, r => r.IsOk);                                                                                       // exactly one pending request per target
        Assert.All(requests.Where(r => !r.IsOk), r => Assert.Equal("reopen_request_pending", r.Code));
        var request = requests.Single(r => r.IsOk).Value!;

        // Two approvers (and a cancellation) race for the same request: exactly one decision wins; the calendar changes at most once.
        var decisions = await Task.WhenAll(
            svc.ApproveRequestAsync(approverA.Id, Company.Id, request.Id, request.Revision, true),
            svc.ApproveRequestAsync(approverB.Id, Company.Id, request.Id, request.Revision, true),
            svc.CancelRequestAsync(requester.Id, Company.Id, request.Id, request.Revision));
        Assert.Single(decisions, r => r.IsOk);
        Assert.All(decisions.Where(r => !r.IsOk), r => Assert.Contains(r.Status, new[] { ConfigStatus.PreconditionFailed, ConfigStatus.Conflict }));
        var final = (await svc.GetRequestAsync(Company.Id, request.Id))!;
        Assert.NotEqual(FiscalReopenStatus.Pending, final.Status);
        var state = (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!;
        Assert.Equal(final.Status == FiscalReopenStatus.Approved ? FiscalPeriodState.SoftClosed : FiscalPeriodState.FinalClosed, state.State);
        Assert.Equal(final.Status == FiscalReopenStatus.Approved ? 1L : 0L, await Sql<long>($"SELECT count(*) FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.FromStatus} = 3 AND {Fh.ToStatus} = 2"));
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task A_blocking_close_probe_prevents_soft_and_final_close_and_unimplemented_checks_are_reported()
    {
        await PrepareFiscal();
        var p = (await NewYear("FY2026", 2026)).Periods[0];
        Assert.Equal(4, Calendar().UnimplementedCloseChecks.Count);                                                                 // nothing is verified yet: never presented as safe
        var blocking = new PeriodCloseGuard([new BlockingProbe("pending_accounting_postings", true)]);
        Assert.Equal(3, Calendar(blocking).UnimplementedCloseChecks.Count);
        var refused = await Calendar(blocking).SoftCloseAsync(Admin.Id, Company.Id, p.Id, 1);
        Assert.Equal("period_close_blocked", refused.Code);
        Assert.Equal(FiscalPeriodState.Open, (await Calendar().FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
        Assert.Equal(AuditBaseline + 1, await Count(Company, Audit_.Table));                                                       // the refused close left no audit event
        Ok(await Calendar(new PeriodCloseGuard([new BlockingProbe("pending_accounting_postings", false)])).SoftCloseAsync(Admin.Id, Company.Id, p.Id, 1));
        Assert.Equal("period_close_blocked", (await Calendar(blocking).FinalCloseAsync(Admin.Id, Company.Id, p.Id, 2)).Code);
        Ok(await Calendar(blocking).ReopenAsync(Admin.Id, Company.Id, p.Id, 2));                                                    // reopening is never blocked
    }

    [PostgresFact]
    public async Task Once_financial_history_exists_no_close_can_proceed_on_placeholder_checks_and_every_close_records_the_missing_checks_before_that()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026, type: "quarterly");
        var closer = Calendar();
        // Before any module holds data the placeholders are harmless, and the close says so in its own history record.
        var soft = Ok(await closer.SoftCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, 1));
        var recorded = await Sql<string>($"SELECT {Fh.Changes}::text FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.ToStatus} = 2 ORDER BY {Fh.Id} DESC LIMIT 1");
        Assert.Contains("closeChecksNotImplemented", recorded);
        Assert.Contains("pending_accounting_postings", recorded);

        // A general ledger that already holds data (its probe says so) but has not registered its close checks: nothing can be closed.
        var ledgerWithData = new FinancialHistoryGuard([new LedgerHistoryProbe("general_ledger", true)]);
        var unsafeCalendar = Calendar(history: ledgerWithData);
        var r1 = await unsafeCalendar.SoftCloseAsync(Admin.Id, Company.Id, y.Periods[1].Id, 1);
        var r2 = await unsafeCalendar.FinalCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, soft.Revision);
        var r3 = await unsafeCalendar.CloseYearAsync(Admin.Id, Company.Id, y.Id, 1);
        foreach (var refused in new[] { (r1.Status, r1.Code), (r2.Status, r2.Code), (r3.Status, r3.Code) })
            Assert.Equal((ConfigStatus.Conflict, "period_close_checks_not_implemented"), refused);
        Assert.Equal(FiscalPeriodState.SoftClosed, (await closer.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);       // nothing changed
        Assert.Equal(FiscalPeriodState.Open, (await closer.FindPeriodAsync(Company.Id, new DateOnly(2026, 4, 5)))!.State);

        // With every close-check domain really implemented, the same ledger may close.
        var all = new PeriodCloseGuard([.. PeriodCloseGuard.RequiredDomains.Select(d => (IPeriodCloseProbe)new BlockingProbe(d, false))]);
        Assert.Empty(all.UnimplementedDomains);
        Ok(await Calendar(all, history: ledgerWithData).FinalCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, soft.Revision));
        Assert.DoesNotContain("closeChecksNotImplemented", await Sql<string>($"SELECT {Fh.Changes}::text FROM {Fh.Table} WHERE {Fh.Entity} = 2 AND {Fh.ToStatus} = 3 ORDER BY {Fh.Id} DESC LIMIT 1"));
    }

    [PostgresFact]
    public async Task Concurrent_closing_and_reopening_requests_resolve_to_one_consistent_state()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var p = y.Periods[0];
        var svc = Calendar();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => svc.SoftCloseAsync(Admin.Id, Company.Id, p.Id, 1)));
        Assert.Single(results, r => r.IsOk);                                                                                        // exactly one wins
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Fh.Table} WHERE {Fh.Entity} = 2"));
        // Racing different transitions of the same revision: one wins, the loser sees a conflict, never a contradictory state.
        var mixed = await Task.WhenAll(svc.ReopenAsync(Admin.Id, Company.Id, p.Id, 2), svc.FinalCloseAsync(Admin.Id, Company.Id, p.Id, 2));
        Assert.Single(mixed, r => r.IsOk);
        var state = (await svc.FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State;
        Assert.True(state is FiscalPeriodState.Open or FiscalPeriodState.FinalClosed);
        Assert.Equal(AuditVerdict.Verified, (await Verifier(Company).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task The_company_business_time_zone_decides_today()
    {
        await Prepare();
        Assert.Null(await Calendar().TodayAsync(Company.Id));                                                                       // nothing is defaulted
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "Pacific/Kiritimati")));   // UTC+14: the clock (2026-11-09 12:00Z) is already the 10th
        Assert.Equal(new DateOnly(2026, 11, 10), await Calendar().TodayAsync(Company.Id));
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 1, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        Assert.Equal(new DateOnly(2026, 11, 9), await Calendar().TodayAsync(Company.Id));
    }
}

/// <summary>The backend gate the future posting engine must call: it enforces period availability, locks against concurrent closes, never trusts the frontend.</summary>
public class FiscalPostingGateTests : FiscalTestBase
{
    private async Task<PostingPeriodCheck> Check(string date, bool allowSoft)
    {
        var resolver = SinglePrincipalResolver(Company);
        await using var conn = await resolver.OpenAsync(Company.Id);
        await using var tx = await conn.BeginTransactionAsync();
        return await FiscalPeriodGate.CheckAsync(conn, tx, DateOnly.Parse(date), allowSoft);
    }

    [PostgresFact]
    public async Task The_gate_accepts_open_periods_gates_soft_closed_by_authorization_and_never_accepts_final_closed_or_uncovered_dates()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var svc = Calendar();
        Assert.True((await Check("2026-01-31", false)).Allowed);
        Assert.Equal(PostingPeriodCheck.NoPeriod, (await Check("2027-01-01", true)).Reason);
        Assert.Equal(PostingPeriodCheck.NoPeriod, (await Check("2025-12-31", true)).Reason);
        Ok(await svc.SoftCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, 1));
        Assert.Equal(PostingPeriodCheck.SoftClosed, (await Check("2026-01-31", false)).Reason);
        Assert.True((await Check("2026-01-31", true)).Allowed);
        Assert.True((await Check("2026-02-01", false)).Allowed);                                                                    // the next period is unaffected
        Ok(await svc.FinalCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, 2));
        var final = await Check("2026-01-15", true);
        Assert.False(final.Allowed);                                                                                                 // not even an authorized caller
        Assert.Equal(PostingPeriodCheck.FinalClosed, final.Reason);
        Assert.Equal(FiscalPeriodState.FinalClosed, final.State);
        // The same decision after an exceptional reopening: Soft Closed again, never Open.
        var req = Ok(await svc.RequestPeriodReopenAsync((await NewAdminOf(Company)).Id, Company.Id, y.Periods[0].Id, 3, new ReasonInput("Reopened for a documented correction")));
        Ok(await svc.ApproveRequestAsync((await NewAdminOf(Company)).Id, Company.Id, req.Id, req.Revision, true));
        Assert.Equal(PostingPeriodCheck.SoftClosed, (await Check("2026-01-15", false)).Reason);
    }

    [PostgresFact]
    public async Task A_close_waits_for_an_in_flight_posting_and_later_postings_see_the_closed_state()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var p = y.Periods[0];
        var resolver = SinglePrincipalResolver(Company);
        await using var conn = await resolver.OpenAsync(Company.Id);
        await using var posting = await conn.BeginTransactionAsync();
        Assert.True((await FiscalPeriodGate.CheckAsync(conn, posting, new DateOnly(2026, 1, 10), false)).Allowed);                  // a posting is in flight (period row share-locked)
        var closing = Calendar().SoftCloseAsync(Admin.Id, Company.Id, p.Id, 1);
        await Task.Delay(1500);
        Assert.False(closing.IsCompleted);                                                                                           // the close waits for the posting transaction
        await posting.CommitAsync();
        Assert.True((await closing).IsOk);
        Assert.Equal(PostingPeriodCheck.SoftClosed, (await Check("2026-01-10", false)).Reason);                                      // every later posting sees the closed period
    }
}

/// <summary>The approval requirement as PostgreSQL enforces it: even a direct statement by the runtime or the owner cannot reopen without an approved request.</summary>
public class FiscalApprovalConstraintTests : FiscalTestBase
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string Raised = PostgresErrorCodes.RaiseException;
    private const string Unique = PostgresErrorCodes.UniqueViolation;

    private static string InsertRequest(int kind, Guid target, long expected, int status, Guid requester, Guid? decider, long? result) =>
        $"INSERT INTO {Rq.Table} ({Rq.TargetKind}, {Rq.TargetId}, {Rq.ExpectedRevision}, {Rq.Reason}, {Rq.Status}, {Rq.RequestedBy}, {Rq.RequestedAtUtc}, {Rq.DecidedBy}, {Rq.DecidedAtUtc}, {Rq.ResultRevision}) " +
        $"VALUES ({kind}, '{target}', {expected}, 'A documented reason', {status}, '{requester}', now(), {(decider is null ? "NULL" : $"'{decider}'")}, {(decider is null ? "NULL" : "now()")}, {(result is null ? "NULL" : result.ToString())})";

    private async Task<(FiscalYearView Year, FiscalPeriodView Period)> FinalClosedPeriod()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026);
        var svc = Calendar();
        var soft = Ok(await svc.SoftCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, 1));
        return (y, Ok(await svc.FinalCloseAsync(Admin.Id, Company.Id, y.Periods[0].Id, soft.Revision)));
    }

    [PostgresFact]
    public async Task A_request_cannot_be_approved_by_its_requester_is_never_edited_and_cannot_be_multiplied_or_deleted()
    {
        var (_, p) = await FinalClosedPeriod();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal(CheckViolation, await Fails(InsertRequest(1, p.Id, 3, 2, a, a, 4)));                                           // approved by the requester
        Assert.Equal(CheckViolation, await Fails(InsertRequest(1, p.Id, 3, 3, a, a, null)));                                        // denied by the requester
        Assert.Equal(CheckViolation, await Fails(InsertRequest(1, p.Id, 3, 4, a, b, null)));                                        // cancelled by someone else
        Assert.Equal(CheckViolation, await Fails(InsertRequest(1, p.Id, 3, 2, a, b, null)));                                        // approved without the resulting revision
        Assert.Equal(CheckViolation, await Fails($"INSERT INTO {Rq.Table} ({Rq.TargetKind}, {Rq.TargetId}, {Rq.ExpectedRevision}, {Rq.Reason}, {Rq.RequestedBy}, {Rq.RequestedAtUtc}) VALUES (1, '{p.Id}', 3, 'short', '{a}', now())"));   // reason too short
        Assert.Null(await Fails(InsertRequest(1, p.Id, 3, 1, a, null, null)));
        Assert.Equal(Unique, await Fails(InsertRequest(1, p.Id, 3, 1, b, null, null)));                                             // one pending per target
        Assert.Equal(Raised, await Fails($"UPDATE {Rq.Table} SET {Rq.Reason} = 'A different reason entirely', {Rq.Revision} = {Rq.Revision} + 1"));
        Assert.Equal(Raised, await Fails($"UPDATE {Rq.Table} SET {Rq.RequestedBy} = '{b}', {Rq.Revision} = {Rq.Revision} + 1"));
        Assert.Equal(CheckViolation, await Fails($"UPDATE {Rq.Table} SET {Rq.Status} = 2, {Rq.DecidedBy} = '{a}', {Rq.DecidedAtUtc} = now(), {Rq.ResultRevision} = 4, {Rq.Revision} = {Rq.Revision} + 1"));   // self-approval by update
        Assert.Null(await Fails($"UPDATE {Rq.Table} SET {Rq.Status} = 3, {Rq.DecidedBy} = '{b}', {Rq.DecidedAtUtc} = now(), {Rq.Revision} = {Rq.Revision} + 1"));
        Assert.Equal(Raised, await Fails($"UPDATE {Rq.Table} SET {Rq.Status} = 1, {Rq.DecidedBy} = NULL, {Rq.DecidedAtUtc} = NULL, {Rq.Revision} = {Rq.Revision} + 1"));   // a decision is final
        Assert.Equal(Raised, await Fails($"DELETE FROM {Rq.Table}"));
        Assert.Equal(Raised, await Fails($"TRUNCATE {Rq.Table}"));
        Assert.Null(await Fails(InsertRequest(1, p.Id, 3, 1, b, null, null)));                                                      // a new pending request is fine after a decision
    }

    [PostgresFact]
    public async Task A_final_closed_period_cannot_be_reopened_without_an_approved_request_for_exactly_that_revision_by_someone_else()
    {
        var (_, p) = await FinalClosedPeriod();
        var requester = Guid.NewGuid(); var approver = Guid.NewGuid();
        var reopen = SetPeriod(p.Id, $"{Fp.Status} = 2");
        Assert.Equal(Raised, await InTx(reopen));                                                                                    // no request at all
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(1, p.Id, 3, 3, requester, approver, null)));                          // a DENIED request
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(1, p.Id, 3, 1, requester, null, null)));                              // a merely PENDING request
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(1, p.Id, 3, 2, requester, approver, 99)));                            // approved for a different resulting revision
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(1, Guid.NewGuid(), 3, 2, requester, approver, p.Revision + 1)));       // approved for a different object
        var closer = await Sql<Guid>($"SELECT {Fp.FinalClosedBy} FROM {Fp.Table} WHERE {Fp.Id} = '{p.Id}'");
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(1, p.Id, 3, 2, requester, closer, p.Revision + 1)));                  // approved by the user who closed it
        Assert.Equal(FiscalPeriodState.FinalClosed, (await Calendar().FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
        Assert.Null(await InTx(reopen, InsertRequest(1, p.Id, 3, 2, requester, approver, p.Revision + 1)));                         // the complete approval commits
        Assert.Equal(FiscalPeriodState.SoftClosed, (await Calendar().FindPeriodAsync(Company.Id, new DateOnly(2026, 1, 5)))!.State);
    }

    [PostgresFact]
    public async Task A_closed_year_cannot_reopen_without_an_approved_request_either()
    {
        await PrepareFiscal();
        var y = await NewYear("FY2026", 2026, type: "quarterly");
        var svc = Calendar();
        for (var i = 0; i < 4; i++)
        {
            var soft = Ok(await svc.SoftCloseAsync(Admin.Id, Company.Id, y.Periods[i].Id, 1));
            Ok(await svc.FinalCloseAsync(Admin.Id, Company.Id, y.Periods[i].Id, soft.Revision));
        }
        var closed = Ok(await svc.CloseYearAsync(Admin.Id, Company.Id, y.Id, 1));
        var requester = Guid.NewGuid(); var approver = Guid.NewGuid();
        var reopen = $"UPDATE {Fy.Table} SET {Fy.Status} = 1, {Fy.ClosedAtUtc} = NULL, {Fy.ClosedBy} = NULL, {Fy.Revision} = {Fy.Revision} + 1 WHERE {Fy.Id} = '{y.Id}'";
        Assert.Equal(Raised, await InTx(reopen));
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(2, y.Id, closed.Revision, 1, requester, null, null)));
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(2, y.Id, closed.Revision, 2, requester, approver, 99)));
        Assert.Equal(Raised, await InTx(reopen, InsertRequest(2, y.Id, closed.Revision, 2, requester, Admin.Id, closed.Revision + 1)));   // approved by the user who closed the year
        Assert.True((await svc.GetAsync(Company.Id, y.Id))!.IsClosed);
        Assert.Null(await InTx(reopen, InsertRequest(2, y.Id, closed.Revision, 2, requester, approver, closed.Revision + 1)));
        Assert.False((await svc.GetAsync(Company.Id, y.Id))!.IsClosed);
    }
}
