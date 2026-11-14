using System.Globalization;
using Hasm.Infrastructure.Reporting;

namespace Hasm.Infrastructure.Ledger;

/// <summary>Builds the neutral <see cref="ReportDocument"/> of the ledger reports from the existing ledger views (the query engine is not duplicated).</summary>
public static class LedgerReportDocuments
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Filters(ReportScope s, string? extra = null) =>
        $"from={D(s.From)};to={D(s.To)};branch={s.BranchId?.ToString() ?? "-"};costCenter={s.CostCenterId?.ToString() ?? "-"};excludeClosing={(s.ExcludeClosing ? "yes" : "no")}" + (extra is null ? "" : ";" + extra);

    private static IReadOnlyList<ReportHeaderLine> Header(ReportScope s, string baseCurrency) =>
    [
        new("الفترة", "Period", $"{D(s.From)} .. {D(s.To)}"),
        new("العملة الأساسية", "Base currency", baseCurrency),
    ];

    public static ReportDocument FromTrialBalance(TrialBalanceView v, ReportScope scope, bool includeZero)
    {
        var rows = new List<ReportRow>(v.Rows.Count + 1);
        foreach (var r in v.Rows)
            rows.Add(new ReportRow(r.IsGroup ? ReportRowKind.GroupHeader : ReportRowKind.Detail, r.Level,
                [r.Code, r.NameAr, r.NameEn, (long)r.Level, r.IsGroup, r.OpeningBalance, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance]));
        rows.Add(new ReportRow(ReportRowKind.Total, 0, ["TOTAL", null, null, null, null, v.TotalOpening, v.TotalDebit, v.TotalCredit, v.TotalClosing]));
        return new ReportDocument
        {
            ReportId = ReportDefinitions.TrialBalance, TitleAr = "ميزان المراجعة", TitleEn = "Trial balance", BaseCurrency = v.BaseCurrency,
            Header = Header(scope, v.BaseCurrency), Columns = ReportDefinitions.TrialBalanceColumns, RowCount = rows.Count,
            Rows = _ => ToAsync(rows), FiltersSummary = Filters(scope, $"includeZero={(includeZero ? "yes" : "no")}"),
        };
    }

    /// <summary>The number of calendar months a range touches: the upper bound of the subtotal rows a monthly grouping can add (so a limit is never under-counted).</summary>
    private static long MonthsIn(ReportScope s) => (s.To.Year - s.From.Year) * 12L + s.To.Month - s.From.Month + 1;

    public static readonly IReadOnlyList<ReportColumn> ComparativeColumns =
    [
        new("cmpOpening", "افتتاحي (المقارنة)", "Opening (comparative)", ReportColumnType.Money, 14, false), new("cmpDebit", "مدين (المقارنة)", "Debit (comparative)", ReportColumnType.Money, 14, false),
        new("cmpCredit", "دائن (المقارنة)", "Credit (comparative)", ReportColumnType.Money, 14, false), new("cmpClosing", "ختامي (المقارنة)", "Closing (comparative)", ReportColumnType.Money),
        new("change", "التغير في الرصيد الختامي", "Change in closing", ReportColumnType.Money),
    ];

    /// <summary>The trial balance (optionally with the comparative period, depth limit and sort already applied) as an export document. Comparative columns exist only when a comparison was resolved.</summary>
    public static ReportDocument FromComparativeTrialBalance(ComparativeTrialBalanceView v, ReportScope scope, bool includeZero)
    {
        var hasCmp = v.Comparative is not null;
        var rows = new List<ReportRow>(v.Rows.Count + 1);
        foreach (var r in v.Rows)
        {
            object?[] cells = [r.Code, r.NameAr, r.NameEn, (long)r.Level, r.IsGroup, r.Opening, r.Debit, r.Credit, r.Closing];
            if (hasCmp) cells = [.. cells, r.CmpOpening, r.CmpDebit, r.CmpCredit, r.CmpClosing, r.Change];
            rows.Add(new ReportRow(r.IsGroup ? ReportRowKind.GroupHeader : ReportRowKind.Detail, r.Level, cells));
        }
        var t = v.Current;
        object?[] total = ["TOTAL", null, null, null, null, t.TotalOpening, t.TotalDebit, t.TotalCredit, t.TotalClosing];
        if (hasCmp) total = [.. total, v.Comparative!.TotalOpening, v.Comparative.TotalDebit, v.Comparative.TotalCredit, v.Comparative.TotalClosing, t.TotalClosing - v.Comparative.TotalClosing];
        rows.Add(new ReportRow(ReportRowKind.Total, 0, total));
        var header = new List<ReportHeaderLine> { new("الفترة", "Period", v.Period.Current.Label + $" ({D(v.Period.Current.From)} .. {D(v.Period.Current.To)})"), new("العملة الأساسية", "Base currency", t.BaseCurrency) };
        if (v.Period.Comparative is { } c) header.Add(new ReportHeaderLine("فترة المقارنة", "Comparative period", c.Label + $" ({D(c.From)} .. {D(c.To)})"));
        else if (v.Period.ComparativeUnavailable is { } why) header.Add(new ReportHeaderLine("فترة المقارنة", "Comparative period", "n/a: " + why));
        return new ReportDocument
        {
            ReportId = ReportDefinitions.TrialBalance, TitleAr = "ميزان المراجعة", TitleEn = "Trial balance", BaseCurrency = t.BaseCurrency, Header = header,
            Columns = hasCmp ? [.. ReportDefinitions.TrialBalanceColumns, .. ComparativeColumns] : ReportDefinitions.TrialBalanceColumns, RowCount = rows.Count,
            Rows = _ => ToAsync(rows),
            FiltersSummary = Filters(scope, $"includeZero={(includeZero ? "yes" : "no")};comparison={(hasCmp ? $"{D(v.Period.Comparative!.From)}..{D(v.Period.Comparative.To)}" : "none")};maxLevel={v.MaxLevel?.ToString() ?? "-"};sort={v.Sort}"),
        };
    }

    private static async IAsyncEnumerable<ReportRow> ToAsync(IReadOnlyList<ReportRow> rows)
    {
        foreach (var r in rows) yield return r;
        await Task.CompletedTask;
    }

    /// <summary>The general ledger of one account: opening balance row, every line (streamed), closing balance row. The document owns the open snapshot and releases it when disposed.</summary>
    public static ReportDocument FromGeneralLedger(GeneralLedgerExport export, ReportScope scope, bool groupByMonth = false)
    {
        var h = export.Header;
        async IAsyncEnumerable<ReportRow> Rows([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ReportRow(ReportRowKind.Subtotal, 0, [null, null, null, "Opening balance", null, null, null, null, null, null, null, h.OpeningBalance]);
            (int Year, int Month)? month = null; decimal md = 0, mc = 0, running = h.OpeningBalance;
            ReportRow Sub() => new(ReportRowKind.Subtotal, 0, [null, null, null, $"Subtotal {month!.Value.Year:0000}-{month.Value.Month:00}", null, null, null, null, null, md, mc, running]);
            await foreach (var l in export.Lines(ct))
            {
                if (groupByMonth)
                {
                    var m = (l.EntryDate.Year, l.EntryDate.Month);
                    if (month is not null && month != m) { yield return Sub(); md = 0; mc = 0; }
                    month = m; md += l.BaseDebit; mc += l.BaseCredit;
                }
                running = l.RunningBalance;
                yield return new ReportRow(ReportRowKind.Detail, 0, [l.EntryDate, l.Number, l.EntryType, l.EntryDescription, l.LineDescription, l.Currency, l.Debit, l.Credit, l.Rate, l.BaseDebit, l.BaseCredit, l.RunningBalance]);
            }
            if (groupByMonth && month is not null) yield return Sub();
            yield return new ReportRow(ReportRowKind.Total, 0, [null, null, null, "Closing balance", null, null, null, null, null, h.TotalDebit, h.TotalCredit, h.ClosingBalance]);
        }
        return new ReportDocument
        {
            ReportId = ReportDefinitions.GeneralLedger, TitleAr = "دفتر الأستاذ", TitleEn = "General ledger", BaseCurrency = h.BaseCurrency,
            Header = [.. Header(scope, h.BaseCurrency), new ReportHeaderLine("الحساب", "Account", h.AccountId.ToString())],
            Columns = ReportDefinitions.GeneralLedgerColumns, RowCount = (long)h.TotalLines + 2 + (groupByMonth ? MonthsIn(scope) : 0), Rows = Rows,
            FiltersSummary = Filters(scope, $"account={h.AccountId}"), OnDispose = export.DisposeAsync,
        };
    }

    /// <summary>A general-ledger document from an in-memory (paged) view: used where the lines are already loaded (tests, small previews). Exports use <see cref="FromGeneralLedger"/>, which streams.</summary>
    public static ReportDocument FromGeneralLedgerView(GeneralLedgerView v)
    {
        var rows = new List<ReportRow> { new(ReportRowKind.Subtotal, 0, [null, null, null, "Opening balance", null, null, null, null, null, null, null, v.OpeningBalance]) };
        foreach (var l in v.Lines) rows.Add(new ReportRow(ReportRowKind.Detail, 0, [l.EntryDate, l.Number, l.EntryType, l.EntryDescription, l.LineDescription, l.Currency, l.Debit, l.Credit, l.Rate, l.BaseDebit, l.BaseCredit, l.RunningBalance]));
        rows.Add(new ReportRow(ReportRowKind.Total, 0, [null, null, null, "Closing balance", null, null, null, null, null, v.TotalDebit, v.TotalCredit, v.ClosingBalance]));
        return new ReportDocument
        {
            ReportId = ReportDefinitions.GeneralLedger, TitleAr = "دفتر الأستاذ", TitleEn = "General ledger", BaseCurrency = v.BaseCurrency, Columns = ReportDefinitions.GeneralLedgerColumns,
            RowCount = rows.Count, Rows = _ => ToAsync(rows), FiltersSummary = $"from={D(v.From)};to={D(v.To)};account={v.AccountId}",
        };
    }

    /// <summary>CSV of every column of an in-memory document, through the shared renderer (the same code path as an export).</summary>
    public static byte[] ToCsv(ReportDocument doc)
    {
        using var ms = new MemoryStream();
        new CsvReportRenderer().RenderAsync(doc, [.. Enumerable.Range(0, doc.Columns.Count)], ms, default).GetAwaiter().GetResult();
        return ms.ToArray();
    }
}
