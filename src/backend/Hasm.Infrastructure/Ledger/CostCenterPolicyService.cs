using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using ER = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenterBranchEligibility;
using PH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PolicyHistory;
using RR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountCostCenterRule;
using RB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenterBranchRule;

namespace Hasm.Infrastructure.Ledger;

/// <summary>An account's cost-center requirement. No row = optional (the default); <see cref="Revision"/> 0 means "never configured".</summary>
public sealed record AccountCostCenterRuleView(Guid AccountId, string Code, string? NameAr, string? NameEn, bool Required, long Revision);
public sealed record AccountCostCenterRuleInput(bool? Required);

/// <summary>A cost center's optional branch eligibility. <see cref="BranchIds"/> are the branches an allocation may use when <see cref="Restricted"/>; revision 0 = never configured.</summary>
public sealed record CostCenterBranchRuleView(Guid CostCenterId, bool Restricted, IReadOnlyList<Guid> BranchIds, long Revision);
public sealed record CostCenterBranchRuleInput(bool? Restricted, IReadOnlyList<Guid>? BranchIds);

/// <summary>Writes the append-only 3.2C policy history (entity 1 account cost-center rule, 2 branch eligibility, 3 approval stage policy, 4 closing settings, 5 fiscal-year closing) next to its audit event.</summary>
internal static class PolicyHistoryWriter
{
    public static async Task RecordAsync(CompanyAuditedWork work, TimeProvider clock, short eventType, short entity, string key, Guid actor, long revision, JsonObject changes, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', changes.Select(c => c.Key))} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {PH.Table} ({PH.Entity}, {PH.EntityKey}, {PH.Revision}, {PH.ActorUserId}, {PH.ChangedAtUtc}, {PH.Changes}, {PH.AuditStream}, {PH.AuditSeq}) VALUES (@e, @k, @r, @a, @t, @c, @s, @q)",
            work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("e", entity); cmd.Parameters.AddWithValue("k", key); cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey); cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Cost-center accounting policies (Phase 3.2C S1). <b>No authorization here by design:</b> <see cref="CostCenterPolicyManagementService"/> wraps it. Cost centers are OPTIONAL by default;
/// a company may (1) require a cost-center allocation on every line posted to particular accounts and (2) restrict a cost center to particular branches. Both are enforced for submission
/// and posting by PostgreSQL (company migration 0009), so a direct write cannot bypass them; this service only maintains the rules (revisioned, audited, never deleted).
/// </summary>
public sealed class CostCenterPolicyService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48435050;   // "HCPP": advisory-lock namespace of cost-center policy changes
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    // ------------------------------------------------------------------------------------------------------------- reads

