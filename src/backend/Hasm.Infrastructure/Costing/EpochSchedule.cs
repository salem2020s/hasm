using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hasm.Infrastructure.Costing;

/// <summary>Role of an epoch operation, the same numbers as the epoch member table of company migration 0036 (1 storno, 2 event, 3 rebook).</summary>
internal enum EpochRole : short { Storno = 1, Event = 2, Rebook = 3 }

/// <summary>
/// One entry an epoch writes. <see cref="OriginalEntryId"/> is the entry a storno takes back or a rebook rewrites (null for the event). The amount and the two running figures are what the REPLAY says
/// the entry must carry (the storno mirrors its original: same kind, opposite direction, same quantity, amount and date). <see cref="Sequence"/> is the sequence number the entry takes on its position
/// (the position's last sequence plus the entries the plan writes before it on that position): with the warehouse (the position) it is the deterministic KEY of the entry, no generated id is involved.
/// </summary>
internal sealed record EpochEntryPlan(EpochRole Role, Guid WarehouseId, CostEntryKind Kind, int Direction, decimal Quantity, DateOnly Date, decimal Amount,
    decimal QuantityAfter, decimal ValueAfter, long? OriginalEntryId, long Sequence = 0);

/// <summary>One epoch operation: a storno or a rebook of ONE original operation (natural kind of the original), or the event. <see cref="OriginalOperationId"/> is null for the event.</summary>
internal sealed record EpochOperationPlan(EpochRole Role, Guid? OriginalOperationId, CostOperationKind Kind, DateOnly Date, ImmutableArray<EpochEntryPlan> Entries);

/// <summary>Where the three entries of one original entry sit in <see cref="EpochScheduleResult.Operations"/>: (operation index, entry index) of its storno and of its rebook.</summary>
internal sealed record EpochLineagePlan(long OriginalEntryId, int StornoOperation, int StornoEntry, int RebookOperation, int RebookEntry);

/// <summary>
/// The complete, ordered write plan of one replay epoch. <see cref="Operations"/> is in WRITE order: every storno operation (global descending entry order), the event, every rebook operation (replay order).
/// <see cref="ExpectedPositions"/> is the final state (quantity, value, open layers) the replay proves; the executor must end at exactly it. <see cref="Fingerprint"/> is a hash of the whole plan over a System.Text.Json
/// serialisation, whose decimal text follows each value's scale: it is deterministic for identical inputs but NOT canonical, and it is NOT the T2 epoch digest (the byte-exact epoch text of the T2 design is a later slice).
/// </summary>
internal sealed record EpochScheduleResult(ImmutableArray<EpochOperationPlan> Operations, ImmutableArray<EpochLineagePlan> Lineage,
    ImmutableArray<ReplayFlowPosition> ExpectedPositions, string Fingerprint);

/// <summary>
/// Phase 3.8D executor slice E1: the PURE derivation of an epoch's write plan from the planner's output (design sections 2 and 5, steps 5 to 7). No database, no clock, no randomness, no caller: nothing in
/// the application registers or reaches it. Everything it cannot prove it refuses (fail closed), with a <see cref="CostingException"/> code:
/// <list type="bullet">
/// <item>Which entries are stornoed: exactly the entries the comparison says change. Those must form a contiguous SUFFIX of their position's sequence, whole operations on a position are all-or-nothing, and a
/// transfer with exactly one changed leg is refused (<c>epoch_partial_transfer_unsupported</c>): the engine writes a transfer as a unit and a FIFO mirror layer follows its out leg.</item>
/// <item>Order: storno operations by their highest original entry id, descending (entries of an operation descending); the event; rebook operations in the replay order of the revised trace, which must be
/// ascending (date, original entry id) (<c>epoch_order_inconsistent</c> otherwise).</item>
/// <item>Dates: storno and rebook carry the ORIGINAL date (X2), the event its own.</item>
/// <item>Ledger proof: the plan is replayed on signed quantity and value per position starting from the baseline; no step may go below zero (<c>epoch_storno_negative</c>), every event and rebook entry must
/// meet the running figures the revised trace states, and every position must end exactly at the proposed quantity and value (<c>epoch_schedule_inconsistent</c>).</item>
/// <item>Section 6 of the design: reversals in the history, operation kinds other than receipt, issue and transfer, an entry outside the closure (<c>epoch_reversal_in_history</c>,
/// <c>impact_history_unsupported</c>, <c>impact_history_inconsistent</c>).</item>
/// </list>
/// The arithmetic of the replay itself is NOT redone here: the amounts come from <see cref="CostReplay"/> through the comparison, and the independent check is the signed-sum ledger proof above.
/// </summary>
internal static class EpochSchedule
{
    private const int MaxEntries = 200_000;
    private static CostingException Inconsistent() => new("impact_history_inconsistent");

