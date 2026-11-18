using System.Collections.Immutable;

namespace Hasm.Infrastructure.Costing;

internal sealed record ReplayFlowChild(Guid ParentId, long LineOrder, long PositionOrdinal, ReplayFlowEvent Event,
    long? DestinationOrdinal = null);
internal sealed record ReplayFlowBlock(ReplayOrderGroup Order, ImmutableArray<ReplayFlowChild> Children);
internal sealed record ReplayFlowBinding(Guid EventId, Guid GroupId, long LineOrder);
internal sealed record ReplayBlockResult(ImmutableArray<Guid> GroupOrder, ReplayFlowResult Flow, ImmutableArray<ReplayFlowBinding> Bindings);

internal static partial class CostReplay
{
    /// <summary>
    /// Schedules complete ordinary operation blocks, then preserves EVERY child line and its rounding.
    /// This pure internal value model cannot prove database provenance, authorization or omitted history;
    /// the future snapshot adapter must bind numeric line order/ordinals to the real entries.
    /// No receipt/issue quantity or price is summed. No reversal/negative/conversion arithmetic or execution.
    /// </summary>
    internal static ReplayBlockResult CalculateBlocks(CostMethod method, int moneyDecimals, ImmutableArray<Guid> positions,
        ImmutableArray<ReplayFlowBlock> blocks, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (method is not (CostMethod.Fifo or CostMethod.MovingAverage) || moneyDecimals is < 0 or > CostingMath.MaxMoneyDecimals ||
            positions.IsDefault || blocks.IsDefault) throw new CostingException("invalid_replay_input");
        if (blocks.Length > 100_000 || positions.Length > 10_000) throw new CostingException("impact_history_too_large");
        var groups = new Dictionary<Guid, ReplayFlowBlock>();
        var orders = ImmutableArray.CreateBuilder<ReplayOrderGroup>(blocks.Length);
        var childCount = 0;
        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (block is null || block.Order is null || block.Children.IsDefaultOrEmpty || !groups.TryAdd(block.Order.Id, block))
                throw new CostingException("impact_history_inconsistent");
            if (block.Order.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer))
                throw new CostingException("impact_history_unsupported");
            if (block.Children.Length > 500) throw new CostingException("impact_history_too_large"); // Existing costing-operation line limit.
            if (block.Children.Length > 200_000 - childCount) throw new CostingException("impact_history_too_large");
            childCount += block.Children.Length;
            orders.Add(block.Order);
        }
        // Proves group atomicity and every historical range before any costing is attempted.
        var schedule = ReplaySchedule.Calculate(orders.MoveToImmutable(), ct: ct);
        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            var order = block.Order;
            var next = order.Legs.ToDictionary(l => l.PositionId, l => l.Ordinal);
            var ends = order.Legs.ToDictionary(l => l.PositionId, l => l.Ordinal + (l.EntryCount - 1));
            long previousLine = 0;
            foreach (var child in block.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (child is null || child.Event is null) throw new CostingException("impact_history_inconsistent");
                if (child.Event.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer))
                    throw new CostingException("impact_history_unsupported");
                if (child.ParentId != order.Id || child.LineOrder <= previousLine || child.Event.Kind != order.Kind)
                    throw new CostingException("impact_history_inconsistent");
                previousLine = child.LineOrder;
                void Match(Guid position, long ordinal)
                {
                    if (!next.TryGetValue(position, out var expected) || ordinal != expected || expected > ends[position])
                        throw new CostingException("impact_history_inconsistent");
                    next[position] = expected + 1; // Scheduler proved contiguous ranges within the 200k-entry bound.
                }
                Match(child.Event.PositionId, child.PositionOrdinal);
                if (order.Kind == CostOperationKind.Transfer)
                {
                    if (block.Children.Length != 1 || child.Event.Id != order.Id || child.Event.DestinationPositionId is not { } target ||
                        child.DestinationOrdinal is not { } ordinal) throw new CostingException("impact_history_inconsistent");
                    Match(target, ordinal);
                }
                else if (child.DestinationOrdinal is not null || child.Event.DestinationPositionId is not null)
                    throw new CostingException("impact_history_inconsistent");
            }
            if (next.Any(pair => pair.Value != ends[pair.Key] + 1)) throw new CostingException("impact_history_inconsistent");
        }
        var events = ImmutableArray.CreateBuilder<ReplayFlowEvent>(childCount);
        var bindings = ImmutableArray.CreateBuilder<ReplayFlowBinding>(childCount);
        foreach (var group in schedule)
            foreach (var child in groups[group].Children)
            {
                ct.ThrowIfCancellationRequested();
                events.Add(child.Event);
                bindings.Add(new ReplayFlowBinding(child.Event.Id, group, child.LineOrder));
            }
        // All parent/range/child shapes are checked; the core validates every numeric value and ID
        // before calculation and retains its shared FIFO component-work budget.
        var flow = CalculateFlowCore(method, moneyDecimals, positions, events.MoveToImmutable(), 200_000, ct);
        return new ReplayBlockResult(schedule, flow, bindings.MoveToImmutable());
    }
}
