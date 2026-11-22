using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>TM-D8 baseline: compensation dates are checked per affected period; only Open passes; nothing is silently moved to another date.</summary>
public class CompensationDateGuardTests
{
    private static readonly Guid Account = Guid.NewGuid(), Branch = Guid.NewGuid(), Year = Guid.NewGuid(), March = Guid.NewGuid(), April = Guid.NewGuid();
    private static readonly DateOnly D1 = new(2026, 3, 12), D2 = new(2026, 4, 12);
    private static CostFiscalPeriodEvidence Period(Guid id, DateOnly start, DateOnly end, FiscalPeriodState state = FiscalPeriodState.Open, bool yearClosed = false) =>
        new(id, Year, start, end, state, 1, yearClosed, 1);
    private static CompensationPlan Plan(params (DateOnly Date, Guid? Period)[] lines) =>
        new([.. lines.SelectMany(l => new[] { new CompensationLine(Guid.NewGuid(), l.Date, l.Period, Branch, Account, 5m, 0m), new CompensationLine(Guid.NewGuid(), l.Date, l.Period, Branch, Account, 0m, 5m) })], [], "fp");
    private static readonly CostFiscalPeriodEvidence Mar = Period(March, new(2026, 3, 1), new(2026, 3, 31)), Apr = Period(April, new(2026, 4, 1), new(2026, 4, 30));
    private static string Refusal(CompensationPlan plan, params CostFiscalPeriodEvidence[] periods) =>
        Assert.Throws<CostingException>(() => CompensationDateGuard.Validate(plan, periods)).Code;

    [Fact] public void Open_periods_containing_every_date_pass() => CompensationDateGuard.Validate(Plan((D1, March), (D2, April)), [Mar, Apr]);
    [Fact] public void An_empty_plan_needs_no_period() => CompensationDateGuard.Validate(Plan(), []);
    [Fact] public void A_final_closed_period_refuses_the_whole_plan() => Assert.Equal(PostingPeriodCheck.FinalClosed, Refusal(Plan((D1, March), (D2, April)), Mar with { State = FiscalPeriodState.FinalClosed }, Apr));
    [Fact] public void A_soft_closed_period_is_refused_because_the_stock_side_has_no_override() => Assert.Equal(PostingPeriodCheck.SoftClosed, Refusal(Plan((D1, March)), Mar with { State = FiscalPeriodState.SoftClosed }));
    [Fact] public void A_date_without_a_period_is_refused() => Assert.Equal(PostingPeriodCheck.NoPeriod, Refusal(Plan((D2, April)), Mar));
    [Fact] public void A_closed_year_is_inconsistent_evidence() => Assert.Equal("compensation_inconsistent", Refusal(Plan((D1, March)), Mar with { YearClosed = true }));
    [Fact] public void A_line_whose_recorded_period_is_not_the_period_of_its_date_is_inconsistent() => Assert.Equal("compensation_inconsistent", Refusal(Plan((D1, April)), Mar, Apr));
    [Fact] public void An_unposted_original_with_no_recorded_period_is_inconsistent() => Assert.Equal("compensation_inconsistent", Refusal(Plan((D1, null)), Mar));
    [Fact] public void Overlapping_periods_are_inconsistent() => Assert.Equal("compensation_inconsistent", Refusal(Plan((D1, March)), Mar, Mar with { Id = Guid.NewGuid() }));
}
