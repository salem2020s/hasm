using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using Ad = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using Ja = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalAllocation;
using Je = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using Jl = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using Jv = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEvent;
using Ni = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;

namespace Hasm.Database.IntegrationTests;

/// <summary>Journal entries and the posting engine (company migration 0008 slice S5, Phase 3.2B) against real PostgreSQL; disposable cluster only.</summary>
public abstract class JournalTestBase : ApprovalTestBase
{
    protected JournalService Journal { get; private set; } = null!;
    protected JournalManagementService JMg { get; private set; } = null!;
    protected FiscalCalendarService Cal { get; private set; } = null!;
    protected CostCenterService Centers { get; private set; } = null!;
    protected ExchangeRateService RateSvc { get; private set; } = null!;
    protected LedgerSettingsService LSet { get; private set; } = null!;
    protected LedgerSettingsManagementService LMg { get; private set; } = null!;
    protected Guid Main, B2;
    protected FiscalYearView Year = null!;
    protected AccountView AnyAcc = null!, Cash = null!, Sales = null!, Expense = null!, UsdOnly = null!, BaseOnly = null!, GroupAcc = null!, Dead = null!, Rounding = null!;
    protected static readonly Guid Maker = Guid.NewGuid(), Checker1 = Guid.NewGuid(), Checker2 = Guid.NewGuid(), Checker3 = Guid.NewGuid();

    protected void BuildJournalFacade()
    {
        BuildApprovalFacade();
        Journal = new JournalService(Resolver, Tx, Clock);
        JMg = new JournalManagementService(Rbac, Journal);
        Cal = new FiscalCalendarService(Resolver, Tx, new PeriodCloseGuard(), new FinancialHistoryGuard(), Clock);
        Centers = new CostCenterService(Resolver, Tx, Clock);
        RateSvc = new ExchangeRateService(Resolver, Tx, Clock);
        LSet = new LedgerSettingsService(Resolver, Tx, Clock);
        LMg = new LedgerSettingsManagementService(Rbac, LSet);
    }

    protected async Task<AccountView> NewAccount(string code, string type, bool group = false, string? policy = null, string? currency = null, bool deactivate = false)
    {
        var a = Ok(await ChartDomain.CreateAsync(Admin.Id, Company.Id, new AccountInput(code, null, "حساب " + code, "Account " + code, type, null, group, policy, currency)));
        return deactivate ? Ok(await ChartDomain.DeactivateAsync(Admin.Id, Company.Id, a.Id, a.Revision)) : a;
    }

    /// <summary>Company with base JOD (3 minor units), USD (rate 0.71 from 2026-01-01) and EUR (no rate) enabled, SAR disabled, fiscal year 2026, a chart and two branches.</summary>
    protected async Task PrepareJournal(bool roundingAccount = false)
    {
        await PrepareRbac();
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        foreach (var (code, isBase, enabled) in new[] { ("JOD", true, true), ("USD", false, true), ("EUR", false, true), ("SAR", false, false) })
            await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, {(enabled ? "true" : "false")}, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset");
        AnyAcc = await NewAccount("1104", "asset", policy: "any");
        Sales = await NewAccount("4101", "revenue");
        Expense = await NewAccount("5101", "expense");
        UsdOnly = await NewAccount("1102", "asset", policy: "specific", currency: "USD");
        BaseOnly = await NewAccount("1103", "asset", policy: "base");
        GroupAcc = await NewAccount("2", "liability", group: true);
        Dead = await NewAccount("5102", "expense", deactivate: true);
        Rounding = await NewAccount("5206", "expense", policy: "any");
        Main = await MainBranch();
        B2 = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{B2}', 'B2', 'Second', true, false, 1, 1, now(), now())");
        Ok(await RateSvc.EnterAsync(Admin.Id, Company.Id, 0, new ExchangeRateInput("USD", 0.71m, new DateOnly(2026, 1, 1), null)));
        if (roundingAccount) Ok(await LSet.PutAsync(Admin.Id, Company.Id, 0, new LedgerSettingsInput(Rounding.Id)));
        Baseline = await Count(Company, Audit_.Table);
    }

    protected JournalInput Entry(string date, params JournalLineInput[] lines) => new(DateOnly.Parse(date), "test entry", Main, lines, null);
    protected static JournalLineInput Dr(Guid account, decimal amount, string? currency = null, Guid? branch = null, params (Guid Cc, decimal Amount)[] allocations) =>
        new(account, null, currency, amount, null, branch, null, null, allocations.Length == 0 ? null : [.. allocations.Select(a => new JournalAllocationInput(a.Cc, a.Amount))]);
    protected static JournalLineInput Cr(Guid account, decimal amount, string? currency = null, Guid? branch = null, params (Guid Cc, decimal Amount)[] allocations) =>
        new(account, null, currency, null, amount, branch, null, null, allocations.Length == 0 ? null : [.. allocations.Select(a => new JournalAllocationInput(a.Cc, a.Amount))]);

    protected JournalInput Simple(string date = "2026-03-10", decimal amount = 100m) => Entry(date, Dr(Cash.Id, amount), Cr(Sales.Id, amount));

    protected async Task<JournalEntryView> Draft(JournalInput? input = null) => Ok(await Journal.CreateDraftAsync(Maker, Company.Id, input ?? Simple(), false));
    protected async Task<JournalEntryView> Submitted(JournalInput? input = null) { var d = await Draft(input); return Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, d.Header.Revision)); }
    protected async Task<JournalEntryView> Posted(JournalInput? input = null, bool soft = false)
    {
        var s = await Submitted(input);
        return Ok(await Journal.PostAsync(Checker3, Company.Id, s.Header.Id, s.Header.Revision, soft));
    }

    protected async Task<ConfigResult<JournalEntryView>> Approve(JournalEntryView v, Guid approver, bool approve = true, string? comment = null) =>
        await Journal.DecideAsync(approver, Company.Id, v.Header.Id, v.Header.Revision, new ApprovalDecisionInput(approve, comment));

    protected async Task<JournalEntryView> Reload(Guid id) => (await Journal.GetAsync(Company.Id, id))!;
    protected Task<long> EntryRows() => Sql<long>($"SELECT count(*) FROM {Je.Table}");
    protected Task<string?> Fails(string sql, params string[] more) => InTx([sql, .. more]);
}

