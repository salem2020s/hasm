using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hasm.Infrastructure.Costing;

/// <summary>
/// The deterministic key of a FIFO layer, with no engine id: the position (warehouse), the SEQUENCE of the entry that creates the layer on that position (an original entry of the snapshot or an entry of the
/// epoch, both have one) and the ordinal of the layer among those that entry creates (a receipt makes one, a transfer in makes one mirror per consumed source layer, in consumption order).
/// </summary>
internal sealed record EpochLayerKey(Guid WarehouseId, long EntrySequence, int Ordinal);

/// <summary>One consumption line of an epoch entry: <see cref="Restores"/> is a storno of an outflow giving back what the original consumed; a storno of an inflow consumes the whole untouched layer (not a restore).</summary>
internal sealed record EpochConsumption(EpochLayerKey Layer, decimal Quantity, decimal Amount, bool Restores);

/// <summary>A layer an epoch entry creates. <see cref="ArrivalRank"/> is the layer sequence on its position (the engine's max + 1 in write order); <see cref="Source"/> is the source layer of a transfer mirror.</summary>
internal sealed record EpochLayerCreation(EpochLayerKey Key, long ArrivalRank, decimal Quantity, decimal Amount, EpochLayerKey? Source);

/// <summary>
/// Everything one entry of the plan writes besides the entry itself: its consumptions and the layers it creates (both empty for the average method). (<see cref="Operation"/>, <see cref="Entry"/>) is its place in
/// <see cref="EpochScheduleResult.Operations"/>; (<see cref="WarehouseId"/>, <see cref="Sequence"/>) is its key and <see cref="OriginalEntryId"/> the entry it takes back or rewrites.
/// </summary>
internal sealed record EpochEntryContent(int Operation, int Entry, Guid WarehouseId, long Sequence, long? OriginalEntryId,
    ImmutableArray<EpochConsumption> Consumptions, ImmutableArray<EpochLayerCreation> Layers);

/// <summary>The figures of one layer the epoch touches: what it held before and what it holds after (the T2 R lines). <see cref="OriginalEntryId"/> is the creating entry of an existing layer (null for a created one).</summary>
internal sealed record EpochLayerRemaining(EpochLayerKey Key, long ArrivalRank, long? OriginalEntryId, bool Created, decimal OriginalQuantity, decimal OriginalAmount,
    decimal BeforeQuantity, decimal BeforeAmount, decimal AfterQuantity, decimal AfterAmount);

/// <summary>The running totals of one position before and after the epoch (the T2 P lines): the last sequence and, for FIFO, the number of open layers.</summary>
internal sealed record EpochPositionTotal(Guid WarehouseId, decimal BaselineQuantity, decimal BaselineValue, long BaselineLastSequence,
    decimal Quantity, decimal Value, long LastSequence, int OpenLayers);

/// <summary>The complete content of an epoch as data, in write order. <see cref="Fingerprint"/> covers the schedule it was derived from and all of it.</summary>
internal sealed record EpochContentResult(ImmutableArray<EpochEntryContent> Entries, ImmutableArray<EpochLayerRemaining> Layers, ImmutableArray<EpochPositionTotal> Positions, string Fingerprint);

/// <summary>The working state of one layer inside the pure simulation (never returned to a caller): the engine layer's figures in integer units, before and after. A top-level record, because the derivation declares no nested types.</summary>
internal sealed record EpochLayerState(Guid Id, EpochLayerKey Key, long Rank, long? OriginalEntry, bool Created, BigInteger OriginalQuantity, BigInteger OriginalAmount, EpochLayerKey? Source)
{
    public BigInteger Quantity { get; set; } = OriginalQuantity;
    public BigInteger Amount { get; set; } = OriginalAmount;
    public BigInteger BeforeQuantity { get; set; } = OriginalQuantity;
    public BigInteger BeforeAmount { get; set; } = OriginalAmount;
    public bool Touched { get; set; }
}

