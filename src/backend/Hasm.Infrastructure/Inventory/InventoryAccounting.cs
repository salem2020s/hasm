using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using V = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingVersion;
using VG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingGroup;
using G = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ItemGroup;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;

namespace Hasm.Infrastructure.Inventory;

/// <summary>Audit event of the inventory accounting configuration (1160 of the business stream).</summary>
public static class InventoryAccountingAuditEvents { public const short Saved = 1160; }

public sealed record InventoryAccountingGroupInput(Guid? GroupId, Guid? AccountId);
/// <summary>The complete configuration (every save is a full, immutable version). A null account means "not configured": nothing can be posted for it.
/// Phase 3.8B-2: <see cref="AdjustmentCounterAccountId"/> takes the other side of stock adjustments (stocktake shortages and surpluses); optional, but nothing with value can be adjusted until it is set.</summary>
public sealed record InventoryAccountingInput(Guid? DefaultInventoryAccountId, Guid? ReceiptCounterAccountId, Guid? IssueCounterAccountId, IReadOnlyList<InventoryAccountingGroupInput>? Groups, string? Note, Guid? AdjustmentCounterAccountId = null);
public sealed record InventoryAccountingVersionView(Guid Id, int VersionNo, DateTimeOffset CreatedAtUtc, Guid CreatedBy, string? Note, int GroupCount, string ContentHash);
public sealed record InventoryAccountingView(InventoryAccountingVersionView? Version, Guid? DefaultInventoryAccountId, Guid? ReceiptCounterAccountId, Guid? IssueCounterAccountId, IReadOnlyDictionary<Guid, Guid> GroupAccounts, Guid? AdjustmentCounterAccountId = null);
public sealed record InventoryAccountRef(Guid Id, string Code, string? NameAr, string? NameEn, int Type);
public sealed record InventoryAccountingLookups(IReadOnlyList<InventoryAccountRef> Accounts, IReadOnlyList<(Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId)> Groups);

/// <summary>The accounts a posting would use for one item group under one configuration version.</summary>
public sealed record ResolvedInventoryAccounts(int VersionNo, Guid VersionId, Guid InventoryAccountId, Guid? ReceiptCounterAccountId, Guid? IssueCounterAccountId, Guid? AdjustmentCounterAccountId = null);

/// <summary>Pure resolution of the inventory account of a group: the nearest group on the chain (the group itself first, then its parents) with a mapping, otherwise the company default; null when none is configured.</summary>
public static class InventoryAccountResolver
{
    public static Guid? Resolve(IReadOnlyDictionary<Guid, Guid> groupAccounts, IEnumerable<Guid> chain, Guid? companyDefault)
    {
        foreach (var g in chain) if (groupAccounts.TryGetValue(g, out var account)) return account;
        return companyDefault;
    }
}