public class JournalLifecycleTests : JournalTestBase
{
    [PostgresFact]
    public async Task A_simple_entry_is_drafted_submitted_and_posted_with_an_official_number_and_a_full_event_log()
    {
        await PrepareJournal();
        var d = await Draft();
        Assert.Equal((JournalStatus.Draft, 1L, null), (d.Header.Status, d.Header.Revision, d.Header.Number));
        Assert.True(d.Balanced);
        Assert.Equal(100m, d.Header.BaseTotal);
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, 1));
        Assert.Equal(JournalStatus.Approved, s.Header.Status);                                            // no policy configured: nothing requires approval
        Assert.Equal(0, s.RequiredApprovals);
        var p = Ok(await Journal.PostAsync(Checker3, Company.Id, d.Header.Id, 1, false));
        Assert.Equal((JournalStatus.Posted, "JV-2026-000001", Checker3), (p.Header.Status, p.Header.Number, p.Header.PostedBy));
        Assert.Equal(Year.Periods[2].Id, p.Header.PeriodId);
        Assert.False(p.Header.PostedInSoftClosedPeriod);
        Assert.Equal([JournalEventKind.Created, JournalEventKind.Submitted, JournalEventKind.Posted], p.Events.Select(e => e.Kind));
        Assert.Equal(1L, await Count(Company, Ni.Table));
        Assert.Equal(3L, await Count(Company, Audit_.Table) - Baseline);                                    // one authenticated audit event per state change
        Assert.Equal(3L, await Sql<long>($"SELECT count(DISTINCT ({Jv.Stream}, {Jv.Seq})) FROM {Jv.Table}"));
    }

    [PostgresFact]
    public async Task Compound_entries_with_many_lines_and_exact_decimals_balance_and_post()
    {
        await PrepareJournal();
        var entry = Entry("2026-02-01", Dr(Cash.Id, 100.125m), Dr(Expense.Id, 49.875m), Cr(Sales.Id, 120.000m), Cr(BaseOnly.Id, 30.000m));
        var p = await Posted(entry);
        Assert.Equal(4, p.Lines.Count);
        Assert.Equal(150m, p.Header.BaseTotal);
        Assert.Equal((150m, 150m), (p.BaseDebitTotal, p.BaseCreditTotal));
        Assert.Equal([1, 2, 3, 4], p.Lines.Select(l => l.LineNo));
    }

    [PostgresFact]
    public async Task Invalid_lines_are_rejected_with_field_errors_and_nothing_is_written()
    {
        await PrepareJournal();
        async Task<ConfigResult<JournalEntryView>> Try(params JournalLineInput[] lines) => await Journal.CreateDraftAsync(Maker, Company.Id, Entry("2026-03-10", lines), false);
        void Fails_(ConfigResult<JournalEntryView> r, string field, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, e => e.Field == field && e.Code == code); }
        Fails_(await Try(Dr(GroupAcc.Id, 10), Cr(Sales.Id, 10)), "lines[0].accountId", "account_not_postable");
        Fails_(await Try(Dr(Dead.Id, 10), Cr(Sales.Id, 10)), "lines[0].accountId", "account_not_postable");
        Fails_(await Try(Dr(Guid.NewGuid(), 10), Cr(Sales.Id, 10)), "lines[0].accountId", "account_not_found");
        Fails_(await Try(Dr(Cash.Id, -5), Cr(Sales.Id, 5)), "lines[0].debit", "must_not_be_negative");
        Fails_(await Try(Dr(Cash.Id, 0), Cr(Sales.Id, 5)), "lines[0].debit", "amount_required");
        Fails_(await Try(new JournalLineInput(Cash.Id, null, null, 5, 5, null, null, null, null), Cr(Sales.Id, 5)), "lines[0].credit", "debit_and_credit");
        Fails_(await Try(Dr(Cash.Id, 10.0001m), Cr(Sales.Id, 10)), "lines[0].debit", "too_many_decimals");     // JOD has 3 minor units
        Fails_(await Try(Dr(Cash.Id, 10, "SAR"), Cr(Sales.Id, 10)), "lines[0].currency", "currency_not_enabled");
        Fails_(await Try(Dr(Cash.Id, 10, "XXX"), Cr(Sales.Id, 10)), "lines[0].currency", "currency_not_enabled");
        Fails_(await Try(Dr(BaseOnly.Id, 10, "USD"), Cr(Sales.Id, 7.1m)), "lines[0].currency", "currency_policy_violation");     // base-only account
        Fails_(await Try(Dr(UsdOnly.Id, 10), Cr(Sales.Id, 10)), "lines[0].currency", "currency_policy_violation");               // USD-only account
        Fails_(await Try(Dr(AnyAcc.Id, 10, "EUR"), Cr(Sales.Id, 10)), "lines[0].currency", "rate_missing");                         // no EUR rate: never assumed
        Fails_(await Try(Dr(Cash.Id, 10, branch: Guid.NewGuid()), Cr(Sales.Id, 10)), "lines[0].branchId", "branch_not_found");
        Fails_(await Journal.CreateDraftAsync(Maker, Company.Id, Entry("2026-03-10"), false), "lines", "required");
        Fails_(await Journal.CreateDraftAsync(Maker, Company.Id, new JournalInput(null, null, Main, [Dr(Cash.Id, 1), Cr(Sales.Id, 1)], null), false), "entryDate", "required");
        Assert.Equal(0L, await EntryRows());
        Assert.Equal(0L, await Count(Company, Audit_.Table) - Baseline);
    }

    [PostgresFact]
    public async Task An_unbalanced_draft_can_be_saved_but_never_submitted_or_posted()
    {
        await PrepareJournal();
        var d = await Draft(Entry("2026-03-10", Dr(Cash.Id, 100), Cr(Sales.Id, 90)));
        Assert.False(d.Balanced);
        Assert.Equal("entry_not_balanced", (await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, 1)).Code);
        Assert.Equal(JournalStatus.Draft, (await Reload(d.Header.Id)).Header.Status);
        var one = await Draft(Entry("2026-03-10", Dr(Cash.Id, 100)));
        Assert.Equal("entry_needs_two_lines", (await Journal.SubmitAsync(Maker, Company.Id, one.Header.Id, 1)).Code);
        // PostgreSQL itself refuses the transitions, whatever the application does.
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{d.Header.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 2, {Je.SubmittedBy} = gen_random_uuid(), {Je.SubmittedAt} = now() WHERE {Je.Id} = '{d.Header.Id}'"));
    }

    [PostgresFact]
    public async Task Drafts_are_edited_with_revision_control_and_submitted_entries_return_to_draft_with_a_new_revision()
    {
        await PrepareJournal();
        var d = await Draft();
        var u = Ok(await Journal.UpdateDraftAsync(Maker, Company.Id, d.Header.Id, 1, Simple(amount: 250m), false));
        Assert.Equal((2L, 250m), (u.Header.Revision, u.Header.BaseTotal));
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Journal.UpdateDraftAsync(Maker, Company.Id, d.Header.Id, 1, Simple(), false)).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Journal.UpdateDraftAsync(Maker, Company.Id, d.Header.Id, null, Simple(), false)).Status);
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, 2));
        Assert.Equal("entry_not_draft", (await Journal.UpdateDraftAsync(Maker, Company.Id, d.Header.Id, 2, Simple(), false)).Code);
        var w = Ok(await Journal.WithdrawAsync(Maker, Company.Id, d.Header.Id, s.Header.Revision));
        Assert.Equal((JournalStatus.Draft, 3L, null), (w.Header.Status, w.Header.Revision, w.Header.SubmittedBy));
        Assert.Equal(3L, (await Journal.UpdateDraftAsync(Maker, Company.Id, d.Header.Id, 3, Simple(), false)).Value!.Header.Revision - 1);
        Assert.Equal(2L, await Count(Company, Jl.Table));                                            // lines are replaced, never accumulated
    }

    [PostgresFact]
    public async Task Cancelling_keeps_the_record_and_blocks_posting_and_frees_nothing_else()
    {
        await PrepareJournal();
        var d = await Draft();
        var c = Ok(await Journal.CancelAsync(Maker, Company.Id, d.Header.Id, 1));
        Assert.Equal(JournalStatus.Cancelled, c.Header.Status);
        Assert.Equal(1L, await EntryRows());
        Assert.Equal("entry_not_draft", (await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, 1)).Code);
        Assert.Equal("entry_cancelled", (await Journal.PostAsync(Maker, Company.Id, d.Header.Id, 1, false)).Code);
        Assert.Equal("entry_not_cancellable", (await Journal.CancelAsync(Maker, Company.Id, d.Header.Id, 1)).Code);
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 1 WHERE {Je.Id} = '{d.Header.Id}'"));
        Assert.Equal(0L, await Count(Company, Ni.Table));                                              // a number is only issued by posting
    }

    [PostgresFact]
    public async Task A_posted_entry_and_its_lines_are_immutable_and_cannot_be_deleted_even_by_SQL()
    {
        await PrepareJournal();
        var p = await Posted(Entry("2026-03-10", Dr(Cash.Id, 100, allocations: []), Cr(Sales.Id, 100)));
        var id = p.Header.Id; var line = p.Lines[0].Id;
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Desc} = 'changed' WHERE {Je.Id} = '{id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Rev} = {Je.Rev} + 1 WHERE {Je.Id} = '{id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 1 WHERE {Je.Id} = '{id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 6 WHERE {Je.Id} = '{id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Number} = 'X' WHERE {Je.Id} = '{id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Je.Table} WHERE {Je.Id} = '{id}'"));
        Assert.Equal("0A000", await Fails($"TRUNCATE {Je.Table}"));   // referenced by foreign keys: PostgreSQL itself refuses
        Assert.Equal("P0001", await Fails($"UPDATE {Jl.Table} SET {Jl.Debit} = 1 WHERE {Jl.Id} = '{line}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Jl.Table} WHERE {Jl.Id} = '{line}'"));
        Assert.Equal("0A000", await Fails($"TRUNCATE {Jl.Table}"));
        Assert.Equal("P0001", await Fails($"INSERT INTO {Jl.Table} ({Jl.Entry}, {Jl.No}, {Jl.Account}, {Jl.Cur}, {Jl.Debit}, {Jl.Credit}, {Jl.Rate}, {Jl.BaseDebit}, {Jl.BaseCredit}, {Jl.Branch}) VALUES ('{id}', 9, '{Cash.Id}', 'JOD', 1, 0, 1, 1, 0, '{Main}')"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Jv.Table}"));
        Assert.Equal(p.Header.Number, (await Reload(id)).Header.Number);
    }

    [PostgresFact]
    public async Task Posting_twice_is_idempotent_and_parallel_posts_issue_one_number_and_one_audit_event()
    {
        await PrepareJournal();
        var s = await Submitted();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Journal.PostAsync(Checker3, Company.Id, s.Header.Id, s.Header.Revision, false)));
        Assert.All(results, r => Assert.True(r.IsOk, r.Code));
        Assert.Single(results.Select(r => r.Value!.Header.Number).Distinct());
        Assert.Equal(1L, await Count(Company, Ni.Table));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Jv.Table} WHERE {Jv.Event} = 8"));
        Assert.Equal("JV-2026-000001", (await Journal.PostAsync(Checker3, Company.Id, s.Header.Id, s.Header.Revision, false)).Value!.Header.Number);
    }

    [PostgresFact]
    public async Task Concurrent_postings_get_unique_gapless_numbers_and_a_failed_posting_consumes_none()
    {
        await PrepareJournal();
        var entries = new List<JournalEntryView>();
        for (var i = 0; i < 10; i++) entries.Add(await Submitted(Simple(amount: 10 + i)));
        var failing = await Submitted(Simple("2026-04-10"));
        var closed = Year.Periods[3];
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, closed.Id, closed.Revision));
        Assert.Equal("fiscal_period_soft_closed", (await Journal.PostAsync(Maker, Company.Id, failing.Header.Id, failing.Header.Revision, false)).Code);   // the number is not consumed
        var posted = await Task.WhenAll(entries.Select(e => Journal.PostAsync(Checker3, Company.Id, e.Header.Id, e.Header.Revision, false)));
        Assert.All(posted, r => Assert.True(r.IsOk, r.Code));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"JV-2026-{i:D6}"), posted.Select(r => r.Value!.Header.Number!).Order());
        Assert.Equal(10L, await Count(Company, Ni.Table));
        Assert.Equal(10L, await Sql<long>($"SELECT {CompanySchema.NumberCounter.LastValue} FROM {CompanySchema.NumberCounter.Table}"));
    }
}

