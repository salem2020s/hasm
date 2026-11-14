using System.IO.Compression;
using System.Text;
using Hasm.Infrastructure.Imports;

namespace Hasm.Infrastructure.Tests;

/// <summary>CSV and XLSX reading, canonical values and every refusal of a hostile or malformed upload (no database, no file system).</summary>
public class ImportFileReaderTests
{
    private static readonly ImportOptions Limits = new();
    private static byte[] Csv(string text, bool bom = false) => [.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(text)];
    private static ParsedSheet ReadCsv(string text, ReadOptions? o = null, ImportOptions? limits = null, bool bom = false) => ImportFileReader.Read("a.csv", Csv(text, bom), limits ?? Limits, o ?? new ReadOptions());
    private static ParsedSheet ReadXlsx(byte[] data, ReadOptions? o = null, ImportOptions? limits = null) => ImportFileReader.Read("a.xlsx", data, limits ?? Limits, o ?? new ReadOptions());
    private static string Rejected(Action act) => Assert.Throws<ImportRejectedException>(act).Code;
    private static string RejectedX(byte[] data, ImportOptions? limits = null, string name = "a.xlsx") => Rejected(() => ImportFileReader.Inspect(name, data, limits ?? Limits));

    // --------------------------------------------------------------------------------------------------------------------------------- CSV

    [Fact]
    public void Csv_keeps_text_exactly_leading_zeros_arabic_quotes_newlines_and_formula_looking_values_and_numbers_stay_text()
    {
        var p = ReadCsv("code,name,amount\n0101,\"الصندوق, النقدي\",\"1,250.50\"\n0102,\"line1\nline2 \"\"quoted\"\"\",=SUM(A1)\n", bom: true);
        Assert.Equal(["code", "name", "amount"], p.Headers);
        Assert.Equal(new[] { "0101", "الصندوق, النقدي", "1,250.50" }, p.Rows[0].Cells);
        Assert.Equal("line1\nline2 \"quoted\"", p.Rows[1].Cells[1]);
        Assert.Equal("=SUM(A1)", p.Rows[1].Cells[2]);                                         // stored as text, never evaluated
        Assert.Equal((2, 3), (p.Rows[0].RowNo, p.Rows[1].RowNo));
        Assert.Equal(',', p.Delimiter);
    }

    [Fact]
    public void Csv_detects_or_takes_the_delimiter_numbers_source_rows_and_skips_blank_rows_and_pre_header_lines()
    {
        var semi = ReadCsv("a;b;c\n1;2;3\n");
        Assert.Equal((';', 3), (semi.Delimiter, semi.Rows[0].Cells.Length));
        Assert.Equal('\t', ReadCsv("a\tb\n1\t2\n").Delimiter);
        Assert.Equal(',', ReadCsv("only\none\n").Delimiter);
        Assert.Equal('|', ReadCsv("a|b\n1|2\n", new ReadOptions(Delimiter: '|')).Delimiter);
        var p = ReadCsv("Report title\n\ncode,name\n1,A\n\n,\n2,B\r\n3,C", new ReadOptions(HeaderRow: 3));
        Assert.Equal(["code", "name"], p.Headers);
        Assert.Equal([4, 7, 8], p.Rows.Select(r => r.RowNo));                                  // blank rows skipped, numbers stay the source record numbers
        Assert.Equal("C", p.Rows[2].Cells[1]);
        Assert.Equal("invalid_delimiter", Rejected(() => ReadCsv("a,b", new ReadOptions(Delimiter: 'x'))));
        Assert.Equal("invalid_header_row", Rejected(() => ReadCsv("a,b", new ReadOptions(HeaderRow: 0))));
    }

