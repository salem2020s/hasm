using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Costing;

/// <summary>
/// Pure check of every compensation date against the fiscal periods (TM-D8 baseline: one entry per affected period, dated at the original operation; the
/// stock side applies NO Soft Closed override, so only Open periods pass; Final Closed and missing periods refuse the WHOLE plan, never a fallback to another
/// date). The line's recorded period (from the original posted journal) must be the period that contains its date. No database access.
/// </summary>
internal static class CompensationDateGuard
{
    internal static void Validate(CompensationPlan plan, IReadOnlyList<CostFiscalPeriodEvidence> periods)
    {
        if (plan is null || periods is null) throw new CostingException("invalid_compensation_input");
        ValidateDates(plan.Lines.Select(l => l.Date), periods);
        foreach (var line in plan.Lines)
        {
            var period = periods.Single(p => p.Start <= line.Date && line.Date <= p.End);
            if (line.PeriodId != period.Id) throw new CostingException("compensation_inconsistent");
        }
    }

    /// <summary>Every date (compensation dates, the proposed event's date and every changed history date) must lie in exactly one OPEN period of an open year.</summary>
    internal static void ValidateDates(IEnumerable<DateOnly> dates, IReadOnlyList<CostFiscalPeriodEvidence> periods)
    {
        if (dates is null || periods is null) throw new CostingException("invalid_compensation_input");
        foreach (var date in dates.Distinct().Order())
        {
            var matches = periods.Where(p => p.Start <= date && date <= p.End).ToArray();
            if (matches.Length > 1) throw new CostingException("compensation_inconsistent");   // overlapping periods
            var period = matches.SingleOrDefault();
            var gate = FiscalPeriodGate.Evaluate(period?.Id, period?.State, allowSoftClosed: false);
            if (!gate.Allowed) throw new CostingException(gate.Reason!);
            if (period!.YearClosed) throw new CostingException("compensation_inconsistent");
        }
    }
}
