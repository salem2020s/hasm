using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;
using OP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using LY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;

namespace Hasm.Infrastructure.Costing;

/// <summary>
/// The inventory costing engine (Phase 3.6): FIFO cost layers and moving weighted average per ITEM x WAREHOUSE (D-017, D-018), exact in the company base currency. <b>It is infrastructure for the posting slice, not a
/// stock feature:</b> nothing in the application calls it for a document, a screen or a job in this phase, it creates no quantity balance anywhere a user can see, and it never reads or changes a stock document.
/// <para><b>Transaction boundary.</b> Every operation runs INSIDE a unit of work owned by the caller (<see cref="CompanyAuditedWork"/>): the future posting transaction commits the document status, the cost and its accounting
/// state together, and an exception anywhere rolls back everything (cost layers, ledger entries, positions, the audit event). <see cref="RunAsync"/> is the single-operation wrapper (tests, staging).</para>
/// <para><b>Concurrency.</b> Every operation first locks the cost positions it touches (<c>SELECT ... FOR UPDATE</c>) in a deterministic order (warehouse, item), so concurrent operations on the same position are
/// serialised in lock-acquisition order, each seeing the committed result of the previous one, and two operations can never consume the same layer. Every ledger entry carries the next sequence number of its position.</para>
/// <para><b>Idempotency.</b> An operation has a caller-chosen key: the same key and content returns the stored result (<see cref="CostingResult.Replayed"/>) without changing anything; the same key with other content is
/// refused (<c>idempotency_key_reused</c>).</para>
/// <para><b>Policy.</b> The valuation method of a position is the one the approved policy (item group, then company default) resolves to at its FIRST entry and is then fixed: a later policy change is never applied
/// silently (<c>valuation_method_changed</c>). Services, tracked (lot, serial, expiry) items and items without a decided policy are refused. Stock never goes negative: an issue above the quantity on hand is refused
/// (<c>insufficient_stock</c>). Back-dated entries are refused (<c>backdated_not_supported</c>): back-dated movements need the impact preview and recalculation of a later slice.</para>
/// <para><b>Corrections.</b> Nothing is edited: a correction is a reversal (compensating entries that restore exactly what was consumed or remove exactly what was received) followed by a new operation.</para>
/// </summary>
public sealed class InventoryCostingService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, CompanyInventoryService inventory, TimeProvider clock)
{
    private sealed class Replay : Exception;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ================================================================================================================ single-operation wrapper

    /// <summary>Runs one costing operation in its own audited transaction (all or nothing). A replay of an already applied key returns its stored result without a transaction effect.</summary>
    public async Task<CostingResult> RunAsync(Guid companyId, Func<CompanyAuditedWork, Task<CostingResult>> body, CancellationToken ct = default)
    {
        CostingResult? replayed = null;
        try
        {
            return await transactions.ExecuteAsync(companyId, async work =>
            {
                var r = await body(work);
                if (r.Replayed) { replayed = r; throw new Replay(); }
                return r;
            }, ct);
        }
        catch (Replay) { return replayed!; }
    }

    public Task<CostingResult> ReceiveAsync(Guid companyId, Guid actor, CostingReceiptRequest request, CancellationToken ct = default) => RunAsync(companyId, w => ReceiveAsync(w, actor, request, ct), ct);
    public Task<CostingResult> IssueAsync(Guid companyId, Guid actor, CostingIssueRequest request, CancellationToken ct = default) => RunAsync(companyId, w => IssueAsync(w, actor, request, ct), ct);
    public Task<CostingResult> TransferAsync(Guid companyId, Guid actor, CostingTransferRequest request, CancellationToken ct = default) => RunAsync(companyId, w => TransferAsync(w, actor, request, ct), ct);
    public Task<CostingResult> ReverseAsync(Guid companyId, Guid actor, CostingReversalRequest request, CancellationToken ct = default) => RunAsync(companyId, w => ReverseAsync(w, actor, request, ct), ct);

    // ================================================================================================================ common

    private sealed record ItemFacts(Guid Id, int Type, bool Tracked, int BaseDecimals);
    private sealed record Position(Guid Id, Guid ItemId, Guid WarehouseId, CostMethod Method, BigInteger Quantity, BigInteger Value, long LastSeq, DateOnly? LastDate);

