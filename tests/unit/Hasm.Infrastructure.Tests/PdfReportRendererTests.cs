using System.Text;
using Hasm.Infrastructure.Reporting;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// The PDF export: bidirectional run splitting, real Arabic shaping with the embedded fonts, the layout (pages, repeated headers, mirrored columns) and the structure of the produced PDF (fonts embedded, text extractable).
/// A successful file generation proves nothing about right-to-left rendering, so shaping and positions are asserted on their own.
/// </summary>
public class PdfReportRendererTests
{
    private static string Visual(IEnumerable<TextRun> runs) => string.Join("|", runs.Select(r => $"{(r.Kind == RunKind.Rtl ? 'R' : r.Kind == RunKind.Ltr ? 'L' : 'A')}:{r.Text}"));

    // ----------------------------------------------------------------------------------------------------------------------------------------- bidi
    [Fact]
    public void An_arabic_phrase_is_one_right_to_left_run_numbers_stay_left_to_right_and_the_runs_of_a_rtl_paragraph_are_laid_out_from_the_right()
    {
        Assert.Equal("R:الصندوق النقدي", Visual(BidiRuns.Split("الصندوق النقدي", true)));                                        // the space between two Arabic words joins them: one run, shaped as one
        Assert.Equal("L:1101|R:النقدية في الصندوق ", Visual(BidiRuns.Split("النقدية في الصندوق 1101", true)));
        // logical order: Arabic words, a number, Arabic words  =>  visual (left to right): the last run comes first
        Assert.Equal("R: النقدية|L:1101|R:الحساب ", Visual(BidiRuns.Split("الحساب 1101 النقدية", true)));
    }

    [Fact]
    public void Left_to_right_paragraphs_keep_the_logical_order_and_neutrals_between_different_directions_take_the_paragraph_direction()
    {
        var ltr = BidiRuns.Split("Cash الصندوق", false);
        Assert.Equal([RunKind.Ltr, RunKind.Rtl], ltr.Select(r => r.Kind));
        Assert.Equal("Cash ", ltr[0].Text);                                                                                    // the space between them stays with the paragraph direction (left to right)
        var rtl = BidiRuns.Split("Cash الصندوق", true);
        Assert.Equal([RunKind.Rtl, RunKind.Ltr], rtl.Select(r => r.Kind));                                                   // visual order of a right-to-left paragraph
        Assert.Equal("1,250.50", Assert.Single(BidiRuns.Split("1,250.50", true)).Text);                                       // a number with its separators is one run
        Assert.Equal(RunKind.Ltr, Assert.Single(BidiRuns.Split("1,250.50", true)).Kind);
        Assert.Empty(BidiRuns.Split("", true));
    }

    [Fact]
    public void Arabic_indic_digits_form_their_own_left_to_right_runs_and_parentheses_stay_with_the_arabic_text()
    {
        var runs = BidiRuns.Split("بند ١٢٣", true);
        Assert.Contains(runs, r => r.Kind == RunKind.ArabicDigits && r.Text == "١٢٣");
        var paren = BidiRuns.Split("ختامي (المقارنة)", true);
        Assert.Equal(("ختامي (المقارنة)", RunKind.Rtl), (Assert.Single(paren).Text, paren[0].Kind));
    }

    // ----------------------------------------------------------------------------------------------------------------------------------------- shaping
    [Fact]
    public void The_embedded_arabic_font_is_shaped_with_contextual_forms_and_ligatures_not_drawn_letter_by_letter()
    {
        var face = PdfFonts.For(RunKind.Rtl, false);
        using var font = new SKFont(face, 10);
        using var shaper = new SKShaper(face);
        const string Word = "الصندوق";
        var nominal = face.GetGlyphs(Word);
        var shaped = shaper.Shape(Word, font);
        Assert.NotEmpty(shaped.Codepoints);
        Assert.False(nominal.SequenceEqual(shaped.Codepoints.Select(c => (ushort)c)), "the letters must be replaced by their joined forms");
        Assert.True(shaped.Width > 0 && shaper.Shape("ا", font).Width < shaped.Width);
        // Latin and digits come from the Latin face
        Assert.NotEqual(face.FamilyName, PdfFonts.For(RunKind.Ltr, false).FamilyName);
        Assert.True(PdfFonts.For(RunKind.Rtl, true).FamilyName.Contains("Naskh") && PdfFonts.For(RunKind.Ltr, true).FamilyName.Contains("Sans"));
    }

