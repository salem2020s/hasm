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
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using LS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerSettings;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Ledger settings (Phase 3.2B S5): today only the account that absorbs base-currency rounding differences of multi-currency entries. <b>No authorization here by design:</b>
/// <see cref="LedgerSettingsManagementService"/> wraps it. Audited, revisioned (If-Match), history entity 5.
/// </summary>
public sealed class LedgerSettingsService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48434C53;   // "HCLS"
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    public async Task<LedgerSettingsView> GetAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadAsync(conn, null, false, ct);
    }

    private static async Task<LedgerSettingsView> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {LS.Account}, {LS.Rev} FROM {LS.Table}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new LedgerSettingsView(r.IsDBNull(0) ? null : r.GetGuid(0), r.GetInt64(1), true) : new LedgerSettingsView(null, 0, false);
    }

    public async Task<ConfigResult<LedgerSettingsView>> PutAsync(Guid actor, Guid companyId, long? expectedRevision, LedgerSettingsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<LedgerSettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!JournalValidation.TryNormalizeSettings(input, out var errors)) return ConfigResult<LedgerSettingsView>.Invalid(errors);
        try
        {
            return ConfigResult<LedgerSettingsView>.Ok(await transactions.ExecuteAsync(companyId, async work =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
                { lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2); await lk.ExecuteNonQueryAsync(ct); }
                var cur = await ReadAsync(work.Connection, work.Transaction, true, ct);
                if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
                if (cur.Exists && cur.RoundingAccountId == input!.RoundingAccountId) throw new Abort(ConfigStatus.Ok, "unchanged");
                if (input!.RoundingAccountId is { } acc)
                {
                    // The rounding account must be an active postable account that accepts base-currency amounts.
                    await using var q = new NpgsqlCommand(
                        $"SELECT a.{AC.IsActive} AND NOT a.{AC.IsGroup} AND (a.{AC.CurrencyPolicy} IN (1, 2) OR a.{AC.CurrencyCode} = (SELECT {CC.Code} FROM {CC.Table} WHERE {CC.IsBase})) FROM {AC.Table} a WHERE a.{AC.Id} = @id", work.Connection, work.Transaction);
                    q.Parameters.AddWithValue("id", acc);
                    var ok = await q.ExecuteScalarAsync(ct);
                    if (ok is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("roundingAccountId", "account_not_found")]);
                    if (!(bool)ok) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("roundingAccountId", "account_not_postable")]);
                }
                var now = clock.GetUtcNow().UtcDateTime;
                await using (var w = new NpgsqlCommand(cur.Exists
                    ? $"UPDATE {LS.Table} SET {LS.Account} = @a, {LS.Rev} = {LS.Rev} + 1, {LS.Updated} = @now"
                    : $"INSERT INTO {LS.Table} ({LS.Singleton}, {LS.Account}, {LS.Rev}, {LS.Updated}) VALUES (true, @a, 1, @now)", work.Connection, work.Transaction))
                {
                    w.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)input.RoundingAccountId ?? DBNull.Value }); w.Parameters.AddWithValue("now", now);
                    await w.ExecuteNonQueryAsync(ct);
                }
                var revision = cur.Revision + 1;
                var changes = new JsonObject { ["roundingAccountId"] = new JsonObject { ["old"] = cur.RoundingAccountId?.ToString(), ["new"] = input.RoundingAccountId?.ToString() } };
                var json = changes.ToJsonString();
                var receipt = await work.AppendAuditAsync(new AuditEventData(LedgerAuditEvents.LedgerSettingsChanged, ActorUserId: actor,
                    Detail: $"ledger settings r{revision} f=roundingAccountId h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
                await using var h = new NpgsqlCommand(
                    $"INSERT INTO {LH.Table} ({LH.Entity}, {LH.EntityKey}, {LH.Revision}, {LH.ActorUserId}, {LH.ChangedAtUtc}, {LH.Changes}, {LH.AuditStream}, {LH.AuditSeq}) VALUES (5, 'settings', @r, @a, @t, @c, @s, @q)", work.Connection, work.Transaction);
                h.Parameters.AddWithValue("r", revision); h.Parameters.AddWithValue("a", actor); h.Parameters.AddWithValue("t", now);
                h.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
                h.Parameters.AddWithValue("s", receipt.StreamKey); h.Parameters.AddWithValue("q", receipt.Seq);
                await h.ExecuteNonQueryAsync(ct);
                return new LedgerSettingsView(input.RoundingAccountId, revision, true);
            }, ct));
        }
        catch (Abort a) when (a.Message == "unchanged") { return ConfigResult<LedgerSettingsView>.Ok(await GetAsync(companyId, ct)); }
        catch (Abort a) { return a.To<LedgerSettingsView>(); }
    }
}

/// <summary>Authorized entry point for ledger settings: view and configure are separate company-wide permissions.</summary>
public sealed class LedgerSettingsManagementService(AccessService access, LedgerSettingsService settings)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    public async Task<ConfigResult<LedgerSettingsView>> GetAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.LedgerSettingsView, ScopeTarget.Company, ct) ? ConfigResult<LedgerSettingsView>.Ok(await settings.GetAsync(companyId, ct)) : Denied<LedgerSettingsView>();

    public async Task<ConfigResult<LedgerSettingsView>> PutAsync(Guid userId, Guid companyId, long? revision, LedgerSettingsInput? input, CancellationToken ct = default) =>
        await access.IsAllowedAsync(userId, companyId, PermissionCatalog.Codes.LedgerSettingsConfigure, ScopeTarget.Company, ct) ? await settings.PutAsync(userId, companyId, revision, input, ct) : Denied<LedgerSettingsView>();
}
