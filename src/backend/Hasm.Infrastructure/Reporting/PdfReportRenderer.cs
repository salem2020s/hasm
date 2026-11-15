using System.Globalization;
using System.Reflection;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Hasm.Infrastructure.Reporting;

public enum RunKind { Ltr = 1, Rtl = 2, ArabicDigits = 3 }

/// <summary>A piece of a text line that is shaped and drawn as one unit. <c>Rtl</c> runs are Arabic-script text (shaped right to left, letters joined), <c>Ltr</c> runs are Latin text and Western digits, <c>ArabicDigits</c> are Arabic-Indic digit groups (always read left to right).</summary>
public sealed record TextRun(string Text, RunKind Kind);

/// <summary>
/// A compact implementation of the part of the Unicode bidirectional algorithm accounting documents need: strong characters decide each run, numbers (with their separators) stay left-to-right inside right-to-left text,
/// neutral characters (spaces, punctuation) between two runs of the same direction join them (so an Arabic phrase is shaped as ONE run), and the runs of a right-to-left paragraph are laid out from the right. The result is the
/// VISUAL order of the runs, left to right. (Explicit directional marks and embedded multi-level nesting are not interpreted: report cells are short single-direction phrases with numbers and names.)
/// </summary>
public static class BidiRuns
{
    private enum T { R, L, EN, AN, Neutral }

    private static T Classify(char c)
    {
        if (c >= '0' && c <= '9') return T.EN;
        if ((c >= '٠' && c <= '٩') || (c >= '۰' && c <= '۹')) return T.AN;
        var cat = char.GetUnicodeCategory(c);
        var arabic = (c >= '؀' && c <= 'ۿ') || (c >= 'ݐ' && c <= 'ݿ') || (c >= 'ࢠ' && c <= 'ࣿ') || (c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿') || (c >= '֐' && c <= '׿');
        if (arabic && (char.IsLetter(c) || cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.ModifierLetter or System.Globalization.UnicodeCategory.EnclosingMark)) return T.R;
        if (char.IsLetter(c) || cat == System.Globalization.UnicodeCategory.NonSpacingMark) return T.L;
        return T.Neutral;
    }

    private static bool IsNumberSeparator(char c) => c is '.' or ',' or ':' or '/' or '-' or '+' or '%' or '٫' or '٬';

    public static IReadOnlyList<TextRun> Split(string text, bool paragraphRtl)
    {
        if (text.Length == 0) return [];
        var types = new T[text.Length];
        for (var i = 0; i < text.Length; i++) types[i] = Classify(text[i]);
        // a separator between two digits of the same kind belongs to the number
        for (var i = 1; i < text.Length - 1; i++)
            if (types[i] == T.Neutral && IsNumberSeparator(text[i]) && types[i - 1] == types[i + 1] && types[i - 1] is T.EN or T.AN) types[i] = types[i - 1];
        // direction of every character: strong ones decide; neutrals take the direction both neighbours share, else the paragraph direction
        var kinds = new RunKind?[text.Length];
        for (var i = 0; i < text.Length; i++) kinds[i] = types[i] switch { T.R => RunKind.Rtl, T.L or T.EN => RunKind.Ltr, T.AN => RunKind.ArabicDigits, _ => null };
        static bool IsRtl(RunKind? k) => k == RunKind.Rtl;
        for (var i = 0; i < text.Length; i++)
        {
            if (kinds[i] is not null) continue;
            var j = i; while (j < text.Length && kinds[j] is null) j++;
            RunKind? before = i > 0 ? kinds[i - 1] : null; RunKind? after = j < text.Length ? kinds[j] : null;
            var chosen = before is not null && after is not null && IsRtl(before) == IsRtl(after) ? (IsRtl(before) ? RunKind.Rtl : RunKind.Ltr) : paragraphRtl ? RunKind.Rtl : RunKind.Ltr;
            // a neutral next to Arabic digits or Latin text keeps that kind when the neighbours agree (an Arabic-digit group stays one run)
            if (before is not null && after is not null && before == after && before == RunKind.ArabicDigits) chosen = RunKind.ArabicDigits;
            for (var k = i; k < j; k++) kinds[k] = chosen;
            i = j - 1;
        }
        var runs = new List<TextRun>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
            if (i == text.Length || kinds[i] != kinds[start]) { runs.Add(new TextRun(text[start..i], kinds[start]!.Value)); start = i; }
        if (paragraphRtl) runs.Reverse();                                                                  // the first logical run sits at the right
        return runs;
    }
}

/// <summary>The redistributable fonts embedded in the product (Noto Naskh Arabic and Noto Sans, SIL Open Font License 1.1: see Reporting/Fonts/OFL.txt). No font is ever read from the machine.</summary>
public static class PdfFonts
{
    private static readonly Lazy<SKTypeface> ArabicRegular = new(() => Load("NotoNaskhArabic-Regular.ttf")), ArabicBold = new(() => Load("NotoNaskhArabic-Bold.ttf"));
    private static readonly Lazy<SKTypeface> LatinRegular = new(() => Load("NotoSans-Regular.ttf")), LatinBold = new(() => Load("NotoSans-Bold.ttf"));

