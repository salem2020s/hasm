using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using System.IO.Compression;
using System.Security;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Hasm.Infrastructure.Reporting;

/// <summary>Text safety shared by the exports.</summary>
public static class ReportText
{
    /// <summary>A text that a spreadsheet could read as a formula (it starts with =, +, -, @, tab or carriage return) gets a leading apostrophe, exactly like the CSV export. Numbers and dates are typed cells and are never affected.</summary>
    public static string Neutralize(string text) => text.Length > 0 && "=+-@\t\r".Contains(text[0]) ? "'" + text : text;

    /// <summary>Removes the characters XML cannot carry (control characters other than tab, line feed and carriage return, unpaired surrogates, U+FFFE and U+FFFF).</summary>
    public static string XmlSafe(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); continue; }
            if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Real XLSX (Open XML) export, streamed row by row so memory stays bounded whatever the size (OpenXmlWriter; no workbook object model). Typed cells: numbers are numeric cells with a number format, dates are real dates,
/// account codes and other codes are TEXT cells (leading zeros preserved), text is stored as literal strings (never a formula; a leading =, +, -, @ is neutralised as in the CSV). Excel stores numbers as 64-bit floating
/// point with about 15 significant digits: an amount with MORE significant digits than that is written as its exact decimal TEXT (and the sheet says so) instead of silently losing digits.
/// Arabic sheets are right-to-left. The header block states the report, period/filters, currency and classification version; notices (incomplete classification, not applicable, failed checks) are printed below the data.
/// </summary>
public sealed class XlsxReportRenderer : IReportRenderer
{
    public string Format => "xlsx";
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string Extension => "xlsx";

    /// <summary>Excel's limit is 1,048,576 rows per sheet; a document must stay clearly below it (the export service enforces its own, lower limit first).</summary>
    public const long MaxSheetRows = 1_048_000;
    private const int MaxSignificantDigits = 15;

    // ------------------------------------------------------------------------------------------------------------------------------------ styles
    private enum Kind { Normal, Bold, Total }
    private static readonly ReportColumnType[] Types = [ReportColumnType.Text, ReportColumnType.Code, ReportColumnType.Money, ReportColumnType.Date, ReportColumnType.Integer, ReportColumnType.Percent, ReportColumnType.Rate, ReportColumnType.Boolean];
    private const uint DateFmt = 164, MoneyFmt = 165, RateFmt = 166, PctFmt = 167;
    private const int Indents = 4;
    private const uint TitleStyle = 1, HeaderStyle = 2, LabelStyle = 3, NoticeStyle = 4, FirstDataStyle = 5;

    private static uint StyleOf(Kind kind, ReportColumnType type, int indent) =>
        FirstDataStyle + (uint)(((int)kind * Types.Length + Array.IndexOf(Types, type)) * Indents + Math.Clamp(indent, 0, Indents - 1));

