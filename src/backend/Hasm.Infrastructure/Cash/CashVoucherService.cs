using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using Ac = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashAccount;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using CU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Currency;
using CV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucher;
using VE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucherEvent;
using VL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucherLine;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;

namespace Hasm.Infrastructure.Cash;

/// <summary>
/// The real usage probe of the cash account master (the plan's section 4), now that vouchers exist: an account is REFERENCED once any voucher names it (its branch and its ledger binding are then frozen) and has OPEN documents
/// while one of its vouchers is not cancelled. In slice S2a nothing can be approved, so "not cancelled" is exactly "open"; the slice that adds approval and the journal link refines it (approved with a posted or cancelled entry is
/// closed), and slice S3 adds transfers. PostgreSQL enforces the same two rules on the account row (company migration 0040).
/// </summary>
public sealed class CashDocumentUsageProbe : ICashAccountUsageProbe
{
    public async Task<CashAccountUsage> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid cashAccountId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {CV.Table} WHERE {CV.CashAccountId} = @id), EXISTS (SELECT 1 FROM {CV.Table} WHERE {CV.CashAccountId} = @id AND {CV.Status} <> {(short)CashVoucherStatus.Cancelled})", connection, transaction);
        q.Parameters.AddWithValue("id", cashAccountId);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new CashAccountUsage(r.GetBoolean(0), r.GetBoolean(1));
    }
}

