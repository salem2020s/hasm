using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using C = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Cost-center domain operations (Phase 3.2B, slice S3). <b>No authorization here by design:</b> <see cref="CostCenterManagementService"/> wraps it.
/// Every mutation runs in <see cref="CompanyAuditedTransactions"/> (change + append-only history row + authenticated audit event commit together) after ONE
/// company-wide advisory lock; PostgreSQL independently enforces the tree (no cycles, parents are groups, a node with children is a group, active needs an
/// active parent), unique codes, immutable ids and no deletion. A cost center is never deleted: deactivation keeps history and merely stops NEW allocations.
/// <para>Allocation compatibility (used by the posting engine, slice S5): only an ACTIVE LEAF receives allocations (<see cref="CostCenterView.CanReceiveAllocations"/>),
/// allocations reference the stable id (so renaming, re-coding, moving or deactivating never rewrites history; earlier names and parents stay in the revision
/// history), and groups never hold amounts, so rolling leaves up the tree never double counts.</para>
/// </summary>
public sealed class CostCenterService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48434343;   // "HCCC": advisory-lock namespace of cost-center changes
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    private static readonly string Columns =
        $"a.{C.Id}, a.{C.Code}, a.{C.NameAr}, a.{C.NameEn}, a.{C.ParentId}, a.{C.BranchId}, a.{C.IsGroup}, a.{C.IsActive}, a.{C.Revision}, a.{C.CreatedAtUtc}, a.{C.UpdatedAtUtc}, " +
        $"EXISTS (SELECT 1 FROM {C.Table} c WHERE c.{C.ParentId} = a.{C.Id})";

    public async Task<IReadOnlyList<CostCenterView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, null, false, ct);
    }

    public async Task<CostCenterView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadAsync(conn, null, id, false, ct)).SingleOrDefault();
    }

    internal static async Task<List<CostCenterView>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {C.Table} a {(id is null ? "" : $"WHERE a.{C.Id} = @id")} ORDER BY upper(a.{C.Code}){(forUpdate ? " FOR UPDATE OF a" : "")}", conn, tx);
        if (id is not null) cmd.Parameters.AddWithValue("id", id.Value);
        var list = new List<CostCenterView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CostCenterView(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.GetBoolean(6), r.GetBoolean(7), r.GetInt64(8), new DateTimeOffset(r.GetDateTime(9), TimeSpan.Zero), new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero), r.GetBoolean(11)));
        return list;
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    public async Task<ConfigResult<CostCenterView>> CreateAsync(Guid actor, Guid companyId, CostCenterInput? input, CancellationToken ct = default)
    {
        if (!CostCenterValidation.TryNormalize(input, out var code, out var nameAr, out var nameEn, out var isGroup, out var errors)) return ConfigResult<CostCenterView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            if (input!.ParentId is { } pid) await RequireActiveGroupParentAsync(work, pid, ct);
            if (input.BranchId is { } bid) await RequireActiveBranchAsync(work, bid, ct);
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                await ExecAsync(work, $"INSERT INTO {C.Table} ({C.Id}, {C.Code}, {C.NameAr}, {C.NameEn}, {C.ParentId}, {C.BranchId}, {C.IsGroup}, {C.IsActive}, {C.Revision}, {C.CreatedAtUtc}, {C.UpdatedAtUtc}) VALUES (@id, @code, @ar, @en, @parent, @branch, @group, true, 1, @now, @now)",
                    p =>
                    {
                        p.AddWithValue("id", id); p.AddWithValue("code", code);
                        p.Add(new NpgsqlParameter("ar", NpgsqlDbType.Varchar) { Value = (object?)nameAr ?? DBNull.Value });
                        p.Add(new NpgsqlParameter("en", NpgsqlDbType.Varchar) { Value = (object?)nameEn ?? DBNull.Value });
                        p.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)input.ParentId ?? DBNull.Value });
                        p.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = (object?)input.BranchId ?? DBNull.Value });
                        p.AddWithValue("group", isGroup); p.AddWithValue("now", now);
                    });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "code_in_use"); }
            var changes = new JsonObject { ["code"] = code, ["nameAr"] = nameAr, ["nameEn"] = nameEn, ["parentId"] = input.ParentId?.ToString(), ["branchId"] = input.BranchId?.ToString(), ["isGroup"] = isGroup };
            await RecordAsync(work, LedgerAuditEvents.CostCenterCreated, actor, id, 1, changes, "cost center created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct);
    }

    public async Task<ConfigResult<CostCenterView>> UpdateDetailsAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CostCenterDetailsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CostCenterView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!CostCenterValidation.TryNormalizeDetails(input, out var nameAr, out var nameEn, out var errors)) return ConfigResult<CostCenterView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            var group = input!.IsGroup ?? cur.IsGroup;
            if (nameAr == cur.NameAr && nameEn == cur.NameEn && input.BranchId == cur.BranchId && group == cur.IsGroup) throw new Abort(ConfigStatus.Ok, "unchanged");
            if (group != cur.IsGroup && !group && cur.HasChildren) throw new Abort(ConfigStatus.Conflict, "has_children");
            if (input.BranchId is { } bid && bid != cur.BranchId) await RequireActiveBranchAsync(work, bid, ct);
            await ExecAsync(work, $"UPDATE {C.Table} SET {C.NameAr} = @ar, {C.NameEn} = @en, {C.BranchId} = @branch, {C.IsGroup} = @group, {C.Revision} = {C.Revision} + 1, {C.UpdatedAtUtc} = @now WHERE {C.Id} = @id",
                p =>
                {
                    p.AddWithValue("id", id);
                    p.Add(new NpgsqlParameter("ar", NpgsqlDbType.Varchar) { Value = (object?)nameAr ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("en", NpgsqlDbType.Varchar) { Value = (object?)nameEn ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = (object?)input.BranchId ?? DBNull.Value });
                    p.AddWithValue("group", group); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                });
            var changes = new JsonObject();
            Diff(changes, "nameAr", cur.NameAr, nameAr); Diff(changes, "nameEn", cur.NameEn, nameEn); Diff(changes, "branchId", cur.BranchId?.ToString(), input.BranchId?.ToString()); Diff(changes, "isGroup", cur.IsGroup.ToString(), group.ToString());
            await RecordAsync(work, LedgerAuditEvents.CostCenterUpdated, actor, id, cur.Revision + 1, changes, "cost center updated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    public async Task<ConfigResult<CostCenterView>> RecodeAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CostCenterCodeInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CostCenterView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var code = CostCenterValidation.NormalizeCode(input?.Code);
        if (code is null) return ConfigResult<CostCenterView>.Invalid([new("code", input?.Code is null ? "required" : "invalid_format")]);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Code == code) throw new Abort(ConfigStatus.Ok, "unchanged");
            try
            {
                await ExecAsync(work, $"UPDATE {C.Table} SET {C.Code} = @code, {C.Revision} = {C.Revision} + 1, {C.UpdatedAtUtc} = @now WHERE {C.Id} = @id",
                    p => { p.AddWithValue("id", id); p.AddWithValue("code", code); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "code_in_use"); }
            var changes = new JsonObject(); Diff(changes, "code", cur.Code, code);
            await RecordAsync(work, LedgerAuditEvents.CostCenterCodeChanged, actor, id, cur.Revision + 1, changes, "cost center code changed", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    public async Task<ConfigResult<CostCenterView>> MoveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CostCenterMoveInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CostCenterView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<CostCenterView>.Invalid([new("body", "required")]);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.ParentId == input.ParentId) throw new Abort(ConfigStatus.Ok, "unchanged");
            if (input.ParentId is { } np)
            {
                if (np == id) throw new Abort(ConfigStatus.Conflict, "cost_center_cycle");
                var parent = await RequireActiveGroupParentAsync(work, np, ct, mustBeActive: cur.IsActive);
                if (await IsDescendantAsync(work, np, id, ct)) throw new Abort(ConfigStatus.Conflict, "cost_center_cycle");
            }
            await ExecAsync(work, $"UPDATE {C.Table} SET {C.ParentId} = @parent, {C.Revision} = {C.Revision} + 1, {C.UpdatedAtUtc} = @now WHERE {C.Id} = @id",
                p => { p.AddWithValue("id", id); p.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)input.ParentId ?? DBNull.Value }); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            var changes = new JsonObject(); Diff(changes, "parentId", cur.ParentId?.ToString(), input.ParentId?.ToString());
            await RecordAsync(work, LedgerAuditEvents.CostCenterMoved, actor, id, cur.Revision + 1, changes, "cost center moved", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    /// <summary>Deactivation never deletes anything; a group can only be deactivated when none of its children is active.</summary>
    public Task<ConfigResult<CostCenterView>> DeactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) => ToggleAsync(actor, companyId, id, expectedRevision, false, ct);

    public Task<ConfigResult<CostCenterView>> ReactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) => ToggleAsync(actor, companyId, id, expectedRevision, true, ct);

    private async Task<ConfigResult<CostCenterView>> ToggleAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool active, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<CostCenterView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.IsActive == active) throw new Abort(ConfigStatus.Ok, "unchanged");
            if (!active)
            {
                await using var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {C.Table} WHERE {C.ParentId} = @id AND {C.IsActive})", work.Connection, work.Transaction);
                q.Parameters.AddWithValue("id", id);
                if ((bool)(await q.ExecuteScalarAsync(ct))!) throw new Abort(ConfigStatus.Conflict, "has_active_children");
            }
            else if (cur.ParentId is { } pid)
            {
                var parent = (await LoadAsync(work.Connection, work.Transaction, pid, true, ct)).Single();
                if (!parent.IsActive) throw new Abort(ConfigStatus.Conflict, "parent_inactive");
            }
            await ExecAsync(work, $"UPDATE {C.Table} SET {C.IsActive} = @a, {C.Revision} = {C.Revision} + 1, {C.UpdatedAtUtc} = @now WHERE {C.Id} = @id",
                p => { p.AddWithValue("id", id); p.AddWithValue("a", active); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            var changes = new JsonObject(); Diff(changes, "isActive", cur.IsActive.ToString(), active.ToString());
            await RecordAsync(work, active ? LedgerAuditEvents.CostCenterReactivated : LedgerAuditEvents.CostCenterDeactivated, actor, id, cur.Revision + 1, changes, active ? "cost center reactivated" : "cost center deactivated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private static void Diff(JsonObject changes, string name, string? old, string? @new)
    {
        if (!string.Equals(old, @new, StringComparison.Ordinal)) changes[name] = new JsonObject { ["old"] = old, ["new"] = @new };
    }

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct, Func<Task<T?>>? onUnchanged = null) where T : class
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) when (abort.Message == "unchanged" && onUnchanged is not null) { return ConfigResult<T>.Ok((await onUnchanged())!); }
        catch (Abort abort) { return abort.To<T>(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(CompanyAuditedWork work, string sql, Action<NpgsqlParameterCollection> bind)
    {
        await using var cmd = new NpgsqlCommand(sql, work.Connection, work.Transaction);
        bind(cmd.Parameters);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("cost center row not written");
    }

    private static async Task<CostCenterView> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var cur = (await LoadAsync(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "cost_center_not_found");
        if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
        return cur;
    }

    private static async Task<CostCenterView> RequireActiveGroupParentAsync(CompanyAuditedWork work, Guid parentId, CancellationToken ct, bool mustBeActive = true)
    {
        var parent = (await LoadAsync(work.Connection, work.Transaction, parentId, true, ct)).SingleOrDefault()
            ?? throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("parentId", "parent_not_found")]);
        if (!parent.IsGroup) throw new Abort(ConfigStatus.Conflict, "parent_not_group");
        if (mustBeActive && !parent.IsActive) throw new Abort(ConfigStatus.Conflict, "parent_inactive");
        return parent;
    }

    private static async Task RequireActiveBranchAsync(CompanyAuditedWork work, Guid branchId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = @id", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("id", branchId);
        var v = await q.ExecuteScalarAsync(ct);
        if (v is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_not_found")]);
        if (!(bool)v) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_inactive")]);
    }

    /// <summary>True when <paramref name="candidate"/> is a descendant of <paramref name="ancestor"/> (moving under it would create a cycle).</summary>
    private static async Task<bool> IsDescendantAsync(CompanyAuditedWork work, Guid candidate, Guid ancestor, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"WITH RECURSIVE up(id, parent) AS (SELECT a.{C.Id}, a.{C.ParentId} FROM {C.Table} a WHERE a.{C.Id} = @c UNION SELECT a.{C.Id}, a.{C.ParentId} FROM {C.Table} a JOIN up ON a.{C.Id} = up.parent) SELECT EXISTS (SELECT 1 FROM up WHERE up.id = @anc)",
            work.Connection, work.Transaction);
        q.Parameters.AddWithValue("c", candidate); q.Parameters.AddWithValue("anc", ancestor);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }

    private async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, Guid id, long revision, JsonObject changes, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', changes.Select(c => c.Key))} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {LH.Table} ({LH.Entity}, {LH.EntityKey}, {LH.Revision}, {LH.ActorUserId}, {LH.ChangedAtUtc}, {LH.Changes}, {LH.AuditStream}, {LH.AuditSeq}) VALUES (3, @k, @r, @a, @t, @c, @s, @q)",
            work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("k", id.ToString()); cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey); cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
