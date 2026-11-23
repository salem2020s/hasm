using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;

namespace Hasm.Infrastructure.Costing;

internal sealed record CostHistoryEntry(long EntryId, Guid OperationId, long Sequence, DateOnly EffectiveDate, CostOperationKind Kind,
    decimal Quantity, decimal Amount, decimal QuantityAfter, decimal ValueAfter, string? SourceType, Guid? SourceId)
{
    // An injective simulation-only identity for the company's positive bigint entry ID, not a stored cost-layer ID.
    internal ReplayEvent ToReplayEvent()
        => new(SimulationId(EntryId), Kind, Quantity, Kind == CostOperationKind.Receipt ? Amount : null);

    internal static Guid SimulationId(long entryId)
    {
        Span<byte> bytes = stackalloc byte[16];
        "HSMENTRY"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], entryId);
        return new Guid(bytes);
    }
}

/// <summary>Immutable input evidence only. HistoryFingerprint is NOT an approval, authorization or execution token.</summary>
internal sealed record CostHistorySnapshot(Guid CompanyId, Guid ItemId, Guid WarehouseId, Guid? PositionId, CostMethod Method,
    string CurrencyCode, int MoneyDecimals, long LastSequence, decimal Quantity, decimal Value,
    ImmutableArray<CostHistoryEntry> Entries, string HistoryFingerprint);

/// <summary>
/// Phase 3.8D-1 internal authorized read-only snapshot adapter; no API or production caller yet. Uses the same warehouse-scoped cost visibility as stock balances.
/// A REPEATABLE READ, READ ONLY transaction reads one complete ordinary receipt/issue trajectory. Unsupported origin/history is refused in full, never omitted.
/// No positions/layers/journals/audit entries are created, and no posting lock is taken. This is not a public preview or execution plan.
/// </summary>
internal sealed class CostHistorySnapshotService(CompanyDatabaseResolver resolver, AccessService access, int maxEntries = 100_000)
{
    // Test-only observation point after the transaction snapshot is established; null in application construction.
    internal Func<NpgsqlConnection, NpgsqlTransaction, Task>? SnapshotEstablished { get; set; }

