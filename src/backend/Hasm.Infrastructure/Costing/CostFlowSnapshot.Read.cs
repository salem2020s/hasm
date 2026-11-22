using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;

namespace Hasm.Infrastructure.Costing;

internal sealed partial class CostFlowSnapshotService
{
    private sealed record Data(ImmutableArray<CostFlowPosition> Positions, ImmutableArray<CostFlowEntry> Entries,
        ImmutableArray<CostFlowLayer> Layers, ImmutableArray<CostFlowConsumption> Consumptions);

    private async Task<Data> ReadData(NpgsqlConnection conn, NpgsqlTransaction tx, Budget budget, Guid item, CostMethod method,
        Guid[] warehouses, Guid[] operations, CancellationToken ct)
    {
        NpgsqlCommand Query(string sql)
        {
            var q = budget.Command(sql, conn, tx); q.Parameters.AddWithValue("i", item);
            q.Parameters.AddWithValue("ws", warehouses); q.Parameters.AddWithValue("ops", operations); return q;
        }
        await using (var q = Query($"SELECT count(*) FROM {W.Table} WHERE {W.Id}=ANY(@ws)"))
            if ((long)(await q.ExecuteScalarAsync(ct))! != warehouses.Length) throw Inconsistent();
        await using (var q = Query($"SELECT EXISTS(SELECT 1 FROM {NO.Table} WHERE {NO.ItemId}=@i AND {NO.WarehouseId}=ANY(@ws))"))
            if ((bool)(await q.ExecuteScalarAsync(ct))!) throw Unsupported(); // Includes settled/reversed obligations.
        var positions = new Dictionary<Guid, CostFlowPosition>();
        await using (var q = Query($"SELECT {CP.Id},{CP.WarehouseId},{CP.Method},{CP.LastSeq},{CP.LastEffectiveDate},{CP.Quantity},{CP.Value} FROM {CP.Table} WHERE {CP.ItemId}=@i AND {CP.WarehouseId}=ANY(@ws) LIMIT @limit"))
        {
            q.Parameters.AddWithValue("limit", limits.Warehouses + 1); await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (positions.Count == limits.Warehouses) throw TooLarge();
                var p = new CostFlowPosition(r.GetGuid(1), r.GetGuid(0), (CostMethod)r.GetInt16(2), r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4), r.GetDecimal(5), r.GetDecimal(6));
                if (!positions.TryAdd(p.WarehouseId, p)) throw Inconsistent();
            }
        }
        foreach (var warehouse in warehouses) positions.TryAdd(warehouse, new(warehouse, null, method, 0, null, 0, 0));
        var entries = ImmutableArray.CreateBuilder<CostFlowEntry>();
        var proof = $"EXISTS(SELECT 1 FROM {SD.Table} d JOIN {SP.Table} sp ON sp.{SP.DocumentId}=d.{SD.Id} " +
            $"WHERE d.{SD.Id}=o.{CO.SourceId} AND sp.{SP.CostOperationId}=o.{CO.Id} AND d.{SD.DocType}=o.{CO.Kind} AND sp.{SP.DocType}=o.{CO.Kind} " +
            // Posting is a separate immutable fact; the document itself remains Approved.
            $"AND d.{SD.Status}={(int)StockDocStatus.Approved} AND d.{SD.DocumentDate}=o.{CO.EffectiveDate} AND sp.{SP.EffectiveDate}=o.{CO.EffectiveDate} AND (" +
            $"(e.{CE.Kind}=1 AND d.{SD.SourceWarehouseId} IS NULL AND d.{SD.TargetWarehouseId}=e.{CE.WarehouseId}) OR " +
            $"(e.{CE.Kind}=2 AND d.{SD.TargetWarehouseId} IS NULL AND d.{SD.SourceWarehouseId}=e.{CE.WarehouseId}) OR " +
            $"(e.{CE.Kind}=3 AND d.{SD.SourceWarehouseId}=e.{CE.WarehouseId}) OR (e.{CE.Kind}=4 AND d.{SD.TargetWarehouseId}=e.{CE.WarehouseId})))";
        await using (var q = Query($"SELECT e.{CE.Id},e.{CE.OperationId},e.{CE.PositionId},e.{CE.ItemId},e.{CE.WarehouseId},e.{CE.Seq},e.{CE.EffectiveDate},e.{CE.Kind},e.{CE.Direction}," +
            $"e.{CE.Quantity},e.{CE.Amount},e.{CE.QuantityAfter},e.{CE.ValueAfter},e.{CE.ReversesEntryId},o.{CO.Id},o.{CO.Kind},o.{CO.EffectiveDate},o.{CO.SourceType},o.{CO.SourceId},o.{CO.ReversesOperationId},o.{CO.ReversedByOperationId},{proof} " +
            $"FROM {CE.Table} e LEFT JOIN {CP.Table} p ON p.{CP.Id}=e.{CE.PositionId} LEFT JOIN {CO.Table} o ON o.{CO.Id}=e.{CE.OperationId} " +
            $"WHERE {SelectedEntry("e", "p")} OR (e.{CE.ItemId}=@i AND e.{CE.OperationId}=ANY(@ops)) LIMIT @limit"))
        {
            q.Parameters.AddWithValue("limit", limits.Entries + 1); await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                budget.Entry(); if (r.IsDBNull(14)) throw Inconsistent();
                entries.Add(new(r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), r.GetInt64(5), r.GetFieldValue<DateOnly>(6),
                    (CostEntryKind)r.GetInt16(7), r.GetInt16(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12),
                    (CostOperationKind)r.GetInt16(15), r.GetFieldValue<DateOnly>(16), r.IsDBNull(17) ? null : r.GetString(17),
                    r.IsDBNull(18) ? null : r.GetGuid(18), !r.IsDBNull(13) || !r.IsDBNull(19) || !r.IsDBNull(20), r.GetBoolean(21)));
            }
        }
        var entryIds = entries.Select(e => e.Id).ToArray();
        var layers = ImmutableArray.CreateBuilder<CostFlowLayer>();
        await using (var q = Query($"SELECT l.{CL.Id},l.{CL.PositionId},l.{CL.ItemId},l.{CL.WarehouseId},l.{CL.EntryId},l.{CL.Seq},l.{CL.OriginalQuantity},l.{CL.OriginalAmount},l.{CL.RemainingQuantity},l.{CL.RemainingAmount},l.{CL.SourceLayerId} " +
            $"FROM {CL.Table} l LEFT JOIN {CP.Table} p ON p.{CP.Id}=l.{CL.PositionId} WHERE {SelectedLayer("l", "p")} OR l.{CL.EntryId}=ANY(@entries) LIMIT @limit"))
        {
            q.Parameters.AddWithValue("entries", entryIds); q.Parameters.AddWithValue("limit", budget.ComponentRemaining + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                budget.Component();
                layers.Add(new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetInt64(4), r.GetInt64(5),
                    r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetGuid(10)));
            }
        }
        var consumptions = ImmutableArray.CreateBuilder<CostFlowConsumption>();
        await using (var q = Query($"SELECT {CC.Id},{CC.EntryId},{CC.LayerId},{CC.Quantity},{CC.Amount},{CC.Restores} FROM {CC.Table} WHERE {CC.EntryId}=ANY(@entries) OR {CC.LayerId}=ANY(@layers) LIMIT @limit"))
        {
            q.Parameters.AddWithValue("entries", entryIds); q.Parameters.AddWithValue("layers", layers.Select(l => l.Id).ToArray());
            q.Parameters.AddWithValue("limit", budget.ComponentRemaining + 1); await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                budget.Component(); consumptions.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetGuid(2), r.GetDecimal(3), r.GetDecimal(4), r.GetBoolean(5)));
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(positions.Values.OrderBy(p => p.WarehouseId.ToString("N"), StringComparer.Ordinal).ToImmutableArray(),
            entries.OrderBy(e => e.Id).ToImmutableArray(), layers.OrderBy(l => l.Id.ToString("N"), StringComparer.Ordinal).ToImmutableArray(),
            consumptions.OrderBy(c => c.Id).ToImmutableArray());
    }
}