    // ----------------------------------------------------------------------------------------------------------------------------------------- the document
    private static readonly IReadOnlyList<ReportColumn> Cols =
    [
        new("code", "رمز الحساب", "Code", ReportColumnType.Code, 14), new("name", "الاسم", "Name", ReportColumnType.Text, 40), new("amt", "المبلغ", "Amount", ReportColumnType.Money, 16),
    ];

    private static ReportDocument Doc(int rows, IReadOnlyList<ReportNotice>? notices = null, IReadOnlyList<ReportColumn>? cols = null) => new()
    {
        ReportId = "t", TitleAr = "ميزان المراجعة", TitleEn = "Trial balance", CompanyName = "شركة الأمل", BaseCurrency = "JOD", Header = [new("الفترة", "Period", "2026-01-01 .. 2026-12-31")],
        Columns = cols ?? Cols, RowCount = rows, Notices = notices ?? [],
        Rows = ct => Stream(rows, cols ?? Cols),
    };

    private static async IAsyncEnumerable<ReportRow> Stream(int rows, IReadOnlyList<ReportColumn> cols)
    {
        for (var i = 0; i < rows; i++)
            yield return i == rows - 1
                ? new ReportRow(ReportRowKind.Total, 0, ["TOTAL", "المجموع", 123456.5m])
                : new ReportRow(ReportRowKind.Detail, i % 3, [$"{1000 + i:0000}", i % 2 == 0 ? $"الصندوق النقدي رقم {i}" : $"Cash account {i} with a long English description that wraps", i * 1.5m]);
        await Task.CompletedTask;
    }

    private static byte[] Render(ReportDocument d, string lang, int[]? columns = null)
    {
        using var ms = new MemoryStream();
        new PdfReportRenderer().RenderAsync(d, columns ?? [.. Enumerable.Range(0, d.Columns.Count)], ms, default, lang).GetAwaiter().GetResult();
        return ms.ToArray();
    }