    private static SKTypeface Load(string file)
    {
        var assembly = typeof(PdfFonts).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
        using var s = assembly.GetManifestResourceStream(name)!;
        using var data = SKData.Create(s);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException("The embedded report font could not be loaded.");
    }

    public static SKTypeface For(RunKind kind, bool bold) => kind == RunKind.Ltr ? (bold ? LatinBold.Value : LatinRegular.Value) : (bold ? ArabicBold.Value : ArabicRegular.Value);
}

/// <summary>
/// Server-side PDF export (A4, portrait or landscape by width). Text is shaped with HarfBuzz (Arabic letters join correctly) and drawn with embedded fonts through Skia's PDF backend (MIT-licensed SkiaSharp,
/// SkiaSharp.HarfBuzz): no commercial dependency and no font from the host. The layout is the product's own: a header block, a repeating column header, wrapped cells, indentation, subtotal/total rows, notices and
/// "Page x of y" (two passes over the document). Right-to-left reports mirror the column order. Amounts are printed exactly (grouped thousands, the decimals the system stores).
/// </summary>
/// <summary>Where the laid-out pages are drawn: a PDF document in production, bitmaps in tests (so the exact same layout can be looked at).</summary>
public interface IPageSink { SKCanvas Begin(float width, float height); void End(); }

public sealed class PdfReportRenderer : IReportRenderer
{
    public string Format => "pdf";
    public string ContentType => "application/pdf";
    public string Extension => "pdf";

    private const float Margin = 36, BodySize = 9, TitleSize = 14, MetaSize = 9.5f, FooterSize = 8, Pad = 3;
    private static readonly SKColor HeaderFill = new(0xD9, 0xE1, 0xF2), TotalFill = new(0xF2, 0xF2, 0xF2), Ink = SKColors.Black, Warning = new(0x9C, 0x1C, 0x1C);