/// <summary>
/// The versioned company configuration of the inventory accounts (Phase 3.7A). <b>No authorization here by design:</b> <see cref="InventoryAccountingManagementService"/> wraps it. Every save creates a new IMMUTABLE version
/// holding the complete mapping (audited, optimistic concurrency by version number, serialised by an advisory lock); an unchanged configuration creates nothing; earlier versions are never touched, so a posting can always
/// be traced to the exact configuration it used. Nothing is hard-coded or seeded: an unconfigured company simply cannot post.
/// Validation against the LIVE chart at save time: inventory accounts must be active, postable (not group) ASSET accounts; counter accounts active and postable and never an inventory account of the same version;
/// no account may be limited to a foreign currency (stock is valued in the base currency). The posting slice validates again at posting time (the chart may have changed since).
/// </summary>
public sealed class InventoryAccountingService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48494143;   // "HIAC"
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? current = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, current);
    }
    private sealed class Unchanged(InventoryAccountingView view) : Exception("unchanged") { public InventoryAccountingView View { get; } = view; }

    private static string Cols => $"{V.Id}, {V.VersionNo}, {V.CreatedAtUtc}, {V.CreatedBy}, {V.Note}, {V.DefaultAccountId}, {V.ReceiptCounterAccountId}, {V.IssueCounterAccountId}, {V.GroupCount}, {V.ContentHash}, {V.AdjustmentCounterAccountId}";

    public static async Task<InventoryAccountingView> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int? versionNo, CancellationToken ct)
    {
        InventoryAccountingVersionView? version = null; Guid? def = null, receipt = null, issue = null, adjustment = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {Cols} FROM {V.Table} " + (versionNo is null ? $"ORDER BY {V.VersionNo} DESC LIMIT 1" : $"WHERE {V.VersionNo} = @n"), conn, tx))
        {
            if (versionNo is not null) cmd.Parameters.AddWithValue("n", versionNo.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                version = new InventoryAccountingVersionView(r.GetGuid(0), r.GetInt32(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(8), r.GetString(9).Trim());
                def = r.IsDBNull(5) ? null : r.GetGuid(5); receipt = r.IsDBNull(6) ? null : r.GetGuid(6); issue = r.IsDBNull(7) ? null : r.GetGuid(7); adjustment = r.IsDBNull(10) ? null : r.GetGuid(10);
            }
        }
        var groups = new Dictionary<Guid, Guid>();
        if (version is not null)
        {
            await using var cmd = new NpgsqlCommand($"SELECT {VG.GroupId}, {VG.AccountId} FROM {VG.Table} WHERE {VG.VersionId} = @v", conn, tx);
            cmd.Parameters.AddWithValue("v", version.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) groups[r.GetGuid(0)] = r.GetGuid(1);
        }
        return new InventoryAccountingView(version, def, receipt, issue, groups, adjustment);
    }

    public async Task<InventoryAccountingView> GetAsync(Guid companyId, int? versionNo = null, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadAsync(conn, null, versionNo, ct);
    }

    public async Task<IReadOnlyList<InventoryAccountingVersionView>> ListVersionsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM {V.Table} ORDER BY {V.VersionNo} DESC", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<InventoryAccountingVersionView>();
        while (await r.ReadAsync(ct)) list.Add(new InventoryAccountingVersionView(r.GetGuid(0), r.GetInt32(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(8), r.GetString(9).Trim()));
        return list;
    }

    /// <summary>The accounts and groups a configuration screen may pick: active postable accounts and all item groups.</summary>
    public async Task<InventoryAccountingLookups> LookupsAsync(Guid companyId, IReadOnlyCollection<Guid>? alsoInclude = null, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var accounts = new List<InventoryAccountRef>();
        await using (var q = new NpgsqlCommand($"SELECT {A.Id}, {A.Code}, {A.NameAr}, {A.NameEn}, {A.Type} FROM {A.Table} WHERE ({A.IsActive} AND NOT {A.IsGroup}) OR {A.Id} = ANY(@extra) ORDER BY {A.Code}", conn))
        {
            q.Parameters.Add(new NpgsqlParameter("extra", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = (alsoInclude ?? []).ToArray() });
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) accounts.Add(new InventoryAccountRef(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt16(4)));
        }
        var groups = new List<(Guid, string, string?, string?, Guid?)>();
        await using (var q = new NpgsqlCommand($"SELECT {G.Id}, {G.Code}, {G.NameAr}, {G.NameEn}, {G.ParentId} FROM {G.Table} ORDER BY {G.Code}", conn))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) groups.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4)));
        return new InventoryAccountingLookups(accounts, groups);
    }

    private sealed record AccountRow(Guid Id, int Type, bool IsGroup, bool IsActive, int Policy, string? Currency);

    private static async Task<Dictionary<Guid, AccountRow>> AccountsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var map = new Dictionary<Guid, AccountRow>();
        await using var q = new NpgsqlCommand($"SELECT {A.Id}, {A.Type}, {A.IsGroup}, {A.IsActive}, {A.CurrencyPolicy}, {A.CurrencyCode} FROM {A.Table} WHERE {A.Id} = ANY(@ids) FOR SHARE", conn, tx);
        q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.Distinct().ToArray() });
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetGuid(0)] = new AccountRow(r.GetGuid(0), r.GetInt16(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt16(4), r.IsDBNull(5) ? null : r.GetString(5).Trim());
        return map;
    }

    /// <summary>The adjustment counter account (3.8B-2) enters the hash only when it is set, so the hash of every configuration without one is exactly what it was before (an unchanged save stays a no-op).</summary>
    private static string Hash(InventoryAccountingInput n, IReadOnlyDictionary<Guid, Guid> groups) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{n.DefaultInventoryAccountId}|{n.ReceiptCounterAccountId}|{n.IssueCounterAccountId}|" + string.Join(";", groups.OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Value}"))
            + (n.AdjustmentCounterAccountId is { } adj ? $"|adjustment:{adj}" : "")))).ToLowerInvariant();

    public async Task<ConfigResult<InventoryAccountingView>> SaveAsync(Guid actor, Guid companyId, long? expectedVersion, InventoryAccountingInput? input, CancellationToken ct = default)
    {
        if (expectedVersion is null) return ConfigResult<InventoryAccountingView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<InventoryAccountingView>.Invalid([new("body", "required")]);
        var errors = new List<FieldError>();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note is { Length: > 500 }) errors.Add(new("note", "too_long"));
        var groups = new Dictionary<Guid, Guid>();
        var supplied = input.Groups ?? [];
        for (var i = 0; i < supplied.Count; i++)
        {
            var g = supplied[i];
            if (g.GroupId is not { } gid || gid == Guid.Empty) { errors.Add(new($"groups[{i}].groupId", "required")); continue; }
            if (g.AccountId is not { } aid || aid == Guid.Empty) { errors.Add(new($"groups[{i}].accountId", "required")); continue; }
            if (!groups.TryAdd(gid, aid)) errors.Add(new($"groups[{i}].groupId", "duplicate_group"));
        }
        if (errors.Count > 0) return ConfigResult<InventoryAccountingView>.Invalid(errors);
        try
        {
            var saved = await transactions.ExecuteAsync(companyId, async work =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
                {
                    lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                var current = await ReadAsync(work.Connection, work.Transaction, null, ct);
                var currentNo = current.Version?.VersionNo ?? 0;
                if (currentNo != expectedVersion) throw new Abort(ConfigStatus.PreconditionFailed, "accounting_version_conflict", currentNo);
                // base currency: stock is valued in it, so no configured account may be limited to another currency
                string? baseCurrency;
                await using (var bq = new NpgsqlCommand($"SELECT {CC.Code} FROM {CC.Table} WHERE {CC.IsBase}", work.Connection, work.Transaction)) baseCurrency = (await bq.ExecuteScalarAsync(ct))?.ToString()?.Trim();
                var allIds = groups.Values.Concat(new[] { input.DefaultInventoryAccountId, input.ReceiptCounterAccountId, input.IssueCounterAccountId, input.AdjustmentCounterAccountId }.Where(x => x is not null).Select(x => x!.Value)).ToList();
                var accounts = await AccountsAsync(work.Connection, work.Transaction, allIds, ct);
                var fields = new List<FieldError>();
                void Check(Guid? id, string field, bool inventory)
                {
                    if (id is not { } v) return;
                    if (!accounts.TryGetValue(v, out var a)) { fields.Add(new(field, "account_not_found")); return; }
                    if (a.IsGroup) fields.Add(new(field, "account_not_postable"));
                    else if (!a.IsActive) fields.Add(new(field, "account_inactive"));
                    else if (inventory && a.Type != (int)AccountType.Asset) fields.Add(new(field, "inventory_account_must_be_asset"));
                    else if (a.Policy == (int)AccountCurrencyPolicy.Specific && !string.Equals(a.Currency, baseCurrency, StringComparison.Ordinal)) fields.Add(new(field, "account_currency_not_base"));
                }
                Check(input.DefaultInventoryAccountId, "defaultInventoryAccountId", true);
                Check(input.ReceiptCounterAccountId, "receiptCounterAccountId", false);
                Check(input.IssueCounterAccountId, "issueCounterAccountId", false);
                Check(input.AdjustmentCounterAccountId, "adjustmentCounterAccountId", false);
                var groupIds = (await GroupIdsAsync(work, groups.Keys, ct));
                var index = 0;
                foreach (var (gid, aid) in groups)
                {
                    if (!groupIds.Contains(gid)) fields.Add(new($"groups[{index}].groupId", "group_not_found"));
                    Check(aid, $"groups[{index}].accountId", true);
                    index++;
                }
                var inventoryAccounts = groups.Values.Append(input.DefaultInventoryAccountId ?? Guid.Empty).Where(x => x != Guid.Empty).ToHashSet();
                if (input.ReceiptCounterAccountId is { } rc && inventoryAccounts.Contains(rc)) fields.Add(new("receiptCounterAccountId", "counter_equals_inventory_account"));
                if (input.IssueCounterAccountId is { } ic && inventoryAccounts.Contains(ic)) fields.Add(new("issueCounterAccountId", "counter_equals_inventory_account"));
                if (input.AdjustmentCounterAccountId is { } ac && inventoryAccounts.Contains(ac)) fields.Add(new("adjustmentCounterAccountId", "counter_equals_inventory_account"));
                if (fields.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: fields);
                var hash = Hash(input, groups);
                if (current.Version is { } cv && cv.ContentHash == hash && (note is null || string.Equals(note, cv.Note, StringComparison.Ordinal))) throw new Unchanged(current);   // a new note on an identical mapping is still recorded as a version
                var versionId = Guid.NewGuid(); var no = currentNo + 1; var now = clock.GetUtcNow().UtcDateTime;
                await using (var ins = new NpgsqlCommand($"INSERT INTO {V.Table} ({V.Id}, {V.VersionNo}, {V.CreatedAtUtc}, {V.CreatedBy}, {V.Note}, {V.DefaultAccountId}, {V.ReceiptCounterAccountId}, {V.IssueCounterAccountId}, {V.GroupCount}, {V.ContentHash}, {V.AdjustmentCounterAccountId}) VALUES (@id, @no, @now, @by, @note, @d, @r, @i, @c, @h, @adj)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", versionId); ins.Parameters.AddWithValue("no", no); ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", actor);
                    ins.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
                    ins.Parameters.AddWithValue("d", (object?)input.DefaultInventoryAccountId ?? DBNull.Value); ins.Parameters.AddWithValue("r", (object?)input.ReceiptCounterAccountId ?? DBNull.Value);
                    ins.Parameters.AddWithValue("i", (object?)input.IssueCounterAccountId ?? DBNull.Value); ins.Parameters.AddWithValue("c", groups.Count); ins.Parameters.AddWithValue("h", hash);
                    ins.Parameters.Add(new NpgsqlParameter("adj", NpgsqlDbType.Uuid) { Value = (object?)input.AdjustmentCounterAccountId ?? DBNull.Value });
                    await ins.ExecuteNonQueryAsync(ct);
                }
                foreach (var (gid, aid) in groups)
                {
                    await using var g = new NpgsqlCommand($"INSERT INTO {VG.Table} ({VG.VersionId}, {VG.GroupId}, {VG.AccountId}) VALUES (@v, @g, @a)", work.Connection, work.Transaction);
                    g.Parameters.AddWithValue("v", versionId); g.Parameters.AddWithValue("g", gid); g.Parameters.AddWithValue("a", aid);
                    await g.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(InventoryAccountingAuditEvents.Saved, ActorUserId: actor, SubjectId: versionId, Detail: $"inventory accounting v{no} groups={groups.Count} hash={hash[..16]}"), ct);
                return await ReadAsync(work.Connection, work.Transaction, no, ct);
            }, ct);
            return ConfigResult<InventoryAccountingView>.Ok(saved);
        }
        catch (Unchanged same) { return ConfigResult<InventoryAccountingView>.Ok(same.View); }
        catch (Abort abort) { return abort.To<InventoryAccountingView>(); }
    }

    private static async Task<HashSet<Guid>> GroupIdsAsync(CompanyAuditedWork work, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        await using var q = new NpgsqlCommand($"SELECT {G.Id} FROM {G.Table} WHERE {G.Id} = ANY(@ids)", work.Connection, work.Transaction);
        q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.Distinct().ToArray() });
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetGuid(0));
        return set;
    }

    /// <summary>
    /// The accounts a posting for an item of <paramref name="groupId"/> uses under the CURRENT configuration, inside the caller's transaction (the posting slice calls this). Refuses with <c>inventory_account_not_configured</c>
    /// when no inventory account resolves; the counter account of the required document kind is checked by the caller with the same refusal style.
    /// </summary>
    public static async Task<ResolvedInventoryAccounts> ResolveAsync(CompanyAuditedWork work, Guid groupId, CancellationToken ct)
    {
        var cfg = await ReadAsync(work.Connection, work.Transaction, null, ct);
        if (cfg.Version is null) throw new InvalidOperationException("inventory_accounting_not_configured");
        var parents = new Dictionary<Guid, Guid?>();
        await using (var q = new NpgsqlCommand($"SELECT {G.Id}, {G.ParentId} FROM {G.Table}", work.Connection, work.Transaction))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) parents[r.GetGuid(0)] = r.IsDBNull(1) ? null : r.GetGuid(1);
        var chain = new List<Guid>();
        for (Guid? cur = groupId; cur is { } id && parents.ContainsKey(id) && !chain.Contains(id); cur = parents[id]) chain.Add(id);
        var account = InventoryAccountResolver.Resolve(cfg.GroupAccounts, chain, cfg.DefaultInventoryAccountId) ?? throw new InvalidOperationException("inventory_account_not_configured");
        return new ResolvedInventoryAccounts(cfg.Version.VersionNo, cfg.Version.Id, account, cfg.ReceiptCounterAccountId, cfg.IssueCounterAccountId, cfg.AdjustmentCounterAccountId);
    }

    /// <summary>
    /// Re-validates, at POSTING time and inside the caller's transaction, the accounts a posting is about to use against the live chart (the chart may have changed since the configuration was saved): inventory accounts must
    /// exist, be postable and active ASSET accounts, the counter account must exist, be postable and active, and none may be limited to a foreign currency. Returns the first refusal code, or null when all are usable.
    /// </summary>
    public static async Task<string?> ValidateForPostingAsync(CompanyAuditedWork work, IReadOnlyCollection<Guid> inventoryAccounts, Guid? counterAccount, CancellationToken ct)
    {
        string? baseCurrency;
        await using (var bq = new NpgsqlCommand($"SELECT {CC.Code} FROM {CC.Table} WHERE {CC.IsBase}", work.Connection, work.Transaction)) baseCurrency = (await bq.ExecuteScalarAsync(ct))?.ToString()?.Trim();
        var ids = inventoryAccounts.Concat(counterAccount is { } c ? [c] : []).ToList();
        var accounts = await AccountsAsync(work.Connection, work.Transaction, ids, ct);
        string? Check(Guid id, bool inventory)
        {
            if (!accounts.TryGetValue(id, out var a)) return "inventory_account_not_found";
            if (a.IsGroup) return "inventory_account_not_postable";
            if (!a.IsActive) return "inventory_account_inactive";
            if (inventory && a.Type != (int)AccountType.Asset) return "inventory_account_must_be_asset";
            if (a.Policy == (int)AccountCurrencyPolicy.Specific && !string.Equals(a.Currency, baseCurrency, StringComparison.Ordinal)) return "inventory_account_currency_not_base";
            return null;
        }
        foreach (var id in inventoryAccounts) if (Check(id, true) is { } e) return e;
        if (counterAccount is { } ca)
        {
            if (inventoryAccounts.Contains(ca)) return "counter_equals_inventory_account";
            if (Check(ca, false) is { } e) return e.Replace("inventory_account", "counter_account");
        }
        return null;
    }
}

