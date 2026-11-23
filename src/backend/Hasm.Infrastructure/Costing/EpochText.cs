using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hasm.Infrastructure.Costing;

/// <summary>One epoch operation as the T2 epoch text names it (O line). <see cref="OriginalOperation"/> is null for the event (role 2); <see cref="SourceType"/> is the plain text (hashed by the builder), null when NULL;
/// <see cref="SourceId"/> is the text <c>execution-posting</c>, a uuid or null; <see cref="Key"/> is the plain operation key (hashed by the builder).</summary>
internal sealed record EpochTextOperation(short Role, Guid? OriginalOperation, short Kind, DateOnly EffectiveDate, Guid? ReversesOperation, string? SourceType, string? SourceId, string Key, Guid CreatedBy);

/// <summary>One epoch entry (E line). <see cref="Origin"/> is the original entry id of a storno or rebook, null for the event.</summary>
internal sealed record EpochTextEntry(Guid Item, Guid Warehouse, long Sequence, short Role, long? Origin, short Kind, short Direction, DateOnly Date, decimal Quantity, decimal Amount, decimal QuantityAfter, decimal ValueAfter);

/// <summary>One layer of an epoch entry (L line); the source layer is named by item, warehouse and layer sequence.</summary>
internal sealed record EpochTextLayer(Guid Item, Guid Warehouse, long LayerSequence, long EntrySequence, decimal OriginalQuantity, decimal OriginalAmount, Guid? SourceItem, Guid? SourceWarehouse, long? SourceLayerSequence);

/// <summary>One consumption row of an epoch entry (C line).</summary>
internal sealed record EpochTextConsumption(Guid EntryItem, Guid EntryWarehouse, long EntrySequence, Guid LayerItem, Guid LayerWarehouse, long LayerSequence, decimal Quantity, decimal Amount, bool Restores);

/// <summary>The remaining figures of a layer after the whole epoch (R line).</summary>
internal sealed record EpochTextRemaining(Guid Item, Guid Warehouse, long LayerSequence, decimal Quantity, decimal Amount);

/// <summary>The totals of one position the epoch touched (P line, design 4.4 version 5): the last sequence before the epoch, the last sequence after it, the final quantity and value and the number of open layers.</summary>
internal sealed record EpochTextPosition(Guid Item, Guid Warehouse, long BaselineLastSequence, long FinalLastSequence, decimal FinalQuantity, decimal FinalValue, int OpenLayers);

internal sealed record EpochTextInput(ImmutableArray<EpochTextOperation> Operations, ImmutableArray<EpochTextEntry> Entries, ImmutableArray<EpochTextLayer> Layers,
    ImmutableArray<EpochTextConsumption> Consumptions, ImmutableArray<EpochTextRemaining> Remaining, ImmutableArray<EpochTextPosition> Positions);

/// <summary>The epoch text and its digest. <see cref="Digest"/> is the lowercase hex SHA-256 of the UTF-8 bytes of <see cref="Text"/>; an epoch with no rows hashes as the empty string.</summary>
internal sealed record EpochTextResult(string Text, string Digest);

/// <summary>
/// Phase 3.8D executor slice E1b: the BYTE-EXACT epoch text of the T2 design (section 4.4, message version <c>hasm-replay-v4</c>): five groups O, E, L, C, R, LF-joined with no trailing LF, ordered as the design
/// orders them, numerics at their column scale (quantity 10, amount 6) in the invariant culture, uuids lower case, booleans t or f, dates yyyy-MM-dd, source type and key as the hex SHA-256 of their UTF-8 bytes. Pure: no database,
/// clock or randomness. Version 5 of the design (the P group) is built; the builder sorts its input itself, so the text never depends on the order the rows were produced in.
/// </summary>
internal static class EpochText
{
    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string Id(Guid? id) => id is { } v ? Id(v) : "-";
    private static string Q(decimal v) => v.ToString("F10", CultureInfo.InvariantCulture);
    private static string A(decimal v) => v.ToString("F6", CultureInfo.InvariantCulture);
    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Flag(bool v) => v ? "t" : "f";
    private static string Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static int Compare(Guid a, Guid b) => string.CompareOrdinal(Id(a), Id(b));      // the lower-case hyphenated text sorts as PostgreSQL's uuid order does

