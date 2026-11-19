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
        foreach (var date in plan.Lines.Select(l => l.Date).Distinct().Order())
        {
            var matches = periods.Where(p => p.Start <= date && date <= p.End).ToArray();
            if (matches.Length > 1) throw new CostingException("compensation_inconsistent");   // overlapping periods
            var period = matches.SingleOrDefault();
            var gate = FiscalPeriodGate.Evaluate(period?.Id, period?.State, allowSoftClosed: false);
            if (!gate.Allowed) throw new CostingException(gate.Reason!);
            if (period!.YearClosed) throw new CostingException("compensation_inconsistent");
            if (plan.Lines.Any(l => l.Date == date && l.PeriodId != period.Id)) throw new CostingException("compensation_inconsistent");
        }
    }
}
