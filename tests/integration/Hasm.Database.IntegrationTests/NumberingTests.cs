using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Lh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using Nc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberCounter;
using Ni = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberIssue;
using Nr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NumberingRule;

namespace Hasm.Database.IntegrationTests;

/// <summary>Document numbering (company migration 0008 slice S1, Phase 3.2B) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class NumberingTestBase : ChartAccessTestBase
{
    protected NumberingService Numbering { get; private set; } = null!;
    protected NumberingManagementService NMgmt { get; private set; } = null!;
    protected long Baseline;
    protected static readonly NumberScope Y2026 = new(null, null, Guid.Parse("00000000-0000-4000-8000-000000002026"), "2026");

    protected async Task PrepareNumbering()
    {
        await PrepareRbac();
        BuildNumberingFacade();
        Baseline = await Count(Company, Audit_.Table);
    }

    protected void BuildNumberingFacade()
    {
        Numbering = new NumberingService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        NMgmt = new NumberingManagementService(Rbac, Numbering);
    }

    protected async Task<IssuedNumber> Issue(NumberScope scope, bool commit = true)
    {
        await using var conn = await Resolver.OpenAsync(Company.Id);
        await using var tx = await conn.BeginTransactionAsync();
        var n = await NumberingService.IssueAsync(conn, tx, DocumentTypes.JournalEntry, scope, Guid.NewGuid(), Admin.Id, DateTime.UtcNow, default);
        if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
        return n;
    }

    protected static NumberingRuleInput Rule(string prefix = "JV", string sep = "-", int width = 6, bool branch = false, bool year = true) => new(prefix, sep, width, branch, year);
}

public class NumberingTests : NumberingTestBase
{
    [PostgresFact]
    public async Task Migration_0008_numbering_tables_start_empty_and_a_read_shows_the_defaults_without_creating_anything()
    {
        await PrepareNumbering();
        foreach (var t in new[] { Nr.Table, Nc.Table, Ni.Table, Lh.Table }) Assert.Equal(0L, await Count(Company, t));
        var rules = Ok(await NMgmt.ListAsync(Admin.Id, Company.Id));
        Assert.Equal(["journal_entry", "stock_receipt", "stock_issue", "stock_transfer", "stocktake", "stock_adjustment"], rules.Select(r => r.DocType));      // one default rule per registered document type (journal entries, the three stock documents, the stocktake and the stock adjustment)
        Assert.Equal(["JV", "SR", "SI", "ST", "STK", "SA"], rules.Select(r => r.Prefix));
        Assert.All(rules, r => Assert.Equal(("-", 6, false, true, 0L, false, false), (r.Separator, r.Width, r.PerBranch, r.PerFiscalYear, r.Revision, r.Exists, r.NumbersIssued)));
        Assert.Equal(0L, await Count(Company, Nr.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table) - Baseline);
    }

    [PostgresFact]
    public async Task Default_numbers_are_prefix_year_and_a_zero_padded_sequence_per_fiscal_year()
    {
        await PrepareNumbering();
        Assert.Equal("JV-2026-000001", (await Issue(Y2026)).Number);
        Assert.Equal("JV-2026-000002", (await Issue(Y2026)).Number);
        var y2027 = new NumberScope(null, null, Guid.NewGuid(), "2027");
        Assert.Equal("JV-2027-000001", (await Issue(y2027)).Number);   // a new fiscal year restarts the sequence
        Assert.Equal("JV-2026-000003", (await Issue(Y2026)).Number);
    }

