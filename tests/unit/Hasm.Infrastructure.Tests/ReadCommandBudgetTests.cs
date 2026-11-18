using Hasm.Infrastructure.CompanyDatabase;

namespace Hasm.Infrastructure.Tests;

public class ReadCommandBudgetTests
{
    [Fact]
    public void Cleanup_reservation_is_atomic_and_exhaustion_never_increases_capacity()
    {
        var budget = new ReadCommandBudget(3, default); budget.Reserve(2); budget.Reserve();
        Assert.Equal(3, budget.Used);
        Assert.Equal(ReadBudgetFailure.Exhausted, Assert.Throws<ReadCommandBudgetException>(() => budget.Reserve()).Failure);
        Assert.Equal(3, budget.Used);
        var small = new ReadCommandBudget(1, default);
        Assert.Throws<ReadCommandBudgetException>(() => small.Reserve(2)); Assert.Equal(0, small.Used);
    }
    [Fact]
    public void Concurrent_reservations_cannot_overspend_the_operation_quota()
    {
        var budget = new ReadCommandBudget(17, default); var accepted = 0;
        Parallel.For(0, 100, _ =>
        {
            try { budget.Reserve(); Interlocked.Increment(ref accepted); }
            catch (ReadCommandBudgetException e) { Assert.Equal(ReadBudgetFailure.Exhausted, e.Failure); }
        });
        Assert.Equal(17, accepted); Assert.Equal(17, budget.Used);
    }
    [Fact]
    public void Cancellation_precedes_limit_diagnostics_and_invalid_reservations_are_refused()
    {
        using var cancelled = new CancellationTokenSource(); var budget = new ReadCommandBudget(1, cancelled.Token);
        budget.Reserve(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => budget.Reserve()); Assert.Equal(1, budget.Used);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadCommandBudget(0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadCommandBudget(20_001, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadCommandBudget(1, default).Reserve(0));
    }
}