public class JournalAuditIntegrityTests : JournalTestBase
{
    [PostgresFact]
    public async Task The_whole_journal_workflow_including_refused_and_rolled_back_operations_leaves_an_unbroken_verified_audit_chain()
    {
        await PrepareJournal();
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "any", null, 0m, 1, "سياسة", "Policy")));
        var s = await Submitted();
        Assert.Equal("separation_of_duties", (await Approve(s, Maker)).Code);                                   // refused: writes nothing
        var approved = Ok(await Approve(s, Checker1));
        var closed = Year.Periods[2];
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, closed.Id, closed.Revision));
        Assert.Equal("fiscal_period_soft_closed", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, approved.Header.Revision, false)).Code);   // rolled back: number, audit event and state together
        var posted = Ok(await Journal.PostAsync(Maker, Company.Id, s.Header.Id, approved.Header.Revision, true));
        var reversal = Ok(await Journal.ReverseAsync(Maker, Company.Id, s.Header.Id, posted.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 2), null)));
        Ok(await Journal.CancelAsync(Maker, Company.Id, reversal.Header.Id, reversal.Header.Revision));
        var report = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.True(report.Verdict == AuditVerdict.Verified || report.Findings.All(f => f.Severity == FindingSeverity.Note), string.Join(", ", report.Findings.Select(f => $"{f.Code}@{f.Seq}")));
        Assert.Equal(await Count(Company, Audit_.Table), report.Events);                                           // every event is chained: none missing, none extra
        Assert.Equal(report.Events, report.VerifiedThroughSeq);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ni.Table}"));                                     // only the successful posting issued a number
    }
}