    [PostgresFact]
    public async Task Rules_choose_prefix_separator_width_and_the_branch_and_year_scopes()
    {
        await PrepareNumbering();
        Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 0, Rule("GL", "/", 4, branch: true, year: true)));
        var b1 = Guid.NewGuid(); var b2 = Guid.NewGuid();
        Assert.Equal("GL/2026/BR1/0001", (await Issue(new NumberScope(b1, "BR1", Y2026.FiscalYearId, "2026"))).Number);
        Assert.Equal("GL/2026/BR2/0001", (await Issue(new NumberScope(b2, "BR2", Y2026.FiscalYearId, "2026"))).Number);   // its own counter per branch
        Assert.Equal("GL/2026/BR1/0002", (await Issue(new NumberScope(b1, "BR1", Y2026.FiscalYearId, "2026"))).Number);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue(Y2026));   // a per-branch rule needs a branch
        // Prefix, separator and width may change later and only affect new numbers.
        Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 1, Rule("JE", "", 8, branch: true, year: true)));
        Assert.Equal("JE2026BR100000003", (await Issue(new NumberScope(b1, "BR1", Y2026.FiscalYearId, "2026"))).Number);
    }

    [PostgresFact]
    public async Task A_rolled_back_posting_leaves_no_gap_and_no_issued_number()
    {
        await PrepareNumbering();
        Assert.Equal("JV-2026-000001", (await Issue(Y2026)).Number);
        Assert.Equal("JV-2026-000002", (await Issue(Y2026, commit: false)).Number);   // allocated inside a transaction that rolls back
        Assert.Equal(1L, await Count(Company, Ni.Table));
        Assert.Equal("JV-2026-000002", (await Issue(Y2026)).Number);                   // the same value is issued again: gapless
        Assert.Equal(2L, await Count(Company, Ni.Table));
    }

    [PostgresFact]
    public async Task Concurrent_issuance_never_duplicates_or_skips_a_number()
    {
        await PrepareNumbering();
        var numbers = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Issue(Y2026)));
        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), numbers.Select(n => n.Value).Order());
        Assert.Equal(24, numbers.Select(n => n.Number).Distinct().Count());
        Assert.Equal(24L, await Sql<long>($"SELECT {Nc.LastValue} FROM {Nc.Table}"));
        Assert.Equal(24L, await Count(Company, Ni.Table));
    }

    [PostgresFact]
    public async Task A_voided_number_stays_recorded_is_never_issued_again_and_cannot_be_voided_twice()
    {
        await PrepareNumbering();
        var first = await Issue(Y2026);
        await using (var conn = await Resolver.OpenAsync(Company.Id))
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await NumberingService.VoidAsync(conn, tx, DocumentTypes.JournalEntry, first.Number, Admin.Id, "issued by mistake", DateTime.UtcNow, default);
            await tx.CommitAsync();
        }
        Assert.Equal("JV-2026-000002", (await Issue(Y2026)).Number);   // the voided 000001 is not reused
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ni.Table} WHERE {Ni.VoidedAtUtc} IS NOT NULL"));
        // A second void of the same number is refused; so is a void without a reason.
        await using var c2 = await Resolver.OpenAsync(Company.Id);
        await using var t2 = await c2.BeginTransactionAsync();
        await Assert.ThrowsAsync<PostgresException>(() => NumberingService.VoidAsync(c2, t2, DocumentTypes.JournalEntry, first.Number, Admin.Id, "again", DateTime.UtcNow, default));
        await t2.RollbackAsync();
        await using var c3 = await Resolver.OpenAsync(Company.Id);
        await using var t3 = await c3.BeginTransactionAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => NumberingService.VoidAsync(c3, t3, DocumentTypes.JournalEntry, first.Number, Admin.Id, " ", DateTime.UtcNow, default));
        await t3.RollbackAsync();
    }

    [PostgresFact]
    public async Task Two_scopes_that_would_produce_the_same_number_fail_in_PostgreSQL_and_nothing_is_issued()
    {
        await PrepareNumbering();
        Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 0, Rule("", "-", 6, branch: true, year: false)));
        Assert.Equal("A-000001", (await Issue(new NumberScope(Guid.NewGuid(), "A", null, null))).Number);
        // A different branch id with the same code label would produce the same text: UNIQUE(document type, number) refuses it and the transaction leaves no counter behind.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Issue(new NumberScope(Guid.NewGuid(), "A", null, null)));
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal(1L, await Count(Company, Ni.Table));
        Assert.Equal(1L, await Count(Company, Nc.Table));
    }

}

public class NumberingDatabaseGuardTests : NumberingTestBase
{
    private async Task SeedOne()
    {
        await PrepareNumbering();
        await Issue(Y2026);
    }

