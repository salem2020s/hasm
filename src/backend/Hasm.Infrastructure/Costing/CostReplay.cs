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
internal static partial class CostReplay
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
            ValidateEvent(e, ids, moneyDecimals);
        }

        var position = new PositionState(method, moneyDecimals);
        var steps = ImmutableArray.CreateBuilder<ReplayStep>(events.Length);
        foreach (var e in events)
            steps.Add(position.AppendValidated(e, ct));
        return new ReplayResult(steps.MoveToImmutable(), position.QuantityAfter, position.ValueAfter);
    }

    private static void ValidateEvent(ReplayEvent e, HashSet<Guid> ids, int moneyDecimals)
    {
        if (e.Id == Guid.Empty || !ids.Add(e.Id) || e.Quantity <= 0m || e.Quantity >= MaxInput ||
            (e.Kind == CostOperationKind.Receipt ? e.ReceiptAmount is null or < 0m or >= MaxInput : e.ReceiptAmount is not null))
            throw new CostingException("invalid_replay_input");
        if (!CostingMath.FitsDecimals(e.Quantity, CostingMath.QuantityScale) ||
            (e.ReceiptAmount is { } a && !CostingMath.FitsDecimals(a, moneyDecimals)))
            throw new CostingException("precision_exceeded");
    }

    // Mutable only inside one pure calculation. A failed calculation discards its whole state;
    // no cursor or partial trajectory escapes to a caller. The internal common-method flow helper
    // reuses it; authorized closure, real-layer mapping and execution remain separate prerequisites.
    private sealed class PositionState(CostMethod method, int moneyDecimals, Action? reserveComponent = null)
    {
        private BigInteger quantity = BigInteger.Zero, value = BigInteger.Zero;
        private readonly LinkedList<CostingMath.LayerState> layers = new();
        private long arrival;

        internal decimal QuantityAfter => CostingMath.FromUnits(quantity, CostingMath.QuantityScale);
        internal decimal ValueAfter => CostingMath.FromUnits(value, CostingMath.AmountScale);
        internal ImmutableArray<CostingMath.LayerState> OpenLayers => layers.ToImmutableArray();

        // Input identity, classification, precision and user-input limits were checked for the
        // COMPLETE batch before reaching this private method. Derived outflow cost is not a new
        // user receipt amount and must not acquire the receipt-input limit.
        internal ReplayStep AppendValidated(ReplayEvent e, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var q = CostingMath.ToUnits(e.Quantity, CostingMath.QuantityScale);
            BigInteger amount;
            var takes = ImmutableArray<CostingMath.Take>.Empty;
            if (e.Kind == CostOperationKind.Receipt)
            {
                amount = CostingMath.ToUnits(e.ReceiptAmount!.Value, CostingMath.AmountScale);
                if (method == CostMethod.Fifo)
                {
                    reserveComponent?.Invoke();
                    layers.AddLast(new CostingMath.LayerState(e.Id, ++arrival, q, amount));
                }
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
                        reserveComponent?.Invoke();
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
            return Step(e.Id, e.Kind, amount, takes);
        }

        // Components have already reserved their shared work budget before allocation. This path
        // receives source-derived fixed units, never a new user-provided/rounded receipt price.
        internal ReplayStep ReceiveTransfer(Guid id, BigInteger q, BigInteger amount,
            ImmutableArray<CostingMath.LayerState> components, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (method == CostMethod.Fifo)
            {
                BigInteger componentQuantity = 0, componentAmount = 0;
                foreach (var component in components)
                {
                    ct.ThrowIfCancellationRequested();
                    componentQuantity += component.RemainingQuantity;
                    componentAmount += component.RemainingAmount;
                    layers.AddLast(component with { Seq = ++arrival });
                }
                if (componentQuantity != q || componentAmount != amount) throw new CostingException("impact_history_inconsistent");
            }
            else if (!components.IsEmpty) throw new CostingException("impact_history_inconsistent");
            quantity += q; value += amount;
            return Step(id, CostOperationKind.Receipt, amount, ImmutableArray<CostingMath.Take>.Empty);
        }

        private ReplayStep Step(Guid id, CostOperationKind kind, BigInteger amount, ImmutableArray<CostingMath.Take> takes)
        {
            // FromUnits converts to decimal before scaling down. Refuse unsupported aggregates
            // with a stable code; the surrounding calculation discards all state on failure.
            if (quantity > MaxConvertibleUnits || value > MaxConvertibleUnits) throw new CostingException("replay_total_out_of_range");
            return new ReplayStep(id, kind, CostingMath.FromUnits(amount, CostingMath.AmountScale), QuantityAfter, ValueAfter, takes);
        }
    }
}