/// <summary>
/// Phase 3.8D executor slice E1b: the PURE derivation of the layers and consumptions an epoch writes (pure data, no text, no generated id), by simulating the semantics of
/// <c>InventoryCostingService</c> on the ACTUAL layers of the snapshot, in the write order of <see cref="EpochSchedule"/>: a storno of an outflow restores exactly what the original consumed (the same layers and
/// amounts); a storno of an inflow takes out the whole layers its original created, which must be untouched at that moment; a receipt creates one layer; an issue or transfer out consumes the oldest open layers
/// (<see cref="CostingMath.PlanFifo"/>, the engine's own planner); a transfer in creates one mirror layer per consumed layer in the same order; layer arrival ranks are the engine's max + 1 per position. Nothing
/// here writes, reads a database or is reachable from the application.
/// <para>Independent proofs, each failing closed with <c>epoch_schedule_inconsistent</c> (or <c>epoch_layer_consumed</c>, <c>impact_history_inconsistent</c>): every cost the engine's planner computes equals the amount
/// the replay stated for that entry; for FIFO the open layers sum to the position quantity and value at the baseline and at the end; the open layers at the end equal, in order, the open layers the revised trace proves.</para>
/// </summary>
internal static class EpochContent
{
    private static CostingException Inconsistent() => new("impact_history_inconsistent");
    private static CostingException Disagree() => new("epoch_schedule_inconsistent");
    private static BigInteger Q(decimal v) => CostingMath.ToUnits(v, CostingMath.QuantityScale);
    private static BigInteger A(decimal v) => CostingMath.ToUnits(v, CostingMath.AmountScale);
    private static decimal Q(BigInteger v) => CostingMath.FromUnits(v, CostingMath.QuantityScale);
    private static decimal A(BigInteger v) => CostingMath.FromUnits(v, CostingMath.AmountScale);

    internal static EpochContentResult Derive(CostFlowSnapshot snapshot, EpochScheduleResult schedule, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot is null || schedule is null || schedule.Operations.IsDefault || schedule.ExpectedPositions.IsDefault || snapshot.Entries.IsDefault || snapshot.Layers.IsDefault || snapshot.Consumptions.IsDefault ||
            snapshot.Positions.IsDefaultOrEmpty || snapshot.Method is not (CostMethod.Fifo or CostMethod.MovingAverage))
            throw new CostingException("invalid_impact_snapshot");
        var fifo = snapshot.Method == CostMethod.Fifo;
        var entries = snapshot.Entries.ToDictionary(e => e.Id);
        var warehouses = snapshot.Positions.Select(p => p.WarehouseId).ToHashSet();

        // ---- the actual layers of the snapshot, keyed by (position, creating entry sequence, ordinal)
        var next = 0;
        Guid NewId() => new(++next, 0, 0, new byte[8]);          // internal handle for the engine's planner only: never returned
        var byActual = new Dictionary<Guid, EpochLayerState>();
        var byOriginalEntry = new Dictionary<long, List<EpochLayerState>>();
        var all = warehouses.ToDictionary(w => w, _ => new List<EpochLayerState>());
        var keys = new Dictionary<Guid, EpochLayerKey>();
        if (!fifo && !snapshot.Layers.IsEmpty) throw Inconsistent();
        foreach (var group in snapshot.Layers.GroupBy(l => l.EntryId))
        {
            if (!entries.TryGetValue(group.Key, out var creator)) throw Inconsistent();
            var ordinal = 0;
            foreach (var l in group.OrderBy(l => l.Sequence))
            {
                if (l.WarehouseId != creator.WarehouseId || !warehouses.Contains(l.WarehouseId) || !keys.TryAdd(l.Id, new EpochLayerKey(l.WarehouseId, creator.Sequence, ordinal++))) throw Inconsistent();
            }
        }
        foreach (var l in snapshot.Layers.OrderBy(l => l.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            EpochLayerKey? source = null;
            if (l.SourceLayerId is { } s && !keys.TryGetValue(s, out source)) throw Inconsistent();
            var layer = new EpochLayerState(NewId(), keys[l.Id], l.Sequence, l.EntryId, false, Q(l.OriginalQuantity), A(l.OriginalAmount), source) { Quantity = Q(l.RemainingQuantity), Amount = A(l.RemainingAmount) };
            layer.BeforeQuantity = layer.Quantity; layer.BeforeAmount = layer.Amount;
            byActual[l.Id] = layer;
            if (!byOriginalEntry.TryGetValue(l.EntryId, out var list)) byOriginalEntry[l.EntryId] = list = [];
            list.Add(layer);
            all[l.WarehouseId].Add(layer);
        }
        var byKey = all.Values.SelectMany(x => x).ToDictionary(l => l.Key);
        var byHandle = all.Values.SelectMany(x => x).ToDictionary(l => l.Id);
        var takesOf = snapshot.Consumptions.GroupBy(c => c.EntryId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).ToList());
        foreach (var p in snapshot.Positions) CheckSums(all[p.WarehouseId], fifo, p.Quantity, p.Value);