    [PostgresFact]
    public async Task Issued_numbers_cannot_be_changed_deleted_or_truncated_only_voided_once()
    {
        await SeedOne();
        Assert.Equal("P0001", await Fails($"DELETE FROM {Ni.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Ni.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ni.Table} SET {Ni.Number} = 'X-1'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ni.Table} SET {Ni.DocumentId} = gen_random_uuid()"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ni.Table} SET {Ni.IssuedBy} = gen_random_uuid()"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ni.Table} SET {Ni.VoidedAtUtc} = NULL"));
        Assert.Equal("23514", await Fails($"UPDATE {Ni.Table} SET {Ni.VoidedAtUtc} = now()"));   // void fields are all-or-nothing
        Assert.Null(await Fails($"UPDATE {Ni.Table} SET {Ni.VoidedAtUtc} = now(), {Ni.VoidedBy} = gen_random_uuid(), {Ni.VoidReason} = 'x'"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ni.Table} SET {Ni.VoidReason} = 'changed'"));   // a voided number is final
    }

    [PostgresFact]
    public async Task Counters_only_advance_by_one_never_move_back_and_never_exist_without_their_issued_number()
    {
        await SeedOne();
        Assert.Equal("P0001", await Fails($"UPDATE {Nc.Table} SET {Nc.LastValue} = {Nc.LastValue} + 2"));
        Assert.Equal("P0001", await Fails($"UPDATE {Nc.Table} SET {Nc.LastValue} = {Nc.LastValue} - 1"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Nc.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Nc.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {Nc.Table} SET {Nc.DocType} = 'other'"));
        // A +1 step without an issue record is rejected at commit (the deferred check): no phantom value.
        Assert.Equal("P0001", await Fails($"UPDATE {Nc.Table} SET {Nc.LastValue} = {Nc.LastValue} + 1"));
        Assert.Equal("23514", await Fails($"INSERT INTO {Nc.Table} ({Nc.DocType}, {Nc.BranchId}, {Nc.FiscalYearId}, {Nc.LastValue}) VALUES ('journal_entry', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 0)"));
        Assert.Equal(1L, await Sql<long>($"SELECT {Nc.LastValue} FROM {Nc.Table}"));
    }

    [PostgresFact]
    public async Task The_issue_ledger_has_unique_numbers_and_unique_sequence_values_per_scope()
    {
        await SeedOne();
        var dup = $"INSERT INTO {Ni.Table} ({Ni.DocType}, {Ni.Number}, {Ni.BranchId}, {Ni.FiscalYearId}, {Ni.Value}, {Ni.DocumentId}, {Ni.IssuedAtUtc}, {Ni.IssuedBy}) VALUES ('journal_entry', '{{0}}', '{Guid.Empty}', '{Y2026.FiscalYearId}', {{1}}, gen_random_uuid(), now(), gen_random_uuid())";
        Assert.Equal("23505", await Fails(string.Format(dup, "JV-2026-000001", 9)));   // same number
        Assert.Equal("23505", await Fails(string.Format(dup, "OTHER", 1)));            // same value in the same scope
        Assert.Equal("23514", await Fails(string.Format(dup, "ZERO", 0)));
    }

    [PostgresFact]
    public async Task Rule_scope_is_locked_once_a_number_was_issued_and_rules_cannot_be_deleted()
    {
        await PrepareNumbering();
        var r = Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 0, Rule(year: false)));
        Assert.False(r.NumbersIssued);
        Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 1, Rule(year: true)));   // free while nothing was issued
        await Issue(Y2026);
        var locked = await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 2, Rule(year: false));
        Assert.Equal(ConfigStatus.Conflict, locked.Status);
        Assert.Equal("numbering_scope_locked", locked.Code);
        Assert.Equal("P0001", await Fails($"UPDATE {Nr.Table} SET {Nr.PerFiscalYear} = false, {Nr.Revision} = {Nr.Revision} + 1"));   // and PostgreSQL agrees
        Assert.Null(await Fails($"UPDATE {Nr.Table} SET {Nr.Prefix} = 'ZZ', {Nr.Revision} = {Nr.Revision} + 1"));                         // prefix stays editable
        Assert.Equal("P0001", await Fails($"UPDATE {Nr.Table} SET {Nr.Prefix} = 'YY'"));                                                 // revision must advance
        Assert.Equal("P0001", await Fails($"DELETE FROM {Nr.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Nr.Table}"));
        Assert.Equal("23514", await Fails($"UPDATE {Nr.Table} SET {Nr.Width} = 13, {Nr.Revision} = {Nr.Revision} + 1"));
    }

    [PostgresFact]
    public async Task Rule_changes_are_audited_with_an_append_only_history_linked_to_the_audit_event()
    {
        await PrepareNumbering();
        Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 0, Rule("AB")));
        Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 1, Rule("AB", "-", 7)));
        Assert.Equal(2L, await Count(Company, Lh.Table));
        Assert.Equal(2L, await Count(Company, Audit_.Table) - Baseline);
        Assert.Equal(2L, await Sql<long>($"SELECT count(DISTINCT ({Lh.AuditStream}, {Lh.AuditSeq})) FROM {Lh.Table}"));
        Assert.Contains("\"width\"", await Sql<string>($"SELECT {Lh.Changes}::text FROM {Lh.Table} WHERE {Lh.Revision} = 2"));
        Assert.Equal("P0001", await Fails($"UPDATE {Lh.Table} SET {Lh.ActorUserId} = gen_random_uuid()"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Lh.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Lh.Table}"));
        // An unchanged save, a stale revision, a missing precondition, bad input and an unknown type write nothing.
        Assert.Equal(7, Ok(await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 2, Rule("AB", "-", 7))).Width);
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 1, Rule("CD"))).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", null, Rule("CD"))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 2, new NumberingRuleInput("bad prefix!", "-", 6, false, true))).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Numbering.PutRuleAsync(Admin.Id, Company.Id, "journal_entry", 2, Rule(width: 0))).Status);
        Assert.Equal(ConfigStatus.NotFound, (await Numbering.PutRuleAsync(Admin.Id, Company.Id, "nope", 0, Rule())).Status);
        Assert.Equal(2L, await Count(Company, Lh.Table));
    }
}

