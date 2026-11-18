using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class CostReplayTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static ReplayEvent Receipt(int n, decimal quantity, decimal amount) => new(Id(n), CostOperationKind.Receipt, quantity, amount);
    private static ReplayEvent Issue(int n, decimal quantity) => new(Id(n), CostOperationKind.Issue, quantity);

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Repeated_partial_issues_keep_the_exact_remainder_and_do_not_mutate_inputs(CostMethod method)
    {
        ImmutableArray<ReplayEvent> events = [Receipt(1, 3, 100), Issue(2, 1), Issue(3, 1), Issue(4, 1)];
        var result = CostReplay.Calculate(method, 2, events);
        Assert.Equal([33.33m, 33.34m, 33.33m], result.Steps.Skip(1).Select(s => s.Amount));
        Assert.Equal(100m, result.Steps.Skip(1).Sum(s => s.Amount));
        Assert.Equal((0m, 0m), (result.QuantityAfter, result.ValueAfter));
        Assert.Equal(Receipt(1, 3, 100), events[0]);
        var again = CostReplay.Calculate(method, 2, events);
        Assert.Equal(result.Steps.Select(s => (s.EventId, s.Amount, s.QuantityAfter, s.ValueAfter)),
            again.Steps.Select(s => (s.EventId, s.Amount, s.QuantityAfter, s.ValueAfter)));
        if (method == CostMethod.Fifo)
            Assert.All(result.Steps.Skip(1), s => Assert.Equal(Id(1), Assert.Single(s.Consumptions).LayerId));
        else Assert.All(result.Steps, s => Assert.Empty(s.Consumptions));
    }

    [Fact]
    public void Moving_average_replay_of_an_inserted_receipt_changes_later_issue_values_without_changing_original_input()
    {
        ImmutableArray<ReplayEvent> original = [Receipt(1, 10, 100), Issue(2, 5), Receipt(3, 10, 300), Issue(4, 5)];
        var before = CostReplay.Calculate(CostMethod.MovingAverage, 2, original);
        var after = CostReplay.Calculate(CostMethod.MovingAverage, 2, [original[0], Receipt(5, 10, 300), original[1], original[2], original[3]]);
        Assert.Equal([50m, 116.67m], before.Steps.Where(s => s.Kind == CostOperationKind.Issue).Select(s => s.Amount));
        Assert.Equal([100m, 120m], after.Steps.Where(s => s.Kind == CostOperationKind.Issue).Select(s => s.Amount));
        Assert.Equal((10m, 233.33m), (before.QuantityAfter, before.ValueAfter));
        Assert.Equal((20m, 480m), (after.QuantityAfter, after.ValueAfter));
        Assert.Equal(53.33m, after.Steps.Where(s => s.Kind == CostOperationKind.Issue).Sum(s => s.Amount) - before.Steps.Where(s => s.Kind == CostOperationKind.Issue).Sum(s => s.Amount));
        Assert.Equal(4, original.Length);
        Assert.Equal(before.ValueAfter, CostReplay.Calculate(CostMethod.MovingAverage, 2, original).ValueAfter);
    }

    [Fact]
    public void Fifo_replay_preserves_receipt_order_and_exact_allocations_after_an_inserted_layer()
    {
        ImmutableArray<ReplayEvent> original = [Receipt(1, 10, 10), Issue(2, 8), Receipt(3, 10, 30), Issue(4, 5)];
        var before = CostReplay.Calculate(CostMethod.Fifo, 2, original);
        var after = CostReplay.Calculate(CostMethod.Fifo, 2, [original[0], Receipt(5, 5, 10), original[1], original[2], original[3]]);
        Assert.Equal((7m, 21m, 11m), (before.QuantityAfter, before.ValueAfter, before.Steps[^1].Amount));
        Assert.Equal((12m, 34m, 8m), (after.QuantityAfter, after.ValueAfter, after.Steps[^1].Amount));
        Assert.Equal([Id(1), Id(5)], after.Steps[^1].Consumptions.Select(c => c.LayerId));
        Assert.Equal([2m, 3m], after.Steps[^1].Consumptions.Select(c => CostingMath.FromUnits(c.Quantity, CostingMath.QuantityScale)));
        Assert.Equal([2m, 6m], after.Steps[^1].Consumptions.Select(c => CostingMath.FromUnits(c.Amount, CostingMath.AmountScale)));
        Assert.Equal(21m, CostReplay.Calculate(CostMethod.Fifo, 2, original).ValueAfter);
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Free_receipts_keep_quantity_and_never_invent_value(CostMethod method)
    {
        var result = CostReplay.Calculate(method, 2, [Receipt(1, 4, 0), Issue(2, 3)]);
        Assert.Equal((1m, 0m, 0m), (result.QuantityAfter, result.ValueAfter, result.Steps[^1].Amount));
        Assert.Equal((0m, 0m), (CostReplay.Calculate(method, 2, []).QuantityAfter, CostReplay.Calculate(method, 2, []).ValueAfter));
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void A_historical_shortage_is_refused_even_if_a_later_receipt_would_cover_it(CostMethod method)
    {
        var ex = Assert.Throws<CostingException>(() => CostReplay.Calculate(method, 2, [Receipt(1, 2, 10), Issue(2, 3), Receipt(3, 10, 100)]));
        Assert.Equal("insufficient_stock", ex.Code);
    }

    [Theory]
    [InlineData(CostOperationKind.Transfer)]
    [InlineData(CostOperationKind.Reversal)]
    [InlineData((CostOperationKind)99)]
    public void Unsupported_history_is_refused_before_a_partial_calculation(CostOperationKind kind)
    {
        var ex = Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.Fifo, 2, [Issue(1, 1), new ReplayEvent(Id(2), kind, 1)]));
        Assert.Equal("impact_history_unsupported", ex.Code);
    }

    [Fact]
    public void Replay_refuses_missing_or_duplicated_identity_and_cannot_accept_an_invented_issue_cost()
    {
        foreach (var events in new ImmutableArray<ReplayEvent>[] {
            [Receipt(1, 1, 1), Receipt(1, 2, 2)],
            [new ReplayEvent(Guid.Empty, CostOperationKind.Receipt, 1, 1)],
            [new ReplayEvent(Id(1), CostOperationKind.Issue, 1, 99)],
            [new ReplayEvent(Id(1), CostOperationKind.Receipt, 1)],
            [Receipt(1, 0, 1)], [Receipt(1, -1, 1)], [Receipt(1, 1, -1)] })
            Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.Fifo, 2, events)).Code);
    }

    [Theory]
    [InlineData("0.00000000001", "1")]
    [InlineData("1", "0.001")]
    public void Precision_is_refused_without_silent_rounding(string quantity, string amount)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var e = Receipt(1, decimal.Parse(quantity, culture), decimal.Parse(amount, culture));
        Assert.Equal("precision_exceeded", Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.Fifo, 2, [e])).Code);
    }

    [Fact]
    public void An_unknown_method_or_currency_precision_cannot_produce_a_plan()
    {
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.Calculate((CostMethod)99, 2, [])).Code);
        foreach (var decimals in new[] { -1, 7 })
            Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.Fifo, decimals, [])).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.Fifo, 2, default)).Code);
    }

    [Fact]
    public void Engine_input_bounds_are_checked_before_scaling_extreme_decimals()
    {
        foreach (var e in new[] { Receipt(1, decimal.MaxValue, 1), Receipt(1, 1, decimal.MaxValue),
            Receipt(1, 1_000_000_000_000_000_000m, 1), Receipt(1, 1, 1_000_000_000_000_000_000m) })
            Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.Fifo, 2, [e])).Code);
    }

    [Fact]
    public void Cumulative_quantity_outside_the_existing_arithmetic_conversion_range_is_refused_without_overflow()
    {
        // Each event is within the engine's input range, but nine such balances exceed FromUnits' decimal integer range.
        var events = Enumerable.Range(1, 9).Select(n => Receipt(n, 900_000_000_000_000_000m, 1)).ToImmutableArray();
        Assert.Equal("replay_total_out_of_range", Assert.Throws<CostingException>(() => CostReplay.Calculate(CostMethod.MovingAverage, 2, events)).Code);
    }

    [Fact]
    public void Many_single_unit_fifo_issues_preserve_each_receipt_and_the_total_value()
    {
        var events = Enumerable.Range(1, 2_000).Select(n => Receipt(n, 1, 1))
            .Concat(Enumerable.Range(2_001, 2_000).Select(n => Issue(n, 1))).ToImmutableArray();
        var result = CostReplay.Calculate(CostMethod.Fifo, 2, events);
        Assert.Equal((0m, 0m), (result.QuantityAfter, result.ValueAfter));
        var issues = result.Steps.Skip(2_000).ToArray();
        Assert.Equal(2_000m, issues.Sum(s => s.Amount));
        Assert.Equal(Enumerable.Range(1, 2_000).Select(Id), issues.Select(s => Assert.Single(s.Consumptions).LayerId));
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Derived_issue_cost_can_exceed_a_single_receipt_input_limit_without_losing_value(CostMethod method)
    {
        const decimal receiptAmount = 900_000_000_000_000_000m;
        var result = CostReplay.Calculate(method, 2, [Receipt(1, 0.5m, receiptAmount), Receipt(2, 0.5m, receiptAmount), Issue(3, 1)]);
        Assert.Equal(1_800_000_000_000_000_000m, result.Steps[^1].Amount);
        Assert.Equal((0m, 0m), (result.QuantityAfter, result.ValueAfter));
        Assert.Equal(result.Steps.Take(2).Sum(s => s.Amount), result.Steps[^1].Amount);
        if (method == CostMethod.Fifo)
        {
            Assert.Equal([Id(1), Id(2)], result.Steps[^1].Consumptions.Select(t => t.LayerId));
            Assert.All(result.Steps[^1].Consumptions, t => Assert.Equal(receiptAmount, CostingMath.FromUnits(t.Amount, CostingMath.AmountScale)));
        }
        else Assert.Empty(result.Steps[^1].Consumptions);
    }

    [Fact]
    public void Cancelled_replay_returns_no_partial_result()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CostReplay.Calculate(CostMethod.Fifo, 2, [Receipt(1, 10, 100)], cancelled.Token));
    }
}
