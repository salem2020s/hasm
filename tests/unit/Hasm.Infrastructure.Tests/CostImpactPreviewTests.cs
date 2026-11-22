using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class CostImpactPreviewTests
{
    private static readonly DateOnly Day = new(2026, 1, 10);
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static CostHistorySnapshot Snapshot(CostMethod method, params (int Day, CostOperationKind Kind, decimal Qty, decimal? Amount)[] inputs)
    {
        var entries = inputs.Select((e, n) => new CostHistoryEntry(n + 1, Id(n + 1), n + 1, Day.AddDays(e.Day), e.Kind, e.Qty, e.Amount ?? 0, 0, 0, null, null)).ToImmutableArray();
        var original = CostReplay.Calculate(method, 2, entries.Select(e => e.ToReplayEvent()).ToImmutableArray());
        entries = entries.Select((e, n) => e with { Amount = original.Steps[n].Amount, QuantityAfter = original.Steps[n].QuantityAfter, ValueAfter = original.Steps[n].ValueAfter }).ToImmutableArray();
        return new CostHistorySnapshot(Id(100), Id(101), Id(102), entries.Length == 0 ? null : Id(103), method, "USD", 2, entries.Length,
            original.QuantityAfter, original.ValueAfter, entries, "limited-history-evidence");
    }

    [Theory]
    [InlineData(CostMethod.Fifo, 50, 210)]
    [InlineData(CostMethod.MovingAverage, 65, 195)]
    public void Backdated_receipt_compares_every_original_event_and_conserves_value(CostMethod method, decimal issueCost, decimal remaining)
    {
        var original = Snapshot(method, (0, CostOperationKind.Receipt, 10, 100), (2, CostOperationKind.Issue, 5, null));
        var proposal = new ProposedCostEvent(Id(200), Day.AddDays(1), CostOperationKind.Receipt, 10, 160);
        var result = CostImpactPreview.Calculate(original, proposal);
        var issue = Assert.Single(result.Changes);
        Assert.Equal(2, issue.EntryId); Assert.Equal(Id(2), issue.OperationId);
        Assert.Equal(50, issue.OriginalAmount); Assert.Equal(issueCost, issue.ProposedAmount);
        Assert.Equal((5m, 15m), (issue.OriginalQuantityAfter, issue.ProposedQuantityAfter));
        Assert.Equal((50m, remaining), (issue.OriginalValueAfter, issue.ProposedValueAfter));
        Assert.Equal((5m, 15m), (result.OriginalQuantity, result.ProposedQuantity));
        Assert.Equal(260m, result.ProposedValue + issue.ProposedAmount);
        Assert.Equal(50m, original.Value); Assert.Equal(50m, original.Entries[1].Amount);
        Assert.Equal(original.HistoryFingerprint, result.HistoryFingerprint);
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Same_date_proposal_follows_existing_sequence_and_does_not_reprice_prior_issue(CostMethod method)
    {
        var original = Snapshot(method, (0, CostOperationKind.Receipt, 10, 100), (0, CostOperationKind.Issue, 5, null));
        var result = CostImpactPreview.Calculate(original, new ProposedCostEvent(Id(200), Day, CostOperationKind.Receipt, 10, 160));
        Assert.Empty(result.Changes);
        Assert.Equal((15m, 210m), (result.ProposedQuantity, result.ProposedValue));
        Assert.Equal((15m, 210m), (result.ProposedStep.QuantityAfter, result.ProposedStep.ValueAfter));
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Backdated_issue_may_not_create_a_shortage_in_any_later_event(CostMethod method)
    {
        var original = Snapshot(method, (0, CostOperationKind.Receipt, 10, 100), (2, CostOperationKind.Issue, 10, null));
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(original,
            new ProposedCostEvent(Id(200), Day.AddDays(1), CostOperationKind.Issue, 1))).Code);
        Assert.Equal((0m, 0m), (original.Quantity, original.Value));
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void Proposal_before_first_receipt_refuses_an_issue_and_accepts_an_exact_free_receipt(CostMethod method)
    {
        var original = Snapshot(method, (0, CostOperationKind.Receipt, 3, 100), (1, CostOperationKind.Issue, 3, null));
        Assert.Equal("insufficient_stock", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(original,
            new ProposedCostEvent(Id(200), Day.AddDays(-1), CostOperationKind.Issue, 1))).Code);
        var result = CostImpactPreview.Calculate(original, new ProposedCostEvent(Id(200), Day.AddDays(-1), CostOperationKind.Receipt, 1, 0));
        Assert.Equal(1m, result.ProposedQuantity);
        Assert.Equal(100m, result.ProposedValue + result.Changes.Single(c => c.EntryId == 2).ProposedAmount);
        Assert.Equal(0m, result.ProposedStep.Amount);
    }

    [Fact]
    public void Empty_history_and_canceled_calculation_have_no_side_effects()
    {
        var original = Snapshot(CostMethod.Fifo);
        var proposed = new ProposedCostEvent(Id(200), Day, CostOperationKind.Receipt, 3, 100);
        var result = CostImpactPreview.Calculate(original, proposed);
        Assert.Empty(result.Changes); Assert.Equal((3m, 100m), (result.ProposedQuantity, result.ProposedValue));
        Assert.Empty(original.Entries);
        Assert.Throws<OperationCanceledException>(() => CostImpactPreview.Calculate(original, proposed, new CancellationToken(true)));
    }

    [Fact]
    public void Inconsistent_original_amounts_sequences_dates_or_totals_are_not_used_as_a_baseline()
    {
        var original = Snapshot(CostMethod.Fifo, (0, CostOperationKind.Receipt, 10, 100), (1, CostOperationKind.Issue, 5, null));
        var proposed = new ProposedCostEvent(Id(200), Day, CostOperationKind.Receipt, 1, 1);
        foreach (var changed in new[] {
            original with { Value = 49 },
            original with { Entries = original.Entries.SetItem(1, original.Entries[1] with { Amount = 49 }) },
            original with { Entries = original.Entries.SetItem(1, original.Entries[1] with { Sequence = 3 }) },
            original with { Entries = original.Entries.SetItem(1, original.Entries[1] with { EffectiveDate = Day.AddDays(-1) }) } })
            Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(changed, proposed)).Code);
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(original with { LastSequence = 1 }, proposed)).Code);
    }

    [Theory]
    [InlineData(CostOperationKind.Transfer)]
    [InlineData(CostOperationKind.Reversal)]
    public void Unsupported_history_or_proposal_is_refused_in_full(CostOperationKind unsupported)
    {
        var original = Snapshot(CostMethod.Fifo, (0, CostOperationKind.Receipt, 10, 100));
        var proposed = new ProposedCostEvent(Id(200), Day, unsupported, 1);
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(original, proposed)).Code);
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(
            original with { Entries = original.Entries.SetItem(0, original.Entries[0] with { Kind = unsupported }) }, proposed with { Kind = CostOperationKind.Issue })).Code);
    }

    [Fact]
    public void Proposed_identity_cannot_alias_an_existing_simulation_entry()
    {
        var original = Snapshot(CostMethod.Fifo, (0, CostOperationKind.Receipt, 10, 100));
        Assert.Equal("invalid_replay_input", Assert.Throws<CostingException>(() => CostImpactPreview.Calculate(original,
            new ProposedCostEvent(original.Entries[0].ToReplayEvent().Id, Day, CostOperationKind.Receipt, 1, 1))).Code);
    }
}
