using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Taxes;
using Npgsql;
using NpgsqlTypes;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using CDA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentApproval;
using CDS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentSnapshot;

namespace Hasm.Infrastructure.Commercial;

/// <summary>
/// The audited company-side store of commercial documents (Phase 3.13 slice S1a): the commercial-status track only (draft, submitted, cancelled). <b>No authorization here by design:</b>
/// <see cref="CommercialDocumentManagementService"/> wraps it. Every write is ONE company transaction: the document row is locked, the revision is fenced, the change, its event row and its audit event commit together.
/// Nothing is ever removed (a draft's lines are replaced only while it is a draft, by PostgreSQL's guard). Creation is idempotent per (document type, creator, key); a key reused with another payload is refused.
/// S1a carries no price, tax, cost, approval, numbering, stock or ledger effect (docs/PHASE-3.13.md).
/// </summary>
public sealed partial class CommercialDocumentService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock, ICommercialDocumentTypeRegistry types, PricingConfigService pricing, TaxConfigService taxes)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }
    /// <summary>A replayed create writes nothing (so it carries no audit event): the transaction is rolled back by this exception and the original is returned.</summary>
    private sealed class Replay(Guid id) : Exception("replay") { public Guid Id { get; } = id; }
    /// <summary>Names the offending reference by its constraint (the line index is not known at that point, so a line reference is reported on "lines").</summary>
    private static FieldError FkField(string? constraint) =>
        constraint == CL.FkItem ? new("lines", "item_not_found") : constraint == CL.FkUnit ? new("lines", "unit_not_found") : constraint == CL.FkWarehouse ? new("lines", "warehouse_not_found")
        : constraint == CD.FkBranch ? new("branchId", "branch_not_found") : constraint == CD.FkParty ? new("partyId", "party_not_found") : new("reference", "reference_not_found");
    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")] private static partial Regex DocTypePattern();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyPattern();
    [GeneratedRegex("^[A-Za-z0-9._:-]{8,64}$")] private static partial Regex KeyPattern();

    private const int MaxLines = 5000;

    /// <summary>The names of the registered document types (empty in production until 3.15/3.16, the test type only in the test composition). Names only: the screen offers Create only when this is not empty.</summary>
    public IReadOnlyList<string> RegisteredTypes => [.. types.Types.Order(StringComparer.Ordinal)];

    /// <summary>The registered link pairs (both types registered): static registry data.</summary>
    public IReadOnlyList<LinkPairView> RegisteredLinkPairs => types.LinkPairs;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static NpgsqlParameter Uuid(string name, Guid? v) => new(name, NpgsqlDbType.Uuid) { Value = v is { } g ? g : DBNull.Value };

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == CD.UqIdempotency) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "idempotency_race"); }   // a concurrent create with the same key won
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // any other unique index (line number, event revision): a racing writer won, nothing was written
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return ConfigResult<T>.Invalid([FkField(ex.ConstraintName)]); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // safety net: a lock cycle or serialization failure is a retryable conflict, never a 500
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }   // a database guard refused a racing or inconsistent change: nothing was written
    }

    // ================================================================================================================ reading

    private const string DocCols = $"{CD.Id}, {CD.DocType}, {CD.BranchId}, {CD.PartyId}, {CD.Currency}, {CD.DocumentDate}, {CD.Mode}, {CD.Status}, {CD.Note}, {CD.SubmittedBy}, {CD.Revision}, {CD.CreatedBy}, {CD.CreatedAtUtc}, {CD.UpdatedAtUtc}, {CDA.OfficialNumber}, {CDA.ApprovedBy}, {CDA.ApprovedAtUtc}, {CDA.SealedAtUtc}, {CDA.ExchangeRate}, {CDA.TotalNet}, {CDA.TotalTax}, {CDA.TotalGross}, {CDA.LastEditedBy}, {CDA.Fingerprint}, {CDA.FiscalYearId}";

    private static CommercialDocumentView ReadHeader(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4).Trim(), r.GetFieldValue<DateOnly>(5), (CommercialMode)r.GetInt16(6), (CommercialStatus)r.GetInt16(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetInt64(10), r.GetGuid(11), r.GetFieldValue<DateTimeOffset>(12), r.GetFieldValue<DateTimeOffset>(13), [], [], false,
        r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetGuid(15), r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16), r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17),
        r.IsDBNull(18) ? null : r.GetDecimal(18), r.IsDBNull(19) ? null : r.GetDecimal(19), r.IsDBNull(20) ? null : r.GetDecimal(20), r.IsDBNull(21) ? null : r.GetDecimal(21), r.IsDBNull(22) ? null : r.GetGuid(22), r.IsDBNull(23) ? null : r.GetString(23).Trim(), r.IsDBNull(24) ? null : r.GetGuid(24));

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
        await using (var q = new NpgsqlCommand($"SELECT {CL.LineNo}, {CL.ItemId}, {CL.UnitId}, {CL.Quantity}, {CL.WarehouseId}, {CDS.LineNet}, {CDS.PricingHash} FROM {CL.Table} WHERE {CL.DocumentId} = @id ORDER BY {CL.LineNo}", conn, tx))
        {
            q.Parameters.AddWithValue("id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines.Add(new CommercialLineView(r.GetInt32(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetString(6).Trim()));
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
    public async Task<IReadOnlyList<CommercialDocumentView>> ListAsync(Guid companyId, CommercialDocumentFilter filter, CancellationToken ct = default) => (await ListPageAsync(companyId, filter, ct)).Items;

    /// <summary>
    /// One page of headers with the exact total. Every filter is a bound parameter and is applied before paging (newest first); the search is a literal, case-insensitive part of the official number (wildcards of the text are escaped).
    /// </summary>
    public async Task<CommercialDocumentPage> ListPageAsync(Guid companyId, CommercialDocumentFilter filter, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = new List<string>();
        await using var q = new NpgsqlCommand { Connection = conn };
        if (filter.Status is { } st) { where.Add($"{CD.Status} = @st"); q.Parameters.AddWithValue("st", (short)st); }
        if (!string.IsNullOrWhiteSpace(filter.DocType)) { where.Add($"{CD.DocType} = @dt"); q.Parameters.AddWithValue("dt", filter.DocType); }
        if (filter.BranchId is { } b) { where.Add($"{CD.BranchId} = @br"); q.Parameters.AddWithValue("br", b); }
        if (filter.PartyId is { } pid) { where.Add($"{CD.PartyId} = @pa"); q.Parameters.AddWithValue("pa", pid); }
        if (!string.IsNullOrWhiteSpace(filter.Currency)) { where.Add($"{CD.Currency} = @cu"); q.Parameters.AddWithValue("cu", filter.Currency.Trim().ToUpperInvariant()); }
        if (ListPaging.TryCleanSearch(filter.Search, out var cleaned) && cleaned is not null) { where.Add(ListPaging.LikeClause("like", CDA.OfficialNumber)); q.Parameters.AddWithValue("like", ListPaging.LikePattern(cleaned)); }
        if (filter.AllowedBranches is { } allowed) { where.Add($"{CD.BranchId} = ANY(@ab)"); q.Parameters.Add(new NpgsqlParameter("ab", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = allowed.ToArray() }); }
        var fromWhere = $"FROM {CD.Table}{(where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))}";
        var off = Math.Max(0, filter.Offset);
        q.CommandText = $"SELECT {DocCols}, count(*) OVER () {fromWhere} ORDER BY {CD.CreatedAtUtc} DESC, {CD.Id} LIMIT @lim OFFSET @off";
        q.Parameters.AddWithValue("lim", Math.Clamp(filter.Limit, 1, 200)); q.Parameters.AddWithValue("off", off);
        var rows = new List<CommercialDocumentView>(); long total = 0;
        await using (var r = await q.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) { rows.Add(ReadHeader(r)); total = r.GetInt64(25); }
        if (rows.Count == 0 && off > 0) total = await PagedSql.TotalAsync(conn, fromWhere, q.Parameters, ct);
        return new CommercialDocumentPage(rows, total);
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
            await using (var ins = new NpgsqlCommand($"INSERT INTO {CD.Table} ({CD.Id}, {CD.DocType}, {CD.BranchId}, {CD.PartyId}, {CD.Currency}, {CD.DocumentDate}, {CD.Mode}, {CD.Status}, {CD.Note}, {CD.IdempotencyKey}, {CD.PayloadHash}, {CD.Revision}, {CD.CreatedBy}, {CD.CreatedAtUtc}, {CD.UpdatedAtUtc}, {CDA.LastEditedBy}) " +
                "VALUES (@id, @dt, @br, @p, @cur, @d, @m, 1, @note, @key, @h, 1, @by, @now, @now, @by)", work.Connection, work.Transaction))
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
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "commercial_document_not_found");
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

    /// <summary>
    /// Moves the document back to DRAFT (withdrawal, re-approval after a material edit, reopening of a rejected document): the submitter, approval, seal, fingerprint, snapshots, rate and totals are cleared and the
    /// line snapshots with them (a draft carries none); the OFFICIAL NUMBER and its fiscal year stay (D-050). One revision, one event.
    /// </summary>
    private async Task ToDraftAsync(CompanyAuditedWork work, CommercialDocumentView cur, CommercialEventKind kind, short auditType, Guid actor, string? reason, string detail, CancellationToken ct)
    {
        await using (var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.Status} = 1, {CD.SubmittedBy} = NULL, {CDA.ApprovedBy} = NULL, {CDA.ApprovedAtUtc} = NULL, {CDA.SealedAtUtc} = NULL, {CDA.Fingerprint} = NULL, {CDA.TaxSnapshot} = NULL, {CDA.TaxHash} = NULL, " +
            $"{CDA.ExchangeRate} = NULL, {CDA.ExchangeRateRef} = NULL, {CDA.TotalNet} = NULL, {CDA.TotalTax} = NULL, {CDA.TotalGross} = NULL, {CDA.LastEditedBy} = @by, {CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction))
        {
            upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", Now);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await using (var lines = new NpgsqlCommand($"UPDATE {CL.Table} SET {CDS.PricingSnapshot} = NULL, {CDS.PricingHash} = NULL, {CDS.LineNet} = NULL WHERE {CL.DocumentId} = @id", work.Connection, work.Transaction))
        {
            lines.Parameters.AddWithValue("id", cur.Id);
            await lines.ExecuteNonQueryAsync(ct);   // allowed: the document is a draft again
        }
        await AppendEventAsync(work, cur.Id, kind, cur.Status, CommercialStatus.Draft, cur.Revision + 1, actor, reason, auditType, detail, ct);
    }

    private static bool SameLines(IReadOnlyList<CommercialLineView> current, IReadOnlyList<CommercialLineInput> input)
    {
        if (current.Count != input.Count) return false;
        for (var n = 0; n < current.Count; n++)
            if (current[n].ItemId != input[n].ItemId || current[n].UnitId != input[n].UnitId || current[n].Quantity != input[n].Quantity || current[n].WarehouseId != input[n].WarehouseId) return false;
        return true;
    }

    /// <summary>
    /// Edits a document. A DRAFT is replaced as a whole (revision-fenced); a REJECTED document is reopened to draft first (event 9); an APPROVED document with no effect changes only its NOTE in place, while any MATERIAL
    /// change (party, currency, date, any line item, unit, quantity or warehouse) sends it back to draft through re-approval (event 8): its approval, seal and snapshots are dropped, its official number is KEPT (D-050).
    /// The mode is frozen once the document is numbered (D-052). The type, branch and key never change. A submitted or cancelled document is not edited.
    /// </summary>
    public Task<ConfigResult<CommercialDocumentView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CommercialDocumentInput? input, CancellationToken ct = default)
    {
        if (input is null) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid([new FieldError("input", "required")]));
        var errors = CheckBody(input, out var currency, out var note, out var lines);
        if (errors.Count > 0) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid(errors));
        return Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status is CommercialStatus.Submitted or CommercialStatus.Cancelled) throw new Abort(ConfigStatus.Conflict, "document_not_draft");
            if (input.DocType is { } t && !string.Equals(t.Trim(), cur.DocType, StringComparison.Ordinal)) throw Invalid("docType", "doc_type_immutable");
            if (input.BranchId is { } b && b != cur.BranchId) throw Invalid("branchId", "branch_immutable");
            if (cur.OfficialNumber is not null && (CommercialMode)input.Mode!.Value != cur.Mode) throw Invalid("mode", "mode_frozen");   // frozen at the first approval
            await RequireBranchAndParty(work, cur.BranchId, input.PartyId!.Value, ct);
            if (!await DateInNumberYearAsync(work, cur.FiscalYearId, input.DocumentDate!.Value, ct)) throw Invalid("documentDate", "date_outside_number_year");   // D-068: a numbered document keeps its date inside the fiscal year of its number
            var working = cur;
            if (cur.Status == CommercialStatus.Approved)
            {
                var material = input.PartyId != cur.PartyId || currency != cur.Currency || input.DocumentDate != cur.DocumentDate || !SameLines(cur.Lines, lines);
                if (!material)
                {
                    // a non-material edit of an approved document: only the note, in place (the guard allows nothing else)
                    await using (var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.Note} = @note, {CDA.LastEditedBy} = @by, {CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction))
                    {
                        upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", Now);
                        upd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    await AppendEventAsync(work, cur.Id, CommercialEventKind.Updated, CommercialStatus.Approved, CommercialStatus.Approved, cur.Revision + 1, actor, null, CommercialAuditEvents.Updated, $"approved commercial document note updated r{cur.Revision + 1}", ct);
                    return;
                }
                if (await DocumentLinkService.HasLinksAsync(work.Connection, work.Transaction, cur.Id, ct)) throw new Abort(ConfigStatus.Conflict, "document_has_links");   // D-075: a linked document stays approved (a material edit would send it back to draft)
                await ToDraftAsync(work, cur, CommercialEventKind.ReapprovalRequested, CommercialAuditEvents.ReapprovalRequested, actor, null, $"material edit of approved commercial document: re-approval required, number {cur.OfficialNumber} kept", ct);
                working = cur with { Status = CommercialStatus.Draft, Revision = cur.Revision + 1 };
            }
            else if (cur.Status == CommercialStatus.Rejected)
            {
                await ToDraftAsync(work, cur, CommercialEventKind.Reopened, CommercialAuditEvents.Reopened, actor, null, "rejected commercial document reopened as a draft", ct);
                working = cur with { Status = CommercialStatus.Draft, Revision = cur.Revision + 1 };
            }
            await using (var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.PartyId} = @p, {CD.Currency} = @cur, {CD.DocumentDate} = @d, {CD.Mode} = @m, {CD.Note} = @note, {CDA.LastEditedBy} = @by, {CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("p", input.PartyId!.Value); upd.Parameters.AddWithValue("cur", currency); upd.Parameters.AddWithValue("by", actor);
                upd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = input.DocumentDate!.Value }); upd.Parameters.AddWithValue("m", (short)input.Mode!.Value);
                upd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value }); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await using (var del = new NpgsqlCommand($"DELETE FROM {CL.Table} WHERE {CL.DocumentId} = @id", work.Connection, work.Transaction)) { del.Parameters.AddWithValue("id", cur.Id); await del.ExecuteNonQueryAsync(ct); }
            await InsertLinesAsync(work, cur.Id, lines, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Updated, CommercialStatus.Draft, CommercialStatus.Draft, working.Revision + 1, actor, null, CommercialAuditEvents.Updated, $"commercial document updated r{working.Revision + 1} lines={lines.Count}", ct);
        }, ct);
    }

    /// <summary>True when the document has no number yet, or the date lies inside the fiscal year the number was issued in (D-068; the 0029 guard enforces the same rule).</summary>
    private static async Task<bool> DateInNumberYearAsync(CompanyAuditedWork work, Guid? fiscalYearId, DateOnly date, CancellationToken ct)
    {
        if (fiscalYearId is null) return true;
        await using var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {FY.Table} WHERE {FY.Id} = @y AND @d BETWEEN {FY.StartDate} AND {FY.EndDate})", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("y", fiscalYearId.Value); q.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = date });
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }

    // ================================================================================================================ the server-produced quote (price, tax, rate)

    private sealed record DocumentQuote(IReadOnlyList<(string Json, string Hash, decimal Net)> Lines, string TaxJson, string TaxHash, decimal Rate, string RateRef, decimal TotalNet, decimal TotalTax, decimal TotalGross, string Fingerprint);

    /// <summary>
    /// Prices and taxes the whole document THROUGH THE SERVER PATH, inside the caller's transaction: every line through the pricing quote (the item group and the customer group come from stored data), the taxes through the
    /// tax resolver and engine (the policy is the company's saved policy: without one the document is refused), and the exchange rate from the base currency or the dated rate table. Nothing is taken from the client.
    /// The fingerprint covers every pricing hash, the tax hash, the rate and the currency, so the approval can prove that nothing changed since the submit (<c>pricing_changed</c>).
    /// </summary>
    private async Task<DocumentQuote> QuoteDocumentAsync(CompanyAuditedWork work, CommercialDocumentView doc, CancellationToken ct)
    {
        var (rate, rateRef) = await ResolveRateAsync(work, doc.Currency, doc.DocumentDate, ct);
        var lines = new List<(string, string, decimal)>();
        var taxLines = new List<TaxLineInput>();
        TaxPolicy? policy = null;
        for (var n = 0; n < doc.Lines.Count; n++)
        {
            var line = doc.Lines[n];
            var q = await pricing.QuoteInWorkAsync(work.Connection, work.Transaction!, new PricingRequest(doc.DocumentDate, doc.Currency, line.ItemId, null, line.UnitId, line.Quantity, doc.PartyId), null, ct);
            if (!q.IsOk) throw Invalid($"lines[{n}]", q.Fields?.FirstOrDefault()?.Code ?? q.Code ?? "pricing_failed");
            var snapshot = q.Value!;
            var tax = await taxes.ResolveInWorkAsync(work.Connection, work.Transaction!, doc.DocType, CommercialDefaults.TaxOperation, line.ItemId, doc.PartyId, doc.DocumentDate, ct);
            if (!tax.IsOk) throw Invalid($"lines[{n}]", tax.Code ?? "tax_failed");
            policy ??= tax.Value!.Policy;
            lines.Add((System.Text.Json.JsonSerializer.Serialize(snapshot), snapshot.ContentHash, snapshot.Result.LineNet));
            taxLines.Add(new TaxLineInput(n + 1, line.Quantity, snapshot.Result.NetUnitPrice, 0m, false, tax.Value!.Components));
        }
        if (policy is null) throw Invalid("lines", "lines_required");
        TaxSnapshot taxSnapshot;
        try { taxSnapshot = TaxSnapshots.Create(policy, taxLines, ct); }
        catch (TaxException ex) { throw Invalid("lines", ex.Code); }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", lines.Select(l => l.Item2)) + "|" + taxSnapshot.ContentHash + "|" + rate.ToString("0.############", CultureInfo.InvariantCulture) + "|" + doc.Currency))).ToLowerInvariant();
        var total = taxSnapshot.Result;
        return new DocumentQuote(lines, System.Text.Json.JsonSerializer.Serialize(taxSnapshot), taxSnapshot.ContentHash, rate, rateRef, total.TotalNet, total.TotalTax, total.TotalGross, fingerprint);
    }

    private static async Task<(decimal Rate, string Ref)> ResolveRateAsync(CompanyAuditedWork work, string currency, DateOnly date, CancellationToken ct)
    {
        string? baseCurrency;
        await using (var q = new NpgsqlCommand($"SELECT {CC.Code} FROM {CC.Table} WHERE {CC.IsBase}", work.Connection, work.Transaction)) baseCurrency = (await q.ExecuteScalarAsync(ct)) as string;
        if (baseCurrency is null) throw Invalid("currency", "base_currency_missing");
        if (string.Equals(baseCurrency.Trim(), currency, StringComparison.OrdinalIgnoreCase)) return (1m, "base");
        var applied = await ExchangeRateService.ResolveAsync(work.Connection, work.Transaction, currency, date, ct) ?? throw Invalid("currency", "exchange_rate_missing");
        return (applied.Rate, "rate:" + applied.RateId.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task WriteQuoteAsync(CompanyAuditedWork work, CommercialDocumentView cur, DocumentQuote quote, CancellationToken ct)
    {
        for (var n = 0; n < cur.Lines.Count; n++)
        {
            await using var upd = new NpgsqlCommand($"UPDATE {CL.Table} SET {CDS.PricingSnapshot} = @s, {CDS.PricingHash} = @h, {CDS.LineNet} = @net WHERE {CL.DocumentId} = @d AND {CL.LineNo} = @n", work.Connection, work.Transaction);
            upd.Parameters.AddWithValue("d", cur.Id); upd.Parameters.AddWithValue("n", cur.Lines[n].LineNo); upd.Parameters.AddWithValue("s", quote.Lines[n].Json); upd.Parameters.AddWithValue("h", quote.Lines[n].Hash);
            upd.Parameters.Add(new NpgsqlParameter("net", NpgsqlDbType.Numeric) { Value = quote.Lines[n].Net });
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    // ================================================================================================================ submit, withdraw, approve, reject, cancel

    /// <summary>
    /// Draft to submitted. The document is priced and taxed by the SERVER (price, tax and rate snapshots are produced here, never accepted from the client), at least one line is required, and the fingerprint of that quote
    /// is recorded: the approval re-quotes and refuses any difference. The submitter is recorded (the approver is never the creator, the last editor or the submitter).
    /// </summary>
    public Task<ConfigResult<CommercialDocumentView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Draft) throw new Abort(ConfigStatus.Conflict, "document_not_draft");
            if (cur.Lines.Count == 0) throw Invalid("lines", "lines_required");
            await RequireBranchAndParty(work, cur.BranchId, cur.PartyId, ct);
            var quote = await QuoteDocumentAsync(work, cur, ct);
            await WriteQuoteAsync(work, cur, quote, ct);   // while the document is still a draft (the lines are locked afterwards)
            await using (var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.Status} = 2, {CD.SubmittedBy} = @by, {CDA.Fingerprint} = @fp, {CDA.TaxSnapshot} = @ts, {CDA.TaxHash} = @th, {CDA.ExchangeRate} = @rate, {CDA.ExchangeRateRef} = @rref, " +
                $"{CDA.TotalNet} = @tn, {CDA.TotalTax} = @tt, {CDA.TotalGross} = @tg, {CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("fp", quote.Fingerprint); upd.Parameters.AddWithValue("ts", quote.TaxJson); upd.Parameters.AddWithValue("th", quote.TaxHash);
                upd.Parameters.Add(new NpgsqlParameter("rate", NpgsqlDbType.Numeric) { Value = quote.Rate }); upd.Parameters.AddWithValue("rref", quote.RateRef);
                upd.Parameters.Add(new NpgsqlParameter("tn", NpgsqlDbType.Numeric) { Value = quote.TotalNet }); upd.Parameters.Add(new NpgsqlParameter("tt", NpgsqlDbType.Numeric) { Value = quote.TotalTax }); upd.Parameters.Add(new NpgsqlParameter("tg", NpgsqlDbType.Numeric) { Value = quote.TotalGross });
                upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await BindPlanAsync(work, cur.Id, cur.DocType, cur.Revision + 1, ct);   // the plan this revision is submitted under is a floor (D-140): the policy is read from the SEALED total just written
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Submitted, CommercialStatus.Draft, CommercialStatus.Submitted, cur.Revision + 1, actor, null, CommercialAuditEvents.Submitted, $"commercial document submitted r{cur.Revision + 1} gross={quote.TotalGross.ToString(CultureInfo.InvariantCulture)}", ct);
        }, ct);

    /// <summary>Submitted back to draft (withdrawal): the submitter record, the quote and the snapshots are dropped and the lines become editable again.</summary>
    public Task<ConfigResult<CommercialDocumentView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Submitted) throw new Abort(ConfigStatus.Conflict, "document_not_submitted");
            await ToDraftAsync(work, cur, CommercialEventKind.Withdrawn, CommercialAuditEvents.Withdrawn, actor, null, $"commercial document withdrawn r{cur.Revision + 1}", ct);
        }, ct);

    /// <summary>
    /// Submitted to approved by an INDEPENDENT approver (never the creator, the last editor or the submitter: <c>approver_not_independent</c>), bound to the exact revision (If-Match). In ONE transaction: the document date must
    /// lie in an OPEN fiscal period; the document is priced and taxed AGAIN by the server and must produce exactly the fingerprint recorded at submit (otherwise <c>pricing_changed</c> and nothing changes); the official number is
    /// issued at the FIRST approval (kept through re-approval, D-050); the quote is sealed. Approval creates no stock movement and no ledger entry (later slices).
    /// </summary>
    public Task<ConfigResult<CommercialDocumentView>> ApproveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default, IReadOnlySet<Guid>? approverRoles = null) =>
        Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Submitted) throw new Abort(ConfigStatus.Conflict, "document_not_submitted");
            if (actor == cur.SubmittedBy || actor == cur.CreatedBy || actor == cur.LastEditedBy) throw new Abort(ConfigStatus.Conflict, "approver_not_independent");
            await RequireBranchAndParty(work, cur.BranchId, cur.PartyId, ct);
            if (!await DateInNumberYearAsync(work, cur.FiscalYearId, cur.DocumentDate, ct)) throw new Abort(ConfigStatus.Conflict, "date_outside_number_year");
            var check = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction!, cur.DocumentDate, false, ct);   // locks the period row FOR SHARE until commit
            if (!check.Allowed) throw new Abort(ConfigStatus.Conflict, check.Reason!);
            var quote = await QuoteDocumentAsync(work, cur, ct);
            if (quote.Fingerprint != cur.Fingerprint) throw new Abort(ConfigStatus.Conflict, "pricing_changed");   // the server prices the document again: any difference since the submit is refused
            // approval POLICY (S5a, D-140/D-141): when a policy governs the sealed total the plan has ordered stages and this call is ONE decision for the first open stage; the document becomes approved only when the
            // plan of this revision is satisfied. With no matching policy the plan is empty and the single independent approval above stays as it was (S1b).
            var plan = await PlanAsync(work.Connection, work.Transaction, cur.Id, cur.DocType, cur.Revision, ct);
            if (plan.Count > 0)
            {
                var open = plan.FirstOrDefault(p => p.Approved < p.Required) ?? throw new Abort(ConfigStatus.Conflict, "approval_stage_complete");
                if (open.RoleId is { } stageRole && (approverRoles is null || !approverRoles.Contains(stageRole))) throw new Abort(ConfigStatus.Forbidden, "approver_role_required");   // the roles come from the authorization store on this very call
                var decision = await DocumentApprovals.RecordAsync(work, new ApprovalDecisionRequest(cur.DocType, cur.Id, cur.Revision, actor, cur.CreatedBy, cur.SubmittedBy, true, null, open.Stage), Now, ct);
                if (decision.Status == ApprovalRecordStatus.SeparationOfDuties) throw new Abort(ConfigStatus.Conflict, "approver_not_independent");
                if (decision.Status == ApprovalRecordStatus.AlreadyDecided) throw new Abort(ConfigStatus.Conflict, "already_decided");
                plan = await PlanAsync(work.Connection, work.Transaction, cur.Id, cur.DocType, cur.Revision, ct);
                if (plan.Any(p => p.Approved < p.Required)) return;   // a later stage (or approver) is still needed: the document stays submitted and its revision does not move
            }
            string number = cur.OfficialNumber!; Guid? yearId = null;
            if (cur.OfficialNumber is null)
            {
                string branchCode; int yearLabel; Guid year;
                await using (var q = new NpgsqlCommand($"SELECT (SELECT {BR.Code} FROM {BR.Table} WHERE {BR.Id} = @b), EXTRACT(YEAR FROM y.{FY.StartDate})::int, y.{FY.Id} FROM {FP.Table} p JOIN {FY.Table} y ON y.{FY.Id} = p.{FP.YearId} WHERE p.{FP.Id} = @p", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("b", cur.BranchId); q.Parameters.AddWithValue("p", check.PeriodId!.Value);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);
                    branchCode = r.GetString(0); yearLabel = r.GetInt32(1); year = r.GetGuid(2);
                }
                var issued = await NumberingService.IssueForRegisteredTypeAsync(work.Connection, work.Transaction!, cur.DocType, new NumberScope(cur.BranchId, branchCode, year, yearLabel.ToString(CultureInfo.InvariantCulture)), cur.Id, actor, Now, ct);
                number = issued.Number; yearId = year;
            }
            await using (var upd = new NpgsqlCommand($"UPDATE {CD.Table} SET {CD.Status} = 3, {CDA.ApprovedBy} = @by, {CDA.ApprovedAtUtc} = @now, {CDA.SealedAtUtc} = @now, {CDA.OfficialNumber} = @num, {CDA.FiscalYearId} = COALESCE(@fy, {CDA.FiscalYearId}), " +
                $"{CD.Revision} = {CD.Revision} + 1, {CD.UpdatedAtUtc} = @now WHERE {CD.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", cur.Id); upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", Now); upd.Parameters.AddWithValue("num", number); upd.Parameters.Add(Uuid("fy", yearId));
                await upd.ExecuteNonQueryAsync(ct);
            }
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Approved, CommercialStatus.Submitted, CommercialStatus.Approved, cur.Revision + 1, actor, null, CommercialAuditEvents.Approved, $"commercial document approved r{cur.Revision + 1} number={number}", ct);
        }, ct);

    /// <summary>Submitted to rejected with a mandatory reason; the submitter's independence rule is the same as for approval. A rejected document may be edited (reopened as a draft) or cancelled.</summary>
    public Task<ConfigResult<CommercialDocumentView>> RejectAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length is < 1 or > 500 || text.Any(char.IsControl)) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid([new FieldError("reason", "reason_required")]));
        return Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status != CommercialStatus.Submitted) throw new Abort(ConfigStatus.Conflict, "document_not_submitted");
            if (actor == cur.SubmittedBy || actor == cur.CreatedBy || actor == cur.LastEditedBy) throw new Abort(ConfigStatus.Conflict, "approver_not_independent");
            await SetStatusAsync(work, cur, CommercialStatus.Rejected, cur.SubmittedBy, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Rejected, CommercialStatus.Submitted, CommercialStatus.Rejected, cur.Revision + 1, actor, text, CommercialAuditEvents.Rejected, $"commercial document rejected r{cur.Revision + 1}", ct);
        }, ct);
    }

    /// <summary>
    /// Cancels a document (final) with a mandatory reason: a draft, submitted, rejected or APPROVED document, the last only before any physical or accounting effect (D-051; no effect exists yet in S1b, the check is the
    /// <see cref="HasEffectsAsync"/> hook later slices fill). When the document holds an official number it is VOIDED in the numbering ledger in the same transaction and is never issued again (D-050).
    /// </summary>
    public Task<ConfigResult<CommercialDocumentView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length is < 1 or > 500 || text.Any(char.IsControl)) return Task.FromResult(ConfigResult<CommercialDocumentView>.Invalid([new FieldError("reason", "reason_required")]));
        return Transition(actor, companyId, id, expectedRevision, async (work, cur) =>
        {
            if (cur.Status == CommercialStatus.Cancelled) throw new Abort(ConfigStatus.Conflict, "document_not_cancellable");
            if (cur.Status == CommercialStatus.Approved && await HasEffectsAsync(work, cur, ct)) throw new Abort(ConfigStatus.Conflict, "document_has_effects");
            if (cur.Status == CommercialStatus.Approved && await DocumentLinkService.HasLinksAsync(work.Connection, work.Transaction, cur.Id, ct)) throw new Abort(ConfigStatus.Conflict, "document_has_links");   // D-075 interim restriction (stricter than Q8)
            if (cur.OfficialNumber is not null)
                await NumberingService.VoidAsync(work.Connection, work.Transaction!, cur.DocType, cur.OfficialNumber, actor, text.Length > 200 ? text[..200] : text, Now, ct);
            await SetStatusAsync(work, cur, CommercialStatus.Cancelled, cur.SubmittedBy, ct);
            await AppendEventAsync(work, cur.Id, CommercialEventKind.Cancelled, cur.Status, CommercialStatus.Cancelled, cur.Revision + 1, actor, text, CommercialAuditEvents.Cancelled, $"commercial document cancelled r{cur.Revision + 1}{(cur.OfficialNumber is null ? "" : $" number {cur.OfficialNumber} voided")}", ct);
        }, ct);
    }

    /// <summary>Whether a physical (stock) or accounting effect exists for the document. Slice S1b has none; the physical and accounting tracks (S3, S4) replace this with their facts, so a cancel can never undo an effect.</summary>
    private static Task<bool> HasEffectsAsync(CompanyAuditedWork work, CommercialDocumentView cur, CancellationToken ct) => Task.FromResult(false);
}
