using System.Collections.Immutable;
using System.Numerics;

namespace Hasm.Infrastructure.Costing;

/// <summary>An already ordered, explicitly classified ordinary receipt or issue. Ordering/history classification belongs to the future snapshot adapter, not this arithmetic primitive.</summary>
internal sealed record ReplayEvent(Guid Id, CostOperationKind Kind, decimal Quantity, decimal? ReceiptAmount = null);

internal sealed record ReplayStep(Guid EventId, CostOperationKind Kind, decimal Amount, decimal QuantityAfter, decimal ValueAfter, ImmutableArray<CostingMath.Take> Consumptions);
internal sealed record ReplayResult(ImmutableArray<ReplayStep> Steps, decimal QuantityAfter, decimal ValueAfter);

/// <summary>
/// Phase 3.8D-1 internal read-only arithmetic primitive, starting from an empty position. Reuses the approved engine's exact arithmetic; never reads or writes a database, clock, document or journal.
/// Not a history loader, scheduler, public preview or authorization boundary. No production caller exists yet. A future adapter must prove history completeness and classify obligations/settlements,
/// stocktakes, transfers and reversals before using this primitive; silently dropping or recasting unsupported history as ordinary receipts/issues is forbidden.
/// </summary>
internal static class CostReplay
{
    // Same per-event upper bound as InventoryCostingService.Quantity/Amount. Validate before decimal scaling.
    private const decimal MaxInput = 1_000_000_000_000_000_000m;
    private static readonly BigInteger MaxConvertibleUnits = new(decimal.MaxValue);

    public static ReplayResult Calculate(CostMethod method, int moneyDecimals, ImmutableArray<ReplayEvent> events, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (method is not (CostMethod.Fifo or CostMethod.MovingAverage) || moneyDecimals is < 0 or > CostingMath.MaxMoneyDecimals || events.IsDefault)
            throw new CostingException("invalid_replay_input");
        // Classify the complete input before arithmetic; unsupported history cannot become a misleading partial result.
        if (events.Any(e => e is null || e.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue)))
            throw new CostingException("impact_history_unsupported");
        var ids = new HashSet<Guid>();
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Id == Guid.Empty || !ids.Add(e.Id) || e.Quantity <= 0m || e.Quantity >= MaxInput ||
                (e.Kind == CostOperationKind.Receipt ? e.ReceiptAmount is null or < 0m or >= MaxInput : e.ReceiptAmount is not null))
                throw new CostingException("invalid_replay_input");
            if (!CostingMath.FitsDecimals(e.Quantity, CostingMath.QuantityScale) ||
                (e.ReceiptAmount is { } a && !CostingMath.FitsDecimals(a, moneyDecimals)))
                throw new CostingException("precision_exceeded");
        }

        var quantity = BigInteger.Zero;
        var value = BigInteger.Zero;
        var layers = new LinkedList<CostingMath.LayerState>();
        var steps = ImmutableArray.CreateBuilder<ReplayStep>(events.Length);
        long arrival = 0;
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            var q = CostingMath.ToUnits(e.Quantity, CostingMath.QuantityScale);
            BigInteger amount;
            var takes = ImmutableArray<CostingMath.Take>.Empty;
            if (e.Kind == CostOperationKind.Receipt)
            {
                amount = CostingMath.ToUnits(e.ReceiptAmount!.Value, CostingMath.AmountScale);
                if (method == CostMethod.Fifo) layers.AddLast(new CostingMath.LayerState(e.Id, ++arrival, q, amount));
                quantity += q;
                value += amount;
            }
            else
            {
                if (method == CostMethod.Fifo)
                {
                    // Only send the required FIFO prefix to the existing arithmetic. Sorting every open layer
                    // for each small issue would otherwise make a long ordinary history quadratic.
                    var prefix = new List<CostingMath.LayerState>();
                    var needed = q;
                    for (var node = layers.First; node is not null && needed.Sign > 0; node = node.Next)
                    {
                        ct.ThrowIfCancellationRequested();
                        prefix.Add(node.Value);
                        needed -= node.Value.RemainingQuantity;
                    }
                    takes = CostingMath.PlanFifo(prefix, q, moneyDecimals).ToImmutableArray();
                    amount = takes.Aggregate(BigInteger.Zero, (sum, t) => sum + t.Amount);
                    foreach (var take in takes)
                    {
                        var first = layers.First!;
                        var layer = first.Value;
                        if (take.Quantity == layer.RemainingQuantity) layers.RemoveFirst();
                        else first.Value = layer with { RemainingQuantity = layer.RemainingQuantity - take.Quantity, RemainingAmount = layer.RemainingAmount - take.Amount };
                    }
                }
                else amount = CostingMath.PlanAverage(quantity, value, q, moneyDecimals);
                quantity -= q;
                value -= amount;
            }
            // FromUnits converts its integer to decimal before scaling down. Refuse an unsupported aggregate
            // with a stable code, rather than leaking OverflowException or returning a partial trajectory.
            if (quantity > MaxConvertibleUnits || value > MaxConvertibleUnits) throw new CostingException("replay_total_out_of_range");
            steps.Add(new ReplayStep(e.Id, e.Kind, CostingMath.FromUnits(amount, CostingMath.AmountScale),
                CostingMath.FromUnits(quantity, CostingMath.QuantityScale), CostingMath.FromUnits(value, CostingMath.AmountScale), takes));
        }
        return new ReplayResult(steps.MoveToImmutable(), CostingMath.FromUnits(quantity, CostingMath.QuantityScale), CostingMath.FromUnits(value, CostingMath.AmountScale));
    }
}
