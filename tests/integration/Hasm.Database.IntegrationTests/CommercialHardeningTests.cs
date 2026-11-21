using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CDA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentApproval;
using FYr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using NC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using NI = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S1b review round (Agent C): the date of a numbered document stays inside the fiscal year of its number (D-068), the quote of a submitted document is frozen when it is decided (direct SQL), and the
/// numbering transaction guarantees (rollback after issue leaves no gap or reuse, sequences per fiscal year, a duplicate number is refused by the unique index). Disposable cluster only.
/// </summary>
public class CommercialHardeningTests : CommercialTestBase
{
    /// <summary>Runs the statement in its own transaction as the owner; returns the SQLSTATE and message of the failure, or (null, "") when it committed.</summary>
    private async Task<(string? State, string Message)> Run(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try { await using var cmd = new NpgsqlCommand(sql, conn, tx); await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync(); return (null, ""); }
        catch (PostgresException ex) { return (ex.SqlState, ex.MessageText); }
    }

    private async Task<CommercialDocumentView> Submitted(Guid branch, Guid party, Guid item, Guid unit, string key, DateOnly? date = null)
    {
        var input = Doc(branch, party, item, unit, key) with { DocumentDate = date ?? Day };
        var d = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, input));
        return Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, d.Id, d.Revision));
    }

    [PostgresFact]
    public async Task The_date_of_a_numbered_document_stays_inside_the_fiscal_year_of_its_number_in_the_service_and_in_PostgreSQL()
    {
        await Prepare();
        var approver = await NewApprover();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("DY1");
        var s = await Submitted(branch, party, item, unit, "year-key-00001");
        var a = Ok(await Docs.ApproveAsync(approver, Company.Id, s.Id, s.Revision));
        Assert.Equal("2026-000001", a.OfficialNumber);
        Assert.NotNull(a.FiscalYearId);

        // the service refuses a date outside the year of the number; nothing changes
        var other = Doc(branch, party, item, unit, "ignored-key-xx") with { DocumentDate = new DateOnly(2027, 1, 5) };
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, a.Id, a.Revision, other), "documentDate", "date_outside_number_year");
        var still = (await Docs.GetAsync(Company.Id, a.Id))!;
        Assert.Equal((CommercialStatus.Approved, a.Revision, Day), (still.Status, still.Revision, still.DocumentDate));

        // PostgreSQL refuses the same for a direct statement on a numbered draft (a re-approval)
        var moved = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, a.Id, a.Revision, Doc(branch, party, item, unit, "ignored-key-xx", qty: 4m)));   // material edit: back to draft, number kept
        Assert.Equal(("2026-000001", CommercialStatus.Draft), (moved.OfficialNumber, moved.Status));
        // the boundaries of the year 2026-01-01 .. 2026-12-31 (L5), in the service first (a direct statement that commits would move the revision without an event): start - 1 and end + 1 are refused, the end and the start themselves are inclusive
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, a.Id, moved.Revision, Doc(branch, party, item, unit, "ignored-key-xx") with { DocumentDate = new DateOnly(2025, 12, 31) }), "documentDate", "date_outside_number_year");
        Invalid(await Docs.UpdateAsync(Admin.Id, Company.Id, a.Id, moved.Revision, Doc(branch, party, item, unit, "ignored-key-xx") with { DocumentDate = new DateOnly(2027, 1, 1) }), "documentDate", "date_outside_number_year");
        var atEnd = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, a.Id, moved.Revision, Doc(branch, party, item, unit, "ignored-key-xx") with { DocumentDate = new DateOnly(2026, 12, 31) }));
        Assert.Equal(new DateOnly(2026, 12, 31), atEnd.DocumentDate);
        var atStart = Ok(await Docs.UpdateAsync(Admin.Id, Company.Id, a.Id, atEnd.Revision, Doc(branch, party, item, unit, "ignored-key-xx") with { DocumentDate = new DateOnly(2026, 1, 1) }));
        Assert.Equal(new DateOnly(2026, 1, 1), atStart.DocumentDate);
        var (state, message) = await Run($"UPDATE {CD.Table} SET {CD.DocumentDate} = '2027-01-05', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{a.Id}'");
        Assert.Equal("P0001", state);
        Assert.Contains("fiscal year of its number", message);
        string Set(string date) => $"UPDATE {CD.Table} SET {CD.DocumentDate} = '{date}', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{a.Id}'";
        Assert.Equal("P0001", (await Run(Set("2025-12-31"))).State);
        Assert.Equal("P0001", (await Run(Set("2027-01-01"))).State);
        // inclusive boundaries and a date inside the year commit (the last statements of the test)
        Assert.Null((await Run(Set("2026-12-31"))).State);                                                                       // the end date itself (inclusive)
        Assert.Null((await Run(Set("2026-01-01"))).State);                                                                       // the start date itself (inclusive)
        // a date inside the same year is allowed (the last statement: it commits)
        Assert.Null((await Run(Set("2026-09-01"))).State);
    }

    [PostgresFact]
    public async Task The_quote_of_a_submitted_document_is_frozen_when_it_is_decided_direct_SQL()
    {
        await Prepare();
        var approver = await NewApprover();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("FZ1");
        var s = await Submitted(branch, party, item, unit, "freeze-key-0001");
        string Reject(string set) => $"UPDATE {CD.Table} SET {CD.Status} = 4, {set}, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{s.Id}'";
        foreach (var set in new[] { $"{CDA.TotalGross} = {CDA.TotalGross} + 1", $"{CDA.TotalNet} = {CDA.TotalNet} + 1", $"{CDA.TotalTax} = {CDA.TotalTax} + 1", $"{CDA.TaxHash} = '{new string('a', 64)}'", $"{CDA.Fingerprint} = '{new string('b', 64)}'", $"{CDA.ExchangeRate} = 2" })
        {
            var (state, message) = await Run(Reject(set));
            Assert.Equal("P0001", state);
            Assert.Contains("frozen when it is decided", message);
        }
        // approval: an otherwise complete 2 -> 3 transition that rewrites the quote is refused for the same reason
        var approval = $"UPDATE {CD.Table} SET {CD.Status} = 3, {CDA.ApprovedBy} = '{approver}', {CDA.ApprovedAtUtc} = now(), {CDA.SealedAtUtc} = now(), {CDA.OfficialNumber} = 'X-1', {CDA.TaxHash} = '{new string('c', 64)}', {CDA.TotalGross} = {CDA.TotalGross} + 5, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{s.Id}'";
        var (st, msg) = await Run(approval);
        Assert.Equal("P0001", st);
        Assert.Contains("frozen when it is decided", msg);
        Assert.Equal(CommercialStatus.Submitted, (await Docs.GetAsync(Company.Id, s.Id))!.Status);
        // the service path still decides the untouched document
        Ok(await Docs.RejectAsync(approver, Company.Id, s.Id, s.Revision, "not now"));
    }

    [PostgresFact]
    public async Task A_rollback_after_the_number_was_issued_leaves_no_gap_or_reuse()
    {
        await Prepare();
        var approver = await NewApprover();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("RB1");
        var doc = await Submitted(branch, party, item, unit, "rollback-key-001");
        // a foreign row already holds the number the counter will hand out: the issue fails AFTER the counter moved, the whole approval rolls back
        var year = await Sql<Guid>($"SELECT {FYr.Id} FROM {FYr.Table} LIMIT 1");
        await Sql($"INSERT INTO {NI.Table} ({NI.DocType}, {NI.Number}, {NI.BranchId}, {NI.FiscalYearId}, {NI.Value}, {NI.DocumentId}, {NI.IssuedAtUtc}, {NI.IssuedBy}) VALUES ('{CommercialTestType.Name}', '2026-000001', '{Guid.Empty}', '{year}', 77, '{Guid.NewGuid()}', now(), '{Admin.Id}')");
        Fails(await Docs.ApproveAsync(approver, Company.Id, doc.Id, doc.Revision), ConfigStatus.Conflict, "concurrent_change");
        var after = (await Docs.GetAsync(Company.Id, doc.Id))!;
        Assert.Equal((CommercialStatus.Submitted, doc.Revision, (string?)null), (after.Status, after.Revision, after.OfficialNumber));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {NC.Table} WHERE {NC.DocType} = '{CommercialTestType.Name}'"));    // the counter move rolled back with the approval
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {NI.Table} WHERE {NI.DocType} = '{CommercialTestType.Name}'"));    // only the foreign row exists: no orphan issue
    }

    [PostgresFact]
    public async Task Sequences_are_per_fiscal_year_shared_by_branches_and_a_duplicate_number_is_refused_by_the_unique_index()
    {
        await Prepare();
        var approver = await NewApprover();
        var branchA = await NewBranchId("BA"); var branchB = await NewBranchId("BB"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("SQ1");
        var tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        var calendar = new FiscalCalendarService(Resolver, tx, new PeriodCloseGuard(), new FinancialHistoryGuard(), Clock);
        Ok(await calendar.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2027", new DateOnly(2027, 1, 1), "monthly")));
        var d1 = await Submitted(branchA, party, item, unit, "seq-key-000001");
        var d2 = await Submitted(branchB, party, item, unit, "seq-key-000002");
        var d3 = await Submitted(branchA, party, item, unit, "seq-key-000003", new DateOnly(2027, 2, 1));
        var a1 = Ok(await Docs.ApproveAsync(approver, Company.Id, d1.Id, d1.Revision));
        var a2 = Ok(await Docs.ApproveAsync(approver, Company.Id, d2.Id, d2.Revision));
        var a3 = Ok(await Docs.ApproveAsync(approver, Company.Id, d3.Id, d3.Revision));
        Assert.Equal(["2026-000001", "2026-000002", "2027-000001"], [a1.OfficialNumber, a2.OfficialNumber, a3.OfficialNumber]);   // one sequence per fiscal year; the rule of a commercial type is not configurable yet, so the branches share it
        // the same number cannot be issued twice, whatever writes it (unique index on type and number)
        var year = await Sql<Guid>($"SELECT {NI.FiscalYearId} FROM {NI.Table} WHERE {NI.Number} = '2026-000001'");
        Assert.Equal("23505", await Fails($"INSERT INTO {NI.Table} ({NI.DocType}, {NI.Number}, {NI.BranchId}, {NI.FiscalYearId}, {NI.Value}, {NI.DocumentId}, {NI.IssuedAtUtc}, {NI.IssuedBy}) VALUES ('{CommercialTestType.Name}', '2026-000001', '{Guid.Empty}', '{year}', 500, '{Guid.NewGuid()}', now(), '{Admin.Id}')"));
    }
}
