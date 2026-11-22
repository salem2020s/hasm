using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hasm.Infrastructure.Costing;

internal sealed record ProposedFlowLine(Guid WarehouseId, decimal Quantity, decimal? ReceiptAmount = null,
    Guid? DestinationWarehouseId = null);
internal sealed record ProposedFlowOperation(Guid Id, DateOnly EffectiveDate, CostOperationKind Kind,
    ImmutableArray<ProposedFlowLine> Lines);
internal sealed record CostFlowEntryImpact(long EntryId, Guid OperationId, Guid WarehouseId,
    decimal OriginalAmount, decimal ProposedAmount, decimal OriginalQuantityAfter, decimal ProposedQuantityAfter,
    decimal OriginalValueAfter, decimal ProposedValueAfter);
internal sealed record CostFlowPositionImpact(Guid WarehouseId, decimal OriginalQuantity, decimal ProposedQuantity,
    decimal OriginalValue, decimal ProposedValue);
internal sealed record CostFlowComparisonResult(string HistoryFingerprint, string ComparisonFingerprint,
    ProposedFlowOperation Proposal, ImmutableArray<ReplayFlowStep> ProposedSteps,
    ImmutableArray<CostFlowEntryImpact> Changes, ImmutableArray<CostFlowPositionImpact> Positions, ReplayFlowResult RevisedTrace);

/// <summary>
/// Pure internal ordinary component comparison. No API, database, DI registration, approval or execution.
/// Rebuilds baseline evidence; the caller must separately prove an authorized complete read set and
/// all period/source/legal/approval rules. Simulation differences are NOT accounting adjustments.
/// </summary>
internal static class CostFlowComparison
{
    private static CostingException Invalid() => new("invalid_replay_input");
    private static CostingException Inconsistent() => new("impact_history_inconsistent");

