using System.Collections.Immutable;

namespace Hasm.Infrastructure.Costing;

internal readonly record struct ReplayOrderLeg(Guid PositionId, long Ordinal);
internal sealed record ReplayOrderGroup(Guid Id, DateOnly EffectiveDate, CostOperationKind Kind,
    ImmutableArray<ReplayOrderLeg> Legs, Guid? Reverses = null);
internal sealed record ReplayOrderProposal(Guid Id, DateOnly EffectiveDate, Guid PositionId);

/// <summary>
/// Pure internal dependency ordering for one item's classified ordinary operation groups. No money, database, authorization or execution.
/// Caller must prove complete history/closure and canonical per-position ordinals; scheduling cannot prove omitted history exists.
/// Transfers/reversals are indivisible groups. Conversion/negative/adjustment adapters are not implemented by this helper.
/// </summary>
internal static class ReplaySchedule
{
    private const int MaxGroups = 100_000, MaxPositions = 10_000, MaxLegs = 200_000, MaxEdges = 400_000;
    private static CostingException Inconsistent() => new("impact_history_inconsistent");
    private static CostingException TooLarge() => new("impact_history_too_large");

    internal static ImmutableArray<Guid> Calculate(ImmutableArray<ReplayOrderGroup> history, ReplayOrderProposal? proposal = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (history.IsDefault) throw Inconsistent();
        if (history.Length > MaxGroups - (proposal is null ? 0 : 1)) throw TooLarge();
        var groups = new Dictionary<Guid, ReplayOrderGroup>();
        var dates = new Dictionary<Guid, DateOnly>();
        var positions = new Dictionary<Guid, List<(long Ordinal, Guid Group)>>();
        var legCount = proposal is null ? 0 : 1;
        foreach (var group in history)
        {
            ct.ThrowIfCancellationRequested();
            if (group is null || group.Id == Guid.Empty || group.Legs.IsDefaultOrEmpty || !groups.TryAdd(group.Id, group)) throw Inconsistent();
            if (group.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer or CostOperationKind.Reversal))
                throw new CostingException("impact_history_unsupported");
            if ((group.Kind is CostOperationKind.Receipt or CostOperationKind.Issue ? group.Legs.Length != 1 :
                group.Kind == CostOperationKind.Transfer ? group.Legs.Length != 2 : group.Legs.Length is < 1 or > 2) ||
                (group.Kind == CostOperationKind.Reversal ? group.Reverses is null : group.Reverses is not null)) throw Inconsistent();
            if (legCount > MaxLegs - group.Legs.Length) throw TooLarge();
            legCount += group.Legs.Length;
            dates.Add(group.Id, group.EffectiveDate);
            var seen = new HashSet<Guid>();
            foreach (var leg in group.Legs)
            {
                ct.ThrowIfCancellationRequested();
                if (leg.PositionId == Guid.Empty || leg.Ordinal <= 0 || !seen.Add(leg.PositionId)) throw Inconsistent();
                if (!positions.TryGetValue(leg.PositionId, out var ordered))
                {
                    if (positions.Count == MaxPositions) throw TooLarge();
                    positions.Add(leg.PositionId, ordered = []);
                }
                ordered.Add((leg.Ordinal, group.Id));
            }
        }
        if (proposal is not null)
        {
            if (proposal.Id == Guid.Empty || proposal.PositionId == Guid.Empty || dates.ContainsKey(proposal.Id)) throw new CostingException("invalid_replay_input");
            if (!positions.ContainsKey(proposal.PositionId) && positions.Count == MaxPositions) throw TooLarge();
            dates.Add(proposal.Id, proposal.EffectiveDate);
        }

        var edges = new HashSet<(Guid From, Guid To)>();
        var outgoing = new Dictionary<Guid, List<Guid>>();
        var incoming = dates.Keys.ToDictionary(id => id, _ => 0);
        void Edge(Guid from, Guid to)
        {
            ct.ThrowIfCancellationRequested();
            if (from == to || dates[from] > dates[to]) throw Inconsistent();
            if (edges.Contains((from, to))) return;
            if (edges.Count == MaxEdges) throw TooLarge();
            edges.Add((from, to));
            if (!outgoing.TryGetValue(from, out var next)) outgoing.Add(from, next = []);
            next.Add(to); incoming[to]++;
        }
        foreach (var (position, ordered) in positions)
        {
            ct.ThrowIfCancellationRequested();
            ordered.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            for (var n = 0; n < ordered.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                if (ordered[n].Ordinal != n + 1L) throw Inconsistent();
                if (n > 0) Edge(ordered[n - 1].Group, ordered[n].Group);
            }
            if (proposal is not null && position == proposal.PositionId)
            {
                var insertion = 0;
                while (insertion < ordered.Count && dates[ordered[insertion].Group] <= proposal.EffectiveDate) insertion++;
                if (insertion > 0) Edge(ordered[insertion - 1].Group, proposal.Id);
                if (insertion < ordered.Count) Edge(proposal.Id, ordered[insertion].Group);
            }
        }
        var reversed = new HashSet<Guid>();
        foreach (var group in history)
        {
            ct.ThrowIfCancellationRequested();
            if (group.Reverses is not { } originalId) continue;
            if (!groups.TryGetValue(originalId, out var original) || original.Kind == CostOperationKind.Reversal || !reversed.Add(originalId) ||
                !group.Legs.Select(l => l.PositionId).ToHashSet().SetEquals(original.Legs.Select(l => l.PositionId))) throw Inconsistent();
            Edge(originalId, group.Id);
        }
        var priorities = Comparer<(DateOnly Date, string Id)>.Create((a, b) =>
            a.Date != b.Date ? a.Date.CompareTo(b.Date) : StringComparer.Ordinal.Compare(a.Id, b.Id));
        var ready = new PriorityQueue<Guid, (DateOnly, string)>(priorities);
        foreach (var (id, count) in incoming)
            if (count == 0) ready.Enqueue(id, (dates[id], id.ToString("N")));
        var result = ImmutableArray.CreateBuilder<Guid>(dates.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(id);
            if (!outgoing.TryGetValue(id, out var next)) continue;
            foreach (var to in next)
                if (--incoming[to] == 0) ready.Enqueue(to, (dates[to], to.ToString("N")));
        }
        if (result.Count != dates.Count) throw Inconsistent();
        return result.MoveToImmutable();
    }
}
