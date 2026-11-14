using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ExcelDataReader;

namespace Hasm.Infrastructure.Imports;

public enum ImportFileKind : short { Csv = 1, Xlsx = 2 }

/// <summary>One worksheet of a workbook (a CSV file has exactly one, named "csv"). Counts come from the security scan; <c>Hidden</c> marks hidden/very hidden sheets.</summary>
public sealed record SheetInfo(int Index, string Name, long Rows, int Columns, bool Hidden);

public sealed record FileInspection(ImportFileKind Kind, IReadOnlyList<SheetInfo> Sheets);

/// <summary>How to read a file into staging rows. <c>HeaderRow</c> is the 1-based source row (CSV: record) holding the column titles; <c>Encoding</c> and <c>Delimiter</c> apply to CSV only.</summary>
public sealed record ReadOptions(int SheetIndex = 0, int HeaderRow = 1, string Encoding = "utf-8", char? Delimiter = null);

/// <summary>A staged data row: its 1-based source row (CSV: record) number and its cells as canonical strings (null = empty), Unicode NFC.</summary>
public sealed record ParsedRow(int RowNo, string?[] Cells);

public sealed record ParsedSheet(IReadOnlyList<string> Headers, IReadOnlyList<ParsedRow> Rows, char? Delimiter);

/// <summary>
/// Reads uploaded CSV and .xlsx files. EVERY uploaded byte is untrusted:
/// <list type="bullet">
/// <item>Only <c>.csv</c> and <c>.xlsx</c>; the extension must agree with the real structure (legacy XLS/OLE, macro-enabled XLSM, PDFs, executables and renamed text are refused).</item>
/// <item>A streaming security scan of the zip container runs BEFORE any parser sees the file: entry-count, declared and ACTUAL expanded-size limits (a lying header cannot defeat it), path traversal,
/// DTD/entity expansion prohibited, nesting depth, per-text-node length, rows/columns/worksheets, processing-time budget, and refusal of macros, ActiveX, embedded objects, data connections, external links and
/// <b>any formula cell</b> (formulas are never executed and cached results are never trusted).</item>
/// <item>Values come from the maintained read-only library ExcelDataReader (pinned); numeric cells are rendered to canonical text (identifier-style number formats such as <c>0000</c> keep their leading
/// zeros), dates to ISO text; text is normalized to Unicode NFC; control characters are refused.</item>
/// <item>CSV is decoded strictly (UTF-8, UTF-16 with BOM or Windows-1256, chosen explicitly; invalid bytes are refused, never replaced) and parsed per RFC 4180 with the same limits.</item>
/// </list>
/// Nothing here writes anywhere or evaluates anything.
/// </summary>
public static class ImportFileReader
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "xl/vbaproject", "xl/externallinks/", "xl/embeddings/", "xl/activex/", "xl/macrosheets/", "xl/dialogsheets/", "xl/ctrlprops/", "xl/connections.xml", "xl/querytables/", "xl/pivotcache/",
    ];

    static ImportFileReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ------------------------------------------------------------------------------------------------------------------------------ detection

    public static ImportFileKind DetectKind(string fileName, ReadOnlySpan<byte> data, ImportOptions limits)
    {
        if (data.Length == 0) throw new ImportRejectedException("empty_file");
        if (data.Length > limits.MaxFileBytes) throw new ImportRejectedException("file_too_large", limits.MaxFileBytes.ToString(CultureInfo.InvariantCulture));
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var zip = data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;
        var ole = data.Length >= 4 && data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0;
        var other = (data.Length >= 4 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46) || (data.Length >= 2 && data[0] == 0x4D && data[1] == 0x5A)
            || (data.Length >= 4 && data[0] == 0x7F && data[1] == 0x45 && data[2] == 0x4C && data[3] == 0x46);
        switch (ext)
        {
            case ".xlsx":
                if (ole || other || !zip) throw new ImportRejectedException("unsupported_format", "content_does_not_match_extension");
                return ImportFileKind.Xlsx;
            case ".csv":
                if (zip || ole || other) throw new ImportRejectedException("unsupported_format", "content_does_not_match_extension");
                return ImportFileKind.Csv;
            default:
                throw new ImportRejectedException("unsupported_format", "only_xlsx_and_csv");
        }
    }

    /// <summary>Safe display name: the file-name part only, control characters removed, at most 255 characters.</summary>
    public static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        var clean = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (clean.Length > 255) clean = clean[^255..];
        return clean.Length == 0 ? "upload" : clean;
    }

    // ------------------------------------------------------------------------------------------------------------------------------ inspection

    public static FileInspection Inspect(string fileName, byte[] data, ImportOptions limits)
    {
        var kind = DetectKind(fileName, data, limits);
        if (kind == ImportFileKind.Csv)
        {
            return new FileInspection(kind, [new SheetInfo(0, "csv", 0, 0, false)]);
        }
        return new FileInspection(kind, ScanXlsx(data, limits));
    }

    private sealed class Budget(ImportOptions limits)
    {
        public long Read;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public void Tick() { if (Clock.Elapsed.TotalSeconds > limits.MaxProcessingSeconds) throw new ImportRejectedException("processing_timeout"); }
        public void Add(long n) { Read += n; if (Read > limits.MaxExpandedBytes) throw new ImportRejectedException("expanded_too_large"); }
    }

    /// <summary>Counts the bytes ACTUALLY produced by a zip entry (the declared size may lie) and aborts at the shared budget.</summary>
    private sealed class CountingStream(Stream inner, Budget budget) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); budget.Add(n); return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); budget.Add(n); return n; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static XmlReader Open(ZipArchiveEntry entry, Budget budget, ImportOptions limits) =>
        XmlReader.Create(new CountingStream(entry.Open(), budget), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true, IgnoreComments = true, IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0, MaxCharactersInDocument = limits.MaxExpandedBytes,
        });

    private static ImportRejectedException Malformed(XmlException ex) =>
        new("malformed_file", ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase) ? "dtd_not_allowed" : "invalid_xml");

    /// <summary>Walks one XML part, enforcing depth, time and per-text-node length; the callback sees every element start (and its depth).</summary>
    private static void Walk(ZipArchiveEntry entry, Budget budget, ImportOptions limits, Action<XmlReader>? onElement = null, Action<XmlReader>? onEnd = null)
    {
        var buffer = new char[1024];
        try
        {
            using var r = Open(entry, budget, limits);
            var n = 0;
            while (r.Read())
            {
                if ((++n & 1023) == 0) budget.Tick();
                if (r.Depth > 64) throw new ImportRejectedException("malformed_file", "nesting_too_deep");
                switch (r.NodeType)
                {
                    case XmlNodeType.Element: onElement?.Invoke(r); break;
                    case XmlNodeType.EndElement: onEnd?.Invoke(r); break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        var total = 0; int got;
                        while ((got = r.ReadValueChunk(buffer, 0, buffer.Length)) > 0) { total += got; if (total > limits.MaxCellChars) throw new ImportRejectedException("cell_too_long"); }
                        break;
                }
            }
        }
        catch (XmlException ex) { throw Malformed(ex); }
        catch (InvalidDataException) { throw new ImportRejectedException("malformed_file", "corrupt_zip_entry"); }
    }

    private static int ColumnOf(string cellRef)
    {
        var c = 0;
        foreach (var ch in cellRef) { if (ch is >= 'A' and <= 'Z') c = c * 26 + (ch - 'A' + 1); else if (ch is >= 'a' and <= 'z') c = c * 26 + (ch - 'a' + 1); else break; }
        return c;
    }

    private static IReadOnlyList<SheetInfo> ScanXlsx(byte[] data, ImportOptions limits)
    {
        var budget = new Budget(limits);
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read, leaveOpen: false); }
        catch (InvalidDataException) { throw new ImportRejectedException("malformed_file", "not_a_valid_zip"); }
        using (zip)
        {
            IReadOnlyList<ZipArchiveEntry> entries;
            try { entries = [.. zip.Entries]; } catch (InvalidDataException) { throw new ImportRejectedException("malformed_file", "not_a_valid_zip"); }
            if (entries.Count > limits.MaxZipEntries) throw new ImportRejectedException("too_many_entries");
            long declared = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                var name = e.FullName;
                if (name.Length == 0 || name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':') || name.Split('/', '\\').Any(s => s == "..")) throw new ImportRejectedException("malformed_file", "unsafe_entry_name");
                if (!names.Add(name)) throw new ImportRejectedException("malformed_file", "duplicate_entry");
                var lower = name.ToLowerInvariant();
                if (ForbiddenPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
                    throw new ImportRejectedException(lower.StartsWith("xl/vbaproject") || lower.StartsWith("xl/macrosheets") ? "macro_not_allowed" : lower.StartsWith("xl/externallinks") ? "external_link_not_allowed" : "unsupported_content", name);
                declared += e.Length;
                if (declared > limits.MaxExpandedBytes) throw new ImportRejectedException("expanded_too_large");
            }
            ZipArchiveEntry? Find(string n) => entries.FirstOrDefault(e => string.Equals(e.FullName, n, StringComparison.OrdinalIgnoreCase));
            var contentTypes = Find("[Content_Types].xml") ?? throw new ImportRejectedException("malformed_file", "not_an_xlsx_workbook");
            var workbook = Find("xl/workbook.xml") ?? throw new ImportRejectedException("malformed_file", "not_an_xlsx_workbook");

            // every XML part: no DTD, bounded depth/text; the content types must not declare a macro-enabled workbook
            var macro = false;
            Walk(contentTypes, budget, limits, r =>
            {
                var ct = r.GetAttribute("ContentType");
                if (ct is not null && (ct.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) || ct.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))) macro = true;
            });
            if (macro) throw new ImportRejectedException("macro_not_allowed");

            var sheets = new List<(string Name, string RelId, bool Hidden)>();
            Walk(workbook, budget, limits, r =>
            {
                if (r.LocalName == "externalReferences") throw new ImportRejectedException("external_link_not_allowed");
                if (r.LocalName == "sheet") sheets.Add((r.GetAttribute("name") ?? "", r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? "", (r.GetAttribute("state") ?? "visible") != "visible"));
            });
            var rels = new Dictionary<string, (string Target, string Type)>(StringComparer.Ordinal);
            if (Find("xl/_rels/workbook.xml.rels") is { } relEntry)
                Walk(relEntry, budget, limits, r =>
                {
                    if (r.LocalName != "Relationship") return;
                    var type = r.GetAttribute("Type") ?? "";
                    if (type.EndsWith("/externalLink", StringComparison.OrdinalIgnoreCase)) throw new ImportRejectedException("external_link_not_allowed");
                    if (type.EndsWith("/vbaProject", StringComparison.OrdinalIgnoreCase)) throw new ImportRejectedException("macro_not_allowed");
                    rels[r.GetAttribute("Id") ?? ""] = (r.GetAttribute("Target") ?? "", type);
                });
            var worksheetSheets = sheets.Where(s => rels.TryGetValue(s.RelId, out var rel) && rel.Type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase)).ToList();
            if (worksheetSheets.Count == 0) throw new ImportRejectedException("malformed_file", "no_worksheet");
            if (worksheetSheets.Count > limits.MaxWorksheets) throw new ImportRejectedException("too_many_worksheets");

            var infos = new List<SheetInfo>();
            var scanned = new HashSet<ZipArchiveEntry>();
            for (var i = 0; i < worksheetSheets.Count; i++)
            {
                var (name, relId, hidden) = worksheetSheets[i];
                var target = rels[relId].Target.TrimStart('/');
                var path = target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
                var entry = Find(path) ?? throw new ImportRejectedException("malformed_file", "missing_worksheet_part");
                scanned.Add(entry);
                long rows = 0; var maxCol = 0; var rowHasValue = false; string cell = "";
                Walk(entry, budget, limits, r =>
                {
                    switch (r.LocalName)
                    {
                        case "c":
                            cell = r.GetAttribute("r") ?? "";
                            var col = ColumnOf(cell);
                            if (col > limits.MaxColumns) throw new ImportRejectedException("too_many_columns", limits.MaxColumns.ToString(CultureInfo.InvariantCulture));
                            if (col > maxCol) maxCol = col;
                            break;
                        case "f": throw new ImportRejectedException("formula_not_allowed", $"{name}!{cell}");
                        case "v" or "t": rowHasValue = true; break;
                        case "row": rowHasValue = false; break;
                    }
                }, r =>
                {
                    if (r.LocalName == "row" && rowHasValue) { if (++rows > (long)limits.MaxRows + 1) throw new ImportRejectedException("too_many_rows", limits.MaxRows.ToString(CultureInfo.InvariantCulture)); }
                });
                infos.Add(new SheetInfo(i, name, rows, maxCol, hidden));
            }
            // every other XML part (styles, shared strings, themes, ...): same DTD / depth / text limits
            foreach (var e in entries)
            {
                var lower = e.FullName.ToLowerInvariant();
                if (e == contentTypes || e == workbook || scanned.Contains(e) || lower == "xl/_rels/workbook.xml.rels") continue;
                if (lower.EndsWith(".xml", StringComparison.Ordinal) || lower.EndsWith(".rels", StringComparison.Ordinal))
                    Walk(e, budget, limits, r =>
                    {
                        if (r.LocalName == "Relationship" && (r.GetAttribute("Type") ?? "").EndsWith("/externalLink", StringComparison.OrdinalIgnoreCase)) throw new ImportRejectedException("external_link_not_allowed");
                        if (r.LocalName == "f" && lower.StartsWith("xl/worksheets/", StringComparison.Ordinal)) throw new ImportRejectedException("formula_not_allowed", e.FullName);
                    });
            }
            return infos;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------ reading

    /// <summary>Reads the chosen sheet into rows. The file must already have passed <see cref="Inspect"/> (the same limits are enforced again while reading).</summary>
    public static ParsedSheet Read(string fileName, byte[] data, ImportOptions limits, ReadOptions options)
    {
        var kind = DetectKind(fileName, data, limits);
        if (options.HeaderRow is < 1 or > 1000) throw new ImportRejectedException("invalid_header_row");
        return kind == ImportFileKind.Csv ? ReadCsv(data, limits, options) : ReadXlsx(data, limits, options);
    }

    private static string Clean(string value, int row, int col, ImportOptions limits)
    {
        if (value.Length > limits.MaxCellChars) throw new ImportRejectedException("cell_too_long", $"row {row}, column {col + 1}");
        foreach (var ch in value)
            if ((ch < 0x20 && ch is not ('\t' or '\n' or '\r')) || ch == 0x7F) throw new ImportRejectedException("invalid_character", $"row {row}, column {col + 1}");
        return value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);
    }

    private static string? Canonical(object? value, string? numberFormat)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s.Length == 0 ? null : s;
            case bool b: return b ? "true" : "false";
            case DateTime dt: return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) return null;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                {
                    var whole = ((long)d).ToString(CultureInfo.InvariantCulture);
                    // identifier-style formats ("0000"): keep the displayed leading zeros (account codes)
                    if (numberFormat is { Length: > 1 } && Regex.IsMatch(numberFormat, "^0+$") && d >= 0 && whole.Length < numberFormat.Length) return whole.PadLeft(numberFormat.Length, '0');
                    return whole;
                }
                var text = d.ToString("R", CultureInfo.InvariantCulture);
                return text.Contains('E', StringComparison.OrdinalIgnoreCase) ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : text;
            case int or long or decimal or float: return Convert.ToString(value, CultureInfo.InvariantCulture);
            default: return value.ToString();
        }
    }

    private static ParsedSheet ReadXlsx(byte[] data, ImportOptions limits, ReadOptions options)
    {
        var sheets = ScanXlsx(data, limits);    // the scan is repeated here: reading never trusts that inspection happened
        if (options.SheetIndex < 0 || options.SheetIndex >= sheets.Count) throw new ImportRejectedException("invalid_worksheet");
        var wanted = sheets[options.SheetIndex].Name;
        var clock = Stopwatch.StartNew();
        List<string> headers = [];
        List<ParsedRow> rows = [];
        try
        {
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(new MemoryStream(data, writable: false), new ExcelReaderConfiguration { LeaveOpen = false });
            do
            {
                if (!string.Equals(reader.Name, wanted, StringComparison.Ordinal)) continue;
                var rowNo = 0;
                while (reader.Read())
                {
                    rowNo++;
                    if ((rowNo & 1023) == 0 && clock.Elapsed.TotalSeconds > limits.MaxProcessingSeconds) throw new ImportRejectedException("processing_timeout");
                    if (rowNo < options.HeaderRow) continue;
                    var cells = new string?[Math.Min(reader.FieldCount, limits.MaxColumns + 1)];
                    if (reader.FieldCount > limits.MaxColumns) { for (var c = limits.MaxColumns; c < reader.FieldCount; c++) if (reader.GetValue(c) is not null) throw new ImportRejectedException("too_many_columns", limits.MaxColumns.ToString(CultureInfo.InvariantCulture)); }
                    for (var c = 0; c < cells.Length && c < reader.FieldCount; c++)
                    {
                        var raw = reader.GetValue(c);
                        var text = Canonical(raw, raw is double ? reader.GetNumberFormatString(c) : null);
                        cells[c] = text is null ? null : Clean(text, rowNo, c, limits);
                    }
                    var last = Array.FindLastIndex(cells, v => v is not null);
                    if (rowNo == options.HeaderRow) { headers = [.. cells.Take(last + 1).Select(v => v?.Trim() ?? "")]; continue; }
                    if (last < 0) continue;      // blank row
                    if (rows.Count >= limits.MaxRows) throw new ImportRejectedException("too_many_rows", limits.MaxRows.ToString(CultureInfo.InvariantCulture));
                    rows.Add(new ParsedRow(rowNo, cells[..(last + 1)]));
                }
                break;
            } while (reader.NextResult());
        }
        catch (ImportRejectedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException)
        {
            throw new ImportRejectedException("malformed_file", "unreadable_workbook");
        }
        return new ParsedSheet(headers, rows, null);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ CSV

    private static string Decode(byte[] data, string encoding)
    {
        try
        {
            switch (encoding.ToLowerInvariant())
            {
                case "utf-8" or "utf8":
                    var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
                    return new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
                case "utf-16" or "utf16":
                    if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return new UnicodeEncoding(false, false, true).GetString(data, 2, data.Length - 2);
                    if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return new UnicodeEncoding(true, false, true).GetString(data, 2, data.Length - 2);
                    throw new ImportRejectedException("invalid_encoding", "utf16_requires_bom");
                case "windows-1256" or "cp1256":
                    return Encoding.GetEncoding(1256, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(data);
                default: throw new ImportRejectedException("invalid_encoding", "unsupported_encoding");
            }
        }
        catch (DecoderFallbackException) { throw new ImportRejectedException("invalid_encoding", "invalid_bytes_for_encoding"); }
    }

    public static char DetectDelimiter(string text)
    {
        var inQuotes = false; int comma = 0, semi = 0, tab = 0;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (!inQuotes)
            {
                if (ch is '\n' or '\r') { if (comma + semi + tab > 0) break; }
                else if (ch == ',') comma++; else if (ch == ';') semi++; else if (ch == '\t') tab++;
            }
        }
        return semi > comma && semi >= tab ? ';' : tab > comma && tab > semi ? '\t' : ',';
    }

    private static ParsedSheet ReadCsv(byte[] data, ImportOptions limits, ReadOptions options)
    {
        var text = Decode(data, options.Encoding);
        if (text.Length > limits.MaxExpandedBytes) throw new ImportRejectedException("expanded_too_large");
        if (text.Contains('\0')) throw new ImportRejectedException("unsupported_format", "binary_content");
        var delimiter = options.Delimiter ?? DetectDelimiter(text);
        if (delimiter is not (',' or ';' or '\t' or '|')) throw new ImportRejectedException("invalid_delimiter");
        var clock = Stopwatch.StartNew();
        var records = new List<(int No, List<string?> Cells)>();
        var field = new StringBuilder(); var cells = new List<string?>();
        var inQuotes = false; var quoted = false; var recordNo = 1; var i = 0;
        void EndField()
        {
            var v = field.ToString(); field.Clear();
            cells.Add(v.Length == 0 ? null : v);
            quoted = false;
            if (cells.Count > limits.MaxColumns + 1) throw new ImportRejectedException("too_many_columns", limits.MaxColumns.ToString(CultureInfo.InvariantCulture));
        }
        void EndRecord()
        {
            EndField();
            records.Add((recordNo, cells)); cells = [];
            recordNo++;
            if (records.Count > (long)limits.MaxRows + options.HeaderRow + 1000) throw new ImportRejectedException("too_many_rows", limits.MaxRows.ToString(CultureInfo.InvariantCulture));
        }
        while (i < text.Length)
        {
            if ((i & 0xFFFF) == 0 && clock.Elapsed.TotalSeconds > limits.MaxProcessingSeconds) throw new ImportRejectedException("processing_timeout");
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; } inQuotes = false; i++; continue; }
                field.Append(ch); i++;
                if (field.Length > limits.MaxCellChars) throw new ImportRejectedException("cell_too_long", $"record {recordNo}");
                continue;
            }
            if (ch == '"' && field.Length == 0 && !quoted) { inQuotes = true; quoted = true; i++; continue; }
            if (ch == '"') throw new ImportRejectedException("malformed_file", $"stray_quote_in_record_{recordNo}");
            if (ch == delimiter) { EndField(); i++; continue; }
            if (ch is '\r' or '\n') { EndRecord(); i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1; continue; }
            field.Append(ch); i++;
            if (field.Length > limits.MaxCellChars) throw new ImportRejectedException("cell_too_long", $"record {recordNo}");
        }
        if (inQuotes) throw new ImportRejectedException("malformed_file", "unterminated_quote");
        if (field.Length > 0 || cells.Count > 0 || quoted) EndRecord();

        List<string> headers = [];
        List<ParsedRow> rows = [];
        foreach (var (no, raw) in records)
        {
            if (no < options.HeaderRow) continue;
            var arr = new string?[raw.Count];
            for (var c = 0; c < raw.Count; c++) arr[c] = raw[c] is null ? null : Clean(raw[c]!, no, c, limits);
            var last = Array.FindLastIndex(arr, v => v is not null);
            if (arr.Length > limits.MaxColumns && last >= limits.MaxColumns) throw new ImportRejectedException("too_many_columns", limits.MaxColumns.ToString(CultureInfo.InvariantCulture));
            if (no == options.HeaderRow) { headers = [.. arr.Take(last + 1).Select(v => v?.Trim() ?? "")]; continue; }
            if (last < 0) continue;
            if (rows.Count >= limits.MaxRows) throw new ImportRejectedException("too_many_rows", limits.MaxRows.ToString(CultureInfo.InvariantCulture));
            rows.Add(new ParsedRow(no, arr[..(last + 1)]));
        }
        return new ParsedSheet(headers, rows, delimiter);
    }
}

/// <summary>Neutralizes cells that a spreadsheet application could interpret as a formula (for any future export or report of imported values).</summary>
public static class SpreadsheetSafe
{
    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
    }
}