    private static ConfigResult<CostHistorySnapshot> Fail(ConfigStatus status, string code) => ConfigResult<CostHistorySnapshot>.Fail(status, code);
    private Task<bool> Can(Guid user, Guid company, Guid warehouse, CancellationToken ct) =>
        access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockBalanceView, ScopeTarget.Warehouse(warehouse), ct);

    internal async Task<ConfigResult<CostHistorySnapshot>> LoadAsync(Guid user, Guid company, Guid item, Guid warehouse, CancellationToken ct = default)
    {
        if (!await Can(user, company, warehouse, ct)) return Fail(ConfigStatus.Forbidden, "forbidden");
        ConfigResult<CostHistorySnapshot> result;
        try { result = await ReadAsync(company, item, warehouse, ct); }
        catch (CostingException ex) { result = Fail(ConfigStatus.Conflict, ex.Code); } // stable codes only; never physical names or hidden history details
        // Permissions live in the system database, not this company snapshot. Never cache or treat the data fingerprint as authorization.
        return await Can(user, company, warehouse, ct) ? result : Fail(ConfigStatus.Forbidden, "forbidden");
    }

    private async Task<ConfigResult<CostHistorySnapshot>> ReadAsync(Guid company, Guid item, Guid warehouse, CancellationToken ct)
    {
        if (maxEntries is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        await using var conn = await resolver.OpenAsync(company, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await mode.ExecuteNonQueryAsync(ct);
        await using (var q = new NpgsqlCommand($"SELECT 1 FROM {W.Table} WHERE {W.Id}=@w", conn, tx))
        {
            q.Parameters.AddWithValue("w", warehouse);
            if (await q.ExecuteScalarAsync(ct) is null) return Fail(ConfigStatus.NotFound, "warehouse_not_found");
        }
        int resolved;
        await using (var q = new NpgsqlCommand($"SELECT {I.ItemType}, {I.IsLot}, {I.IsSerial}, {I.HasExpiry}, {CompanySchema.NegativeStockGuard.ResolvedMethodFunction}({I.Id}) FROM {I.Table} WHERE {I.Id}=@i", conn, tx))
        {
            q.Parameters.AddWithValue("i", item);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(ConfigStatus.NotFound, "item_not_found");
            if (r.GetInt16(0) != 1 || r.GetBoolean(1) || r.GetBoolean(2) || r.GetBoolean(3)) return Fail(ConfigStatus.Conflict, "impact_history_unsupported");
            if (r.IsDBNull(4)) return Fail(ConfigStatus.Conflict, "valuation_method_unresolved");
            resolved = Convert.ToInt32(r.GetValue(4));
            if (resolved is not (1 or 2)) return Fail(ConfigStatus.Conflict, "valuation_method_unresolved");
        }
        string currency;
        int decimals;
        await using (var q = new NpgsqlCommand(
            $"SELECT c.{CompanySchema.Currency.Code},c.{CompanySchema.Currency.MinorUnits} FROM {CompanySchema.CompanyCurrency.Table} cc JOIN {CompanySchema.Currency.Table} c ON c.{CompanySchema.Currency.Code}=cc.{CompanySchema.CompanyCurrency.Code} WHERE cc.{CompanySchema.CompanyCurrency.IsBase}", conn, tx))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(ConfigStatus.Conflict, "base_currency_not_set");
            currency = r.GetString(0); decimals = Convert.ToInt32(r.GetValue(1));
        }
        if (decimals is < 0 or > CostingMath.MaxMoneyDecimals) return Fail(ConfigStatus.Conflict, "unsupported_currency_precision");
        if (SnapshotEstablished is { } observed) await observed(conn, tx);

        // Includes settled and reversed obligations: their history cannot be inferred from normal ledger entries alone.
        await using (var q = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {NO.Table} WHERE {NO.ItemId}=@i AND {NO.WarehouseId}=@w)", conn, tx))
        {
            q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", warehouse);
            if ((bool)(await q.ExecuteScalarAsync(ct))!) return Fail(ConfigStatus.Conflict, "impact_history_unsupported");
        }
        Guid? position = null;
        var method = (CostMethod)resolved;
        long lastSeq = 0;
        decimal quantity = 0, value = 0;
        await using (var q = new NpgsqlCommand($"SELECT {CP.Id},{CP.Method},{CP.LastSeq},{CP.Quantity},{CP.Value} FROM {CP.Table} WHERE {CP.ItemId}=@i AND {CP.WarehouseId}=@w", conn, tx))
        {
            q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", warehouse);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                position = r.GetGuid(0); method = (CostMethod)r.GetInt16(1); lastSeq = r.GetInt64(2); quantity = r.GetDecimal(3); value = r.GetDecimal(4);
                if ((int)method != resolved) return Fail(ConfigStatus.Conflict, "valuation_method_changed");
            }
        }
        var entries = ImmutableArray.CreateBuilder<CostHistoryEntry>();
        if (position is { } p)
        {
            await using var q = new NpgsqlCommand(
                $"SELECT e.{CE.Id},e.{CE.OperationId},e.{CE.Seq},e.{CE.EffectiveDate},e.{CE.Kind},e.{CE.Direction},e.{CE.Quantity},e.{CE.Amount},e.{CE.QuantityAfter},e.{CE.ValueAfter},e.{CE.ReversesEntryId}," +
                $"o.{CO.Kind},o.{CO.ReversesOperationId},o.{CO.ReversedByOperationId},o.{CO.SourceType},o.{CO.SourceId},o.{CO.EffectiveDate}," +
                $"EXISTS(SELECT 1 FROM {CompanySchema.StockDocument.Table} d JOIN {CompanySchema.StockPosting.Table} sp ON sp.{CompanySchema.StockPosting.DocumentId}=d.{CompanySchema.StockDocument.Id} " +
                $"WHERE d.{CompanySchema.StockDocument.Id}=o.{CO.SourceId} AND d.{CompanySchema.StockDocument.DocType}=o.{CO.Kind} AND sp.{CompanySchema.StockPosting.CostOperationId}=o.{CO.Id} " +
                $"AND (d.{CompanySchema.StockDocument.SourceWarehouseId}=@w OR d.{CompanySchema.StockDocument.TargetWarehouseId}=@w)) " +
                $"FROM {CE.Table} e JOIN {CO.Table} o ON o.{CO.Id}=e.{CE.OperationId} WHERE e.{CE.PositionId}=@p AND e.{CE.ItemId}=@i AND e.{CE.WarehouseId}=@w ORDER BY e.{CE.Seq} LIMIT @limit", conn, tx);
            q.Parameters.AddWithValue("p", p); q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", warehouse); q.Parameters.AddWithValue("limit", maxEntries + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            DateOnly? lastDate = null;
            while (await r.ReadAsync(ct))
            {
                if (entries.Count == maxEntries) return Fail(ConfigStatus.Conflict, "impact_history_too_large");
                var kind = (CostOperationKind)r.GetInt16(11);
                var source = r.IsDBNull(14) ? null : r.GetString(14);
                Guid? sourceId = r.IsDBNull(15) ? null : r.GetGuid(15);
                if (kind is not (CostOperationKind.Receipt or CostOperationKind.Issue) || !r.IsDBNull(10) || !r.IsDBNull(12) || !r.IsDBNull(13) ||
                    (source is null ? sourceId is not null : source != "stock_document" || sourceId is null || !r.GetBoolean(17)))
                    return Fail(ConfigStatus.Conflict, "impact_history_unsupported");
                var id = r.GetInt64(0); var seq = r.GetInt64(2); var date = r.GetFieldValue<DateOnly>(3);
                var expectedEntryKind = kind == CostOperationKind.Receipt ? CostEntryKind.Receipt : CostEntryKind.Issue;
                if (id <= 0 || seq != entries.Count + 1L || r.GetInt16(4) != (short)expectedEntryKind || r.GetInt16(5) != (kind == CostOperationKind.Receipt ? 1 : -1) ||
                    date != r.GetFieldValue<DateOnly>(16) || (lastDate is { } d && date < d))
                    return Fail(ConfigStatus.Conflict, "impact_history_inconsistent");
                entries.Add(new CostHistoryEntry(id, r.GetGuid(1), seq, date, kind, r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), source, sourceId));
                lastDate = date;
            }
        }
        var history = entries.ToImmutable();
        if (lastSeq != history.Length) return Fail(ConfigStatus.Conflict, "impact_history_inconsistent");
        var replay = CostReplay.Calculate(method, decimals, history.Select(e => e.ToReplayEvent()).ToImmutableArray(), ct);
        for (var n = 0; n < history.Length; n++)
            if ((history[n].Amount, history[n].QuantityAfter, history[n].ValueAfter) != (replay.Steps[n].Amount, replay.Steps[n].QuantityAfter, replay.Steps[n].ValueAfter))
                return Fail(ConfigStatus.Conflict, "impact_history_inconsistent");
        if ((quantity, value) != (replay.QuantityAfter, replay.ValueAfter)) return Fail(ConfigStatus.Conflict, "impact_history_inconsistent");
        ct.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Company = company, Item = item, Warehouse = warehouse, Position = position,
            Method = method, Currency = currency, Decimals = decimals, LastSeq = lastSeq, Quantity = quantity, Value = value, Entries = history });
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // Disposal rolls back the read-only transaction, including every early-return path. There are no side effects to commit.
        return ConfigResult<CostHistorySnapshot>.Ok(new CostHistorySnapshot(company, item, warehouse, position, method, currency, decimals, lastSeq, quantity, value, history, hash));
    }
}
