using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using R = Hasm.Infrastructure.CompanyDatabase.CompanySchema.WarehouseRevision;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Warehouse domain operations (Phase 3.1C). <b>No authorization here by design:</b> <see cref="WarehouseManagementService"/> wraps it.
/// Every mutation runs in <see cref="CompanyAuditedTransactions"/>: the change, its append-only revision row and its authenticated audit event
/// commit or roll back together, after the SAME transaction-level advisory lock that serialises branch-structure changes (so a warehouse
/// change and a branch deactivation can never interleave), taken before the audit-stream lock as the lock order requires.
/// <list type="bullet">
/// <item>A warehouse belongs to exactly one branch for life; it can be created or reactivated only in an ACTIVE branch (also enforced by PostgreSQL).</item>
/// <item>Trial limit: counted over every warehouse ever created (inactive included); rows are never deleted.</item>
/// <item>ActivationEpoch advances on every deactivation and reactivation.</item>
/// </list>
/// Sensitive fields (address lines, postal code, phone, email, website) are recorded as "changed" only, never their values.
/// </summary>
public sealed class CompanyWarehouseService(
    CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, IEntitlementPolicy entitlements, TimeProvider clock)
{
    private static readonly string Columns =
        $"{W.Id}, {W.Code}, {W.NameAr}, {W.NameEn}, {W.IsActive}, {W.BranchId}, {W.ActivationEpoch}, {W.Address1}, {W.Address2}, {W.City}, {W.Region}, " +
        $"{W.PostalCode}, {W.Phone}, {W.Email}, {W.Website}, {W.Revision}, {W.CreatedAtUtc}, {W.UpdatedAtUtc}";

    private sealed record Field(string Name, bool HistoryValues, Func<WarehouseData, string?> Get);

    private static readonly IReadOnlyList<Field> Fields =
    [
        new("nameAr", true, d => d.NameAr), new("nameEn", true, d => d.NameEn),
        new("addressLine1", false, d => d.AddressLine1), new("addressLine2", false, d => d.AddressLine2),
        new("city", true, d => d.City), new("region", true, d => d.Region), new("postalCode", false, d => d.PostalCode),
        new("phone", false, d => d.Phone), new("email", false, d => d.Email), new("website", false, d => d.Website),
    ];

    public int MaxWarehouses => entitlements.MaxWarehouses;

    private sealed class Abort(ConfigResult<WarehouseView>? result, ConfigStatus status, string code, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<WarehouseView> ToResult() => result ?? ConfigResult<WarehouseView>.Fail(status, code, currentRevision);
    }

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<IReadOnlyList<WarehouseView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {W.Table} ORDER BY {W.Code}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<WarehouseView>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<WarehouseView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, false, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    public async Task<ConfigResult<WarehouseView>> CreateAsync(Guid actor, Guid companyId, WarehouseInput? input, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeWarehouse(input, null, null, out var data, out var branchId, out var errors)) return ConfigResult<WarehouseView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            // The branch must exist and be active (the structure lock already serialises branch changes against this transaction).
            await using (var q = new NpgsqlCommand($"SELECT {B.IsActive} FROM {B.Table} WHERE {B.Id} = @b FOR SHARE", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("b", branchId);
                var active = await q.ExecuteScalarAsync(ct);
                if (active is null) throw new Abort(ConfigResult<WarehouseView>.Invalid([new("branchId", "branch_not_found")]), ConfigStatus.Invalid, "validation_failed");
                if (!(bool)active) throw new Abort(null, ConfigStatus.Conflict, "branch_inactive");
            }
            // Trial/edition limit over EVERY warehouse ever created (rows are never deleted, so inactive warehouses keep counting).
            await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {W.Table}", work.Connection, work.Transaction))
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= entitlements.MaxWarehouses)
                    throw new Abort(null, ConfigStatus.EntitlementDenied, "entitlement_denied");
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO {W.Table} ({W.Id}, {W.Code}, {W.BranchId}, {W.NameAr}, {W.NameEn}, {W.IsActive}, {W.ActivationEpoch}, {W.Address1}, {W.Address2}, {W.City}, {W.Region}, " +
                    $"{W.PostalCode}, {W.Phone}, {W.Email}, {W.Website}, {W.Revision}, {W.CreatedAtUtc}, {W.UpdatedAtUtc}) " +
                    "VALUES (@id, @code, @branch, @ar, @en, true, 1, @a1, @a2, @city, @region, @postal, @phone, @email, @web, 1, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("code", data.Code);
                ins.Parameters.AddWithValue("branch", branchId);
                Bind(ins, data);
                ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new Abort(null, ConfigStatus.Conflict, "warehouse_code_taken");
            }
            var changes = Diff(null, data);
            changes["code"] = new JsonObject { ["old"] = null, ["new"] = data.Code };
            changes["branchId"] = new JsonObject { ["old"] = null, ["new"] = branchId.ToString() };
            await RecordAsync(work, ConfigAuditEvents.WarehouseCreated, actor, id, 1, changes, [.. changes.Select(c => c.Key)], "warehouse created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public async Task<ConfigResult<WarehouseView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, WarehouseInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<WarehouseView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!ConfigurationValidation.TryNormalizeWarehouse(input, current.Data.Code, current.BranchId, out var data, out _, out var errors))
                throw new Abort(ConfigResult<WarehouseView>.Invalid(errors), ConfigStatus.Invalid, "validation_failed");
            var changes = Diff(current.Data, data);
            if (changes.Count == 0) throw new Abort(ConfigResult<WarehouseView>.Ok(current), ConfigStatus.Ok, "unchanged");   // nothing to audit, nothing written
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {W.Table} SET {W.NameAr} = @ar, {W.NameEn} = @en, {W.Address1} = @a1, {W.Address2} = @a2, {W.City} = @city, {W.Region} = @region, " +
                $"{W.PostalCode} = @postal, {W.Phone} = @phone, {W.Email} = @email, {W.Website} = @web, {W.Revision} = {W.Revision} + 1, {W.UpdatedAtUtc} = @now WHERE {W.Id} = @id",
                work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                Bind(upd, data);
                upd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await RecordAsync(work, ConfigAuditEvents.WarehouseUpdated, actor, id, current.Revision + 1, changes, [.. changes.Select(c => c.Key)], "warehouse updated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>Deactivates a warehouse (epoch +1). Warehouse-scoped access assignments recorded under the old epoch stop being effective.</summary>
    public async Task<ConfigResult<WarehouseView>> DeactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<WarehouseView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!current.IsActive) throw new Abort(null, ConfigStatus.Conflict, "warehouse_already_inactive");
            // A warehouse with open stock documents (draft, submitted or approved and not yet posted) cannot be deactivated: the documents would be left pointing at an unusable warehouse.
            await using (var open = new NpgsqlCommand($"SELECT 1 FROM {SD.Table} WHERE ({SD.SourceWarehouseId} = @w OR {SD.TargetWarehouseId} = @w) AND {SD.Status} IN (1, 2, 3) LIMIT 1", work.Connection, work.Transaction))
            {
                open.Parameters.AddWithValue("w", id);
                if (await open.ExecuteScalarAsync(ct) is not null) throw new Abort(null, ConfigStatus.Conflict, "warehouse_has_open_documents");
            }
            return await ToggleAsync(work, actor, current, false, ConfigAuditEvents.WarehouseDeactivated, "warehouse deactivated", ct);
        }, ct);
    }

    /// <summary>Reactivates a warehouse (epoch +1) — only while its branch is active. Assignments recorded under an earlier epoch never become effective again.</summary>
    public async Task<ConfigResult<WarehouseView>> ReactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<WarehouseView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (current.IsActive) throw new Abort(null, ConfigStatus.Conflict, "warehouse_already_active");
            await using (var q = new NpgsqlCommand($"SELECT {B.IsActive} FROM {B.Table} WHERE {B.Id} = @b FOR SHARE", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("b", current.BranchId);
                if (await q.ExecuteScalarAsync(ct) is not true) throw new Abort(null, ConfigStatus.Conflict, "branch_inactive");
            }
            return await ToggleAsync(work, actor, current, true, ConfigAuditEvents.WarehouseReactivated, "warehouse reactivated", ct);
        }, ct);
    }

    private async Task<WarehouseView> ToggleAsync(CompanyAuditedWork work, Guid actor, WarehouseView current, bool active, short eventType, string label, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {W.Table} SET {W.IsActive} = @a, {W.ActivationEpoch} = {W.ActivationEpoch} + 1, {W.Revision} = {W.Revision} + 1, {W.UpdatedAtUtc} = @now WHERE {W.Id} = @id",
            work.Connection, work.Transaction))
        {
            cmd.Parameters.AddWithValue("a", active);
            cmd.Parameters.AddWithValue("id", current.Id);
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("warehouse row not updated");
        }
        var changes = new JsonObject
        {
            ["isActive"] = new JsonObject { ["old"] = !active, ["new"] = active },
            ["activationEpoch"] = new JsonObject { ["old"] = current.ActivationEpoch, ["new"] = current.ActivationEpoch + 1 },
        };
        await RecordAsync(work, eventType, actor, current.Id, current.Revision + 1, changes, [.. changes.Select(c => c.Key)], label, ct);
        return (await LoadAsync(work.Connection, work.Transaction, current.Id, false, ct))!;
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private async Task<ConfigResult<WarehouseView>> MutateAsync(Guid companyId, Func<CompanyAuditedWork, Task<WarehouseView>> work, CancellationToken ct)
    {
        try { return ConfigResult<WarehouseView>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) { return abort.ToResult(); }
        // Phase 3.8B-3 (R12): PostgreSQL refuses deactivating a warehouse that holds an open negative-stock obligation
        catch (PostgresException ex) when (ex.MessageText == "negative_obligation_open") { return ConfigResult<WarehouseView>.Fail(ConfigStatus.Conflict, "negative_obligation_open"); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
        lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(NpgsqlCommand cmd, WarehouseData d)
    {
        cmd.Parameters.AddWithValue("ar", (object?)d.NameAr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("en", (object?)d.NameEn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a1", (object?)d.AddressLine1 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a2", (object?)d.AddressLine2 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("city", (object?)d.City ?? DBNull.Value);
        cmd.Parameters.AddWithValue("region", (object?)d.Region ?? DBNull.Value);
        cmd.Parameters.AddWithValue("postal", (object?)d.PostalCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("phone", (object?)d.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("email", (object?)d.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("web", (object?)d.Website ?? DBNull.Value);
    }

    private async Task<WarehouseView> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var current = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(null, ConfigStatus.NotFound, "warehouse_not_found");
        if (current.Revision != expectedRevision) throw new Abort(null, ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
        return current;
    }

    private static async Task<WarehouseView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {W.Table} WHERE {W.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // Columns: 0 id, 1 code, 2 ar, 3 en, 4 active, 5 branch, 6 epoch, 7-14 address/contact, 15 revision, 16 created, 17 updated.
    private static WarehouseView Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(5),
        new WarehouseData(r.GetString(1), S(r, 2), S(r, 3), S(r, 7), S(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), S(r, 13), S(r, 14)),
        r.GetBoolean(4), r.GetInt64(6), r.GetInt64(15), r.GetFieldValue<DateTimeOffset>(16), r.GetFieldValue<DateTimeOffset>(17));

    private static JsonObject Diff(WarehouseData? before, WarehouseData after)
    {
        var changes = new JsonObject();
        foreach (var f in Fields)
        {
            var o = before is null ? null : f.Get(before);
            var n = f.Get(after);
            if (string.Equals(o, n, StringComparison.Ordinal)) continue;
            changes[f.Name] = f.HistoryValues
                ? new JsonObject { ["old"] = o, ["new"] = n }
                : new JsonObject { ["changed"] = true };
        }
        return changes;
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();

    private async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, Guid warehouseId, long revision, JsonObject changes, IReadOnlyList<string> names, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', names)} h={Hash(json)}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {R.Table} ({R.WarehouseId}, {R.Revision}, {R.ActorUserId}, {R.ChangedAtUtc}, {R.Changes}, {R.AuditStream}, {R.AuditSeq}) " +
            "VALUES (@k, @r, @a, @t, @c, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("k", warehouseId);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey);
        cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
