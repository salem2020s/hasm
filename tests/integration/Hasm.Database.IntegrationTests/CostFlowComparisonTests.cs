using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;

namespace Hasm.Database.IntegrationTests;

public class CostFlowComparisonTests : CostingTestBase
{
    private async Task<CostFlowSnapshot> Snapshot(Guid item, params Guid[] roots)
    {
        var result = await new CostFlowSnapshotService(Resolver, Rbac).LoadAsync(Admin.Id, Company.Id, item,
            (roots.Length == 0 ? new[] { W1.Id } : roots).ToImmutableArray());
        Assert.True(result.IsOk, result.Code); return result.Value!;
    }
    private async Task<long[]> Footprint() =>
        [await Count(CompanySchema.CostPosition.Table), await Count(CompanySchema.CostOperation.Table),
         await Count(CompanySchema.CostEntry.Table), await Count(CompanySchema.CostLayer.Table),
         await Count(CompanySchema.CostConsumption.Table), await Count(CompanySchema.BusinessAudit.Table),
         await Count(CompanySchema.JournalEntry.Table), await Count(CompanySchema.JournalLine.Table)];
    private static ProposedFlowOperation Proposal(CostOperationKind kind, DateOnly date, params ProposedFlowLine[] lines) =>
        new(Guid.NewGuid(), date, kind, lines.ToImmutableArray());
    private static CostFlowPositionImpact Position(CostFlowComparisonResult result, Guid warehouse) => Assert.Single(result.Positions, p => p.WarehouseId == warehouse);
    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<CostingException>(action).Code);

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Backdated_receipt_reprices_every_chained_transfer_leg_and_downstream_issue(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg; var w3 = await NewWarehouse("W3");
        await Receive(item, 10, 100); var first = await Move(item, 6, date: D2);
        var second = await Move(item, 4, W2, w3, D3); var issue = await IssueOf(item, 2, w3, D3);
        var snapshot = await Snapshot(item.Id); var before = await Footprint();
        var proposal = Proposal(CostOperationKind.Receipt, D1.AddDays(-1), new ProposedFlowLine(W1.Id, 10, 300));
        var result = CostFlowComparison.Calculate(snapshot, proposal);
        Assert.Equal(6, result.Changes.Length);
        foreach (var (operation, cost) in new[] { (first.OperationId, method == CostMethod.Fifo ? 180m : 120m), (second.OperationId, method == CostMethod.Fifo ? 120m : 80m) })
        {
            var legs = result.Changes.Where(c => c.OperationId == operation).ToArray(); Assert.Equal(2, legs.Length);
            Assert.All(legs, l => Assert.Equal(cost, l.ProposedAmount));
        }
        Assert.Equal(method == CostMethod.Fifo ? 60m : 40m, Assert.Single(result.Changes, c => c.OperationId == issue.OperationId).ProposedAmount);
        var source = Position(result, W1.Id); Assert.Equal((4m, 14m, 40m, method == CostMethod.Fifo ? 220m : 280m),
            (source.OriginalQuantity, source.ProposedQuantity, source.OriginalValue, source.ProposedValue));
        Assert.Equal(method == CostMethod.Fifo ? 60m : 40m, Position(result, W2.Id).ProposedValue);
        Assert.Equal(method == CostMethod.Fifo ? 60m : 40m, Position(result, w3.Id).ProposedValue);
        var repeat = CostFlowComparison.Calculate(snapshot, proposal);
        Assert.Equal(result.ComparisonFingerprint, repeat.ComparisonFingerprint);
        Assert.Equal(result.ProposedSteps.Select(s => s.EventId), repeat.ProposedSteps.Select(s => s.EventId));
        Assert.Equal(snapshot.HistoryFingerprint, (await Snapshot(item.Id)).HistoryFingerprint);
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Backdated_issue_includes_trajectory_changes_even_when_later_cost_is_unchanged(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 10, 100); var transfer = await Move(item, 8, date: D2); await IssueOf(item, 4, W2, D3);
        var snapshot = await Snapshot(item.Id); var before = await Footprint();
        var result = CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Issue, D1, new ProposedFlowLine(W1.Id, 1)));
        Assert.Equal(10m, Assert.Single(result.ProposedSteps).Legs[0].Step.Amount);
        var change = Assert.Single(result.Changes); Assert.Equal(transfer.OperationId, change.OperationId);
        Assert.Equal((80m, 80m, 2m, 1m, 20m, 10m),
            (change.OriginalAmount, change.ProposedAmount, change.OriginalQuantityAfter, change.ProposedQuantityAfter, change.OriginalValueAfter, change.ProposedValueAfter));
        Assert.Equal((4m, 4m, 40m, 40m), (Position(result, W2.Id).OriginalQuantity, Position(result, W2.Id).ProposedQuantity,
            Position(result, W2.Id).OriginalValue, Position(result, W2.Id).ProposedValue));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task A_shortage_in_a_later_transfer_refuses_the_entire_comparison(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 10, 100); await Move(item, 8, date: D2); await IssueOf(item, 4, W2, D3);
        var snapshot = await Snapshot(item.Id); var before = await Footprint();
        Error("insufficient_stock", () => CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Issue, D1, new ProposedFlowLine(W1.Id, 3))));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Proposed_transfer_joins_disconnected_histories_without_inventing_a_client_transfer_price(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 2, 2); await Receive(item, 2, 6, W2); var issued = await IssueOf(item, 2, W2, D3);
        var proposal = Proposal(CostOperationKind.Transfer, D2, new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W2.Id));
        var incomplete = await Snapshot(item.Id);
        Error("invalid_replay_input", () => CostFlowComparison.Calculate(incomplete, proposal));
        var snapshot = await Snapshot(item.Id, W1.Id, W2.Id); var before = await Footprint();
        var result = CostFlowComparison.Calculate(snapshot, proposal);
        Assert.All(Assert.Single(result.ProposedSteps).Legs, l => Assert.Equal(1m, l.Step.Amount));
        var issue = Assert.Single(result.Changes); Assert.Equal(issued.OperationId, issue.OperationId);
        Assert.Equal(method == CostMethod.Fifo ? 6m : 4.67m, issue.ProposedAmount);
        Assert.Equal((1m, method == CostMethod.Fifo ? 1m : 2.33m), (Position(result, W2.Id).ProposedQuantity, Position(result, W2.Id).ProposedValue));
        Assert.Equal((1m, 1m), (Position(result, W1.Id).ProposedQuantity, Position(result, W1.Id).ProposedValue));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Proposed_transfer_to_an_empty_destination_creates_only_simulated_positions(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg; await Receive(item, 2, 2);
        var snapshot = await Snapshot(item.Id, W1.Id, W2.Id); var before = await Footprint();
        var result = CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Transfer, D2, new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W2.Id)));
        Assert.Empty(result.Changes); Assert.Equal(2, result.Positions.Length);
        Assert.All(result.Positions, p => Assert.Equal((1m, 1m), (p.ProposedQuantity, p.ProposedValue)));
        Assert.Null(Assert.Single(snapshot.Positions, p => p.WarehouseId == W2.Id).PositionId);
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Multiline_proposal_preserves_each_receipt_layer_and_an_unpriced_free_destination(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 3, 9, date: D2); var issued = await IssueOf(item, 2, date: D3);
        var snapshot = await Snapshot(item.Id, W1.Id, W2.Id); var before = await Footprint();
        var result = CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Receipt, D1,
            new ProposedFlowLine(W1.Id, 1, 1), new ProposedFlowLine(W1.Id, 2, 100), new ProposedFlowLine(W2.Id, 1, 0)));
        Assert.Equal(new[] { 1m, 100m, 0m }, result.ProposedSteps.Select(s => s.Legs[0].Step.Amount));
        Assert.Equal(method == CostMethod.Fifo ? 51m : 36.67m, Assert.Single(result.Changes, c => c.OperationId == issued.OperationId).ProposedAmount);
        Assert.Equal((4m, method == CostMethod.Fifo ? 59m : 73.33m), (Position(result, W1.Id).ProposedQuantity, Position(result, W1.Id).ProposedValue));
        Assert.Equal((1m, 0m), (Position(result, W2.Id).ProposedQuantity, Position(result, W2.Id).ProposedValue));
        if (method == CostMethod.Fifo) Assert.Single(Assert.Single(result.RevisedTrace.Positions, p => p.PositionId == W2.Id).OpenLayers);
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Same_date_proposal_follows_history_on_both_transfer_endpoints(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 2, 2); await Receive(item, 1, 3, W2); await IssueOf(item, 1, W2);
        var snapshot = await Snapshot(item.Id, W1.Id, W2.Id); var before = await Footprint();
        var result = CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Transfer, D1, new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W2.Id)));
        Assert.Empty(result.Changes); Assert.All(Assert.Single(result.ProposedSteps).Legs, l => Assert.Equal(1m, l.Step.Amount));
        Assert.Equal((1m, 1m), (Position(result, W2.Id).ProposedQuantity, Position(result, W2.Id).ProposedValue));
        Assert.Equal(before, await Footprint());
    }

    [PostgresTheory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public async Task Backdated_free_stock_replays_returning_transfers_without_reusing_old_layers(CostMethod method)
    {
        await PrepareCosting(); var item = method == CostMethod.Fifo ? Fifo : Avg;
        await Receive(item, 4, 4); await Receive(item, 4, 12); await Move(item, 6, date: D2);
        await Move(item, 2, W2, W1, D3); var issued = await IssueOf(item, 3, date: D3);
        var snapshot = await Snapshot(item.Id); var before = await Footprint();
        var result = CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Receipt, D1.AddDays(-1), new ProposedFlowLine(W1.Id, 4, 0)));
        Assert.Equal(method == CostMethod.Fifo ? 5m : 4m, Assert.Single(result.Changes, c => c.OperationId == issued.OperationId).ProposedAmount);
        Assert.Equal((5m, method == CostMethod.Fifo ? 9m : 6.67m), (Position(result, W1.Id).ProposedQuantity, Position(result, W1.Id).ProposedValue));
        Assert.Equal((4m, method == CostMethod.Fifo ? 2m : 5.33m), (Position(result, W2.Id).ProposedQuantity, Position(result, W2.Id).ProposedValue));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }

    [PostgresFact]
    public async Task Invalid_proposals_and_counterfeit_evidence_cannot_return_partial_comparisons()
    {
        await PrepareCosting(); await Receive(Fifo, 3, 9); await IssueOf(Fifo, 1);
        var snapshot = await Snapshot(Fifo.Id); var valid = Proposal(CostOperationKind.Receipt, D2, new ProposedFlowLine(W1.Id, 1, 1));
        var before = await Footprint();
        foreach (var invalid in new[] { valid with { Id = Guid.Empty }, valid with { Id = snapshot.Entries[0].OperationId },
                     valid with { Id = snapshot.LayerBindings[0].SimulationId }, valid with { Lines = [] },
                     Proposal(CostOperationKind.Receipt, D2, new ProposedFlowLine(W1.Id, 0, 1)),
                     Proposal(CostOperationKind.Issue, D2, new ProposedFlowLine(W1.Id, 1, 1)),
                     Proposal(CostOperationKind.Transfer, D2, new ProposedFlowLine(W1.Id, 1, 1, W2.Id)),
                     Proposal(CostOperationKind.Receipt, D2, new ProposedFlowLine(W1.Id, 1, 1, W2.Id)),
                     Proposal(CostOperationKind.Transfer, D2, new ProposedFlowLine(W1.Id, 1, DestinationWarehouseId: W1.Id)) })
            Error("invalid_replay_input", () => CostFlowComparison.Calculate(snapshot, invalid));
        Error("precision_exceeded", () => CostFlowComparison.Calculate(snapshot, Proposal(CostOperationKind.Receipt, D2, new ProposedFlowLine(W1.Id, 1, 0.001m))));
        Error("impact_history_unsupported", () => CostFlowComparison.Calculate(snapshot, valid with { Kind = CostOperationKind.Reversal }));
        Error("impact_history_inconsistent", () => CostFlowComparison.Calculate(snapshot with { HistoryFingerprint = "changed" }, valid));
        Error("impact_history_inconsistent", () => CostFlowComparison.Calculate(snapshot with
            { Entries = snapshot.Entries.SetItem(0, snapshot.Entries[0] with { Amount = 10 }) }, valid));
        Error("impact_history_too_large", () => CostFlowComparison.Calculate(snapshot, valid with
            { Lines = Enumerable.Repeat(valid.Lines[0], 501).ToImmutableArray() }));
        Error("impact_history_too_large", () => CostFlowComparison.Calculate(snapshot with
            { Entries = Enumerable.Repeat(snapshot.Entries[0], 200_000).ToImmutableArray() }, valid));
        var expected = CostFlowComparison.Calculate(snapshot, valid);
        var rebuilt = CostFlowComparison.Calculate(snapshot with { Blocks = [], LayerBindings = [] }, valid);
        Assert.Equal(expected.ComparisonFingerprint, rebuilt.ComparisonFingerprint);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CostFlowComparison.Calculate(snapshot, valid, cancelled.Token));
        Assert.Equal(before, await Footprint()); await AssertConsistent();
    }
}
