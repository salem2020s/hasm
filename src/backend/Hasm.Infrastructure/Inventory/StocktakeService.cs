using System.Globalization;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;
using S = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Stocktake;
using L = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StocktakeLine;
using E = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StocktakeEvent;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using IU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemUnit;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;

namespace Hasm.Infrastructure.Inventory;

/// <summary>
/// Stocktake sheets (Phase 3.8B-1): the physical count of ONE warehouse, the book quantity of the costing ledger (base unit, as of the count date) and the exact variance per item, from draft to approval.
/// <b>No authorization here by design:</b> <see cref="StocktakeManagementService"/> wraps it.
/// <para><b>This service never changes a stock figure.</b> It reads the append-only costing ledger for the book quantities and writes only the sheet; it creates no movement, cost operation or accounting entry.
/// The book quantity is a SNAPSHOT for information: retaken at every draft save, sheet generation and at submit, frozen from submit on. Approval does not re-check it: a later posting dated on or before the count
/// date only marks the line as stale. The adjustment slice (3.8B-2) books the variance against the CURRENT book quantity at that time.</para>
/// Every mutation runs in <see cref="CompanyAuditedTransactions"/> (change, history row and authenticated audit event commit or roll back together) after the warehouse-structure advisory lock, and every transition
/// is fenced by the revision (a repeated or concurrent request fails with a precondition failure and does nothing twice).
/// </summary>
public sealed class StocktakeService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, "validation_failed", [new FieldError(field, code)]);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static object DbN<T>(T? v) where T : struct => v is null ? DBNull.Value : v.Value;
    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static decimal? Dn(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static NpgsqlParameter DateParam(string name, DateOnly d) => new(name, NpgsqlDbType.Date) { Value = d.ToDateTime(TimeOnly.MinValue) };

    private async Task<ConfigResult<StocktakeView>> Mutate(Guid companyId, Func<CompanyAuditedWork, Task<StocktakeView>> work, CancellationToken ct)
    {
        try
        {
            return ConfigResult<StocktakeView>.Ok(await transactions.ExecuteAsync(companyId, async w =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", w.Connection, w.Transaction))
                {
                    lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
                    lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                return await work(w);
            }, ct));
        }
        catch (Abort a) { return a.To<StocktakeView>(); }
    }

    // ================================================================================================================ reads

    private static readonly string HeadCols = string.Join(", ", new[] { S.Id, S.Status, S.BranchId, S.WarehouseId, S.CountDate, S.Reference, S.Notes, S.Number, S.FiscalYearId, S.SnapshotAtUtc, S.CreatedBy, S.CreatedAtUtc,
        S.SubmittedBy, S.SubmittedAtUtc, S.DecidedBy, S.DecidedAtUtc, S.DecisionNote, S.CancelledBy, S.CancelledAtUtc, S.CancelReason, S.Revision, S.UpdatedAtUtc }.Select(c => "s." + c));

    internal static async Task<StocktakeView?> Load(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        StocktakeView? h;
        await using (var cmd = new NpgsqlCommand($"SELECT {HeadCols} FROM {S.Table} s WHERE s.{S.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            h = await r.ReadAsync(ct)
                ? new StocktakeView(r.GetGuid(0), r.GetInt16(1), r.GetGuid(2), r.GetGuid(3), Iso(r.GetFieldValue<DateOnly>(4)), Str(r, 5), Str(r, 6), Str(r, 7), G(r, 8), r.GetFieldValue<DateTimeOffset>(9), r.GetGuid(10),
                    r.GetFieldValue<DateTimeOffset>(11), G(r, 12), T(r, 13), G(r, 14), T(r, 15), Str(r, 16), G(r, 17), T(r, 18), Str(r, 19), r.GetInt64(20), r.GetFieldValue<DateTimeOffset>(21), [], [])
                : null;
        }
        if (h is null) return null;
        var lines = new List<StocktakeLineView>();
        // the current book quantity is computed with the same rule as the snapshot (costing ledger, this warehouse, effective on or before the count date)
        await using (var lc = new NpgsqlCommand(
            $"SELECT l.{L.LineNo}, l.{L.ItemId}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, l.{L.UnitId}, u.{U.Code}, l.{L.CountedQuantity}, l.{L.Factor}, l.{L.CountedBaseQuantity}, l.{L.BookQuantity}, l.{L.Variance}, bu.{U.Code}, l.{L.Reason}, " +
            $"COALESCE((SELECT SUM(e.{CE.Direction} * e.{CE.Quantity}) FROM {CE.Table} e WHERE e.{CE.ItemId} = l.{L.ItemId} AND e.{CE.WarehouseId} = s.{S.WarehouseId} AND e.{CE.EffectiveDate} <= s.{S.CountDate}), 0) " +
            $"FROM {L.Table} l JOIN {S.Table} s ON s.{S.Id} = l.{L.StocktakeId} JOIN {I.Table} i ON i.{I.Id} = l.{L.ItemId} JOIN {U.Table} u ON u.{U.Id} = l.{L.UnitId} JOIN {U.Table} bu ON bu.{U.Id} = i.{I.BaseUnitId} " +
            $"WHERE l.{L.StocktakeId} = @id ORDER BY l.{L.LineNo}", conn, tx))
        {
            lc.Parameters.AddWithValue("id", id);
            await using var lr = await lc.ExecuteReaderAsync(ct);
            while (await lr.ReadAsync(ct))
            {
                var book = lr.GetDecimal(10); var current = lr.GetDecimal(14);
                lines.Add(new StocktakeLineView(lr.GetInt32(0), lr.GetGuid(1), lr.GetString(2), Str(lr, 3), Str(lr, 4), lr.GetGuid(5), lr.GetString(6), Dn(lr, 7), lr.GetDecimal(8), Dn(lr, 9), book, Dn(lr, 11), lr.GetString(12), Str(lr, 13), current, current != book));
            }
        }
        var events = new List<StockEventView>();
        await using (var ec = new NpgsqlCommand($"SELECT {E.Id}, {E.Kind}, {E.FromStatus}, {E.ToStatus}, {E.ActorUserId}, {E.AtUtc}, {E.Note} FROM {E.Table} WHERE {E.StocktakeId} = @id ORDER BY {E.Id}", conn, tx))
        {
            ec.Parameters.AddWithValue("id", id);
            await using var er = await ec.ExecuteReaderAsync(ct);
            while (await er.ReadAsync(ct)) events.Add(new StockEventView(er.GetInt64(0), er.GetInt16(1), er.IsDBNull(2) ? null : er.GetInt16(2), er.GetInt16(3), er.GetGuid(4), er.GetFieldValue<DateTimeOffset>(5), Str(er, 6)));
        }
        // the units each line may be counted in (follow-up M2 of the 3.8B-1 review): the base unit, the active alternate units and always the unit the line was saved in, with their exact factors
        var units = new Dictionary<Guid, List<StockUnitRef>>();
        if (lines.Count > 0)
        {
            await using var uq = new NpgsqlCommand(
                $"SELECT i.{I.Id}, u.{U.Id}, u.{U.Code}, u.{U.NameAr}, u.{U.NameEn}, u.{U.Decimals}, 1::numeric, true, u.{U.IsActive} FROM {I.Table} i JOIN {U.Table} u ON u.{U.Id} = i.{I.BaseUnitId} WHERE i.{I.Id} = ANY(@ids) " +
                $"UNION ALL SELECT iu.{IU.ItemId}, u.{U.Id}, u.{U.Code}, u.{U.NameAr}, u.{U.NameEn}, u.{U.Decimals}, iu.{IU.Factor}, false, u.{U.IsActive} FROM {IU.Table} iu JOIN {U.Table} u ON u.{U.Id} = iu.{IU.UnitId} WHERE iu.{IU.ItemId} = ANY(@ids) " +
                "ORDER BY 1, 8 DESC, 3", conn, tx);
            uq.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = lines.Select(l => l.ItemId).Distinct().ToArray() });
            var saved = lines.ToDictionary(l => l.ItemId, l => l.UnitId);
            await using var ur = await uq.ExecuteReaderAsync(ct);
            while (await ur.ReadAsync(ct))
            {
                var item = ur.GetGuid(0); var unit = ur.GetGuid(1); var isBase = ur.GetBoolean(7);
                if (!isBase && !ur.GetBoolean(8) && saved.GetValueOrDefault(item) != unit) continue;
                if (!units.TryGetValue(item, out var list)) units[item] = list = [];
                list.Add(new StockUnitRef(unit, ur.GetString(2), Str(ur, 3), Str(ur, 4), ur.GetInt16(5), ur.GetDecimal(6), isBase));
            }
        }
        Guid? adjustment = null; string? adjustmentNumber = null;
        await using (var aq = new NpgsqlCommand($"SELECT {CompanySchema.StockAdjustment.Id}, {CompanySchema.StockAdjustment.Number} FROM {CompanySchema.StockAdjustment.Table} WHERE {CompanySchema.StockAdjustment.StocktakeId} = @id", conn, tx))
        {
            aq.Parameters.AddWithValue("id", id);
            await using var ar = await aq.ExecuteReaderAsync(ct);
            if (await ar.ReadAsync(ct)) { adjustment = ar.GetGuid(0); adjustmentNumber = Str(ar, 1); }
        }
        return h with { Lines = [.. lines.Select(l => l with { Units = units.GetValueOrDefault(l.ItemId) ?? [] })], Events = events, AdjustmentId = adjustment, AdjustmentNumber = adjustmentNumber };
    }

    public async Task<StocktakeView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await Load(conn, null, id, false, ct);
    }

    /// <summary>The warehouse of a stocktake (for the authorization of the caller), or null when it does not exist.</summary>
    public async Task<Guid?> WarehouseOfAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {S.WarehouseId} FROM {S.Table} WHERE {S.Id} = @id", conn);
        q.Parameters.AddWithValue("id", id);
        return await q.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    /// <summary>Filtered, paged list (newest first). <see cref="StocktakeFilter.VisibleWarehouses"/> (null = all) is decided by the caller's permissions and applied inside the query, before counting and paging.</summary>
    public async Task<StocktakePage> ListAsync(Guid companyId, StocktakeFilter f, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = new List<string>();
        await using var cmd = new NpgsqlCommand { Connection = conn };
        if (f.Status is { } st) { where.Add($"s.{S.Status} = @st"); cmd.Parameters.AddWithValue("st", (short)st); }
        if (f.WarehouseId is { } w) { where.Add($"s.{S.WarehouseId} = @w"); cmd.Parameters.AddWithValue("w", w); }
        if (DateOnly.TryParseExact(f.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) { where.Add($"s.{S.CountDate} >= @from"); cmd.Parameters.Add(DateParam("from", from)); }
        if (DateOnly.TryParseExact(f.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) { where.Add($"s.{S.CountDate} <= @to"); cmd.Parameters.Add(DateParam("to", to)); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            where.Add($"(upper(coalesce(s.{S.Number}, '')) LIKE @like OR upper(coalesce(s.{S.Reference}, '')) LIKE @like)");
            cmd.Parameters.AddWithValue("like", "%" + f.Search.Trim().ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        if (f.VisibleWarehouses is { } vis)
        {
            where.Add($"s.{S.WarehouseId} = ANY(@vis)");
            cmd.Parameters.Add(new NpgsqlParameter("vis", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = vis.ToArray() });
        }
        var wh = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"SELECT count(*) FROM {S.Table} s{wh}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        cmd.Parameters.AddWithValue("lim", Math.Clamp(f.Limit, 1, 200));
        cmd.Parameters.AddWithValue("off", Math.Max(0, f.Offset));
        cmd.CommandText = $"SELECT s.{S.Id}, s.{S.Status}, s.{S.Number}, s.{S.CountDate}, s.{S.BranchId}, s.{S.WarehouseId}, s.{S.Reference}, " +
                          $"(SELECT count(*) FROM {L.Table} l WHERE l.{L.StocktakeId} = s.{S.Id}), (SELECT count(*) FROM {L.Table} l WHERE l.{L.StocktakeId} = s.{S.Id} AND l.{L.CountedQuantity} IS NOT NULL), " +
                          $"(SELECT count(*) FROM {L.Table} l WHERE l.{L.StocktakeId} = s.{S.Id} AND l.{L.Variance} <> 0), s.{S.Revision}, s.{S.CreatedAtUtc}, s.{S.UpdatedAtUtc} " +
                          $"FROM {S.Table} s{wh} ORDER BY s.{S.CreatedAtUtc} DESC, s.{S.Id} LIMIT @lim OFFSET @off";
        var items = new List<StocktakeSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new StocktakeSummary(r.GetGuid(0), r.GetInt16(1), Str(r, 2), Iso(r.GetFieldValue<DateOnly>(3)), r.GetGuid(4), r.GetGuid(5), Str(r, 6), (int)r.GetInt64(7), (int)r.GetInt64(8), (int)r.GetInt64(9),
                r.GetInt64(10), r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<DateTimeOffset>(12)));
        return new StocktakePage(items, total);
    }

    // ================================================================================================================ validation against the real data

    /// <summary>The warehouse must exist and be active, and so must its branch. Returns the branch id and code.</summary>
    private static async Task<(Guid BranchId, string BranchCode)> ResolveWarehouse(CompanyAuditedWork work, Guid warehouseId, CancellationToken ct)
    {
        Guid branch; bool active;
        await using (var q = new NpgsqlCommand($"SELECT {W.BranchId}, {W.IsActive} FROM {W.Table} WHERE {W.Id} = @w FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("w", warehouseId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw Invalid("warehouseId", "warehouse_not_found");
            branch = r.GetGuid(0); active = r.GetBoolean(1);
        }
        if (!active) throw new Abort(ConfigStatus.Conflict, "warehouse_inactive");
        await using var bq = new NpgsqlCommand($"SELECT {B.Code}, {B.IsActive} FROM {B.Table} WHERE {B.Id} = @b FOR SHARE", work.Connection, work.Transaction);
        bq.Parameters.AddWithValue("b", branch);
        await using var br = await bq.ExecuteReaderAsync(ct);
        await br.ReadAsync(ct);
        if (!br.GetBoolean(1)) throw new Abort(ConfigStatus.Conflict, "branch_inactive");
        return (branch, br.GetString(0));
    }

    /// <summary>The book quantity (base unit) of each item in the warehouse as of the date, from the append-only costing ledger (absent = zero).</summary>
    internal static async Task<Dictionary<Guid, decimal>> BookAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid warehouseId, DateOnly asOf, IReadOnlyCollection<Guid>? items, CancellationToken ct)
    {
        var filter = items is null ? "" : $" AND {CE.ItemId} = ANY(@ids)";
        var having = items is null ? $" HAVING SUM({CE.Direction} * {CE.Quantity}) <> 0" : "";
        await using var q = new NpgsqlCommand($"SELECT {CE.ItemId}, SUM({CE.Direction} * {CE.Quantity}) FROM {CE.Table} WHERE {CE.WarehouseId} = @w AND {CE.EffectiveDate} <= @d{filter} GROUP BY {CE.ItemId}{having}", conn, tx);
        q.Parameters.AddWithValue("w", warehouseId);
        q.Parameters.Add(DateParam("d", asOf));
        if (items is not null) q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = items.Distinct().ToArray() });
        var book = new Dictionary<Guid, decimal>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) book[r.GetGuid(0)] = r.GetDecimal(1);
        return book;
    }

    private sealed record ItemRow(Guid Id, int Type, Guid BaseUnitId, bool Tracked);
    private sealed record Resolved(Guid ItemId, Guid UnitId, decimal? Counted, decimal Factor, decimal? CountedBase, decimal Book, string? Reason);

    /// <summary>
    /// Resolves lines against the current items and units and takes the book snapshot. An inactive item is accepted (it may still hold stock that must be counted); a service and an item tracked by lot, serial
    /// or expiry are refused (tracked counting needs lot/serial identification, which does not exist yet). The counted quantity is converted into the base unit exactly or refused, never rounded.
    /// </summary>
    private static async Task<List<Resolved>> ResolveLines(CompanyAuditedWork work, Guid warehouseId, DateOnly asOf, IReadOnlyList<StocktakeValidation.Line> lines, CancellationToken ct)
    {
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToArray();
        var items = new Dictionary<Guid, ItemRow>();
        await using (var q = new NpgsqlCommand($"SELECT {I.Id}, {I.ItemType}, {I.BaseUnitId}, ({I.IsLot} OR {I.IsSerial} OR {I.HasExpiry}) FROM {I.Table} WHERE {I.Id} = ANY(@ids) FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = itemIds });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items[r.GetGuid(0)] = new ItemRow(r.GetGuid(0), r.GetInt16(1), r.GetGuid(2), r.GetBoolean(3));
        }
        var units = new Dictionary<Guid, (bool Active, int Decimals)>();
        await using (var q = new NpgsqlCommand($"SELECT {U.Id}, {U.IsActive}, {U.Decimals} FROM {U.Table}", work.Connection, work.Transaction))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) units[r.GetGuid(0)] = (r.GetBoolean(1), r.GetInt16(2));
        }
        var factors = new Dictionary<(Guid, Guid), decimal>();
        await using (var q = new NpgsqlCommand($"SELECT {IU.ItemId}, {IU.UnitId}, {IU.Factor} FROM {IU.Table} WHERE {IU.ItemId} = ANY(@ids)", work.Connection, work.Transaction))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = itemIds });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) factors[(r.GetGuid(0), r.GetGuid(1))] = r.GetDecimal(2);
        }
        var book = await BookAsync(work.Connection, work.Transaction, warehouseId, asOf, itemIds, ct);
        var result = new List<Resolved>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var f = $"lines[{i}]";
            if (!items.TryGetValue(l.ItemId, out var item)) throw Invalid(f + ".itemId", "item_not_found");
            if (item.Type != (int)ItemType.Goods) throw Invalid(f + ".itemId", "item_not_stockable");
            if (item.Tracked) throw Invalid(f + ".itemId", "tracked_item_not_supported_yet");
            if (!units.TryGetValue(l.UnitId, out var unit)) throw Invalid(f + ".unitId", "unit_not_found");
            decimal factor;
            if (l.UnitId == item.BaseUnitId) factor = 1m;
            else if (factors.TryGetValue((l.ItemId, l.UnitId), out var fa)) factor = fa;
            else throw Invalid(f + ".unitId", "unit_not_defined_for_item");
            if (!unit.Active && l.UnitId != item.BaseUnitId) throw Invalid(f + ".unitId", "unit_inactive");
            decimal? countedBase = null;
            if (l.Counted is { } counted)
            {
                decimal b; string? err;
                try { (b, err) = StockDocumentValidation.Convert(counted, factor, unit.Decimals, units[item.BaseUnitId].Decimals); }
                catch (OverflowException) { (b, err) = (0, "invalid_quantity"); }
                if (err is not null) throw Invalid(f + ".countedQuantity", err);
                countedBase = b;
            }
            result.Add(new Resolved(l.ItemId, l.UnitId, l.Counted, factor, countedBase, book.GetValueOrDefault(l.ItemId), l.Reason));
        }
        return result;
    }

    // ================================================================================================================ writes

    private async Task RecordAsync(CompanyAuditedWork work, StocktakeEventKind kind, Guid actor, Guid id, StocktakeStatus? from, StocktakeStatus to, string? note, string detail, CancellationToken ct)
    {
        var receipt = await work.AppendAuditAsync(new AuditEventData(StocktakeAuditEvents.Of(kind), ActorUserId: actor, SubjectId: id, Detail: detail), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {E.Table} ({E.StocktakeId}, {E.Kind}, {E.FromStatus}, {E.ToStatus}, {E.ActorUserId}, {E.AtUtc}, {E.Note}, {E.AuditStream}, {E.AuditSeq}) VALUES (@d, @k, @f, @t, @a, @at, @n, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("d", id);
        cmd.Parameters.AddWithValue("k", (short)kind);
        cmd.Parameters.AddWithValue("f", from is { } fs ? (short)fs : DBNull.Value);
        cmd.Parameters.AddWithValue("t", (short)to);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("at", Now);
        cmd.Parameters.AddWithValue("n", Db(note));
        cmd.Parameters.AddWithValue("s", receipt.StreamKey);
        cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteLines(CompanyAuditedWork work, Guid id, IReadOnlyList<Resolved> lines, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand($"DELETE FROM {L.Table} WHERE {L.StocktakeId} = @d", work.Connection, work.Transaction))
        {
            del.Parameters.AddWithValue("d", id);
            await del.ExecuteNonQueryAsync(ct);
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {L.Table} ({L.StocktakeId}, {L.LineNo}, {L.ItemId}, {L.UnitId}, {L.CountedQuantity}, {L.Factor}, {L.CountedBaseQuantity}, {L.BookQuantity}, {L.Variance}, {L.Reason}) VALUES (@d, @n, @i, @u, @q, @f, @b, @bk, @v, @r)",
                work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("d", id); ins.Parameters.AddWithValue("n", i + 1); ins.Parameters.AddWithValue("i", l.ItemId); ins.Parameters.AddWithValue("u", l.UnitId);
            ins.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Numeric) { Value = DbN(l.Counted) });
            ins.Parameters.AddWithValue("f", l.Factor);
            ins.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Numeric) { Value = DbN(l.CountedBase) });
            ins.Parameters.AddWithValue("bk", l.Book);
            ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Numeric) { Value = DbN(StocktakeValidation.Variance(l.CountedBase, l.Book)) });
            ins.Parameters.AddWithValue("r", Db(l.Reason));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private static List<StocktakeValidation.Line> LinesOf(StocktakeView v) => [.. v.Lines.Select(l => new StocktakeValidation.Line(l.ItemId, l.UnitId, l.CountedQuantity, l.Reason))];
    private static DateOnly DateOf(StocktakeView v) => DateOnly.ParseExact(v.CountDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<ConfigResult<StocktakeView>> CreateAsync(Guid actor, Guid companyId, StocktakeInput? input, CancellationToken ct = default)
    {
        if (!StocktakeValidation.TryNormalize(input, null, out var h, out var lines, out var errors)) return ConfigResult<StocktakeView>.Invalid(errors);
        return await Mutate(companyId, async work =>
        {
            var (branch, _) = await ResolveWarehouse(work, h.WarehouseId, ct);
            var resolved = await ResolveLines(work, h.WarehouseId, h.CountDate, lines, ct);
            var id = Guid.NewGuid();
            await using (var ins = new NpgsqlCommand(
                $"INSERT INTO {S.Table} ({S.Id}, {S.Status}, {S.BranchId}, {S.WarehouseId}, {S.CountDate}, {S.Reference}, {S.Notes}, {S.SnapshotAtUtc}, {S.CreatedBy}, {S.CreatedAtUtc}, {S.Revision}, {S.UpdatedAtUtc}) " +
                "VALUES (@id, 1, @b, @w, @date, @ref, @notes, @now, @a, @now, 1, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("b", branch); ins.Parameters.AddWithValue("w", h.WarehouseId); ins.Parameters.Add(DateParam("date", h.CountDate));
                ins.Parameters.AddWithValue("ref", Db(h.Reference)); ins.Parameters.AddWithValue("notes", Db(h.Notes)); ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await WriteLines(work, id, resolved, ct);
            await RecordAsync(work, StocktakeEventKind.Created, actor, id, null, StocktakeStatus.Draft, null, $"stocktake created lines={resolved.Count}", ct);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    private static async Task<StocktakeView> Require(CompanyAuditedWork work, Guid id, long? expectedRevision, StocktakeStatus? mustBe, CancellationToken ct)
    {
        var cur = await Load(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "stocktake_not_found");
        if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
        if (mustBe is { } m && cur.Status != (int)m) throw new Abort(ConfigStatus.Conflict, "invalid_status");
        return cur;
    }

    private static async Task SetStatus(CompanyAuditedWork work, Guid id, StocktakeStatus status, DateTime now, string? extraSet, Action<NpgsqlCommand>? bind, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand($"UPDATE {S.Table} SET {S.Status} = @st, {S.Revision} = {S.Revision} + 1, {S.UpdatedAtUtc} = @now{(extraSet is null ? "" : ", " + extraSet)} WHERE {S.Id} = @id", work.Connection, work.Transaction);
        upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("st", (short)status); upd.Parameters.AddWithValue("now", now);
        bind?.Invoke(upd);
        await upd.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveDraft(CompanyAuditedWork work, Guid id, StocktakeValidation.Header h, IReadOnlyList<Resolved> resolved, CancellationToken ct)
    {
        await using (var upd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.CountDate} = @date, {S.Reference} = @ref, {S.Notes} = @notes, {S.SnapshotAtUtc} = @now, {S.Revision} = {S.Revision} + 1, {S.UpdatedAtUtc} = @now WHERE {S.Id} = @id", work.Connection, work.Transaction))
        {
            upd.Parameters.AddWithValue("id", id); upd.Parameters.Add(DateParam("date", h.CountDate)); upd.Parameters.AddWithValue("ref", Db(h.Reference)); upd.Parameters.AddWithValue("notes", Db(h.Notes)); upd.Parameters.AddWithValue("now", Now);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await WriteLines(work, id, resolved, ct);
    }

    /// <summary>Edits a draft: header (count date, reference, notes; never the warehouse) and the full set of lines. The book snapshot of every line is retaken as of the (possibly new) count date.</summary>
    public Task<ConfigResult<StocktakeView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, StocktakeInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<StocktakeView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await Require(work, id, expectedRevision, StocktakeStatus.Draft, ct);
            if (!StocktakeValidation.TryNormalize(input, cur.WarehouseId, out var h, out var lines, out var errors)) throw new Abort(ConfigStatus.Invalid, "validation_failed", errors);
            await ResolveWarehouse(work, h.WarehouseId, ct);
            var resolved = await ResolveLines(work, h.WarehouseId, h.CountDate, lines, ct);
            await SaveDraft(work, id, h, resolved, ct);
            await RecordAsync(work, StocktakeEventKind.Updated, actor, id, StocktakeStatus.Draft, StocktakeStatus.Draft, null, $"stocktake updated r{cur.Revision + 1} lines={resolved.Count}", ct);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>
    /// Generates or extends the sheet of a draft: adds one uncounted line (base unit) for every item with a non-zero book quantity in the warehouse at the count date (or for exactly the chosen items) that is not
    /// on the sheet yet; existing lines and their counts are kept; every book snapshot is retaken. Items that cannot be counted (services, tracked items) are refused when chosen and skipped otherwise.
    /// </summary>
    public Task<ConfigResult<StocktakeView>> GenerateSheetAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, StocktakeSheetInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<StocktakeView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        var chosen = input?.ItemIds?.Where(x => x != Guid.Empty).Distinct().ToList();
        if (input?.ItemIds is { Count: > 0 } && chosen!.Count == 0) return Task.FromResult(ConfigResult<StocktakeView>.Invalid([new("itemIds", "required")]));
        if (chosen is { Count: > StocktakeValidation.MaxLines }) return Task.FromResult(ConfigResult<StocktakeView>.Invalid([new("itemIds", "too_many")]));
        return Mutate(companyId, async work =>
        {
            var cur = await Require(work, id, expectedRevision, StocktakeStatus.Draft, ct);
            var date = DateOf(cur);
            await ResolveWarehouse(work, cur.WarehouseId, ct);
            List<Guid> add;
            if (chosen is { Count: > 0 }) add = chosen;
            else
            {
                var held = await BookAsync(work.Connection, work.Transaction, cur.WarehouseId, date, null, ct);
                // only what can be counted: goods without lot/serial/expiry tracking (tracked items cannot hold stock today: stock documents refuse them)
                await using var q = new NpgsqlCommand($"SELECT {I.Id} FROM {I.Table} WHERE {I.Id} = ANY(@ids) AND {I.ItemType} = 1 AND NOT ({I.IsLot} OR {I.IsSerial} OR {I.HasExpiry}) ORDER BY {I.Code}", work.Connection, work.Transaction);
                q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = held.Keys.ToArray() });
                add = [];
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) add.Add(r.GetGuid(0));
            }
            var present = cur.Lines.Select(l => l.ItemId).ToHashSet();
            var baseUnits = new Dictionary<Guid, Guid>();
            await using (var q = new NpgsqlCommand($"SELECT {I.Id}, {I.BaseUnitId} FROM {I.Table} WHERE {I.Id} = ANY(@ids)", work.Connection, work.Transaction))
            {
                q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = add.ToArray() });
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) baseUnits[r.GetGuid(0)] = r.GetGuid(1);
            }
            var lines = LinesOf(cur);
            foreach (var item in add.Where(x => !present.Contains(x)))
            {
                if (!baseUnits.TryGetValue(item, out var bu)) throw Invalid("itemIds", "item_not_found");
                lines.Add(new StocktakeValidation.Line(item, bu, null, null));
            }
            if (lines.Count > StocktakeValidation.MaxLines) throw Invalid("itemIds", "too_many");
            List<Resolved> resolved;
            try { resolved = await ResolveLines(work, cur.WarehouseId, date, lines, ct); }
            catch (Abort) when (chosen is { Count: > 0 }) { throw Invalid("itemIds", "item_not_countable"); }
            await SaveDraft(work, id, new StocktakeValidation.Header(cur.WarehouseId, date, cur.Reference, cur.Notes), resolved, ct);
            await RecordAsync(work, StocktakeEventKind.SheetGenerated, actor, id, StocktakeStatus.Draft, StocktakeStatus.Draft, null, $"stocktake sheet generated added={resolved.Count - cur.Lines.Count} lines={resolved.Count}", ct);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>
    /// Draft to submitted. The sheet must be complete: at least one line, every line counted, a reason on every line with a variance. The references are validated again and the book snapshot is retaken one last
    /// time as of the count date; from now on the sheet (counts, snapshot, variance) is frozen.
    /// </summary>
    public Task<ConfigResult<StocktakeView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, StocktakeStatus.Draft, async (work, cur) =>
        {
            await ResolveWarehouse(work, cur.WarehouseId, ct);
            if (cur.Lines.Count == 0) throw Invalid("lines", "required");
            var resolved = await ResolveLines(work, cur.WarehouseId, DateOf(cur), LinesOf(cur), ct);
            var missing = new List<FieldError>();
            for (var i = 0; i < resolved.Count; i++)
            {
                if (resolved[i].CountedBase is null) missing.Add(new($"lines[{i}].countedQuantity", "required"));
                else if (StocktakeValidation.Variance(resolved[i].CountedBase, resolved[i].Book) != 0 && resolved[i].Reason is null) missing.Add(new($"lines[{i}].reason", "required_for_variance"));
            }
            if (missing.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", missing);
            await WriteLines(work, id, resolved, ct);                                                             // the final snapshot, frozen from here on
            await SetStatus(work, id, StocktakeStatus.Submitted, Now, $"{S.SubmittedBy} = @who, {S.SubmittedAtUtc} = @now, {S.SnapshotAtUtc} = @now", c => c.Parameters.AddWithValue("who", actor), ct);
            await RecordAsync(work, StocktakeEventKind.Submitted, actor, id, StocktakeStatus.Draft, StocktakeStatus.Submitted, null, $"stocktake submitted lines={resolved.Count}", ct);
        }, ct);

    public Task<ConfigResult<StocktakeView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, StocktakeStatus.Submitted, async (work, cur) =>
        {
            await SetStatus(work, id, StocktakeStatus.Draft, Now, $"{S.SubmittedBy} = NULL, {S.SubmittedAtUtc} = NULL", null, ct);
            await RecordAsync(work, StocktakeEventKind.Withdrawn, actor, id, StocktakeStatus.Submitted, StocktakeStatus.Draft, null, "stocktake withdrawn to draft", ct);
        }, ct);

    /// <summary>
    /// Submitted to approved by someone other than the submitter. The count date must lie in an OPEN fiscal period; the official number is issued in the same transaction. The book snapshot is NOT re-checked
    /// (a later back-dated posting only marks lines as stale). Approval records the decision only: no movement, cost or accounting entry.
    /// </summary>
    public Task<ConfigResult<StocktakeView>> ApproveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, StocktakeStatus.Submitted, async (work, cur) =>
        {
            if (cur.SubmittedBy == actor) throw new Abort(ConfigStatus.Conflict, "self_approval_not_allowed");
            var (_, branchCode) = await ResolveWarehouse(work, cur.WarehouseId, ct);
            var check = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, DateOf(cur), false, ct);
            if (!check.Allowed) throw new Abort(ConfigStatus.Conflict, check.Reason!);
            Guid yearId; int yearLabel;
            await using (var q = new NpgsqlCommand($"SELECT y.{FY.Id}, EXTRACT(YEAR FROM y.{FY.StartDate})::int FROM {CompanySchema.FiscalPeriod.Table} p JOIN {FY.Table} y ON y.{FY.Id} = p.{CompanySchema.FiscalPeriod.YearId} WHERE p.{CompanySchema.FiscalPeriod.Id} = @p", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("p", check.PeriodId!.Value);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                yearId = r.GetGuid(0); yearLabel = r.GetInt32(1);
            }
            var number = await NumberingService.IssueAsync(work.Connection, work.Transaction, DocumentTypes.Stocktake, new NumberScope(cur.BranchId, branchCode, yearId, yearLabel.ToString(CultureInfo.InvariantCulture)), id, actor, Now, ct);
            await SetStatus(work, id, StocktakeStatus.Approved, Now, $"{S.Number} = @num, {S.FiscalYearId} = @fy, {S.DecidedBy} = @who, {S.DecidedAtUtc} = @now", c => { c.Parameters.AddWithValue("num", number.Number); c.Parameters.AddWithValue("fy", yearId); c.Parameters.AddWithValue("who", actor); }, ct);
            await RecordAsync(work, StocktakeEventKind.Approved, actor, id, StocktakeStatus.Submitted, StocktakeStatus.Approved, null, $"stocktake approved number={number.Number}", ct);
        }, ct);

    public Task<ConfigResult<StocktakeView>> RejectAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? note, CancellationToken ct = default)
    {
        var n = note?.Trim();
        if (string.IsNullOrEmpty(n)) return Task.FromResult(ConfigResult<StocktakeView>.Invalid([new("note", "required")]));
        if (n.Length > 500) return Task.FromResult(ConfigResult<StocktakeView>.Invalid([new("note", "too_long")]));
        return Transition(companyId, id, expectedRevision, StocktakeStatus.Submitted, async (work, cur) =>
        {
            if (cur.SubmittedBy == actor) throw new Abort(ConfigStatus.Conflict, "self_approval_not_allowed");
            await SetStatus(work, id, StocktakeStatus.Rejected, Now, $"{S.DecidedBy} = @who, {S.DecidedAtUtc} = @now, {S.DecisionNote} = @note", c => { c.Parameters.AddWithValue("who", actor); c.Parameters.AddWithValue("note", n); }, ct);
            await RecordAsync(work, StocktakeEventKind.Rejected, actor, id, StocktakeStatus.Submitted, StocktakeStatus.Rejected, n, "stocktake rejected", ct);
        }, ct);
    }

    /// <summary>Draft, submitted or approved to cancelled with a mandatory reason; an approved stocktake keeps its number, which is marked void and never issued again.</summary>
    public Task<ConfigResult<StocktakeView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r)) return Task.FromResult(ConfigResult<StocktakeView>.Invalid([new("reason", "required")]));
        if (r.Length > 500) return Task.FromResult(ConfigResult<StocktakeView>.Invalid([new("reason", "too_long")]));
        return Transition(companyId, id, expectedRevision, null, async (work, cur) =>
        {
            if (cur.Status is not (1 or 2 or 3)) throw new Abort(ConfigStatus.Conflict, "invalid_status");
            // the count is booked (or about to be booked) by its adjustment: the stocktake stays as the record of that count (PostgreSQL enforces this too)
            if (cur.AdjustmentId is not null) throw new Abort(ConfigStatus.Conflict, "adjustment_exists");
            if (cur.Status == 3 && cur.Number is { } number)
                await NumberingService.VoidAsync(work.Connection, work.Transaction, DocumentTypes.Stocktake, number, actor, r.Length > 200 ? r[..200] : r, Now, ct);
            await SetStatus(work, id, StocktakeStatus.Cancelled, Now, $"{S.CancelledBy} = @who, {S.CancelledAtUtc} = @now, {S.CancelReason} = @why", c => { c.Parameters.AddWithValue("who", actor); c.Parameters.AddWithValue("why", r); }, ct);
            await RecordAsync(work, StocktakeEventKind.Cancelled, actor, id, (StocktakeStatus)cur.Status, StocktakeStatus.Cancelled, r, "stocktake cancelled", ct);
        }, ct);
    }

    private Task<ConfigResult<StocktakeView>> Transition(Guid companyId, Guid id, long? expectedRevision, StocktakeStatus? from, Func<CompanyAuditedWork, StocktakeView, Task> body, CancellationToken ct)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<StocktakeView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await Require(work, id, expectedRevision, from, ct);
            await body(work, cur);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }
}

