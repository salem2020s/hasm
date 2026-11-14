using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Builds minimal .xlsx files for tests (no spreadsheet application, no library): strings are written inline, numbers carry an optional number format, and every part can be replaced or added so
/// that hostile structures (formulas, DTDs, macros, external links, bombs, traversal names) can be produced exactly.
/// </summary>
public sealed class XlsxBuilder
{
    public sealed record Cell(string Ref, object? Value, int Style = 0, string? Formula = null);

    private readonly List<(string Name, List<(int Row, List<Cell> Cells)> Rows, string State)> sheets = [];
    public readonly Dictionary<string, byte[]> Extra = [];
    public readonly Dictionary<string, string> Replace = [];
    public string ContentTypesExtra = "";
    public string WorkbookExtra = "";
    public string StylesXml = Styles;
    public List<string> RawEntryNames = [];

    public const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0000\"/></numFmts>" +
        "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
        "</styleSheet>";

    public XlsxBuilder Sheet(string name, IEnumerable<IEnumerable<object?>> rows, string state = "visible", int firstRow = 1)
    {
        var list = new List<(int, List<Cell>)>();
        var r = firstRow;
        foreach (var row in rows)
        {
            var cells = new List<Cell>(); var c = 0;
            foreach (var v in row) { cells.Add(v is Cell cell ? cell : new Cell(ColumnName(c) + r, v)); c++; }
            list.Add((r++, cells));
        }
        sheets.Add((name, list, state));
        return this;
    }

    public static string ColumnName(int index)
    {
        var s = ""; index++;
        while (index > 0) { var m = (index - 1) % 26; s = (char)('A' + m) + s; index = (index - 1) / 26; }
        return s;
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s);

    private string SheetXml(List<(int Row, List<Cell> Cells)> rows)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        foreach (var (row, cells) in rows)
        {
            sb.Append($"<row r=\"{row}\">");
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                var reference = c.Ref.Length > 0 ? c.Ref : ColumnName(i) + row;
                var f = c.Formula is null ? "" : $"<f>{Esc(c.Formula)}</f>";
                switch (c.Value)
                {
                    case null when c.Formula is null: break;
                    case null: sb.Append($"<c r=\"{reference}\" s=\"{c.Style}\">{f}</c>"); break;
                    case string s: sb.Append($"<c r=\"{reference}\" s=\"{c.Style}\" t=\"inlineStr\">{f}<is><t xml:space=\"preserve\">{Esc(s)}</t></is></c>"); break;
                    case bool b: sb.Append($"<c r=\"{reference}\" s=\"{c.Style}\" t=\"b\">{f}<v>{(b ? 1 : 0)}</v></c>"); break;
                    case DateTime d: sb.Append($"<c r=\"{reference}\" s=\"2\">{f}<v>{(d - new DateTime(1899, 12, 30)).TotalDays.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>"); break;
                    default: sb.Append($"<c r=\"{reference}\" s=\"{c.Style}\">{f}<v>{Convert.ToString(c.Value, System.Globalization.CultureInfo.InvariantCulture)}</v></c>"); break;
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    public byte[] Build()
    {
        var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 0; i < sheets.Count; i++) ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        ct.Append(ContentTypesExtra).Append("</Types>");
        var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 0; i < sheets.Count; i++)
        {
            wb.Append($"<sheet name=\"{Esc(sheets[i].Name)}\" sheetId=\"{i + 1}\" state=\"{sheets[i].State}\" r:id=\"rId{i + 1}\"/>");
            rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        }
        wb.Append("</sheets>").Append(WorkbookExtra).Append("</workbook>");
        rels.Append($"<Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = ct.ToString(),
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = wb.ToString(),
            ["xl/_rels/workbook.xml.rels"] = rels.ToString(),
            ["xl/styles.xml"] = StylesXml,
        };
        for (var i = 0; i < sheets.Count; i++) parts[$"xl/worksheets/sheet{i + 1}.xml"] = SheetXml(sheets[i].Rows);
        foreach (var (k, v) in Replace) parts[k] = v;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in parts) { var e = zip.CreateEntry(name, CompressionLevel.Optimal); using var s = e.Open(); s.Write(Encoding.UTF8.GetBytes(text)); }
            foreach (var (name, bytes) in Extra) { var e = zip.CreateEntry(name, CompressionLevel.Optimal); using var s = e.Open(); s.Write(bytes); }
            foreach (var name in RawEntryNames) { var e = zip.CreateEntry(name); using var s = e.Open(); s.Write("<x/>"u8); }
        }
        return ms.ToArray();
    }

    /// <summary>A workbook with one sheet "Sheet1".</summary>
    public static byte[] Simple(params object?[][] rows) => new XlsxBuilder().Sheet("Sheet1", rows).Build();
}