public class JournalPeriodTests : JournalTestBase
{
    [PostgresFact]
    public async Task Posting_respects_open_soft_closed_final_closed_and_missing_fiscal_periods_inside_the_posting_transaction()
    {
        await PrepareJournal();
        var jan = Year.Periods[0]; var feb = Year.Periods[1]; var mar = Year.Periods[2];
        var inJan = await Submitted(Simple("2026-01-15")); var inFeb = await Submitted(Simple("2026-02-15")); var inMar = await Submitted(Simple("2026-03-15"));
        var outside = await Submitted(Simple("2027-06-15"));
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, feb.Id, feb.Revision));
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, jan.Id, jan.Revision));
        Ok(await Cal.FinalCloseAsync(Admin.Id, Company.Id, jan.Id, 2));
        Assert.Equal("fiscal_period_final_closed", (await Journal.PostAsync(Maker, Company.Id, inJan.Header.Id, 1, true)).Code);      // never, even with the soft-closed permission
        Assert.Equal("fiscal_period_soft_closed", (await Journal.PostAsync(Maker, Company.Id, inFeb.Header.Id, 1, false)).Code);
        var soft = Ok(await Journal.PostAsync(Maker, Company.Id, inFeb.Header.Id, 1, true));
        Assert.True(soft.Header.PostedInSoftClosedPeriod);                                                                                 // recorded on the entry
        Assert.False(Ok(await Journal.PostAsync(Maker, Company.Id, inMar.Header.Id, 1, false)).Header.PostedInSoftClosedPeriod);
        Assert.Equal("no_fiscal_period", (await Journal.PostAsync(Maker, Company.Id, outside.Header.Id, 1, true)).Code);
        // PostgreSQL enforces the same rules: even a direct state change with a lying period or soft-closed flag is refused.
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 4, {Je.Number} = 'X-1', {Je.PostedBy} = gen_random_uuid(), {Je.PostedAt} = now(), {Je.Period} = '{jan.Id}' WHERE {Je.Id} = '{inJan.Header.Id}'"));
        Assert.Equal(JournalStatus.Approved, (await Reload(inJan.Header.Id)).Header.Status);
    }

    [PostgresFact]
    public async Task A_period_closed_after_submission_blocks_the_post_and_a_post_in_flight_blocks_the_close()
    {
        await PrepareJournal();
        var s = await Submitted(Simple("2026-05-10"));
        var may = Year.Periods[4];
        Ok(await Cal.SoftCloseAsync(Admin.Id, Company.Id, may.Id, may.Revision));
        Assert.Equal("fiscal_period_soft_closed", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, 1, false)).Code);
        Ok(await Cal.ReopenAsync(Admin.Id, Company.Id, may.Id, 2));
        // A posting that holds the period (FOR SHARE) makes a concurrent close wait for it; the close then sees the committed posting.
        await using var conn = await Resolver.OpenAsync(Company.Id);
        await using var tx = await conn.BeginTransactionAsync();
        Assert.True((await FiscalPeriodGate.CheckAsync(conn, tx, new DateOnly(2026, 5, 10), false)).Allowed);
        var closing = Task.Run(() => Cal.SoftCloseAsync(Admin.Id, Company.Id, may.Id, 3));
        await Task.Delay(500);
        Assert.False(closing.IsCompleted);                                                                                                  // blocked behind the in-flight posting
        await tx.CommitAsync();
        Assert.True((await closing).IsOk);
    }
}

public class JournalMultiCurrencyTests : JournalTestBase
{
    [PostgresFact]
    public async Task A_foreign_line_uses_the_effective_rate_converts_exactly_and_preserves_the_rate_snapshot()
    {
        await PrepareJournal();
        var p = await Posted(Entry("2026-03-10", Dr(Cash.Id, 100m), Dr(UsdOnly.Id, 1000m, "USD"), Cr(Sales.Id, 810m)));
        var usd = p.Lines.Single(l => l.Currency == "USD");
        Assert.Equal((1000m, 0.71m, 710m, 0m), (usd.Debit, usd.Rate, usd.BaseDebit, usd.BaseCredit));
        Assert.NotNull(usd.RateId);
        Assert.Equal((810m, 810m), (p.BaseDebitTotal, p.BaseCreditTotal));
        // Later and backdated rate entries never change the posted snapshot.
        Ok(await RateSvc.EnterAsync(Admin.Id, Company.Id, await LatestId("USD"), new ExchangeRateInput("USD", 0.90m, new DateOnly(2026, 1, 1), "correction")));
        Ok(await RateSvc.EnterAsync(Admin.Id, Company.Id, await LatestId("USD"), new ExchangeRateInput("USD", 0.95m, new DateOnly(2026, 3, 1), null)));
        var again = await Reload(p.Header.Id);
        var u2 = again.Lines.Single(l => l.Currency == "USD");
        Assert.Equal((usd.RateId, usd.Rate, usd.BaseDebit), (u2.RateId, u2.Rate, u2.BaseDebit));
        Assert.Equal("P0001", await Fails($"UPDATE {Jl.Table} SET {Jl.Rate} = 0.95 WHERE {Jl.Id} = '{usd.Id}'"));
    }