    /// <summary>Every account that currently REQUIRES a cost center.</summary>
    public async Task<IReadOnlyList<AccountCostCenterRuleView>> ListRequiredAccountsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT a.{AC.Id}, a.{AC.Code}, a.{AC.NameAr}, a.{AC.NameEn}, r.{RR.IsRequired}, r.{RR.Revision} FROM {RR.Table} r JOIN {AC.Table} a ON a.{AC.Id} = r.{RR.AccountId} WHERE r.{RR.IsRequired} ORDER BY upper(a.{AC.Code})", conn);
        var list = new List<AccountCostCenterRuleView>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new(rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetBoolean(4), rd.GetInt64(5)));
        return list;
    }

    public async Task<AccountCostCenterRuleView?> GetAccountRuleAsync(Guid companyId, Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAccountRuleAsync(conn, null, accountId, ct);
    }

    private static async Task<AccountCostCenterRuleView?> LoadAccountRuleAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT a.{AC.Id}, a.{AC.Code}, a.{AC.NameAr}, a.{AC.NameEn}, COALESCE(r.{RR.IsRequired}, false), COALESCE(r.{RR.Revision}, 0) FROM {AC.Table} a LEFT JOIN {RR.Table} r ON r.{RR.AccountId} = a.{AC.Id} WHERE a.{AC.Id} = @a", conn, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new(rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetBoolean(4), rd.GetInt64(5)) : null;
    }

    public async Task<CostCenterBranchRuleView?> GetBranchRuleAsync(Guid companyId, Guid costCenterId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadBranchRuleAsync(conn, null, costCenterId, ct);
    }

    /// <summary>Every cost center that is restricted to branches (for the line-level checks of the UI).</summary>
    public async Task<IReadOnlyList<CostCenterBranchRuleView>> ListRestrictedAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT {RB.CostCenterId} FROM {RB.Table} WHERE {RB.IsRestricted}", conn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        var list = new List<CostCenterBranchRuleView>();
        foreach (var id in ids) if (await LoadBranchRuleAsync(conn, null, id, ct) is { } v) list.Add(v);
        return list;
    }

    private static async Task<CostCenterBranchRuleView?> LoadBranchRuleAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid costCenterId, CancellationToken ct)
    {
        await using (var exists = new NpgsqlCommand($"SELECT 1 FROM {CC.Table} WHERE {CC.Id} = @c", conn, tx))
        {
            exists.Parameters.AddWithValue("c", costCenterId);
            if (await exists.ExecuteScalarAsync(ct) is null) return null;
        }
        bool restricted = false; long revision = 0;
        await using (var cmd = new NpgsqlCommand($"SELECT {RB.IsRestricted}, {RB.Revision} FROM {RB.Table} WHERE {RB.CostCenterId} = @c", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", costCenterId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { restricted = rd.GetBoolean(0); revision = rd.GetInt64(1); }
        }
        var branches = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ER.BranchId} FROM {ER.Table} WHERE {ER.CostCenterId} = @c AND {ER.IsEligible} ORDER BY {ER.BranchId}", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", costCenterId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) branches.Add(rd.GetGuid(0));
        }
        return new CostCenterBranchRuleView(costCenterId, restricted, branches, revision);
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    /// <summary>Requires (or stops requiring) a cost center on an account. <paramref name="expectedRevision"/> is the rule revision the caller saw (0 = never configured).</summary>
    public async Task<ConfigResult<AccountCostCenterRuleView>> SetAccountRuleAsync(Guid actor, Guid companyId, Guid accountId, long? expectedRevision, AccountCostCenterRuleInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<AccountCostCenterRuleView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input?.Required is null) return ConfigResult<AccountCostCenterRuleView>.Invalid([new("required", "required")]);
        var required = input.Required.Value;
        return await MutateAsync(async () => await transactions.ExecuteAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await LoadAccountRuleAsync(work.Connection, work.Transaction, accountId, ct) ?? throw new Abort(ConfigStatus.NotFound, "account_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Required == required) throw new Abort(ConfigStatus.Ok, "unchanged");
            await using (var q = new NpgsqlCommand($"SELECT {AC.IsGroup} FROM {AC.Table} WHERE {AC.Id} = @a", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("a", accountId);
                if ((bool)(await q.ExecuteScalarAsync(ct))!) throw new Abort(ConfigStatus.Conflict, "account_is_group");
            }
            var now = clock.GetUtcNow().UtcDateTime;
            var newRevision = cur.Revision + 1;
            await using (var w = new NpgsqlCommand(cur.Revision == 0
                ? $"INSERT INTO {RR.Table} ({RR.AccountId}, {RR.IsRequired}, {RR.Revision}, {RR.CreatedAtUtc}, {RR.UpdatedAtUtc}) VALUES (@a, @r, 1, @t, @t)"
                : $"UPDATE {RR.Table} SET {RR.IsRequired} = @r, {RR.Revision} = {RR.Revision} + 1, {RR.UpdatedAtUtc} = @t WHERE {RR.AccountId} = @a", work.Connection, work.Transaction))
            {
                w.Parameters.AddWithValue("a", accountId); w.Parameters.AddWithValue("r", required); w.Parameters.AddWithValue("t", now);
                await w.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject { ["required"] = new JsonObject { ["old"] = cur.Required.ToString(), ["new"] = required.ToString() } };
            await PolicyHistoryWriter.RecordAsync(work, clock, LedgerAuditEvents.AccountCostCenterRuleChanged, 1, accountId.ToString(), actor, newRevision, changes, "account cost-center rule changed", ct);
            return (await LoadAccountRuleAsync(work.Connection, work.Transaction, accountId, ct))!;
        }, ct), async () => (await GetAccountRuleAsync(companyId, accountId, ct))!);
    }

    /// <summary>Replaces the branch eligibility of a leaf cost center (restricted or not, and the eligible active branches).</summary>
    public async Task<ConfigResult<CostCenterBranchRuleView>> SetBranchRuleAsync(Guid actor, Guid companyId, Guid costCenterId, long? expectedRevision, CostCenterBranchRuleInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CostCenterBranchRuleView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input?.Restricted is null) return ConfigResult<CostCenterBranchRuleView>.Invalid([new("restricted", "required")]);
        var restricted = input.Restricted.Value;
        var branchIds = (input.BranchIds ?? []).Distinct().OrderBy(b => b).ToList();
        if (branchIds.Count > 500) return ConfigResult<CostCenterBranchRuleView>.Invalid([new("branchIds", "too_many")]);
        if (restricted && branchIds.Count == 0) return ConfigResult<CostCenterBranchRuleView>.Invalid([new("branchIds", "branches_required")]);
        return await MutateAsync(async () => await transactions.ExecuteAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await LoadBranchRuleAsync(work.Connection, work.Transaction, costCenterId, ct) ?? throw new Abort(ConfigStatus.NotFound, "cost_center_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            await using (var q = new NpgsqlCommand($"SELECT {CC.IsGroup} FROM {CC.Table} WHERE {CC.Id} = @c", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("c", costCenterId);
                if ((bool)(await q.ExecuteScalarAsync(ct))!) throw new Abort(ConfigStatus.Conflict, "cost_center_is_group");
            }
            foreach (var b in branchIds)
            {
                await using var q = new NpgsqlCommand($"SELECT {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = @b", work.Connection, work.Transaction);
                q.Parameters.AddWithValue("b", b);
                var v = await q.ExecuteScalarAsync(ct);
                if (v is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchIds", "branch_not_found")]);
                if (!(bool)v && !cur.BranchIds.Contains(b)) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchIds", "branch_inactive")]);
            }
            var same = cur.Restricted == restricted && cur.BranchIds.OrderBy(b => b).SequenceEqual(branchIds);
            if (same) throw new Abort(ConfigStatus.Ok, "unchanged");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var w = new NpgsqlCommand(cur.Revision == 0
                ? $"INSERT INTO {RB.Table} ({RB.CostCenterId}, {RB.IsRestricted}, {RB.Revision}, {RB.CreatedAtUtc}, {RB.UpdatedAtUtc}) VALUES (@c, @r, 1, @t, @t)"
                : $"UPDATE {RB.Table} SET {RB.IsRestricted} = @r, {RB.Revision} = {RB.Revision} + 1, {RB.UpdatedAtUtc} = @t WHERE {RB.CostCenterId} = @c", work.Connection, work.Transaction))
            {
                w.Parameters.AddWithValue("c", costCenterId); w.Parameters.AddWithValue("r", restricted); w.Parameters.AddWithValue("t", now);
                await w.ExecuteNonQueryAsync(ct);
            }
            // The eligible set: listed branches become/stay eligible, every other existing row switches off (rows are never deleted).
            await using (var off = new NpgsqlCommand($"UPDATE {ER.Table} SET {ER.IsEligible} = false, {ER.UpdatedAtUtc} = @t WHERE {ER.CostCenterId} = @c AND {ER.IsEligible} AND NOT ({ER.BranchId} = ANY(@b))", work.Connection, work.Transaction))
            {
                off.Parameters.AddWithValue("c", costCenterId); off.Parameters.AddWithValue("t", now);
                off.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = branchIds.ToArray() });
                await off.ExecuteNonQueryAsync(ct);
            }
            foreach (var b in branchIds)
            {
                await using var on = new NpgsqlCommand(
                    $"INSERT INTO {ER.Table} ({ER.CostCenterId}, {ER.BranchId}, {ER.IsEligible}, {ER.UpdatedAtUtc}) VALUES (@c, @b, true, @t) ON CONFLICT ({ER.CostCenterId}, {ER.BranchId}) DO UPDATE SET {ER.IsEligible} = true, {ER.UpdatedAtUtc} = @t", work.Connection, work.Transaction);
                on.Parameters.AddWithValue("c", costCenterId); on.Parameters.AddWithValue("b", b); on.Parameters.AddWithValue("t", now);
                await on.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject
            {
                ["restricted"] = new JsonObject { ["old"] = cur.Restricted.ToString(), ["new"] = restricted.ToString() },
                ["branches"] = new JsonObject { ["old"] = string.Join(',', cur.BranchIds.Order()), ["new"] = string.Join(',', branchIds) },
            };
            await PolicyHistoryWriter.RecordAsync(work, clock, LedgerAuditEvents.CostCenterBranchEligibilityChanged, 2, costCenterId.ToString(), actor, cur.Revision + 1, changes, "cost-center branch eligibility changed", ct);
            return (await LoadBranchRuleAsync(work.Connection, work.Transaction, costCenterId, ct))!;
        }, ct), async () => (await GetBranchRuleAsync(companyId, costCenterId, ct))!);
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private static async Task<ConfigResult<T>> MutateAsync<T>(Func<Task<T>> run, Func<Task<T>> onUnchanged) where T : class
    {
        try { return ConfigResult<T>.Ok(await run()); }
        catch (Abort abort) when (abort.Message == "unchanged") { return ConfigResult<T>.Ok(await onUnchanged()); }
        catch (Abort abort) { return abort.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return ConfigResult<T>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict"); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Authorized entry point for the cost-center policies. Company-wide permissions (<c>company.costcenterpolicies.view|configure</c>), re-read on every call; a branch or cost-center
/// grant never reaches configuration. Cost-center-scoped users read the allowable cost centers through the journal lookup, not through this service.
/// </summary>
public sealed class CostCenterPolicyManagementService(AccessService access, CostCenterPolicyService policies)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<IReadOnlyList<AccountCostCenterRuleView>>> ListRequiredAccountsAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCenterPoliciesView, ct) ? ConfigResult<IReadOnlyList<AccountCostCenterRuleView>>.Ok(await policies.ListRequiredAccountsAsync(companyId, ct)) : Denied<IReadOnlyList<AccountCostCenterRuleView>>();

    public async Task<ConfigResult<AccountCostCenterRuleView>> GetAccountRuleAsync(Guid userId, Guid companyId, Guid accountId, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.CostCenterPoliciesView, ct)) return Denied<AccountCostCenterRuleView>();
        var v = await policies.GetAccountRuleAsync(companyId, accountId, ct);
        return v is null ? ConfigResult<AccountCostCenterRuleView>.Fail(ConfigStatus.NotFound, "account_not_found") : ConfigResult<AccountCostCenterRuleView>.Ok(v);
    }

    public async Task<ConfigResult<AccountCostCenterRuleView>> SetAccountRuleAsync(Guid userId, Guid companyId, Guid accountId, long? revision, AccountCostCenterRuleInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCenterPoliciesConfigure, ct) ? await policies.SetAccountRuleAsync(userId, companyId, accountId, revision, input, ct) : Denied<AccountCostCenterRuleView>();

    public async Task<ConfigResult<IReadOnlyList<CostCenterBranchRuleView>>> ListRestrictedAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCenterPoliciesView, ct) ? ConfigResult<IReadOnlyList<CostCenterBranchRuleView>>.Ok(await policies.ListRestrictedAsync(companyId, ct)) : Denied<IReadOnlyList<CostCenterBranchRuleView>>();

    public async Task<ConfigResult<CostCenterBranchRuleView>> GetBranchRuleAsync(Guid userId, Guid companyId, Guid costCenterId, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.CostCenterPoliciesView, ct)) return Denied<CostCenterBranchRuleView>();
        var v = await policies.GetBranchRuleAsync(companyId, costCenterId, ct);
        return v is null ? ConfigResult<CostCenterBranchRuleView>.Fail(ConfigStatus.NotFound, "cost_center_not_found") : ConfigResult<CostCenterBranchRuleView>.Ok(v);
    }

    public async Task<ConfigResult<CostCenterBranchRuleView>> SetBranchRuleAsync(Guid userId, Guid companyId, Guid costCenterId, long? revision, CostCenterBranchRuleInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.CostCenterPoliciesConfigure, ct) ? await policies.SetBranchRuleAsync(userId, companyId, costCenterId, revision, input, ct) : Denied<CostCenterBranchRuleView>();
}
