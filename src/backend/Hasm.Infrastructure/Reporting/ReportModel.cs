using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Reporting;

/// <summary>
/// Reporting limits (section <c>Hasm:Reports</c>, environment <c>Hasm__Reports__*</c>). Every value is a server-enforced, documented, configurable default; an invalid or non-positive value falls back to
/// the default rather than disabling a limit. No limit ever truncates silently: an export above a limit is refused before any file is produced.
/// </summary>
public sealed class ReportOptions
{
    public const string SectionName = "Hasm:Reports";

    /// <summary>Maximum rows of one CSV or XLSX export (Excel itself allows 1,048,576 rows per sheet).</summary>
    public int MaxExportRows { get; init; } = 500_000;
    /// <summary>Maximum rows of one PDF export (a PDF is a presentation document; a separate, lower limit applies).</summary>
    public int MaxPdfRows { get; init; } = 50_000;
    /// <summary>Longest date range a report may cover, in years. A sanity bound for date arithmetic, not a business limit.</summary>
    public int MaxRangeYears { get; init; } = 100;
    /// <summary>Directory for the short-lived temporary export files (deleted when the response ends). Default: the system temp directory.</summary>
    public string? TempDirectory { get; init; }

    public static ReportOptions FromConfiguration(IConfiguration configuration)
    {
        var d = new ReportOptions();
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        int I(string key, int fallback) => int.TryParse(Get(key), out var v) && v > 0 ? v : fallback;
        return new ReportOptions { MaxExportRows = I("MaxExportRows", d.MaxExportRows), MaxPdfRows = I("MaxPdfRows", d.MaxPdfRows), MaxRangeYears = I("MaxRangeYears", d.MaxRangeYears), TempDirectory = Get("TempDirectory") };
    }
}

/// <summary>How a column's values are typed (and formatted by each renderer). <c>Code</c> values are text that must keep leading zeros; <c>Percent</c> holds percentage points (12.5 = 12.5 %); <c>Rate</c> is an exchange rate with ten decimals.</summary>
public enum ReportColumnType { Text = 1, Code = 2, Date = 3, Money = 4, Integer = 5, Percent = 6, Boolean = 7, Rate = 8 }

/// <summary>One column of a report definition. The key is stable (clients and the future designer refer to it); titles are bilingual.</summary>
public sealed record ReportColumn(string Key, string TitleAr, string TitleEn, ReportColumnType Type, int Width = 14, bool Default = true);

public enum ReportRowKind { Detail = 1, GroupHeader = 2, Subtotal = 3, Total = 4, Notice = 5 }

/// <summary>A row; <c>Cells</c> align with the document's FULL column list (renderers pick the selected ones). Values: string, decimal, long/int, DateOnly, bool or null.</summary>
public sealed record ReportRow(ReportRowKind Kind, int Level, IReadOnlyList<object?> Cells);

/// <summary>A label/value pair shown above the table (period, filters, mapping version...). Values are data (dates, ids, numbers), never free user text beyond account names.</summary>
public sealed record ReportHeaderLine(string LabelAr, string LabelEn, string Value);

/// <summary>A notice printed with the report (for example an incomplete classification). Both texts are fixed product texts.</summary>
public sealed record ReportNotice(string Code, string TextAr, string TextEn);

/// <summary>
/// The neutral result every report produces and every renderer (CSV, XLSX, PDF) consumes. <see cref="Rows"/> is a stream, so a large report never has to be held in memory; <see cref="RowCount"/> is known
/// BEFORE rendering so a limit can refuse an oversized export instead of producing an incomplete file. The document represents the AUTHORIZED query result, never just the page on screen.
/// </summary>
public sealed class ReportDocument : IAsyncDisposable
{
    public required string ReportId { get; init; }
    public required string TitleAr { get; init; }
    public required string TitleEn { get; init; }
    public string? CompanyName { get; set; }
    public string BaseCurrency { get; init; } = "";
    public IReadOnlyList<ReportHeaderLine> Header { get; init; } = [];
    public required IReadOnlyList<ReportColumn> Columns { get; init; }
    public required Func<CancellationToken, IAsyncEnumerable<ReportRow>> Rows { get; init; }
    public required long RowCount { get; init; }
    public IReadOnlyList<ReportNotice> Notices { get; init; } = [];
    /// <summary>A compact, non-sensitive description of the filters for the audit record (dates, ids, flags).</summary>
    public string FiltersSummary { get; init; } = "";
    /// <summary>Releases the resources (database snapshot) behind <see cref="Rows"/>.</summary>
    public Func<ValueTask>? OnDispose { get; init; }
    public async ValueTask DisposeAsync() { if (OnDispose is not null) await OnDispose(); }
}

