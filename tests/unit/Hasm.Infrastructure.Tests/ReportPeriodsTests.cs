using Microsoft.Extensions.Configuration;
using System.Text;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Reporting;

namespace Hasm.Infrastructure.Tests;

/// <summary>Fiscal period resolution with precise comparison rules (company fiscal calendars, including non-calendar years), the report options and the CSV renderer.</summary>
public class ReportPeriodsTests
{
    private static FiscalYearView Year(string name, DateOnly start, FiscalPeriodType type = FiscalPeriodType.Monthly)
    {
        var id = Guid.NewGuid(); var months = type == FiscalPeriodType.Monthly ? 1 : 3; var n = 12 / months;
        var periods = Enumerable.Range(0, n).Select(i =>
        {
            var s = start.AddMonths(i * months);
            return new FiscalPeriodView(Guid.NewGuid(), id, i + 1, s, s.AddMonths(months).AddDays(-1), FiscalPeriodState.Open, null, 1);
        }).ToList();
        return new FiscalYearView(id, name, start, start.AddYears(1).AddDays(-1), type, "UTC", false, null, 1, DateTimeOffset.UtcNow, periods);
    }

    // a company whose fiscal year runs April-March: FY2024 = 2024-04-01..2025-03-31, FY2025 = 2025-04-01..2026-03-31
    private static readonly FiscalYearView Fy24 = Year("FY2024", new DateOnly(2024, 4, 1));
    private static readonly FiscalYearView Fy25 = Year("FY2025", new DateOnly(2025, 4, 1));
    private static readonly FiscalYearView Fy26Q = Year("FY2026", new DateOnly(2026, 4, 1), FiscalPeriodType.Quarterly);
    private static readonly IReadOnlyList<FiscalYearView> Years = [Fy24, Fy25, Fy26Q];

    private static ResolvedPeriod Ok(ReportPeriodRequest r) { var res = ReportPeriods.Resolve(Years, r, 100); Assert.True(res.IsOk, $"{res.Code}"); return res.Value!; }

    [Fact]
    public void A_fiscal_year_resolves_to_its_own_dates_never_to_january_december_and_its_comparison_is_the_year_that_ends_the_day_before()
    {
        var r = Ok(new ReportPeriodRequest(Fy25.Id, null, null, null, ComparisonMode.PreviousYear));
        Assert.Equal((new DateOnly(2025, 4, 1), new DateOnly(2026, 3, 31)), (r.Current.From, r.Current.To));
        Assert.Equal((new DateOnly(2024, 4, 1), new DateOnly(2025, 3, 31), Fy24.Id), (r.Comparative!.From, r.Comparative.To, r.Comparative.YearId!.Value));
        Assert.Null(r.ComparativeUnavailable);
        var prevPeriod = Ok(new ReportPeriodRequest(Fy25.Id, null, null, null, ComparisonMode.PreviousPeriod));
        Assert.Equal(r.Comparative, prevPeriod.Comparative);                                                  // for a whole year the previous period is the previous year
    }

    [Fact]
    public void A_comparison_without_a_counterpart_is_unavailable_with_a_reason_and_nothing_is_invented()
    {
        var first = Ok(new ReportPeriodRequest(Fy24.Id, null, null, null, ComparisonMode.PreviousYear));
        Assert.Null(first.Comparative);
        Assert.Equal("no_previous_year", first.ComparativeUnavailable);
        Assert.Equal("no_previous_period", Ok(new ReportPeriodRequest(Fy24.Id, null, null, null, ComparisonMode.PreviousPeriod)).ComparativeUnavailable);
        var firstPeriod = Ok(new ReportPeriodRequest(null, Fy24.Periods[0].Id, null, null, ComparisonMode.PreviousPeriod));
        Assert.Equal("no_previous_period", firstPeriod.ComparativeUnavailable);
        Assert.Equal("no_previous_year", Ok(new ReportPeriodRequest(null, Fy24.Periods[5].Id, null, null, ComparisonMode.PreviousYear)).ComparativeUnavailable);
    }