    internal static EpochTextResult Build(EpochTextInput input)
    {
        if (input is null || input.Operations.IsDefault || input.Entries.IsDefault || input.Layers.IsDefault || input.Consumptions.IsDefault || input.Remaining.IsDefault || input.Positions.IsDefault)
            throw new CostingException("invalid_impact_snapshot");
        var lines = new List<string>();
        foreach (var o in input.Operations.OrderBy(o => o.Role).ThenBy(o => o.OriginalOperation is { } g ? Id(g) : "", StringComparer.Ordinal))
            lines.Add(string.Join('|', "O", Num(o.Role), Id(o.OriginalOperation), Num(o.Kind), Date(o.EffectiveDate), Id(o.ReversesOperation),
                o.SourceType is null ? "-" : Hex(o.SourceType), o.SourceId ?? "-", Hex(o.Key), Id(o.CreatedBy)));
        foreach (var e in input.Entries.OrderBy(e => Id(e.Item), StringComparer.Ordinal).ThenBy(e => Id(e.Warehouse), StringComparer.Ordinal).ThenBy(e => e.Sequence))
            lines.Add(string.Join('|', "E", Id(e.Item), Id(e.Warehouse), Num(e.Sequence), Num(e.Role), e.Origin is { } origin ? Num(origin) : "-", Num(e.Kind), Num(e.Direction),
                Date(e.Date), Q(e.Quantity), A(e.Amount), Q(e.QuantityAfter), A(e.ValueAfter)));
        foreach (var l in input.Layers.OrderBy(l => Id(l.Item), StringComparer.Ordinal).ThenBy(l => Id(l.Warehouse), StringComparer.Ordinal).ThenBy(l => l.LayerSequence))
            lines.Add(string.Join('|', "L", Id(l.Item), Id(l.Warehouse), Num(l.LayerSequence), Num(l.EntrySequence), Q(l.OriginalQuantity), A(l.OriginalAmount),
                l.SourceItem is { } si && l.SourceWarehouse is { } sw && l.SourceLayerSequence is { } ss ? Id(si) + "/" + Id(sw) + "/" + Num(ss) : "-"));
        foreach (var c in input.Consumptions.OrderBy(c => Id(c.EntryItem), StringComparer.Ordinal).ThenBy(c => Id(c.EntryWarehouse), StringComparer.Ordinal).ThenBy(c => c.EntrySequence)
                     .ThenBy(c => Id(c.LayerItem), StringComparer.Ordinal).ThenBy(c => Id(c.LayerWarehouse), StringComparer.Ordinal).ThenBy(c => c.LayerSequence).ThenBy(c => c.Restores))
            lines.Add(string.Join('|', "C", Id(c.EntryItem), Id(c.EntryWarehouse), Num(c.EntrySequence), Id(c.LayerItem), Id(c.LayerWarehouse), Num(c.LayerSequence), Q(c.Quantity), A(c.Amount), Flag(c.Restores)));
        foreach (var r in input.Remaining.OrderBy(r => Id(r.Item), StringComparer.Ordinal).ThenBy(r => Id(r.Warehouse), StringComparer.Ordinal).ThenBy(r => r.LayerSequence))
            lines.Add(string.Join('|', "R", Id(r.Item), Id(r.Warehouse), Num(r.LayerSequence), Q(r.Quantity), A(r.Amount)));
        foreach (var p in input.Positions.OrderBy(p => Id(p.Item), StringComparer.Ordinal).ThenBy(p => Id(p.Warehouse), StringComparer.Ordinal))
            lines.Add(string.Join('|', "P", Id(p.Item), Id(p.Warehouse), Num(p.BaselineLastSequence), Num(p.FinalLastSequence), Q(p.FinalQuantity), A(p.FinalValue), Num(p.OpenLayers)));
        var text = string.Join('\n', lines);
        return new EpochTextResult(text, Hex(text));
    }