    [Fact]
    public void Csv_decodes_strictly_utf8_utf16_with_bom_and_windows_1256_and_never_replaces_invalid_bytes()
    {
        Assert.Equal("جدول", ReadCsv("a\nجدول").Rows[0].Cells[0]);
        var arabicAnsi = Encoding.GetEncoding(1256).GetBytes("a\nحساب\n");
        Assert.Equal("حساب", ImportFileReader.Read("a.csv", arabicAnsi, Limits, new ReadOptions(Encoding: "windows-1256")).Rows[0].Cells[0]);
        Assert.Equal("invalid_encoding", Rejected(() => ImportFileReader.Read("a.csv", arabicAnsi, Limits, new ReadOptions())));          // cp1256 bytes are not valid UTF-8: refused, not garbled
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a\nx\n")).ToArray();
        Assert.Equal("x", ImportFileReader.Read("a.csv", utf16, Limits, new ReadOptions(Encoding: "utf-16")).Rows[0].Cells[0]);
        Assert.Equal("invalid_encoding", Rejected(() => ImportFileReader.Read("a.csv", Encoding.Unicode.GetBytes("a\nx\n"), Limits, new ReadOptions(Encoding: "utf-16"))));   // no BOM
        Assert.Equal("invalid_encoding", Rejected(() => ImportFileReader.Read("a.csv", Csv("a\nx"), Limits, new ReadOptions(Encoding: "ebcdic"))));
    }

    [Fact]
    public void Csv_refuses_malformed_binary_and_oversized_content()
    {
        Assert.Equal("malformed_file", Rejected(() => ReadCsv("a,b\n\"unterminated,1\n")));
        Assert.Equal("malformed_file", Rejected(() => ReadCsv("a,b\nab\"cd,1\n")));
        Assert.Equal("unsupported_format", Rejected(() => ReadCsv("a,b\n1\02\n")));                // NUL: binary content
        Assert.Equal("invalid_character", Rejected(() => ReadCsv("a,b\n1\u0007,2\n")));
        var small = new ImportOptions { MaxCellChars = 10, MaxColumns = 3, MaxRows = 2 };
        Assert.Equal("cell_too_long", Rejected(() => ReadCsv("a,b\n12345678901,2\n", limits: small)));
        Assert.Equal("cell_too_long", Rejected(() => ReadCsv("a,b\n\"12345678901\",2\n", limits: small)));
        Assert.Equal("too_many_columns", Rejected(() => ReadCsv("a,b,c,d\n1,2,3,4\n", limits: small)));
        Assert.Equal("too_many_rows", Rejected(() => ReadCsv("a\n1\n2\n3\n", limits: small)));
        Assert.Equal(2, ReadCsv("a\n1\n2\n", limits: small).Rows.Count);                        // exactly at the limit is fine
        Assert.Equal("expanded_too_large", Rejected(() => ReadCsv("a\n" + new string('x', 50), limits: new ImportOptions { MaxExpandedBytes = 20 })));
    }

