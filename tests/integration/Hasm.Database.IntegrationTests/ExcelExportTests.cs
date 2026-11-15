using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Reporting;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.3 S4 against real PostgreSQL: real XLSX exports of the ledger reports (complete result, typed cells, the explicit row limit, authorization before export, audit). Disposable cluster only.</summary>
public class ExcelExportTests : LedgerReportTestBase
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private LedgerReportManagementService Mgmt = null!;
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private ReportExportService Exporter(ReportOptions? o = null) => new(Tx, o ?? new ReportOptions(), [new CsvReportRenderer(), new XlsxReportRenderer()]);

    /// <summary>Every row of the first worksheet as the visible text of its cells (strings as text, numbers as written).</summary>
    internal static List<List<string>> ReadRows(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true);
        using var s = zip.Entries.Single(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)).Open();
        var doc = XDocument.Load(s);
        return [.. doc.Descendants(Ns + "row").Select(r => r.Elements(Ns + "c").Select(c => c.Descendants(Ns + "t").Any() ? string.Concat(c.Descendants(Ns + "t").Select(t => t.Value)) : c.Element(Ns + "v")?.Value ?? "").ToList())];
    }

    private async Task SeedCashLines(int entries, int lines, string date = "2026-03-10")
    {
        for (var e = 0; e < entries; e++) await Posted(Entry(date, [.. Enumerable.Range(0, lines).Select(_ => Dr(Cash.Id, 1m)), Cr(Sales.Id, lines)]));
    }

    private Task<long> Events(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");

    [PostgresFact]
    public async Task The_general_ledger_xlsx_contains_every_authorized_line_with_typed_cells_and_is_audited()
    {
        await PrepareReports();
        Mgmt = new LedgerReportManagementService(Rbac, Reports, Journal, new ReportOptions(), Cal);
        await SeedCashLines(14, 400);                                                                                      // 5,600 lines on Cash
        var events = await Events(ReportAuditEvents.Exported);
        await using var doc = Ok(await Mgmt.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        await using var file = Ok(await Exporter().ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "xlsx", ["date", "number", "baseDebit", "runningBalance"], "gl", "ar")));
        Assert.Equal("gl.xlsx", file.FileName);
        Assert.StartsWith("application/vnd.openxmlformats", file.ContentType);
        var rows = ReadRows(file.Content);
        Assert.Contains(rows, r => r.SequenceEqual(["التاريخ", "رقم القيد", "مدين بالأساس", "الرصيد الجاري"]));          // Arabic titles in the chosen columns
        var data = rows.Where(r => r.Count == 4 && int.TryParse(r[0], out var serial) && serial > 40000).ToList();       // dates are numeric serial cells
        Assert.Equal(5600, data.Count);                                                                                      // nothing cut at 5,000
        Assert.Equal("5600", data[^1][3]);                                                                                  // running balance of the last line
        Assert.Equal(events + 1, await Events(ReportAuditEvents.Exported));
        Assert.Equal(AuditVerdict.Verified, (await new AuditVerifier(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(Company.Id))).Verdict);
    }

    [PostgresFact]
    public async Task The_row_limit_applies_to_xlsx_before_any_file_is_produced_and_the_refusal_is_audited()
    {
        await PrepareReports();
        Mgmt = new LedgerReportManagementService(Rbac, Reports, Journal, new ReportOptions(), Cal);
        await SeedCashLines(2, 100);
        await using var doc = Ok(await Mgmt.GeneralLedgerDocumentAsync(Admin.Id, Company.Id, Cash.Id, D("2026-01-01"), D("2026-12-31"), null, null, false));
        var refused = await Exporter(new ReportOptions { MaxExportRows = 150 }).ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "xlsx", null, "gl"));
        Assert.Equal((ConfigStatus.Conflict, "export_too_large"), (refused.Status, refused.Code));
        Assert.Equal(1L, await Events(ReportAuditEvents.ExportRefused));
        Assert.True((await Exporter(new ReportOptions { MaxExportRows = 500 }).ExportAsync(new ReportExportRequest(Company.Id, Admin.Id, doc, "xlsx", null, "gl"))).IsOk);
    }

    [PostgresFact]
    public async Task The_endpoints_export_xlsx_with_the_comparison_columns_the_chosen_language_and_the_same_authorization_as_the_screen()
    {
        await StartApi(configure: b => b.UseSetting("Hasm:Reports:MaxExportRows", "200"));
        BuildChartFacade(); BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2025", new DateOnly(2025, 1, 1), "monthly")));
        Cash = await NewAccount("0101", "asset"); Sales = await NewAccount("4101", "revenue"); Main = await MainBranch();
        await Posted(Entry("2025-06-10", Dr(Cash.Id, 200m), Cr(Sales.Id, 200m)));
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1000.5m), Cr(Sales.Id, 1000.5m)));
        const string Base = "/api/v1/company/reports";
        var cookie = await SignIn(Admin);
        async Task<(HttpStatusCode Status, byte[] Body, string? Type)> Fetch(string path, string c)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path); req.Headers.Add("Cookie", $"{CookieName}={c}");
            using var resp = await client.SendAsync(req);
            return (resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType);
        }
        var tb = await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&comparison=previous_year&format=xlsx&lang=ar", cookie);
        Assert.Equal((HttpStatusCode.OK, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"), (tb.Status, tb.Type));
        var rows = ReadRows(new MemoryStream(tb.Body));
        Assert.Equal("ميزان المراجعة", rows[0][0]);
        Assert.Contains("ختامي (المقارنة)", rows.Single(r => r.Contains("رمز الحساب")));                                        // the comparison columns are in the file
        var cash = rows.Single(r => r.Count > 0 && r[0] == "0101");                                                         // the code is TEXT: leading zero kept
        Assert.Contains("1200.5", cash);                                                                                   // closing 2026: numeric
        Assert.Contains("200", cash);                                                                                      // comparative closing (2025)
        // column selection
        var only = ReadRows(new MemoryStream((await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&format=xlsx&columns=code,closing", cookie)).Body));
        Assert.Contains(only, r => r.SequenceEqual(["Code", "Closing"]));
        // the same row limit as every other format
        await SeedCashLines(1, 250, "2026-04-01");
        var big = await Fetch($"{Base}/general-ledger?accountId={Cash.Id}&fiscalYearId={Year.Id}&format=xlsx", cookie);
        Assert.Equal(HttpStatusCode.Conflict, big.Status);
        Assert.Contains("export_too_large", Encoding.UTF8.GetString(big.Body));
        // viewing is not exporting
        var (viewer, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Company, null);
        var viewCookie = await SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&format=xlsx", viewCookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}", viewCookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&format=ods", cookie)).Status);
    }

    [PostgresFact]
    public async Task Pdf_exports_are_real_documents_in_both_languages_with_the_same_authorization_row_limit_and_audit()
    {
        await StartApi(configure: b => b.UseSetting("Hasm:Reports:MaxPdfRows", "40"));
        BuildChartFacade(); BuildJournalFacade();
        Ok(await Svc.PutSettingsAsync(Admin.Id, Company.Id, 0, new SettingsInput("en", "yyyy-MM-dd", "latin", "UTC")));
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {CompanySchema.CompanyCurrency.Table} ({CompanySchema.CompanyCurrency.Code}, {CompanySchema.CompanyCurrency.IsBase}, {CompanySchema.CompanyCurrency.Enabled}, {CompanySchema.CompanyCurrency.Revision}, {CompanySchema.CompanyCurrency.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now())");
        Year = Ok(await Cal.CreateYearAsync(Admin.Id, Company.Id, new FiscalYearInput("FY2026", new DateOnly(2026, 1, 1), "monthly")));
        Cash = await NewAccount("1101", "asset"); Sales = await NewAccount("4101", "revenue"); Main = await MainBranch();
        await Posted(Entry("2026-03-10", Dr(Cash.Id, 1234.5m), Cr(Sales.Id, 1234.5m)));
        const string Base = "/api/v1/company/reports";
        var cookie = await SignIn(Admin);
        async Task<(HttpStatusCode Status, byte[] Body, string? Type)> Fetch(string path, string c)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path); req.Headers.Add("Cookie", $"{CookieName}={c}");
            using var resp = await client.SendAsync(req);
            return (resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType);
        }
        var events = await Events(ReportAuditEvents.Exported);
        var en = await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&format=pdf&lang=en", cookie);
        Assert.Equal((HttpStatusCode.OK, "application/pdf"), (en.Status, en.Type));
        using (var pdf = PdfDocument.Open(en.Body))
        {
            var text = pdf.GetPage(1).Text;
            Assert.Contains("Trial balance", text);
            Assert.Contains("1101", text);
            Assert.Contains("1,234.5000", text);
        }
        var ar = await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&format=pdf&lang=ar", cookie);
        Assert.Equal(HttpStatusCode.OK, ar.Status);
        using (var pdf = PdfDocument.Open(ar.Body)) Assert.Contains("1,234.5000", pdf.GetPage(1).Text);
        Assert.Equal(events + 2, await Events(ReportAuditEvents.Exported));
        // a PDF has its own, separate limit: a general ledger with more rows than the configured 40 is refused for PDF but exported as XLSX
        await SeedCashLines(1, 60, "2026-04-01");
        var tooBig = await Fetch($"{Base}/general-ledger?accountId={Cash.Id}&fiscalYearId={Year.Id}&format=pdf", cookie);
        Assert.Equal(HttpStatusCode.Conflict, tooBig.Status);
        Assert.Contains("export_too_large", Encoding.UTF8.GetString(tooBig.Body));
        Assert.Equal(HttpStatusCode.OK, (await Fetch($"{Base}/general-ledger?accountId={Cash.Id}&fiscalYearId={Year.Id}&format=xlsx", cookie)).Status);
        // viewing is not exporting
        var (viewer, _) = await Member([PermissionCatalog.Codes.LedgerReportsView], ScopeKind.Company, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fetch($"{Base}/trial-balance?fiscalYearId={Year.Id}&format=pdf", await SignIn(viewer))).Status);
    }
}
