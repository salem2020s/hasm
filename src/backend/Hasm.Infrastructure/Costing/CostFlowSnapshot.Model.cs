using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hasm.Infrastructure.Costing;

internal sealed record CostFlowPosition(Guid WarehouseId, Guid? PositionId, CostMethod Method, long LastSequence,
    DateOnly? LastDate, decimal Quantity, decimal Value);
internal sealed record CostFlowEntry(long Id, Guid OperationId, Guid PositionId, Guid ItemId, Guid WarehouseId, long Sequence,
    DateOnly Date, CostEntryKind EntryKind, int Direction, decimal Quantity, decimal Amount, decimal QuantityAfter, decimal ValueAfter,
    CostOperationKind OperationKind, DateOnly OperationDate, string? SourceType, Guid? SourceId, bool HasReversal, bool DocumentProof);
internal sealed record CostFlowLayer(Guid Id, Guid PositionId, Guid ItemId, Guid WarehouseId, long EntryId, long Sequence,
    decimal OriginalQuantity, decimal OriginalAmount, decimal RemainingQuantity, decimal RemainingAmount, Guid? SourceLayerId);
internal sealed record CostFlowConsumption(long Id, long EntryId, Guid LayerId, decimal Quantity, decimal Amount, bool Restores);
internal sealed record CostFlowLayerBinding(Guid SimulationId, Guid ActualId);
internal sealed record CostFlowSnapshot(Guid CompanyId, Guid ItemId, CostMethod Method, string CurrencyCode, int MoneyDecimals,
    ImmutableArray<CostFlowPosition> Positions, ImmutableArray<CostFlowEntry> Entries, ImmutableArray<CostFlowLayer> Layers,
    ImmutableArray<CostFlowConsumption> Consumptions, ImmutableArray<CostFlowLayerBinding> LayerBindings,
    ImmutableArray<ReplayFlowBlock> Blocks, string HistoryFingerprint);

internal sealed partial class CostFlowSnapshotService
{
    private static CostingException Inconsistent() => new("impact_history_inconsistent");
    private static CostingException Unsupported() => new("impact_history_unsupported");
    private static CostingException TooLarge() => new("impact_history_too_large");