    public static string FormatValue(object? v, ReportColumnType type, bool ar) => v switch
    {
        null => "",
        decimal d => type switch
        {
            ReportColumnType.Rate => d.ToString("0.0000000000", CultureInfo.InvariantCulture), ReportColumnType.Percent => d.ToString("0.00", CultureInfo.InvariantCulture),
            ReportColumnType.Integer => d.ToString("0", CultureInfo.InvariantCulture), _ => XlsxReportRenderer.Canonical(d) is var c && c == Math.Truncate(c) ? c.ToString("#,##0.0000", CultureInfo.InvariantCulture) : d.ToString("#,##0.0000", CultureInfo.InvariantCulture),
        },
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? (ar ? "نعم" : "Yes") : (ar ? "لا" : "No"),
        _ => (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '),
    };

    private sealed class Painter(bool rtl)
    {
        private readonly Dictionary<SKTypeface, SKShaper> shapers = [];
        private readonly Dictionary<(RunKind, bool, float), SKFont> fonts = [];

        private SKFont Font(RunKind kind, bool bold, float size)
        {
            // Naskh letterforms have a smaller x-height than the Latin face: a slightly larger size keeps both languages the same visual weight
            if (!fonts.TryGetValue((kind, bold, size), out var f)) fonts[(kind, bold, size)] = f = new SKFont(PdfFonts.For(kind, bold), kind == RunKind.Ltr ? size : size * 1.12f) { Subpixel = true, LinearMetrics = true };
            return f;
        }

        private SKShaper Shaper(SKTypeface t) { if (!shapers.TryGetValue(t, out var s)) shapers[t] = s = new SKShaper(t); return s; }

        private static string ForShaping(TextRun r) => r.Kind == RunKind.ArabicDigits ? new string(r.Text.Reverse().ToArray()) : r.Text;      // digits read left to right: feed them reversed to the right-to-left shaper

        public float Measure(string text, bool bold, float size)
        {
            float total = 0;
            foreach (var run in BidiRuns.Split(text, rtl))
            {
                var font = Font(run.Kind, bold, size);
                total += Shaper(font.Typeface!).Shape(ForShaping(run), font).Width;
            }
            return total;
        }

        /// <summary>Draws a line starting at <paramref name="x"/> (its left edge) on the baseline <paramref name="y"/>.</summary>
        public void Draw(SKCanvas canvas, string text, float x, float y, bool bold, float size, SKColor color)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            foreach (var run in BidiRuns.Split(text, rtl))
            {
                var font = Font(run.Kind, bold, size);
                var shaper = Shaper(font.Typeface!);
                var shaped = ForShaping(run);
                canvas.DrawShapedText(shaper, shaped, x, y, font, paint);
                x += shaper.Shape(shaped, font).Width;
            }
        }

        /// <summary>Greedy word wrap to a width; a single word wider than the width is cut at the last fitting character so nothing overflows into the next cell.</summary>
        public List<string> Wrap(string text, float width, bool bold, float size)
        {
            var lines = new List<string>();
            if (text.Length == 0) { lines.Add(""); return lines; }
            var line = new StringBuilder();
            foreach (var word in text.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Measure(candidate, bold, size) <= width) { line.Clear().Append(candidate); continue; }
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                var rest = word;
                while (Measure(rest, bold, size) > width && rest.Length > 1)
                {
                    var n = rest.Length - 1; while (n > 1 && Measure(rest[..n], bold, size) > width) n--;
                    lines.Add(rest[..n]); rest = rest[n..];
                }
                line.Append(rest);
            }
            lines.Add(line.ToString());
            return lines;
        }
    }