        // ---- the simulation, in write order
        var content = ImmutableArray.CreateBuilder<EpochEntryContent>();
        var lastSequence = snapshot.Positions.ToDictionary(p => p.WarehouseId, p => p.LastSequence);
        var finals = snapshot.Positions.ToDictionary(p => p.WarehouseId, p => (Quantity: p.Quantity, Value: p.Value));

        EpochLayerCreation Create(EpochEntryPlan entry, int ordinal, BigInteger quantity, BigInteger amount, EpochLayerKey? source)
        {
            var list = all[entry.WarehouseId];
            var rank = list.Select(l => l.Rank).DefaultIfEmpty(0L).Max() + 1;          // the engine: max(layer sequence) + 1 on the position, zero-remaining layers included
            var layer = new EpochLayerState(NewId(), new EpochLayerKey(entry.WarehouseId, entry.Sequence, ordinal), rank, null, true, quantity, amount, source) { Touched = true, BeforeQuantity = 0, BeforeAmount = 0 };
            if (!byKey.TryAdd(layer.Key, layer)) throw Disagree();
            byHandle[layer.Id] = layer; list.Add(layer);
            return new EpochLayerCreation(layer.Key, rank, Q(quantity), A(amount), source);
        }

        for (var o = 0; o < schedule.Operations.Length; o++)
        {
            var operation = schedule.Operations[o];
            (IReadOnlyList<CostingMath.Take> Takes, EpochEntryPlan Entry)? pendingOut = null;
            for (var n = 0; n < operation.Entries.Length; n++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = operation.Entries[n];
                var previousOut = pendingOut; pendingOut = null;
                if (!warehouses.Contains(entry.WarehouseId) || entry.Sequence != lastSequence[entry.WarehouseId] + 1) throw Disagree();
                lastSequence[entry.WarehouseId] = entry.Sequence; finals[entry.WarehouseId] = (entry.QuantityAfter, entry.ValueAfter);
                var consumptions = ImmutableArray.CreateBuilder<EpochConsumption>();
                var created = ImmutableArray.CreateBuilder<EpochLayerCreation>();
                if (fifo)
                {
                    var quantity = Q(entry.Quantity); var amount = A(entry.Amount);
                    if (entry.Role == EpochRole.Storno)
                    {
                        if (!entries.TryGetValue(entry.OriginalEntryId ?? 0, out var original) || original.WarehouseId != entry.WarehouseId) throw Inconsistent();
                        if (original.Direction < 0)
                        {
                            // an outflow gets back exactly what it consumed: the same layers, the same amounts
                            if (!takesOf.TryGetValue(original.Id, out var takes) || takes.Any(t => t.Restores) || takes.Aggregate(BigInteger.Zero, (s, t) => s + Q(t.Quantity)) != quantity ||
                                takes.Aggregate(BigInteger.Zero, (s, t) => s + A(t.Amount)) != amount) throw Inconsistent();
                            foreach (var t in takes)
                            {
                                if (!byActual.TryGetValue(t.LayerId, out var layer) || layer.Key.WarehouseId != entry.WarehouseId) throw Inconsistent();
                                layer.Touched = true; layer.Quantity += Q(t.Quantity); layer.Amount += A(t.Amount);
                                if (layer.Quantity > layer.OriginalQuantity || layer.Amount > layer.OriginalAmount) throw Disagree();
                                consumptions.Add(new EpochConsumption(layer.Key, t.Quantity, t.Amount, true));
                            }
                        }
                        else
                        {
                            // an inflow is taken out again: the whole layers it created, which must be untouched now
                            if (!byOriginalEntry.TryGetValue(original.Id, out var layers) || layers.Count == 0) throw Inconsistent();
                            BigInteger sumQ = 0, sumA = 0;
                            foreach (var layer in layers.OrderBy(l => l.Rank))
                            {
                                if (layer.Quantity != layer.OriginalQuantity || layer.Amount != layer.OriginalAmount) throw new CostingException("epoch_layer_consumed");
                                layer.Touched = true;
                                consumptions.Add(new EpochConsumption(layer.Key, Q(layer.Quantity), A(layer.Amount), false));
                                sumQ += layer.Quantity; sumA += layer.Amount; layer.Quantity = 0; layer.Amount = 0;
                            }
                            if (sumQ != quantity || sumA != amount) throw Inconsistent();
                        }
                    }
                    else if (entry.Kind == CostEntryKind.Receipt)
                        created.Add(Create(entry, 0, quantity, amount, null));
                    else if (entry.Kind is CostEntryKind.Issue or CostEntryKind.TransferOut)
                    {
                        IReadOnlyList<CostingMath.Take> plan;
                        try { plan = CostingMath.PlanFifo(all[entry.WarehouseId].Where(l => l.Quantity.Sign > 0).Select(l => new CostingMath.LayerState(l.Id, l.Rank, l.Quantity, l.Amount)), quantity, snapshot.MoneyDecimals); }
                        catch (CostingException) { throw Disagree(); }
                        if (plan.Aggregate(BigInteger.Zero, (s, t) => s + t.Amount) != amount) throw Disagree();       // the engine's cost equals the replay's
                        foreach (var t in plan)
                        {
                            var layer = byHandle[t.LayerId];
                            layer.Touched = true; layer.Quantity -= t.Quantity; layer.Amount -= t.Amount;
                            consumptions.Add(new EpochConsumption(layer.Key, Q(t.Quantity), A(t.Amount), false));
                        }
                        if (entry.Kind == CostEntryKind.TransferOut) pendingOut = (plan, entry);
                    }
                    else if (entry.Kind == CostEntryKind.TransferIn)
                    {
                        // the destination receives the SAME layers (same quantity and exact amount each) in the same order
                        if (previousOut is not { } p || p.Entry.Quantity != entry.Quantity || p.Entry.Amount != entry.Amount || p.Entry.WarehouseId == entry.WarehouseId) throw Inconsistent();
                        var ordinal = 0;
                        foreach (var t in p.Takes) created.Add(Create(entry, ordinal++, t.Quantity, t.Amount, byHandle[t.LayerId].Key));
                    }
                    else throw Inconsistent();
                }
                content.Add(new EpochEntryContent(o, n, entry.WarehouseId, entry.Sequence, entry.OriginalEntryId, consumptions.ToImmutable(), created.ToImmutable()));
            }
        }