    [PostgresFact]
    public async Task A_rate_that_changed_after_the_draft_was_saved_must_be_refreshed_before_posting()
    {
        await PrepareJournal();
        var s = await Submitted(Entry("2026-03-10", Dr(UsdOnly.Id, 1000m, "USD"), Cr(Sales.Id, 710m)));
        Ok(await RateSvc.EnterAsync(Admin.Id, Company.Id, await LatestId("USD"), new ExchangeRateInput("USD", 0.72m, new DateOnly(2026, 3, 1), null)));
        Assert.Equal("rate_changed", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, s.Header.Revision, false)).Code);
        var w = Ok(await Journal.WithdrawAsync(Maker, Company.Id, s.Header.Id, s.Header.Revision));
        var refreshed = Ok(await Journal.UpdateDraftAsync(Maker, Company.Id, s.Header.Id, w.Header.Revision, Entry("2026-03-10", Dr(UsdOnly.Id, 1000m, "USD"), Cr(Sales.Id, 720m)), false));
        Assert.Equal(720m, refreshed.Header.BaseTotal);
        var submitted = Ok(await Journal.SubmitAsync(Maker, Company.Id, s.Header.Id, refreshed.Header.Revision));
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Maker, Company.Id, s.Header.Id, submitted.Header.Revision, false)).Header.Status);
    }

    [PostgresFact]
    public async Task A_manual_rate_override_needs_the_permission_and_a_reason_and_is_recorded_on_the_line()
    {
        await PrepareJournal();
        JournalInput WithOverride(decimal? rate, string? reason) => Entry("2026-03-10",
            new JournalLineInput(UsdOnly.Id, null, "USD", 1000m, null, null, rate, reason, null), Cr(Sales.Id, 800m));
        Assert.Equal("rate_override_forbidden", (await Journal.CreateDraftAsync(Maker, Company.Id, WithOverride(0.8m, "negotiated rate"), false)).Code);
        Assert.Equal(ConfigStatus.Invalid, (await Journal.CreateDraftAsync(Maker, Company.Id, WithOverride(0.8m, null), true)).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Journal.CreateDraftAsync(Maker, Company.Id, WithOverride(0m, "negotiated rate"), true)).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Journal.CreateDraftAsync(Maker, Company.Id, WithOverride(0.12345678901m, "negotiated rate"), true)).Status);
        var d = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, WithOverride(0.8m, "negotiated rate"), true));
        var l = d.Lines.Single(x => x.Currency == "USD");
        Assert.Equal((0.8m, null, "negotiated rate", 800m), (l.Rate, l.RateId, l.RateOverrideReason, l.BaseDebit));
        var posted = Ok(await Journal.PostAsync(Maker, Company.Id, Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, 1)).Header.Id, 1, false));
        Assert.Equal(JournalStatus.Posted, posted.Header.Status);
        // A foreign line without an effective rate AND without an override never posts (checked by PostgreSQL as well).
        var foreign = await Draft(Entry("2026-03-10", Dr(UsdOnly.Id, 100m, "USD"), Cr(Sales.Id, 71m)));
        var line = foreign.Lines.Single(x => x.Currency == "USD");
        Assert.Equal("P0001", await Fails($"UPDATE {Jl.Table} SET {Jl.RateId} = NULL WHERE {Jl.Id} = '{line.Id}'", $"UPDATE {Je.Table} SET {Je.Status} = 2, {Je.SubmittedBy} = gen_random_uuid(), {Je.SubmittedAt} = now() WHERE {Je.Id} = '{foreign.Header.Id}'"));
    }

    [PostgresFact]
    public async Task Base_amounts_round_half_away_from_zero_and_a_system_rounding_line_absorbs_the_difference_only_when_configured()
    {
        await PrepareJournal(roundingAccount: false);
        // USD 0.05 at 0.71 = JOD 0.0355 -> 0.036 (half away from zero) while the credit is JOD 0.035.
        // The 0.001 difference cannot be balanced without a configured rounding account.
        var unbalanced = await Draft(Entry("2026-03-10", Dr(UsdOnly.Id, 0.05m, "USD"), Cr(Sales.Id, 0.035m)));
        Assert.False(unbalanced.Balanced);                                                                                              // no rounding account: stays visibly unbalanced
        Assert.Equal("entry_not_balanced", (await Journal.SubmitAsync(Maker, Company.Id, unbalanced.Header.Id, 1)).Code);
    }

    [PostgresFact]
    public async Task With_a_rounding_account_a_one_minor_unit_difference_gets_one_explicit_system_line_and_larger_gaps_stay_unbalanced()
    {
        await PrepareJournal(roundingAccount: true);
        var d = await Draft(Entry("2026-03-10", Dr(UsdOnly.Id, 0.05m, "USD"), Cr(Sales.Id, 0.035m)));      // base 0.036 vs 0.035: 0.001 difference
        Assert.True(d.Balanced);
        var rounding = Assert.Single(d.Lines, l => l.IsRounding);
        Assert.Equal((Rounding.Id, 0m, 0.001m), (rounding.AccountId, rounding.BaseDebit, rounding.BaseCredit));
        var posted = Ok(await Journal.PostAsync(Maker, Company.Id, Ok(await Journal.SubmitAsync(Maker, Company.Id, d.Header.Id, 1)).Header.Id, 1, false));
        Assert.Equal(JournalStatus.Posted, posted.Header.Status);
        var wide = await Draft(Entry("2026-03-10", Dr(UsdOnly.Id, 100m, "USD"), Cr(Sales.Id, 70m)));   // 71 vs 70: a real error, never "rounded away"
        Assert.False(wide.Balanced);
        Assert.DoesNotContain(wide.Lines, l => l.IsRounding);
    }

    private async Task<long> LatestId(string currency) => (await RateSvc.OverviewAsync(Company.Id))!.Currencies.Single(c => c.CurrencyCode == currency).LatestId;
}

public class JournalAllocationTests : JournalTestBase
{
    private async Task<CostCenterView> Center(string code, bool group = false, Guid? parent = null) => Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput(code, parent, "مركز", "Center", null, group)));

    [PostgresFact]
    public async Task Allocations_sum_exactly_to_the_line_in_both_currencies_and_are_preserved_with_the_posted_entry()
    {
        await PrepareJournal();
        var a = await Center("A"); var b = await Center("B"); var c = await Center("C");
        var p = await Posted(Entry("2026-03-10", Dr(Expense.Id, 100m, allocations: [(a.Id, 33.333m), (b.Id, 33.333m), (c.Id, 33.334m)]), Cr(Cash.Id, 100m)));
        var line = p.Lines[0];
        Assert.Equal(100m, line.Allocations.Sum(x => x.Amount));
        Assert.Equal(100m, line.Allocations.Sum(x => x.BaseAmount));
        // Foreign line: base shares are rounded individually and the last share absorbs the remainder, so the base total is exact.
        var f = await Posted(Entry("2026-03-10", Dr(UsdOnly.Id, 100m, "USD", allocations: [(a.Id, 33.33m), (b.Id, 33.33m), (c.Id, 33.34m)]), Cr(Sales.Id, 71m)));
        var fl = f.Lines.Single(x => x.Currency == "USD");
        Assert.Equal(71m, fl.BaseDebit);
        Assert.Equal(71m, fl.Allocations.Sum(x => x.BaseAmount));
        Assert.Equal(100m, fl.Allocations.Sum(x => x.Amount));
        // History stays reproducible after the cost center is renamed, moved and deactivated.
        Ok(await Centers.UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, new CostCenterDetailsInput("اسم", "Renamed", null, null)));
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, b.Id, 1));
        Assert.Equal(line.Allocations.Select(x => (x.CostCenterId, x.Amount)), (await Reload(p.Header.Id)).Lines[0].Allocations.Select(x => (x.CostCenterId, x.Amount)));
    }

    [PostgresFact]
    public async Task Incomplete_duplicate_group_inactive_and_unknown_allocations_are_refused()
    {
        await PrepareJournal();
        var a = await Center("A"); var g = await Center("G", group: true); var off = await Center("OFF");
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, off.Id, 1));
        async Task<ConfigResult<JournalEntryView>> Try(params (Guid, decimal)[] allocs) => await Journal.CreateDraftAsync(Maker, Company.Id, Entry("2026-03-10", Dr(Expense.Id, 100m, allocations: allocs), Cr(Cash.Id, 100m)), false);
        void Err(ConfigResult<JournalEntryView> r, string code) { Assert.Equal(ConfigStatus.Invalid, r.Status); Assert.Contains(r.Fields!, e => e.Code == code); }
        Err(await Try((a.Id, 60m)), "allocation_sum_mismatch");                                   // incomplete
        Err(await Try((a.Id, 60m), (a.Id, 40m)), "duplicate_cost_center");
        Err(await Try((g.Id, 100m)), "cost_center_not_allocatable");                              // groups never receive allocations
        Err(await Try((off.Id, 100m)), "cost_center_not_allocatable");                            // inactive ones receive no new postings
        Err(await Try((Guid.NewGuid(), 100m)), "cost_center_not_found");
        Err(await Try((a.Id, 100.0001m)), "too_many_decimals");
        Err(await Try((a.Id, 0m), (a.Id, 100m)), "must_be_positive");
        Assert.Equal(0L, await EntryRows());
    }

    [PostgresFact]
    public async Task A_cost_center_deactivated_after_the_draft_cannot_receive_the_posting_and_the_database_agrees()
    {
        await PrepareJournal();
        var a = await Center("A");
        var s = await Submitted(Entry("2026-03-10", Dr(Expense.Id, 100m, allocations: [(a.Id, 100m)]), Cr(Cash.Id, 100m)));
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, a.Id, 1));
        Assert.Equal("cost_center_not_allocatable", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, s.Header.Revision, false)).Code);
        Assert.Equal(JournalStatus.Approved, (await Reload(s.Header.Id)).Header.Status);
        // Allocations of a posted entry are immutable and the set always sums (checked at commit).
        var ok = await Draft(Entry("2026-03-10", Dr(Expense.Id, 100m), Cr(Cash.Id, 100m)));
        var center = await Center("B");
        Assert.Equal("P0001", await Fails($"INSERT INTO {Ja.Table} ({Ja.Line}, {Ja.CC}, {Ja.Amount}, {Ja.Base}) VALUES ('{ok.Lines[0].Id}', '{center.Id}', 40, 40)"));      // 40 of 100: not complete
        Assert.Null(await Fails($"INSERT INTO {Ja.Table} ({Ja.Line}, {Ja.CC}, {Ja.Amount}, {Ja.Base}) VALUES ('{ok.Lines[0].Id}', '{center.Id}', 100, 100)"));
    }

    [PostgresFact]
    public async Task Line_branches_are_validated_and_preserved()
    {
        await PrepareJournal();
        var p = await Posted(Entry("2026-03-10", Dr(Cash.Id, 100m, branch: B2), Cr(Sales.Id, 100m, branch: Main)));
        Assert.Equal([B2, Main], p.Lines.Select(l => l.BranchId));
        Assert.Equal(Main, p.Header.BranchId);
        var dormant = Guid.NewGuid();
        await Sql($"INSERT INTO {Br.Table} ({Br.Id}, {Br.Code}, {Br.NameEn}, {Br.IsActive}, {Br.IsMain}, {Br.ActivationEpoch}, {Br.Revision}, {Br.CreatedAtUtc}, {Br.UpdatedAtUtc}) VALUES ('{dormant}', 'B3', 'Dormant', false, false, 1, 1, now(), now())");
        var r = await Journal.CreateDraftAsync(Maker, Company.Id, Entry("2026-03-10", Dr(Cash.Id, 1m, branch: dormant), Cr(Sales.Id, 1m)), false);
        Assert.Contains(r.Fields!, e => e is { Field: "lines[0].branchId", Code: "branch_inactive" });
    }
}