    internal static CostFlowComparisonResult Calculate(CostFlowSnapshot snapshot, ProposedFlowOperation proposal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot is null || snapshot.Positions.IsDefaultOrEmpty || snapshot.Entries.IsDefault)
            throw new CostingException("invalid_impact_snapshot");
        if (proposal is null || proposal.Id == Guid.Empty || proposal.Lines.IsDefaultOrEmpty) throw Invalid();
        if (proposal.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer))
            throw new CostingException("impact_history_unsupported");
        if (proposal.Lines.Length > 500 || snapshot.Positions.Length > 10_000)
            throw new CostingException("impact_history_too_large");
        if (proposal.Kind == CostOperationKind.Transfer && proposal.Lines.Length != 1) throw Invalid();
        var entryCount = proposal.Kind == CostOperationKind.Transfer ? 2 : proposal.Lines.Length;
        if (snapshot.Entries.Length > 200_000 - entryCount) throw new CostingException("impact_history_too_large");
        // Reserve combined group/individual-entry bounds BEFORE either replay, not just revised math.
        var historicalGroups = new HashSet<Guid>();
        foreach (var entry in snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry is null) throw Inconsistent();
            historicalGroups.Add(entry.OperationId);
            if (historicalGroups.Count == 100_000) throw new CostingException("impact_history_too_large");
        }
        if (historicalGroups.Contains(proposal.Id)) throw Invalid();
        var positionIds = ImmutableArray.CreateBuilder<Guid>(snapshot.Positions.Length);
        foreach (var position in snapshot.Positions)
        {
            ct.ThrowIfCancellationRequested(); if (position is null) throw Inconsistent();
            positionIds.Add(position.WarehouseId);
        }
        var positions = positionIds.MoveToImmutable();
        var children = ImmutableArray.CreateBuilder<ReplayFlowChild>(proposal.Lines.Length);
        var counts = new Dictionary<Guid, long>();
        long Next(Guid position) { var value = counts.GetValueOrDefault(position) + 1; counts[position] = value; return value; }
        for (var n = 0; n < proposal.Lines.Length; n++)
        {
            ct.ThrowIfCancellationRequested(); var line = proposal.Lines[n];
            if (line is null || (proposal.Kind == CostOperationKind.Transfer ? line.DestinationWarehouseId is null : line.DestinationWarehouseId is not null)) throw Invalid();
            var id = proposal.Kind == CostOperationKind.Transfer ? proposal.Id : ChildId(proposal.Id, n + 1);
            children.Add(new(proposal.Id, n + 1, Next(line.WarehouseId),
                new(id, proposal.Kind, line.WarehouseId, line.Quantity, line.ReceiptAmount, line.DestinationWarehouseId),
                line.DestinationWarehouseId is { } target ? Next(target) : null));
        }
        var proposedChildren = children.MoveToImmutable();
        // Exactly the same quantity/amount/precision/endpoint/identity validator as the live replay core.
        CostReplay.ValidateProposedFlowInputs(snapshot.Method, snapshot.MoneyDecimals, positions, proposedChildren.Select(c => c.Event).ToImmutableArray(), ct);
        var proposedBlock = new ReplayFlowBlock(new(proposal.Id, proposal.EffectiveDate, proposal.Kind,
            counts.OrderBy(p => p.Key.ToString("N"), StringComparer.Ordinal).Select(p => new ReplayOrderLeg(p.Key, 1, p.Value)).ToImmutableArray()), proposedChildren);

        var baseline = CostFlowSnapshotService.RebuildForComparison(snapshot, ct);
        var original = new Dictionary<(Guid Event, Guid Warehouse), CostFlowEntry>();
        var reserved = new HashSet<Guid>(historicalGroups);
        foreach (var entry in baseline.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var id = entry.OperationKind == CostOperationKind.Transfer ? entry.OperationId : CostHistoryEntry.SimulationId(entry.Id);
            if (!original.TryAdd((id, entry.WarehouseId), entry)) throw Inconsistent();
            reserved.Add(id);
        }
        foreach (var binding in baseline.LayerBindings) { ct.ThrowIfCancellationRequested(); reserved.Add(binding.SimulationId); }
        if (!reserved.Add(proposal.Id)) throw Invalid();
        var proposedIds = new HashSet<Guid>();
        foreach (var child in proposedChildren)
        {
            ct.ThrowIfCancellationRequested();
            if (!proposedIds.Add(child.Event.Id) || proposal.Kind != CostOperationKind.Transfer && !reserved.Add(child.Event.Id)) throw Invalid();
        }
        var revised = CostReplay.CalculateBlocksWithInsertion(baseline.Method, baseline.MoneyDecimals, positions, baseline.Blocks, proposedBlock, ct).Flow;
        foreach (var layer in revised.CreatedLayers)
        {
            ct.ThrowIfCancellationRequested();
            if (proposal.Kind != CostOperationKind.Transfer && layer.LayerId == proposal.Id) throw Invalid();
        }
        var changes = ImmutableArray.CreateBuilder<CostFlowEntryImpact>();
        var proposedSteps = ImmutableArray.CreateBuilder<ReplayFlowStep>(proposal.Lines.Length);
        var seen = new HashSet<long>();
        foreach (var step in revised.Steps)
        {
            ct.ThrowIfCancellationRequested();
            if (proposedIds.Contains(step.EventId)) { proposedSteps.Add(step); continue; }
            foreach (var leg in step.Legs)
            {
                ct.ThrowIfCancellationRequested();
                if (!original.TryGetValue((step.EventId, leg.PositionId), out var entry) || !seen.Add(entry.Id)) throw Inconsistent();
                var next = leg.Step;
                if ((entry.Amount, entry.QuantityAfter, entry.ValueAfter) != (next.Amount, next.QuantityAfter, next.ValueAfter))
                    changes.Add(new(entry.Id, entry.OperationId, entry.WarehouseId, entry.Amount, next.Amount,
                        entry.QuantityAfter, next.QuantityAfter, entry.ValueAfter, next.ValueAfter));
            }
        }
        if (seen.Count != baseline.Entries.Length || proposedSteps.Count != proposal.Lines.Length) throw Inconsistent();
        var finals = revised.Positions.ToDictionary(p => p.PositionId);
        var positionChanges = ImmutableArray.CreateBuilder<CostFlowPositionImpact>(baseline.Positions.Length);
        foreach (var position in baseline.Positions)
        {
            ct.ThrowIfCancellationRequested();
            if (!finals.Remove(position.WarehouseId, out var next)) throw Inconsistent();
            positionChanges.Add(new(position.WarehouseId, position.Quantity, next.Quantity, position.Value, next.Value));
        }
        if (finals.Count != 0) throw Inconsistent();
        // Binds exact proposal payload + already reconciled history + algorithm contract version.
        // It is reproducibility evidence, never a source revision or approval/execution token.
        ct.ThrowIfCancellationRequested();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Version = "cost-flow-comparison-v1", History = baseline.HistoryFingerprint, Proposal = proposal }))).ToLowerInvariant();
        var immutableChanges = changes.OrderBy(c => c.EntryId).ToImmutableArray();
        var immutableProposedSteps = proposedSteps.MoveToImmutable();
        var immutablePositions = positionChanges.MoveToImmutable();
        ct.ThrowIfCancellationRequested();
        return new(baseline.HistoryFingerprint, fingerprint, proposal, immutableProposedSteps,
            immutableChanges, immutablePositions, revised);
    }

    private static Guid ChildId(Guid proposal, int line)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes("hasm-flow-proposal-line-v1/" + proposal.ToString("N") + "/" +
            line.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));
}
