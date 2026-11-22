using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Npgsql;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashAccount;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;

namespace Hasm.Infrastructure.Cash;

/// <summary>
/// Whether a cash or bank account is referenced by vouchers or transfers (the binding to its general-ledger account and its branch freeze once it is) and whether any such document is still OPEN (not approved, or approved with
/// its journal entry neither posted nor cancelled), which blocks deactivation. Slices S2 and S3 provide the real probe. Until then no such document can exist (no table of one exists), so <see cref="NoCashDocuments"/> answers truthfully.
/// </summary>
public interface ICashAccountUsageProbe
{
    Task<CashAccountUsage> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid cashAccountId, CancellationToken ct);
}

public sealed class NoCashDocuments : ICashAccountUsageProbe
{
    public Task<CashAccountUsage> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid cashAccountId, CancellationToken ct) => Task.FromResult(new CashAccountUsage(false, false));
}

/// <summary>
/// Cash and bank account domain operations (Phase 3.14 slice S1). <b>No authorization here by design:</b> <see cref="CashAccountManagementService"/> wraps it. Every mutation runs in <see cref="CompanyAuditedTransactions"/>, so the
/// change and its authenticated audit event commit or roll back together, after the advisory lock that serialises branch-structure changes (an account is never created in a branch that is being deactivated). PostgreSQL enforces
/// the invariants too (see the migration): the binding rule, the unique code and general-ledger binding, no removal. This service creates NO journal entry and allocates NO number: it is configuration only (D-214: financial posting
/// for cash and bank stays closed). The full account number is stored only as an envelope under the key ring; the key ring being unavailable fails the write (nothing is stored in clear).
/// </summary>
public sealed class CashAccountService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, EnvelopeEncryptor encryptor, ICashAccountUsageProbe usage, TimeProvider clock)
{
    private static readonly string Select =
        $"SELECT c.{CA.Id}, c.{CA.Kind}, c.{CA.Code}, c.{CA.NameAr}, c.{CA.NameEn}, c.{CA.BranchId}, c.{CA.AccountId}, c.{CA.Currency}, c.{CA.BankName}, c.{CA.BankBranchName}, c.{CA.NumberTail}, c.{CA.IsActive}, c.{CA.Revision}, c.{CA.CreatedAtUtc}, c.{CA.UpdatedAtUtc}, " +
        $"x.{Ac.Code}, x.{Ac.NameAr}, x.{Ac.NameEn} FROM {CA.Table} c JOIN {Ac.Table} x ON x.{Ac.Id} = c.{CA.AccountId}";

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null, CashAccountView? unchanged = null) : Exception(code)
    {
        public ConfigResult<CashAccountView> ToResult() => unchanged is not null ? ConfigResult<CashAccountView>.Ok(unchanged)
            : fields is not null ? ConfigResult<CashAccountView>.Invalid(fields) : ConfigResult<CashAccountView>.Fail(status, code_(), currentRevision);
        private string code_() => Message;
    }

    private static string Context(Guid companyId, Guid id) => $"cashaccount:{companyId:N}:{id:N}";

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<IReadOnlyList<CashAccountView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand($"{Select} ORDER BY c.{CA.Code}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CashAccountView>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<CashAccountView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, false, ct);
    }

    /// <summary>
    /// The ledger accounts that could be bound now (bounded to <paramref name="limit"/>, with the total): active, non-group asset accounts that admit the base currency and serve no cash or bank account yet, matching the optional search
    /// text on code or either name. The same rule the database enforces on a write; a company without a base currency has none.
    /// </summary>
    public async Task<(IReadOnlyList<GlCandidate> Items, long Total)> CandidatesAsync(Guid companyId, string? search, int limit, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = $"x.{Ac.Type} = 1 AND x.{Ac.IsActive} AND NOT x.{Ac.IsGroup} " +
            $"AND EXISTS (SELECT 1 FROM {CC.Table} cc WHERE cc.{CC.IsBase} AND cc.{CC.Enabled} AND (x.{Ac.CurrencyPolicy} IN (1, 2) OR (x.{Ac.CurrencyPolicy} = 3 AND x.{Ac.CurrencyCode} = cc.{CC.Code}))) " +
            $"AND NOT EXISTS (SELECT 1 FROM {CA.Table} c WHERE c.{CA.AccountId} = x.{Ac.Id})" +
            (string.IsNullOrWhiteSpace(search) ? "" : $" AND (x.{Ac.Code} ILIKE @q OR x.{Ac.NameAr} ILIKE @q OR x.{Ac.NameEn} ILIKE @q)");
        void Bind(NpgsqlCommand cmd) { if (!string.IsNullOrWhiteSpace(search)) cmd.Parameters.AddWithValue("q", "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); }
        long total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {Ac.Table} x WHERE {where}", conn)) { Bind(count); total = Convert.ToInt64(await count.ExecuteScalarAsync(ct)); }
        var items = new List<GlCandidate>();
        await using (var cmd = new NpgsqlCommand($"SELECT x.{Ac.Id}, x.{Ac.Code}, x.{Ac.NameAr}, x.{Ac.NameEn} FROM {Ac.Table} x WHERE {where} ORDER BY x.{Ac.Code} LIMIT @limit", conn))
        {
            Bind(cmd);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(new GlCandidate(r.GetGuid(0), r.GetString(1), S(r, 2), S(r, 3)));
        }
        return (items, total);
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    public async Task<ConfigResult<CashAccountView>> CreateAsync(Guid actor, Guid companyId, CashAccountInput? input, CancellationToken ct = default)
    {
        if (!CashAccountRules.TryNormalize(input, null, null, out var data, out var number, out var errors)) return ConfigResult<CashAccountView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            await RequireActiveBranchAsync(work, data.BranchId, ct);
            var currency = await BaseCurrencyAsync(work, ct);
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO {CA.Table} ({CA.Id}, {CA.Kind}, {CA.Code}, {CA.NameAr}, {CA.NameEn}, {CA.BranchId}, {CA.AccountId}, {CA.Currency}, {CA.BankName}, {CA.BankBranchName}, {CA.NumberCipher}, {CA.NumberKeyId}, {CA.NumberTail}, " +
                    $"{CA.IsActive}, {CA.Revision}, {CA.CreatedBy}, {CA.CreatedAtUtc}, {CA.UpdatedAtUtc}) VALUES (@id, @kind, @code, @ar, @en, @branch, @acct, @ccy, @bank, @bbranch, @cipher, @keyid, @tail, true, 1, @actor, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("kind", (short)data.Kind);
                ins.Parameters.AddWithValue("code", data.Code);
                ins.Parameters.AddWithValue("ccy", currency);
                ins.Parameters.AddWithValue("actor", actor);
                ins.Parameters.AddWithValue("now", now);
                BindData(ins, data);
                BindNumber(ins, number is null ? null : Seal(number, companyId, id));
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) { throw Map(ex); }
            var changes = Diff(null, data, null, number is null ? null : CashAccountRules.Mask(CashAccountRules.Tail(number)));
            changes["code"] = new JsonObject { ["old"] = null, ["new"] = data.Code };
            changes["kind"] = new JsonObject { ["old"] = null, ["new"] = data.Kind.ToString().ToLowerInvariant() };
            await RecordAsync(work, CashAuditEvents.AccountCreated, actor, id, 1, changes, "cash account created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public async Task<ConfigResult<CashAccountView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CashAccountInput? input, Func<Guid, Task<bool>>? mayUseBranch = null, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CashAccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!CashAccountRules.TryNormalize(input, current.Data, current.MaskedNumber, out var data, out var number, out var errors))
                throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: errors);
            var bindingChanged = data.BranchId != current.Data.BranchId || data.AccountId != current.Data.AccountId;
            if (bindingChanged)
            {
                // the account the documents were posted against, and its branch, never change once anything refers to the account (a different binding is a new cash or bank account)
                if ((await usage.GetAsync(work.Connection, work.Transaction!, id, ct)).Referenced) throw new Abort(ConfigStatus.Conflict, "binding_frozen");
                if (data.BranchId != current.Data.BranchId)
                {
                    if (mayUseBranch is not null && !await mayUseBranch(data.BranchId)) throw new Abort(ConfigStatus.Forbidden, "forbidden");
                    await RequireActiveBranchAsync(work, data.BranchId, ct);
                }
            }
            var bankChanged = data.BankName != current.Data.BankName || data.BankBranchName != current.Data.BankBranchName || number is not null;
            string? newMasked = number is null ? null : CashAccountRules.Mask(CashAccountRules.Tail(number));
            var changes = Diff(current.Data, data, current.MaskedNumber, newMasked);
            if (changes.Count == 0) throw new Abort(ConfigStatus.Ok, "unchanged", unchanged: current);
            try
            {
                await using var upd = new NpgsqlCommand(
                    $"UPDATE {CA.Table} SET {CA.NameAr} = @ar, {CA.NameEn} = @en, {CA.BranchId} = @branch, {CA.AccountId} = @acct, {CA.BankName} = @bank, {CA.BankBranchName} = @bbranch" +
                    (number is null ? "" : $", {CA.NumberCipher} = @cipher, {CA.NumberKeyId} = @keyid, {CA.NumberTail} = @tail") +
                    $", {CA.Revision} = {CA.Revision} + 1, {CA.UpdatedAtUtc} = @now WHERE {CA.Id} = @id", work.Connection, work.Transaction);
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                BindData(upd, data);
                if (number is not null) BindNumber(upd, Seal(number, companyId, id));
                await upd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) { throw Map(ex); }
            await RecordAsync(work, bankChanged ? CashAuditEvents.BankDetailsChanged : CashAuditEvents.AccountUpdated, actor, id, current.Revision + 1, changes, "cash account updated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    public async Task<ConfigResult<CashAccountView>> DeactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CashAccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!current.IsActive) throw new Abort(ConfigStatus.Conflict, "cash_account_already_inactive");
            // an account with an OPEN voucher or transfer cannot be deactivated (the documents would be left pointing at an unusable account); the balance of the ledger account is not a condition (no rule is invented)
            if ((await usage.GetAsync(work.Connection, work.Transaction!, id, ct)).HasOpenDocuments) throw new Abort(ConfigStatus.Conflict, "cash_account_has_open_documents");
            return await ToggleAsync(work, actor, current, false, CashAuditEvents.AccountDeactivated, "cash account deactivated", ct);
        }, ct);
    }

    public async Task<ConfigResult<CashAccountView>> ReactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<CashAccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (current.IsActive) throw new Abort(ConfigStatus.Conflict, "cash_account_already_active");
            await RequireActiveBranchAsync(work, current.Data.BranchId, ct);
            return await ToggleAsync(work, actor, current, true, CashAuditEvents.AccountReactivated, "cash account reactivated", ct);
        }, ct);
    }

    /// <summary>
    /// Returns the FULL account number. The caller (the management service) has already checked the sensitive permission on the account's branch. The reveal is audited (<see cref="CashAuditEvents.AccountNumberRevealed"/>, the id only)
    /// in the same transaction as the read, so a reveal that cannot be recorded is not served. An authentication failure of the envelope (wrong key, tampering, moved to another account) throws <see cref="EncryptionUnavailableException"/>.
    /// </summary>
    public async Task<ConfigResult<string>> RevealNumberAsync(Guid actor, Guid companyId, Guid id, CancellationToken ct = default)
    {
        try
        {
            return ConfigResult<string>.Ok(await transactions.ExecuteAsync(companyId, async work =>
            {
                byte[]? cipher;
                await using (var q = new NpgsqlCommand($"SELECT {CA.NumberCipher} FROM {CA.Table} WHERE {CA.Id} = @id", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("id", id);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    if (!await r.ReadAsync(ct)) throw new Abort(ConfigStatus.NotFound, "cash_account_not_found");
                    cipher = r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0);
                }
                if (cipher is null) throw new Abort(ConfigStatus.NotFound, "cash_account_number_not_set");
                var plain = Encoding.UTF8.GetString(encryptor.Decrypt(cipher, Context(companyId, id)));
                await work.AppendAuditAsync(new AuditEventData(CashAuditEvents.AccountNumberRevealed, ActorUserId: actor, SubjectId: id, Detail: $"cash account number revealed id={id:N}"), ct);
                return plain;
            }, ct));
        }
        catch (Abort a) { var r = a.ToResult(); return ConfigResult<string>.Fail(r.Status, r.Code ?? "conflict"); }
    }

    /// <summary>
    /// Re-wraps the data key of every stored account number under the CURRENT master key (maintenance, the same family as the attachment and import re-wraps): idempotent (a number already under the active key is skipped), one transaction and one
    /// audit event per account, fail-closed (an account whose old key is missing or whose envelope fails authentication is left untouched, counted and audited with a reason code; the others go on). The ciphertext and its binding to company and
    /// account are unchanged. The revision advances by one (the row guard), so an editor holding the old revision is asked to reload. When the active key itself is unavailable the whole call fails (503) and nothing changes.
    /// </summary>
    public async Task<CashRewrapOutcome> RewrapNumbersAsync(Guid actor, Guid companyId, CancellationToken ct = default)
    {
        var active = encryptor.ActiveKeyId;
        var candidates = new List<(Guid Id, string KeyId)>();
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        await using (var cmd = new NpgsqlCommand($"SELECT {CA.Id}, {CA.NumberKeyId} FROM {CA.Table} WHERE {CA.NumberCipher} IS NOT NULL ORDER BY {CA.Id}", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetString(1)));
        int done = 0, skipped = 0, failed = 0;
        foreach (var (id, keyId) in candidates)
        {
            if (keyId == active) { skipped++; continue; }
            try
            {
                var moved = await transactions.ExecuteAsync(companyId, async work =>
                {
                    byte[] cipher; string oldKey; long revision;
                    await using (var q = new NpgsqlCommand($"SELECT {CA.NumberCipher}, {CA.NumberKeyId}, {CA.Revision} FROM {CA.Table} WHERE {CA.Id} = @id FOR UPDATE", work.Connection, work.Transaction))
                    {
                        q.Parameters.AddWithValue("id", id);
                        await using var r = await q.ExecuteReaderAsync(ct);
                        if (!await r.ReadAsync(ct) || r.IsDBNull(0)) return false;
                        cipher = r.GetFieldValue<byte[]>(0); oldKey = r.GetString(1); revision = r.GetInt64(2);
                    }
                    if (oldKey == active) return false;                                                    // moved by someone else meanwhile
                    var rewrapped = encryptor.Rewrap(cipher, Context(companyId, id));                      // throws when the old key is missing or the envelope does not authenticate: the row stays as it is
                    await using (var u = new NpgsqlCommand($"UPDATE {CA.Table} SET {CA.NumberCipher} = @c, {CA.NumberKeyId} = @k, {CA.Revision} = {CA.Revision} + 1, {CA.UpdatedAtUtc} = @now WHERE {CA.Id} = @id", work.Connection, work.Transaction))
                    {
                        u.Parameters.Add(new NpgsqlParameter("c", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = rewrapped });
                        u.Parameters.AddWithValue("k", EnvelopeEncryptor.KeyIdOf(rewrapped));
                        u.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                        u.Parameters.AddWithValue("id", id);
                        await u.ExecuteNonQueryAsync(ct);
                    }
                    await work.AppendAuditAsync(new AuditEventData(CashAuditEvents.NumberRewrapped, ActorUserId: actor, SubjectId: id, Detail: $"cash account number rewrapped id={id:N} r{revision + 1} from={oldKey} to={EnvelopeEncryptor.KeyIdOf(rewrapped)}"), ct);
                    return true;
                }, ct);
                if (moved) done++; else skipped++;
            }
            catch (Exception ex) when (ex is CryptographicException or EncryptionUnavailableException)
            {
                failed++;
                var reason = ex is EncryptionUnavailableException e ? e.Code : "authentication_failed";
                try { await transactions.ExecuteAsync(companyId, async work => { await work.AppendAuditAsync(new AuditEventData(CashAuditEvents.NumberRewrapFailed, ActorUserId: actor, SubjectId: id, Detail: $"cash account number rewrap failed id={id:N} reason={reason}"), ct); return true; }, ct); }
                catch (Exception audit) when (audit is not OperationCanceledException) { /* the failure is counted; the audit of the failure is best effort */ }
            }
        }
        return new CashRewrapOutcome(done, skipped, failed);
    }

    /// <summary>The number of encrypted account numbers per master key id (key usage for the retirement report: a key must stay available while any number is protected by it). No secret and no number is read; a company database of an older level (no table) has none.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> KeyUsageAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var usage = new SortedDictionary<string, int>(StringComparer.Ordinal);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn))
        {
            exists.Parameters.AddWithValue("t", CA.Table);
            if (await exists.ExecuteScalarAsync(ct) is not true) return usage;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {CA.NumberKeyId}, count(*) FROM {CA.Table} WHERE {CA.NumberKeyId} IS NOT NULL GROUP BY 1", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) usage[r.GetString(0)] = (int)r.GetInt64(1);
        return usage;
    }

    /// <summary>The same as <see cref="EncryptionKeyUsage"/> for the key retirement report: the key ids in use and how many numbers are not under <paramref name="activeKeyId"/>. Nothing here is ever "unknown": the key id is a column of the row.</summary>
    public static async Task<EncryptionKeyUsage> KeyUsageAsync(NpgsqlConnection conn, string? activeKeyId, CancellationToken ct = default)
    {
        var usage = await KeyUsageAsync(conn, ct);
        return new EncryptionKeyUsage([.. usage.Keys], 0, usage.Where(u => u.Key != activeKeyId).Sum(u => u.Value));
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private sealed record Sealed(byte[] Cipher, string KeyId, string Tail);

    private Sealed Seal(string number, Guid companyId, Guid id)
    {
        var cipher = encryptor.Encrypt(Encoding.UTF8.GetBytes(number), Context(companyId, id));
        return new Sealed(cipher, EnvelopeEncryptor.KeyIdOf(cipher), CashAccountRules.Tail(number));
    }

    private static void BindData(NpgsqlCommand cmd, CashAccountData d)
    {
        cmd.Parameters.AddWithValue("ar", (object?)d.NameAr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("en", (object?)d.NameEn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("branch", d.BranchId);
        cmd.Parameters.AddWithValue("acct", d.AccountId);
        cmd.Parameters.AddWithValue("bank", (object?)d.BankName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bbranch", (object?)d.BankBranchName ?? DBNull.Value);
    }

    /// <summary>Binds the number columns. An insert always binds them (NULL when there is no number); an update binds them only when a new number is being set.</summary>
    private static void BindNumber(NpgsqlCommand cmd, Sealed? s)
    {
        cmd.Parameters.Add(new NpgsqlParameter("cipher", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = s is null ? DBNull.Value : s.Cipher });
        cmd.Parameters.Add(new NpgsqlParameter("keyid", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = s is null ? DBNull.Value : s.KeyId });
        cmd.Parameters.Add(new NpgsqlParameter("tail", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = s is null ? DBNull.Value : s.Tail });
    }

    private async Task<ConfigResult<CashAccountView>> MutateAsync(Guid companyId, Func<CompanyAuditedWork, Task<CashAccountView>> work, CancellationToken ct)
    {
        try { return ConfigResult<CashAccountView>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) { return abort.ToResult(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
        lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static async Task RequireActiveBranchAsync(CompanyAuditedWork work, Guid branchId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {B.IsActive} FROM {B.Table} WHERE {B.Id} = @b FOR SHARE", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("b", branchId);
        var active = await q.ExecuteScalarAsync(ct);
        if (active is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_not_found")]);
        if (!(bool)active) throw new Abort(ConfigStatus.Conflict, "branch_inactive");
    }

    private static async Task<string> BaseCurrencyAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {CC.Code} FROM {CC.Table} WHERE {CC.IsBase} AND {CC.Enabled} LIMIT 1", work.Connection, work.Transaction);
        return await q.ExecuteScalarAsync(ct) as string ?? throw new Abort(ConfigStatus.Conflict, "base_currency_not_selected");
    }

    /// <summary>The database guards state the reason as the exception text; the stable refusal codes are derived from it (never the raw text).</summary>
    private static Abort Map(PostgresException ex) => ex switch
    {
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var c } when c == CA.UqCode => new Abort(ConfigStatus.Conflict, "cash_account_code_taken"),
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var c } when c == CA.UqAccount => new Abort(ConfigStatus.Conflict, "gl_account_already_bound"),
        { MessageText: "cash_account_gl_not_found" } => new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("accountId", "account_not_found")]),
        { MessageText: "cash_account_gl_not_asset" } => new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("accountId", "account_not_asset")]),
        { MessageText: "cash_account_gl_not_postable" } => new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("accountId", "account_not_postable")]),
        { MessageText: "cash_account_gl_currency" } => new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("accountId", "account_currency_not_base")]),
        { MessageText: "cash_base_currency_not_selected" } => new Abort(ConfigStatus.Conflict, "base_currency_not_selected"),
        _ => throw ex,
    };

    private async Task<CashAccountView> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var current = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "cash_account_not_found");
        if (current.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
        return current;
    }

    private async Task<CashAccountView> ToggleAsync(CompanyAuditedWork work, Guid actor, CashAccountView current, bool active, short eventType, string label, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand($"UPDATE {CA.Table} SET {CA.IsActive} = @a, {CA.Revision} = {CA.Revision} + 1, {CA.UpdatedAtUtc} = @now WHERE {CA.Id} = @id", work.Connection, work.Transaction);
            cmd.Parameters.AddWithValue("a", active);
            cmd.Parameters.AddWithValue("id", current.Id);
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("cash account row not updated");
        }
        catch (PostgresException ex) { throw Map(ex); }   // a reactivation re-checks the binding rule: its ledger account may have changed since
        var changes = new JsonObject { ["isActive"] = new JsonObject { ["old"] = !active, ["new"] = active } };
        await RecordAsync(work, eventType, actor, current.Id, current.Revision + 1, changes, label, ct);
        return (await LoadAsync(work.Connection, work.Transaction, current.Id, false, ct))!;
    }

    private async Task<CashAccountView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"{Select} WHERE c.{CA.Id} = @id{(forUpdate ? " FOR UPDATE OF c" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // Columns: 0 id, 1 kind, 2 code, 3 ar, 4 en, 5 branch, 6 account, 7 currency, 8 bank, 9 bank branch, 10 tail, 11 active, 12 revision, 13 created, 14 updated, 15-17 the ledger account (code, Arabic and English name).
    private static CashAccountView Read(NpgsqlDataReader r) => new(r.GetGuid(0),
        new CashAccountData((CashAccountKind)r.GetInt16(1), r.GetString(2), S(r, 3), S(r, 4), r.GetGuid(5), r.GetGuid(6), S(r, 8), S(r, 9)),
        r.GetString(7), S(r, 10) is { } tail ? CashAccountRules.Mask(tail) : null, r.GetBoolean(11), r.GetInt64(12), r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14),
        new GlAccountRef(r.GetGuid(6), r.GetString(15), S(r, 16), S(r, 17)));

    /// <summary>The changed fields. Names and ids are recorded with their values; the number only as its MASKED old and new form.</summary>
    private static JsonObject Diff(CashAccountData? before, CashAccountData after, string? oldMasked, string? newMasked)
    {
        var changes = new JsonObject();
        void Add(string name, string? o, string? n) { if (!string.Equals(o, n, StringComparison.Ordinal)) changes[name] = new JsonObject { ["old"] = o, ["new"] = n }; }
        Add("nameAr", before?.NameAr, after.NameAr); Add("nameEn", before?.NameEn, after.NameEn);
        Add("branchId", before?.BranchId.ToString(), after.BranchId.ToString()); Add("accountId", before?.AccountId.ToString(), after.AccountId.ToString());
        Add("bankName", before?.BankName, after.BankName); Add("bankBranchName", before?.BankBranchName, after.BankBranchName);
        if (newMasked is not null) changes["accountNumber"] = new JsonObject { ["old"] = oldMasked, ["new"] = newMasked };
        return changes;
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();

    private static async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, Guid id, long revision, JsonObject changes, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var masked = changes["accountNumber"] is JsonObject n ? $" number {n["old"]}->{n["new"]}" : "";
        await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor, SubjectId: id,
            Detail: $"{label} id={id:N} r{revision} f={string.Join(',', changes.Select(c => c.Key))}{masked} h={Hash(json)}"), ct);
    }
}