public class JournalApprovalTests : JournalTestBase
{
    private async Task<JournalEntryView> NeedsApprovals(int n, string source = "any", decimal min = 0m)
    {
        await PrepareJournal();
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, source, null, min, n, "سياسة", "Policy")));
        return await Submitted();
    }

    [PostgresFact]
    public async Task A_matching_policy_makes_submission_pending_and_posting_needs_the_required_distinct_approvers_for_exactly_this_revision()
    {
        var s = await NeedsApprovals(2);
        Assert.Equal((JournalStatus.PendingApproval, 2), (s.Header.Status, s.RequiredApprovals));
        Assert.Equal("entry_not_approved", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, s.Header.Revision, false)).Code);
        var one = Ok(await Approve(s, Checker1));
        Assert.Equal(JournalStatus.PendingApproval, one.Header.Status);
        Assert.Equal("already_decided", (await Approve(one, Checker1)).Code);
        var two = Ok(await Approve(one, Checker2));
        Assert.Equal(JournalStatus.Approved, two.Header.Status);
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Checker3, Company.Id, s.Header.Id, s.Header.Revision, false)).Header.Status);
        Assert.Equal([JournalEventKind.Created, JournalEventKind.Submitted, JournalEventKind.DecisionRecorded, JournalEventKind.DecisionRecorded, JournalEventKind.Approved, JournalEventKind.Posted],
            (await Reload(s.Header.Id)).Events.Select(e => e.Kind));
    }

    [PostgresFact]
    public async Task The_creator_and_the_submitter_can_never_approve_their_own_entry()
    {
        await PrepareJournal();
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "any", null, 0m, 1, "سياسة", "Policy")));
        var d = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, Simple(), false));
        var submitter = Guid.NewGuid();
        var s = Ok(await Journal.SubmitAsync(submitter, Company.Id, d.Header.Id, 1));
        Assert.Equal("separation_of_duties", (await Approve(s, Maker)).Code);
        Assert.Equal("separation_of_duties", (await Approve(s, submitter)).Code);
        Assert.Equal(0L, await Count(Company, Ad.Table));
        Assert.Equal(JournalStatus.Approved, Ok(await Approve(s, Checker1)).Header.Status);
    }

    [PostgresFact]
    public async Task Editing_after_approval_invalidates_it_and_a_stale_approval_can_never_authorize_posting()
    {
        var s = await NeedsApprovals(1);
        var approved = Ok(await Approve(s, Checker1));
        Assert.Equal(JournalStatus.Approved, approved.Header.Status);
        var w = Ok(await Journal.WithdrawAsync(Maker, Company.Id, s.Header.Id, approved.Header.Revision));
        var edited = Ok(await Journal.UpdateDraftAsync(Maker, Company.Id, s.Header.Id, w.Header.Revision, Simple(amount: 999m), false));
        var resubmitted = Ok(await Journal.SubmitAsync(Maker, Company.Id, s.Header.Id, edited.Header.Revision));
        Assert.Equal(JournalStatus.PendingApproval, resubmitted.Header.Status);                                  // the earlier approval belonged to another revision
        Assert.Equal(1L, await Count(Company, Ad.Table));
        // Even a direct state change cannot reuse it: PostgreSQL checks the approvals of the CURRENT revision.
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{s.Header.Id}'"));
        Assert.Equal("entry_not_approved", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, resubmitted.Header.Revision, false)).Code);
    }

    [PostgresFact]
    public async Task A_rejection_returns_the_entry_to_draft_with_a_new_revision_and_needs_a_comment()
    {
        var s = await NeedsApprovals(1);
        Assert.Equal(ConfigStatus.Invalid, (await Approve(s, Checker1, approve: false, comment: " ")).Status);
        var r = Ok(await Approve(s, Checker1, approve: false, comment: "wrong account"));
        Assert.Equal((JournalStatus.Draft, s.Header.Revision + 1), (r.Header.Status, r.Header.Revision));
        Assert.Contains(r.Events, e => e.Kind == JournalEventKind.Rejected);
        Assert.Equal("entry_not_pending_approval", (await Approve(r, Checker2)).Code);
    }

    [PostgresFact]
    public async Task Tightening_the_policy_after_approval_requires_the_stricter_approvals_at_posting_time()
    {
        await PrepareJournal();
        var policy = Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "any", null, 0m, 1, "سياسة", "Policy")));
        var s = await Submitted();
        var approved = Ok(await Approve(s, Checker1));
        Ok(await Policies.UpdateAsync(Admin.Id, Company.Id, policy.Id, 1, new ApprovalPolicyDetailsInput("any", null, 0m, 2, "سياسة", "Policy")));
        Assert.Equal("approval_required", (await Journal.PostAsync(Maker, Company.Id, s.Header.Id, approved.Header.Revision, false)).Code);
        Assert.Equal(JournalStatus.Approved, (await Reload(s.Header.Id)).Header.Status);
        // Deactivating the policy lifts the requirement; with a second approver the original requirement is also met.
        Ok(await Policies.DeactivateAsync(Admin.Id, Company.Id, policy.Id, 2));
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Maker, Company.Id, s.Header.Id, approved.Header.Revision, false)).Header.Status);
    }

    [PostgresFact]
    public async Task Automatic_entries_follow_the_same_policies_stay_pending_without_approval_and_are_idempotent_per_source_key()
    {
        await PrepareJournal();
        Ok(await Policies.CreateAsync(Admin.Id, Company.Id, new ApprovalPolicyInput(DocumentTypes.JournalEntry, "automatic", null, 0m, 1, "سياسة", "Auto policy")));
        var auto = Ok(await Journal.CreateAutomaticAsync(Maker, Company.Id, new AutomaticJournalInput("sales-invoice:1001", Simple()), false));
        Assert.Equal((DocumentSource.Automatic, "sales-invoice:1001"), (auto.Header.Source, auto.Header.SourceKey));
        var again = Ok(await Journal.CreateAutomaticAsync(Maker, Company.Id, new AutomaticJournalInput("sales-invoice:1001", Simple(amount: 5m)), false));
        Assert.Equal(auto.Header.Id, again.Header.Id);                                                           // a retried business event never creates a second entry
        Assert.Equal(1L, await EntryRows());
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, auto.Header.Id, 1));
        Assert.Equal(JournalStatus.PendingApproval, s.Header.Status);                                            // not approved: it stays an explicit pending draft
        Assert.Equal("entry_not_approved", (await Journal.PostAsync(Maker, Company.Id, auto.Header.Id, 1, false)).Code);
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 3 WHERE {Je.Id} = '{auto.Header.Id}'"));
        var approved = Ok(await Approve(s, Checker1));
        Assert.Equal(JournalStatus.Approved, approved.Header.Status);
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Maker, Company.Id, auto.Header.Id, approved.Header.Revision, false)).Header.Status);
        // Manual entries are not covered by the automatic-only policy.
        Assert.Equal(JournalStatus.Approved, (await Submitted()).Header.Status);
    }

    [PostgresFact]
    public async Task The_source_key_is_unique_among_non_cancelled_entries_in_the_database()
    {
        await PrepareJournal();
        var a = Ok(await Journal.CreateAutomaticAsync(Maker, Company.Id, new AutomaticJournalInput("evt-1", Simple()), false));
        Assert.Equal("23505", await Fails($"INSERT INTO {Je.Table} ({Je.Source}, {Je.SourceKey}, {Je.Date}, {Je.Branch}, {Je.BaseCur}, {Je.CreatedBy}, {Je.CreatedAt}, {Je.UpdatedAt}) VALUES (2, 'evt-1', '2026-03-10', '{Main}', 'JOD', gen_random_uuid(), now(), now())"));
        Assert.Equal("23514", await Fails($"INSERT INTO {Je.Table} ({Je.Source}, {Je.Date}, {Je.Branch}, {Je.BaseCur}, {Je.CreatedBy}, {Je.CreatedAt}, {Je.UpdatedAt}) VALUES (2, '2026-03-10', '{Main}', 'JOD', gen_random_uuid(), now(), now())"));   // automatic needs a key
        Ok(await Journal.CancelAsync(Maker, Company.Id, a.Header.Id, 1));
        var b = Ok(await Journal.CreateAutomaticAsync(Maker, Company.Id, new AutomaticJournalInput("evt-1", Simple()), false));
        Assert.NotEqual(a.Header.Id, b.Header.Id);
    }
}