    [Fact]
    public void Detection_accepts_only_csv_and_xlsx_whose_content_matches_the_extension_and_rejects_everything_else()
    {
        var x = XlsxBuilder.Simple(["a"], ["1"]);
        Assert.Equal(ImportFileKind.Xlsx, ImportFileReader.DetectKind("A.XLSX", x, Limits));
        Assert.Equal(ImportFileKind.Csv, ImportFileReader.DetectKind("a.csv", Csv("a\n1"), Limits));
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.xls", x, Limits)));
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.xlsm", x, Limits)));
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.txt", Csv("a"), Limits)));
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("noextension", Csv("a"), Limits)));
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.csv", x, Limits)));                                    // a zip renamed .csv
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.xlsx", Csv("a,b\n1,2"), Limits)));                    // text renamed .xlsx
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.xlsx", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], Limits)));   // legacy XLS / OLE container
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.csv", "%PDF-1.7 x"u8.ToArray(), Limits)));
        Assert.Equal("unsupported_format", Rejected(() => ImportFileReader.DetectKind("a.csv", [0x4D, 0x5A, 0x90, 0x00], Limits)));            // executable
        Assert.Equal("empty_file", Rejected(() => ImportFileReader.DetectKind("a.csv", [], Limits)));
        Assert.Equal("file_too_large", Rejected(() => ImportFileReader.DetectKind("a.csv", Csv("abcdef"), new ImportOptions { MaxFileBytes = 5 })));
        Assert.Equal("upload", ImportFileReader.SafeFileName("   "));
        Assert.Equal("evil.csv", ImportFileReader.SafeFileName("..\\..\\dir/evil\u0000.csv"));
    }

    // --------------------------------------------------------------------------------------------------------------------------------- XLSX

    [Fact]
    public void Xlsx_values_are_canonical_text_with_identifier_formats_dates_booleans_arabic_and_source_row_numbers()
    {
        var data = new XlsxBuilder().Sheet("Accounts", [
            ["code", "name", "level", "opening", "date", "flag"],
            [new XlsxBuilder.Cell("A2", 101, 1), "الصندوق", 2, 1250.5, new DateTime(2026, 3, 9), true],
            ["0102", "  Bank  ", 0.1 + 0.2, 1e-7, new DateTime(2026, 3, 9, 14, 30, 0), false],
            [null, null, null],
            [new XlsxBuilder.Cell("A5", "0103"), "Cash"],
        ], firstRow: 1).Build();
        var p = ReadXlsx(data);
        Assert.Equal(["code", "name", "level", "opening", "date", "flag"], p.Headers);
        Assert.Equal(new[] { "0101", "الصندوق", "2", "1250.5", "2026-03-09", "true" }, p.Rows[0].Cells);   // number format 0000 keeps the leading zero
        Assert.Equal(new[] { "0102", "  Bank  ", "0.30000000000000004", "0.0000001", "2026-03-09T14:30:00", "false" }, p.Rows[1].Cells);
        Assert.Equal([2, 3, 5], p.Rows.Select(r => r.RowNo));                                          // the blank row 4 is skipped, numbers are sheet rows
        Assert.Equal("0103", p.Rows[2].Cells[0]);
    }

    [Fact]
    public void Xlsx_header_row_other_sheets_hidden_sheets_and_inspection_counts()
    {
        var data = new XlsxBuilder()
            .Sheet("First", [["Report"], ["note"], ["h1", "h2"], ["a", "b"], ["c", "d"]])
            .Sheet("Second", [["x"], ["1"]], "hidden").Build();
        var info = ImportFileReader.Inspect("a.xlsx", data, Limits);
        Assert.Equal(ImportFileKind.Xlsx, info.Kind);
        Assert.Equal([("First", 5L, 2, false), ("Second", 2L, 1, true)], info.Sheets.Select(s => (s.Name, s.Rows, s.Columns, s.Hidden)));
        var p = ReadXlsx(data, new ReadOptions(0, 3));
        Assert.Equal(["h1", "h2"], p.Headers);
        Assert.Equal([4, 5], p.Rows.Select(r => r.RowNo));
        Assert.Equal("1", ReadXlsx(data, new ReadOptions(1)).Rows[0].Cells[0]);
        Assert.Equal("invalid_worksheet", Rejected(() => ReadXlsx(data, new ReadOptions(2))));
    }

    [Fact]
    public void Xlsx_data_starting_below_the_first_row_keeps_true_sheet_row_numbers()
    {
        var data = new XlsxBuilder().Sheet("S", [["code", "name"], ["1", "A"]], firstRow: 3).Build();
        var p = ReadXlsx(data, new ReadOptions(HeaderRow: 3));
        Assert.Equal(["code", "name"], p.Headers);
        Assert.Equal(4, p.Rows[0].RowNo);
    }

    // --------------------------------------------------------------------------------------------------------------------------------- hostile files

    [Fact]
    public void Formula_cells_macros_external_links_connections_and_embedded_objects_are_refused_never_executed_or_trusted()
    {
        var formula = new XlsxBuilder().Sheet("S", [["a", "b"], [new XlsxBuilder.Cell("A2", 3, 0, "1+2"), "x"]]).Build();
        var ex = Assert.Throws<ImportRejectedException>(() => ImportFileReader.Inspect("a.xlsx", formula, Limits));
        Assert.Equal(("formula_not_allowed", "S!A2"), (ex.Code, ex.Detail));
        Assert.Equal("formula_not_allowed", Rejected(() => ReadXlsx(formula)));                                                       // reading re-checks
        var ddeStyle = new XlsxBuilder().Sheet("S", [["a"], [new XlsxBuilder.Cell("A2", null, 0, "cmd|' /C calc'!A0")]]).Build();
        Assert.Equal("formula_not_allowed", RejectedX(ddeStyle));
        foreach (var (name, expected) in new[] { ("xl/vbaProject.bin", "macro_not_allowed"), ("xl/externalLinks/externalLink1.xml", "external_link_not_allowed"), ("xl/embeddings/oleObject1.bin", "unsupported_content"),
                     ("xl/activeX/activeX1.xml", "unsupported_content"), ("xl/connections.xml", "unsupported_content"), ("xl/macrosheets/sheet1.xml", "macro_not_allowed") })
        {
            var b = new XlsxBuilder().Sheet("S", [["a"], ["1"]]); b.Extra[name] = [1, 2, 3];
            Assert.Equal(expected, RejectedX(b.Build()));
        }
        var macroTypes = new XlsxBuilder().Sheet("S", [["a"], ["1"]]); macroTypes.ContentTypesExtra = "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>";
        Assert.Equal("macro_not_allowed", RejectedX(macroTypes.Build()));
        var xlsm = new XlsxBuilder().Sheet("S", [["a"], ["1"]]);
        xlsm.Replace["[Content_Types].xml"] = "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.ms-excel.sheet.macroEnabled.main+xml\"/></Types>";
        Assert.Equal("macro_not_allowed", RejectedX(xlsm.Build()));
        var extRefs = new XlsxBuilder().Sheet("S", [["a"], ["1"]]); extRefs.WorkbookExtra = "<externalReferences><externalReference r:id=\"rId9\"/></externalReferences>";
        Assert.Equal("external_link_not_allowed", RejectedX(extRefs.Build()));
        var extRel = new XlsxBuilder().Sheet("S", [["a"], ["1"]]);
        extRel.Replace["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink\" Target=\"file:///c:/secret.xlsx\" TargetMode=\"External\"/></Relationships>";
        Assert.Equal("external_link_not_allowed", RejectedX(extRel.Build()));
    }

    [Fact]
    public void Entity_expansion_dtds_and_deep_nesting_in_any_part_are_refused()
    {
        const string bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]>";
        foreach (var part in new[] { "xl/worksheets/sheet1.xml", "xl/workbook.xml", "xl/styles.xml", "[Content_Types].xml", "xl/sharedStrings.xml" })
        {
            var b = new XlsxBuilder().Sheet("S", [["a"], ["1"]]);
            if (part == "xl/sharedStrings.xml") b.Extra[part] = Encoding.UTF8.GetBytes(bomb + "<sst><si><t>&lol3;</t></si></sst>");
            else b.Replace[part] = bomb + "<root>&lol3;</root>";
            var ex = Assert.Throws<ImportRejectedException>(() => ImportFileReader.Inspect("a.xlsx", b.Build(), Limits));
            Assert.Equal(("malformed_file", "dtd_not_allowed"), (ex.Code, ex.Detail));
        }
        var deep = new XlsxBuilder().Sheet("S", [["a"], ["1"]]);
        deep.Extra["xl/theme/theme1.xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>" + string.Concat(Enumerable.Repeat("<a>", 200)) + string.Concat(Enumerable.Repeat("</a>", 200)));
        Assert.Equal("malformed_file", RejectedX(deep.Build()));
    }

    [Fact]
    public void Zip_bombs_oversized_entries_traversal_duplicates_and_corrupt_containers_are_refused()
    {
        // 150 MB of zeros compresses to a few hundred KB: well under the file limit, far over the expanded limit
        var bomb = new XlsxBuilder().Sheet("S", [["a"], ["1"]]);
        bomb.Extra["xl/media/zeros.bin"] = new byte[150 * 1024 * 1024];
        var bytes = bomb.Build();
        Assert.True(bytes.Length < Limits.MaxFileBytes);
        Assert.Equal("expanded_too_large", RejectedX(bytes));
        Assert.Equal("expanded_too_large", Rejected(() => ReadXlsx(bytes)));
        // a single huge text node
        var huge = new XlsxBuilder().Sheet("S", [["a"], [new string('x', 5000)]]).Build();
        Assert.Equal("cell_too_long", RejectedX(huge));
        // too many entries / worksheets / rows / columns
        var many = new XlsxBuilder().Sheet("S", [["a"], ["1"]]); for (var i = 0; i < 30; i++) many.Extra[$"xl/media/i{i}.png"] = [1];
        Assert.Equal("too_many_entries", RejectedX(many.Build(), new ImportOptions { MaxZipEntries = 20 }));
        var sheets = new XlsxBuilder(); for (var i = 0; i < 5; i++) sheets.Sheet("S" + i, [["a"], ["1"]]);
        Assert.Equal("too_many_worksheets", RejectedX(sheets.Build(), new ImportOptions { MaxWorksheets = 4 }));
        Assert.Equal("too_many_rows", RejectedX(XlsxBuilder.Simple(["a"], ["1"], ["2"], ["3"]), new ImportOptions { MaxRows = 2 }));
        Assert.Equal("too_many_columns", RejectedX(XlsxBuilder.Simple(["a", "b", "c"], ["1", "2", "3"]), new ImportOptions { MaxColumns = 2 }));
        // traversal / absolute / drive / duplicate names
        foreach (var name in new[] { "../evil.xml", "xl/../../evil.xml", "/etc/passwd", "C:/evil.xml", "xl\\..\\evil.xml" })
        {
            var b = new XlsxBuilder().Sheet("S", [["a"], ["1"]]); b.RawEntryNames.Add(name);
            Assert.Equal("malformed_file", RejectedX(b.Build()));
        }
        var dup = new XlsxBuilder().Sheet("S", [["a"], ["1"]]); dup.RawEntryNames.Add("xl/styles.xml");
        Assert.Equal("malformed_file", RejectedX(dup.Build()));
        // corrupt, truncated and non-workbook containers
        var good = XlsxBuilder.Simple(["a"], ["1"]);
        Assert.Equal("malformed_file", RejectedX(good[..(good.Length / 2)]));
        var garbage = (byte[])good.Clone(); for (var i = 30; i < garbage.Length - 40; i += 7) garbage[i] ^= 0x5A;
        Assert.Contains(RejectedX(garbage), new[] { "malformed_file" });
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { var e = z.CreateEntry("hello.txt"); using var w = new StreamWriter(e.Open()); w.Write("hi"); }
            Assert.Equal("malformed_file", RejectedX(ms.ToArray()));                                                                 // a zip that is not a workbook
        }
    }

    [Fact]
    public void Output_neutralization_defuses_formula_injection_for_any_future_export()
    {
        Assert.Equal("'=cmd|' /C calc'!A0", SpreadsheetSafe.Neutralize("=cmd|' /C calc'!A0"));
        foreach (var v in new[] { "+1", "-1", "@SUM(1)", "\tx", "\rx" }) Assert.StartsWith("'", SpreadsheetSafe.Neutralize(v));
        Assert.Equal("0101", SpreadsheetSafe.Neutralize("0101"));
        Assert.Equal("", SpreadsheetSafe.Neutralize(null));
    }
}