    private sealed class Context(CompanyAuditedWork work, int moneyDecimals)
    {
        public CompanyAuditedWork Work { get; } = work;
        public int MoneyDecimals { get; } = moneyDecimals;
        public Dictionary<Guid, ItemFacts> Items { get; } = [];
        public Dictionary<(Guid Item, Guid Warehouse), Position> Positions { get; } = [];
    }

    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 100 || key != key.Trim() || key.Any(char.IsControl)) throw new CostingException("invalid_key");
    }

    private static string Hash(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    private static string D(decimal v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
    private static string Src(CostingSource? s) => $"{s?.Type}|{s?.Id}";

    private async Task<Context> BeginAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        int decimals;
        await using (var q = new NpgsqlCommand(
            $"SELECT c.{CompanySchema.Currency.MinorUnits} FROM {CompanySchema.CompanyCurrency.Table} cc JOIN {CompanySchema.Currency.Table} c ON c.{CompanySchema.Currency.Code} = cc.{CompanySchema.CompanyCurrency.Code} WHERE cc.{CompanySchema.CompanyCurrency.IsBase}", work.Connection, work.Transaction))
        {
            var v = await q.ExecuteScalarAsync(ct);
            if (v is null) throw new CostingException("base_currency_not_set");
            decimals = v is DBNull ? 2 : Convert.ToInt32(v);
        }
        if (decimals is < 0 or > CostingMath.MaxMoneyDecimals) throw new CostingException("unsupported_currency_precision");
        return new Context(work, decimals);
    }

    private async Task<CostingResult?> FindReplayAsync(Context cx, string key, CostOperationKind kind, string hash, CancellationToken ct)
    {
        // two requests with the same key serialise here, so the second one finds the committed operation and replays it (never a unique-key failure, never a double application)
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, hashtext(@k))", cx.Work.Connection, cx.Work.Transaction))
        {
            lk.Parameters.AddWithValue("a", 0x48435354);
            lk.Parameters.AddWithValue("k", key);
            await lk.ExecuteNonQueryAsync(ct);
        }
        Guid id; short k; string h; DateOnly date;
        await using (var q = new NpgsqlCommand($"SELECT {OP.Id}, {OP.Kind}, {OP.RequestHash}, {OP.EffectiveDate} FROM {OP.Table} WHERE {OP.Key} = @k", cx.Work.Connection, cx.Work.Transaction))
        {
            q.Parameters.AddWithValue("k", key);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0); k = r.GetInt16(1); h = r.GetString(2).Trim(); date = r.GetFieldValue<DateOnly>(3);
        }
        if (k != (short)kind || !string.Equals(h, hash, StringComparison.Ordinal)) throw new CostingException("idempotency_key_reused");
        return new CostingResult(id, key, kind, true, date, await LoadLinesAsync(cx.Work, id, ct));
    }

    private static async Task<List<CostLineResult>> LoadLinesAsync(CompanyAuditedWork work, Guid operationId, CancellationToken ct)
    {
        var lines = new List<(long Id, Guid Item, Guid Wh, short Kind, short Dir, decimal Qty, decimal Amount, decimal QAfter, decimal VAfter)>();
        await using (var q = new NpgsqlCommand($"SELECT {EN.Id}, {EN.ItemId}, {EN.WarehouseId}, {EN.Kind}, {EN.Direction}, {EN.Quantity}, {EN.Amount}, {EN.QuantityAfter}, {EN.ValueAfter} FROM {EN.Table} WHERE {EN.OperationId} = @o ORDER BY {EN.Id}", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("o", operationId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines.Add((r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetInt16(3), r.GetInt16(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)));
        }
        var result = new List<CostLineResult>();
        foreach (var l in lines)
        {
            var cons = new List<CostConsumptionView>();
            await using (var q = new NpgsqlCommand($"SELECT {CS.LayerId}, {CS.Quantity}, {CS.Amount}, {CS.Restores} FROM {CS.Table} WHERE {CS.EntryId} = @e ORDER BY {CS.Id}", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("e", l.Id);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) cons.Add(new CostConsumptionView(r.GetGuid(0), r.GetDecimal(1), r.GetDecimal(2), r.GetBoolean(3)));
            }
            result.Add(new CostLineResult(l.Id, l.Item, l.Wh, (CostEntryKind)l.Kind, l.Dir, l.Qty, l.Amount, CostingMath.UnitCost(l.Amount, l.Qty), l.QAfter, l.VAfter, cons));
        }
        return result;
    }

    private async Task<Guid> InsertOperationAsync(Context cx, string key, CostOperationKind kind, string hash, CostingSource? source, DateOnly date, Guid actor, Guid? reverses, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var ins = new NpgsqlCommand(
            $"INSERT INTO {OP.Table} ({OP.Id}, {OP.Key}, {OP.Kind}, {OP.RequestHash}, {OP.SourceType}, {OP.SourceId}, {OP.EffectiveDate}, {OP.CreatedBy}, {OP.CreatedAtUtc}, {OP.ReversesOperationId}) VALUES (@id, @k, @kd, @h, @st, @si, @d, @a, @now, @rv)", cx.Work.Connection, cx.Work.Transaction);
        ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("k", key); ins.Parameters.AddWithValue("kd", (short)kind); ins.Parameters.AddWithValue("h", hash);
        ins.Parameters.AddWithValue("st", (object?)source?.Type ?? DBNull.Value); ins.Parameters.AddWithValue("si", (object?)source?.Id ?? DBNull.Value); ins.Parameters.AddWithValue("d", date);
        ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", Now); ins.Parameters.AddWithValue("rv", (object?)reverses ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync(ct);
        return id;
    }

    private async Task<ItemFacts> ItemAsync(Context cx, Guid itemId, bool requireDecidedPolicy, CancellationToken ct)
    {
        if (cx.Items.TryGetValue(itemId, out var cached)) return cached;
        ItemFacts f;
        await using (var q = new NpgsqlCommand($"SELECT i.{I.ItemType}, (i.{I.IsLot} OR i.{I.IsSerial} OR i.{I.HasExpiry}), u.{U.Decimals} FROM {I.Table} i JOIN {U.Table} u ON u.{U.Id} = i.{I.BaseUnitId} WHERE i.{I.Id} = @i", cx.Work.Connection, cx.Work.Transaction))
        {
            q.Parameters.AddWithValue("i", itemId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new CostingException("item_not_found", itemId.ToString());
            f = new ItemFacts(itemId, r.GetInt16(0), r.GetBoolean(1), r.GetInt16(2));
        }
        if (f.Type != (int)ItemType.Goods) throw new CostingException("item_not_stockable", itemId.ToString());
        if (f.Tracked) throw new CostingException("tracked_item_not_supported_yet", itemId.ToString());
        _ = requireDecidedPolicy;
        cx.Items[itemId] = f;
        return f;
    }

    private static BigInteger Quantity(decimal q, ItemFacts item)
    {
        if (q <= 0m || q >= 1_000_000_000_000_000_000m) throw new CostingException("invalid_quantity");
        if (!CostingMath.FitsDecimals(q, item.BaseDecimals)) throw new CostingException("quantity_precision_exceeded", $"{q}");
        return CostingMath.ToUnits(q, CostingMath.QuantityScale);
    }

    private static BigInteger Amount(decimal a, int moneyDecimals)
    {
        if (a < 0m || a >= 1_000_000_000_000_000_000m) throw new CostingException("invalid_amount");
        if (!CostingMath.FitsDecimals(a, moneyDecimals)) throw new CostingException("amount_precision_exceeded", $"{a}");
        return CostingMath.ToUnits(a, CostingMath.AmountScale);
    }

    private static decimal Q(BigInteger u) => CostingMath.FromUnits(u, CostingMath.QuantityScale);
    private static decimal M(BigInteger u) => CostingMath.FromUnits(u, CostingMath.AmountScale);

    /// <summary>Creates the positions that do not exist yet and locks all of them, in the deterministic order (warehouse, item); validates the policy once per position.</summary>
    private async Task LockPositionsAsync(Context cx, IEnumerable<(Guid Item, Guid Warehouse)> wanted, CancellationToken ct)
    {
        foreach (var (item, wh) in wanted.Distinct().OrderBy(x => x.Warehouse).ThenBy(x => x.Item))
        {
            await using (var ex = new NpgsqlCommand($"SELECT 1 FROM {W.Table} WHERE {W.Id} = @w", cx.Work.Connection, cx.Work.Transaction))
            {
                ex.Parameters.AddWithValue("w", wh);
                if (await ex.ExecuteScalarAsync(ct) is null) throw new CostingException("warehouse_not_found", wh.ToString());
            }
            var facts = await ItemAsync(cx, item, true, ct);
            _ = facts;
            var resolved = await inventory.ResolveValuationInWorkAsync(cx.Work, item, ct) ?? throw new CostingException("valuation_method_unresolved", item.ToString());
            await using (var ins = new NpgsqlCommand($"INSERT INTO {PO.Table} ({PO.Id}, {PO.ItemId}, {PO.WarehouseId}, {PO.Method}, {PO.Quantity}, {PO.Value}, {PO.LastSeq}, {PO.CreatedAtUtc}, {PO.UpdatedAtUtc}) VALUES (gen_random_uuid(), @i, @w, @m, 0, 0, 0, @now, @now) ON CONFLICT ({PO.ItemId}, {PO.WarehouseId}) DO NOTHING", cx.Work.Connection, cx.Work.Transaction))
            {
                ins.Parameters.AddWithValue("i", item); ins.Parameters.AddWithValue("w", wh); ins.Parameters.AddWithValue("m", (short)(resolved == ValuationMethod.Fifo ? CostMethod.Fifo : CostMethod.MovingAverage)); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            cx.Positions[(item, wh)] = await LoadPositionAsync(cx, item, wh, resolved, ct);
        }
    }

    private async Task<Position> LoadPositionAsync(Context cx, Guid item, Guid wh, ValuationMethod resolved, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {PO.Id}, {PO.Method}, {PO.Quantity}, {PO.Value}, {PO.LastSeq}, {PO.LastEffectiveDate} FROM {PO.Table} WHERE {PO.ItemId} = @i AND {PO.WarehouseId} = @w FOR UPDATE", cx.Work.Connection, cx.Work.Transaction);
        q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", wh);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var method = (CostMethod)r.GetInt16(1);
        var wanted = resolved == ValuationMethod.Fifo ? CostMethod.Fifo : CostMethod.MovingAverage;
        if (method != wanted) throw new CostingException("valuation_method_changed", $"{item}/{wh}");              // the policy changed after the position was costed: never applied silently
        return new Position(r.GetGuid(0), item, wh, method, CostingMath.ToUnits(r.GetDecimal(2), CostingMath.QuantityScale), CostingMath.ToUnits(r.GetDecimal(3), CostingMath.AmountScale), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5));
    }

    /// <summary>Appends one entry to a position (next sequence number), moves the position and returns the entry id. Enforces the no-back-dating rule.</summary>
    private async Task<long> AppendEntryAsync(Context cx, Guid operationId, Position pos, CostEntryKind kind, int direction, DateOnly date, BigInteger quantity, BigInteger amount, long? reverses, CancellationToken ct)
    {
        if (pos.LastDate is { } last && date < last) throw new CostingException("backdated_not_supported", $"{date} < {last}");
        var qtyAfter = pos.Quantity + direction * quantity;
        var valueAfter = pos.Value + direction * amount;
        if (qtyAfter.Sign < 0 || valueAfter.Sign < 0) throw new CostingException("insufficient_stock");
        var seq = pos.LastSeq + 1;
        long id;
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {EN.Table} ({EN.OperationId}, {EN.PositionId}, {EN.ItemId}, {EN.WarehouseId}, {EN.Kind}, {EN.Direction}, {EN.EffectiveDate}, {EN.Quantity}, {EN.Amount}, {EN.QuantityAfter}, {EN.ValueAfter}, {EN.Seq}, {EN.ReversesEntryId}, {EN.CreatedAtUtc}) " +
            "VALUES (@o, @p, @i, @w, @k, @d, @date, @q, @a, @qa, @va, @s, @rv, @now) RETURNING " + EN.Id, cx.Work.Connection, cx.Work.Transaction))
        {
            ins.Parameters.AddWithValue("o", operationId); ins.Parameters.AddWithValue("p", pos.Id); ins.Parameters.AddWithValue("i", pos.ItemId); ins.Parameters.AddWithValue("w", pos.WarehouseId);
            ins.Parameters.AddWithValue("k", (short)kind); ins.Parameters.AddWithValue("d", (short)direction); ins.Parameters.AddWithValue("date", date);
            ins.Parameters.AddWithValue("q", Q(quantity)); ins.Parameters.AddWithValue("a", M(amount)); ins.Parameters.AddWithValue("qa", Q(qtyAfter)); ins.Parameters.AddWithValue("va", M(valueAfter));
            ins.Parameters.AddWithValue("s", seq); ins.Parameters.AddWithValue("rv", (object?)reverses ?? DBNull.Value); ins.Parameters.AddWithValue("now", Now);
            id = (long)(await ins.ExecuteScalarAsync(ct))!;
        }
        await using (var upd = new NpgsqlCommand($"UPDATE {PO.Table} SET {PO.Quantity} = @q, {PO.Value} = @v, {PO.LastSeq} = @s, {PO.LastEffectiveDate} = @d, {PO.UpdatedAtUtc} = @now WHERE {PO.Id} = @id", cx.Work.Connection, cx.Work.Transaction))
        {
            upd.Parameters.AddWithValue("id", pos.Id); upd.Parameters.AddWithValue("q", Q(qtyAfter)); upd.Parameters.AddWithValue("v", M(valueAfter)); upd.Parameters.AddWithValue("s", seq); upd.Parameters.AddWithValue("d", date); upd.Parameters.AddWithValue("now", Now);
            await upd.ExecuteNonQueryAsync(ct);
        }
        cx.Positions[(pos.ItemId, pos.WarehouseId)] = pos with { Quantity = qtyAfter, Value = valueAfter, LastSeq = seq, LastDate = date };
        return id;
    }

    private async Task<Guid> InsertLayerAsync(Context cx, Position pos, long entryId, BigInteger quantity, BigInteger amount, Guid? source, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        long seq;
        await using (var next = new NpgsqlCommand($"SELECT coalesce(max({LY.Seq}), 0) + 1 FROM {LY.Table} WHERE {LY.PositionId} = @p", cx.Work.Connection, cx.Work.Transaction))
        {
            next.Parameters.AddWithValue("p", pos.Id);
            seq = (long)(await next.ExecuteScalarAsync(ct))!;                                              // the position row is locked: no concurrent writer can take the same number
        }
        await using var ins = new NpgsqlCommand(
            $"INSERT INTO {LY.Table} ({LY.Id}, {LY.PositionId}, {LY.ItemId}, {LY.WarehouseId}, {LY.EntryId}, {LY.Seq}, {LY.OriginalQuantity}, {LY.OriginalAmount}, {LY.RemainingQuantity}, {LY.RemainingAmount}, {LY.SourceLayerId}, {LY.CreatedAtUtc}) VALUES (@id, @p, @i, @w, @e, @s, @q, @a, @q, @a, @src, @now)", cx.Work.Connection, cx.Work.Transaction);
        ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("p", pos.Id); ins.Parameters.AddWithValue("i", pos.ItemId); ins.Parameters.AddWithValue("w", pos.WarehouseId); ins.Parameters.AddWithValue("e", entryId);
        ins.Parameters.AddWithValue("s", seq); ins.Parameters.AddWithValue("q", Q(quantity)); ins.Parameters.AddWithValue("a", M(amount)); ins.Parameters.AddWithValue("src", (object?)source ?? DBNull.Value); ins.Parameters.AddWithValue("now", Now);
        await ins.ExecuteNonQueryAsync(ct);
        return id;
    }

    private async Task AddConsumptionAsync(Context cx, long entryId, Guid layerId, BigInteger quantity, BigInteger amount, bool restores, CancellationToken ct)
    {
        await using var ins = new NpgsqlCommand($"INSERT INTO {CS.Table} ({CS.EntryId}, {CS.LayerId}, {CS.Quantity}, {CS.Amount}, {CS.Restores}) VALUES (@e, @l, @q, @a, @r)", cx.Work.Connection, cx.Work.Transaction);
        ins.Parameters.AddWithValue("e", entryId); ins.Parameters.AddWithValue("l", layerId); ins.Parameters.AddWithValue("q", Q(quantity)); ins.Parameters.AddWithValue("a", M(amount)); ins.Parameters.AddWithValue("r", restores);
        await ins.ExecuteNonQueryAsync(ct);
    }

    private async Task AdjustLayerAsync(Context cx, Guid layerId, BigInteger quantityDelta, BigInteger amountDelta, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand($"UPDATE {LY.Table} SET {LY.RemainingQuantity} = {LY.RemainingQuantity} + @q, {LY.RemainingAmount} = {LY.RemainingAmount} + @a WHERE {LY.Id} = @id", cx.Work.Connection, cx.Work.Transaction);
        upd.Parameters.AddWithValue("id", layerId); upd.Parameters.AddWithValue("q", Q(quantityDelta)); upd.Parameters.AddWithValue("a", M(amountDelta));
        if (await upd.ExecuteNonQueryAsync(ct) != 1) throw new CostingException("layer_not_found");
    }

    private async Task<List<CostingMath.LayerState>> OpenLayersAsync(Context cx, Guid positionId, CancellationToken ct)
    {
        var list = new List<CostingMath.LayerState>();
        await using var q = new NpgsqlCommand($"SELECT {LY.Id}, {LY.Seq}, {LY.RemainingQuantity}, {LY.RemainingAmount} FROM {LY.Table} WHERE {LY.PositionId} = @p AND {LY.RemainingQuantity} > 0 ORDER BY {LY.Seq} FOR UPDATE", cx.Work.Connection, cx.Work.Transaction);
        q.Parameters.AddWithValue("p", positionId);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new CostingMath.LayerState(r.GetGuid(0), r.GetInt64(1), CostingMath.ToUnits(r.GetDecimal(2), CostingMath.QuantityScale), CostingMath.ToUnits(r.GetDecimal(3), CostingMath.AmountScale)));
        return list;
    }

    private Task AuditAsync(Context cx, short type, Guid actor, Guid operationId, string detail, CancellationToken ct) =>
        cx.Work.AppendAuditAsync(new AuditEventData(type, ActorUserId: actor, SubjectId: operationId, Detail: detail), ct);

    private CostLineResult Line(long entryId, Position pos, CostEntryKind kind, int direction, BigInteger quantity, BigInteger amount, IEnumerable<CostConsumptionView>? consumptions = null)
    {
        return new CostLineResult(entryId, pos.ItemId, pos.WarehouseId, kind, direction, Q(quantity), M(amount), CostingMath.UnitCost(M(amount), Q(quantity)), Q(pos.Quantity), M(pos.Value), [.. consumptions ?? []]);
    }

    // ================================================================================================================ receipt

    public async Task<CostingResult> ReceiveAsync(CompanyAuditedWork work, Guid actor, CostingReceiptRequest request, CancellationToken ct = default)
    {
        ValidateKey(request.Key);
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500) throw new CostingException("invalid_lines");
        var cx = await BeginAsync(work, ct);
        var hash = Hash($"receipt|{request.EffectiveDate:yyyy-MM-dd}|{Src(request.Source)}|" + string.Join(";", request.Lines.Select(l => $"{l.ItemId}:{l.WarehouseId}:{D(l.Quantity)}:{D(l.Amount)}")));
        if (await FindReplayAsync(cx, request.Key, CostOperationKind.Receipt, hash, ct) is { } replay) return replay;
        await LockPositionsAsync(cx, request.Lines.Select(l => (l.ItemId, l.WarehouseId)), ct);
        var opId = await InsertOperationAsync(cx, request.Key, CostOperationKind.Receipt, hash, request.Source, request.EffectiveDate, actor, null, ct);
        var results = new List<CostLineResult>();
        foreach (var l in request.Lines)
        {
            var item = await ItemAsync(cx, l.ItemId, true, ct);
            var qty = Quantity(l.Quantity, item);
            var amount = Amount(l.Amount, cx.MoneyDecimals);
            var pos = cx.Positions[(l.ItemId, l.WarehouseId)];
            var entry = await AppendEntryAsync(cx, opId, pos, CostEntryKind.Receipt, 1, request.EffectiveDate, qty, amount, null, ct);
            var after = cx.Positions[(l.ItemId, l.WarehouseId)];
            if (pos.Method == CostMethod.Fifo) await InsertLayerAsync(cx, after, entry, qty, amount, null, ct);
            results.Add(Line(entry, after, CostEntryKind.Receipt, 1, qty, amount));
        }
        await AuditAsync(cx, CostingAuditEvents.Received, actor, opId, $"costing receipt {request.Key} lines={results.Count} value={D(results.Sum(r => r.Amount))}", ct);
        return new CostingResult(opId, request.Key, CostOperationKind.Receipt, false, request.EffectiveDate, results);
    }

    // ================================================================================================================ issue

    private sealed record Outflow(long EntryId, BigInteger Amount, IReadOnlyList<CostingMath.Take> Takes);

    /// <summary>Costs and records an outflow (issue or transfer out): FIFO consumes the oldest layers, average takes the proportional value. Refuses more than the quantity on hand.</summary>
    private async Task<Outflow> OutflowAsync(Context cx, Guid operationId, Position pos, CostEntryKind kind, DateOnly date, BigInteger quantity, CancellationToken ct)
    {
        if (quantity > pos.Quantity) throw new CostingException("insufficient_stock", $"{pos.ItemId}/{pos.WarehouseId}: {Q(quantity)} > {Q(pos.Quantity)}");
        if (pos.Method == CostMethod.Fifo)
        {
            var plan = CostingMath.PlanFifo(await OpenLayersAsync(cx, pos.Id, ct), quantity, cx.MoneyDecimals);
            var total = plan.Aggregate(BigInteger.Zero, (s, t) => s + t.Amount);
            var entry = await AppendEntryAsync(cx, operationId, pos, kind, -1, date, quantity, total, null, ct);
            foreach (var t in plan)
            {
                await AdjustLayerAsync(cx, t.LayerId, -t.Quantity, -t.Amount, ct);
                await AddConsumptionAsync(cx, entry, t.LayerId, t.Quantity, t.Amount, false, ct);
            }
            return new Outflow(entry, total, plan);
        }
        var cost = CostingMath.PlanAverage(pos.Quantity, pos.Value, quantity, cx.MoneyDecimals);
        return new Outflow(await AppendEntryAsync(cx, operationId, pos, kind, -1, date, quantity, cost, null, ct), cost, []);
    }

    public async Task<CostingResult> IssueAsync(CompanyAuditedWork work, Guid actor, CostingIssueRequest request, CancellationToken ct = default)
    {
        ValidateKey(request.Key);
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500) throw new CostingException("invalid_lines");
        var cx = await BeginAsync(work, ct);
        var hash = Hash($"issue|{request.EffectiveDate:yyyy-MM-dd}|{Src(request.Source)}|" + string.Join(";", request.Lines.Select(l => $"{l.ItemId}:{l.WarehouseId}:{D(l.Quantity)}")));
        if (await FindReplayAsync(cx, request.Key, CostOperationKind.Issue, hash, ct) is { } replay) return replay;
        await LockPositionsAsync(cx, request.Lines.Select(l => (l.ItemId, l.WarehouseId)), ct);
        var opId = await InsertOperationAsync(cx, request.Key, CostOperationKind.Issue, hash, request.Source, request.EffectiveDate, actor, null, ct);
        var results = new List<CostLineResult>();
        foreach (var l in request.Lines)
        {
            var item = await ItemAsync(cx, l.ItemId, true, ct);
            var qty = Quantity(l.Quantity, item);
            var pos = cx.Positions[(l.ItemId, l.WarehouseId)];
            var o = await OutflowAsync(cx, opId, pos, CostEntryKind.Issue, request.EffectiveDate, qty, ct);
            results.Add(Line(o.EntryId, cx.Positions[(l.ItemId, l.WarehouseId)], CostEntryKind.Issue, -1, qty, o.Amount, o.Takes.Select(t => new CostConsumptionView(t.LayerId, Q(t.Quantity), M(t.Amount), false))));
        }
        await AuditAsync(cx, CostingAuditEvents.Issued, actor, opId, $"costing issue {request.Key} lines={results.Count} value={D(results.Sum(r => r.Amount))}", ct);
        return new CostingResult(opId, request.Key, CostOperationKind.Issue, false, request.EffectiveDate, results);
    }

    // ================================================================================================================ transfer

    /// <summary>
    /// Moves stock between two warehouses of the company at its ACTUAL cost: the source is costed by its own method, and exactly that value is carried to the destination (FIFO: one mirrored layer per consumed layer,
    /// in the same order; average: the same total added to the destination), so the operation never creates profit or loss and the company total value does not change.
    /// </summary>
    public async Task<CostingResult> TransferAsync(CompanyAuditedWork work, Guid actor, CostingTransferRequest request, CancellationToken ct = default)
    {
        ValidateKey(request.Key);
        if (request.FromWarehouseId == request.ToWarehouseId) throw new CostingException("same_warehouse");
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500) throw new CostingException("invalid_lines");
        if (request.Lines.Select(l => l.ItemId).Distinct().Count() != request.Lines.Count) throw new CostingException("duplicate_item_line");
        var cx = await BeginAsync(work, ct);
        var hash = Hash($"transfer|{request.EffectiveDate:yyyy-MM-dd}|{Src(request.Source)}|{request.FromWarehouseId}>{request.ToWarehouseId}|" + string.Join(";", request.Lines.Select(l => $"{l.ItemId}:{D(l.Quantity)}")));
        if (await FindReplayAsync(cx, request.Key, CostOperationKind.Transfer, hash, ct) is { } replay) return replay;
        await LockPositionsAsync(cx, request.Lines.SelectMany(l => new[] { (l.ItemId, request.FromWarehouseId), (l.ItemId, request.ToWarehouseId) }), ct);
        var opId = await InsertOperationAsync(cx, request.Key, CostOperationKind.Transfer, hash, request.Source, request.EffectiveDate, actor, null, ct);
        var results = new List<CostLineResult>();
        foreach (var l in request.Lines)
        {
            var item = await ItemAsync(cx, l.ItemId, true, ct);
            var qty = Quantity(l.Quantity, item);
            var from = cx.Positions[(l.ItemId, request.FromWarehouseId)];
            var to0 = cx.Positions[(l.ItemId, request.ToWarehouseId)];
            if (from.Method != to0.Method) throw new CostingException("valuation_method_mismatch", l.ItemId.ToString());
            var o = await OutflowAsync(cx, opId, from, CostEntryKind.TransferOut, request.EffectiveDate, qty, ct);
            results.Add(Line(o.EntryId, cx.Positions[(l.ItemId, request.FromWarehouseId)], CostEntryKind.TransferOut, -1, qty, o.Amount, o.Takes.Select(t => new CostConsumptionView(t.LayerId, Q(t.Quantity), M(t.Amount), false))));
            var to = cx.Positions[(l.ItemId, request.ToWarehouseId)];
            var inEntry = await AppendEntryAsync(cx, opId, to, CostEntryKind.TransferIn, 1, request.EffectiveDate, qty, o.Amount, null, ct);
            var toAfter = cx.Positions[(l.ItemId, request.ToWarehouseId)];
            if (to.Method == CostMethod.Fifo)
            {
                // the destination receives the SAME layers (same quantity and exact amount each) in the same order: the actual cost is carried, nothing is re-priced
                foreach (var t in o.Takes) await InsertLayerAsync(cx, toAfter, inEntry, t.Quantity, t.Amount, t.LayerId, ct);
            }
            results.Add(Line(inEntry, toAfter, CostEntryKind.TransferIn, 1, qty, o.Amount));
        }
        await AuditAsync(cx, CostingAuditEvents.Transferred, actor, opId, $"costing transfer {request.Key} lines={request.Lines.Count} value={D(results.Where(r => r.Direction < 0).Sum(r => r.Amount))}", ct);
        return new CostingResult(opId, request.Key, CostOperationKind.Transfer, false, request.EffectiveDate, results);
    }

    // ================================================================================================================ reversal

    private sealed record StoredEntry(long Id, Guid PositionId, Guid ItemId, Guid WarehouseId, CostEntryKind Kind, int Direction, BigInteger Quantity, BigInteger Amount, long Seq);

    /// <summary>
    /// Reverses a whole operation by compensating entries (never an edit): an outflow gets back exactly what it consumed (the same layers, the same amounts; average: the same quantity and value); an inflow is taken out
    /// again, which is only possible while that stock is still there untouched (FIFO: its layer fully remaining; average: it is the last entry of the position), otherwise it is refused
    /// (<c>reversal_not_possible_goods_consumed</c> / <c>reversal_not_possible_later_movements</c>): correcting history behind later movements needs the recalculation of a later slice. An operation is reversed at most once.
    /// </summary>
    public async Task<CostingResult> ReverseAsync(CompanyAuditedWork work, Guid actor, CostingReversalRequest request, CancellationToken ct = default)
    {
        ValidateKey(request.Key);
        ValidateKey(request.ReversesKey);
        var cx = await BeginAsync(work, ct);
        var hash = Hash($"reversal|{request.ReversesKey}|{request.EffectiveDate:yyyy-MM-dd}|{Src(request.Source)}");
        if (await FindReplayAsync(cx, request.Key, CostOperationKind.Reversal, hash, ct) is { } replay) return replay;
        Guid originalId; short originalKind; Guid? reversedBy;
        await using (var q = new NpgsqlCommand($"SELECT {OP.Id}, {OP.Kind}, {OP.ReversedByOperationId} FROM {OP.Table} WHERE {OP.Key} = @k FOR UPDATE", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("k", request.ReversesKey);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new CostingException("operation_not_found", request.ReversesKey);
            originalId = r.GetGuid(0); originalKind = r.GetInt16(1); reversedBy = r.IsDBNull(2) ? null : r.GetGuid(2);
        }
        if (originalKind == (short)CostOperationKind.Reversal) throw new CostingException("reversal_cannot_be_reversed");
        if (reversedBy is not null) throw new CostingException("already_reversed");
        var originals = new List<StoredEntry>();
        await using (var q = new NpgsqlCommand($"SELECT {EN.Id}, {EN.PositionId}, {EN.ItemId}, {EN.WarehouseId}, {EN.Kind}, {EN.Direction}, {EN.Quantity}, {EN.Amount}, {EN.Seq} FROM {EN.Table} WHERE {EN.OperationId} = @o ORDER BY {EN.Id} DESC", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("o", originalId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) originals.Add(new StoredEntry(r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), (CostEntryKind)r.GetInt16(4), r.GetInt16(5), CostingMath.ToUnits(r.GetDecimal(6), CostingMath.QuantityScale), CostingMath.ToUnits(r.GetDecimal(7), CostingMath.AmountScale), r.GetInt64(8)));
        }
        await LockPositionsAsync(cx, originals.Select(e => (e.ItemId, e.WarehouseId)), ct);
        var opId = await InsertOperationAsync(cx, request.Key, CostOperationKind.Reversal, hash, request.Source, request.EffectiveDate, actor, originalId, ct);
        var results = new List<CostLineResult>();
        foreach (var e in originals)                                                                        // newest first: a transfer's destination is undone before its source is restored
        {
            var pos = cx.Positions[(e.ItemId, e.WarehouseId)];
            if (e.Direction < 0)
            {
                var consumptions = new List<(Guid Layer, BigInteger Qty, BigInteger Amount)>();
                await using (var q = new NpgsqlCommand($"SELECT {CS.LayerId}, {CS.Quantity}, {CS.Amount} FROM {CS.Table} WHERE {CS.EntryId} = @e AND NOT {CS.Restores} ORDER BY {CS.Id}", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("e", e.Id);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) consumptions.Add((r.GetGuid(0), CostingMath.ToUnits(r.GetDecimal(1), CostingMath.QuantityScale), CostingMath.ToUnits(r.GetDecimal(2), CostingMath.AmountScale)));
                }
                var entry = await AppendEntryAsync(cx, opId, pos, e.Kind, 1, request.EffectiveDate, e.Quantity, e.Amount, e.Id, ct);
                foreach (var c in consumptions)
                {
                    await AdjustLayerAsync(cx, c.Layer, c.Qty, c.Amount, ct);
                    await AddConsumptionAsync(cx, entry, c.Layer, c.Qty, c.Amount, true, ct);
                }
                results.Add(Line(entry, cx.Positions[(e.ItemId, e.WarehouseId)], e.Kind, 1, e.Quantity, e.Amount, consumptions.Select(c => new CostConsumptionView(c.Layer, Q(c.Qty), M(c.Amount), true))));
            }
            else
            {
                var layers = new List<(Guid Id, BigInteger OrigQty, BigInteger OrigAmount, BigInteger RemQty, BigInteger RemAmount)>();
                if (pos.Method == CostMethod.Fifo)
                {
                    await using var q = new NpgsqlCommand($"SELECT {LY.Id}, {LY.OriginalQuantity}, {LY.OriginalAmount}, {LY.RemainingQuantity}, {LY.RemainingAmount} FROM {LY.Table} WHERE {LY.EntryId} = @e ORDER BY {LY.Seq} FOR UPDATE", work.Connection, work.Transaction);
                    q.Parameters.AddWithValue("e", e.Id);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) layers.Add((r.GetGuid(0), CostingMath.ToUnits(r.GetDecimal(1), 10), CostingMath.ToUnits(r.GetDecimal(2), 6), CostingMath.ToUnits(r.GetDecimal(3), 10), CostingMath.ToUnits(r.GetDecimal(4), 6)));
                    if (layers.Any(l => l.RemQty != l.OrigQty || l.RemAmount != l.OrigAmount)) throw new CostingException("reversal_not_possible_goods_consumed", e.Id.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    // the entry is still intact in the average only if every later entry of the position has been taken back again (a movement and its reversal cancel out exactly)
                    await using var later = new NpgsqlCommand($"SELECT count(*) FROM {EN.Table} x WHERE x.{EN.PositionId} = @p AND x.{EN.Seq} > @s AND x.{EN.ReversesEntryId} IS NULL AND NOT EXISTS (SELECT 1 FROM {EN.Table} y WHERE y.{EN.ReversesEntryId} = x.{EN.Id})", work.Connection, work.Transaction);
                    later.Parameters.AddWithValue("p", pos.Id); later.Parameters.AddWithValue("s", e.Seq);
                    if ((long)(await later.ExecuteScalarAsync(ct))! > 0) throw new CostingException("reversal_not_possible_later_movements", e.Id.ToString(CultureInfo.InvariantCulture));
                }
                var entry = await AppendEntryAsync(cx, opId, pos, e.Kind, -1, request.EffectiveDate, e.Quantity, e.Amount, e.Id, ct);
                var cons = new List<CostConsumptionView>();
                foreach (var l in layers)
                {
                    await AdjustLayerAsync(cx, l.Id, -l.RemQty, -l.RemAmount, ct);                         // the whole untouched layer leaves again
                    await AddConsumptionAsync(cx, entry, l.Id, l.RemQty, l.RemAmount, false, ct);
                    cons.Add(new CostConsumptionView(l.Id, Q(l.RemQty), M(l.RemAmount), false));
                }
                results.Add(Line(entry, cx.Positions[(e.ItemId, e.WarehouseId)], e.Kind, -1, e.Quantity, e.Amount, cons));
            }
        }
        await using (var mark = new NpgsqlCommand($"UPDATE {OP.Table} SET {OP.ReversedByOperationId} = @r WHERE {OP.Id} = @o", work.Connection, work.Transaction))
        {
            mark.Parameters.AddWithValue("r", opId); mark.Parameters.AddWithValue("o", originalId);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(cx, CostingAuditEvents.Reversed, actor, opId, $"costing reversal {request.Key} of {request.ReversesKey} entries={results.Count}", ct);
        return new CostingResult(opId, request.Key, CostOperationKind.Reversal, false, request.EffectiveDate, results);
    }

    // ================================================================================================================ reads and reconciliation

    public async Task<CostPositionView?> GetPositionAsync(Guid companyId, Guid itemId, Guid warehouseId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        Guid id; CostMethod method; decimal qty, value; long seq;
        await using (var q = new NpgsqlCommand($"SELECT {PO.Id}, {PO.Method}, {PO.Quantity}, {PO.Value}, {PO.LastSeq} FROM {PO.Table} WHERE {PO.ItemId} = @i AND {PO.WarehouseId} = @w", conn))
        {
            q.Parameters.AddWithValue("i", itemId); q.Parameters.AddWithValue("w", warehouseId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0); method = (CostMethod)r.GetInt16(1); qty = r.GetDecimal(2); value = r.GetDecimal(3); seq = r.GetInt64(4);
        }
        var layers = new List<CostLayerView>();
        await using (var q = new NpgsqlCommand($"SELECT {LY.Id}, {LY.Seq}, {LY.OriginalQuantity}, {LY.OriginalAmount}, {LY.RemainingQuantity}, {LY.RemainingAmount}, {LY.SourceLayerId} FROM {LY.Table} WHERE {LY.PositionId} = @p AND {LY.RemainingQuantity} > 0 ORDER BY {LY.Seq}", conn))
        {
            q.Parameters.AddWithValue("p", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) layers.Add(new CostLayerView(r.GetGuid(0), r.GetInt64(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetGuid(6)));
        }
        return new CostPositionView(id, itemId, warehouseId, method, qty, value, CostingMath.UnitCost(value, qty), seq, layers);
    }

    /// <summary>
    /// Verifies the invariants of the whole costing ledger of the company and returns every violation (empty = consistent): a position equals the signed sum of its entries and the figures after its last entry; FIFO:
    /// the open layers add up to the position and each layer's consumption equals what left it; an outflow's amount equals its consumptions; a transfer moves exactly the same value out as in; and the company total
    /// value equals the signed sum of all entries.
    /// </summary>
    public async Task<IReadOnlyList<CostDiscrepancy>> ReconcileAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var found = new List<CostDiscrepancy>();
        async Task Check(string name, string sql)
        {
            await using var q = new NpgsqlCommand(sql, conn);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) found.Add(new CostDiscrepancy(name, r.GetString(0)));
        }
        await Check("position_equals_entries",
            $"SELECT p.{PO.Id}::text FROM {PO.Table} p LEFT JOIN (SELECT {EN.PositionId} pid, sum({EN.Direction} * {EN.Quantity}) q, sum({EN.Direction} * {EN.Amount}) v FROM {EN.Table} GROUP BY {EN.PositionId}) e ON e.pid = p.{PO.Id} WHERE p.{PO.Quantity} <> coalesce(e.q, 0) OR p.{PO.Value} <> coalesce(e.v, 0)");
        await Check("position_equals_last_entry",
            $"SELECT p.{PO.Id}::text FROM {PO.Table} p JOIN {EN.Table} e ON e.{EN.PositionId} = p.{PO.Id} AND e.{EN.Seq} = p.{PO.LastSeq} WHERE p.{PO.Quantity} <> e.{EN.QuantityAfter} OR p.{PO.Value} <> e.{EN.ValueAfter}");
        await Check("fifo_layers_equal_position",
            $"SELECT p.{PO.Id}::text FROM {PO.Table} p LEFT JOIN (SELECT {LY.PositionId} pid, sum({LY.RemainingQuantity}) q, sum({LY.RemainingAmount}) v FROM {LY.Table} GROUP BY {LY.PositionId}) l ON l.pid = p.{PO.Id} WHERE p.{PO.Method} = 1 AND (p.{PO.Quantity} <> coalesce(l.q, 0) OR p.{PO.Value} <> coalesce(l.v, 0))");
        await Check("layer_consumption_equals_outflow",
            $"SELECT l.{LY.Id}::text FROM {LY.Table} l LEFT JOIN (SELECT {CS.LayerId} lid, sum(CASE WHEN {CS.Restores} THEN -{CS.Quantity} ELSE {CS.Quantity} END) q, sum(CASE WHEN {CS.Restores} THEN -{CS.Amount} ELSE {CS.Amount} END) a FROM {CS.Table} GROUP BY {CS.LayerId}) c ON c.lid = l.{LY.Id} " +
            $"WHERE l.{LY.OriginalQuantity} - l.{LY.RemainingQuantity} <> coalesce(c.q, 0) OR l.{LY.OriginalAmount} - l.{LY.RemainingAmount} <> coalesce(c.a, 0)");
        await Check("entry_equals_consumptions",
            $"SELECT e.{EN.Id}::text FROM {EN.Table} e JOIN {PO.Table} p ON p.{PO.Id} = e.{EN.PositionId} LEFT JOIN (SELECT {CS.EntryId} eid, sum({CS.Quantity}) q, sum({CS.Amount}) a FROM {CS.Table} GROUP BY {CS.EntryId}) c ON c.eid = e.{EN.Id} " +
            $"WHERE p.{PO.Method} = 1 AND (e.{EN.Direction} = -1 OR e.{EN.ReversesEntryId} IS NOT NULL) AND (coalesce(c.q, 0) <> e.{EN.Quantity} OR coalesce(c.a, 0) <> e.{EN.Amount})");
        await Check("transfer_value_conserved",
            $"SELECT o.{OP.Id}::text FROM {OP.Table} o JOIN {EN.Table} e ON e.{EN.OperationId} = o.{OP.Id} WHERE o.{OP.Kind} = 3 GROUP BY o.{OP.Id} HAVING sum(e.{EN.Direction} * e.{EN.Amount}) <> 0 OR sum(e.{EN.Direction} * e.{EN.Quantity}) <> 0");
        await Check("company_total_equals_entries",
            $"SELECT 'total' WHERE (SELECT coalesce(sum({PO.Value}), 0) FROM {PO.Table}) <> (SELECT coalesce(sum({EN.Direction} * {EN.Amount}), 0) FROM {EN.Table})");
        await Check("no_negative_balances",
            $"SELECT {PO.Id}::text FROM {PO.Table} WHERE {PO.Quantity} < 0 OR {PO.Value} < 0 OR ({PO.Quantity} = 0 AND {PO.Value} <> 0)");
        return found;
    }
}
