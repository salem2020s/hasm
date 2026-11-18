using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Hasm.Infrastructure.Costing;

internal sealed record ReplayFlowEvent(Guid Id, CostOperationKind Kind, Guid PositionId, decimal Quantity,
    decimal? ReceiptAmount = null, Guid? DestinationPositionId = null);
internal sealed record ReplayFlowLeg(Guid PositionId, ReplayStep Step);
internal sealed record ReplayFlowStep(Guid EventId, CostOperationKind Kind, ImmutableArray<ReplayFlowLeg> Legs);
internal sealed record ReplayFlowPosition(Guid PositionId, decimal Quantity, decimal Value, ImmutableArray<CostingMath.LayerState> OpenLayers);
internal sealed record ReplayFlowLayer(Guid PositionId, Guid EventId, Guid LayerId, Guid? SourceLayerId,
    int Component, BigInteger Quantity, BigInteger Amount);
internal sealed record ReplayFlowResult(ImmutableArray<ReplayFlowStep> Steps, ImmutableArray<ReplayFlowPosition> Positions,
    ImmutableArray<ReplayFlowLayer> CreatedLayers);

internal static partial class CostReplay
{
    /// <summary>
    /// Pure internal arithmetic for one item's ALREADY ORDERED and completely classified ordinary
    /// receipt/issue/transfer history, from empty positions under one common method/currency.
    /// Caller must prove authorized closure, provenance, ordering and absence of unsupported epochs,
    /// reversals, adjustments and negative obligations. No snapshot, production caller or execution.
    /// Synthetic layer IDs are simulation evidence, never persisted-layer replacements.
    /// </summary>
    internal static ReplayFlowResult CalculateFlow(CostMethod method, int moneyDecimals, ImmutableArray<Guid> positions,
        ImmutableArray<ReplayFlowEvent> events, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (method is not (CostMethod.Fifo or CostMethod.MovingAverage) || moneyDecimals is < 0 or > CostingMath.MaxMoneyDecimals ||
            positions.IsDefault || events.IsDefault) throw new CostingException("invalid_replay_input");
        if (positions.Length > 10_000 || events.Length > 100_000) throw new CostingException("impact_history_too_large");
        if (events.Any(e => e is null || e.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer)))
            throw new CostingException("impact_history_unsupported");
        var positionIds = new HashSet<Guid>();
        foreach (var id in positions)
        {
            ct.ThrowIfCancellationRequested();
            if (id == Guid.Empty || !positionIds.Add(id)) throw new CostingException("invalid_replay_input");
        }
        var eventIds = new HashSet<Guid>();
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            if (!positionIds.Contains(e.PositionId) || (e.Kind == CostOperationKind.Transfer
                ? e.DestinationPositionId is not { } target || target == e.PositionId || !positionIds.Contains(target)
                : e.DestinationPositionId is not null)) throw new CostingException("invalid_replay_input");
            ValidateEvent(new ReplayEvent(e.Id, e.Kind == CostOperationKind.Transfer ? CostOperationKind.Issue : e.Kind,
                e.Quantity, e.ReceiptAmount), eventIds, moneyDecimals);
        }

        // Shared across ALL positions: every FIFO creation and consumption edge, even zero value,
        // reserves before allocating its state/plan and retained evidence. Average output is bounded
        // separately by the group/position limits. A failed calculation exposes no partial state.
        var used = 0;
        void Reserve()
        {
            ct.ThrowIfCancellationRequested();
            if (used == 400_000) throw new CostingException("impact_history_too_large");
            used++;
        }
        var states = positions.ToDictionary(id => id, _ => new PositionState(method, moneyDecimals, Reserve));
        var usedLayerIds = new HashSet<Guid>(eventIds); // Also catches collisions with future receipts.
        var created = ImmutableArray.CreateBuilder<ReplayFlowLayer>();
        var steps = ImmutableArray.CreateBuilder<ReplayFlowStep>(events.Length);
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            var source = states[e.PositionId];
            var outward = source.AppendValidated(new ReplayEvent(e.Id,
                e.Kind == CostOperationKind.Transfer ? CostOperationKind.Issue : e.Kind, e.Quantity, e.ReceiptAmount), ct);
            var sourceLeg = new ReplayFlowLeg(e.PositionId, outward);
            if (e.Kind == CostOperationKind.Receipt && method == CostMethod.Fifo)
                created.Add(new ReplayFlowLayer(e.PositionId, e.Id, e.Id, null, 1,
                    CostingMath.ToUnits(e.Quantity, CostingMath.QuantityScale), CostingMath.ToUnits(e.ReceiptAmount!.Value, CostingMath.AmountScale)));
            if (e.Kind != CostOperationKind.Transfer)
            {
                steps.Add(new ReplayFlowStep(e.Id, e.Kind, [sourceLeg]));
                continue;
            }

            var destination = e.DestinationPositionId!.Value;
            var components = ImmutableArray.CreateBuilder<CostingMath.LayerState>(outward.Consumptions.Length);
            var ordinal = 0;
            foreach (var take in outward.Consumptions)
            {
                Reserve();
                var layerId = MirrorIdentity(e.Id, take.LayerId);
                if (!usedLayerIds.Add(layerId)) throw new CostingException("impact_history_inconsistent");
                components.Add(new CostingMath.LayerState(layerId, 0, take.Quantity, take.Amount));
                created.Add(new ReplayFlowLayer(destination, e.Id, layerId, take.LayerId, ++ordinal, take.Quantity, take.Amount));
            }
            var inward = states[destination].ReceiveTransfer(e.Id, CostingMath.ToUnits(e.Quantity, CostingMath.QuantityScale),
                CostingMath.ToUnits(outward.Amount, CostingMath.AmountScale), components.MoveToImmutable(), ct);
            steps.Add(new ReplayFlowStep(e.Id, e.Kind, [sourceLeg, new ReplayFlowLeg(destination, inward)]));
        }
        var results = ImmutableArray.CreateBuilder<ReplayFlowPosition>(positions.Length);
        foreach (var id in positions.OrderBy(id => id.ToString("N"), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var state = states[id];
            results.Add(new ReplayFlowPosition(id, state.QuantityAfter, state.ValueAfter, state.OpenLayers));
        }
        return new ReplayFlowResult(steps.MoveToImmutable(), results.MoveToImmutable(), created.ToImmutable());
    }

    private static Guid MirrorIdentity(Guid operation, Guid sourceLayer)
    {
        var bytes = Encoding.UTF8.GetBytes("hasm-replay-mirror-v1\0" + operation.ToString("N") + ":" + sourceLayer.ToString("N"));
        return new Guid(SHA256.HashData(bytes).AsSpan(0, 16), bigEndian: true);
    }
}