/// <summary>
/// Authorized entry point for stocktakes (the only thing the API calls). Permissions are re-read on every call and are WAREHOUSE-scoped (<c>company.stocktake.*</c>): a stocktake needs the permission on its warehouse;
/// a company-wide grant covers every warehouse; there is no inheritance from branches. A stocktake the caller may not view is never disclosed (forbidden, not "not found"). Separation of duties (the approver is not
/// the submitter) is enforced by <see cref="StocktakeService"/> and by PostgreSQL.
/// </summary>
public sealed class StocktakeManagementService(AccessService access, StockDocumentService documents, StocktakeService stocktakes)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    public async Task<StocktakeCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        var all = (await documents.ListWarehousesAsync(company, ct)).Select(w => w.Id).ToList();
        IReadOnlyList<Guid> Of(string code) => [.. all.Where(w => eff.Has(code, ScopeTarget.Warehouse(w)))];
        return new StocktakeCapabilities(Of(PermissionCatalog.Codes.StocktakeView), Of(PermissionCatalog.Codes.StocktakeCreate), Of(PermissionCatalog.Codes.StocktakeUpdate),
            Of(PermissionCatalog.Codes.StocktakeSubmit), Of(PermissionCatalog.Codes.StocktakeApprove), Of(PermissionCatalog.Codes.StocktakeCancel),
            // Phase 3.8B-2: the adjustment of an approved stocktake (no new permission: generating creates a stock movement document, posting and reversing are those of stock documents)
            [.. all.Where(w => eff.Has(PermissionCatalog.Codes.StocktakeView, ScopeTarget.Warehouse(w)) && eff.Has(PermissionCatalog.Codes.StockDocsCreate, ScopeTarget.Warehouse(w)))],
            Of(PermissionCatalog.Codes.StockDocsPost), Of(PermissionCatalog.Codes.StockDocsReverse));
    }

    private Task<bool> AllowedAsync(Guid user, Guid company, string code, Guid warehouse, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Warehouse(warehouse), ct);

    /// <summary>Warehouses the caller may see or count (view, create or update).</summary>
    public async Task<ConfigResult<IReadOnlyList<StockWarehouseRef>>> WarehousesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        var allowed = caps.View.Concat(caps.Create).Concat(caps.Update).ToHashSet();
        if (allowed.Count == 0) return Denied<IReadOnlyList<StockWarehouseRef>>();
        return ConfigResult<IReadOnlyList<StockWarehouseRef>>.Ok([.. (await documents.ListWarehousesAsync(company, ct)).Where(w => allowed.Contains(w.Id))]);
    }

    /// <summary>Countable items for the sheet editor; only for callers who can create or edit stocktakes somewhere.</summary>
    public async Task<ConfigResult<IReadOnlyList<StockItemRef>>> SearchItemsAsync(Guid user, Guid company, string? search, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        if (caps.Create.Count == 0 && caps.Update.Count == 0) return Denied<IReadOnlyList<StockItemRef>>();
        return ConfigResult<IReadOnlyList<StockItemRef>>.Ok(await documents.SearchItemsAsync(company, search, ct));
    }

    public async Task<ConfigResult<StocktakePage>> ListAsync(Guid user, Guid company, StocktakeFilter filter, CancellationToken ct = default)
    {
        var caps = await CapabilitiesAsync(user, company, ct);
        if (caps.View.Count == 0) return Denied<StocktakePage>();
        // a requested warehouse the caller cannot view is not disclosed: the list is restricted, inside the query, to what they may see
        return ConfigResult<StocktakePage>.Ok(await stocktakes.ListAsync(company, filter with { VisibleWarehouses = caps.View }, ct));
    }

    public async Task<ConfigResult<StocktakeView>> GetAsync(Guid user, Guid company, Guid id, CancellationToken ct = default)
    {
        var wh = await stocktakes.WarehouseOfAsync(company, id, ct);
        if (wh is null) return (await CapabilitiesAsync(user, company, ct)).View.Count > 0 ? ConfigResult<StocktakeView>.Fail(ConfigStatus.NotFound, "stocktake_not_found") : Denied<StocktakeView>();
        if (!await AllowedAsync(user, company, PermissionCatalog.Codes.StocktakeView, wh.Value, ct)) return Denied<StocktakeView>();
        var v = await stocktakes.GetAsync(company, id, ct);
        return v is null ? ConfigResult<StocktakeView>.Fail(ConfigStatus.NotFound, "stocktake_not_found") : ConfigResult<StocktakeView>.Ok(v);
    }

    public async Task<ConfigResult<StocktakeView>> CreateAsync(Guid user, Guid company, StocktakeInput? input, CancellationToken ct = default)
    {
        if (input?.WarehouseId is not { } wh || wh == Guid.Empty)
            return (await CapabilitiesAsync(user, company, ct)).Create.Count > 0 ? await stocktakes.CreateAsync(user, company, input, ct) : Denied<StocktakeView>();   // a validation error, if allowed to create anywhere
        return await AllowedAsync(user, company, PermissionCatalog.Codes.StocktakeCreate, wh, ct) ? await stocktakes.CreateAsync(user, company, input, ct) : Denied<StocktakeView>();
    }

    private async Task<ConfigResult<StocktakeView>> OnAsync(Guid user, Guid company, Guid id, string code, Func<Task<ConfigResult<StocktakeView>>> run, CancellationToken ct)
    {
        var wh = await stocktakes.WarehouseOfAsync(company, id, ct);
        if (wh is null) return (await CapabilitiesAsync(user, company, ct)).View.Count > 0 ? ConfigResult<StocktakeView>.Fail(ConfigStatus.NotFound, "stocktake_not_found") : Denied<StocktakeView>();
        // the caller must be able to see the stocktake at all (no existence probing), then hold the specific permission on its warehouse
        if (!await AllowedAsync(user, company, PermissionCatalog.Codes.StocktakeView, wh.Value, ct)) return Denied<StocktakeView>();
        return await AllowedAsync(user, company, code, wh.Value, ct) ? await run() : Denied<StocktakeView>();
    }

    public Task<ConfigResult<StocktakeView>> UpdateAsync(Guid user, Guid company, Guid id, long? rev, StocktakeInput? input, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeUpdate, () => stocktakes.UpdateAsync(user, company, id, rev, input, ct), ct);

    public Task<ConfigResult<StocktakeView>> GenerateSheetAsync(Guid user, Guid company, Guid id, long? rev, StocktakeSheetInput? input, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeUpdate, () => stocktakes.GenerateSheetAsync(user, company, id, rev, input, ct), ct);

    public Task<ConfigResult<StocktakeView>> SubmitAsync(Guid user, Guid company, Guid id, long? rev, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeSubmit, () => stocktakes.SubmitAsync(user, company, id, rev, ct), ct);

    public Task<ConfigResult<StocktakeView>> WithdrawAsync(Guid user, Guid company, Guid id, long? rev, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeUpdate, () => stocktakes.WithdrawAsync(user, company, id, rev, ct), ct);

    public Task<ConfigResult<StocktakeView>> ApproveAsync(Guid user, Guid company, Guid id, long? rev, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeApprove, () => stocktakes.ApproveAsync(user, company, id, rev, ct), ct);

    public Task<ConfigResult<StocktakeView>> RejectAsync(Guid user, Guid company, Guid id, long? rev, string? note, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeApprove, () => stocktakes.RejectAsync(user, company, id, rev, note, ct), ct);

    public Task<ConfigResult<StocktakeView>> CancelAsync(Guid user, Guid company, Guid id, long? rev, string? reason, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StocktakeCancel, () => stocktakes.CancelAsync(user, company, id, rev, reason, ct), ct);
}
