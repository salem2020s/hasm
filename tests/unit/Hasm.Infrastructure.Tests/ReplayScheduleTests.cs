using System.Collections.Immutable;
using System.Globalization;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class ReplayScheduleTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid A = Id(100), B = Id(101), C = Id(102);
    private static readonly DateOnly Day = new(2026, 1, 10);
    private static ReplayOrderGroup Receipt(int id, Guid position, long ordinal, int day = 0) =>
        new(Id(id), Day.AddDays(day), CostOperationKind.Receipt, [new(position, ordinal)]);
    private static ReplayOrderGroup Transfer(int id, Guid from, long fromOrdinal, Guid to, long toOrdinal, int day = 0) =>
        new(Id(id), Day.AddDays(day), CostOperationKind.Transfer, [new(from, fromOrdinal), new(to, toOrdinal)]);
    private static void Before(ImmutableArray<Guid> order, Guid first, Guid second) => Assert.True(order.IndexOf(first) < order.IndexOf(second));
    private static void Inconsistent(ImmutableArray<ReplayOrderGroup> history) =>
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate(history)).Code);

    [Fact]
    public void Proposed_receipt_order_propagates_through_indivisible_chained_transfers()
    {
        var ra = Receipt(10, A, 1); var rb = Receipt(20, B, 1); var rc = Receipt(30, C, 1);
        var t = Transfer(40, A, 2, B, 2, 1); var u = Transfer(50, B, 3, C, 2, 2);
        ImmutableArray<ReplayOrderGroup> history = [u, rb, t, rc, ra];
        var proposal = new ReplayOrderProposal(Id(1), Day, A);
        var order = ReplaySchedule.Calculate(history, proposal);
        Assert.Equal(6, order.Length); Assert.Equal(6, order.Distinct().Count());
        Before(order, ra.Id, proposal.Id); Before(order, proposal.Id, t.Id); Before(order, rb.Id, t.Id);
        Before(order, t.Id, u.Id); Before(order, rc.Id, u.Id);
        Assert.Equal(order.ToArray(), ReplaySchedule.Calculate(history.Reverse().ToImmutableArray(), proposal).ToArray());
        Assert.Equal([u, rb, t, rc, ra], history.ToArray());
    }

    [Fact]
    public void Same_date_proposal_follows_all_existing_position_ties_even_when_its_identity_sorts_first()
    {
        var ra = Receipt(10, A, 1); var rb = Receipt(20, B, 1); var t = Transfer(30, A, 2, B, 2);
        var issue = new ReplayOrderGroup(Id(40), Day, CostOperationKind.Issue, [new(A, 3)]);
        var proposal = new ReplayOrderProposal(Id(1), Day, A);
        Assert.Equal([ra.Id, rb.Id, t.Id, issue.Id, proposal.Id], ReplaySchedule.Calculate([issue, t, rb, ra], proposal).ToArray());
    }

    [Fact]
    public void Earlier_proposal_precedes_the_first_affected_event_without_reordering_existing_history()
    {
        var ra = Receipt(10, A, 1); var issue = new ReplayOrderGroup(Id(20), Day.AddDays(1), CostOperationKind.Issue, [new(A, 2)]);
        var proposal = new ReplayOrderProposal(Id(30), Day.AddDays(-1), A);
        Assert.Equal([proposal.Id, ra.Id, issue.Id], ReplaySchedule.Calculate([issue, ra], proposal).ToArray());
    }

    [Fact]
    public void Transfer_reversal_keeps_both_positions_and_original_dependency_once()
    {
        var ra = Receipt(10, A, 1); var rb = Receipt(20, B, 1); var t = Transfer(30, A, 2, B, 2);
        var reversal = new ReplayOrderGroup(Id(40), Day, CostOperationKind.Reversal, [new(B, 3), new(A, 3)], t.Id);
        Assert.Equal([ra.Id, rb.Id, t.Id, reversal.Id], ReplaySchedule.Calculate([reversal, rb, t, ra]).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_reversal_with_missing_or_unrelated_position_is_not_scheduled(bool wrongPosition)
    {
        var ra = Receipt(10, A, 1); var rb = Receipt(20, B, 1); var t = Transfer(30, A, 2, B, 2);
        ImmutableArray<ReplayOrderLeg> legs = wrongPosition ? [new(A, 3), new(C, 1)] : [new(A, 3)];
        Inconsistent([ra, rb, t, new(Id(40), Day, CostOperationKind.Reversal, legs, t.Id)]);
    }

    [Fact]
    public void Missing_origin_double_reversal_and_reversal_of_reversal_are_refused()
    {
        var receipt = Receipt(10, A, 1);
        var reversal = new ReplayOrderGroup(Id(20), Day, CostOperationKind.Reversal, [new(A, 2)], receipt.Id);
        Inconsistent([receipt, reversal with { Reverses = Id(99) }]);
        Inconsistent([receipt, reversal, new(Id(30), Day, CostOperationKind.Reversal, [new(A, 3)], receipt.Id)]);
        Inconsistent([receipt, reversal, new(Id(30), Day, CostOperationKind.Reversal, [new(A, 3)], reversal.Id)]);
    }

    [Fact]
    public void Cyclic_transfer_order_refuses_the_entire_graph_instead_of_sorting_away_the_conflict()
    {
        Inconsistent([Transfer(10, A, 1, B, 2), Transfer(20, A, 2, B, 1)]);
    }

    [Fact]
    public void Historical_dates_may_not_run_backwards_along_a_required_position_edge()
    {
        Inconsistent([Receipt(10, A, 1, 1), Receipt(20, A, 2, 0)]);
    }

    [Fact]
    public void Duplicate_or_gapped_ordinals_duplicate_legs_and_duplicate_events_are_not_compressed_silently()
    {
        var receipt = Receipt(10, A, 1);
        Inconsistent([receipt, Receipt(20, A, 1)]);
        Inconsistent([receipt, Receipt(20, A, 3)]);
        Inconsistent([receipt with { Legs = [new(A, 0)] }]);
        Inconsistent([Transfer(10, A, 1, A, 2)]);
        Inconsistent([receipt, receipt]);
    }

    [Fact]
    public void An_original_group_may_not_carry_a_reversal_link_and_invalid_transfer_shapes_are_refused()
    {
        var receipt = Receipt(10, A, 1);
        Inconsistent([receipt with { Reverses = Id(99) }]);
        Inconsistent([receipt with { Kind = CostOperationKind.Transfer }]);
        // Ordinary operations legitimately span positions; transfers still have exactly two legs.
        Inconsistent([receipt with { Kind = CostOperationKind.Transfer, Legs = [new(A, 1), new(B, 1), new(C, 1)] }]);
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate([receipt with { Kind = (CostOperationKind)99 }])).Code);
    }

    [Fact]
    public void Independent_ready_groups_use_culture_independent_canonical_identity_order()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            ImmutableArray<ReplayOrderGroup> history = [Receipt(30, C, 1), Receipt(10, A, 1), Receipt(20, B, 1)];
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var arabic = ReplaySchedule.Calculate(history);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal([Id(10), Id(20), Id(30)], arabic.ToArray());
            Assert.Equal(arabic.ToArray(), ReplaySchedule.Calculate(history.Reverse().ToImmutableArray()).ToArray());
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }

    [Fact]
    public void Empty_history_new_position_and_proposal_identity_validation_are_explicit()
    {
        Assert.Empty(ReplaySchedule.Calculate([]));
        var proposal = new ReplayOrderProposal(Id(1), Day, A);
        Assert.Equal([proposal.Id], ReplaySchedule.Calculate([], proposal).ToArray());
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate([Receipt(1, B, 1)], proposal)).Code);
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate([], proposal with { PositionId = Guid.Empty })).Code);
        Inconsistent(default);
    }

    [Fact]
    public void Oversized_histories_and_cancellation_do_not_return_partial_schedules()
    {
        var receipt = Receipt(1, A, 1);
        var tooMany = Enumerable.Repeat(receipt, 100_001).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate(tooMany)).Code);
        var tooManyPositions = Enumerable.Range(1, 10_001).Select(n => Receipt(n, Id(n + 100_000), 1)).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate(tooManyPositions)).Code);
        Assert.Throws<OperationCanceledException>(() => ReplaySchedule.Calculate([receipt], ct: new CancellationToken(true)));
    }

    [Theory]
    [InlineData(CostOperationKind.Receipt)]
    [InlineData(CostOperationKind.Issue)]
    public void Multiline_ranges_and_multiposition_proposals_are_indivisible(CostOperationKind kind)
    {
        var block = new ReplayOrderGroup(Id(20), Day, kind, [new(A, 1, 3), new(B, 1, 2)]);
        var transfer = Transfer(30, A, 4, B, 3, 1);
        var proposal = new ReplayOrderInsertion(Id(1), Day, [new(A, 2), new(B, 1)]);
        Assert.Equal([block.Id, proposal.Id, transfer.Id], ReplaySchedule.CalculateInsertion([transfer, block], proposal).ToArray());
        Assert.Equal([block.Id, transfer.Id], ReplaySchedule.Calculate([transfer, block]).ToArray());
        Assert.Equal([new ReplayOrderLeg(A, 1, 3), new ReplayOrderLeg(B, 1, 2)], block.Legs.ToArray());
    }

    [Fact]
    public void Ranges_cannot_overlap_have_gaps_or_interleave_another_group()
    {
        var block = Receipt(10, A, 1) with { Legs = [new(A, 1, 3)] };
        Inconsistent([block, Receipt(20, A, 3)]);
        Inconsistent([block, Receipt(20, A, 5)]);
        Inconsistent([block with { Legs = [new(A, 1, 0)] }]);
        Inconsistent([block with { Legs = [new(A, 1, -1)] }]);
        Inconsistent([block with { Legs = [new(A, long.MaxValue, 2)] }]);
        Inconsistent([block with { Legs = [new(A, long.MaxValue, 1)] }]);
        Inconsistent([block with { Legs = [new(A, 1, 2), new(B, 3, 2)] },
            new(Id(20), Day, CostOperationKind.Issue, [new(A, 3, 2), new(B, 1, 2)])]);
        Inconsistent([Transfer(10, A, 1, B, 1) with { Legs = [new(A, 1, 2), new(B, 1)] }]);
    }

    [Fact]
    public void A_whole_multiline_reversal_matches_every_original_position_and_entry_count()
    {
        var original = Receipt(10, A, 1) with { Legs = [new(A, 1, 3), new(B, 1, 2), new(C, 1)] };
        var reversal = new ReplayOrderGroup(Id(20), Day, CostOperationKind.Reversal, [new(C, 2), new(B, 3, 2), new(A, 4, 3)], original.Id);
        Assert.Equal([original.Id, reversal.Id], ReplaySchedule.Calculate([reversal, original]).ToArray());
        Inconsistent([original, reversal with { Legs = [new(A, 4, 2), new(B, 3, 2), new(C, 2)] }]);
    }

    [Fact]
    public void Bounds_count_individual_entries_including_proposed_ranges()
    {
        var block = Receipt(10, A, 1) with { Legs = [new(A, 1, 200_000)] };
        Assert.Equal([block.Id], ReplaySchedule.Calculate([block]).ToArray());
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate([block with { Legs = [new(A, 1, 200_001)] }])).Code);
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => ReplaySchedule.Calculate([block with { Legs = [new(A, 1, long.MaxValue)] }])).Code);
        var proposal = new ReplayOrderInsertion(Id(1), Day, [new(A)]);
        var smaller = block with { Legs = [new(A, 1, 199_999)] };
        Assert.Equal([smaller.Id, proposal.Id], ReplaySchedule.CalculateInsertion([smaller], proposal).ToArray());
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => ReplaySchedule.CalculateInsertion([smaller], proposal with { Legs = [new(A, 2)] })).Code);
    }

    [Fact]
    public void Proposal_positions_and_counts_must_be_complete_distinct_and_bounded()
    {
        var proposal = new ReplayOrderInsertion(Id(1), Day, [new(A), new(B)]);
        Assert.Equal([proposal.Id], ReplaySchedule.CalculateInsertion([], proposal).ToArray());
        foreach (var legs in new ImmutableArray<ReplayOrderProposalLeg>[] { [], default, [new(A), new(A)], [new(A, 0)], [new(Guid.Empty)] })
            Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => ReplaySchedule.CalculateInsertion([], proposal with { Legs = legs })).Code);
        var positions = Enumerable.Range(1, 10_001).Select(n => new ReplayOrderProposalLeg(Id(n + 100_000))).ToImmutableArray();
        Assert.Equal("impact_history_too_large", Assert.Throws<CostingException>(() => ReplaySchedule.CalculateInsertion([], proposal with { Legs = positions })).Code);
        Assert.Throws<OperationCanceledException>(() => ReplaySchedule.CalculateInsertion([], proposal, new CancellationToken(true)));
    }
}