public class JournalReversalTests : JournalTestBase
{
    [PostgresFact]
    public async Task Reversing_a_posted_entry_drafts_a_mirrored_entry_and_posting_it_marks_the_original_reversed()
    {
        await PrepareJournal();
        var p = await Posted(Entry("2026-03-10", Dr(UsdOnly.Id, 1000m, "USD"), Cr(Sales.Id, 710m)));
        var r = Ok(await Journal.ReverseAsync(Maker, Company.Id, p.Header.Id, p.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 2), null)));
        Assert.Equal((JournalType.Reversal, JournalStatus.Draft, p.Header.Id), (r.Header.Type, r.Header.Status, r.Header.ReversesId));
        Assert.Equal(p.Lines.Select(l => (l.AccountId, l.Currency, l.Debit, l.Credit)), r.Lines.Select(l => (l.AccountId, l.Currency, l.Credit, l.Debit)));   // sides swapped
        Assert.Equal(p.Lines.Select(l => l.RateId), r.Lines.Select(l => l.RateId));                                      // the original rate snapshot, so it cancels exactly
        Assert.Equal(p.Lines.Select(l => (Guid?)l.Id), r.Lines.Select(l => l.ReversesLineId));
        Assert.Equal("already_reversed", (await Journal.ReverseAsync(Maker, Company.Id, p.Header.Id, p.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 3), null))).Code);
        Assert.Equal("reversal_not_editable", (await Journal.UpdateDraftAsync(Maker, Company.Id, r.Header.Id, 1, Simple(), false)).Code);
        var rp = Ok(await Journal.PostAsync(Maker, Company.Id, Ok(await Journal.SubmitAsync(Maker, Company.Id, r.Header.Id, 1)).Header.Id, 1, false));
        Assert.Equal(("JV-2026-000002", JournalStatus.Posted), (rp.Header.Number, rp.Header.Status));
        var original = await Reload(p.Header.Id);
        Assert.Equal(JournalStatus.Reversed, original.Header.Status);
        Assert.Contains(original.Events, e => e.Kind == JournalEventKind.Reversed);
        Assert.Equal("entry_not_reversible", (await Journal.ReverseAsync(Maker, Company.Id, p.Header.Id, original.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 3), null))).Code);
        Assert.Equal("entry_not_reversible", (await Journal.ReverseAsync(Maker, Company.Id, rp.Header.Id, rp.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 3), null))).Code);   // a reversal is corrected with an adjusting entry
    }

    [PostgresFact]
    public async Task A_reversal_can_still_post_after_its_account_or_cost_center_was_deactivated_but_not_into_a_closed_period()
    {
        await PrepareJournal();
        var extra = await NewAccount("5103", "expense");
        var cc = Ok(await Centers.CreateAsync(Admin.Id, Company.Id, new CostCenterInput("RC", null, "م", "C", null, false)));
        var p = await Posted(Entry("2026-03-10", Dr(extra.Id, 100m, allocations: [(cc.Id, 100m)]), Cr(Cash.Id, 100m)));
        Ok(await ChartDomain.DeactivateAsync(Admin.Id, Company.Id, extra.Id, extra.Revision));
        Ok(await Centers.DeactivateAsync(Admin.Id, Company.Id, cc.Id, 1));
        var r = Ok(await Journal.ReverseAsync(Maker, Company.Id, p.Header.Id, p.Header.Revision, new ReversalInput(new DateOnly(2026, 6, 1), null)));
        var s = Ok(await Journal.SubmitAsync(Maker, Company.Id, r.Header.Id, 1));
        Assert.Equal(JournalStatus.Posted, Ok(await Journal.PostAsync(Maker, Company.Id, r.Header.Id, s.Header.Revision, false)).Header.Status);
        // A new entry to the deactivated account is still refused.
        Assert.Equal(ConfigStatus.Invalid, (await Journal.CreateDraftAsync(Maker, Company.Id, Entry("2026-06-02", Dr(extra.Id, 1m), Cr(Cash.Id, 1m)), false)).Status);
    }

    [PostgresFact]
    public async Task PostgreSQL_refuses_a_reversal_that_does_not_mirror_its_original()
    {
        await PrepareJournal();
        var p = await Posted();
        var r = Ok(await Journal.ReverseAsync(Maker, Company.Id, p.Header.Id, p.Header.Revision, new ReversalInput(new DateOnly(2026, 4, 2), null)));
        // Tamper with a draft reversal line (allowed while a draft): the mirror check refuses the submission.
        Assert.Equal("P0001", await Fails($"UPDATE {Jl.Table} SET {Jl.Account} = '{Expense.Id}' WHERE {Jl.Id} = '{r.Lines[0].Id}'",
            $"UPDATE {Je.Table} SET {Je.Status} = 2, {Je.SubmittedBy} = gen_random_uuid(), {Je.SubmittedAt} = now() WHERE {Je.Id} = '{r.Header.Id}'"));
        Assert.Equal(JournalStatus.Draft, (await Reload(r.Header.Id)).Header.Status);
        // Posting a reversal without marking the original reversed is refused at commit.
        Assert.Equal(JournalStatus.Posted, (await Reload(p.Header.Id)).Header.Status);
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Status} = 5 WHERE {Je.Id} = '{p.Header.Id}'"));          // no posted reversal exists yet
    }

    [PostgresFact]
    public async Task An_adjusting_entry_references_a_posted_entry_and_follows_the_normal_workflow()
    {
        await PrepareJournal();
        var p = await Posted();
        var draft = await Draft();
        var input = Simple(amount: 30m) with { AdjustsId = p.Header.Id };
        var adj = Ok(await Journal.CreateDraftAsync(Maker, Company.Id, input, false));
        Assert.Equal((JournalType.Adjusting, p.Header.Id), (adj.Header.Type, adj.Header.AdjustsId));
        Assert.Contains(adj.Events, e => e.Kind == JournalEventKind.AdjustingDrafted);
        Assert.Equal("adjusted_entry_not_posted", (await Journal.CreateDraftAsync(Maker, Company.Id, Simple() with { AdjustsId = draft.Header.Id }, false)).Code);
        Assert.Equal(ConfigStatus.Invalid, (await Journal.CreateDraftAsync(Maker, Company.Id, Simple() with { AdjustsId = Guid.NewGuid() }, false)).Status);
        var posted = Ok(await Journal.PostAsync(Maker, Company.Id, Ok(await Journal.SubmitAsync(Maker, Company.Id, adj.Header.Id, 1)).Header.Id, 1, false));
        Assert.Equal(JournalStatus.Posted, posted.Header.Status);
        Assert.Equal(JournalStatus.Posted, (await Reload(p.Header.Id)).Header.Status);                       // adjusting never changes the original
    }
}

