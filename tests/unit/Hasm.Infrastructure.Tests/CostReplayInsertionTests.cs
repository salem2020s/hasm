using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class CostReplayInsertionTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid A = Id(1001), B = Id(1002);
    private static readonly DateOnly Day = new(2026, 1, 10);
    private static ReplayFlowBlock Ordinary(int group, int child, CostOperationKind kind, Guid position,
        long ordinal, DateOnly date, decimal quantity, decimal? amount = null) =>
        new(new(Id(group), date, kind, [new(position, ordinal)]),
            [new(Id(group), 1, ordinal, new(Id(child), kind, position, quantity, amount))]);
    private static ReplayFlowBlock Transfer(int group, DateOnly date) =>
        new(new(Id(group), date, CostOperationKind.Transfer, [new(A, 1), new(B, 1)]),
            [new(Id(group), 1, 1, new(Id(group), CostOperationKind.Transfer, A, 1, DestinationPositionId: B), 1)]);

    [Theory]
    [InlineData(CostMethod.Fifo, 1, 7)]
    [InlineData(CostMethod.MovingAverage, 2, 6)]
    public void Receipt_insertion_preserves_historical_ordinals_and_replays_later_costs(CostMethod method, decimal cost, decimal value)
    {
        var receipt = Ordinary(10, 11, CostOperationKind.Receipt, A, 1, Day, 2, 2);
        var issue = Ordinary(20, 21, CostOperationKind.Issue, A, 2, Day.AddDays(2), 1);
        var proposed = Ordinary(30, 31, CostOperationKind.Receipt, A, 1, Day.AddDays(1), 2, 6);
        var revised = CostReplay.CalculateBlocksWithInsertion(method, 2, [A], [issue, receipt], proposed);
        Assert.Equal(new[] { Id(10), Id(30), Id(20) }, revised.GroupOrder);
        Assert.Equal(cost, revised.Flow.Steps[^1].Legs[0].Step.Amount);
        var final = Assert.Single(revised.Flow.Positions); Assert.Equal((3m, value), (final.Quantity, final.Value));
        Assert.Equal(2, issue.Children[0].PositionOrdinal); Assert.Equal(2, issue.Order.Legs[0].Ordinal);
    }

    [Theory]
    [InlineData(CostMethod.Fifo, "6", "1")]
    [InlineData(CostMethod.MovingAverage, "4.67", "2.33")]
    public void Transfer_insertion_carries_computed_cost_into_a_previously_disconnected_history(CostMethod method, string cost, string value)
    {
        var source = Ordinary(10, 11, CostOperationKind.Receipt, A, 1, Day, 2, 2);
        var target = Ordinary(20, 21, CostOperationKind.Receipt, B, 1, Day, 2, 6);
        var issue = Ordinary(40, 41, CostOperationKind.Issue, B, 2, Day.AddDays(2), 2);
        var proposed = Transfer(30, Day.AddDays(1));
        var revised = CostReplay.CalculateBlocksWithInsertion(method, 2, [A, B], [issue, target, source], proposed);
        var transfer = Assert.Single(revised.Flow.Steps, s => s.EventId == Id(30));
        Assert.All(transfer.Legs, l => Assert.Equal(1m, l.Step.Amount));
        Assert.Equal(decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), revised.Flow.Steps[^1].Legs[0].Step.Amount);
        var final = Assert.Single(revised.Flow.Positions, p => p.PositionId == B);
        Assert.Equal((1m, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), (final.Quantity, final.Value));
    }

    [Fact]
    public void Proposal_follows_all_same_date_groups_on_both_endpoints_even_when_its_id_sorts_first()
    {
        var source = Ordinary(10, 11, CostOperationKind.Receipt, A, 1, Day, 2, 2);
        var target = Ordinary(20, 21, CostOperationKind.Receipt, B, 1, Day, 1, 3);
        var issue = Ordinary(40, 41, CostOperationKind.Issue, B, 2, Day, 1);
        var revised = CostReplay.CalculateBlocksWithInsertion(CostMethod.Fifo, 2, [A, B], [source, target, issue], Transfer(30, Day));
        Assert.Equal(new[] { Id(10), Id(20), Id(40), Id(30) }, revised.GroupOrder);
        var final = Assert.Single(revised.Flow.Positions, p => p.PositionId == B); Assert.Equal((1m, 1m), (final.Quantity, final.Value));
    }

    [Fact]
    public void Proposal_ordinals_are_local_ranges_and_event_identity_collisions_are_refused()
    {
        var receipt = Ordinary(10, 11, CostOperationKind.Receipt, A, 1, Day, 2, 2);
        var invalidRange = Ordinary(30, 31, CostOperationKind.Receipt, A, 2, Day.AddDays(1), 1, 1);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() =>
            CostReplay.CalculateBlocksWithInsertion(CostMethod.Fifo, 2, [A], [receipt], invalidRange)).Code);
        var duplicateEvent = Ordinary(30, 11, CostOperationKind.Receipt, A, 1, Day.AddDays(1), 1, 1);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() =>
            CostReplay.CalculateBlocksWithInsertion(CostMethod.Fifo, 2, [A], [receipt], duplicateEvent)).Code);
    }

    [Fact]
    public void Proposed_group_reserves_capacity_before_any_historical_arithmetic()
    {
        var receipt = Ordinary(10, 11, CostOperationKind.Receipt, A, 1, Day, 2, 2);
        var proposed = Ordinary(30, 31, CostOperationKind.Receipt, A, 1, Day, 1, 1);
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() =>
            CostReplay.CalculateBlocksWithInsertion(CostMethod.Fifo, 2, [A], Enumerable.Repeat(receipt, 100_000).ToImmutableArray(), proposed)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CostReplay.CalculateBlocksWithInsertion(CostMethod.Fifo, 2, [A], [], proposed, cancelled.Token));
    }
}
