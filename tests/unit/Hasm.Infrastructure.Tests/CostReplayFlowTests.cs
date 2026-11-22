using System.Collections.Immutable;
using System.Globalization;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class CostReplayFlowTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid A = Id(10001), B = Id(10002), C = Id(10003);
    private static ReplayFlowEvent Receipt(int id, Guid position, decimal q, decimal amount) => new(Id(id), CostOperationKind.Receipt, position, q, amount);
    private static ReplayFlowEvent Issue(int id, Guid position, decimal q) => new(Id(id), CostOperationKind.Issue, position, q);
    private static ReplayFlowEvent Transfer(int id, Guid from, Guid to, decimal q) => new(Id(id), CostOperationKind.Transfer, from, q, DestinationPositionId: to);
    private static ReplayFlowResult Run(CostMethod method, ImmutableArray<ReplayFlowEvent> events) => CostReplay.CalculateFlow(method, 2, [A, B, C], events);
    private static ReplayFlowPosition Position(ReplayFlowResult result, Guid id) => Assert.Single(result.Positions, p => p.PositionId == id);

    private static void AssertConservation(ReplayFlowResult result, ImmutableArray<ReplayFlowEvent> events)
    {
        var balances = result.Positions.ToDictionary(p => p.PositionId, _ => (Q: 0m, V: 0m));
        foreach (var pair in result.Steps.Zip(events))
        {
            var (step, input) = pair;
            Assert.Equal(input.Id, step.EventId);
            var beforeQ = balances.Values.Sum(p => p.Q); var beforeV = balances.Values.Sum(p => p.V);
            foreach (var leg in step.Legs) balances[leg.PositionId] = (leg.Step.QuantityAfter, leg.Step.ValueAfter);
            var afterQ = balances.Values.Sum(p => p.Q); var afterV = balances.Values.Sum(p => p.V);
            if (input.Kind == CostOperationKind.Transfer)
            {
                Assert.Equal(2, step.Legs.Length);
                Assert.Equal(CostOperationKind.Issue, step.Legs[0].Step.Kind);
                Assert.Equal(CostOperationKind.Receipt, step.Legs[1].Step.Kind);
                Assert.Equal(step.Legs[0].Step.Amount, step.Legs[1].Step.Amount);
                Assert.Equal((beforeQ, beforeV), (afterQ, afterV));
            }
            else
            {
                var leg = Assert.Single(step.Legs);
                var sign = input.Kind == CostOperationKind.Receipt ? 1 : -1;
                Assert.Equal((beforeQ + sign * input.Quantity, beforeV + sign * leg.Step.Amount), (afterQ, afterV));
            }
        }
        foreach (var position in result.Positions)
            Assert.Equal((position.Quantity, position.Value), balances[position.PositionId]);
    }

    [Theory]
    [InlineData(CostMethod.Fifo, 3, 1, 6, 3, 3)]
    [InlineData(CostMethod.MovingAverage, 6, 2, 4, 2, 2)]
    public void Chained_transfers_preserve_each_cost_component_and_company_value(CostMethod method,
        decimal bIssue, decimal cIssue, decimal aValue, decimal bValue, decimal cValue)
    {
        ImmutableArray<ReplayFlowEvent> events = [Receipt(1, A, 4, 4), Receipt(2, A, 4, 12), Transfer(3, A, B, 6),
            Issue(4, B, 3), Transfer(5, B, C, 2), Issue(6, C, 1)];
        var result = Run(method, events);
        Assert.Equal(bIssue, result.Steps[3].Legs[0].Step.Amount);
        Assert.Equal(cIssue, result.Steps[5].Legs[0].Step.Amount);
        Assert.Equal((2m, aValue), (Position(result, A).Quantity, Position(result, A).Value));
        Assert.Equal((1m, bValue), (Position(result, B).Quantity, Position(result, B).Value));
        Assert.Equal((1m, cValue), (Position(result, C).Quantity, Position(result, C).Value));
        AssertConservation(result, events);
        if (method == CostMethod.Fifo)
        {
            var first = result.CreatedLayers.Where(l => l.EventId == Id(3)).ToArray();
            Assert.Equal([Id(1), Id(2)], first.Select(l => l.SourceLayerId!.Value));
            Assert.Equal([1, 2], first.Select(l => l.Component));
            Assert.Equal([4m, 2m], first.Select(l => CostingMath.FromUnits(l.Quantity, CostingMath.QuantityScale)));
            Assert.Equal([4m, 6m], first.Select(l => CostingMath.FromUnits(l.Amount, CostingMath.AmountScale)));
            var second = result.CreatedLayers.Where(l => l.EventId == Id(5)).ToArray();
            Assert.Equal(first.Select(l => l.LayerId), second.Select(l => l.SourceLayerId!.Value));
            Assert.Equal([1m, 3m], second.Select(l => CostingMath.FromUnits(l.Amount, CostingMath.AmountScale)));
            // Independent collapsed-layer counterexamples: correct B issue is3, not5;
            // with correct B source components, correct C issue is1, not2.
            Assert.NotEqual(5m, result.Steps[3].Legs[0].Step.Amount);
            Assert.NotEqual(2m, result.Steps[5].Legs[0].Step.Amount);
        }
        else
        {
            Assert.Empty(result.CreatedLayers);
            Assert.All(result.Positions, p => Assert.Empty(p.OpenLayers));
        }
    }

    [Theory]
    [InlineData(CostMethod.Fifo, "33.34", "66.66")]
    [InlineData(CostMethod.MovingAverage, "33.33", "66.67")]
    public void Returning_transfer_appends_new_layers_instead_of_reopening_an_origin(CostMethod method, string issue, string value)
    {
        ImmutableArray<ReplayFlowEvent> events = [Receipt(1, A, 3, 100), Transfer(2, A, B, 1), Transfer(3, B, A, 1), Issue(4, A, 1)];
        var result = Run(method, events);
        Assert.Equal(decimal.Parse(issue, CultureInfo.InvariantCulture), result.Steps[^1].Legs[0].Step.Amount);
        Assert.Equal((2m, decimal.Parse(value, CultureInfo.InvariantCulture)), (Position(result, A).Quantity, Position(result, A).Value));
        Assert.Equal((0m, 0m), (Position(result, B).Quantity, Position(result, B).Value));
        AssertConservation(result, events);
        if (method == CostMethod.Fifo)
        {
            var outward = Assert.Single(result.CreatedLayers, l => l.EventId == Id(2));
            var returned = Assert.Single(result.CreatedLayers, l => l.EventId == Id(3));
            Assert.Equal(outward.LayerId, returned.SourceLayerId);
            Assert.NotEqual(Id(1), returned.LayerId);
            Assert.Equal([Id(1), returned.LayerId], Position(result, A).OpenLayers.Select(l => l.Id));
        }
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Large_derived_transfer_value_is_carried_exactly_and_free_stock_stays_free(CostMethod method)
    {
        ImmutableArray<ReplayFlowEvent> events = [Receipt(1, A, 0.5m, 900_000_000_000_000_000m), Receipt(2, A, 0.5m, 900_000_000_000_000_000m),
            Transfer(3, A, B, 1), Issue(4, B, 1), Receipt(5, C, 3, 0), Transfer(6, C, A, 3), Issue(7, A, 3)];
        var result = Run(method, events);
        Assert.Equal(1_800_000_000_000_000_000m, result.Steps[2].Legs[1].Step.Amount);
        Assert.Equal(result.Steps[2].Legs[1].Step.Amount, result.Steps[3].Legs[0].Step.Amount);
        Assert.Equal(0m, result.Steps[^1].Legs[0].Step.Amount);
        Assert.All(result.Positions, p => Assert.Equal((0m, 0m), (p.Quantity, p.Value)));
        AssertConservation(result, events);
    }

    [Fact]
    public void Mirror_identity_is_versioned_repeatable_and_culture_independent()
    {
        ImmutableArray<ReplayFlowEvent> events = [Receipt(1, A, 3, 100), Transfer(3, A, B, 1), Transfer(4, B, C, 1)];
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var first = Run(CostMethod.Fifo, events);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = Run(CostMethod.Fifo, events);
            Assert.Equal(Guid.Parse("5dcb9668-ab0e-e5e1-4ffc-f2d850fe1c27"), first.CreatedLayers[1].LayerId);
            Assert.Equal(first.CreatedLayers.ToArray(), second.CreatedLayers.ToArray());
            Assert.Equal(first.Steps.SelectMany(s => s.Legs).Select(l => (l.PositionId, l.Step.EventId, l.Step.Amount, l.Step.QuantityAfter, l.Step.ValueAfter)),
                second.Steps.SelectMany(s => s.Legs).Select(l => (l.PositionId, l.Step.EventId, l.Step.Amount, l.Step.QuantityAfter, l.Step.ValueAfter)));
            Assert.Equal(3, events.Length);
            Assert.Equal(Receipt(1, A, 3, 100), events[0]);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Theory]
    [InlineData(CostMethod.Fifo, 0, "2", "1")]
    [InlineData(CostMethod.MovingAverage, 0, "2", "1")]
    [InlineData(CostMethod.Fifo, 6, "0.000002", "0.000001")]
    [InlineData(CostMethod.MovingAverage, 6, "0.000002", "0.000001")]
    public void Currency_and_quantity_precision_boundaries_preserve_the_last_remainder(CostMethod method, int decimals, string value, string part)
    {
        const decimal unit = 0.0000000001m;
        var total = decimal.Parse(value, CultureInfo.InvariantCulture); var amount = decimal.Parse(part, CultureInfo.InvariantCulture);
        ImmutableArray<ReplayFlowEvent> events = [Receipt(1, A, 3 * unit, total), Transfer(2, A, B, unit), Transfer(3, B, C, unit),
            Issue(4, A, unit), Issue(5, A, unit), Issue(6, C, unit)];
        var result = CostReplay.CalculateFlow(method, decimals, [A, B, C], events);
        Assert.Equal(amount, result.Steps[1].Legs[1].Step.Amount);
        Assert.Equal([amount, 0m, amount], result.Steps.Skip(3).Select(s => s.Legs[0].Step.Amount));
        Assert.All(result.Positions, p => Assert.Equal((0m, 0m), (p.Quantity, p.Value)));
        AssertConservation(result, events);
    }

    [Theory]
    [InlineData(CostMethod.Fifo, "7", "14", "3", "10", "6", "32")]
    [InlineData(CostMethod.MovingAverage, "10", "23.33", "2", "4.67", "4", "28")]
    public void Inserted_receipt_propagates_recalculated_cost_through_both_transfers_without_mutating_history(CostMethod method,
        string oldB, string newB, string oldC, string newC, string oldValue, string newValue)
    {
        ImmutableArray<ReplayFlowEvent> original = [Receipt(1, A, 4, 4), Receipt(2, A, 4, 12), Transfer(3, A, B, 6),
            Issue(4, B, 5), Transfer(5, B, C, 1), Issue(6, C, 1)];
        ImmutableArray<ReplayFlowEvent> revised = [original[0], Receipt(7, A, 4, 40), .. original.Skip(1)];
        var before = Run(method, original); var after = Run(method, revised);
        decimal Money(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal(Money(oldB), before.Steps[3].Legs[0].Step.Amount);
        Assert.Equal(Money(newB), after.Steps[4].Legs[0].Step.Amount);
        Assert.Equal(Money(oldC), before.Steps[^1].Legs[0].Step.Amount);
        Assert.Equal(Money(newC), after.Steps[^1].Legs[0].Step.Amount);
        Assert.Equal((2m, Money(oldValue)), (Position(before, A).Quantity, Position(before, A).Value));
        Assert.Equal((6m, Money(newValue)), (Position(after, A).Quantity, Position(after, A).Value));
        AssertConservation(before, original); AssertConservation(after, revised);
        Assert.Equal(6, original.Length);
        Assert.Equal(Receipt(2, A, 4, 12), original[1]);
        if (method == CostMethod.Fifo)
        {
            Assert.Equal([Id(1), Id(2)], before.CreatedLayers.Where(l => l.EventId == Id(3)).Select(l => l.SourceLayerId!.Value));
            Assert.Equal([Id(1), Id(7)], after.CreatedLayers.Where(l => l.EventId == Id(3)).Select(l => l.SourceLayerId!.Value));
        }
    }

    [Fact]
    public void A_mirror_identity_colliding_with_any_future_input_is_refused()
    {
        var futureId = Guid.Parse("5dcb9668-ab0e-e5e1-4ffc-f2d850fe1c27");
        ImmutableArray<ReplayFlowEvent> events = [Receipt(1, A, 1, 1), Transfer(3, A, B, 1), new(futureId, CostOperationKind.Receipt, A, 1, 1)];
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, events)).Code);
    }

    [Theory]
    [InlineData(CostOperationKind.Reversal)]
    [InlineData((CostOperationKind)99)]
    public void Unsupported_history_is_refused_before_an_earlier_shortage_is_calculated(CostOperationKind kind)
    {
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo,
            [Issue(1, A, 1), new(Id(2), kind, A, 1)])).Code);
    }

    [Fact]
    public void Input_shape_identity_and_invented_transfer_cost_are_refused()
    {
        foreach (var events in new ImmutableArray<ReplayFlowEvent>[] {
            [Receipt(1, A, 1, 1), Receipt(1, B, 1, 1)], [Receipt(1, Id(999), 1, 1)],
            [Transfer(1, A, A, 1)], [Transfer(1, A, Id(999), 1)], [new(Id(1), CostOperationKind.Transfer, A, 1)],
            [Transfer(1, A, B, 1) with { ReceiptAmount = 10 }], [Receipt(1, A, 1, 1) with { DestinationPositionId = B }],
            [Receipt(1, A, 0, 1)], [Receipt(1, A, 1, -1)], [Receipt(1, A, decimal.MaxValue, 1)] })
            Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, events)).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 2, [A, A], [])).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow((CostMethod)99, 2, [A], [])).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 7, [A], [])).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 2, default, [])).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 2, [A], default)).Code);
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Precision_shortage_and_aggregate_overflow_return_no_partial_result(CostMethod method)
    {
        Assert.Equal("precision_exceeded", Assert.Throws<CostingException>(() => Run(method, [Receipt(1, A, 1, 0.001m)])).Code);
        Assert.Equal("precision_exceeded", Assert.Throws<CostingException>(() => Run(method, [Transfer(1, A, B, 0.00000000001m)])).Code);
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => Run(method, [Receipt(1, A, 1, 1), Transfer(2, A, B, 2), Receipt(3, A, 9, 9)])).Code);
        var large = Enumerable.Range(1, 9).Select(n => Receipt(n, A, 900_000_000_000_000_000m, 1)).ToImmutableArray();
        Assert.Equal("replay_total_out_of_range", Assert.Throws<CostingException>(() => Run(method, large)).Code);
        Assert.Empty(CostReplay.CalculateFlow(method, 2, [], []).Steps);
    }

    [Fact]
    public void Free_fifo_components_consume_the_shared_work_budget_in_returning_transfers()
    {
        var events = Enumerable.Range(1, 1001).Select(n => Receipt(n, A, 1, 0)).Concat(
            Enumerable.Range(0, 200).Select(n => Transfer(n + 2000, n % 2 == 0 ? A : B, n % 2 == 0 ? B : A, 1001))).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, events)).Code);
        var average = Run(CostMethod.MovingAverage, events);
        Assert.Equal((1001m, 0m), (Position(average, A).Quantity, Position(average, A).Value));
        Assert.Empty(average.CreatedLayers);
    }

    [Fact]
    public void Structural_bounds_and_cancellation_do_not_return_a_truncated_plan()
    {
        var excessive = Enumerable.Range(1, 100_001).Select(n => Receipt(n, A, 1, 0)).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, excessive)).Code);
        var positions = Enumerable.Range(1, 10_001).Select(Id).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 2, positions, [])).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 2, [A], [Receipt(1, A, 1, 1)], cancelled.Token));
    }
}