/// <summary>Authorized entry point: view needs <c>company.inventoryaccounting.view</c>, saving <c>company.inventoryaccounting.configure</c> (company-wide), re-read on every call.</summary>
public sealed class InventoryAccountingManagementService(AccessService access, InventoryAccountingService accounting)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);

    /// <summary>Whoever may configure may also read (the screen cannot be used otherwise).</summary>
    private async Task<bool> CanView(Guid user, Guid company, CancellationToken ct) => await Can(user, company, PermissionCatalog.Codes.InventoryAccountingView, ct) || await Can(user, company, PermissionCatalog.Codes.InventoryAccountingConfigure, ct);

    public async Task<ConfigResult<InventoryAccountingView>> GetAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await CanView(user, company, ct) ? ConfigResult<InventoryAccountingView>.Ok(await accounting.GetAsync(company, null, ct)) : Denied<InventoryAccountingView>();

    public async Task<ConfigResult<IReadOnlyList<InventoryAccountingVersionView>>> VersionsAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await CanView(user, company, ct) ? ConfigResult<IReadOnlyList<InventoryAccountingVersionView>>.Ok(await accounting.ListVersionsAsync(company, ct)) : Denied<IReadOnlyList<InventoryAccountingVersionView>>();

    /// <summary>The accounts and groups to show or pick: every active postable account plus the ones the given configuration already uses (so an account deactivated later is still named). Viewers and configurers.</summary>
    public async Task<ConfigResult<InventoryAccountingLookups>> LookupsAsync(Guid user, Guid company, IReadOnlyCollection<Guid>? referenced = null, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.InventoryAccountingView, ct) || await Can(user, company, PermissionCatalog.Codes.InventoryAccountingConfigure, ct) ? ConfigResult<InventoryAccountingLookups>.Ok(await accounting.LookupsAsync(company, referenced, ct)) : Denied<InventoryAccountingLookups>();

    public async Task<ConfigResult<InventoryAccountingView>> SaveAsync(Guid user, Guid company, long? expectedVersion, InventoryAccountingInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.InventoryAccountingConfigure, ct) ? await accounting.SaveAsync(user, company, expectedVersion, input, ct) : Denied<InventoryAccountingView>();

    public async Task<bool> CanConfigureAsync(Guid user, Guid company, CancellationToken ct = default) => await Can(user, company, PermissionCatalog.Codes.InventoryAccountingConfigure, ct);
}
