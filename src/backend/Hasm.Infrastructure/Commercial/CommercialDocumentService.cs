using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;

namespace Hasm.Infrastructure.Commercial;

/// <summary>
/// The audited company-side store of commercial documents (Phase 3.13 slice S1a): the commercial-status track only (draft, submitted, cancelled). <b>No authorization here by design:</b>
/// <see cref="CommercialDocumentManagementService"/> wraps it. Every write is ONE company transaction: the document row is locked, the revision is fenced, the change, its event row and its audit event commit together.
/// Nothing is ever removed (a draft's lines are replaced only while it is a draft, by PostgreSQL's guard). Creation is idempotent per (document type, creator, key); a key reused with another payload is refused.
/// S1a carries no price, tax, cost, approval, numbering, stock or ledger effect (docs/PHASE-3.13.md).
/// </summary>
public sealed partial class CommercialDocumentService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock, ICommercialDocumentTypeRegistry types)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }
    /// <summary>A replayed create writes nothing (so it carries no audit event): the transaction is rolled back by this exception and the original is returned.</summary>
    private sealed class Replay(Guid id) : Exception("replay") { public Guid Id { get; } = id; }
    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")] private static partial Regex DocTypePattern();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyPattern();
    [GeneratedRegex("^[A-Za-z0-9._:-]{8,64}$")] private static partial Regex KeyPattern();

    private const int MaxLines = 5000;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static NpgsqlParameter Uuid(string name, Guid? v) => new(name, NpgsqlDbType.Uuid) { Value = v is { } g ? g : DBNull.Value };

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == CD.UqIdempotency) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "idempotency_race"); }   // a concurrent create with the same key won
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return ConfigResult<T>.Invalid([new FieldError("reference", "reference_not_found")]); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }   // a database guard refused a racing or inconsistent change: nothing was written
    }

    // ================================================================================================================ reading

    private const string DocCols = $"{CD.Id}, {CD.DocType}, {CD.BranchId}, {CD.PartyId}, {CD.Currency}, {CD.DocumentDate}, {CD.Mode}, {CD.Status}, {CD.Note}, {CD.SubmittedBy}, {CD.Revision}, {CD.CreatedBy}, {CD.CreatedAtUtc}, {CD.UpdatedAtUtc}";

    private static CommercialDocumentView ReadHeader(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4).Trim(), r.GetFieldValue<DateOnly>(5), (CommercialMode)r.GetInt16(6), (CommercialStatus)r.GetInt16(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetInt64(10), r.GetGuid(11), r.GetFieldValue<DateTimeOffset>(12), r.GetFieldValue<DateTimeOffset>(13), [], []);

    private static async Task<CommercialDocumentView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, bool withChildren, CancellationToken ct)
    {
        CommercialDocumentView? head;
        await using (var q = new NpgsqlCommand($"SELECT {DocCols} FROM {CD.Table} WHERE {CD.Id} = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            head = await r.ReadAsync(ct) ? ReadHeader(r) : null;
        }
        if (head is null || !withChildren) return head;
        var lines = new List<CommercialLineView>();
        await using (var q = new NpgsqlCommand($"SELECT {CL.LineNo}, {CL.ItemId}, {CL.UnitId}, {CL.Quantity}, {CL.WarehouseId} FROM {CL.Table} WHERE {CL.DocumentId} = @id ORDER BY {CL.LineNo}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines.Add(new CommercialLineView(r.GetInt32(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetGuid(4)));
        }
        var events = new List<CommercialEventView>();
        await using (var q = new NpgsqlCommand($"SELECT {CE.Kind}, {CE.FromStatus}, {CE.ToStatus}, {CE.Revision}, {CE.ActorUserId}, {CE.Reason}, {CE.AtUtc} FROM {CE.Table} WHERE {CE.DocumentId} = @id ORDER BY {CE.Revision}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) events.Add(new CommercialEventView((CommercialEventKind)r.GetInt16(0), r.IsDBNull(1) ? null : (CommercialStatus)r.GetInt16(1), (CommercialStatus)r.GetInt16(2), r.GetInt64(3), r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        }
        return head with { Lines = lines, Events = events };
    }

    public async Task<CommercialDocumentView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, false, true, ct);
    }

    /// <summary>Headers only (no lines or events). <see cref="CommercialDocumentFilter.AllowedBranches"/> restricts the query itself, so paging only walks documents the caller may see.</summary>
    public async Task<IReadOnlyList<CommercialDocumentView>> ListAsync(Guid companyId, CommercialDocumentFilter filter, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = new List<string>();
        await using var q = new NpgsqlCommand { Connection = conn };
        if (filter.Status is { } st) { where.Add($"{CD.Status} = @st"); q.Parameters.AddWithValue("st", (short)st); }
        if (!string.IsNullOrWhiteSpace(filter.DocType)) { where.Add($"{CD.DocType} = @dt"); q.Parameters.AddWithValue("dt", filter.DocType); }
        if (filter.BranchId is { } b) { where.Add($"{CD.BranchId} = @br"); q.Parameters.AddWithValue("br", b); }
        if (filter.AllowedBranches is { } allowed) { where.Add($"{CD.BranchId} = ANY(@ab)"); q.Parameters.Add(new NpgsqlParameter("ab", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = allowed.ToArray() }); }
        q.CommandText = $"SELECT {DocCols} FROM {CD.Table}{(where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))} ORDER BY {CD.CreatedAtUtc} DESC, {CD.Id} LIMIT @lim OFFSET @off";
        q.Parameters.AddWithValue("lim", Math.Clamp(filter.Limit, 1, 200)); q.Parameters.AddWithValue("off", Math.Max(0, filter.Offset));
        var rows = new List<CommercialDocumentView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadHeader(r));
        return rows;
    }

    // ================================================================================================================ validation

    private static string Canonical(CommercialDocumentInput i, string docType, string currency, string? note, IReadOnlyList<CommercialLineInput> lines)
    {
        var sb = new StringBuilder();
        sb.Append(docType).Append('|').Append(i.BranchId).Append('|').Append(i.PartyId).Append('|').Append(currency).Append('|').Append(i.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(i.Mode).Append('|').Append(note);
        for (var n = 0; n < lines.Count; n++)
            sb.Append('|').Append(n + 1).Append(':').Append(lines[n].ItemId).Append(':').Append(lines[n].UnitId).Append(':').Append(lines[n].Quantity?.ToString("0.##########", CultureInfo.InvariantCulture)).Append(':').Append(lines[n].WarehouseId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static List<FieldError> CheckBody(CommercialDocumentInput input, out string currency, out string? note, out IReadOnlyList<CommercialLineInput> lines)
    {
        var errors = new List<FieldError>();
        currency = input.Currency?.Trim().ToUpperInvariant() ?? "";
        if (!CurrencyPattern().IsMatch(currency)) errors.Add(new FieldError("currency", "currency_invalid"));
        if (input.PartyId is null) errors.Add(new FieldError("partyId", "required"));
        if (input.DocumentDate is null) errors.Add(new FieldError("documentDate", "required"));
        if (input.Mode is null) errors.Add(new FieldError("mode", "required"));
        else if (input.Mode is < 1 or > 2) errors.Add(new FieldError("mode", "invalid_value"));
        note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note is { Length: > 500 } || note?.Any(char.IsControl) == true) errors.Add(new FieldError("note", "note_invalid"));
        lines = input.Lines ?? [];
        if (lines.Count > MaxLines) errors.Add(new FieldError("lines", "too_many_lines"));
        for (var n = 0; n < Math.Min(lines.Count, MaxLines); n++)
        {
            var l = lines[n];
            if (l.ItemId is null) errors.Add(new FieldError($"lines[{n}].itemId", "required"));
            if (l.UnitId is null) errors.Add(new FieldError($"lines[{n}].unitId", "required"));
            if (l.Quantity is null) errors.Add(new FieldError($"lines[{n}].quantity", "required"));
            else if (l.Quantity <= 0 || l.Quantity >= 1_000_000_000m || decimal.Round(l.Quantity.Value, 10) != l.Quantity) errors.Add(new FieldError($"lines[{n}].quantity", "out_of_range"));
        }
        return errors;
    }

    private static async Task RequireBranchAndParty(CompanyAuditedWork work, Guid branchId, Guid partyId, CancellationToken ct)
    {
        await using (var q = new NpgsqlCommand($"SELECT {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = @b", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("b", branchId);
            var v = await q.ExecuteScalarAsync(ct);
            if (v is null) throw Invalid("branchId", "branch_not_found");
            if (!(bool)v) throw Invalid("branchId", "branch_inactive");
        }
        await using (var q = new NpgsqlCommand($"SELECT 1 FROM {PR.Table} WHERE {PR.PartyId} = @p AND {PR.IsActive} FOR SHARE", work.Connection, work.Transaction))
        {
            q.Parameters.AddWithValue("p", partyId);
            if (await q.ExecuteScalarAsync(ct) is null) throw Invalid("partyId", "party_role_required");   // the party must hold an active customer or supplier role
        }
    }

    private static async Task InsertLinesAsync(CompanyAuditedWork work, Guid documentId, IReadOnlyList<CommercialLineInput> lines, CancellationToken ct)
    {
        for (var n = 0; n < lines.Count; n++)
        {
            await using var ins = new NpgsqlCommand($"INSERT INTO {CL.Table} ({CL.Id}, {CL.DocumentId}, {CL.LineNo}, {CL.ItemId}, {CL.UnitId}, {CL.Quantity}, {CL.WarehouseId}) VALUES (@id, @d, @n, @i, @u, @q, @w)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("d", documentId); ins.Parameters.AddWithValue("n", n + 1);
            ins.Parameters.AddWithValue("i", lines[n].ItemId!.Value); ins.Parameters.AddWithValue("u", lines[n].UnitId!.Value);
            ins.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Numeric) { Value = lines[n].Quantity!.Value }); ins.Parameters.Add(Uuid("w", lines[n].WarehouseId));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task AppendEventAsync(CompanyAuditedWork work, Guid documentId, CommercialEventKind kind, CommercialStatus? from, CommercialStatus to, long revision, Guid actor, string? reason, short auditType, string detail, CancellationToken ct)
    {
        await using (var ins = new NpgsqlCommand($"INSERT INTO {CE.Table} ({CE.Id}, {CE.DocumentId}, {CE.Kind}, {CE.FromStatus}, {CE.ToStatus}, {CE.Revision}, {CE.ActorUserId}, {CE.Reason}, {CE.AtUtc}) VALUES (@id, @d, @k, @f, @t, @r, @a, @re, @now)", work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("d", documentId); ins.Parameters.AddWithValue("k", (short)kind);
            ins.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Smallint) { Value = from is { } f ? (short)f : DBNull.Value });
            ins.Parameters.AddWithValue("t", (short)to); ins.Parameters.AddWithValue("r", revision); ins.Parameters.AddWithValue("a", actor);
            ins.Parameters.Add(new NpgsqlParameter("re", NpgsqlDbType.Varchar) { Value = (object?)reason ?? DBNull.Value }); ins.Parameters.AddWithValue("now", Now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(auditType, ActorUserId: actor, SubjectId: documentId, Detail: detail), ct);
    }

    // ================================================================================================================ create

    /// <summary>
    /// Creates a draft. Idempotent per (document type, creator, key): the same key with the same payload returns the original document (<c>Replayed</c>); the same key with another payload is refused
    /// (<c>idempotency_key_reused</c>). Simultaneous creates with one key produce exactly one document (the unique index decides; the loser returns the winner).
    /// </summary>
    public async Task<ConfigResult<CommercialDocumentView>> CreateAsync(Guid actor, Guid companyId, CommercialDocumentInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<CommercialDocumentView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckBody(input, out var currency, out var note, out var lines);
        var docType = input.DocType?.Trim() ?? "";
        if (!DocTypePattern().IsMatch(docType) || !types.IsRegistered(docType)) errors.Add(new FieldError("docType", "document_type_not_registered"));
        if (input.BranchId is null) errors.Add(new FieldError("branchId", "required"));
        var key = input.IdempotencyKey?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key)) errors.Add(new FieldError("idempotencyKey", "idempotency_key_invalid"));
        if (errors.Count > 0) return ConfigResult<CommercialDocumentView>.Invalid(errors);
        var hash = Canonical(input, docType, currency, note, lines);
        var id = Guid.NewGuid();
        ConfigResult<Guid> r;
        try { r = await Mutate(companyId, async work =>
        {
            var existing = await FindByKeyAsync(work, docType, actor, key, ct);
            if (existing is { } e) throw ReplayOrRefuse(e, hash);
            await RequireBranchAndParty(work, input.BranchId!.Value, input.PartyId!.Value, ct);
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {CD.Table} ({CD.Id}, {CD.DocType}, {CD.BranchId}, {CD.PartyId}, {CD.Currency}, {CD.DocumentDate}, {CD.Mode}, {CD.Status}, {CD.Note}, {CD.IdempotencyKey}, {CD.PayloadHash}, {CD.Revision}, {CD.CreatedBy}, {CD.CreatedAtUtc}, {CD.UpdatedAtUtc}) " +
                "VALUES (@id, @dt, @br, @p, @cur, @d, @m, 1, @note, @key, @h, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("dt", docType); ins.Parameters.AddWithValue("br", input.BranchId!.Value); ins.Parameters.AddWithValue("p", input.PartyId!.Value);
                ins.Parameters.AddWithValue("cur", currency); ins.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = input.DocumentDate!.Value }); ins.Parameters.AddWithValue("m", (short)input.Mode!.Value);
                ins.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value }); ins.Parameters.AddWithValue("key", key); ins.Parameters.AddWithValue("h", hash);
                ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await InsertLinesAsync(work, id, lines, ct);
            await AppendEventAsync(work, id, CommercialEventKind.Created, null, CommercialStatus.Draft, 1, actor, null, CommercialAuditEvents.Created, $"commercial document created type={docType} lines={lines.Count}", ct);
            return id;
        }, ct); }
        catch (Replay replay) { return ConfigResult<CommercialDocumentView>.Ok((await GetAsync(companyId, replay.Id, ct))! with { Replayed = true }); }
        if (r.IsOk) return ConfigResult<CommercialDocumentView>.Ok((await GetAsync(companyId, r.Value, ct))!);
        if (r.Code == "idempotency_key_reused") return ConfigResult<CommercialDocumentView>.Fail(ConfigStatus.Conflict, "idempotency_key_reused");
        // a concurrent create with the same key won the unique index: return it (or refuse a different payload)
        if (r.Code == "idempotency_race") return await ResolveRaceAsync(companyId, docType, actor, key, hash, ct) ?? ConfigResult<CommercialDocumentView>.Fail(r.Status, r.Code!);
        return ConfigResult<CommercialDocumentView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    private static Exception ReplayOrRefuse((Guid Id, string Hash) existing, string hash) =>
        existing.Hash == hash ? new Replay(existing.Id) : new Abort(ConfigStatus.Conflict, "idempotency_key_reused");

    private static async Task<(Guid Id, string Hash)?> FindByKeyAsync(CompanyAuditedWork work, string docType, Guid actor, string key, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {CD.Id}, {CD.PayloadHash} FROM {CD.Table} WHERE {CD.DocType} = @t AND {CD.CreatedBy} = @u AND {CD.IdempotencyKey} = @k", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("t", docType); q.Parameters.AddWithValue("u", actor); q.Parameters.AddWithValue("k", key);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1).Trim()) : null;
    }

    private async Task<ConfigResult<CommercialDocumentView>?> ResolveRaceAsync(Guid companyId, string docType, Guid actor, string key, string hash, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {CD.Id}, {CD.PayloadHash} FROM {CD.Table} WHERE {CD.DocType} = @t AND {CD.CreatedBy} = @u AND {CD.IdempotencyKey} = @k", conn);
        q.Parameters.AddWithValue("t", docType); q.Parameters.AddWithValue("u", actor); q.Parameters.AddWithValue("k", key);
        Guid id; string stored;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0); stored = r.GetString(1).Trim();
        }
        if (stored != hash) return ConfigResult<CommercialDocumentView>.Fail(ConfigStatus.Conflict, "idempotency_key_reused");
        return ConfigResult<CommercialDocumentView>.Ok((await LoadAsync(conn, null, id, false, true, ct))! with { Replayed = true });
    }

    // ================================================================================================================ update and transitions

    private async Task<ConfigResult<CommercialDocumentView>> Transition(Guid actor, Guid companyId, Guid id, long? expectedRevision,
        Func<CompanyAuditedWork, CommercialDocumentView, Task> change, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<CommercialDocumentView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var r = await Mutate(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, false, ct) ?? throw new Abort(ConfigStatus.NotFound, "commercial_document_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            await change(work, cur);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<CommercialDocumentView>.Ok((await GetAsync(companyId, id, ct))!) : ConfigResult<CommercialDocumentView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    private async Task SetStatusAsync(CompanyAuditedWork work, CommercialDocumentView cur, CommercialStatus to, Guid? submittedBy, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.Status} = @s, {CD.SubmittedBy} = @sb, {CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction);
        upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("s", (short)to); upd.Parameters.Add(Uuid("sb", submittedBy)); upd.Parameters.AddWithValue("now", Now);
        await upd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replaces the editable fields and the whole line set of a DRAFT (revision-fenced). The type, branch and key never change. Material edits of an approved document are slice S1b.</summary>
    public Task<ConfigResult<CommercialDocumentView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CommercialDocumentInput? input, CancellationToken ct = default)
    {
        if (input is null) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid([new FieldError("input", "required")]));
        var errors = CheckBody(input, out var currency, out var note, out var lines);
        if (errors.Count > 0) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid(errors));
        return Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Draft) throw new Abort(ConfigStatus.Conflict, "document_not_draft");
            if (input.DocType is { } t && !string.Equals(t.Trim(), cur.DocType, StringComparison.Ordinal)) throw Invalid("docType", "doc_type_immutable");
            if (input.BranchId is { } b && b != cur.BranchId) throw Invalid("branchId", "branch_immutable");
            await RequireBranchAndParty(work, cur.BranchId, input.PartyId!.Value, ct);
            await using (var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.PartyId} = @p, {CD.Currency} = @cur, {CD.DocumentDate} = @d, {CD.Mode} = @m, {CD.Note} = @note, {CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("p", input.PartyId!.Value); upd.Parameters.AddWithValue("cur", currency);
                upd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = input.DocumentDate!.Value }); upd.Parameters.AddWithValue("m", (short)input.Mode!.Value);
                upd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value }); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await using (var del = new NpgsqlCommand($"DELETE FROM {CL.Table} WHERE {CL.DocumentId} = @id", work.Connection, work.Transaction)) { del.Parameters.AddWithValue("id", cur.Id); await del.ExecuteNonQueryAsync(ct); }
            await InsertLinesAsync(work, cur.Id, lines, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Updated, CommercialStatus.Draft, CommercialStatus.Draft, cur.Revision + 1, actor, null, CommercialAuditEvents.Updated, $"commercial document updated r{cur.Revision + 1} lines={lines.Count}", ct);
        }, ct);
    }

    /// <summary>Draft to submitted. A document needs at least one line to be submitted; the submitter is recorded (the independent-approver rule of S1b compares against it).</summary>
    public Task<ConfigResult<CommercialDocumentView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Draft) throw new Abort(ConfigStatus.Conflict, "document_not_draft");
            await using (var c = new NpgsqlCommand($"SELECT count(*) FROM {CL.Table} WHERE {CL.DocumentId} = @id", work.Connection, work.Transaction)) { c.Parameters.AddWithValue("id", cur.Id); if ((long)(await c.ExecuteScalarAsync(ct))! == 0) throw Invalid("lines", "lines_required"); }
            await SetStatusAsync(work, cur, CommercialStatus.Submitted, actor, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Submitted, CommercialStatus.Draft, CommercialStatus.Submitted, cur.Revision + 1, actor, null, CommercialAuditEvents.Submitted, $"commercial document submitted r{cur.Revision + 1}", ct);
        }, ct);

    /// <summary>Submitted back to draft (withdrawal): the submitter record is cleared and the lines become editable again.</summary>
    public Task<ConfigResult<CommercialDocumentView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Submitted) throw new Abort(ConfigStatus.Conflict, "document_not_submitted");
            await SetStatusAsync(work, cur, CommercialStatus.Draft, null, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Withdrawn, CommercialStatus.Submitted, CommercialStatus.Draft, cur.Revision + 1, actor, null, CommercialAuditEvents.Withdrawn, $"commercial document withdrawn r{cur.Revision + 1}", ct);
        }, ct);

    /// <summary>Draft or submitted to cancelled (final), with a mandatory reason. The cancellation of an APPROVED document and the voiding of its number are slice S1b.</summary>
    public Task<ConfigResult<CommercialDocumentView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length is < 1 or > 500 || text.Any(char.IsControl)) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid([new FieldError("reason", "reason_required")]));
        return Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status is not (CommercialStatus.Draft or CommercialStatus.Submitted)) throw new Abort(ConfigStatus.Conflict, "document_not_cancellable");
            await SetStatusAsync(work, cur, CommercialStatus.Cancelled, cur.SubmittedBy, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Cancelled, cur.Status, CommercialStatus.Cancelled, cur.Revision + 1, actor, text, CommercialAuditEvents.Cancelled, $"commercial document cancelled r{cur.Revision + 1}", ct);
        }, ct);
    }
}
