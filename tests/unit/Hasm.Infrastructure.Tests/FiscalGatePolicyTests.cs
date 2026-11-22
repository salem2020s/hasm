using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Tests;

public class FiscalGatePolicyTests
{
    [Theory]
    [InlineData(FiscalPeriodState.Open, false, true, null)]
    [InlineData(FiscalPeriodState.Open, true, true, null)]
    [InlineData(FiscalPeriodState.SoftClosed, false, false, PostingPeriodCheck.SoftClosed)]
    [InlineData(FiscalPeriodState.SoftClosed, true, true, null)]
    [InlineData(FiscalPeriodState.FinalClosed, false, false, PostingPeriodCheck.FinalClosed)]
    [InlineData(FiscalPeriodState.FinalClosed, true, false, PostingPeriodCheck.FinalClosed)]
    public void Final_closed_is_always_a_barrier_and_soft_closed_needs_explicit_authority(
        FiscalPeriodState state, bool allowSoftClosed, bool allowed, string? reason)
    {
        var id = Guid.NewGuid(); var result = FiscalPeriodGate.Evaluate(id, state, allowSoftClosed);
        Assert.Equal((allowed, reason, id, state), (result.Allowed, result.Reason, result.PeriodId, result.State));
    }

    [Fact]
    public void Absence_of_a_period_never_grants_posting_authority()
    {
        var result = FiscalPeriodGate.Evaluate(null, null, true);
        Assert.False(result.Allowed); Assert.Equal(PostingPeriodCheck.NoPeriod, result.Reason);
        Assert.Null(result.PeriodId); Assert.Null(result.State);
    }
}