    /// <summary>
    /// Assembles the text input of an epoch from the schedule, its content and the plan's identity: the deterministic operation keys <c>epoch:&lt;plan&gt;:1:&lt;original operation&gt;</c>, <c>epoch:&lt;plan&gt;:2</c> and
    /// <c>epoch:&lt;plan&gt;:3:&lt;original operation&gt;</c>, the source types <c>replay-storno</c>, <c>stock-posting</c> (the event) and <c>replay-rebook</c>, the event's source id <c>execution-posting</c> (storno and rebook: NULL),
    /// reverses-operation NULL, and the creator as an input. Entries and layers are keyed by (item, warehouse, sequence); a layer's source and every consumed layer by their arrival ranks, taken from the remaining figures.
    /// </summary>
    internal static EpochTextInput FromEpoch(CostFlowSnapshot snapshot, EpochScheduleResult schedule, EpochContentResult content, Guid planId, Guid createdBy)
    {
        if (snapshot is null || schedule is null || content is null || planId == Guid.Empty || createdBy == Guid.Empty || content.Entries.Length != schedule.Operations.Sum(o => o.Entries.Length))
            throw new CostingException("invalid_impact_snapshot");
        var item = snapshot.ItemId;
        var rank = new Dictionary<EpochLayerKey, long>();
        foreach (var r in content.Layers) rank[r.Key] = r.ArrivalRank;
        long Rank(EpochLayerKey key) => rank.TryGetValue(key, out var v) ? v : throw new CostingException("epoch_schedule_inconsistent");
        var operations = ImmutableArray.CreateBuilder<EpochTextOperation>();
        var entries = ImmutableArray.CreateBuilder<EpochTextEntry>();
        var layers = ImmutableArray.CreateBuilder<EpochTextLayer>();
        var consumptions = ImmutableArray.CreateBuilder<EpochTextConsumption>();
        var plan = Id(planId);
        for (var o = 0; o < schedule.Operations.Length; o++)
        {
            var operation = schedule.Operations[o];
            var original = operation.OriginalOperationId is { } g ? Id(g) : "";
            var (sourceType, sourceId, key) = operation.Role switch
            {
                EpochRole.Storno => ("replay-storno", (string?)null, $"epoch:{plan}:1:{original}"),
                EpochRole.Event => ("stock-posting", "execution-posting", $"epoch:{plan}:2"),
                _ => ("replay-rebook", (string?)null, $"epoch:{plan}:3:{original}"),
            };
            operations.Add(new EpochTextOperation((short)operation.Role, operation.OriginalOperationId, (short)operation.Kind, operation.Date, null, sourceType, sourceId, key, createdBy));
            for (var n = 0; n < operation.Entries.Length; n++)
            {
                var e = operation.Entries[n]; var c = content.Entries.Single(x => x.Operation == o && x.Entry == n);
                entries.Add(new EpochTextEntry(item, e.WarehouseId, e.Sequence, (short)e.Role, e.OriginalEntryId, (short)e.Kind, (short)e.Direction, e.Date, e.Quantity, e.Amount, e.QuantityAfter, e.ValueAfter));
                foreach (var l in c.Layers)
                    layers.Add(new EpochTextLayer(item, l.Key.WarehouseId, l.ArrivalRank, l.Key.EntrySequence, l.Quantity, l.Amount,
                        l.Source is null ? null : item, l.Source?.WarehouseId, l.Source is null ? null : Rank(l.Source)));
                foreach (var u in c.Consumptions)
                    consumptions.Add(new EpochTextConsumption(item, e.WarehouseId, e.Sequence, item, u.Layer.WarehouseId, Rank(u.Layer), u.Quantity, u.Amount, u.Restores));
            }
        }
        var remaining = content.Layers.Select(r => new EpochTextRemaining(item, r.Key.WarehouseId, r.ArrivalRank, r.AfterQuantity, r.AfterAmount)).ToImmutableArray();
        // one P line per position the epoch wrote an entry on (its last sequence moved)
        var positions = content.Positions.Where(p => p.LastSequence > p.BaselineLastSequence)
            .Select(p => new EpochTextPosition(item, p.WarehouseId, p.BaselineLastSequence, p.LastSequence, p.Quantity, p.Value, p.OpenLayers)).ToImmutableArray();
        return new EpochTextInput(operations.ToImmutable(), entries.ToImmutable(), layers.ToImmutable(), consumptions.ToImmutable(), remaining, positions);
    }
}
