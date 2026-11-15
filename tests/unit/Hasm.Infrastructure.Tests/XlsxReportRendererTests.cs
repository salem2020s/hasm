using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Reporting;

namespace Hasm.Infrastructure.Tests;

/// <summary>The real XLSX export: typed cells, text safety, Excel's numeric precision, structure validity (checked by the Open XML validator and an independent reader) and bounded-memory streaming.</summary>
public class XlsxReportRendererTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly IReadOnlyList<ReportColumn> Cols =
    [
        new("code", "الرمز", "Code", ReportColumnType.Code, 14), new("name", "الاسم", "Name", ReportColumnType.Text, 30), new("amt", "المبلغ", "Amount", ReportColumnType.Money),
        new("date", "التاريخ", "Date", ReportColumnType.Date, 12), new("n", "العدد", "Count", ReportColumnType.Integer, 8), new("ok", "فعال", "Active", ReportColumnType.Boolean, 8),
        new("rate", "السعر", "Rate", ReportColumnType.Rate, 14),
    ];

    private static ReportDocument Doc(IEnumerable<ReportRow> rows, IReadOnlyList<ReportNotice>? notices = null, IReadOnlyList<ReportColumn>? cols = null, string title = "Trial balance") => new()
    {
        ReportId = "t", TitleAr = "ميزان المراجعة", TitleEn = title, CompanyName = "شركة الأمل", BaseCurrency = "JOD", Header = [new("الفترة", "Period", "2026-01-01 .. 2026-12-31")],
        Columns = cols ?? Cols, Rows = ct => Stream(rows), RowCount = 0, Notices = notices ?? [],
    };
    private static async IAsyncEnumerable<ReportRow> Stream(IEnumerable<ReportRow> rows) { foreach (var r in rows) yield return r; await Task.CompletedTask; }

    private static byte[] Render(ReportDocument doc, int[]? columns = null, string language = "en")
    {
        using var ms = new MemoryStream();
        new XlsxReportRenderer().RenderAsync(doc, columns ?? [.. Enumerable.Range(0, doc.Columns.Count)], ms, default, language).GetAwaiter().GetResult();
        return ms.ToArray();
    }

    private static XDocument Sheet(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        using var s = zip.Entries.Single(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)).Open();
        return XDocument.Load(s);
    }

    private static XDocument Styles(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        using var s = zip.GetEntry("xl/styles.xml")!.Open();
        return XDocument.Load(s);
    }

    private static XElement Cell(XDocument sheet, string reference) => sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == reference);
    private static string Inline(XElement cell) => string.Concat(cell.Descendants(Ns + "t").Select(t => t.Value));
    private static string NumFmtOf(XDocument styles, XElement cell)
    {
        var idx = int.Parse((string?)cell.Attribute("s") ?? "0");
        var xf = styles.Descendants(Ns + "cellXfs").Single().Elements(Ns + "xf").ElementAt(idx);
        var id = (string?)xf.Attribute("numFmtId") ?? "0";
        return styles.Descendants(Ns + "numFmt").FirstOrDefault(n => (string?)n.Attribute("numFmtId") == id)?.Attribute("formatCode")?.Value ?? "builtin:" + id;
    }

    private static ReportRow R(params object?[] cells) => new(ReportRowKind.Detail, 0, cells);

    // data rows start after: title, company, one header line, blank, column headers = row 6 is the first data row
    private const int First = 6;

    [Fact]
    public void Cells_are_typed_codes_keep_leading_zeros_numbers_and_dates_are_real_and_the_package_is_valid()
    {
        var bytes = Render(Doc([R("0101", "الصندوق", 1234.5m, new DateOnly(2026, 3, 9), 7L, true, 0.71m)]));
        using (var package = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
            Assert.Empty(new OpenXmlValidator().Validate(package));                                                    // schema-valid
        var sheet = Sheet(bytes); var styles = Styles(bytes);
        var code = Cell(sheet, $"A{First}"); var name = Cell(sheet, $"B{First}"); var amount = Cell(sheet, $"C{First}"); var date = Cell(sheet, $"D{First}"); var count = Cell(sheet, $"E{First}"); var ok = Cell(sheet, $"F{First}"); var rate = Cell(sheet, $"G{First}");
        Assert.Equal(("inlineStr", "0101", "builtin:49"), ((string?)code.Attribute("t"), Inline(code), NumFmtOf(styles, code)));      // text format: leading zero kept
        Assert.Equal("الصندوق", Inline(name));
        Assert.Null(amount.Attribute("t")); Assert.Equal("1234.5", amount.Element(Ns + "v")!.Value); Assert.Contains("0.0000", NumFmtOf(styles, amount));
        Assert.Null(date.Attribute("t")); Assert.Equal("46090", date.Element(Ns + "v")!.Value); Assert.Contains("yyyy", NumFmtOf(styles, date));       // 2026-03-09
        Assert.Equal("7", count.Element(Ns + "v")!.Value);
        Assert.Equal("Yes", Inline(ok));
        Assert.Equal("0.71", rate.Element(Ns + "v")!.Value); Assert.Equal("0.0000000000", NumFmtOf(styles, rate));
        Assert.Empty(sheet.Descendants(Ns + "f"));                                                                     // no formula anywhere
    }

    [Fact]
    public void An_independent_reader_gets_the_same_values_back_including_arabic_and_leading_zeros()
    {
        var bytes = Render(Doc([R("0101", "الصندوق النقدي", 1234.5m, new DateOnly(2026, 3, 9), 7L, false, 1m), R("0102", "Bank, \"quoted\"\nsecond line", -20.25m, null, null, null, null)]));
        var parsed = ImportFileReader.Read("r.xlsx", bytes, new ImportOptions(), new ReadOptions(HeaderRow: 5));
        Assert.Equal(["Code", "Name", "Amount", "Date", "Count", "Active", "Rate"], parsed.Headers);
        Assert.Equal(["0101", "الصندوق النقدي"], parsed.Rows[0].Cells.Take(2));
        Assert.Equal("1234.5", parsed.Rows[0].Cells[2]);
        Assert.Equal(["0102", "Bank, \"quoted\"\nsecond line", "-20.25"], parsed.Rows[1].Cells.Take(3));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-5+3")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData("\t=HYPERLINK(\"http://x\")")]
    [InlineData("\r=1")]
    public void Text_that_could_start_a_formula_is_stored_as_a_neutralised_literal_string_and_never_as_a_formula(string hostile)
    {
        var bytes = Render(Doc([R("X1", hostile, 1m, null, null, null, null)]));
        var sheet = Sheet(bytes);
        var cell = Cell(sheet, $"B{First}");
        Assert.Equal("inlineStr", (string?)cell.Attribute("t"));
        Assert.Equal("'" + hostile.Replace("\r", "\n"), Inline(cell));                                                // XML reads a bare carriage return as a line feed; either way it is a literal string
        Assert.Empty(sheet.Descendants(Ns + "f"));
        using var package = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator().Validate(package));
        // the header block and notices go through the same neutralisation
        var withNotice = Render(Doc([], [new ReportNotice("n", "=عربي", "=notice")]));
        Assert.Contains("'=notice", string.Concat(Sheet(withNotice).Descendants(Ns + "t").Select(t => t.Value + "|")));
    }

    [Theory]
    [InlineData("1234.5", true)]
    [InlineData("123456789012.345", true)]          // 15 digits
    [InlineData("-0.0001", true)]
    [InlineData("0", true)]
    [InlineData("1234567890123.4567", false)]       // 17 digits
    [InlineData("12345678901234.5678", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("9999999999999.999", false)]        // 16 digits
    public void The_precision_rule_counts_significant_digits(string text, bool fits) => Assert.Equal(fits, XlsxReportRenderer.FitsExcel(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_large_amount_is_never_silently_rounded()
    {
        var big = 12345678901234.5678m;
        var bytes = Render(Doc([R("1", "n", big, null, null, null, null), R("2", "n", 1234.5m, null, null, null, null)]));
        var sheet = Sheet(bytes);
        var cell = Cell(sheet, $"C{First}");
        Assert.Equal(("inlineStr", "12345678901234.5678"), ((string?)cell.Attribute("t"), Inline(cell)));              // exact text, not 1.23456789012346E+13
        Assert.Null(Cell(sheet, $"C{First + 1}").Attribute("t"));                                                     // the safe one stays numeric
        Assert.Contains("15 significant digits", string.Concat(sheet.Descendants(Ns + "t").Select(t => t.Value)));
        Assert.Equal("12345678901234.5678", ImportFileReader.Read("r.xlsx", bytes, new ImportOptions(), new ReadOptions(HeaderRow: 5)).Rows[0].Cells[2]);
    }

    [Fact]
    public void Characters_xml_cannot_carry_are_removed_and_the_package_stays_valid()
    {
        var bytes = Render(Doc([R("A\u0000B", "x\u0001y\u000Bz￾w", 1m, null, null, null, null)]));
        using var package = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator().Validate(package));
        var sheet = Sheet(bytes);
        Assert.Equal("AB", Inline(Cell(sheet, $"A{First}")));
        Assert.Equal("xyzw", Inline(Cell(sheet, $"B{First}")));
        Assert.Equal("a\tb\nc\rd", ReportText.XmlSafe("a\tb\nc\rd"));
        Assert.Equal("😀", ReportText.XmlSafe("😀\uD83D"));                                         // a lone high surrogate is dropped, a pair kept
    }

    [Fact]
    public void Column_selection_order_titles_header_block_notices_and_direction_follow_the_request()
    {
        var doc = Doc([new ReportRow(ReportRowKind.GroupHeader, 0, ["1", "Assets", null, null, null, null, null]), new ReportRow(ReportRowKind.Detail, 2, ["1101", "Cash", 5m, null, null, null, null]), new ReportRow(ReportRowKind.Total, 0, ["TOTAL", null, 5m, null, null, null, null])],
            [new ReportNotice("w", "تنبيه", "Warning: incomplete")]);
        var en = Render(doc, [2, 0], "en"); var ar = Render(doc, [2, 0], "ar");
        var sheetEn = Sheet(en); var sheetAr = Sheet(ar);
        Assert.Equal("Trial balance", Inline(Cell(sheetEn, "A1")));
        Assert.Equal("ميزان المراجعة", Inline(Cell(sheetAr, "A1")));
        Assert.Equal("شركة الأمل", Inline(Cell(sheetEn, "A2")));
        Assert.Equal(("Period", "2026-01-01 .. 2026-12-31"), (Inline(Cell(sheetEn, "A3")), Inline(Cell(sheetEn, "B3"))));
        Assert.Equal(("Amount", "Code"), (Inline(Cell(sheetEn, "A5")), Inline(Cell(sheetEn, "B5"))));                   // the chosen columns in the chosen order
        Assert.Equal(("المبلغ", "الرمز"), (Inline(Cell(sheetAr, "A5")), Inline(Cell(sheetAr, "B5"))));
        Assert.NotEqual("1", (string?)sheetEn.Descendants(Ns + "sheetView").Single().Attribute("rightToLeft"));
        Assert.Equal("1", (string?)sheetAr.Descendants(Ns + "sheetView").Single().Attribute("rightToLeft"));
        Assert.Equal("Warning: incomplete", Inline(Cell(sheetEn, "A9")));
        Assert.Equal("frozen", (string?)sheetEn.Descendants(Ns + "pane").Single().Attribute("state"));
        Assert.Equal("1", (string?)sheetEn.Descendants(Ns + "pageSetup").Single().Attribute("fitToWidth"));
        // indentation of a nested line is a cell style, never leading spaces in the data
        Assert.Equal("1101", Inline(Cell(sheetEn, "B7")));
        Assert.Equal(7, sheetEn.Descendants(Ns + "row").Count() - 2);                                                  // title, company, header line, blank, column headers, 3 data rows, notice (9 rows)
    }

    [Fact]
    public void The_sheet_name_is_safe_and_the_document_properties_name_the_report()
    {
        var bytes = Render(Doc([], title: "Profit/Loss: [2026] * ? \\ very long very long very long title"));
        using var package = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var name = package.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single().Name!.Value!;
        Assert.True(name.Length <= 31 && name.IndexOfAny("[]:*?/\\".ToCharArray()) < 0, name);
        Assert.Empty(new OpenXmlValidator().Validate(package));
    }

    [Fact]
    public async Task A_very_large_report_streams_row_by_row_with_bounded_memory()
    {
        const int Rows = 400_000;
        var baseline = GC.GetTotalMemory(true);
        long peak = 0;
        async IAsyncEnumerable<ReportRow> Many()
        {
            for (var i = 0; i < Rows; i++)
            {
                if (i % 50_000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(true) - baseline);          // sampled while the export is in flight
                yield return new ReportRow(ReportRowKind.Detail, 0, [$"{i:000000}", $"Account {i}", i * 1.25m, new DateOnly(2026, 1, 1).AddDays(i % 300), (long)i, true, 1m]);
            }
            await Task.CompletedTask;
        }
        var path = Path.Combine(Path.GetTempPath(), "hasm-xlsx-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
                await new XlsxReportRenderer().RenderAsync(new ReportDocument { ReportId = "big", TitleAr = "ك", TitleEn = "Big", Columns = Cols, Rows = _ => Many(), RowCount = Rows }, [.. Enumerable.Range(0, Cols.Count)], file, default);
            Assert.True(peak < 120L * 1024 * 1024, $"peak managed memory above the baseline was {peak / 1024 / 1024} MB");
            // count the rows with a streaming reader (the file itself is never loaded whole)
            using var zip = ZipFile.OpenRead(path);
            using var s = zip.Entries.Single(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)).Open();
            using var xr = System.Xml.XmlReader.Create(s);
            var rows = 0; while (xr.Read()) if (xr.NodeType == System.Xml.XmlNodeType.Element && xr.LocalName == "row") rows++;
            Assert.Equal(Rows + 3, rows);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_row_limit_of_a_worksheet_is_far_above_the_default_export_limit_and_the_service_caps_xlsx_below_it()
    {
        Assert.True(XlsxReportRenderer.MaxSheetRows < 1_048_576);
        Assert.True(new ReportOptions().MaxExportRows < XlsxReportRenderer.MaxSheetRows);
        Assert.Equal("xlsx", new XlsxReportRenderer().Extension);
        Assert.StartsWith("application/vnd.openxmlformats", new XlsxReportRenderer().ContentType);
    }
}
