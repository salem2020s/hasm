using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class CostReplayBlockTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid A = Id(10001), B = Id(10002);
    private static readonly DateOnly Day = new(2026, 1, 10);
    private static ReplayFlowChild Child(int parent, int id, long ordinal, decimal q, decimal? amount = null, Guid? position = null, long? line = null) =>
        new(Id(parent), line ?? id, ordinal, new(Id(id), amount is null ? CostOperationKind.Issue : CostOperationKind.Receipt, position ?? A, q, amount));
    private static ReplayFlowBlock Block(int id, CostOperationKind kind, ImmutableArray<ReplayOrderLeg> legs, params ReplayFlowChild[] children) =>
        new(new(Id(id), Day, kind, legs), children.ToImmutableArray());
    private static ReplayBlockResult Run(CostMethod method, params ReplayFlowBlock[] blocks) => CostReplay.CalculateBlocks(method, 2, [A, B], blocks.ToImmutableArray());
    private static void Inconsistent(params ReplayFlowBlock[] blocks) =>
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, blocks)).Code);

    [Fact]
    public void Receipt_children_retain_distinct_fifo_layers_instead_of_becoming_one_average_layer()
    {
        var receipt = Block(10, CostOperationKind.Receipt, [new(A, 1, 2)], Child(10, 1, 1, 1, 1), Child(10, 2, 2, 2, 100));
        var issue = Block(20, CostOperationKind.Issue, [new(A, 3)], Child(20, 3, 3, 2));
        var result = Run(CostMethod.Fifo, issue, receipt);
        Assert.Equal([Id(10), Id(20)], result.GroupOrder.ToArray());
        Assert.Equal([Id(1), Id(2), Id(3)], result.Flow.Steps.Select(s => s.EventId));
        Assert.Equal(51m, result.Flow.Steps[^1].Legs[0].Step.Amount);
        Assert.Equal([Id(1), Id(2)], result.Flow.Steps[^1].Legs[0].Step.Consumptions.Select(t => t.LayerId));
        Assert.Equal([1m, 50m], result.Flow.Steps[^1].Legs[0].Step.Consumptions.Select(t => CostingMath.FromUnits(t.Amount, CostingMath.AmountScale)));
        Assert.Equal([Id(10), Id(10), Id(20)], result.Bindings.Select(b => b.GroupId));
        var collapsed = CostReplay.CalculateFlow(CostMethod.Fifo, 2, [A],
            [new(Id(4), CostOperationKind.Receipt, A, 3, 101), new(Id(5), CostOperationKind.Issue, A, 2)]);
        Assert.Equal(67.33m, collapsed.Steps[^1].Legs[0].Step.Amount);
        Assert.Equal(2, receipt.Children.Length);
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Issue_children_round_individually_without_inventing_a_net_operation_cost(CostMethod method)
    {
        var receipt = Block(10, CostOperationKind.Receipt, [new(A, 1)], Child(10, 1, 1, 6, 0.02m));
        var issue = Block(20, CostOperationKind.Issue, [new(A, 2, 2)], Child(20, 2, 2, 1), Child(20, 3, 3, 1));
        var result = Run(method, issue, receipt);
        Assert.Equal([0m, 0m], result.Flow.Steps.Skip(1).Select(s => s.Legs[0].Step.Amount));
        var position = Assert.Single(result.Flow.Positions, p => p.PositionId == A);
        Assert.Equal((4m, 0.02m), (position.Quantity, position.Value));
        var collapsed = CostReplay.CalculateFlow(method, 2, [A],
            [new(Id(4), CostOperationKind.Receipt, A, 6, 0.02m), new(Id(5), CostOperationKind.Issue, A, 2)]);
        Assert.Equal(0.01m, collapsed.Steps[^1].Legs[0].Step.Amount);
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Valid_receipt_children_keep_their_own_input_limits_when_the_operation_total_is_larger(CostMethod method)
    {
        var receipt = Block(10, CostOperationKind.Receipt, [new(A, 1, 2)],
            Child(10, 1, 1, 0.5m, 900_000_000_000_000_000m), Child(10, 2, 2, 0.5m, 900_000_000_000_000_000m));
        var issue = Block(20, CostOperationKind.Issue, [new(A, 3)], Child(20, 3, 3, 1));
        var result = Run(method, receipt, issue);
        Assert.Equal(1_800_000_000_000_000_000m, result.Flow.Steps[^1].Legs[0].Step.Amount);
        Assert.All(result.Flow.Positions, p => Assert.Equal((0m, 0m), (p.Quantity, p.Value)));
    }

    [Theory]
    [InlineData(CostMethod.Fifo, "10", "33.33", "67.67")]
    [InlineData(CostMethod.MovingAverage, "13.42", "13.41", "84.17")]
    public void Multiline_multiwarehouse_groups_remain_atomic_before_a_later_transfer(CostMethod method, string first, string last, string remaining)
    {
        var receipt = Block(10, CostOperationKind.Receipt, [new(A, 1, 2), new(B, 1)],
            Child(10, 1, 1, 3, 100), Child(10, 2, 1, 1, 10, B), Child(10, 3, 2, 3, 1));
        var transfer = Block(20, CostOperationKind.Transfer, [new(A, 3), new(B, 2)],
            new ReplayFlowChild(Id(20), 4, 3, new(Id(20), CostOperationKind.Transfer, A, 1, DestinationPositionId: B), 2));
        var issue = Block(30, CostOperationKind.Issue, [new(B, 3, 2)], Child(30, 5, 3, 1, position: B), Child(30, 6, 4, 1, position: B));
        var result = Run(method, issue, transfer, receipt);
        decimal Money(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal([Id(10), Id(20), Id(30)], result.GroupOrder.ToArray());
        Assert.Equal([Id(1), Id(2), Id(3), Id(20), Id(5), Id(6)], result.Bindings.Select(b => b.EventId));
        Assert.Equal([Money(first), Money(last)], result.Flow.Steps.TakeLast(2).Select(s => s.Legs[0].Step.Amount));
        var a = Assert.Single(result.Flow.Positions, p => p.PositionId == A);
        Assert.Equal((5m, Money(remaining)), (a.Quantity, a.Value));
        Assert.Equal(result.Bindings.ToArray(), Run(method, receipt, issue, transfer).Bindings.ToArray());
    }

    [Fact]
    public void Missing_extra_reordered_and_mislinked_children_are_refused_not_summed()
    {
        var one = Child(10, 1, 1, 1, 1); var two = Child(10, 2, 2, 1, 1);
        var block = Block(10, CostOperationKind.Receipt, [new(A, 1, 2)], one, two);
        foreach (var children in new ImmutableArray<ReplayFlowChild>[] {
            [one], [one, two, Child(10, 3, 3, 1, 1)], [two, one], [one, two with { LineOrder = 1 }],
            [one, two with { ParentId = Id(99) }], [one, two with { PositionOrdinal = 3 }],
            [one, two with { Event = two.Event with { Kind = CostOperationKind.Issue, ReceiptAmount = null } }],
            [one, two with { Event = two.Event with { PositionId = B } }], [one, two with { DestinationOrdinal = 1 }] })
            Inconsistent(block with { Children = children });
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo,
            block with { Children = [one, two with { Event = two.Event with { Id = one.Event.Id } }] })).Code);
    }

    [Fact]
    public void Transfer_children_must_bind_the_single_exact_pair_and_parent()
    {
        var receipt = Block(10, CostOperationKind.Receipt, [new(A, 1)], Child(10, 1, 1, 3, 100));
        var child = new ReplayFlowChild(Id(20), 2, 2, new(Id(20), CostOperationKind.Transfer, A, 1, DestinationPositionId: B), 1);
        var transfer = Block(20, CostOperationKind.Transfer, [new(A, 2), new(B, 1)], child);
        foreach (var bad in new[] { child with { DestinationOrdinal = null }, child with { DestinationOrdinal = 2 },
            child with { Event = child.Event with { Id = Id(99) } }, child with { Event = child.Event with { DestinationPositionId = A } } })
            Inconsistent(receipt, transfer with { Children = [bad] });
        Inconsistent(receipt, transfer with { Children = [child, child] });
    }

    [Fact]
    public void Unsupported_child_kind_is_refused_before_any_earlier_shortage()
    {
        var issue = Block(10, CostOperationKind.Issue, [new(A, 1, 2)], Child(10, 1, 1, 1),
            Child(10, 2, 2, 1) with { Event = new(Id(2), CostOperationKind.Reversal, A, 1) });
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, issue)).Code);
    }

    [Fact]
    public void Multiline_entry_budget_is_proved_by_parent_ranges_without_relaxing_the_ungrouped_limit()
    {
        var blocks = Enumerable.Range(0, 201).Select(b => {
            var parent = b + 1_000_000;
            return Block(parent, CostOperationKind.Receipt, [new(A, b * 500L + 1, 500)],
                Enumerable.Range(1, 500).Select(n => Child(parent, b * 500 + n, b * 500L + n, 1, 0)).ToArray());
        }).ToImmutableArray();
        var result = CostReplay.CalculateBlocks(CostMethod.Fifo, 2, [A], blocks);
        Assert.Equal(100_500, result.Flow.Steps.Length);
        Assert.Equal(201, result.GroupOrder.Length);
        Assert.Equal(100_500m, Assert.Single(result.Flow.Positions).Quantity);
        var flattened = blocks.SelectMany(b => b.Children).Select(c => c.Event).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => CostReplay.CalculateFlow(CostMethod.Fifo, 2, [A], flattened)).Code);
        var oversized = blocks[0] with { Children = blocks[0].Children.Add(Child(1_000_000, 100_501, 501, 1, 0)) };
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, oversized)).Code);
    }

    [Fact]
    public void Config_numeric_input_and_cancellation_gates_remain_mandatory()
    {
        var receipt = Block(10, CostOperationKind.Receipt, [new(A, 1)], Child(10, 1, 1, 1, 0.001m));
        Assert.Equal("precision_exceeded", Assert.Throws<CostingException>(() => Run(CostMethod.Fifo, receipt)).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateBlocks(CostMethod.Fifo, 2, [A], default)).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostReplay.CalculateBlocks((CostMethod)99, 2, [A], [])).Code);
        Assert.Throws<OperationCanceledException>(() => CostReplay.CalculateBlocks(CostMethod.Fifo, 2, [A], [receipt], new CancellationToken(true)));
    }
}