/// <summary>A report parameter, as the report-definition metadata describes it (the future designer and the shared UI build their controls from it).</summary>
public sealed record ReportParameter(string Name, string Kind, bool Required, string? TitleAr = null, string? TitleEn = null);

/// <summary>
/// Stable report-definition metadata (identifier, titles, permissions, parameters, columns, supported formats). The visual Report Designer (a mandatory future Release 1 deliverable, NOT part of this phase)
/// will build on these definitions; the fixed reports are produced from the same definitions, so nothing is duplicated.
/// </summary>
public sealed record ReportDefinition(
    string Id, string TitleAr, string TitleEn, string ViewPermission, string ExportPermission, IReadOnlyList<ReportParameter> Parameters, IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string> Formats, bool SupportsComparison, bool SupportsGrouping);

/// <summary>The registry of report definitions. Statements are added by the statements slice.</summary>
public static class ReportDefinitions
{
    public const string TrialBalance = "trial-balance";
    public const string GeneralLedger = "general-ledger";
    public const string ProfitAndLoss = "profit-and-loss";
    public const string BalanceSheet = "balance-sheet";
    public const string TradingAccount = "trading-account";

    private static readonly string[] Formats = ["csv", "xlsx", "pdf"];

    public static readonly IReadOnlyList<ReportColumn> TrialBalanceColumns =
    [
        new("code", "رمز الحساب", "Code", ReportColumnType.Code, 14), new("nameAr", "الاسم بالعربية", "Name (ar)", ReportColumnType.Text, 30), new("nameEn", "الاسم بالإنجليزية", "Name (en)", ReportColumnType.Text, 30),
        new("level", "المستوى", "Level", ReportColumnType.Integer, 8, false), new("isGroup", "حساب رئيسي", "Group", ReportColumnType.Boolean, 8, false),
        new("opening", "الرصيد الافتتاحي", "Opening", ReportColumnType.Money), new("debit", "مدين", "Debit", ReportColumnType.Money), new("credit", "دائن", "Credit", ReportColumnType.Money),
        new("closing", "الرصيد الختامي", "Closing", ReportColumnType.Money),
    ];

    public static readonly IReadOnlyList<ReportColumn> GeneralLedgerColumns =
    [
        new("date", "التاريخ", "Date", ReportColumnType.Date, 12), new("number", "رقم القيد", "Number", ReportColumnType.Code, 14), new("type", "النوع", "Type", ReportColumnType.Text, 12),
        new("description", "البيان", "Description", ReportColumnType.Text, 34), new("lineDescription", "بيان السطر", "Line description", ReportColumnType.Text, 30), new("currency", "العملة", "Currency", ReportColumnType.Text, 9),
        new("debit", "مدين", "Debit", ReportColumnType.Money), new("credit", "دائن", "Credit", ReportColumnType.Money), new("rate", "السعر", "Rate", ReportColumnType.Rate, 14, false),
        new("baseDebit", "مدين بالأساس", "Base debit", ReportColumnType.Money), new("baseCredit", "دائن بالأساس", "Base credit", ReportColumnType.Money), new("runningBalance", "الرصيد الجاري", "Running balance", ReportColumnType.Money),
    ];

    private static readonly IReadOnlyList<ReportParameter> PeriodParameters =
    [
        new("fiscalYearId", "fiscal_year", false, "السنة المالية", "Fiscal year"), new("fiscalPeriodId", "fiscal_period", false, "الفترة المالية", "Fiscal period"),
        new("from", "date", false, "من تاريخ", "From"), new("to", "date", false, "إلى تاريخ", "To"), new("comparison", "comparison", false, "المقارنة", "Comparison"),
        new("branchId", "branch", false, "الفرع", "Branch"), new("costCenterId", "cost_center", false, "مركز التكلفة", "Cost center"),
    ];

    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(TrialBalance, "ميزان المراجعة", "Trial balance", "company.ledgerreports.view", "company.ledgerreports.export",
            [.. PeriodParameters, new("includeZero", "bool", false, "إظهار الأرصدة الصفرية", "Include zero rows"), new("excludeClosing", "bool", false, "استبعاد قيود الإقفال", "Exclude closing entries")], TrialBalanceColumns, Formats, true, true),
        new(GeneralLedger, "دفتر الأستاذ", "General ledger", "company.ledgerreports.view", "company.ledgerreports.export",
            [new("accountId", "account", true, "الحساب", "Account"), .. PeriodParameters.Where(p => p.Name != "comparison"), new("excludeClosing", "bool", false, "استبعاد قيود الإقفال", "Exclude closing entries")], GeneralLedgerColumns, Formats, false, false),
    ];

    public static ReportDefinition? Find(string? id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}
