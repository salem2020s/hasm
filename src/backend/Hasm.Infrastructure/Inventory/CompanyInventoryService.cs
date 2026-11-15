using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemGroup;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using IU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemUnit;
using BC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemBarcode;
using S = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventorySettings;

namespace Hasm.Infrastructure.Inventory;

/// <summary>
/// Inventory master-data operations (Phase 3.4). <b>No authorization here by design:</b> <see cref="InventoryManagementService"/> wraps it. Every mutation runs in
/// <see cref="CompanyAuditedTransactions"/> (the change and its authenticated audit event commit or roll back together) after one transaction-level advisory lock that
/// serialises inventory structure changes (group tree, units of an item, settings), then row locks. This phase creates NO stock movement and NO accounting entry; it
/// only records master data and the valuation POLICY (the costing engine is a later phase).
/// </summary>
public sealed class CompanyInventoryService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48494E56;   // "HINV": advisory-lock namespace of inventory master data
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    private sealed class Unchanged(object value) : Exception("unchanged") { public object Value { get; } = value; }

    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, "validation_failed", [new FieldError(field, code)]);

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try
        {
            return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, async w =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", w.Connection, w.Transaction))
                {
                    lk.Parameters.AddWithValue("a", LockKey1);
                    lk.Parameters.AddWithValue("b", LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                return await work(w);
            }, ct));
        }
        catch (Abort a) { return a.To<T>(); }
        catch (Unchanged u) { return ConfigResult<T>.Ok((T)u.Value); }
    }

    private static object Db<T>(T? value) where T : class => (object?)value ?? DBNull.Value;
    private static object DbN<T>(T? value) where T : struct => value is null ? DBNull.Value : value.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task Audit(CompanyAuditedWork work, short type, Guid actor, Guid subject, string detail, CancellationToken ct) =>
        await work.AppendAuditAsync(new AuditEventData(type, ActorUserId: actor, SubjectId: subject, Detail: detail), ct);


    // ================================================================================================================ settings

    public async Task<InventorySettingsView> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadSettings(conn, null, ct);
    }

    private static async Task<InventorySettingsView> ReadSettings(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {S.DefaultValuation}, {S.Revision} FROM {S.Table}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new InventorySettingsView(r.IsDBNull(0) ? null : r.GetInt16(0), r.GetInt64(1)) : new InventorySettingsView(null, 0);
    }

    /// <summary>Sets the company default valuation method (FIFO / moving average); null clears it. <paramref name="expectedRevision"/> is 0 while no settings row exists.</summary>
    public Task<ConfigResult<InventorySettingsView>> SaveSettingsAsync(Guid actor, Guid companyId, long? expectedRevision, InventorySettingsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<InventorySettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        if (input is null) return Task.FromResult(ConfigResult<InventorySettingsView>.Invalid([new("body", "required")]));
        if (input.DefaultValuation is not null and not (1 or 2)) return Task.FromResult(ConfigResult<InventorySettingsView>.Invalid([new("defaultValuation", "invalid_method")]));
        return Mutate(companyId, async work =>
        {
            var current = await ReadSettings(work.Connection, work.Transaction, ct);
            if (current.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, current.Revision);
            if (current.Revision == 0)
            {
                await using var ins = new NpgsqlCommand($"INSERT INTO {S.Table} ({S.Id}, {S.DefaultValuation}, {S.NextItemNumber}, {S.Revision}, {S.UpdatedAtUtc}) VALUES (1, @v, 1, 1, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("v", DbN((short?)input.DefaultValuation));
                ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            else
            {
                if (current.DefaultValuation == input.DefaultValuation) throw new Unchanged(current);   // nothing to change, nothing audited
                await using var upd = new NpgsqlCommand($"UPDATE {S.Table} SET {S.DefaultValuation} = @v, {S.Revision} = {S.Revision} + 1, {S.UpdatedAtUtc} = @now WHERE {S.Id} = 1", work.Connection, work.Transaction);
                upd.Parameters.AddWithValue("v", DbN((short?)input.DefaultValuation));
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await Audit(work, InventoryAuditEvents.SettingsChanged, actor, companyId, $"inventory settings default valuation {current.DefaultValuation?.ToString() ?? "none"} -> {input.DefaultValuation?.ToString() ?? "none"}", ct);
            return await ReadSettings(work.Connection, work.Transaction, ct);
        }, ct);
    }

    // ================================================================================================================ units

    private static readonly string UnitCols = $"{U.Id}, {U.Code}, {U.NameAr}, {U.NameEn}, {U.Decimals}, {U.IsActive}, {U.Revision}, {U.CreatedAtUtc}, {U.UpdatedAtUtc}";

    private static UnitView ReadUnit(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt16(4), r.GetBoolean(5), r.GetInt64(6),
        r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8));

    public async Task<IReadOnlyList<UnitView>> ListUnitsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {UnitCols} FROM {U.Table} ORDER BY {U.Code}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<UnitView>();
        while (await r.ReadAsync(ct)) list.Add(ReadUnit(r));
        return list;
    }

    private static async Task<UnitView?> LoadUnit(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {UnitCols} FROM {U.Table} WHERE {U.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUnit(r) : null;
    }

    public async Task<UnitView?> GetUnitAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadUnit(conn, null, id, false, ct);
    }

    public async Task<ConfigResult<UnitView>> CreateUnitAsync(Guid actor, Guid companyId, UnitInput? input, CancellationToken ct = default)
    {
        if (!InventoryValidation.TryNormalizeUnit(input, null, out var d, out var errors)) return ConfigResult<UnitView>.Invalid(errors);
        return await Mutate(companyId, async work =>
        {
            var id = Guid.NewGuid();
            try
            {
                await using var ins = new NpgsqlCommand($"INSERT INTO {U.Table} ({U.Id}, {U.Code}, {U.NameAr}, {U.NameEn}, {U.Decimals}, {U.IsActive}, {U.Revision}, {U.CreatedAtUtc}, {U.UpdatedAtUtc}) VALUES (@id, @c, @ar, @en, @d, true, 1, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("c", d.Code);
                ins.Parameters.AddWithValue("ar", Db(d.NameAr));
                ins.Parameters.AddWithValue("en", Db(d.NameEn));
                ins.Parameters.AddWithValue("d", (short)d.Decimals);
                ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "unit_code_taken"); }
            await Audit(work, InventoryAuditEvents.UnitCreated, actor, id, $"unit {d.Code} created decimals={d.Decimals}", ct);
            return (await LoadUnit(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public async Task<ConfigResult<UnitView>> UpdateUnitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, UnitInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<UnitView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await Mutate(companyId, async work =>
        {
            var cur = await LoadUnit(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "unit_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (!InventoryValidation.TryNormalizeUnit(input, cur, out var d, out var errors)) throw new Abort(ConfigStatus.Invalid, "validation_failed", errors);
            if (d.NameAr == cur.NameAr && d.NameEn == cur.NameEn) throw new Unchanged(cur);
            await using (var upd = new NpgsqlCommand($"UPDATE {U.Table} SET {U.NameAr} = @ar, {U.NameEn} = @en, {U.Revision} = {U.Revision} + 1, {U.UpdatedAtUtc} = @now WHERE {U.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("ar", Db(d.NameAr));
                upd.Parameters.AddWithValue("en", Db(d.NameEn));
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await Audit(work, InventoryAuditEvents.UnitUpdated, actor, id, $"unit {cur.Code} updated r{cur.Revision + 1}", ct);
            return (await LoadUnit(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public Task<ConfigResult<UnitView>> SetUnitActiveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool active, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<UnitView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await LoadUnit(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "unit_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.IsActive == active) throw new Abort(ConfigStatus.Conflict, active ? "unit_already_active" : "unit_already_inactive");
            await using (var upd = new NpgsqlCommand($"UPDATE {U.Table} SET {U.IsActive} = @a, {U.Revision} = {U.Revision} + 1, {U.UpdatedAtUtc} = @now WHERE {U.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("a", active);
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await Audit(work, InventoryAuditEvents.UnitActiveChanged, actor, id, $"unit {cur.Code} {(active ? "activated" : "deactivated")}", ct);
            return (await LoadUnit(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    // ================================================================================================================ item groups

    private static readonly string GroupCols = $"{G.Id}, {G.Code}, {G.NameAr}, {G.NameEn}, {G.ParentId}, {G.Valuation}, {G.IsActive}, {G.Revision}, {G.CreatedAtUtc}, {G.UpdatedAtUtc}";

    private sealed record RawGroup(Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId, int? Valuation, bool IsActive, long Revision, DateTimeOffset Created, DateTimeOffset Updated);

    private static RawGroup ReadGroup(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetInt16(5), r.GetBoolean(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9));

    private static async Task<List<RawGroup>> LoadGroups(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {GroupCols} FROM {G.Table} ORDER BY {G.Code}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RawGroup>();
        while (await r.ReadAsync(ct)) list.Add(ReadGroup(r));
        return list;
    }

    /// <summary>The valuation method of a group: its own, else the nearest ancestor's, else the company default (null while nobody decided).</summary>
    private static int? Effective(IReadOnlyDictionary<Guid, RawGroup> byId, Guid groupId, int? companyDefault)
    {
        var chain = new List<ValuationMethod?>();
        var seen = new HashSet<Guid>();
        for (Guid? cur = groupId; cur is { } id && byId.TryGetValue(id, out var g) && seen.Add(id); cur = g.ParentId)
            chain.Add(g.Valuation is { } v ? (ValuationMethod)v : null);
        return (int?)InventoryValidation.ResolveValuation(chain, companyDefault is { } d ? (ValuationMethod)d : null);
    }

    private static ItemGroupView ToView(RawGroup g, IReadOnlyDictionary<Guid, RawGroup> byId, int? companyDefault) =>
        new(g.Id, g.Code, g.NameAr, g.NameEn, g.ParentId, g.Valuation, Effective(byId, g.Id, companyDefault), g.IsActive, g.Revision, g.Created, g.Updated);

    public async Task<IReadOnlyList<ItemGroupView>> ListGroupsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var groups = await LoadGroups(conn, null, ct);
        var byId = groups.ToDictionary(g => g.Id);
        var def = (await ReadSettings(conn, null, ct)).DefaultValuation;
        return [.. groups.Select(g => ToView(g, byId, def))];
    }

    public async Task<ItemGroupView?> GetGroupAsync(Guid companyId, Guid id, CancellationToken ct = default) => (await ListGroupsAsync(companyId, ct)).FirstOrDefault(g => g.Id == id);

    private async Task<ItemGroupView> ViewGroup(CompanyAuditedWork work, Guid id, CancellationToken ct)
    {
        var groups = await LoadGroups(work.Connection, work.Transaction, ct);
        var def = (await ReadSettings(work.Connection, work.Transaction, ct)).DefaultValuation;
        return ToView(groups.First(g => g.Id == id), groups.ToDictionary(g => g.Id), def);
    }

    private static void RequireActiveParent(IReadOnlyDictionary<Guid, RawGroup> byId, Guid? parentId)
    {
        if (parentId is not { } p) return;
        if (!byId.TryGetValue(p, out var parent)) throw Invalid("parentId", "parent_not_found");
        if (!parent.IsActive) throw new Abort(ConfigStatus.Conflict, "parent_group_inactive");
    }

    public async Task<ConfigResult<ItemGroupView>> CreateGroupAsync(Guid actor, Guid companyId, ItemGroupInput? input, CancellationToken ct = default)
    {
        if (!InventoryValidation.TryNormalizeGroup(input, null, out var d, out var errors)) return ConfigResult<ItemGroupView>.Invalid(errors);
        return await Mutate(companyId, async work =>
        {
            RequireActiveParent((await LoadGroups(work.Connection, work.Transaction, ct)).ToDictionary(g => g.Id), d.ParentId);
            var id = Guid.NewGuid();
            try
            {
                await using var ins = new NpgsqlCommand($"INSERT INTO {G.Table} ({G.Id}, {G.Code}, {G.NameAr}, {G.NameEn}, {G.ParentId}, {G.Valuation}, {G.IsActive}, {G.Revision}, {G.CreatedAtUtc}, {G.UpdatedAtUtc}) VALUES (@id, @c, @ar, @en, @p, @v, true, 1, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("c", d.Code);
                ins.Parameters.AddWithValue("ar", Db(d.NameAr));
                ins.Parameters.AddWithValue("en", Db(d.NameEn));
                ins.Parameters.AddWithValue("p", DbN(d.ParentId));
                ins.Parameters.AddWithValue("v", DbN(d.Valuation is { } v ? (short)v : (short?)null));
                ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "group_code_taken"); }
            catch (PostgresException ex) when (ex.MessageText.Contains("8 levels", StringComparison.Ordinal)) { throw Invalid("parentId", "tree_too_deep"); }
            await Audit(work, InventoryAuditEvents.ItemGroupCreated, actor, id, $"item group {d.Code} created valuation={(d.Valuation is { } m ? (int)m : 0)}", ct);
            return await ViewGroup(work, id, ct);
        }, ct);
    }

    public async Task<ConfigResult<ItemGroupView>> UpdateGroupAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, ItemGroupInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ItemGroupView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await Mutate(companyId, async work =>
        {
            var groups = await LoadGroups(work.Connection, work.Transaction, ct);
            var byId = groups.ToDictionary(g => g.Id);
            var cur = byId.GetValueOrDefault(id) ?? throw new Abort(ConfigStatus.NotFound, "group_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (!InventoryValidation.TryNormalizeGroup(input, cur.Code, out var d, out var errors)) throw new Abort(ConfigStatus.Invalid, "validation_failed", errors);
            if (d.ParentId != cur.ParentId) RequireActiveParent(byId, d.ParentId);
            var newValuation = d.Valuation is { } v ? (int)v : (int?)null;
            if (d.NameAr == cur.NameAr && d.NameEn == cur.NameEn && d.ParentId == cur.ParentId && newValuation == cur.Valuation) throw new Unchanged(ToView(cur, byId, (await ReadSettings(work.Connection, work.Transaction, ct)).DefaultValuation));
            try
            {
                await using var upd = new NpgsqlCommand($"UPDATE {G.Table} SET {G.NameAr} = @ar, {G.NameEn} = @en, {G.ParentId} = @p, {G.Valuation} = @v, {G.Revision} = {G.Revision} + 1, {G.UpdatedAtUtc} = @now WHERE {G.Id} = @id", work.Connection, work.Transaction);
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("ar", Db(d.NameAr));
                upd.Parameters.AddWithValue("en", Db(d.NameEn));
                upd.Parameters.AddWithValue("p", DbN(d.ParentId));
                upd.Parameters.AddWithValue("v", DbN(newValuation is { } nv ? (short)nv : (short?)null));
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.MessageText.Contains("ancestor", StringComparison.Ordinal) || ex.MessageText.Contains("8 levels", StringComparison.Ordinal))
            { throw Invalid("parentId", ex.MessageText.Contains("8 levels", StringComparison.Ordinal) ? "tree_too_deep" : "cycle"); }
            await Audit(work, InventoryAuditEvents.ItemGroupUpdated, actor, id, $"item group {cur.Code} updated r{cur.Revision + 1} valuation {cur.Valuation?.ToString() ?? "inherit"} -> {newValuation?.ToString() ?? "inherit"}", ct);
            return await ViewGroup(work, id, ct);
        }, ct);
    }

    public Task<ConfigResult<ItemGroupView>> SetGroupActiveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool active, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<ItemGroupView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var groups = await LoadGroups(work.Connection, work.Transaction, ct);
            var byId = groups.ToDictionary(g => g.Id);
            var cur = byId.GetValueOrDefault(id) ?? throw new Abort(ConfigStatus.NotFound, "group_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.IsActive == active) throw new Abort(ConfigStatus.Conflict, active ? "group_already_active" : "group_already_inactive");
            if (active) RequireActiveParent(byId, cur.ParentId);
            else
            {
                if (groups.Any(g => g.ParentId == id && g.IsActive)) throw new Abort(ConfigStatus.Conflict, "group_has_active_children");
                await using var q = new NpgsqlCommand($"SELECT 1 FROM {I.Table} WHERE {I.GroupId} = @g AND {I.IsActive} LIMIT 1", work.Connection, work.Transaction);
                q.Parameters.AddWithValue("g", id);
                if (await q.ExecuteScalarAsync(ct) is not null) throw new Abort(ConfigStatus.Conflict, "group_has_active_items");
            }
            await using (var upd = new NpgsqlCommand($"UPDATE {G.Table} SET {G.IsActive} = @a, {G.Revision} = {G.Revision} + 1, {G.UpdatedAtUtc} = @now WHERE {G.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("a", active);
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await Audit(work, InventoryAuditEvents.ItemGroupActiveChanged, actor, id, $"item group {cur.Code} {(active ? "activated" : "deactivated")}", ct);
            return await ViewGroup(work, id, ct);
        }, ct);
    }

    // ================================================================================================================ items

    private static readonly string ItemCols =
        $"{I.Id}, {I.Code}, {I.NameAr}, {I.NameEn}, {I.ItemType}, {I.GroupId}, {I.BaseUnitId}, {I.PurchaseUnitId}, {I.SalesUnitId}, {I.IsLot}, {I.IsSerial}, {I.HasExpiry}, {I.Notes}, {I.IsActive}, {I.Revision}, {I.CreatedAtUtc}, {I.UpdatedAtUtc}";

    private sealed record RawItem(Guid Id, string Code, string? NameAr, string? NameEn, int Type, Guid GroupId, Guid BaseUnitId, Guid? PurchaseUnitId, Guid? SalesUnitId, bool Lot, bool Serial, bool Expiry,
        string? Notes, bool IsActive, long Revision, DateTimeOffset Created, DateTimeOffset Updated);

    private static RawItem ReadItem(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt16(4), r.GetGuid(5), r.GetGuid(6),
        r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.GetBoolean(9), r.GetBoolean(10), r.GetBoolean(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetBoolean(13), r.GetInt64(14),
        r.GetFieldValue<DateTimeOffset>(15), r.GetFieldValue<DateTimeOffset>(16));

    private static async Task<Dictionary<Guid, List<ItemUnitView>>> LoadAlternates(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<Guid>? itemIds, CancellationToken ct)
    {
        var sql = $"SELECT iu.{IU.ItemId}, iu.{IU.UnitId}, u.{U.Code}, iu.{IU.Factor} FROM {IU.Table} iu JOIN {U.Table} u ON u.{U.Id} = iu.{IU.UnitId}" + (itemIds is null ? "" : $" WHERE iu.{IU.ItemId} = ANY(@ids)") + $" ORDER BY u.{U.Code}";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        if (itemIds is not null) cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = itemIds.ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var map = new Dictionary<Guid, List<ItemUnitView>>();
        while (await r.ReadAsync(ct))
        {
            if (!map.TryGetValue(r.GetGuid(0), out var l)) map[r.GetGuid(0)] = l = [];
            l.Add(new ItemUnitView(r.GetGuid(1), r.GetString(2), r.GetDecimal(3)));
        }
        return map;
    }

    private static ItemView ToView(RawItem i, IReadOnlyDictionary<Guid, List<ItemUnitView>> alt, IReadOnlyDictionary<Guid, RawGroup> groups, int? companyDefault) =>
        new(i.Id, i.Code, i.NameAr, i.NameEn, i.Type, i.GroupId, i.BaseUnitId, i.PurchaseUnitId, i.SalesUnitId, i.Lot, i.Serial, i.Expiry, i.Notes, i.IsActive, i.Revision,
            i.Type == (int)ItemType.Goods ? Effective(groups, i.GroupId, companyDefault) : null, alt.GetValueOrDefault(i.Id) ?? [], i.Created, i.Updated);

    private async Task<ItemView?> LoadItem(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        RawItem? raw;
        await using (var cmd = new NpgsqlCommand($"SELECT {ItemCols} FROM {I.Table} WHERE {I.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            raw = await r.ReadAsync(ct) ? ReadItem(r) : null;
        }
        if (raw is null) return null;
        var alt = await LoadAlternates(conn, tx, [id], ct);
        var groups = (await LoadGroups(conn, tx, ct)).ToDictionary(g => g.Id);
        return ToView(raw, alt, groups, (await ReadSettings(conn, tx, ct)).DefaultValuation);
    }

    public async Task<ItemView?> GetItemAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadItem(conn, null, id, false, ct);
    }

    public sealed record ItemFilter(string? Search, Guid? GroupId, bool? Active, int? Type, int Offset, int Limit);
    public sealed record ItemPage(IReadOnlyList<ItemView> Items, long Total);

    /// <summary>Filtered, ordered (by code) and paged item list. <see cref="ItemFilter.Search"/> matches the code, either name or an exact barcode (case-insensitive); a group filter includes its sub-groups.</summary>
    public async Task<ItemPage> ListItemsAsync(Guid companyId, ItemFilter filter, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var groups = await LoadGroups(conn, null, ct);
        var byId = groups.ToDictionary(g => g.Id);
        var def = (await ReadSettings(conn, null, ct)).DefaultValuation;
        var where = new List<string>();
        await using var cmd = new NpgsqlCommand { Connection = conn };
        if (filter.Active is { } a) { where.Add($"i.{I.IsActive} = @active"); cmd.Parameters.AddWithValue("active", a); }
        if (filter.Type is { } t) { where.Add($"i.{I.ItemType} = @type"); cmd.Parameters.AddWithValue("type", (short)t); }
        if (filter.GroupId is { } g)
        {
            var ids = new List<Guid> { g };
            for (var k = 0; k < ids.Count; k++) ids.AddRange(groups.Where(x => x.ParentId == ids[k]).Select(x => x.Id));
            where.Add($"i.{I.GroupId} = ANY(@groups)");
            cmd.Parameters.Add(new NpgsqlParameter("groups", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() });
        }
        if (InventoryValidation.Clean(filter.Search) is { } s)
        {
            where.Add($"(upper(i.{I.Code}) LIKE @like OR upper(coalesce(i.{I.NameAr}, '')) LIKE @like OR upper(coalesce(i.{I.NameEn}, '')) LIKE @like OR EXISTS (SELECT 1 FROM {BC.Table} b WHERE b.{BC.ItemId} = i.{I.Id} AND upper(b.{BC.Barcode}) = @exact))");
            cmd.Parameters.AddWithValue("like", "%" + s.ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            cmd.Parameters.AddWithValue("exact", s.ToUpperInvariant());
        }
        var w = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"SELECT count(*) FROM {I.Table} i{w}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        cmd.Parameters.AddWithValue("lim", Math.Clamp(filter.Limit, 1, 500));
        cmd.Parameters.AddWithValue("off", Math.Max(0, filter.Offset));
        cmd.CommandText = $"SELECT {string.Join(", ", ItemCols.Split(", ").Select(c => "i." + c))} FROM {I.Table} i{w} ORDER BY i.{I.Code} LIMIT @lim OFFSET @off";
        var raws = new List<RawItem>();
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) raws.Add(ReadItem(r));
        var alt = raws.Count == 0 ? [] : await LoadAlternates(conn, null, [.. raws.Select(x => x.Id)], ct);
        return new ItemPage([.. raws.Select(x => ToView(x, alt, byId, def))], total);
    }

    private async Task<(Dictionary<Guid, UnitView> Units, Dictionary<Guid, RawGroup> Groups, int? Default)> Refs(CompanyAuditedWork work, CancellationToken ct)
    {
        var units = new Dictionary<Guid, UnitView>();
        await using (var cmd = new NpgsqlCommand($"SELECT {UnitCols} FROM {U.Table}", work.Connection, work.Transaction))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) { var u = ReadUnit(r); units[u.Id] = u; }
        var groups = (await LoadGroups(work.Connection, work.Transaction, ct)).ToDictionary(g => g.Id);
        return (units, groups, (await ReadSettings(work.Connection, work.Transaction, ct)).DefaultValuation);
    }

    private static void ValidateItemRefs(InventoryValidation.ItemData d, IReadOnlyDictionary<Guid, UnitView> units, IReadOnlyDictionary<Guid, RawGroup> groups, int? def, ItemView? existing)
    {
        if (!groups.TryGetValue(d.GroupId, out var group)) throw Invalid("groupId", "group_not_found");
        if (!group.IsActive && (existing is null || existing.GroupId != d.GroupId)) throw new Abort(ConfigStatus.Conflict, "group_inactive");
        var attached = existing is null ? [] : existing.AlternateUnits.Select(a => a.UnitId).Append(existing.BaseUnitId).ToHashSet();
        void Unit(Guid id, string field)
        {
            if (!units.TryGetValue(id, out var u)) throw Invalid(field, "unit_not_found");
            if (!u.IsActive && !attached.Contains(id)) throw Invalid(field, "unit_inactive");
        }
        Unit(d.BaseUnitId, "baseUnitId");
        for (var i = 0; i < d.Alternates.Count; i++) Unit(d.Alternates[i].UnitId, $"alternateUnits[{i}].unitId");
        if (d.PurchaseUnitId is { } p) Unit(p, "purchaseUnitId");
        if (d.SalesUnitId is { } s) Unit(s, "salesUnitId");
        if (d.Type == ItemType.Goods && Effective(groups, d.GroupId, def) is null) throw Invalid("groupId", "valuation_method_unresolved");   // a stockable item needs a decided policy
    }

    private async Task WriteAlternates(CompanyAuditedWork work, Guid itemId, IReadOnlyList<(Guid UnitId, decimal Factor)> alt, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand($"DELETE FROM {IU.Table} WHERE {IU.ItemId} = @i", work.Connection, work.Transaction))
        {
            del.Parameters.AddWithValue("i", itemId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var (unitId, factor) in alt)
        {
            await using var ins = new NpgsqlCommand($"INSERT INTO {IU.Table} ({IU.ItemId}, {IU.UnitId}, {IU.Factor}) VALUES (@i, @u, @f)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("i", itemId);
            ins.Parameters.AddWithValue("u", unitId);
            ins.Parameters.AddWithValue("f", factor);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<ConfigResult<ItemView>> CreateItemAsync(Guid actor, Guid companyId, ItemInput? input, CancellationToken ct = default)
    {
        if (!InventoryValidation.TryNormalizeItem(input, null, out var d, out var errors)) return ConfigResult<ItemView>.Invalid(errors);
        return await Mutate(companyId, async work =>
        {
            var (units, groups, def) = await Refs(work, ct);
            ValidateItemRefs(d, units, groups, def, null);
            var code = d.Code ?? await NextGeneratedCode(work, ct);
            var id = Guid.NewGuid();
            try
            {
                await using var ins = new NpgsqlCommand($"INSERT INTO {I.Table} ({ItemCols}) VALUES (@id, @c, @ar, @en, @t, @g, @bu, @pu, @su, @lot, @ser, @exp, @notes, true, 1, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("c", code);
                ins.Parameters.AddWithValue("ar", Db(d.NameAr));
                ins.Parameters.AddWithValue("en", Db(d.NameEn));
                ins.Parameters.AddWithValue("t", (short)d.Type);
                ins.Parameters.AddWithValue("g", d.GroupId);
                ins.Parameters.AddWithValue("bu", d.BaseUnitId);
                ins.Parameters.AddWithValue("pu", DbN(d.PurchaseUnitId));
                ins.Parameters.AddWithValue("su", DbN(d.SalesUnitId));
                ins.Parameters.AddWithValue("lot", d.IsLot);
                ins.Parameters.AddWithValue("ser", d.IsSerial);
                ins.Parameters.AddWithValue("exp", d.HasExpiry);
                ins.Parameters.AddWithValue("notes", Db(d.Notes));
                ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "item_code_taken"); }
            await WriteAlternates(work, id, d.Alternates, ct);
            await Audit(work, InventoryAuditEvents.ItemCreated, actor, id, $"item {code} created type={(int)d.Type} alternates={d.Alternates.Count}", ct);
            return (await LoadItem(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>The next free automatic code (ITM-000001, ...): the counter is advanced inside the transaction and skips any code a user already took manually.</summary>
    private async Task<string> NextGeneratedCode(CompanyAuditedWork work, CancellationToken ct)
    {
        await using (var ensure = new NpgsqlCommand($"INSERT INTO {S.Table} ({S.Id}, {S.NextItemNumber}, {S.Revision}, {S.UpdatedAtUtc}) VALUES (1, 1, 1, @now) ON CONFLICT ({S.Id}) DO NOTHING", work.Connection, work.Transaction))
        {
            ensure.Parameters.AddWithValue("now", Now);
            await ensure.ExecuteNonQueryAsync(ct);
        }
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            long n;
            await using (var take = new NpgsqlCommand($"UPDATE {S.Table} SET {S.NextItemNumber} = {S.NextItemNumber} + 1, {S.Revision} = {S.Revision} + 1, {S.UpdatedAtUtc} = @now WHERE {S.Id} = 1 RETURNING {S.NextItemNumber} - 1", work.Connection, work.Transaction))
            {
                take.Parameters.AddWithValue("now", Now);
                n = Convert.ToInt64(await take.ExecuteScalarAsync(ct));
            }
            var code = InventoryValidation.FormatGeneratedCode(n);
            await using var exists = new NpgsqlCommand($"SELECT 1 FROM {I.Table} WHERE upper({I.Code}) = @c", work.Connection, work.Transaction);
            exists.Parameters.AddWithValue("c", code);
            if (await exists.ExecuteScalarAsync(ct) is null) return code;
        }
        throw new Abort(ConfigStatus.Conflict, "item_code_generation_failed");
    }

    public Task<ConfigResult<ItemView>> UpdateItemAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, ItemInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<ItemView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await LoadItem(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "item_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (!InventoryValidation.TryNormalizeItem(input, cur, out var d, out var errors)) throw new Abort(ConfigStatus.Invalid, "validation_failed", errors);
            var (units, groups, def) = await Refs(work, ct);
            ValidateItemRefs(d, units, groups, def, cur);
            var sameAlt = cur.AlternateUnits.Count == d.Alternates.Count && cur.AlternateUnits.All(a => d.Alternates.Any(b => b.UnitId == a.UnitId && b.Factor == a.Factor));
            if (d.NameAr == cur.NameAr && d.NameEn == cur.NameEn && d.GroupId == cur.GroupId && d.PurchaseUnitId == cur.PurchaseUnitId && d.SalesUnitId == cur.SalesUnitId && d.IsLot == cur.IsLot
                && d.IsSerial == cur.IsSerial && d.HasExpiry == cur.HasExpiry && d.Notes == cur.Notes && sameAlt) throw new Unchanged(cur);   // nothing to change, nothing audited
            await using (var upd = new NpgsqlCommand($"UPDATE {I.Table} SET {I.NameAr} = @ar, {I.NameEn} = @en, {I.GroupId} = @g, {I.PurchaseUnitId} = @pu, {I.SalesUnitId} = @su, {I.IsLot} = @lot, {I.IsSerial} = @ser, {I.HasExpiry} = @exp, {I.Notes} = @notes, {I.Revision} = {I.Revision} + 1, {I.UpdatedAtUtc} = @now WHERE {I.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("ar", Db(d.NameAr));
                upd.Parameters.AddWithValue("en", Db(d.NameEn));
                upd.Parameters.AddWithValue("g", d.GroupId);
                upd.Parameters.AddWithValue("pu", DbN(d.PurchaseUnitId));
                upd.Parameters.AddWithValue("su", DbN(d.SalesUnitId));
                upd.Parameters.AddWithValue("lot", d.IsLot);
                upd.Parameters.AddWithValue("ser", d.IsSerial);
                upd.Parameters.AddWithValue("exp", d.HasExpiry);
                upd.Parameters.AddWithValue("notes", Db(d.Notes));
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            if (!sameAlt) await WriteAlternates(work, id, d.Alternates, ct);
            await Audit(work, InventoryAuditEvents.ItemUpdated, actor, id, $"item {cur.Code} updated r{cur.Revision + 1} group {(cur.GroupId == d.GroupId ? "same" : "changed")} units {(sameAlt ? "same" : "changed")}", ct);
            return (await LoadItem(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public Task<ConfigResult<ItemView>> SetItemActiveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool active, CancellationToken ct = default)
    {
        if (expectedRevision is null) return Task.FromResult(ConfigResult<ItemView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required"));
        return Mutate(companyId, async work =>
        {
            var cur = await LoadItem(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "item_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.IsActive == active) throw new Abort(ConfigStatus.Conflict, active ? "item_already_active" : "item_already_inactive");
            if (active)
            {
                var (_, groups, _) = await Refs(work, ct);
                if (!groups[cur.GroupId].IsActive) throw new Abort(ConfigStatus.Conflict, "group_inactive");
            }
            await using (var upd = new NpgsqlCommand($"UPDATE {I.Table} SET {I.IsActive} = @a, {I.Revision} = {I.Revision} + 1, {I.UpdatedAtUtc} = @now WHERE {I.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("a", active);
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await Audit(work, InventoryAuditEvents.ItemActiveChanged, actor, id, $"item {cur.Code} {(active ? "activated" : "deactivated")}", ct);
            return (await LoadItem(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    // ================================================================================================================ barcodes

    private static readonly string BarcodeCols = $"{BC.Id}, {BC.ItemId}, {BC.UnitId}, {BC.Barcode}, {BC.Kind}, {BC.IsActive}, {BC.CreatedAtUtc}, {BC.UpdatedAtUtc}";

    private static BarcodeView ReadBarcode(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt16(4), r.GetBoolean(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7));

    public async Task<IReadOnlyList<BarcodeView>> ListBarcodesAsync(Guid companyId, Guid itemId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {BarcodeCols} FROM {BC.Table} WHERE {BC.ItemId} = @i ORDER BY {BC.CreatedAtUtc}, {BC.Barcode}", conn);
        cmd.Parameters.AddWithValue("i", itemId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<BarcodeView>();
        while (await r.ReadAsync(ct)) list.Add(ReadBarcode(r));
        return list;
    }

    /// <summary>Exact barcode lookup (case-insensitive, active or inactive): the barcode with its item and the unit factor to the item's base unit.</summary>
    public async Task<BarcodeLookup?> LookupBarcodeAsync(Guid companyId, string? barcode, CancellationToken ct = default)
    {
        var v = InventoryValidation.Clean(barcode)?.ToUpperInvariant();
        if (v is null) return null;
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {string.Join(", ", BarcodeCols.Split(", ").Select(c => "b." + c))}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, u.{U.Code}, CASE WHEN b.{BC.UnitId} = i.{I.BaseUnitId} THEN 1 ELSE coalesce((SELECT iu.{IU.Factor} FROM {IU.Table} iu WHERE iu.{IU.ItemId} = i.{I.Id} AND iu.{IU.UnitId} = b.{BC.UnitId}), 1) END " +
            $"FROM {BC.Table} b JOIN {I.Table} i ON i.{I.Id} = b.{BC.ItemId} JOIN {U.Table} u ON u.{U.Id} = b.{BC.UnitId} WHERE upper(b.{BC.Barcode}) = @v", conn);
        cmd.Parameters.AddWithValue("v", v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new BarcodeLookup(ReadBarcode(r), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetDecimal(12));
    }

    public async Task<ConfigResult<BarcodeView>> AddBarcodeAsync(Guid actor, Guid companyId, Guid itemId, BarcodeInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<BarcodeView>.Invalid([new("body", "required")]);
        if (!InventoryValidation.TryNormalizeBarcode(input.Barcode, input.Kind, out var barcode, out var kind, out var errors)) return ConfigResult<BarcodeView>.Invalid(errors);
        return await Mutate(companyId, async work =>
        {
            var item = await LoadItem(work.Connection, work.Transaction, itemId, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "item_not_found");
            if (!item.IsActive) throw new Abort(ConfigStatus.Conflict, "item_inactive");
            var unitId = input.UnitId ?? item.BaseUnitId;
            if (unitId != item.BaseUnitId && item.AlternateUnits.All(a => a.UnitId != unitId)) throw Invalid("unitId", "unit_not_defined_for_item");
            var id = Guid.NewGuid();
            try
            {
                await using var ins = new NpgsqlCommand($"INSERT INTO {BC.Table} ({BarcodeCols}) VALUES (@id, @i, @u, @b, @k, true, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("i", itemId);
                ins.Parameters.AddWithValue("u", unitId);
                ins.Parameters.AddWithValue("b", barcode);
                ins.Parameters.AddWithValue("k", (short)kind);
                ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "barcode_taken"); }
            await Audit(work, InventoryAuditEvents.BarcodeAdded, actor, id, $"barcode added to item {item.Code} kind={(int)kind}", ct);
            return await LoadBarcode(work, id, ct);
        }, ct);
    }

    private static async Task<BarcodeView> LoadBarcode(CompanyAuditedWork work, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {BarcodeCols} FROM {BC.Table} WHERE {BC.Id} = @id", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ReadBarcode(r);
    }

    public Task<ConfigResult<BarcodeView>> SetBarcodeActiveAsync(Guid actor, Guid companyId, Guid itemId, Guid barcodeId, bool active, CancellationToken ct = default) =>
        Mutate(companyId, async work =>
        {
            BarcodeView cur;
            await using (var q = new NpgsqlCommand($"SELECT {BarcodeCols} FROM {BC.Table} WHERE {BC.Id} = @id AND {BC.ItemId} = @i FOR UPDATE", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("id", barcodeId);
                q.Parameters.AddWithValue("i", itemId);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) throw new Abort(ConfigStatus.NotFound, "barcode_not_found");
                cur = ReadBarcode(r);
            }
            if (cur.IsActive == active) throw new Abort(ConfigStatus.Conflict, active ? "barcode_already_active" : "barcode_already_inactive");
            if (active)
            {
                await using var item = new NpgsqlCommand($"SELECT {I.IsActive} FROM {I.Table} WHERE {I.Id} = @i", work.Connection, work.Transaction);
                item.Parameters.AddWithValue("i", itemId);
                if (await item.ExecuteScalarAsync(ct) is not true) throw new Abort(ConfigStatus.Conflict, "item_inactive");
            }
            await using (var upd = new NpgsqlCommand($"UPDATE {BC.Table} SET {BC.IsActive} = @a, {BC.UpdatedAtUtc} = @now WHERE {BC.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", barcodeId);
                upd.Parameters.AddWithValue("a", active);
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await Audit(work, InventoryAuditEvents.BarcodeActiveChanged, actor, barcodeId, $"barcode {(active ? "activated" : "deactivated")}", ct);
            return await LoadBarcode(work, barcodeId, ct);
        }, ct);
}