/// <summary>
/// The audited company-side store of cash vouchers (Phase 3.14 slice S2a): draft, submitted (withdrawn back to draft) and cancelled. <b>No authorization here by design:</b> <see cref="CashVoucherManagementService"/> wraps it.
/// Every write is ONE company transaction: the voucher row is locked, the revision is fenced, and the change, its line set, its history event and its audit event commit together. Nothing is ever removed or overwritten: an edit
/// of a draft writes a new line set under the new revision. Creation is idempotent per (creator, key); a key reused with another payload is refused.
/// <b>This service creates NO journal entry, allocates NO number and has NO ledger or balance effect</b> (D-214: financial posting for cash and bank stays closed). It has no approve, reject, number, journal or reversal operation.
/// </summary>
public sealed partial class CashVoucherService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }
    /// <summary>A replayed create writes nothing (so it carries no audit event): the transaction is rolled back by this exception and the original is returned.</summary>
    private sealed class Replay(Guid id) : Exception("replay") { public Guid Id { get; } = id; }
    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);
    private static Abort Invalid(IReadOnlyList<FieldError> fields) => new(ConfigStatus.Invalid, "validation_failed", fields);

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,64}$")] private static partial Regex KeyPattern();
    [GeneratedRegex("^cash_[a-z_]{3,60}$")] private static partial Regex GuardCode();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static NpgsqlParameter Uuid(string name, Guid? v) => new(name, NpgsqlDbType.Uuid) { Value = v is { } g ? g : DBNull.Value };
    private static NpgsqlParameter Text(string name, string? v) => new(name, NpgsqlDbType.Varchar) { Value = (object?)v ?? DBNull.Value };

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == CV.UqIdempotency) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "idempotency_race"); }   // a concurrent create with the same key won
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // a line number or an event revision: a racing writer won, nothing was written
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return ConfigResult<T>.Invalid([ex.ConstraintName == CV.FkParty ? new FieldError("partyId", "party_not_found") : ex.ConstraintName == VL.FkAccount ? new FieldError("lines", "account_not_found")
                : ex.ConstraintName == CV.FkCashAccount ? new FieldError("cashAccountId", "cash_account_not_found") : new FieldError("reference", "reference_not_found")]);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // a retryable conflict, never a 500
        // a database guard refused a change the checks above let through (a race with a deactivation, a binding or a party role): nothing was written. A guard that names its rule is reported by that stable code.
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, GuardCode().IsMatch(ex.MessageText) ? ex.MessageText : "invariant_violation"); }
    }

    // ================================================================================================================ reading

    private const string Total = $"(SELECT COALESCE(sum(x.{VL.Amount}), 0) FROM {VL.Table} x WHERE x.{VL.VoucherId} = v.{CV.Id} AND x.{VL.Revision} = v.{CV.LinesRevision})";
    private const string Cols = $"v.{CV.Id}, v.{CV.Kind}, v.{CV.CashAccountId}, v.{CV.BranchId}, v.{CV.Currency}, v.{CV.VoucherDate}, v.{CV.PartyId}, v.{CV.Reference}, v.{CV.Note}, v.{CV.Status}, v.{CV.SubmittedBy}, v.{CV.UpdatedBy}, v.{CV.Revision}, " +
        $"v.{CV.CreatedBy}, v.{CV.CreatedAtUtc}, v.{CV.UpdatedAtUtc}, v.{CV.LinesRevision}";

    private static (CashVoucherView View, long LinesRevision) ReadHeader(NpgsqlDataReader r, decimal total) => (new(r.GetGuid(0), (CashVoucherKind)r.GetInt16(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4).Trim(), r.GetFieldValue<DateOnly>(5),
        r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), (CashVoucherStatus)r.GetInt16(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.GetGuid(11), r.GetInt64(12),
        r.GetGuid(13), r.GetFieldValue<DateTimeOffset>(14), r.GetFieldValue<DateTimeOffset>(15), total, [], []), r.GetInt64(16));

    private static async Task<IReadOnlyList<CashVoucherLineView>> LinesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, long linesRevision, CancellationToken ct)
    {
        var lines = new List<CashVoucherLineView>();
        await using var q = new NpgsqlCommand($"SELECT {VL.LineNo}, {VL.AccountId}, {VL.Amount}, {VL.Description} FROM {VL.Table} WHERE {VL.VoucherId} = @id AND {VL.Revision} = @rev ORDER BY {VL.LineNo}", conn, tx);
        q.Parameters.AddWithValue("id", id); q.Parameters.AddWithValue("rev", linesRevision);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lines.Add(new CashVoucherLineView(r.GetInt32(0), r.GetGuid(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return lines;
    }

    /// <summary>The voucher with its CURRENT line set (and, when asked, its history). <paramref name="forUpdate"/> locks the row for a revision-fenced change.</summary>
    private static async Task<CashVoucherView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, bool withEvents, CancellationToken ct)
    {
        CashVoucherView head; long linesRevision;
        await using (var q = new NpgsqlCommand($"SELECT {Cols} FROM {CV.Table} v WHERE v.{CV.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (head, linesRevision) = ReadHeader(r, 0m);
        }
        var lines = await LinesAsync(conn, tx, id, linesRevision, ct);
        var events = new List<CashVoucherEventView>();
        if (withEvents)
        {
            await using var q = new NpgsqlCommand($"SELECT {VE.Kind}, {VE.FromStatus}, {VE.ToStatus}, {VE.Revision}, {VE.ActorUserId}, {VE.Reason}, {VE.AtUtc} FROM {VE.Table} WHERE {VE.VoucherId} = @id ORDER BY {VE.Revision}", conn, tx);
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new CashVoucherEventView((CashVoucherEventKind)r.GetInt16(0), r.IsDBNull(1) ? null : (CashVoucherStatus)r.GetInt16(1), (CashVoucherStatus)r.GetInt16(2), r.GetInt64(3), r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        }
        return head with { Total = lines.Sum(l => l.Amount), Lines = lines, Events = events };
    }

    public async Task<CashVoucherView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, false, true, ct);
    }

    /// <summary>Headers with their total (no lines or events). <see cref="CashVoucherFilter.AllowedBranches"/> restricts the query itself, so paging only walks vouchers the caller may see.</summary>
    public async Task<IReadOnlyList<CashVoucherView>> ListAsync(Guid companyId, CashVoucherFilter filter, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = new List<string>();
        await using var q = new NpgsqlCommand { Connection = conn };
        if (filter.Status is { } st) { where.Add($"v.{CV.Status} = @st"); q.Parameters.AddWithValue("st", (short)st); }
        if (filter.Kind is { } k) { where.Add($"v.{CV.Kind} = @k"); q.Parameters.AddWithValue("k", (short)k); }
        if (filter.CashAccountId is { } a) { where.Add($"v.{CV.CashAccountId} = @a"); q.Parameters.AddWithValue("a", a); }
        if (filter.BranchId is { } b) { where.Add($"v.{CV.BranchId} = @br"); q.Parameters.AddWithValue("br", b); }
        if (filter.AllowedBranches is { } allowed) { where.Add($"v.{CV.BranchId} = ANY(@ab)"); q.Parameters.Add(new NpgsqlParameter("ab", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = allowed.ToArray() }); }
        q.CommandText = $"SELECT {Cols}, {Total} FROM {CV.Table} v{(where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))} ORDER BY v.{CV.CreatedAtUtc} DESC, v.{CV.Id} LIMIT @lim OFFSET @off";
        q.Parameters.AddWithValue("lim", Math.Clamp(filter.Limit, 1, 200)); q.Parameters.AddWithValue("off", Math.Max(0, filter.Offset));
        var rows = new List<CashVoucherView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadHeader(r, r.GetDecimal(17)).View);
        return rows;
    }

    // ================================================================================================================ validation

    private sealed record Body(DateOnly Date, Guid? PartyId, string? Reference, string? Note, IReadOnlyList<CashVoucherLineView> Lines);

    private static string Canonical(CashVoucherKind kind, Guid cashAccountId, Body b)
    {
        var sb = new StringBuilder();
        sb.Append((short)kind).Append('|').Append(cashAccountId).Append('|').Append(b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(b.PartyId).Append('|').Append(b.Reference).Append('\u001f').Append(b.Note);
        foreach (var l in b.Lines) sb.Append('\u001e').Append(l.LineNo).Append(':').Append(l.AccountId).Append(':').Append(l.Amount.ToString("0.####", CultureInfo.InvariantCulture)).Append(':').Append(l.Description);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>The shape of a body (everything that needs no database): the date, the texts, the number of lines and the presence of each line's account and amount. The amount's decimals are checked against the base currency in the transaction.</summary>
    private static List<FieldError> CheckBody(CashVoucherInput input, out Body body)
    {
        var errors = new List<FieldError>();
        if (input.VoucherDate is null) errors.Add(new FieldError("voucherDate", "required"));
        else if (input.VoucherDate < new DateOnly(2000, 1, 1) || input.VoucherDate > new DateOnly(2100, 12, 31)) errors.Add(new FieldError("voucherDate", "out_of_range"));
        if (!CashVoucherRules.TryText(input.Reference, CashVoucherRules.MaxReferenceLength, out var reference)) errors.Add(new FieldError("reference", "reference_invalid"));
        if (!CashVoucherRules.TryText(input.Note, CashVoucherRules.MaxTextLength, out var note)) errors.Add(new FieldError("note", "note_invalid"));
        var raw = input.Lines ?? [];
        var lines = new List<CashVoucherLineView>();
        if (raw.Count > CashVoucherRules.MaxLines) errors.Add(new FieldError("lines", "too_many_lines"));
        else
            for (var n = 0; n < raw.Count; n++)
            {
                var l = raw[n];
                if (l is null) { errors.Add(new FieldError($"lines[{n}]", "required")); continue; }
                if (l.AccountId is null) errors.Add(new FieldError($"lines[{n}].accountId", "required"));
                if (l.Amount is null) errors.Add(new FieldError($"lines[{n}].amount", "required"));
                else if (l.Amount <= 0 || l.Amount > CashVoucherRules.MaxAmount) errors.Add(new FieldError($"lines[{n}].amount", "out_of_range"));
                if (!CashVoucherRules.TryText(l.Description, CashVoucherRules.MaxTextLength, out var description)) errors.Add(new FieldError($"lines[{n}].description", "description_invalid"));
                lines.Add(new CashVoucherLineView(n + 1, l.AccountId ?? Guid.Empty, l.Amount ?? 0m, description));
            }
        if (errors.Count == 0 && lines.Sum(l => l.Amount) > CashVoucherRules.MaxAmount) errors.Add(new FieldError("lines", "total_out_of_range"));
        body = new Body(input.VoucherDate ?? default, input.PartyId, reference, note, lines);
        return errors;
    }

    /// <summary>
    /// Everything the plan requires of a voucher that is written or submitted (docs/PHASE-3.14.md section 6), read inside the transaction: a selected base currency with defined minor units; an ACTIVE cash or bank account in that
    /// currency, in an active branch; and for every line a counter account that is postable and active, admits the base currency, is not bound to a cash or bank account and is not the control account of any party role, with an
    /// amount that has no more decimals than the base currency. Returns the account's branch and currency. The fiscal period of the date is NOT checked here: the plan checks it at approval and at posting.
    /// PostgreSQL enforces the account and counter-account rules again (migration 0040).
    /// </summary>
    private static async Task<(Guid BranchId, string Currency)> RequireValidAsync(CompanyAuditedWork work, Guid cashAccountId, IReadOnlyList<CashVoucherLineView> lines, Func<Guid, bool>? mayUseBranch, CancellationToken ct)
    {
        string? baseCode = null; int? minor = null;
        await using (var c = new NpgsqlCommand($"SELECT cc.{CC.Code}, cu.{CU.MinorUnits} FROM {CC.Table} cc JOIN {CU.Table} cu ON cu.{CU.Code} = cc.{CC.Code} WHERE cc.{CC.IsBase} AND cc.{CC.Enabled}", work.Connection, work.Transaction))
        await using (var r = await c.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { baseCode = r.GetString(0).Trim(); minor = r.IsDBNull(1) ? null : r.GetInt16(1); }
        if (baseCode is null) throw new Abort(ConfigStatus.Conflict, "base_currency_not_selected");
        if (minor is null) throw new Abort(ConfigStatus.Conflict, "base_currency_minor_units_undefined");

        Guid branch; bool active; string currency;
        // the share lock keeps the account as it is read until this transaction ends: it is not deactivated or re-bound beside this voucher
        await using (var q = new NpgsqlCommand($"SELECT {CA.BranchId}, {CA.IsActive}, {CA.Currency} FROM {CA.Table} WHERE {CA.Id} = @a FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("a", cashAccountId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw Invalid("cashAccountId", "cash_account_not_found");
            branch = r.GetGuid(0); active = r.GetBoolean(1); currency = r.GetString(2).Trim();
        }
        // an account of a branch the caller does not hold answers exactly like an unknown one (decided on the branch read under the lock, so a re-binding cannot be raced)
        if (mayUseBranch is not null && !mayUseBranch(branch)) throw Invalid("cashAccountId", "cash_account_not_found");
        if (!active) throw Invalid("cashAccountId", "cash_account_inactive");
        if (currency != baseCode) throw Invalid("cashAccountId", "cash_account_currency_not_base");
        await using (var q = new NpgsqlCommand($"SELECT {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = @b FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("b", branch);
            if (await q.ExecuteScalarAsync(ct) is not true) throw Invalid("cashAccountId", "branch_inactive");
        }

        var errors = new List<FieldError>();
        var accounts = new Dictionary<Guid, (bool Group, bool Active, short Policy, string? Currency, bool Cash, bool Control)>();
        if (lines.Count > 0)
        {
            await using var q = new NpgsqlCommand(
                $"SELECT x.{Ac.Id}, x.{Ac.IsGroup}, x.{Ac.IsActive}, x.{Ac.CurrencyPolicy}, x.{Ac.CurrencyCode}, EXISTS (SELECT 1 FROM {CA.Table} c WHERE c.{CA.AccountId} = x.{Ac.Id}), " +
                $"EXISTS (SELECT 1 FROM {PR.Table} p WHERE p.{PR.ControlAccountId} = x.{Ac.Id}) FROM {Ac.Table} x WHERE x.{Ac.Id} = ANY(@ids)", work.Connection, work.Transaction);
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = lines.Select(l => l.AccountId).Distinct().ToArray() });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) accounts[r.GetGuid(0)] = (r.GetBoolean(1), r.GetBoolean(2), r.GetInt16(3), r.IsDBNull(4) ? null : r.GetString(4).Trim(), r.GetBoolean(5), r.GetBoolean(6));
        }
        for (var n = 0; n < lines.Count; n++)
        {
            var l = lines[n];
            if (CashVoucherRules.AmountError(l.Amount, minor.Value) is { } amountError) errors.Add(new FieldError($"lines[{n}].amount", amountError));
            var field = $"lines[{n}].accountId";
            if (!accounts.TryGetValue(l.AccountId, out var a)) errors.Add(new FieldError(field, "account_not_found"));
            else if (a.Group || !a.Active) errors.Add(new FieldError(field, "account_not_postable"));
            else if (!(a.Policy is 1 or 2 || (a.Policy == 3 && a.Currency == baseCode))) errors.Add(new FieldError(field, "account_currency_not_base"));
            else if (a.Cash) errors.Add(new FieldError(field, "counter_account_is_cash_account"));          // a transfer in disguise (it would bypass the transfer rules, Q7)
            else if (a.Control) errors.Add(new FieldError(field, "counter_account_is_control_account"));    // an unallocated posting to a receivable or payable control account is not decided (Q3)
        }
        if (errors.Count > 0) throw Invalid(errors);
        return (branch, currency);
    }

    private static async Task InsertLinesAsync(CompanyAuditedWork work, Guid voucherId, long revision, IReadOnlyList<CashVoucherLineView> lines, CancellationToken ct)
    {
        foreach (var l in lines)
        {
            await using var ins = new NpgsqlCommand($"INSERT INTO {VL.Table} ({VL.Id}, {VL.VoucherId}, {VL.Revision}, {VL.LineNo}, {VL.AccountId}, {VL.Amount}, {VL.Description}) VALUES (@id, @v, @r, @n, @a, @m, @d)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("v", voucherId); ins.Parameters.AddWithValue("r", revision); ins.Parameters.AddWithValue("n", l.LineNo); ins.Parameters.AddWithValue("a", l.AccountId);
            ins.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Numeric) { Value = l.Amount }); ins.Parameters.Add(Text("d", l.Description));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The history event and the audit event of one revision (written AFTER the change, in its transaction: PostgreSQL requires the event to carry the voucher's current revision and status, and every revision to have one).</summary>
    private async Task AppendEventAsync(CompanyAuditedWork work, Guid voucherId, CashVoucherEventKind kind, CashVoucherStatus? from, CashVoucherStatus to, long revision, Guid actor, string? reason, short auditType, string detail, CancellationToken ct)
    {
        await using (var ins = new NpgsqlCommand($"INSERT INTO {VE.Table} ({VE.Id}, {VE.VoucherId}, {VE.Kind}, {VE.FromStatus}, {VE.ToStatus}, {VE.Revision}, {VE.ActorUserId}, {VE.Reason}, {VE.AtUtc}) VALUES (@id, @v, @k, @f, @t, @r, @a, @re, @now)", work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("v", voucherId); ins.Parameters.AddWithValue("k", (short)kind);
            ins.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Smallint) { Value = from is { } f ? (short)f : DBNull.Value });
            ins.Parameters.AddWithValue("t", (short)to); ins.Parameters.AddWithValue("r", revision); ins.Parameters.AddWithValue("a", actor); ins.Parameters.Add(Text("re", reason)); ins.Parameters.AddWithValue("now", Now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(auditType, ActorUserId: actor, SubjectId: voucherId, Detail: detail), ct);
    }

    // ================================================================================================================ create

    /// <summary>
    /// Creates a draft on an active cash or bank account, in that account's branch and currency. Idempotent per (creator, key): the same key with the same payload returns the original voucher (<c>Replayed</c>); the same key with
    /// another payload is refused (<c>idempotency_key_reused</c>). Simultaneous creates with one key produce exactly one voucher (the unique index decides; the loser returns the winner).
    /// <paramref name="mayUseBranch"/> is the caller's authorization for the account's branch, decided inside the transaction on the branch read under the lock.
    /// </summary>
    public async Task<ConfigResult<CashVoucherView>> CreateAsync(Guid actor, Guid companyId, CashVoucherInput? input, Func<Guid, bool>? mayUseBranch = null, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<CashVoucherView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckBody(input, out var body);
        var kind = CashVoucherRules.ParseKind(input.Kind);
        if (kind is null) errors.Add(new FieldError("kind", string.IsNullOrWhiteSpace(input.Kind) ? "required" : "invalid_value"));
        if (input.CashAccountId is null || input.CashAccountId == Guid.Empty) errors.Add(new FieldError("cashAccountId", "required"));
        var key = input.IdempotencyKey?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key)) errors.Add(new FieldError("idempotencyKey", "idempotency_key_invalid"));
        if (errors.Count > 0) return ConfigResult<CashVoucherView>.Invalid(errors);
        var cashAccountId = input.CashAccountId!.Value;
        var hash = Canonical(kind!.Value, cashAccountId, body);
        var id = Guid.NewGuid();
        ConfigResult<Guid> r;
        try
        {
            r = await Mutate(companyId, async work =>
            {
                if (await FindByKeyAsync(work.Connection, work.Transaction, actor, key, ct) is { } existing) throw existing.Hash == hash ? new Replay(existing.Id) : new Abort(ConfigStatus.Conflict, "idempotency_key_reused");
                var (branch, currency) = await RequireValidAsync(work, cashAccountId, body.Lines, mayUseBranch, ct);
                var now = Now;
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {CV.Table} ({CV.Id}, {CV.Kind}, {CV.CashAccountId}, {CV.BranchId}, {CV.Currency}, {CV.VoucherDate}, {CV.PartyId}, {CV.Reference}, {CV.Note}, {CV.Status}, {CV.LinesRevision}, {CV.IdempotencyKey}, {CV.PayloadHash}, " +
                    $"{CV.UpdatedBy}, {CV.Revision}, {CV.CreatedBy}, {CV.CreatedAtUtc}, {CV.UpdatedAtUtc}) VALUES (@id, @k, @ca, @br, @cur, @d, @p, @ref, @note, 1, 1, @key, @h, @by, 1, @by, @now, @now)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("k", (short)kind.Value); ins.Parameters.AddWithValue("ca", cashAccountId); ins.Parameters.AddWithValue("br", branch); ins.Parameters.AddWithValue("cur", currency);
                    ins.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = body.Date }); ins.Parameters.Add(Uuid("p", body.PartyId)); ins.Parameters.Add(Text("ref", body.Reference)); ins.Parameters.Add(Text("note", body.Note));
                    ins.Parameters.AddWithValue("key", key); ins.Parameters.AddWithValue("h", hash); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await InsertLinesAsync(work, id, 1, body.Lines, ct);
                await AppendEventAsync(work, id, CashVoucherEventKind.Created, null, CashVoucherStatus.Draft, 1, actor, null, CashVoucherAuditEvents.Created,
                    $"cash voucher created kind={CashVoucherRules.KindName(kind.Value)} account={cashAccountId:N} lines={body.Lines.Count}", ct);
                return id;
            }, ct);
        }
        catch (Replay replay) { return ConfigResult<CashVoucherView>.Ok((await GetAsync(companyId, replay.Id, ct))! with { Replayed = true }); }
        if (r.IsOk) return ConfigResult<CashVoucherView>.Ok((await GetAsync(companyId, r.Value, ct))!);
        // a concurrent create with the same key won the unique index: return it (or refuse a different payload)
        if (r.Code == "idempotency_race") return await ResolveRaceAsync(companyId, actor, key, hash, ct) ?? ConfigResult<CashVoucherView>.Fail(r.Status, r.Code!);
        return ConfigResult<CashVoucherView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    private static async Task<(Guid Id, string Hash)?> FindByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid actor, string key, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {CV.Id}, {CV.PayloadHash} FROM {CV.Table} WHERE {CV.CreatedBy} = @u AND {CV.IdempotencyKey} = @k", conn, tx);
        q.Parameters.AddWithValue("u", actor); q.Parameters.AddWithValue("k", key);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1).Trim()) : null;
    }

    private async Task<ConfigResult<CashVoucherView>?> ResolveRaceAsync(Guid companyId, Guid actor, string key, string hash, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        if (await FindByKeyAsync(conn, null, actor, key, ct) is not { } winner) return null;
        if (winner.Hash != hash) return ConfigResult<CashVoucherView>.Fail(ConfigStatus.Conflict, "idempotency_key_reused");
        return ConfigResult<CashVoucherView>.Ok((await LoadAsync(conn, null, winner.Id, false, true, ct))! with { Replayed = true });
    }

    // ================================================================================================================ update and transitions

    private async Task<ConfigResult<CashVoucherView>> Transition(Guid companyId, Guid id, long? expectedRevision, Func<CompanyAuditedWork, CashVoucherView, Task> change, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<CashVoucherView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var r = await Mutate(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, false, ct) ?? throw new Abort(ConfigStatus.NotFound, "cash_voucher_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            await change(work, cur);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<CashVoucherView>.Ok((await GetAsync(companyId, id, ct))!) : ConfigResult<CashVoucherView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    private async Task SetStatusAsync(CompanyAuditedWork work, CashVoucherView cur, CashVoucherStatus to, Guid? submittedBy, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand($"UPDATE {CV.Table} SET {CV.Status} = @s, {CV.SubmittedBy} = @sb, {CV.Revision} = {CV.Revision} + 1, {CV.UpdatedAtUtc} = @now WHERE {CV.Id} = @id", work.Connection, work.Transaction);
        upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("s", (short)to); upd.Parameters.Add(Uuid("sb", submittedBy)); upd.Parameters.AddWithValue("now", Now);
        await upd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replaces the date, party, reference, note and the whole line set of a DRAFT (revision-fenced): a NEW line set is written under the new revision and the earlier sets stay as history. The kind, the cash account and the key never change. The actor becomes the last editor.</summary>
    public Task<ConfigResult<CashVoucherView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CashVoucherInput? input, CancellationToken ct = default)
    {
        if (input is null) return Task.FromResult(ConfigResult<CashVoucherView>.Invalid([new FieldError("input", "required")]));
        var errors = CheckBody(input, out var body);
        if (errors.Count > 0) return Task.FromResult(ConfigResult<CashVoucherView>.Invalid(errors));
        return Transition(companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CashVoucherStatus.Draft) throw new Abort(ConfigStatus.Conflict, "cash_voucher_not_draft");
            if (!string.IsNullOrWhiteSpace(input.Kind) && CashVoucherRules.ParseKind(input.Kind) != cur.Kind) throw Invalid("kind", "kind_immutable");
            if (input.CashAccountId is { } a && a != cur.CashAccountId) throw Invalid("cashAccountId", "cash_account_immutable");
            await RequireValidAsync(work, cur.CashAccountId, body.Lines, null, ct);
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {CV.Table} SET {CV.VoucherDate} = @d, {CV.PartyId} = @p, {CV.Reference} = @ref, {CV.Note} = @note, {CV.UpdatedBy} = @by, {CV.Revision} = {CV.Revision} + 1, {CV.LinesRevision} = {CV.Revision} + 1, {CV.UpdatedAtUtc} = @now WHERE {CV.Id} = @id",
                work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = body.Date }); upd.Parameters.Add(Uuid("p", body.PartyId)); upd.Parameters.Add(Text("ref", body.Reference));
                upd.Parameters.Add(Text("note", body.Note)); upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await InsertLinesAsync(work, cur.Id, cur.Revision + 1, body.Lines, ct);
            await AppendEventAsync(work, cur.Id, CashVoucherEventKind.Updated, CashVoucherStatus.Draft, CashVoucherStatus.Draft, cur.Revision + 1, actor, null, CashVoucherAuditEvents.Updated, $"cash voucher updated r{cur.Revision + 1} lines={body.Lines.Count}", ct);
        }, ct);
    }

    /// <summary>
    /// Draft to submitted. A voucher needs at least one line, and everything a draft was checked for is checked again (the account or a counter account may have changed since). The submitter is recorded: the independent-approver
    /// rule of the later slice compares against it. Submitting approves nothing and posts nothing.
    /// </summary>
    public Task<ConfigResult<CashVoucherView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CashVoucherStatus.Draft) throw new Abort(ConfigStatus.Conflict, "cash_voucher_not_draft");
            if (cur.Lines.Count == 0) throw Invalid("lines", "lines_required");
            await RequireValidAsync(work, cur.CashAccountId, cur.Lines, null, ct);
            await SetStatusAsync(work, cur, CashVoucherStatus.Submitted, actor, ct);
            await AppendEventAsync(work, cur.Id, CashVoucherEventKind.Submitted, CashVoucherStatus.Draft, CashVoucherStatus.Submitted, cur.Revision + 1, actor, null, CashVoucherAuditEvents.Submitted, $"cash voucher submitted r{cur.Revision + 1}", ct);
        }, ct);

    /// <summary>Submitted back to draft (withdrawal): the submitter record is cleared and the voucher is editable again.</summary>
    public Task<ConfigResult<CashVoucherView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CashVoucherStatus.Submitted) throw new Abort(ConfigStatus.Conflict, "cash_voucher_not_submitted");
            await SetStatusAsync(work, cur, CashVoucherStatus.Draft, null, ct);
            await AppendEventAsync(work, cur.Id, CashVoucherEventKind.Withdrawn, CashVoucherStatus.Submitted, CashVoucherStatus.Draft, cur.Revision + 1, actor, null, CashVoucherAuditEvents.Withdrawn, $"cash voucher withdrawn r{cur.Revision + 1}", ct);
        }, ct);

    /// <summary>Draft or submitted to cancelled (final), with a mandatory reason. Nothing was numbered or posted, so nothing is voided or reversed: a cancelled voucher never consumed a number (the plan's section 6).</summary>
    public Task<ConfigResult<CashVoucherView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length is < 1 or > CashVoucherRules.MaxTextLength || text.Any(char.IsControl)) return Task.FromResult(ConfigResult<CashVoucherView>.Invalid([new FieldError("reason", "reason_required")]));
        return Transition(companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status is not (CashVoucherStatus.Draft or CashVoucherStatus.Submitted)) throw new Abort(ConfigStatus.Conflict, "cash_voucher_not_cancellable");
            await SetStatusAsync(work, cur, CashVoucherStatus.Cancelled, cur.SubmittedBy, ct);
            await AppendEventAsync(work, cur.Id, CashVoucherEventKind.Cancelled, cur.Status, CashVoucherStatus.Cancelled, cur.Revision + 1, actor, text, CashVoucherAuditEvents.Cancelled, $"cash voucher cancelled r{cur.Revision + 1}", ct);
        }, ct);
    }
}