    internal static EpochScheduleResult Derive(CostFlowSnapshot snapshot, CostFlowComparisonResult comparison, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot is null || comparison is null || comparison.Proposal is null || comparison.RevisedTrace is null || snapshot.Entries.IsDefault || snapshot.Positions.IsDefaultOrEmpty ||
            comparison.Changes.IsDefault || comparison.ProposedSteps.IsDefault || comparison.Positions.IsDefault || snapshot.Method is not (CostMethod.Fifo or CostMethod.MovingAverage))
            throw new CostingException("invalid_impact_snapshot");
        var proposal = comparison.Proposal;
        if (proposal.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer) || proposal.Lines.IsDefaultOrEmpty || comparison.ProposedSteps.Length != proposal.Lines.Length)
            throw new CostingException("impact_history_unsupported");
        if (snapshot.Entries.Length > MaxEntries) throw new CostingException("impact_history_too_large");

        var closure = new HashSet<Guid>(snapshot.Positions.Select(p => p.WarehouseId));
        var entries = new Dictionary<long, CostFlowEntry>();
        foreach (var e in snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e is null || !entries.TryAdd(e.Id, e) || !closure.Contains(e.WarehouseId) || e.ItemId != snapshot.ItemId) throw Inconsistent();      // every entry belongs to the snapshot's item (the item of the E line)
            if (e.HasReversal) throw new CostingException("epoch_reversal_in_history");
            if (e.OperationKind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer)) throw new CostingException("impact_history_unsupported");
        }

        // ---- which entries are affected: the changed ones, whole operations per position, a contiguous suffix per position
        var changed = new HashSet<long>();
        foreach (var change in comparison.Changes)
            if (!entries.ContainsKey(change.EntryId) || !changed.Add(change.EntryId)) throw Inconsistent();
        var affected = snapshot.Entries.Where(e => changed.Contains(e.Id)).ToList();
        foreach (var operation in affected.Where(e => e.OperationKind == CostOperationKind.Transfer).GroupBy(e => e.OperationId))
        {
            var legs = snapshot.Entries.Count(e => e.OperationId == operation.Key);
            if (operation.Count() != legs) throw new CostingException("epoch_partial_transfer_unsupported");
        }
        foreach (var byPosition in snapshot.Entries.GroupBy(e => e.WarehouseId))
        {
            ct.ThrowIfCancellationRequested();
            var ordered = byPosition.OrderBy(e => e.Sequence).ToList();
            var firstAffected = ordered.FindIndex(e => changed.Contains(e.Id));
            if (firstAffected >= 0 && ordered.Skip(firstAffected).Any(e => !changed.Contains(e.Id))) throw new CostingException("epoch_not_a_suffix");
        }
        // an operation's entries on the closure are all affected or none (a block is atomic)
        foreach (var operation in snapshot.Entries.GroupBy(e => e.OperationId))
        {
            var any = operation.Any(e => changed.Contains(e.Id));
            if (any && operation.Any(e => !changed.Contains(e.Id))) throw new CostingException(operation.First().OperationKind == CostOperationKind.Transfer ? "epoch_partial_transfer_unsupported" : "epoch_not_a_suffix");
        }

        // ---- storno: operations by highest entry id descending, entries of an operation descending
        var stornoOperations = affected.GroupBy(e => e.OperationId)
            .Select(g => (Id: g.Key, Kind: g.First().OperationKind, Date: g.First().Date, Entries: g.OrderByDescending(e => e.Id).ToList()))
            .OrderByDescending(g => g.Entries[0].Id).ToList();
        var stornoPlans = stornoOperations.Select(o => new EpochOperationPlan(EpochRole.Storno, o.Id, o.Kind, o.Date, [.. o.Entries.Select(e =>
            new EpochEntryPlan(EpochRole.Storno, e.WarehouseId, e.EntryKind, -e.Direction, e.Quantity, e.Date, e.Amount, 0m, 0m, e.Id))])).ToList();

        // ---- event: one step per proposal line, in line order
        var eventEntries = ImmutableArray.CreateBuilder<EpochEntryPlan>();
        var proposedIds = new HashSet<Guid>(comparison.ProposedSteps.Select(s => s.EventId));
        if (proposedIds.Count != comparison.ProposedSteps.Length) throw Inconsistent();
        for (var lineNo = 0; lineNo < comparison.ProposedSteps.Length; lineNo++)
        {
            var step = comparison.ProposedSteps[lineNo];
            if (step.Kind != proposal.Kind || step.Legs.IsDefaultOrEmpty || step.Legs.Length != (proposal.Kind == CostOperationKind.Transfer ? 2 : 1)) throw Inconsistent();
            for (var n = 0; n < step.Legs.Length; n++)
            {
                var leg = step.Legs[n];
                if (!closure.Contains(leg.PositionId)) throw Inconsistent();
                var (kind, direction) = proposal.Kind switch
                {
                    CostOperationKind.Receipt => (CostEntryKind.Receipt, 1),
                    CostOperationKind.Issue => (CostEntryKind.Issue, -1),
                    _ => n == 0 ? (CostEntryKind.TransferOut, -1) : (CostEntryKind.TransferIn, 1),
                };
                var quantity = proposal.Lines[lineNo].Quantity;
                eventEntries.Add(new EpochEntryPlan(EpochRole.Event, leg.PositionId, kind, direction, quantity, proposal.EffectiveDate, leg.Step.Amount, leg.Step.QuantityAfter, leg.Step.ValueAfter, null));
            }
        }
        var eventPlan = new EpochOperationPlan(EpochRole.Event, null, proposal.Kind, proposal.EffectiveDate, eventEntries.ToImmutable());

        // ---- rebook: the replay order of the revised trace, grouped by original operation
        var original = new Dictionary<(Guid Event, Guid Warehouse), CostFlowEntry>();
        foreach (var e in snapshot.Entries)
            if (!original.TryAdd((e.OperationKind == CostOperationKind.Transfer ? e.OperationId : CostHistoryEntry.SimulationId(e.Id), e.WarehouseId), e)) throw Inconsistent();
        var rebookOrder = new List<Guid>();
        var rebookEntries = new Dictionary<Guid, List<EpochEntryPlan>>();
        var rebookOriginals = new List<long>();
        foreach (var step in comparison.RevisedTrace.Steps)
        {
            ct.ThrowIfCancellationRequested();
            if (proposedIds.Contains(step.EventId)) continue;
            foreach (var leg in step.Legs)
            {
                if (!original.TryGetValue((step.EventId, leg.PositionId), out var entry)) throw Inconsistent();
                if (!changed.Contains(entry.Id)) continue;
                if (!rebookEntries.TryGetValue(entry.OperationId, out var list))
                {
                    rebookEntries[entry.OperationId] = list = [];
                    rebookOrder.Add(entry.OperationId);
                }
                list.Add(new EpochEntryPlan(EpochRole.Rebook, entry.WarehouseId, entry.EntryKind, entry.Direction, entry.Quantity, entry.Date, leg.Step.Amount, leg.Step.QuantityAfter, leg.Step.ValueAfter, entry.Id));
                rebookOriginals.Add(entry.Id);
            }
        }
        if (rebookOriginals.Count != changed.Count || rebookOriginals.Distinct().Count() != changed.Count) throw Inconsistent();   // every affected entry is rebooked exactly once
        var rebookSorted = rebookOriginals.Select(id => entries[id]).Select(e => (e.Date, e.Id)).ToList();
        for (var n = 1; n < rebookSorted.Count; n++)
            if (rebookSorted[n - 1].CompareTo(rebookSorted[n]) > 0) throw new CostingException("epoch_order_inconsistent");
        var rebookPlans = rebookOrder.Select(id =>
        {
            var first = entries[rebookEntries[id][0].OriginalEntryId!.Value];
            return new EpochOperationPlan(EpochRole.Rebook, id, first.OperationKind, first.Date, [.. rebookEntries[id]]);
        }).ToList();
        foreach (var plan in rebookPlans)       // a rebook operation rewrites exactly the entries its storno took back
        {
            var storno = stornoPlans.Single(s => s.OriginalOperationId == plan.OriginalOperationId);
            if (!storno.Entries.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x).SequenceEqual(plan.Entries.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x))) throw Inconsistent();
        }

        var operations = ImmutableArray.CreateBuilder<EpochOperationPlan>();
        operations.AddRange(stornoPlans);
        operations.Add(eventPlan);
        operations.AddRange(rebookPlans);

        // the order the entries are WRITTEN in (operations grouped, entries in operation order) is ascending by (original date, original entry id), not only the trace order
        var written = operations.Where(o => o.Role == EpochRole.Rebook).SelectMany(o => o.Entries).Select(e => entries[e.OriginalEntryId!.Value]).Select(e => (e.Date, e.Id)).ToList();
        for (var n = 1; n < written.Count; n++)
            if (written[n - 1].CompareTo(written[n]) > 0) throw new CostingException("epoch_order_inconsistent");

        // ---- lineage
        var stornoAt = new Dictionary<long, (int Operation, int Entry)>();
        var rebookAt = new Dictionary<long, (int Operation, int Entry)>();
        for (var o = 0; o < operations.Count; o++)
            for (var n = 0; n < operations[o].Entries.Length; n++)
            {
                var entry = operations[o].Entries[n];
                if (entry.Role == EpochRole.Storno) stornoAt[entry.OriginalEntryId!.Value] = (o, n);
                else if (entry.Role == EpochRole.Rebook) rebookAt[entry.OriginalEntryId!.Value] = (o, n);
            }
        var lineage = rebookOriginals.Select(id => new EpochLineagePlan(id, stornoAt[id].Operation, stornoAt[id].Entry, rebookAt[id].Operation, rebookAt[id].Entry)).ToImmutableArray();

        // ---- the ledger proof: replay the plan on signed quantity and value per position
        var running = snapshot.Positions.ToDictionary(p => p.WarehouseId, p => (Quantity: p.Quantity, Value: p.Value));
        var nextSequence = snapshot.Positions.ToDictionary(p => p.WarehouseId, p => snapshot.Entries.Where(e => e.WarehouseId == p.WarehouseId).Select(e => e.Sequence).DefaultIfEmpty(0L).Max());
        foreach (var p in snapshot.Positions) if (p.LastSequence != nextSequence[p.WarehouseId]) throw Inconsistent();   // the baseline's last sequence is the last sequence of its entries
        for (var o = 0; o < operations.Count; o++)
        {
            var plan = operations[o];
            var filled = ImmutableArray.CreateBuilder<EpochEntryPlan>(plan.Entries.Length);
            foreach (var entry in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var (quantity, value) = running[entry.WarehouseId];
                quantity += entry.Direction * entry.Quantity;
                value += entry.Direction * entry.Amount;
                if (quantity < 0m || value < 0m) throw new CostingException("epoch_storno_negative");
                running[entry.WarehouseId] = (quantity, value);
                var sequence = ++nextSequence[entry.WarehouseId];
                if (entry.Role != EpochRole.Storno && (entry.QuantityAfter, entry.ValueAfter) != (quantity, value)) throw new CostingException("epoch_schedule_inconsistent");
                filled.Add(entry.Role == EpochRole.Storno ? entry with { QuantityAfter = quantity, ValueAfter = value, Sequence = sequence } : entry with { Sequence = sequence });
            }
            operations[o] = plan with { Entries = filled.MoveToImmutable() };
        }
        // the end state covers EVERY closure position: each one is in the comparison and in the revised trace, and both equal the plan's end
        var finals = new Dictionary<Guid, ReplayFlowPosition>();
        foreach (var proven in comparison.RevisedTrace.Positions)
            if (!finals.TryAdd(proven.PositionId, proven)) throw new CostingException("epoch_schedule_inconsistent");
        var impacts = new Dictionary<Guid, CostFlowPositionImpact>();
        foreach (var impact in comparison.Positions)
            if (!impacts.TryAdd(impact.WarehouseId, impact)) throw new CostingException("epoch_schedule_inconsistent");      // a position twice
        foreach (var (warehouse, end) in running)
        {
            if (!impacts.TryGetValue(warehouse, out var impact) || !finals.TryGetValue(warehouse, out var proven) ||
                (end.Quantity, end.Value) != (impact.ProposedQuantity, impact.ProposedValue) || (proven.Quantity, proven.Value) != end)
                throw new CostingException("epoch_schedule_inconsistent");
        }
        if (impacts.Keys.Any(w => !closure.Contains(w))) throw new CostingException("epoch_schedule_inconsistent");

        var result = operations.ToImmutable();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Contract = "epoch-schedule-v1", History = comparison.HistoryFingerprint, Comparison = comparison.ComparisonFingerprint, Operations = result, Lineage = lineage }))).ToLowerInvariant();
        return new EpochScheduleResult(result, lineage, comparison.RevisedTrace.Positions, fingerprint);
    }
}