    [Fact]
    public void The_pdf_is_a_valid_document_with_the_fonts_embedded_and_the_text_extractable()
    {
        var bytes = Render(Doc(12, [new ReportNotice("w", "تنبيه", "Warning: incomplete")]), "en");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(bytes, bytes.Length - 20, 20));
        var raw = Encoding.Latin1.GetString(bytes);
        Assert.Contains("NotoSans", raw);                                                                                    // the fonts are embedded (subset names carry the family)
        Assert.Contains("NotoNaskhArabic", raw);
        Assert.Contains("FontFile", raw);
        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(1, pdf.NumberOfPages);
        var text = pdf.GetPage(1).Text;
        Assert.Contains("Trial balance", text);
        Assert.Contains("2026-01-01 .. 2026-12-31", text);
        Assert.Contains("1.5000", text);
        Assert.Contains("123,456.5000", text);                                                                                 // exact, grouped amounts
        Assert.Contains("Warning: incomplete", text);
        Assert.Contains("Page 1 of 1", text);
        Assert.Contains("1000", text);                                                                                         // codes keep their digits
    }

    [Fact]
    public void A_long_report_flows_over_pages_repeats_the_column_header_and_numbers_the_pages_of_the_total()
    {
        var bytes = Render(Doc(400), "en");
        using var pdf = PdfDocument.Open(bytes);
        var n = pdf.NumberOfPages;
        Assert.True(n >= 5, $"pages: {n}");
        for (var p = 1; p <= n; p++)
        {
            var text = pdf.GetPage(p).Text;
            Assert.Contains($"Page {p} of {n}", text);
            Assert.Contains("Code", text);                                                                                    // the header row repeats on every page
            Assert.Contains("Amount", text);
        }
        Assert.Contains("TOTAL", pdf.GetPage(n).Text);
    }

    [Fact]
    public void Right_to_left_reports_mirror_the_column_order_and_left_to_right_ones_do_not()
    {
        static double X(UglyToad.PdfPig.Content.Page page, string word) => page.GetWords().First(w => w.Text.Contains(word)).BoundingBox.Left;
        using var en = PdfDocument.Open(Render(Doc(6), "en"));
        using var ar = PdfDocument.Open(Render(Doc(6), "ar"));
        // the account code is the first column, the amount the last: left to right in English, right to left in Arabic
        var enCode = X(en.GetPage(1), "1002"); var enAmount = X(en.GetPage(1), "3.0000");
        var arCode = X(ar.GetPage(1), "1002"); var arAmount = X(ar.GetPage(1), "3.0000");
        Assert.True(enCode < enAmount, $"en: code {enCode} amount {enAmount}");
        Assert.True(arCode > arAmount, $"ar: code {arCode} amount {arAmount}");
    }

    [Fact]
    public void Landscape_is_used_for_wide_reports_and_portrait_for_narrow_ones()
    {
        using var narrow = PdfDocument.Open(Render(Doc(3), "en"));
        Assert.True(narrow.GetPage(1).Width < narrow.GetPage(1).Height);
        var wide = Enumerable.Range(0, 8).Select(i => new ReportColumn($"c{i}", $"ع{i}", $"Col {i}", ReportColumnType.Text, 14)).ToList();
        var d = new ReportDocument { ReportId = "w", TitleAr = "ع", TitleEn = "Wide", Columns = wide, RowCount = 1, Rows = _ => Rows(wide.Count) };
        static async IAsyncEnumerable<ReportRow> Rows(int n) { yield return new ReportRow(ReportRowKind.Detail, 0, [.. Enumerable.Range(0, n).Select(i => (object?)$"v{i}")]); await Task.CompletedTask; }
        using var landscape = PdfDocument.Open(Render(d, "en"));
        Assert.True(landscape.GetPage(1).Width > landscape.GetPage(1).Height);
    }

    [Fact]
    public async Task The_layout_draws_arabic_text_onto_the_page_and_can_be_looked_at()
    {
        var pages = await new PdfReportRenderer().RenderBitmapsAsync(Doc(30, [new ReportNotice("w", "تنبيه: القائمة غير مكتملة لوجود حسابات غير مصنّفة.", "Warning")]), [0, 1, 2], "ar", 1.5f);
        Assert.True(pages.Count >= 1);
        var page = pages[0];
        var dark = 0; for (var y = 0; y < page.Height; y += 2) for (var x = 0; x < page.Width; x += 2) if (page.GetPixel(x, y).Red < 90) dark++;
        Assert.True(dark > 800, $"dark pixels: {dark}");                                                                     // text was really drawn
        var dir = Environment.GetEnvironmentVariable("HASM_PDF_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            for (var i = 0; i < Math.Min(2, pages.Count); i++) { using var img = SKImage.FromBitmap(pages[i]); using var data = img.Encode(SKEncodedImageFormat.Png, 90); File.WriteAllBytes(Path.Combine(dir, $"ar-{i + 1}.png"), data.ToArray()); }
            var en = await new PdfReportRenderer().RenderBitmapsAsync(Doc(30), [0, 1, 2], "en", 1.5f);
            using var img2 = SKImage.FromBitmap(en[0]); using var data2 = img2.Encode(SKEncodedImageFormat.Png, 90); File.WriteAllBytes(Path.Combine(dir, "en-1.png"), data2.ToArray());
        }
        foreach (var p in pages) p.Dispose();
    }

    [Fact]
    public void Number_and_value_formatting_is_exact_grouped_and_localised_only_for_booleans()
    {
        Assert.Equal("1,234.5000", PdfReportRenderer.FormatValue(1234.5m, ReportColumnType.Money, false));
        Assert.Equal("-12,345,678.1235", PdfReportRenderer.FormatValue(-12345678.1235m, ReportColumnType.Money, false));
        Assert.Equal("0.7100000000", PdfReportRenderer.FormatValue(0.71m, ReportColumnType.Rate, false));
        Assert.Equal("2026-03-09", PdfReportRenderer.FormatValue(new DateOnly(2026, 3, 9), ReportColumnType.Date, true));
        Assert.Equal(("Yes", "نعم"), (PdfReportRenderer.FormatValue(true, ReportColumnType.Boolean, false), PdfReportRenderer.FormatValue(true, ReportColumnType.Boolean, true)));
        Assert.Equal("a b c", PdfReportRenderer.FormatValue("a\nb\tc", ReportColumnType.Text, false));
        Assert.Equal("", PdfReportRenderer.FormatValue(null, ReportColumnType.Text, false));
    }
}
