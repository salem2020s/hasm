using System.Globalization;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using D = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using L = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentLine;
using E = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentEvent;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using IU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemUnit;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;

namespace Hasm.Infrastructure.Inventory;

/// <summary>
/// Stock movement documents (Phase 3.5): receipt, issue and warehouse-to-warehouse transfer, from draft to approval. <b>No authorization here by design:</b> <see cref="StockDocumentManagementService"/> wraps it.
/// <para><b>This service never changes a stock figure.</b> There is no quantity-on-hand, movement ledger, cost layer or accounting entry: a physical stock effect must be committed atomically with its valuation
/// (FIFO layers or moving weighted average per item and warehouse) and its accounting state, and those engines are a later slice. An approved document is therefore only an approved intention (official number,
/// fiscal year, approver) that the later posting slice will turn into movements; drafting, editing, submitting and approving never touch inventory.</para>
/// Every mutation runs in <see cref="CompanyAuditedTransactions"/> (change, history row and authenticated audit event commit or roll back together) after the same advisory lock that serialises warehouse structure
/// changes, so a warehouse can never be deactivated underneath a document being written. Transitions are fenced by the revision (a repeated or concurrent request fails and does nothing twice).
/// </summary>
public sealed class StockDocumentService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock, ILogger? logger = null)
{
    internal sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields)
            : status == ConfigStatus.Conflict && fields is { Count: > 0 } ? new ConfigResult<T>(status, code, default, fields, currentRevision)      // Phase 3.8B-3: a conflict that names its lines (the item refused, the line without a provisional cost)
            : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    internal static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, "validation_failed", [new FieldError(field, code)]);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static object DbN<T>(T? v) where T : struct => v is null ? DBNull.Value : v.Value;

    private async Task<ConfigResult<StockDocumentView>> Mutate(Guid companyId, Func<CompanyAuditedWork, Task<StockDocumentView>> work, CancellationToken ct)
    {
        try
        {
            return ConfigResult<StockDocumentView>.Ok(await transactions.ExecuteAsync(companyId, async w =>
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
        catch (Abort a) { return a.To<StockDocumentView>(); }
    }

    // ================================================================================================================ reads

    private const string Cols =
        "d.{0}, d.{1}, d.{2}, d.{3}, d.{4}, d.{5}, d.{6}, d.{7}, d.{8}, d.{9}, d.{10}, d.{11}, d.{12}, d.{13}, d.{14}, d.{15}, d.{16}, d.{17}, d.{18}, d.{19}, d.{20}, d.{21}, d.{22}";
    private static readonly string DocCols = string.Format(Cols, D.Id, D.DocType, D.Status, D.BranchId, D.SourceWarehouseId, D.TargetWarehouseId, D.DocumentDate, D.Reference, D.Notes, D.Number, D.FiscalYearId,
        D.CreatedBy, D.CreatedAtUtc, D.SubmittedBy, D.SubmittedAtUtc, D.DecidedBy, D.DecidedAtUtc, D.DecisionNote, D.CancelledBy, D.CancelledAtUtc, D.CancelReason, D.Revision, D.UpdatedAtUtc);

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static StockDocumentView ReadDoc(NpgsqlDataReader r, IReadOnlyList<StockLineView> lines, IReadOnlyList<StockEventView> events) => new(
        r.GetGuid(0), r.GetInt16(1), r.GetInt16(2), r.GetGuid(3), G(r, 4), G(r, 5), r.GetFieldValue<DateOnly>(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), S(r, 7), S(r, 8), S(r, 9), G(r, 10),
        r.GetGuid(11), r.GetFieldValue<DateTimeOffset>(12), G(r, 13), T(r, 14), G(r, 15), T(r, 16), S(r, 17), G(r, 18), T(r, 19), S(r, 20), r.GetInt64(21), r.GetFieldValue<DateTimeOffset>(22), lines, events);

    internal static async Task<StockDocumentView?> Load(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {DocCols} FROM {D.Table} d WHERE d.{D.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        // read the header first, then the children (a reader must be closed before the next command on the connection)
        StockDocumentView? header;
        await using (var r = await cmd.ExecuteReaderAsync(ct)) header = await r.ReadAsync(ct) ? ReadDoc(r, [], []) : null;
        if (header is null) return null;
        var lines = new List<StockLineView>();
        await using (var lc = new NpgsqlCommand(
            $"SELECT l.{L.LineNo}, l.{L.ItemId}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, l.{L.UnitId}, u.{U.Code}, l.{L.Quantity}, l.{L.Factor}, l.{L.BaseQuantity}, bu.{U.Code}, l.{L.Notes} " +
            $"FROM {L.Table} l JOIN {I.Table} i ON i.{I.Id} = l.{L.ItemId} JOIN {U.Table} u ON u.{U.Id} = l.{L.UnitId} JOIN {U.Table} bu ON bu.{U.Id} = i.{I.BaseUnitId} WHERE l.{L.DocumentId} = @id ORDER BY l.{L.LineNo}", conn, tx))
        {
            lc.Parameters.AddWithValue("id", id);
            await using var lr = await lc.ExecuteReaderAsync(ct);
            while (await lr.ReadAsync(ct))
                lines.Add(new StockLineView(lr.GetInt32(0), lr.GetGuid(1), lr.GetString(2), S(lr, 3), S(lr, 4), lr.GetGuid(5), lr.GetString(6), lr.GetDecimal(7), lr.GetDecimal(8), lr.GetDecimal(9), lr.GetString(10), S(lr, 11)));
        }
        var events = new List<StockEventView>();
        await using (var ec = new NpgsqlCommand($"SELECT {E.Id}, {E.Kind}, {E.FromStatus}, {E.ToStatus}, {E.ActorUserId}, {E.AtUtc}, {E.Note} FROM {E.Table} WHERE {E.DocumentId} = @id ORDER BY {E.Id}", conn, tx))
        {
            ec.Parameters.AddWithValue("id", id);
            await using var er = await ec.ExecuteReaderAsync(ct);
            while (await er.ReadAsync(ct)) events.Add(new StockEventView(er.GetInt64(0), er.GetInt16(1), er.IsDBNull(2) ? null : er.GetInt16(2), er.GetInt16(3), er.GetGuid(4), er.GetFieldValue<DateTimeOffset>(5), S(er, 6)));
        }
        return header with { Lines = lines, Events = events };
    }

    public async Task<StockDocumentView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await Load(conn, null, id, false, ct);
    }

    /// <summary>Filtered, paged list (newest first). <see cref="StockDocumentFilter.VisibleWarehouses"/> (null = all) restricts the result to documents that involve at least one of those warehouses; it is decided by the caller's permissions.</summary>
    public async Task<StockDocumentPage> ListAsync(Guid companyId, StockDocumentFilter f, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = new List<string>();
        await using var cmd = new NpgsqlCommand { Connection = conn };
        if (f.DocType is { } t) { where.Add($"d.{D.DocType} = @t"); cmd.Parameters.AddWithValue("t", (short)t); }
        if (f.Status is { } s) { where.Add($"d.{D.Status} = @s"); cmd.Parameters.AddWithValue("s", (short)s); }
        if (f.WarehouseId is { } w) { where.Add($"(d.{D.SourceWarehouseId} = @w OR d.{D.TargetWarehouseId} = @w)"); cmd.Parameters.AddWithValue("w", w); }
        if (DateOnly.TryParseExact(f.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) { where.Add($"d.{D.DocumentDate} >= @from"); cmd.Parameters.AddWithValue("from", from); }
        if (DateOnly.TryParseExact(f.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) { where.Add($"d.{D.DocumentDate} <= @to"); cmd.Parameters.AddWithValue("to", to); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            where.Add($"(upper(coalesce(d.{D.Number}, '')) LIKE @like OR upper(coalesce(d.{D.Reference}, '')) LIKE @like)");
            cmd.Parameters.AddWithValue("like", "%" + f.Search.Trim().ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        if (f.VisibleWarehouses is { } vis)
        {
            where.Add($"(d.{D.SourceWarehouseId} = ANY(@vis) OR d.{D.TargetWarehouseId} = ANY(@vis))");
            cmd.Parameters.Add(new NpgsqlParameter("vis", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = vis.ToArray() });
        }
        var wh = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"SELECT count(*) FROM {D.Table} d{wh}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        cmd.Parameters.AddWithValue("lim", Math.Clamp(f.Limit, 1, 200));
        cmd.Parameters.AddWithValue("off", Math.Max(0, f.Offset));
        cmd.CommandText = $"SELECT d.{D.Id}, d.{D.DocType}, d.{D.Status}, d.{D.Number}, d.{D.DocumentDate}, d.{D.BranchId}, d.{D.SourceWarehouseId}, d.{D.TargetWarehouseId}, d.{D.Reference}, " +
                          $"(SELECT count(*) FROM {L.Table} l WHERE l.{L.DocumentId} = d.{D.Id}), d.{D.Revision}, d.{D.CreatedAtUtc}, d.{D.UpdatedAtUtc} FROM {D.Table} d{wh} ORDER BY d.{D.CreatedAtUtc} DESC, d.{D.Id} LIMIT @lim OFFSET @off";
        var items = new List<StockDocumentSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new StockDocumentSummary(r.GetGuid(0), r.GetInt16(1), r.GetInt16(2), S(r, 3), r.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetGuid(5), G(r, 6), G(r, 7), S(r, 8),
                (int)r.GetInt64(9), r.GetInt64(10), r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<DateTimeOffset>(12)));
        return new StockDocumentPage(items, total);
    }

    // ================================================================================================================ validation against the real data

    internal sealed record WarehouseRow(Guid Id, Guid BranchId, bool IsActive, string Code);
    internal sealed record ItemRow(Guid Id, string Code, int Type, Guid BaseUnitId, bool Tracked, bool IsActive);

    internal static async Task<(Guid BranchId, string BranchCode)> ResolveWarehouses(CompanyAuditedWork work, StockDocumentValidation.Header h, CancellationToken ct)
    {
        var rows = new Dictionary<Guid, WarehouseRow>();
        var ids = new[] { h.Source, h.Target }.Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        await using (var q = new NpgsqlCommand($"SELECT {W.Id}, {W.BranchId}, {W.IsActive}, {W.Code} FROM {W.Table} WHERE {W.Id} = ANY(@ids) FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows[r.GetGuid(0)] = new WarehouseRow(r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2), r.GetString(3));
        }
        void Check(Guid? id, string field)
        {
            if (id is not { } v) return;
            if (!rows.TryGetValue(v, out var w)) throw Invalid(field, "warehouse_not_found");
            if (!w.IsActive) throw new Abort(ConfigStatus.Conflict, "warehouse_inactive");
        }
        Check(h.Source, "sourceWarehouseId");
        Check(h.Target, "targetWarehouseId");
        var branch = rows[(h.Source ?? h.Target)!.Value].BranchId;
        if (h.Source is not null && h.Target is not null && rows[h.Source.Value].BranchId != rows[h.Target.Value].BranchId) throw Invalid("targetWarehouseId", "transfer_cross_branch_not_supported");
        await using var bq = new NpgsqlCommand($"SELECT {B.Code}, {B.IsActive} FROM {B.Table} WHERE {B.Id} = @b FOR SHARE", work.Connection, work.Transaction);
        bq.Parameters.AddWithValue("b", branch);
        await using var br = await bq.ExecuteReaderAsync(ct);
        await br.ReadAsync(ct);
        if (!br.GetBoolean(1)) throw new Abort(ConfigStatus.Conflict, "branch_inactive");
        return (branch, br.GetString(0));
    }

    internal sealed record Resolved(Guid ItemId, Guid UnitId, decimal Quantity, decimal Factor, decimal Base, string? Notes);

    internal static async Task<List<Resolved>> ResolveLines(CompanyAuditedWork work, IReadOnlyList<StockDocumentValidation.Line> lines, CancellationToken ct)
    {
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToArray();
        var items = new Dictionary<Guid, ItemRow>();
        await using (var q = new NpgsqlCommand($"SELECT {I.Id}, {I.Code}, {I.ItemType}, {I.BaseUnitId}, ({I.IsLot} OR {I.IsSerial} OR {I.HasExpiry}), {I.IsActive} FROM {I.Table} WHERE {I.Id} = ANY(@ids) FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = itemIds });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items[r.GetGuid(0)] = new ItemRow(r.GetGuid(0), r.GetString(1), r.GetInt16(2), r.GetGuid(3), r.GetBoolean(4), r.GetBoolean(5));
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
        var result = new List<Resolved>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var f = $"lines[{i}]";
            if (!items.TryGetValue(l.ItemId, out var item)) throw Invalid(f + ".itemId", "item_not_found");
            if (!item.IsActive) throw Invalid(f + ".itemId", "item_inactive");
            if (item.Type != (int)ItemType.Goods) throw Invalid(f + ".itemId", "item_not_stockable");                    // a service has no stock
            // lot / serial / expiry tracking needs lot and serial identification on the line, which does not exist yet: such an item cannot be moved instead of being moved without its tracking
            if (item.Tracked) throw Invalid(f + ".itemId", "tracked_item_not_supported_yet");
            if (!units.TryGetValue(l.UnitId, out var unit)) throw Invalid(f + ".unitId", "unit_not_found");
            decimal factor;
            if (l.UnitId == item.BaseUnitId) factor = 1m;
            else if (factors.TryGetValue((l.ItemId, l.UnitId), out var fa)) factor = fa;
            else throw Invalid(f + ".unitId", "unit_not_defined_for_item");
            if (!unit.Active) throw Invalid(f + ".unitId", "unit_inactive");
            var baseDecimals = units[item.BaseUnitId].Decimals;
            decimal baseQty; string? err;
            try { (baseQty, err) = StockDocumentValidation.Convert(l.Quantity, factor, unit.Decimals, baseDecimals); }
            catch (OverflowException) { (baseQty, err) = (0, "invalid_quantity"); }
            if (err is not null) throw Invalid(f + ".quantity", err);
            result.Add(new Resolved(l.ItemId, l.UnitId, l.Quantity, factor, baseQty, l.Notes));
        }
        return result;
    }

    // ================================================================================================================ writes

    private async Task RecordAsync(CompanyAuditedWork work, StockEventKind kind, Guid actor, Guid docId, StockDocStatus? from, StockDocStatus to, string? note, string detail, CancellationToken ct)
    {
        var receipt = await work.AppendAuditAsync(new AuditEventData(StockAuditEvents.Of(kind), ActorUserId: actor, SubjectId: docId, Detail: detail), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {E.Table} ({E.DocumentId}, {E.Kind}, {E.FromStatus}, {E.ToStatus}, {E.ActorUserId}, {E.AtUtc}, {E.Note}, {E.AuditStream}, {E.AuditSeq}) VALUES (@d, @k, @f, @t, @a, @at, @n, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("d", docId);
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

    private static async Task WriteLines(CompanyAuditedWork work, Guid docId, IReadOnlyList<Resolved> lines, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand($"DELETE FROM {L.Table} WHERE {L.DocumentId} = @d", work.Connection, work.Transaction))
        {
            del.Parameters.AddWithValue("d", docId);
            await del.ExecuteNonQueryAsync(ct);
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            await using var ins = new NpgsqlCommand($"INSERT INTO {L.Table} ({L.DocumentId}, {L.LineNo}, {L.ItemId}, {L.UnitId}, {L.Quantity}, {L.Factor}, {L.BaseQuantity}, {L.Notes}) VALUES (@d, @n, @i, @u, @q, @f, @b, @t)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("d", docId); ins.Parameters.AddWithValue("n", i + 1); ins.Parameters.AddWithValue("i", l.ItemId); ins.Parameters.AddWithValue("u", l.UnitId);
            ins.Parameters.AddWithValue("q", l.Quantity); ins.Parameters.AddWithValue("f", l.Factor); ins.Parameters.AddWithValue("b", l.Base); ins.Parameters.AddWithValue("t", Db(l.Notes));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<ConfigResult<StockDocumentView>> CreateAsync(Guid actor, Guid companyId, StockDocumentInput? input, CancellationToken ct = default)
    {
        if (!StockDocumentValidation.TryNormalize(input, null, out var h, out var lines, out var errors)) return ConfigResult<StockDocumentView>.Invalid(errors);
        return await Mutate(companyId, async work =>
        {
            var (branch, _) = await ResolveWarehouses(work, h, ct);
            var resolved = await ResolveLines(work, lines, ct);
            var id = Guid.NewGuid();
            await using (var ins = new NpgsqlCommand(
                $"INSERT INTO {D.Table} ({D.Id}, {D.DocType}, {D.Status}, {D.BranchId}, {D.SourceWarehouseId}, {D.TargetWarehouseId}, {D.DocumentDate}, {D.Reference}, {D.Notes}, {D.CreatedBy}, {D.CreatedAtUtc}, {D.Revision}, {D.UpdatedAtUtc}) " +
                "VALUES (@id, @t, 1, @b, @s, @tg, @date, @ref, @notes, @a, @now, 1, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("t", (short)h.Type); ins.Parameters.AddWithValue("b", branch);
                ins.Parameters.AddWithValue("s", DbN(h.Source)); ins.Parameters.AddWithValue("tg", DbN(h.Target)); ins.Parameters.AddWithValue("date", h.Date);
                ins.Parameters.AddWithValue("ref", Db(h.Reference)); ins.Parameters.AddWithValue("notes", Db(h.Notes)); ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await WriteLines(work, id, resolved, ct);
            await RecordAsync(work, StockEventKind.Created, actor, id, null, StockDocStatus.Draft, null, $"stock document created type={(int)h.Type} lines={resolved.Count}", ct);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    private static async Task<StockDocumentView> Require(CompanyAuditedWork work, Guid id, long? expectedRevision, StockDocStatus? mustBe, CancellationToken ct)
    {
        var cur = await Load(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "document_not_found");
        if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
        if (mustBe is { } m && cur.Status != (int)m) throw new Abort(ConfigStatus.Conflict, "invalid_status");
        return cur;
    }

    private static async Task SetStatus(CompanyAuditedWork work, Guid id, StockDocStatus status, DateTime now, string? extraSet, Action<NpgsqlCommand>? bind, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand($"UPDATE {D.Table} SET {D.Status} = @st, {D.Revision} = {D.Revision} + 1, {D.UpdatedAtUtc} = @now{(extraSet is null ? "" : ", " + extraSet)} WHERE {D.Id} = @id", work.Connection, work.Transaction);
        upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("st", (short)status); upd.Parameters.AddWithValue("now", now);
        bind?.Invoke(upd);
        await upd.ExecuteNonQueryAsync(ct);
    }

    private static StockDocumentValidation.Header HeaderOf(StockDocumentView d) => new((StockDocType)d.DocType, d.SourceWarehouseId, d.TargetWarehouseId, DateOnly.ParseExact(d.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), d.Reference, d.Notes);
    private static List<StockDocumentValidation.Line> LinesOf(StockDocumentView d) => [.. d.Lines.Select(l => new StockDocumentValidation.Line(l.ItemId, l.UnitId, l.Quantity, l.Notes))];

    public Task<ConfigResult<StockDocumentView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, StockDocumentInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<StockDocumentView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await Require(work, id, expectedRevision, StockDocStatus.Draft, ct);
            if (!StockDocumentValidation.TryNormalize(input, (StockDocType)cur.DocType, out var h, out var lines, out var errors)) throw new Abort(ConfigStatus.Invalid, "validation_failed", errors);
            await ResolveWarehouses(work, h, ct);
            var resolved = await ResolveLines(work, lines, ct);
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {D.Table} SET {D.SourceWarehouseId} = @s, {D.TargetWarehouseId} = @tg, {D.DocumentDate} = @date, {D.Reference} = @ref, {D.Notes} = @notes, {D.Revision} = {D.Revision} + 1, {D.UpdatedAtUtc} = @now WHERE {D.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("s", DbN(h.Source)); upd.Parameters.AddWithValue("tg", DbN(h.Target)); upd.Parameters.AddWithValue("date", h.Date);
                upd.Parameters.AddWithValue("ref", Db(h.Reference)); upd.Parameters.AddWithValue("notes", Db(h.Notes)); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await WriteLines(work, id, resolved, ct);
            await RecordAsync(work, StockEventKind.Updated, actor, id, StockDocStatus.Draft, StockDocStatus.Draft, null, $"stock document updated r{cur.Revision + 1} lines={resolved.Count}", ct);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>Draft to submitted. The references are validated again against the CURRENT data (an item or warehouse may have been deactivated since the draft was saved).</summary>
    public Task<ConfigResult<StockDocumentView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, StockDocStatus.Draft, async (work, cur) =>
        {
            await ResolveWarehouses(work, HeaderOf(cur), ct);
            await ResolveLines(work, LinesOf(cur), ct);
            await SetStatus(work, id, StockDocStatus.Submitted, Now, $"{D.SubmittedBy} = @who, {D.SubmittedAtUtc} = @now", c => c.Parameters.AddWithValue("who", actor), ct);
            await RecordAsync(work, StockEventKind.Submitted, actor, id, StockDocStatus.Draft, StockDocStatus.Submitted, null, "stock document submitted for approval", ct);
        }, ct);

    /// <summary>Submitted back to draft (the submitter or an editor takes it back, for example to correct it); no decision exists yet.</summary>
    public Task<ConfigResult<StockDocumentView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, StockDocStatus.Submitted, async (work, cur) =>
        {
            await SetStatus(work, id, StockDocStatus.Draft, Now, $"{D.SubmittedBy} = NULL, {D.SubmittedAtUtc} = NULL", null, ct);
            await RecordAsync(work, StockEventKind.Withdrawn, actor, id, StockDocStatus.Submitted, StockDocStatus.Draft, null, "stock document withdrawn to draft", ct);
        }, ct);

    /// <summary>
    /// Submitted to approved by someone other than the submitter. Validates the references again, requires the document date to lie in an OPEN fiscal period and issues the official number (per type, branch
    /// and fiscal year as configured) in the same transaction, so a rollback leaves no gap. Approval records the decision only: it creates no movement, quantity or accounting entry.
    /// </summary>
    public Task<ConfigResult<StockDocumentView>> ApproveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, StockDocStatus.Submitted, async (work, cur) =>
        {
            if (cur.SubmittedBy == actor) throw new Abort(ConfigStatus.Conflict, "self_approval_not_allowed");
            var header = HeaderOf(cur);
            var (_, branchCode) = await ResolveWarehouses(work, header, ct);
            await ResolveLines(work, LinesOf(cur), ct);
            var check = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, header.Date, false, ct);
            if (!check.Allowed) throw new Abort(ConfigStatus.Conflict, check.Reason!);
            Guid yearId; int yearLabel;
            await using (var q = new NpgsqlCommand($"SELECT y.{FY.Id}, EXTRACT(YEAR FROM y.{FY.StartDate})::int FROM {CompanySchema.FiscalPeriod.Table} p JOIN {FY.Table} y ON y.{FY.Id} = p.{CompanySchema.FiscalPeriod.YearId} WHERE p.{CompanySchema.FiscalPeriod.Id} = @p", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("p", check.PeriodId!.Value);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                yearId = r.GetGuid(0); yearLabel = r.GetInt32(1);
            }
            var docType = DocTypeOf((StockDocType)cur.DocType);
            var number = await NumberingService.IssueOrRefuseAsync(() => NumberingService.IssueAsync(work.Connection, work.Transaction, docType, new NumberScope(cur.BranchId, branchCode, yearId, yearLabel.ToString(CultureInfo.InvariantCulture)), id, actor, Now, ct),
                docType, logger, () => new Abort(ConfigStatus.Conflict, NumberingService.NumberAlreadyIssued));
            await SetStatus(work, id, StockDocStatus.Approved, Now, $"{D.Number} = @num, {D.FiscalYearId} = @fy, {D.DecidedBy} = @who, {D.DecidedAtUtc} = @now", c => { c.Parameters.AddWithValue("num", number.Number); c.Parameters.AddWithValue("fy", yearId); c.Parameters.AddWithValue("who", actor); }, ct);
            await RecordAsync(work, StockEventKind.Approved, actor, id, StockDocStatus.Submitted, StockDocStatus.Approved, null, $"stock document approved number={number.Number}", ct);
        }, ct);

    public Task<ConfigResult<StockDocumentView>> RejectAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? note, CancellationToken ct = default)
    {
        var n = note?.Trim();
        if (string.IsNullOrEmpty(n)) return Task.FromResult(ConfigResult<StockDocumentView>.Invalid([new("note", "required")]));
        if (n.Length > 500) return Task.FromResult(ConfigResult<StockDocumentView>.Invalid([new("note", "too_long")]));
        return Transition(companyId, id, expectedRevision, StockDocStatus.Submitted, async (work, cur) =>
        {
            if (cur.SubmittedBy == actor) throw new Abort(ConfigStatus.Conflict, "self_approval_not_allowed");
            await SetStatus(work, id, StockDocStatus.Rejected, Now, $"{D.DecidedBy} = @who, {D.DecidedAtUtc} = @now, {D.DecisionNote} = @note", c => { c.Parameters.AddWithValue("who", actor); c.Parameters.AddWithValue("note", n); }, ct);
            await RecordAsync(work, StockEventKind.Rejected, actor, id, StockDocStatus.Submitted, StockDocStatus.Rejected, n, "stock document rejected", ct);
        }, ct);
    }

    /// <summary>Draft, submitted or approved to cancelled, with a mandatory reason. An approved document keeps its official number, which is marked void and is never issued again. (A later slice refuses cancelling a posted document.)</summary>
    public Task<ConfigResult<StockDocumentView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r)) return Task.FromResult(ConfigResult<StockDocumentView>.Invalid([new("reason", "required")]));
        if (r.Length > 500) return Task.FromResult(ConfigResult<StockDocumentView>.Invalid([new("reason", "too_long")]));
        return Transition(companyId, id, expectedRevision, null, async (work, cur) =>
        {
            if (cur.Status is not (1 or 2 or 3)) throw new Abort(ConfigStatus.Conflict, "invalid_status");
            // an executed document has moved stock and carries its valuation and accounting state: it is undone only by a reversal, never by cancelling (PostgreSQL enforces this too)
            await using (var ex = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {CompanySchema.StockPosting.Table} WHERE {CompanySchema.StockPosting.DocumentId} = @d)", work.Connection, work.Transaction))
            {
                ex.Parameters.AddWithValue("d", id);
                if ((bool)(await ex.ExecuteScalarAsync(ct))!) throw new Abort(ConfigStatus.Conflict, "document_executed");
            }
            if (cur.Status == 3 && cur.Number is { } number)
                await NumberingService.VoidAsync(work.Connection, work.Transaction, DocTypeOf((StockDocType)cur.DocType), number, actor, r.Length > 200 ? r[..200] : r, Now, ct);
            await SetStatus(work, id, StockDocStatus.Cancelled, Now, $"{D.CancelledBy} = @who, {D.CancelledAtUtc} = @now, {D.CancelReason} = @why", c => { c.Parameters.AddWithValue("who", actor); c.Parameters.AddWithValue("why", r); }, ct);
            await RecordAsync(work, StockEventKind.Cancelled, actor, id, (StockDocStatus)cur.Status, StockDocStatus.Cancelled, r, "stock document cancelled", ct);
        }, ct);
    }

    private Task<ConfigResult<StockDocumentView>> Transition(Guid companyId, Guid id, long? expectedRevision, StockDocStatus? from, Func<CompanyAuditedWork, StockDocumentView, Task> body, CancellationToken ct)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<StockDocumentView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await Require(work, id, expectedRevision, from, ct);
            await body(work, cur);
            return (await Load(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public static string DocTypeOf(StockDocType t) => t switch { StockDocType.Receipt => DocumentTypes.StockReceipt, StockDocType.Issue => DocumentTypes.StockIssue, _ => DocumentTypes.StockTransfer };

    /// <summary>Warehouses of the company for the capability map and the editor's pickers.</summary>
    public async Task<IReadOnlyList<StockWarehouseRef>> ListWarehousesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {W.Id}, {W.BranchId}, {W.Code}, {W.NameAr}, {W.NameEn}, {W.IsActive} FROM {W.Table} ORDER BY {W.Code}", conn);
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<StockWarehouseRef>();
        while (await r.ReadAsync(ct)) list.Add(new StockWarehouseRef(r.GetGuid(0), r.GetGuid(1), r.GetString(2), S(r, 3), S(r, 4), r.GetBoolean(5)));
        return list;
    }

    /// <summary>Items a document line may use (see <see cref="StockItemRef"/>), found by code, name or exact barcode; at most 25.</summary>
    public async Task<IReadOnlyList<StockItemRef>> SearchItemsAsync(Guid companyId, string? search, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var items = new List<(Guid Id, string Code, string? Ar, string? En, Guid BaseUnit)>();
        await using (var q = new NpgsqlCommand(
            $"SELECT i.{I.Id}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, i.{I.BaseUnitId} FROM {I.Table} i WHERE i.{I.IsActive} AND i.{I.ItemType} = 1 AND NOT (i.{I.IsLot} OR i.{I.IsSerial} OR i.{I.HasExpiry}) " +
            $"AND (@s = '' OR upper(i.{I.Code}) LIKE @like OR upper(coalesce(i.{I.NameAr}, '')) LIKE @like OR upper(coalesce(i.{I.NameEn}, '')) LIKE @like OR EXISTS (SELECT 1 FROM {CompanySchema.ItemBarcode.Table} b WHERE b.{CompanySchema.ItemBarcode.ItemId} = i.{I.Id} AND upper(b.{CompanySchema.ItemBarcode.Barcode}) = @exact)) " +
            $"ORDER BY i.{I.Code} LIMIT 25", conn))
        {
            var term = (search ?? "").Trim().ToUpperInvariant();
            q.Parameters.AddWithValue("s", term);
            q.Parameters.AddWithValue("like", "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            q.Parameters.AddWithValue("exact", term);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add((r.GetGuid(0), r.GetString(1), S(r, 2), S(r, 3), r.GetGuid(4)));
        }
        if (items.Count == 0) return [];
        var units = new Dictionary<Guid, (string Code, string? Ar, string? En, int Decimals)>();
        await using (var q = new NpgsqlCommand($"SELECT {U.Id}, {U.Code}, {U.NameAr}, {U.NameEn}, {U.Decimals} FROM {U.Table} WHERE {U.IsActive}", conn))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) units[r.GetGuid(0)] = (r.GetString(1), S(r, 2), S(r, 3), r.GetInt16(4));
        var alt = new Dictionary<Guid, List<(Guid Unit, decimal Factor)>>();
        await using (var q = new NpgsqlCommand($"SELECT {IU.ItemId}, {IU.UnitId}, {IU.Factor} FROM {IU.Table} WHERE {IU.ItemId} = ANY(@ids)", conn))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = items.Select(x => x.Id).ToArray() });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { if (!alt.TryGetValue(r.GetGuid(0), out var l)) alt[r.GetGuid(0)] = l = []; l.Add((r.GetGuid(1), r.GetDecimal(2))); }
        }
        var result = new List<StockItemRef>();
        foreach (var it in items)
        {
            var list = new List<StockUnitRef>();
            if (units.TryGetValue(it.BaseUnit, out var bu)) list.Add(new StockUnitRef(it.BaseUnit, bu.Code, bu.Ar, bu.En, bu.Decimals, 1m, true));
            foreach (var (uid, f) in alt.GetValueOrDefault(it.Id) ?? []) if (units.TryGetValue(uid, out var au)) list.Add(new StockUnitRef(uid, au.Code, au.Ar, au.En, au.Decimals, f, false));
            if (list.Count > 0) result.Add(new StockItemRef(it.Id, it.Code, it.Ar, it.En, list));
        }
        return result;
    }
}