    public async Task RenderAsync(ReportDocument document, IReadOnlyList<int> columns, Stream output, CancellationToken ct, string language = "en")
    {
        var ar = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
        var cols = columns.Select(i => document.Columns[i]).ToList();
        var units = cols.Sum(c => Math.Max(6f, c.Width));
        var landscape = cols.Count > 6 || units > 90;
        var pageW = landscape ? 842f : 595f; var pageH = landscape ? 595f : 842f;
        var usable = pageW - 2 * Margin;
        var widths = cols.Select(c => usable * Math.Max(6f, c.Width) / units).ToArray();
        var painter = new Painter(ar);
        var title = ar ? document.TitleAr : document.TitleEn;
        var pages = await LayoutAsync(document, columns, cols, widths, painter, ar, pageW, pageH, title, null, null, ct);
        using var stream = new SKManagedWStream(output, false);
        using var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title, Creator = "HASM", Producer = "HASM", Author = document.CompanyName ?? "HASM", RasterDpi = 300 });
        await LayoutAsync(document, columns, cols, widths, painter, ar, pageW, pageH, title, new PdfSink(pdf), pages, ct);
        pdf.Close();
    }

    private sealed class PdfSink(SKDocument pdf) : IPageSink { public SKCanvas Begin(float w, float h) => pdf.BeginPage(w, h); public void End() => pdf.EndPage(); }

    /// <summary>The same layout drawn onto bitmaps (one per page), for tests and visual review.</summary>
    public async Task<IReadOnlyList<SKBitmap>> RenderBitmapsAsync(ReportDocument document, IReadOnlyList<int> columns, string language = "en", float scale = 1.5f, CancellationToken ct = default)
    {
        var ar = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
        var cols = columns.Select(i => document.Columns[i]).ToList();
        var units = cols.Sum(c => Math.Max(6f, c.Width));
        var landscape = cols.Count > 6 || units > 90;
        var pageW = landscape ? 842f : 595f; var pageH = landscape ? 595f : 842f;
        var widths = cols.Select(c => (pageW - 2 * Margin) * Math.Max(6f, c.Width) / units).ToArray();
        var painter = new Painter(ar); var title = ar ? document.TitleAr : document.TitleEn;
        var pages = await LayoutAsync(document, columns, cols, widths, painter, ar, pageW, pageH, title, null, null, ct);
        var sink = new BitmapSink(scale);
        await LayoutAsync(document, columns, cols, widths, painter, ar, pageW, pageH, title, sink, pages, ct);
        return sink.Pages;
    }

    private sealed class BitmapSink(float scale) : IPageSink
    {
        public List<SKBitmap> Pages { get; } = [];
        private SKCanvas? canvas;
        public SKCanvas Begin(float w, float h) { var b = new SKBitmap((int)(w * scale), (int)(h * scale)); Pages.Add(b); canvas = new SKCanvas(b); canvas.Scale(scale); canvas.Clear(SKColors.White); return canvas; }
        public void End() { canvas?.Dispose(); canvas = null; }
    }

    /// <summary>One layout pass over the document: counts the pages when <paramref name="pdf"/> is null, draws them (with "Page x of y") otherwise.</summary>
    private static async Task<int> LayoutAsync(ReportDocument doc, IReadOnlyList<int> columns, List<ReportColumn> cols, float[] widths, Painter painter, bool ar, float pageW, float pageH, string title, IPageSink? sink, int? totalPages, CancellationToken ct)
    {
        var lineH = BodySize * 1.45f;
        SKCanvas? canvas = null; var page = 0; float y = 0;
        float Left(int c) { float x = Margin; if (ar) { x = pageW - Margin; for (var k = 0; k <= c; k++) x -= widths[k]; } else for (var k = 0; k < c; k++) x += widths[k]; return x; }
        void Start()
        {
            if (sink is not null) { if (canvas is not null) sink.End(); canvas = sink.Begin(pageW, pageH); }
            page++; y = Margin;
        }
        void Footer()
        {
            if (canvas is null) return;
            var text = totalPages is null ? $"{page}" : ar ? $"صفحة {page} من {totalPages}" : $"Page {page} of {totalPages}";
            var w = painter.Measure(text, false, FooterSize);
            painter.Draw(canvas, text, (pageW - w) / 2, pageH - Margin / 2, false, FooterSize, Ink);
            var start = ar ? pageW - Margin - painter.Measure(title, false, FooterSize) : Margin;
            painter.Draw(canvas, title, start, pageH - Margin / 2, false, FooterSize, SKColors.Gray);
        }
        float StartX(float cellLeft, float cellW, float textW, bool numeric) => numeric ? cellLeft + cellW - Pad - textW : ar ? cellLeft + cellW - Pad - textW : cellLeft + Pad;
        void ColumnHeader()
        {
            var h = lineH * 1.2f + 4;
            if (canvas is not null)
            {
                using var fill = new SKPaint { Color = HeaderFill };
                canvas.DrawRect(Margin, y, pageW - 2 * Margin, h, fill);
                for (var c = 0; c < cols.Count; c++)
                {
                    var t = ar ? cols[c].TitleAr : cols[c].TitleEn;
                    var numeric = cols[c].Type is ReportColumnType.Money or ReportColumnType.Integer or ReportColumnType.Percent or ReportColumnType.Rate;
                    var w = painter.Measure(t, true, BodySize);
                    painter.Draw(canvas, t, StartX(Left(c), widths[c], Math.Min(w, widths[c] - 2 * Pad), numeric), y + h - 5, true, BodySize, Ink);
                }
            }
            y += h;
        }
        void Paragraph(string text, bool bold, float size, SKColor color, float spaceAfter)
        {
            foreach (var line in painter.Wrap(text, pageW - 2 * Margin, bold, size))
            {
                if (canvas is not null) painter.Draw(canvas, line, ar ? pageW - Margin - painter.Measure(line, bold, size) : Margin, y + size, bold, size, color);
                y += size * 1.4f;
            }
            y += spaceAfter;
        }

        Start();
        if (doc.CompanyName is { } company) Paragraph(company, true, MetaSize, Ink, 0);
        Paragraph(title, true, TitleSize, Ink, 2);
        foreach (var h in doc.Header) Paragraph($"{(ar ? h.LabelAr : h.LabelEn)}: {h.Value}", false, MetaSize, Ink, 0);
        y += 6;
        ColumnHeader();

        await foreach (var row in doc.Rows(ct).WithCancellation(ct))
        {
            var bold = row.Kind is ReportRowKind.GroupHeader or ReportRowKind.Subtotal or ReportRowKind.Total;
            var cells = new List<List<string>>(); var maxLines = 1;
            for (var c = 0; c < cols.Count; c++)
            {
                var type = cols[c].Type; var numeric = type is ReportColumnType.Money or ReportColumnType.Integer or ReportColumnType.Percent or ReportColumnType.Rate;
                var text = FormatValue(row.Cells[columns[c]], type, ar);
                var indent = c == 0 || type == ReportColumnType.Text ? Math.Min(row.Level, 4) * 8f : 0;
                var lines = numeric ? [text] : painter.Wrap(text, Math.Max(10, widths[c] - 2 * Pad - indent), bold, BodySize);
                cells.Add(lines); maxLines = Math.Max(maxLines, lines.Count);
            }
            var rowH = maxLines * lineH + 4;
            if (y + rowH > pageH - Margin - 14)
            {
                Footer(); Start(); Paragraph(title, true, MetaSize, Ink, 2); ColumnHeader();
            }
            if (canvas is not null)
            {
                if (row.Kind is ReportRowKind.Total or ReportRowKind.Subtotal)
                {
                    using var fill = new SKPaint { Color = TotalFill }; canvas.DrawRect(Margin, y, pageW - 2 * Margin, rowH, fill);
                    using var line = new SKPaint { Color = SKColors.Gray, StrokeWidth = 0.6f, Style = SKPaintStyle.Stroke }; canvas.DrawLine(Margin, y, pageW - Margin, y, line);
                }
                for (var c = 0; c < cols.Count; c++)
                {
                    var type = cols[c].Type; var numeric = type is ReportColumnType.Money or ReportColumnType.Integer or ReportColumnType.Percent or ReportColumnType.Rate;
                    var indent = c == 0 || type == ReportColumnType.Text ? Math.Min(row.Level, 4) * 8f : 0;
                    for (var k = 0; k < cells[c].Count; k++)
                    {
                        var t = cells[c][k]; if (t.Length == 0) continue;
                        var w = painter.Measure(t, bold, BodySize);
                        var x = StartX(Left(c), widths[c], w, numeric) + (numeric ? 0 : ar ? -indent : indent);
                        painter.Draw(canvas, t, x, y + lineH * (k + 1) - 1, bold, BodySize, Ink);
                    }
                }
            }
            y += rowH;
        }
        y += 8;
        foreach (var n in doc.Notices)
        {
            if (y + lineH * 2 > pageH - Margin - 14) { Footer(); Start(); }
            Paragraph(ar ? n.TextAr : n.TextEn, true, BodySize, Warning, 3);
        }
        Footer();
        if (canvas is not null && sink is not null) sink.End();
        return page;
    }
}
