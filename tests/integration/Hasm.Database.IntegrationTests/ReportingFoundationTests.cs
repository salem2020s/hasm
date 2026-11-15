using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Reporting;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.3 S1 against real PostgreSQL: the shared export service, the complete (never truncated) General Ledger export, the explicit row limit, column selection, audited exports, authorization before export,
/// report definitions and fiscal-period resolution over HTTP. Disposable cluster only.
/// </summary>
public class ReportingFoundationTests : LedgerReportTestBase
{
    private ReportExportService Exporter(ReportOptions? o = null) => new(Tx, o ?? new ReportOptions(), [new CsvReportRenderer()]);
    private Task<long> Events(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");
    private AuditVerifier CompanyVerifier() => new(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints);
    private static DateOnly D(string s) => DateOnly.Parse(s);

    /// <summary><paramref name="entries"/> posted entries of <paramref name="lines"/> one-unit debit lines on Cash against one credit on Sales.</summary>
    private async Task SeedCashLines(int entries, int lines, string date = "2026-03-10")
    {
        for (var e = 0; e < entries; e++)
            await Posted(Entry(date, [.. Enumerable.Range(0, lines).Select(_ => Dr(Cash.Id, 1m)), Cr(Sales.Id, lines)]));
    }

    private async Task<string> Text(ExportFile f) { using var r = new StreamReader(f.Content, new UTF8Encoding(true)); return await r.ReadToEndAsync(); }

    [PostgresFact]
    public async Task The_general_ledger_export_contains_every_authorized_line_not_the_first_5000_and_is_audited()
    {
        await PrepareReports();
        await SeedCashLines(14, 400);                                                                                      // 5,600 lines on Cash
        var page = Ok(await RMg.GeneralLedgerAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, 5000, 0, false));
        Assert.Equal((5600, 5000), (page.TotalLines, page.Lines.Count));                                                    // the interactive page limit is explicit and unchanged
        var exported = await Events(ReportAuditEvents.Exported);
        await using var doc = Ok(await RMg.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        Assert.Equal(5602, doc.RowCount);
        await using var file = Ok(await Exporter().ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "csv", null, "general-ledger")));
        var text = await Text(file);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5603, lines.Length);                                                                                   // header + opening + 5,600 lines + closing
        Assert.Equal(5600, lines.Count(l => l.StartsWith("2026-03-10,")));
        Assert.Contains(lines, l => l.EndsWith(",5001.0000"));                                                              // line 5,001 exists: nothing was cut at 5,000
        Assert.EndsWith(",5600.0000", lines[^1]);
        Assert.Contains("Closing balance", lines[^1]);
        Assert.Equal(exported + 1, await Events(ReportAuditEvents.Exported));
        Assert.Equal(AuditVerdict.Verified, (await CompanyVerifier().VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task An_export_above_the_row_limit_is_refused_before_any_file_is_produced_and_the_refusal_is_audited()
    {
        await PrepareReports();
        await SeedCashLines(3, 60);                                                                                         // 180 lines
        var options = new ReportOptions { MaxExportRows = 100 };
        await using var doc = Ok(await RMg.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        var refused = await Exporter(options).ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "csv", null, "gl"));
        Assert.Equal((ConfigStatus.Conflict, "export_too_large"), (refused.Status, refused.Code));
        Assert.Null(refused.Value);
        Assert.Equal(1L, await Events(ReportAuditEvents.ExportRefused));
        Assert.Equal(0L, await Events(ReportAuditEvents.Exported));
        // within the limit the very same export succeeds
        await using var ok = Ok(await Exporter(new ReportOptions { MaxExportRows = 1000 }).ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "csv", null, "gl")));
        Assert.True(ok.Length > 0);
        // a PDF has its own, separate limit
        Assert.Equal(50_000, new ReportOptions().MaxPdfRows);
        Assert.Equal(500_000, new ReportOptions().MaxExportRows);
    }

    [PostgresFact]
    public async Task Column_selection_and_format_are_validated_and_only_the_selected_columns_are_exported()
    {
        await PrepareReports();
        await Posted(Simple("2026-03-10", 250m));
        await using var doc = Ok(await RMg.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        var ex = Exporter();
        await using var picked = Ok(await ex.ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "CSV", ["date", "debit"], "x")));
        var lines = (await Text(picked)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date,Debit", lines[0]);
        Assert.Contains("2026-03-10,250.0000", lines);
        foreach (var bad in new[] { new[] { "nope" }, ["date", "date"], ["Date", "nonexistent"] })
        {
            var r = await ex.ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "csv", bad, "x"));
            Assert.Equal((ConfigStatus.Invalid, "validation_failed"), (r.Status, r.Code));
            Assert.Equal("invalid_columns", r.Fields![0].Code);
        }
        var unsupported = await ex.ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "docx", null, "x"));
        Assert.Equal("unsupported_format", unsupported.Fields![0].Code);
        Assert.Equal(0L, await Events(ReportAuditEvents.ExportFailed));
    }

    [PostgresFact]
    public async Task An_export_needs_the_export_permission_and_only_contains_the_branches_the_caller_may_see()
    {
        await PrepareReports();
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 10m), Cr(Sales.Id, 10m)));                                           // Main
        await Posted(Entry("2026-03-11", Dr(Cash.Id, 7m, null, B2), Cr(Sales.Id, 7m, null, B2)));                         // branch B2 only
        var (viewer, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Company, null);
        var denied = await RMg.GeneralLedgerDocumentAsync(viewer.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false);
        Assert.Equal(ConfigStatus.Forbidden, denied.Status);
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.TrialBalanceDocumentAsync(viewer.Id, Company.Id, D("2026-01-01"), D("2026-12-31"), null, null, false, false)).Status);
        var (mainOnly, _) = await Member([PermissionCatalog.Codes.LedgerReportsView, PermissionCatalog.Codes.LedgerReportsExport], ScopeKind.Branch, Main);
        await using var doc = Ok(await RMg.GeneralLedgerDocumentAsync(mainOnly.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        await using var file = Ok(await Exporter().ExportAsync(new ReportExportRequest(Company.Id, mainOnly.Id, doc, "csv", null, "gl")));
        var text = await Text(file);
        Assert.Contains("10.0000", text);
        Assert.DoesNotContain("7.0000", text);                                                                              // branch B2's line never reaches the file
        Assert.Equal(3, doc.RowCount);
        await using var all = Ok(await RMg.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        Assert.Equal(4, all.RowCount);
    }

    [PostgresFact]
    public async Task Report_definitions_are_listed_only_for_callers_who_may_view_them()
    {
        await PrepareReports();
        Assert.Equal(["balance-sheet", "general-ledger", "profit-and-loss", "trading-account", "trial-balance"], (await RMg.DefinitionsAsync(Admin.Id, Company.Id)).Select(d => d.Definition.Id).Order());
        Assert.All(await RMg.DefinitionsAsync(Admin.Id, Company.Id), d => Assert.True(d.CanExport));
        var (viewer, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Company, null);
        Assert.All(await RMg.DefinitionsAsync(viewer.Id, Company.Id), d => Assert.False(d.CanExport));
        Assert.Equal(["general-ledger", "trial-balance"], (await RMg.DefinitionsAsync(viewer.Id, Company.Id)).Select(d => d.Definition.Id).Order());       // the statements have their own permissions
        var (statements, _) = await Member([PermissionCatalog.Codes.FinancialStatementsView], ScopeKind.Company, null);
        Assert.Equal(["balance-sheet", "profit-and-loss", "trading-account"], (await RMg.DefinitionsAsync(statements.Id, Company.Id)).Select(d => d.Definition.Id).Order());
        var (journalOnly, _) = await Member([PermissionCatalog.Codes.JournalView], ScopeKind.Company, null);
        Assert.Empty(await RMg.DefinitionsAsync(journalOnly.Id, Company.Id));
        Assert.Equal(ConfigStatus.Forbidden, (await RMg.ResolvePeriodAsync(journalOnly.Id, Company.Id, new ReportPeriodRequest(null, null, D("2026-01-01"), D("2026-01-31")))).Status);
    }

    [PostgresFact]
    public async Task The_endpoints_export_the_complete_result_report_definitions_and_resolve_fiscal_periods()
    {
        await StartApi(configure: b => b.UseSetting("Hasm:Reports:MaxExportRows", "300"));
        BuildChartFacade();
        BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset"); Sales = await NewAccount("4101", "revenue");
        Main = await MainBranch();
        await SeedCashLines(1, 100);
        const string Base = "/api/v1/company/reports";
        var cookie = await SignIn(Admin);
        var csv = await client.SendAsync(Req($"{Base}/general-ledger?accountId={Cash.Id}&from=2026-03-01&to=2026-03-31&format=csv", cookie));
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", csv.Headers.CacheControl!.ToString());
        Assert.Equal(103, (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);       // header + opening + 100 + closing
        var cols = await client.SendAsync(Req($"{Base}/trial-balance?from=2026-03-01&to=2026-03-31&format=csv&columns=code,closing", cookie));
        Assert.StartsWith("Code,Closing", (await cols.Content.ReadAsStringAsync()).TrimStart('﻿'));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/trial-balance?from=2026-03-01&to=2026-03-31&format=csv&columns=bogus", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/trial-balance?from=2026-03-01&to=2026-03-31&format=docx", cookie: cookie)).Status);
        // above the configured limit (300) the API refuses with a clear code instead of cutting the file
        await SeedCashLines(3, 100, "2026-03-12");
        var tooBig = await Send(HttpMethod.Get, $"{Base}/general-ledger?accountId={Cash.Id}&from=2026-03-01&to=2026-03-31&format=csv", cookie: cookie);
        Assert.Equal((HttpStatusCode.Conflict, "export_too_large"), (tooBig.Status, tooBig.Error));
        var defs = await Send(HttpMethod.Get, $"{Base}/definitions", cookie: cookie);
        Assert.Equal(["balance-sheet", "general-ledger", "profit-and-loss", "trading-account", "trial-balance"], defs.Json.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).Order());
        var period = await Send(HttpMethod.Get, $"{Base}/period?fiscalYearId={Year.Id}&comparison=previous_year", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, period.Status);
        Assert.Equal("2026-01-01", period.Json.GetProperty("current").GetProperty("from").GetString());
        Assert.Equal("no_previous_year", period.Json.GetProperty("comparativeUnavailable").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/period?fiscalYearId={Year.Id}&from=2026-01-01&to=2026-02-01", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/period?from=2026-01-01&to=2026-02-01&comparison=weird", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"{Base}/definitions")).Status);
    }

    private HttpRequestMessage Req(string path, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return req;
    }
}