    // Called only after full metadata quarantine/live scope checks and bounded reads in ONE snapshot.
    // This proves supported baseline arithmetic/provenance, not source approval or execution authority.
    private static CostFlowSnapshot Reconcile(Guid company, Guid item, CostMethod method, string currency, int decimals,
        ImmutableArray<CostFlowPosition> positions, ImmutableArray<CostFlowEntry> entries,
        ImmutableArray<CostFlowLayer> layers, ImmutableArray<CostFlowConsumption> consumptions, CancellationToken ct)
    {
        var byWarehouse = positions.ToDictionary(p => p.WarehouseId);
        var byPosition = positions.Where(p => p.PositionId is not null).ToDictionary(p => p.PositionId!.Value);
        var byEntry = new Dictionary<long, CostFlowEntry>();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Id <= 0 || !byEntry.TryAdd(e.Id, e) || e.OperationId == Guid.Empty || e.ItemId != item ||
                !byPosition.TryGetValue(e.PositionId, out var pos) || pos.WarehouseId != e.WarehouseId || e.Date != e.OperationDate) throw Inconsistent();
            if (e.OperationKind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer) || e.HasReversal ||
                (e.SourceType is null ? e.SourceId is not null : e.SourceType != "stock_document" || e.SourceId is null || !e.DocumentProof)) throw Unsupported();
        }
        var perPosition = entries.GroupBy(e => e.WarehouseId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Sequence).ToArray());
        foreach (var p in positions)
        {
            ct.ThrowIfCancellationRequested();
            var rows = perPosition.GetValueOrDefault(p.WarehouseId) ?? [];
            if (p.Method != method) throw new CostingException("valuation_method_changed");
            if (p.LastSequence != rows.Length || (p.PositionId is null && rows.Length != 0) || p.Quantity < 0 || p.Value < 0 ||
                (rows.Length == 0 ? p.LastDate is not null : p.LastDate != rows[^1].Date)) throw Inconsistent();
            for (var n = 0; n < rows.Length; n++)
                if (rows[n].Sequence != n + 1L || (n > 0 && rows[n].Date < rows[n - 1].Date)) throw Inconsistent();
        }
        var blocks = ImmutableArray.CreateBuilder<ReplayFlowBlock>();
        var eventEntries = new Dictionary<(Guid Event, Guid Warehouse), CostFlowEntry>();
        foreach (var group in entries.GroupBy(e => e.OperationId).OrderBy(g => g.Key.ToString("N"), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var rows = group.OrderBy(e => e.Id).ToArray(); var first = rows[0];
            if (rows.Any(e => (e.OperationKind, e.OperationDate, e.SourceType, e.SourceId) !=
                (first.OperationKind, first.OperationDate, first.SourceType, first.SourceId))) throw Inconsistent();
            var legs = rows.GroupBy(e => e.WarehouseId).Select(g => new ReplayOrderLeg(g.Key, g.Min(e => e.Sequence), g.LongCount())).ToImmutableArray();
            var children = ImmutableArray.CreateBuilder<ReplayFlowChild>();
            if (first.OperationKind == CostOperationKind.Transfer)
            {
                var source = rows.Where(e => e.EntryKind == CostEntryKind.TransferOut && e.Direction == -1).ToArray();
                var target = rows.Where(e => e.EntryKind == CostEntryKind.TransferIn && e.Direction == 1).ToArray();
                if (rows.Length != 2 || source.Length != 1 || target.Length != 1) throw Inconsistent();
                var a = source[0]; var b = target[0];
                if (a.WarehouseId == b.WarehouseId || a.Id >= b.Id || a.Quantity != b.Quantity || a.Amount != b.Amount) throw Inconsistent();
                children.Add(new ReplayFlowChild(group.Key, a.Id, a.Sequence,
                    new(group.Key, CostOperationKind.Transfer, a.WarehouseId, a.Quantity, DestinationPositionId: b.WarehouseId), b.Sequence));
                if (!eventEntries.TryAdd((group.Key, a.WarehouseId), a) || !eventEntries.TryAdd((group.Key, b.WarehouseId), b)) throw Inconsistent();
            }
            else
                foreach (var e in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var receipt = e.OperationKind == CostOperationKind.Receipt;
                    if (e.EntryKind != (receipt ? CostEntryKind.Receipt : CostEntryKind.Issue) || e.Direction != (receipt ? 1 : -1)) throw Inconsistent();
                    var id = CostHistoryEntry.SimulationId(e.Id);
                    children.Add(new ReplayFlowChild(group.Key, e.Id, e.Sequence,
                        new(id, e.OperationKind, e.WarehouseId, e.Quantity, receipt ? e.Amount : null)));
                    if (!eventEntries.TryAdd((id, e.WarehouseId), e)) throw Inconsistent();
                }
            blocks.Add(new ReplayFlowBlock(new(group.Key, first.Date, first.OperationKind, legs), children.ToImmutable()));
        }
        var immutableBlocks = blocks.ToImmutable();
        var replay = CostReplay.CalculateBlocks(method, decimals, positions.Select(p => p.WarehouseId).ToImmutableArray(), immutableBlocks, ct).Flow;
        foreach (var step in replay.Steps)
            foreach (var leg in step.Legs)
            {
                ct.ThrowIfCancellationRequested();
                if (!eventEntries.TryGetValue((step.EventId, leg.PositionId), out var e) ||
                    (e.Amount, e.QuantityAfter, e.ValueAfter) != (leg.Step.Amount, leg.Step.QuantityAfter, leg.Step.ValueAfter)) throw Inconsistent();
            }
        foreach (var p in replay.Positions)
            if ((p.Quantity, p.Value) != (byWarehouse[p.PositionId].Quantity, byWarehouse[p.PositionId].Value)) throw Inconsistent();

        var actualLayers = new Dictionary<Guid, CostFlowLayer>();
        foreach (var layer in layers)
        {
            ct.ThrowIfCancellationRequested();
            if (layer.Id == Guid.Empty || !actualLayers.TryAdd(layer.Id, layer) || layer.ItemId != item ||
                !byPosition.TryGetValue(layer.PositionId, out var position) || position.WarehouseId != layer.WarehouseId ||
                !byEntry.TryGetValue(layer.EntryId, out var entry) || entry.PositionId != layer.PositionId ||
                layer.Sequence <= 0 || layer.OriginalQuantity <= 0 || layer.OriginalAmount < 0 || layer.RemainingQuantity < 0 || layer.RemainingAmount < 0 ||
                layer.RemainingQuantity > layer.OriginalQuantity || layer.RemainingAmount > layer.OriginalAmount ||
                (layer.RemainingQuantity == 0 && layer.RemainingAmount != 0)) throw Inconsistent();
        }
        var layersByEntry = layers.GroupBy(l => l.EntryId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Sequence).ToArray());
        var layerMap = new Dictionary<Guid, Guid>();
        var arrival = new Dictionary<Guid, long>();
        var bindingBuilder = ImmutableArray.CreateBuilder<CostFlowLayerBinding>();
        if (layers.Length != replay.CreatedLayers.Length) throw Inconsistent();
        foreach (var made in replay.CreatedLayers)
        {
            ct.ThrowIfCancellationRequested();
            var entry = eventEntries[(made.EventId, made.PositionId)];
            if (!layersByEntry.TryGetValue(entry.Id, out var actual) || made.Component > actual.Length) throw Inconsistent();
            var layer = actual[made.Component - 1];
            var next = arrival.GetValueOrDefault(made.PositionId) + 1; arrival[made.PositionId] = next;
            Guid? parent = null;
            if (made.SourceLayerId is { } source)
            {
                if (!layerMap.TryGetValue(source, out var parentId)) throw Inconsistent();
                parent = parentId;
            }
            if (layer.Sequence != next || layer.SourceLayerId != parent ||
                (layer.OriginalQuantity, layer.OriginalAmount) != (CostingMath.FromUnits(made.Quantity, CostingMath.QuantityScale), CostingMath.FromUnits(made.Amount, CostingMath.AmountScale)) ||
                !layerMap.TryAdd(made.LayerId, layer.Id)) throw Inconsistent();
            bindingBuilder.Add(new(made.LayerId, layer.Id));
        }
        var open = replay.Positions.SelectMany(p => p.OpenLayers).ToDictionary(l => layerMap[l.Id]);
        foreach (var layer in layers)
        {
            ct.ThrowIfCancellationRequested();
            var expected = open.GetValueOrDefault(layer.Id);
            if ((layer.RemainingQuantity, layer.RemainingAmount) != (expected is null ? 0m : CostingMath.FromUnits(expected.RemainingQuantity, CostingMath.QuantityScale),
                expected is null ? 0m : CostingMath.FromUnits(expected.RemainingAmount, CostingMath.AmountScale))) throw Inconsistent();
        }
        var consumptionIds = new HashSet<long>();
        foreach (var take in consumptions)
            if (take.Id <= 0 || !consumptionIds.Add(take.Id) || take.Restores || !byEntry.ContainsKey(take.EntryId) || !actualLayers.ContainsKey(take.LayerId)) throw Inconsistent();
        var takesByEntry = consumptions.GroupBy(c => c.EntryId).ToDictionary(g => g.Key,
            g => g.OrderBy(c => actualLayers[c.LayerId].Sequence).ThenBy(c => c.Id).ToArray());
        var checkedTakes = 0;
        foreach (var step in replay.Steps)
            foreach (var leg in step.Legs)
            {
                ct.ThrowIfCancellationRequested();
                var entry = eventEntries[(step.EventId, leg.PositionId)]; var original = takesByEntry.GetValueOrDefault(entry.Id) ?? [];
                if (original.Length != leg.Step.Consumptions.Length) throw Inconsistent();
                for (var n = 0; n < original.Length; n++)
                {
                    ct.ThrowIfCancellationRequested(); var a = original[n]; var b = leg.Step.Consumptions[n];
                    if (actualLayers[a.LayerId].PositionId != entry.PositionId || a.LayerId != layerMap[b.LayerId] ||
                        (a.Quantity, a.Amount) != (CostingMath.FromUnits(b.Quantity, CostingMath.QuantityScale), CostingMath.FromUnits(b.Amount, CostingMath.AmountScale))) throw Inconsistent();
                    checkedTakes++;
                }
            }
        if (checkedTakes != consumptions.Length) throw Inconsistent();
        var bindings = bindingBuilder.ToImmutable();
        ct.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = "cost-flow-snapshot-v1", company, item, method, currency, decimals, positions, entries, layers, consumptions, bindings });
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        ct.ThrowIfCancellationRequested();
        return new(company, item, method, currency, decimals, positions, entries, layers, consumptions, bindings, immutableBlocks, fingerprint);
    }
}