public class JournalDatabaseInvariantTests : JournalTestBase
{
    [PostgresFact]
    public async Task PostgreSQL_checks_line_and_header_shape_for_direct_writes()
    {
        await PrepareJournal();
        var d = await Draft();
        string Line(string deb, string cre, string bd, string bc, string rate = "1") =>
            $"INSERT INTO {Jl.Table} ({Jl.Entry}, {Jl.No}, {Jl.Account}, {Jl.Cur}, {Jl.Debit}, {Jl.Credit}, {Jl.Rate}, {Jl.BaseDebit}, {Jl.BaseCredit}, {Jl.Branch}) VALUES ('{d.Header.Id}', 50, '{Cash.Id}', 'JOD', {deb}, {cre}, {rate}, {bd}, {bc}, '{Main}')";
        Assert.Equal("23514", await Fails(Line("1", "1", "1", "0")));                    // both sides
        Assert.Equal("23514", await Fails(Line("0", "0", "0", "0")));                    // neither side
        Assert.Equal("23514", await Fails(Line("-1", "0", "-1", "0")));                  // negative
        Assert.Equal("23514", await Fails(Line("1", "0", "0", "1")));                    // base on the other side
        Assert.Equal("23514", await Fails(Line("1", "0", "1", "0", rate: "0")));         // non-positive rate
        Assert.Equal("23505", await Fails(Line("1", "0", "1", "0").Replace(", 50,", ", 1,")));   // duplicate line number
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Number} = 'X-1' WHERE {Je.Id} = '{d.Header.Id}'"));   // a number only exists once posted
        Assert.Equal("23514", await Fails($"UPDATE {Je.Table} SET {Je.Desc} = '   ' WHERE {Je.Id} = '{d.Header.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.SourceKey} = 'k' WHERE {Je.Id} = '{d.Header.Id}'"));  // provenance is immutable
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Rev} = {Je.Rev} + 2 WHERE {Je.Id} = '{d.Header.Id}'"));
        Assert.Null(await Fails($"UPDATE {Je.Table} SET {Je.Desc} = 'edited', {Je.Rev} = {Je.Rev} + 1 WHERE {Je.Id} = '{d.Header.Id}'"));
    }

    [PostgresFact]
    public async Task A_submitted_entrys_content_and_lines_cannot_change_and_the_event_log_is_append_only()
    {
        await PrepareJournal();
        var s = await Submitted();
        Assert.Equal("P0001", await Fails($"UPDATE {Jl.Table} SET {Jl.Debit} = 5, {Jl.BaseDebit} = 5 WHERE {Jl.Id} = '{s.Lines[0].Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Jl.Table} WHERE {Jl.Entry} = '{s.Header.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Je.Table} SET {Je.Date} = '2026-04-01' WHERE {Je.Id} = '{s.Header.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Jv.Table} SET {Jv.Detail} = 'x'"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Jv.Table}"));
    }

    [PostgresFact]
    public async Task A_failed_posting_rolls_back_the_number_the_audit_event_and_the_state_together()
    {
        await PrepareJournal();
        var s = await Submitted();
        var before = await Count(Company, Audit_.Table);
        // The account is deactivated after submission: PostgreSQL refuses the posting inside the same transaction that already issued the number and wrote the audit event.
        Ok(await ChartDomain.DeactivateAsync(Admin.Id, Company.Id, Cash.Id, Cash.Revision));
        var r = await Journal.PostAsync(Maker, Company.Id, s.Header.Id, s.Header.Revision, false);
        Assert.Equal((ConfigStatus.Conflict, "account_not_postable"), (r.Status, r.Code));
        Assert.Equal(0L, await Count(Company, Ni.Table));
        Assert.Equal(before + 1, await Count(Company, Audit_.Table));                                         // only the deactivation itself
        Assert.Equal(JournalStatus.Approved, (await Reload(s.Header.Id)).Header.Status);
        Ok(await ChartDomain.ReactivateAsync(Admin.Id, Company.Id, Cash.Id, Cash.Revision + 1));
        Assert.Equal("JV-2026-000001", Ok(await Journal.PostAsync(Maker, Company.Id, s.Header.Id, s.Header.Revision, false)).Header.Number);   // no gap
    }

}