        // ---- the end state: the open layers sum to the position, and equal the open layers the revised trace proves
        var expected = schedule.ExpectedPositions.ToDictionary(p => p.PositionId);
        var totals = ImmutableArray.CreateBuilder<EpochPositionTotal>();
        foreach (var p in snapshot.Positions.OrderBy(p => p.WarehouseId.ToString("N"), StringComparer.Ordinal))
        {
            var (quantity, value) = finals[p.WarehouseId];
            CheckSums(all[p.WarehouseId], fifo, quantity, value);
            var openLayers = all[p.WarehouseId].Where(l => l.Quantity.Sign > 0).OrderBy(l => l.Rank).ToList();
            if (!expected.TryGetValue(p.WarehouseId, out var proven) || (proven.Quantity, proven.Value) != (quantity, value)) throw Disagree();
            if (fifo && !openLayers.Select(l => (l.Quantity, l.Amount)).SequenceEqual(proven.OpenLayers.Select(l => (l.RemainingQuantity, l.RemainingAmount)))) throw Disagree();
            totals.Add(new EpochPositionTotal(p.WarehouseId, p.Quantity, p.Value, p.LastSequence, quantity, value, lastSequence[p.WarehouseId], openLayers.Count));
        }
        var remaining = all.Values.SelectMany(x => x).Where(l => l.Touched)
            .OrderBy(l => l.Key.WarehouseId.ToString("N"), StringComparer.Ordinal).ThenBy(l => l.Key.EntrySequence).ThenBy(l => l.Key.Ordinal)
            .Select(l => new EpochLayerRemaining(l.Key, l.Rank, l.OriginalEntry, l.Created, Q(l.OriginalQuantity), A(l.OriginalAmount), Q(l.BeforeQuantity), A(l.BeforeAmount), Q(l.Quantity), A(l.Amount))).ToImmutableArray();
        var entriesResult = content.ToImmutable(); var totalsResult = totals.ToImmutable();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Contract = "epoch-content-v1", Schedule = schedule.Fingerprint, Entries = entriesResult, Layers = remaining, Positions = totalsResult }))).ToLowerInvariant();
        return new EpochContentResult(entriesResult, remaining, totalsResult, fingerprint);
    }

    // FIFO: the layers of a position add up exactly to its quantity and value (the ledger and the layers agree); average: no layers exist
    private static void CheckSums(List<EpochLayerState> layers, bool fifo, decimal quantity, decimal value)
    {
        if (!fifo) return;
        if (layers.Aggregate(BigInteger.Zero, (s, l) => s + l.Quantity) != Q(quantity) || layers.Aggregate(BigInteger.Zero, (s, l) => s + l.Amount) != A(value) ||
            layers.Any(l => l.Quantity.Sign < 0 || l.Amount.Sign < 0)) throw Disagree();
    }
}