public class NumberingAccessTests : NumberingTestBase
{
    [PostgresFact]
    public async Task Numbering_view_and_configure_are_separate_company_wide_permissions_and_only_the_administrator_preset_holds_them()
    {
        await PrepareNumbering();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains("company.numbering.view", admin.Permissions);
        Assert.Contains("company.numbering.configure", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.numbering"));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.NumberingView);
        var (configurer, _) = await Holder(PermissionCatalog.Codes.NumberingConfigure);
        Assert.Equal(6, Ok(await NMgmt.ListAsync(viewer.Id, Company.Id)).Count);                       // journal entries, three stock documents, stocktake, stock adjustment
        Assert.Equal(ConfigStatus.Forbidden, (await NMgmt.PutRuleAsync(viewer.Id, Company.Id, "journal_entry", 0, Rule())).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await NMgmt.ListAsync(configurer.Id, Company.Id)).Status);
        Assert.Equal(1, Ok(await NMgmt.PutRuleAsync(configurer.Id, Company.Id, "journal_entry", 0, Rule("ZZ"))).Revision);
    }

    [PostgresFact]
    public async Task The_numbering_endpoints_need_authentication_a_company_if_match_and_the_permission()
    {
        await StartApi();
        BuildNumberingFacade();
        const string Base = "/api/v1/company/numbering";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Put, $"{Base}/journal_entry", new { })).Status);
        var cookie = await SignIn(Admin);
        var list = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
        Assert.Equal("JV", list.Json.GetProperty("rules")[0].GetProperty("prefix").GetString());
        Assert.False(list.Json.GetProperty("rules")[0].GetProperty("configured").GetBoolean());
        Assert.True(list.Json.GetProperty("can").GetProperty("configure").GetBoolean());
        var body = new { prefix = "GL", separator = "-", width = 5, perBranch = false, perFiscalYear = true };
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Put, $"{Base}/journal_entry", body, cookie)).Status);
        var ok = await Send(HttpMethod.Put, $"{Base}/journal_entry", body, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("\"1\"", Etag(ok));
        Assert.Equal((HttpStatusCode)412, (await Send(HttpMethod.Put, $"{Base}/journal_entry", body, cookie, ifMatch: "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"{Base}/unknown", body, cookie, ifMatch: "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/journal_entry", new { prefix = "!!", separator = "-", width = 5, perBranch = false, perFiscalYear = true }, cookie, ifMatch: "\"1\"")).Status);
    }
}
