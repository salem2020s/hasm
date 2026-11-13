using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using R = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BranchRevision;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Branch domain operations (Phase 3.1B, Checkpoint B). <b>No authorization here by design:</b> the permission checks, the API and the
/// branch-scoped RBAC arrive with Checkpoint C and wrap this service; callers must not expose it without them.
/// Every mutation runs in <see cref="CompanyAuditedTransactions"/>: the change, its append-only <c>ConfigRevision</c> row and its authenticated
/// audit event commit or roll back together, after one transaction-level advisory lock that serialises ALL branch-structure changes of the
/// company (taken before the audit-stream lock, as the lock order requires). PostgreSQL independently enforces the invariants (see migration 0004).
/// <list type="bullet">
/// <item>Exactly one active main branch: a reassignment unsets the old and sets the new main in ONE transaction; a failure leaves the previous main unchanged.</item>
/// <item>The main branch cannot be deactivated unless another ACTIVE branch becomes main in the same transaction.</item>
/// <item>Trial limit: counted over every branch ever created (inactive included); rows are never deleted.</item>
/// <item>ActivationEpoch advances on every deactivation and reactivation.</item>
/// </list>
/// Sensitive fields (address lines, postal code, phone, email, website) are recorded as "changed" only, never their values.
/// </summary>
public sealed class CompanyBranchService(
    CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, IEntitlementPolicy entitlements, TimeProvider clock)
{
    private const int LockKey1 = 0x48425243;   // "HBRC": advisory-lock namespace of branch structure
    private const int LockKey2 = 1;

    private static readonly string Columns =
        $"{B.Id}, {B.Code}, {B.NameAr}, {B.NameEn}, {B.IsActive}, {B.IsMain}, {B.ActivationEpoch}, {B.Address1}, {B.Address2}, {B.City}, {B.Region}, " +
        $"{B.PostalCode}, {B.Phone}, {B.Email}, {B.Website}, {B.SeedMigration}, {B.Revision}, {B.CreatedAtUtc}, {B.UpdatedAtUtc}";

    private sealed record Field(string Name, bool HistoryValues, Func<BranchData, string?> Get);

    private static readonly IReadOnlyList<Field> Fields =
    [
        new("nameAr", true, d => d.NameAr), new("nameEn", true, d => d.NameEn),
        new("addressLine1", false, d => d.AddressLine1), new("addressLine2", false, d => d.AddressLine2),
        new("city", true, d => d.City), new("region", true, d => d.Region), new("postalCode", false, d => d.PostalCode),
        new("phone", false, d => d.Phone), new("email", false, d => d.Email), new("website", false, d => d.Website),
    ];

    private sealed class Abort(ConfigResult<BranchView>? result, ConfigStatus status, string code, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<BranchView> ToResult() => result ?? ConfigResult<BranchView>.Fail(status, code, currentRevision);
    }

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<IReadOnlyList<BranchView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {B.Table} ORDER BY {B.IsMain} DESC, {B.Code}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<BranchView>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<BranchView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, false, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    public async Task<ConfigResult<BranchView>> CreateAsync(Guid actor, Guid companyId, BranchInput? input, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeBranch(input, null, out var data, out var errors)) return ConfigResult<BranchView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            // Trial/edition limit over EVERY branch ever created (rows are never deleted, so inactive branches keep counting).
            await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {B.Table}", work.Connection, work.Transaction))
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= entitlements.MaxBranches)
                    throw new Abort(null, ConfigStatus.EntitlementDenied, "entitlement_denied");
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO {B.Table} ({B.Id}, {B.Code}, {B.NameAr}, {B.NameEn}, {B.IsActive}, {B.IsMain}, {B.ActivationEpoch}, {B.Address1}, {B.Address2}, {B.City}, {B.Region}, " +
                    $"{B.PostalCode}, {B.Phone}, {B.Email}, {B.Website}, {B.Revision}, {B.CreatedAtUtc}, {B.UpdatedAtUtc}) " +
                    "VALUES (@id, @code, @ar, @en, true, false, 1, @a1, @a2, @city, @region, @postal, @phone, @email, @web, 1, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("code", data.Code);
                Bind(ins, data);
                ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new Abort(null, ConfigStatus.Conflict, "branch_code_taken");
            }
            var changes = Diff(null, data);
            changes["code"] = new JsonObject { ["old"] = null, ["new"] = data.Code };
            await RecordAsync(work, ConfigAuditEvents.BranchCreated, actor, id, 1, changes, [.. changes.Select(c => c.Key)], "branch created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public async Task<ConfigResult<BranchView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, BranchInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<BranchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!ConfigurationValidation.TryNormalizeBranch(input, current.Data.Code, out var data, out var errors))
                throw new Abort(ConfigResult<BranchView>.Invalid(errors), ConfigStatus.Invalid, "validation_failed");
            var changes = Diff(current.Data, data);
            if (changes.Count == 0) throw new Abort(ConfigResult<BranchView>.Ok(current), ConfigStatus.Ok, "unchanged");   // nothing to audit, nothing written
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {B.Table} SET {B.NameAr} = @ar, {B.NameEn} = @en, {B.Address1} = @a1, {B.Address2} = @a2, {B.City} = @city, {B.Region} = @region, " +
                $"{B.PostalCode} = @postal, {B.Phone} = @phone, {B.Email} = @email, {B.Website} = @web, {B.Revision} = {B.Revision} + 1, {B.UpdatedAtUtc} = @now WHERE {B.Id} = @id",
                work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id);
                Bind(upd, data);
                upd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await RecordAsync(work, ConfigAuditEvents.BranchUpdated, actor, id, current.Revision + 1, changes, [.. changes.Select(c => c.Key)], "branch updated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>
    /// Deactivates a branch (epoch +1). The main branch can only be deactivated together with the designation of <paramref name="newMainId"/>,
    /// another ACTIVE branch, in the same transaction; otherwise it is refused (<c>main_branch_deactivation_denied</c>).
    /// </summary>
    public async Task<ConfigResult<BranchView>> DeactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, Guid? newMainId = null, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<BranchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!current.IsActive) throw new Abort(null, ConfigStatus.Conflict, "branch_already_inactive");
            BranchView? successor = null;
            if (current.IsMain)
            {
                if (newMainId is null || newMainId == id) throw new Abort(null, ConfigStatus.Conflict, "main_branch_deactivation_denied");
                successor = await LoadAsync(work.Connection, work.Transaction, newMainId.Value, true, ct);
                if (successor is null) throw new Abort(null, ConfigStatus.Conflict, "branch_not_found");
                if (!successor.IsActive) throw new Abort(null, ConfigStatus.Conflict, "branch_inactive");
            }
            var now = clock.GetUtcNow().UtcDateTime;
            // The old main loses the role in the SAME statement that deactivates it (a main branch is always active); then the successor is set.
            await ExecAsync(work, $"UPDATE {B.Table} SET {B.IsActive} = false, {B.IsMain} = false, {B.ActivationEpoch} = {B.ActivationEpoch} + 1, {B.Revision} = {B.Revision} + 1, {B.UpdatedAtUtc} = @now WHERE {B.Id} = @id",
                ("id", id), ("now", now));
            if (successor is not null)
                await ExecAsync(work, $"UPDATE {B.Table} SET {B.IsMain} = true, {B.Revision} = {B.Revision} + 1, {B.UpdatedAtUtc} = @now WHERE {B.Id} = @id", ("id", successor.Id), ("now", now));
            var changes = new JsonObject
            {
                ["isActive"] = new JsonObject { ["old"] = true, ["new"] = false },
                ["activationEpoch"] = new JsonObject { ["old"] = current.ActivationEpoch, ["new"] = current.ActivationEpoch + 1 },
            };
            if (current.IsMain) changes["isMain"] = new JsonObject { ["old"] = true, ["new"] = false };
            await RecordAsync(work, ConfigAuditEvents.BranchDeactivated, actor, id, current.Revision + 1, changes, [.. changes.Select(c => c.Key)], "branch deactivated", ct);
            if (successor is not null)
                await RecordAsync(work, ConfigAuditEvents.BranchMainDesignated, actor, successor.Id, successor.Revision + 1,
                    new JsonObject { ["isMain"] = new JsonObject { ["old"] = false, ["new"] = true } }, ["isMain"], "main branch designated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>Reactivates a branch (epoch +1). Branch-scoped access assignments recorded under an earlier epoch never become effective again.</summary>
    public async Task<ConfigResult<BranchView>> ReactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<BranchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (current.IsActive) throw new Abort(null, ConfigStatus.Conflict, "branch_already_active");
            await ExecAsync(work, $"UPDATE {B.Table} SET {B.IsActive} = true, {B.ActivationEpoch} = {B.ActivationEpoch} + 1, {B.Revision} = {B.Revision} + 1, {B.UpdatedAtUtc} = @now WHERE {B.Id} = @id",
                ("id", id), ("now", clock.GetUtcNow().UtcDateTime));
            var changes = new JsonObject
            {
                ["isActive"] = new JsonObject { ["old"] = false, ["new"] = true },
                ["activationEpoch"] = new JsonObject { ["old"] = current.ActivationEpoch, ["new"] = current.ActivationEpoch + 1 },
            };
            await RecordAsync(work, ConfigAuditEvents.BranchReactivated, actor, id, current.Revision + 1, changes, [.. changes.Select(c => c.Key)], "branch reactivated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>Atomically designates an active branch as the main branch; the previous main is unset in the same transaction.</summary>
    public async Task<ConfigResult<BranchView>> MakeMainAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<BranchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var target = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!target.IsActive) throw new Abort(null, ConfigStatus.Conflict, "branch_inactive");
            if (target.IsMain) throw new Abort(null, ConfigStatus.Conflict, "branch_already_main");
            Guid oldMainId; long oldRevision;
            await using (var q = new NpgsqlCommand($"SELECT {B.Id}, {B.Revision} FROM {B.Table} WHERE {B.IsMain} FOR UPDATE", work.Connection, work.Transaction))
            await using (var r = await q.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw new InvalidOperationException("branch invariant violated: no main branch");
                oldMainId = r.GetGuid(0); oldRevision = r.GetInt64(1);
            }
            var now = clock.GetUtcNow().UtcDateTime;
            await ExecAsync(work, $"UPDATE {B.Table} SET {B.IsMain} = false, {B.Revision} = {B.Revision} + 1, {B.UpdatedAtUtc} = @now WHERE {B.Id} = @id", ("id", oldMainId), ("now", now));
            await ExecAsync(work, $"UPDATE {B.Table} SET {B.IsMain} = true, {B.Revision} = {B.Revision} + 1, {B.UpdatedAtUtc} = @now WHERE {B.Id} = @id", ("id", id), ("now", now));
            await RecordAsync(work, ConfigAuditEvents.BranchMainRevoked, actor, oldMainId, oldRevision + 1,
                new JsonObject { ["isMain"] = new JsonObject { ["old"] = true, ["new"] = false } }, ["isMain"], "main branch revoked", ct);
            await RecordAsync(work, ConfigAuditEvents.BranchMainDesignated, actor, id, target.Revision + 1,
                new JsonObject { ["isMain"] = new JsonObject { ["old"] = false, ["new"] = true } }, ["isMain"], "main branch designated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private async Task<ConfigResult<BranchView>> MutateAsync(Guid companyId, Func<CompanyAuditedWork, Task<BranchView>> work, CancellationToken ct)
    {
        try { return ConfigResult<BranchView>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) { return abort.ToResult(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1);
        lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(CompanyAuditedWork work, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, work.Connection, work.Transaction);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("branch row not updated");
    }

    private static void Bind(NpgsqlCommand cmd, BranchData d)
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

    /// <summary>Loads the row (locked when it is going to be changed) and applies the optimistic-concurrency check.</summary>
    private async Task<BranchView> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var current = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(null, ConfigStatus.Conflict, "branch_not_found");
        if (current.Revision != expectedRevision) throw new Abort(null, ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
        return current;
    }

    private static async Task<BranchView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {B.Table} WHERE {B.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static BranchView Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        new BranchData(r.GetString(1), S(r, 2), S(r, 3), S(r, 7), S(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), S(r, 13), S(r, 14)),
        r.GetBoolean(4), r.GetBoolean(5), r.GetInt64(6), r.IsDBNull(15) ? null : r.GetInt16(15), r.GetInt64(16),
        r.GetFieldValue<DateTimeOffset>(17), r.GetFieldValue<DateTimeOffset>(18));

    /// <summary>Field-level before/after for permitted fields; sensitive fields record only that they changed.</summary>
    private static JsonObject Diff(BranchData? before, BranchData after)
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

    /// <summary>Appends the authenticated audit event and the linked append-only revision row, inside the caller's transaction.</summary>
    private async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, Guid branchId, long revision, JsonObject changes, IReadOnlyList<string> names, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', names)} h={Hash(json)}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {R.Table} ({R.BranchId}, {R.Revision}, {R.ActorUserId}, {R.ChangedAtUtc}, {R.Changes}, {R.AuditStream}, {R.AuditSeq}) " +
            "VALUES (@k, @r, @a, @t, @c, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("k", branchId);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey);
        cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