    [Fact]
    public void The_previous_period_of_a_period_is_the_one_before_it_even_across_a_fiscal_year_boundary()
    {
        var july = Fy25.Periods[3];                                                                           // 2025-07
        var r = Ok(new ReportPeriodRequest(null, july.Id, null, null, ComparisonMode.PreviousPeriod));
        Assert.Equal((new DateOnly(2025, 7, 1), new DateOnly(2025, 7, 31)), (r.Current.From, r.Current.To));
        Assert.Equal((new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30)), (r.Comparative!.From, r.Comparative.To));
        var april = Fy25.Periods[0];                                                                          // first period of FY2025: its predecessor is the LAST period of FY2024
        var across = Ok(new ReportPeriodRequest(null, april.Id, null, null, ComparisonMode.PreviousPeriod));
        Assert.Equal((new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), Fy24.Id, 12), (across.Comparative!.From, across.Comparative.To, across.Comparative.YearId!.Value, Fy24.Periods.Single(p => p.Id == across.Comparative.PeriodId).Number));
    }

    [Fact]
    public void The_previous_year_of_a_period_is_the_period_with_the_same_number_in_the_previous_fiscal_year()
    {
        var august = Fy25.Periods[4];                                                                         // period 5 of FY2025
        var r = Ok(new ReportPeriodRequest(null, august.Id, null, null, ComparisonMode.PreviousYear));
        Assert.Equal((new DateOnly(2024, 8, 1), new DateOnly(2024, 8, 31)), (r.Comparative!.From, r.Comparative.To));
        // quarterly year: the previous quarter crosses into the previous (monthly) year, the previous YEAR has no quarter 1 because FY2025 is monthly
        var q = Fy26Q.Periods[1];                                                                             // quarter 2 of FY2026 = 2026-07..2026-09
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)), (q.StartDate, q.EndDate));
        var prevQuarter = Ok(new ReportPeriodRequest(null, q.Id, null, null, ComparisonMode.PreviousPeriod));
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)), (prevQuarter.Comparative!.From, prevQuarter.Comparative.To));
        var sameQuarterLastYear = Ok(new ReportPeriodRequest(null, q.Id, null, null, ComparisonMode.PreviousYear));
        Assert.Null(sameQuarterLastYear.Comparative);                                                         // the previous year is divided into months: a quarter is never compared with a month
        Assert.Equal("period_division_differs", sameQuarterLastYear.ComparativeUnavailable);
    }

    [Fact]
    public void A_partial_custom_range_is_compared_with_the_same_range_one_year_earlier_with_month_ends_preserved()
    {
        var r = Ok(new ReportPeriodRequest(null, null, new DateOnly(2026, 4, 10), new DateOnly(2026, 5, 20), ComparisonMode.PreviousYear));
        Assert.Equal((new DateOnly(2025, 4, 10), new DateOnly(2025, 5, 20)), (r.Comparative!.From, r.Comparative.To));
        var leap = Ok(new ReportPeriodRequest(null, null, new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29), ComparisonMode.PreviousYear));      // a full February of a leap year
        Assert.Equal((new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28)), (leap.Comparative!.From, leap.Comparative.To));
        var clamp = Ok(new ReportPeriodRequest(null, null, new DateOnly(2028, 2, 29), new DateOnly(2028, 3, 15), ComparisonMode.PreviousYear));    // 29 February has no counterpart: clamped
        Assert.Equal((new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 15)), (clamp.Comparative!.From, clamp.Comparative.To));
        var endOfMarch = Ok(new ReportPeriodRequest(null, null, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), ComparisonMode.PreviousYear));
        Assert.Equal(new DateOnly(2025, 3, 31), endOfMarch.Comparative!.To);
    }

    [Fact]
    public void The_previous_period_of_a_custom_range_is_the_range_of_the_same_length_ending_the_day_before()
    {
        var r = Ok(new ReportPeriodRequest(null, null, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 19), ComparisonMode.PreviousPeriod));           // 10 days
        Assert.Equal((new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 9)), (r.Comparative!.From, r.Comparative.To));
        Assert.Equal(10, r.Comparative.To.DayNumber - r.Comparative.From.DayNumber + 1);
    }

    [Fact]
    public void Exactly_one_selector_is_required_and_unknown_ids_and_bad_ranges_are_refused()
    {
        Assert.Equal("validation_failed", ReportPeriods.Resolve(Years, new ReportPeriodRequest(null, null, null, null), 100).Code);
        Assert.Equal("validation_failed", ReportPeriods.Resolve(Years, new ReportPeriodRequest(Fy24.Id, Fy24.Periods[0].Id, null, null), 100).Code);
        Assert.Equal("validation_failed", ReportPeriods.Resolve(Years, new ReportPeriodRequest(Fy24.Id, null, new DateOnly(2025, 1, 1), new DateOnly(2025, 2, 1)), 100).Code);
        Assert.Equal("fiscal_year_not_found", ReportPeriods.Resolve(Years, new ReportPeriodRequest(Guid.NewGuid(), null, null, null), 100).Code);
        Assert.Equal("fiscal_period_not_found", ReportPeriods.Resolve(Years, new ReportPeriodRequest(null, Guid.NewGuid(), null, null), 100).Code);
        Assert.Equal("validation_failed", ReportPeriods.Resolve(Years, new ReportPeriodRequest(null, null, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)), 100).Code);
        Assert.Equal("validation_failed", ReportPeriods.Resolve(Years, new ReportPeriodRequest(null, null, new DateOnly(2026, 1, 1), null), 100).Code);
    }

    [Fact]
    public void There_is_no_fixed_five_year_limit_only_a_configurable_sanity_bound()
    {
        var ten = ReportPeriods.Resolve(Years, new ReportPeriodRequest(null, null, new DateOnly(2010, 1, 1), new DateOnly(2026, 1, 1)), 100);
        Assert.True(ten.IsOk);                                                                                // 16 years: fine
        var tooLong = ReportPeriods.Resolve(Years, new ReportPeriodRequest(null, null, new DateOnly(2010, 1, 1), new DateOnly(2026, 1, 1)), 5);
        Assert.False(tooLong.IsOk);
        Assert.Equal(100, new ReportOptions().MaxRangeYears);
    }

    [Fact]
    public void Report_options_read_configuration_and_fall_back_to_the_defaults_for_invalid_values()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Hasm:Reports:MaxExportRows"] = "1234", ["Hasm:Reports:MaxPdfRows"] = "-5", ["Hasm:Reports:MaxRangeYears"] = "abc", ["Hasm:Reports:TempDirectory"] = "  /tmp/x  " }).Build();
        var o = ReportOptions.FromConfiguration(cfg);
        Assert.Equal((1234, 50_000, 100, "/tmp/x"), (o.MaxExportRows, o.MaxPdfRows, o.MaxRangeYears, o.TempDirectory));
    }

    private static ReportDocument Doc(params ReportRow[] rows) => new()
    {
        ReportId = "t", TitleAr = "ت", TitleEn = "T", Columns = [new("code", "ر", "Code", ReportColumnType.Code), new("name", "ا", "Name", ReportColumnType.Text), new("amt", "م", "Amount", ReportColumnType.Money), new("d", "ت", "Date", ReportColumnType.Date), new("rate", "س", "Rate", ReportColumnType.Rate), new("ok", "ص", "Ok", ReportColumnType.Boolean)],
        Rows = _ => Stream(rows), RowCount = rows.Length,
    };
    private static async IAsyncEnumerable<ReportRow> Stream(ReportRow[] rows) { foreach (var r in rows) yield return r; await Task.CompletedTask; }

    [Fact]
    public async Task The_csv_renderer_keeps_codes_exact_formats_numbers_and_dates_invariantly_and_neutralises_formulas()
    {
        var doc = Doc(
            new ReportRow(ReportRowKind.Detail, 0, ["0101", "=SUM(A1)", 1234.5m, new DateOnly(2026, 3, 9), 0.71m, true]),
            new ReportRow(ReportRowKind.Detail, 0, ["+1", "@cmd, \"q\"", -0.5m, null, null, false]),
            new ReportRow(ReportRowKind.Total, 0, ["TOTAL", "خصوم", 0m, null, null, null]));
        using var ms = new MemoryStream();
        await new CsvReportRenderer().RenderAsync(doc, [0, 1, 2, 3, 4, 5], ms, default);
        var bytes = ms.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Code,Name,Amount,Date,Rate,Ok", lines[0]);
        Assert.Equal("0101,\"'=SUM(A1)\",1234.5000,2026-03-09,0.7100000000,yes", lines[1]);                  // leading zeros kept, formula neutralised
        Assert.Equal("\"'+1\",\"'@cmd, \"\"q\"\"\",-0.5000,,,no", lines[2]);
        Assert.Equal("TOTAL,خصوم,0.0000,,,", lines[3]);
        using var narrow = new MemoryStream();
        await new CsvReportRenderer().RenderAsync(doc, [2, 0], narrow, default);
        Assert.StartsWith("Amount,Code", Encoding.UTF8.GetString(narrow.ToArray(), 3, 11));
    }
}