    private static Stylesheet BuildStyles()
    {
        var numFmts = new NumberingFormats(
            new NumberingFormat { NumberFormatId = DateFmt, FormatCode = "yyyy\\-mm\\-dd" },
            new NumberingFormat { NumberFormatId = MoneyFmt, FormatCode = "#,##0.0000;\\-#,##0.0000" },
            new NumberingFormat { NumberFormatId = RateFmt, FormatCode = "0.0000000000" },
            new NumberingFormat { NumberFormatId = PctFmt, FormatCode = "0.00" });
        var fonts = new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Arial" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Arial" }),
            new Font(new Bold(), new FontSize { Val = 14 }, new FontName { Val = "Arial" }),
            new Font(new Italic(), new FontSize { Val = 10 }, new FontName { Val = "Arial" }));
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFD9E1F2" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF2F2F2" }) { PatternType = PatternValues.Solid }));
        var borders = new Borders(
            new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
            new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder { Style = BorderStyleValues.Thin }, new DiagonalBorder()),
            new Border(new LeftBorder(), new RightBorder(), new TopBorder { Style = BorderStyleValues.Thin }, new BottomBorder(), new DiagonalBorder()));
        var formats = new CellFormats(
            new CellFormat(),                                                                                                                                                                  // 0 default
            new CellFormat { FontId = 2, ApplyFont = true },                                                                                                                                   // 1 title
            new CellFormat { FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true, Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = true } },  // 2 header
            new CellFormat { FontId = 1, ApplyFont = true },                                                                                                                                   // 3 label
            new CellFormat { FontId = 3, ApplyFont = true, ApplyAlignment = true, Alignment = new Alignment { WrapText = false } });                                                          // 4 notice
        foreach (var kind in Enum.GetValues<Kind>())
            foreach (var type in Types)
                for (var indent = 0; indent < Indents; indent++)
                {
                    var numFmt = type switch { ReportColumnType.Code => 49u, ReportColumnType.Money => MoneyFmt, ReportColumnType.Date => DateFmt, ReportColumnType.Integer => 1u, ReportColumnType.Percent => PctFmt, ReportColumnType.Rate => RateFmt, _ => 0u };
                    var numeric = type is ReportColumnType.Money or ReportColumnType.Integer or ReportColumnType.Percent or ReportColumnType.Rate;
                    formats.Append(new CellFormat
                    {
                        NumberFormatId = numFmt, FontId = kind == Kind.Normal ? 0u : 1u, FillId = kind == Kind.Total ? 3u : 0u, BorderId = kind == Kind.Total ? 2u : 0u,
                        ApplyNumberFormat = numFmt != 0, ApplyFont = kind != Kind.Normal, ApplyFill = kind == Kind.Total, ApplyBorder = kind == Kind.Total, ApplyAlignment = true,
                        Alignment = new Alignment { Horizontal = numeric ? HorizontalAlignmentValues.Right : HorizontalAlignmentValues.General, Indent = type is ReportColumnType.Text or ReportColumnType.Code ? (uint)indent : 0u },
                    });
                }
        return new Stylesheet(numFmts, fonts, fills, borders, formats);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ cells
    private static string Ref(int column, long row)
    {
        var name = new StringBuilder();
        for (var c = column; c > 0; c = (c - 1) / 26) name.Insert(0, (char)('A' + (c - 1) % 26));
        return name.Append(row.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    /// <summary>True when the decimal has at most 15 significant digits, so a 64-bit float (what Excel stores) holds it exactly as typed.</summary>
    /// <summary>The same value without insignificant trailing zeros (5600.0000 is written as 5600): the number is identical, only its spelling is canonical.</summary>
    public static decimal Canonical(decimal d) => d / 1.0000000000000000000000000000m;

    public static bool FitsExcel(decimal d)
    {
        var digits = Math.Abs(Canonical(d)).ToString("0.############################", CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0');
        return digits.Length <= MaxSignificantDigits;
    }

    private static void Text(OpenXmlWriter w, int col, long row, string? text, uint style)
    {
        if (string.IsNullOrEmpty(text)) { w.WriteElement(new Cell { CellReference = Ref(col, row), StyleIndex = style }); return; }
        w.WriteStartElement(new Cell { CellReference = Ref(col, row), StyleIndex = style, DataType = CellValues.InlineString });
        w.WriteElement(new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(ReportText.XmlSafe(ReportText.Neutralize(text))) { Space = SpaceProcessingModeValues.Preserve }));
        w.WriteEndElement();
    }

    private static void Number(OpenXmlWriter w, int col, long row, string invariant, uint style)
    {
        w.WriteStartElement(new Cell { CellReference = Ref(col, row), StyleIndex = style });
        w.WriteElement(new CellValue(invariant));
        w.WriteEndElement();
    }

    private static string L(bool ar, string en, string arText) => ar ? arText : en;

    public async Task RenderAsync(ReportDocument document, IReadOnlyList<int> columns, Stream output, CancellationToken ct, string language = "en")
    {
        var ar = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
        var title = ar ? document.TitleAr : document.TitleEn;
        var sheetName = new string(title.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        sheetName = sheetName.Length == 0 ? "Report" : sheetName[..Math.Min(31, sheetName.Length)];
        // The package is written straight into the output as a ZIP stream (entry by entry, the worksheet row by row): memory stays bounded whatever the size. The small parts are plain, schema-valid XML.
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Part(string name, string xml) { var e = zip.CreateEntry(name, CompressionLevel.Optimal); using var es = e.Open(); using var tw = new StreamWriter(es, new UTF8Encoding(false)); tw.Write(xml); }
        const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        Part("[Content_Types].xml", Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
        Part("_rels/.rels", Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
        Part("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>" + SecurityElement.Escape(ReportText.XmlSafe(title)) + "</dc:title><dc:creator>HASM</dc:creator></cp:coreProperties>");
        Part("xl/workbook.xml", Decl + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"" + SecurityElement.Escape(ReportText.XmlSafe(sheetName)) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Part("xl/styles.xml", Decl + BuildStyles().OuterXml);
        var sheetStream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
        long row = 0; var textFallback = 0;
        using (var w = OpenXmlWriter.Create(sheetStream, new UTF8Encoding(false)))
        {
            var headerRows = 1 + (document.CompanyName is null ? 0 : 1) + document.Header.Count + 1;           // title, company, header lines, blank
            var headerRowIndex = headerRows + 1;
            w.WriteStartElement(new Worksheet());
            w.WriteElement(new SheetProperties(new PageSetupProperties { FitToPage = true }));
            w.WriteStartElement(new SheetViews());
            w.WriteStartElement(new SheetView { WorkbookViewId = 0, RightToLeft = ar });
            w.WriteElement(new Pane { VerticalSplit = headerRowIndex, TopLeftCell = $"A{headerRowIndex + 1}", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen });
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement(new Columns());
            for (var i = 0; i < columns.Count; i++) w.WriteElement(new Column { Min = (uint)(i + 1), Max = (uint)(i + 1), Width = Math.Max(8, document.Columns[columns[i]].Width), CustomWidth = true });
            w.WriteEndElement();
            w.WriteStartElement(new SheetData());

            void Row(Action body) { row++; w.WriteStartElement(new Row { RowIndex = (uint)row }); body(); w.WriteEndElement(); }
            Row(() => Text(w, 1, row, title, TitleStyle));
            if (document.CompanyName is { } company) Row(() => Text(w, 1, row, company, LabelStyle));
            foreach (var h in document.Header) Row(() => { Text(w, 1, row, ar ? h.LabelAr : h.LabelEn, LabelStyle); Text(w, 2, row, h.Value, 0); });
            Row(() => { });
            Row(() => { for (var i = 0; i < columns.Count; i++) { var c = document.Columns[columns[i]]; Text(w, i + 1, row, ar ? c.TitleAr : c.TitleEn, HeaderStyle); } });

            await foreach (var r in document.Rows(ct).WithCancellation(ct))
            {
                if (row + 1 > MaxSheetRows) throw new InvalidOperationException("The report has more rows than a worksheet can hold.");
                var kind = r.Kind switch { ReportRowKind.GroupHeader => Kind.Bold, ReportRowKind.Subtotal or ReportRowKind.Total => Kind.Total, _ => Kind.Normal };
                Row(() =>
                {
                    for (var i = 0; i < columns.Count; i++)
                    {
                        var col = document.Columns[columns[i]]; var v = r.Cells[columns[i]];
                        var style = StyleOf(kind, col.Type, i == 0 || col.Type is ReportColumnType.Text ? r.Level : 0);
                        switch (v)
                        {
                            case null: w.WriteElement(new Cell { CellReference = Ref(i + 1, row), StyleIndex = style }); break;
                            case decimal d when col.Type is ReportColumnType.Money or ReportColumnType.Percent or ReportColumnType.Rate or ReportColumnType.Integer:
                                if (FitsExcel(d)) Number(w, i + 1, row, Canonical(d).ToString(CultureInfo.InvariantCulture), style);
                                else { textFallback++; Text(w, i + 1, row, Canonical(d).ToString(CultureInfo.InvariantCulture), StyleOf(kind, ReportColumnType.Code, 0)); }
                                break;
                            case decimal d: Text(w, i + 1, row, Canonical(d).ToString(CultureInfo.InvariantCulture), style); break;
                            case long or int or short: Number(w, i + 1, row, Convert.ToString(v, CultureInfo.InvariantCulture)!, style); break;
                            case DateOnly date: Number(w, i + 1, row, (date.DayNumber - new DateOnly(1899, 12, 30).DayNumber).ToString(CultureInfo.InvariantCulture), StyleOf(kind, ReportColumnType.Date, 0)); break;
                            case bool b: Text(w, i + 1, row, b ? L(ar, "Yes", "نعم") : L(ar, "No", "لا"), style); break;
                            default: Text(w, i + 1, row, Convert.ToString(v, CultureInfo.InvariantCulture), style); break;
                        }
                    }
                });
            }
            if (textFallback > 0)
                Row(() => Text(w, 1, row, L(ar, $"Note: {textFallback} amount(s) have more than 15 significant digits, which Excel cannot hold exactly as numbers; they are written as exact text.", $"ملاحظة: {textFallback} مبلغ(ات) تتجاوز 15 خانة معنوية ولا يستطيع Excel حفظها كأرقام بدقة؛ كُتبت كنص دقيق."), NoticeStyle));
            foreach (var n in document.Notices) Row(() => Text(w, 1, row, ar ? n.TextAr : n.TextEn, NoticeStyle));
            w.WriteEndElement();                                                                                    // SheetData
            w.WriteElement(new PageMargins { Left = 0.5, Right = 0.5, Top = 0.6, Bottom = 0.6, Header = 0.3, Footer = 0.3 });
            w.WriteElement(new PageSetup { Orientation = columns.Count > 6 ? OrientationValues.Landscape : OrientationValues.Portrait, FitToWidth = 1, FitToHeight = 0 });
            w.WriteEndElement();                                                                                    // Worksheet
        }
        await sheetStream.DisposeAsync();
    }
}
