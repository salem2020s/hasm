using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Reporting;

public enum ComparisonMode { None = 0, PreviousPeriod = 1, PreviousYear = 2 }

/// <summary>What the caller selected: a fiscal period, a fiscal year, or a custom date range (exactly one of the three), plus the comparison.</summary>
public sealed record ReportPeriodRequest(Guid? FiscalYearId, Guid? FiscalPeriodId, DateOnly? From, DateOnly? To, ComparisonMode Comparison = ComparisonMode.None);

public sealed record ResolvedRange(DateOnly From, DateOnly To, Guid? YearId, Guid? PeriodId, string Label);

/// <summary>The resolved current range and, when asked, the comparative range. A comparison that has no counterpart (no previous year exists yet) is reported as unavailable with a reason, never invented.</summary>
public sealed record ResolvedPeriod(ResolvedRange Current, ResolvedRange? Comparative, string? ComparativeUnavailable);

/// <summary>
/// Fiscal-year and period resolution with PRECISE comparison rules (the company's own fiscal calendar, never January-December): a fiscal year is exactly 12 months starting on the first of any month.
/// <list type="bullet">
/// <item><b>Previous period</b>: for a fiscal period, the period that ends the day before it starts (it can belong to the previous fiscal year); for a fiscal year, the fiscal year that ends the day before it starts;
///   for a custom range, the range of the same number of days that ends the day before the range starts.</item>
/// <item><b>Previous year</b>: for a fiscal year, the fiscal year that ends the day before it starts; for a fiscal period, the period with the same number in that previous fiscal year (unavailable when it has none or when that year is divided differently, for example quarters against months);
///   for a custom range, the same range moved back exactly 12 months (a range ending on the last day of a month ends on the last day of the same month a year earlier; a start day missing in the target month is clamped).</item>
/// <item>A comparison whose counterpart does not exist is unavailable, with a reason: nothing is estimated.</item>
/// </list>
/// </summary>
public static class ReportPeriods
{
    public static ConfigResult<ResolvedPeriod> Resolve(IReadOnlyList<FiscalYearView> years, ReportPeriodRequest r, int maxRangeYears)
    {
        var selectors = (r.FiscalPeriodId is not null ? 1 : 0) + (r.FiscalYearId is not null ? 1 : 0) + (r.From is not null || r.To is not null ? 1 : 0);
        if (selectors != 1) return ConfigResult<ResolvedPeriod>.Invalid([new("period", "choose_one_period_selector")]);

        FiscalYearView? year = null; FiscalPeriodView? period = null; ResolvedRange current;
        if (r.FiscalPeriodId is { } pid)
        {
            year = years.FirstOrDefault(y => y.Periods.Any(p => p.Id == pid));
            period = year?.Periods.First(p => p.Id == pid);
            if (period is null || year is null) return ConfigResult<ResolvedPeriod>.Fail(ConfigStatus.NotFound, "fiscal_period_not_found");
            current = new ResolvedRange(period.StartDate, period.EndDate, year.Id, period.Id, $"{year.Name} / {period.Number}");
        }
        else if (r.FiscalYearId is { } yid)
        {
            year = years.FirstOrDefault(y => y.Id == yid);
            if (year is null) return ConfigResult<ResolvedPeriod>.Fail(ConfigStatus.NotFound, "fiscal_year_not_found");
            current = new ResolvedRange(year.StartDate, year.EndDate, year.Id, null, year.Name);
        }
        else
        {
            if (r.From is null || r.To is null || r.To < r.From || r.To.Value.DayNumber - r.From.Value.DayNumber > 366 * Math.Max(1, maxRangeYears)) return ConfigResult<ResolvedPeriod>.Invalid([new("to", "invalid_range")]);
            current = new ResolvedRange(r.From.Value, r.To.Value, null, null, $"{r.From:yyyy-MM-dd} .. {r.To:yyyy-MM-dd}");
        }

        if (r.Comparison == ComparisonMode.None) return ConfigResult<ResolvedPeriod>.Ok(new ResolvedPeriod(current, null, null));

        FiscalYearView? Preceding(FiscalYearView y) => years.FirstOrDefault(o => o.EndDate.AddDays(1) == y.StartDate);
        ResolvedRange? comparative = null; string? why = null;
        if (period is not null && year is not null)
        {
            if (r.Comparison == ComparisonMode.PreviousPeriod)
            {
                var prev = years.SelectMany(y => y.Periods.Select(p => (Year: y, Period: p))).FirstOrDefault(x => x.Period.EndDate.AddDays(1) == period.StartDate);
                if (prev.Period is null) why = "no_previous_period"; else comparative = new ResolvedRange(prev.Period.StartDate, prev.Period.EndDate, prev.Year.Id, prev.Period.Id, $"{prev.Year.Name} / {prev.Period.Number}");
            }
            else
            {
                var prevYear = Preceding(year);
                var same = prevYear is not null && prevYear.PeriodType == year.PeriodType ? prevYear.Periods.FirstOrDefault(p => p.Number == period.Number) : null;
                if (prevYear is null) why = "no_previous_year";
                else if (prevYear.PeriodType != year.PeriodType) why = "period_division_differs";     // a quarter is never compared with a month
                else if (same is null) why = "no_previous_year"; else comparative = new ResolvedRange(same.StartDate, same.EndDate, prevYear.Id, same.Id, $"{prevYear.Name} / {same.Number}");
            }
        }
        else if (year is not null)
        {
            var prevYear = Preceding(year);       // previous period of a whole year, and previous year of a whole year, are the same thing
            if (prevYear is null) why = r.Comparison == ComparisonMode.PreviousPeriod ? "no_previous_period" : "no_previous_year";
            else comparative = new ResolvedRange(prevYear.StartDate, prevYear.EndDate, prevYear.Id, null, prevYear.Name);
        }
        else if (r.Comparison == ComparisonMode.PreviousPeriod)
        {
            var days = current.To.DayNumber - current.From.DayNumber + 1;
            var end = current.From.AddDays(-1); var start = end.AddDays(-(days - 1));
            comparative = new ResolvedRange(start, end, null, null, $"{start:yyyy-MM-dd} .. {end:yyyy-MM-dd}");
        }
        else
        {
            var start = ShiftBackOneYear(current.From, false); var end = ShiftBackOneYear(current.To, DateTime.DaysInMonth(current.To.Year, current.To.Month) == current.To.Day);
            comparative = new ResolvedRange(start, end, null, null, $"{start:yyyy-MM-dd} .. {end:yyyy-MM-dd}");
        }
        return ConfigResult<ResolvedPeriod>.Ok(new ResolvedPeriod(current, comparative, why));
    }

    /// <summary>The same date exactly 12 months earlier; the last day of the month stays the last day of the month, and a day that does not exist in the target month is clamped to its last day.</summary>
    public static DateOnly ShiftBackOneYear(DateOnly d, bool lastDayOfMonth)
    {
        var y = d.Year - 1;
        var last = DateTime.DaysInMonth(y, d.Month);
        return new DateOnly(y, d.Month, lastDayOfMonth ? last : Math.Min(d.Day, last));
    }
}
