using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using DL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentLink;
using SC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentShortClose;

namespace Hasm.Infrastructure.Commercial;

/// <summary>
/// The audited company-side store of links between commercial documents (Phase 3.13 slice S2, decisions D-074..D-077). <b>No authorization here by design:</b> <see cref="DocumentLinkManagementService"/> wraps it.
/// A link is written in ONE company transaction that mirrors the PostgreSQL guard in the same order: the two lines are locked (in id order), the two documents are share-locked, then every rule is checked with the stable code
/// a caller needs, and the link and its audit event commit together. PostgreSQL re-enforces every rule (a direct statement is refused the same way), so a race that slips past these checks still cannot over-fulfil.
/// Quantities are DERIVED from the link rows (<see cref="QuantitiesAsync"/>); nothing is stored per line. Links are append-only and idempotent per (creator, key). Both documents must be APPROVED, share party, currency and
/// branch, and the (source type, target type, kind) pair must be registered (production registers none until 3.15/3.16). Q3 default (owner decision pending): tolerance 0, no closing short.
/// </summary>
public sealed partial class DocumentLinkService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock, ICommercialDocumentTypeRegistry types)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code);
    }
    private sealed class Replay(Guid id) : Exception("replay") { public Guid Id { get; } = id; }
    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);
    private static Abort NotFoundDocument(string field) => Invalid(field, "document_not_found");   // a missing document and one of another branch look the same (nothing is revealed)

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,64}$")] private static partial Regex KeyPattern();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == DL.UqIdempotency) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "idempotency_race"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == DL.UqPair) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "link_exists"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // safety net: a lock cycle or serialization failure is a retryable conflict, never a 500
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }   // a database guard refused a racing or inconsistent link: nothing was written
    }

    private sealed record DocRow(Guid Id, string DocType, CommercialStatus Status, Guid PartyId, string Currency, Guid BranchId);
    private sealed record LineRow(Guid Id, Guid DocumentId, int LineNo, decimal Quantity);

    private static string Canonical(DocumentLinkInput i, int kind, decimal quantity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', i.FromDocumentId, i.FromLineNo, i.ToDocumentId, i.ToLineNo, kind, quantity.ToString("0.##########", CultureInfo.InvariantCulture))))).ToLowerInvariant();

    // ================================================================================================================ create

    /// <summary>
    /// Creates a link from a line of an approved document to a line of another approved document. Refusals: <c>document_not_found</c> (also another branch), <c>same_document</c>, <c>document_not_approved</c>, <c>party_mismatch</c>,
    /// <c>currency_mismatch</c>, <c>link_not_allowed</c> (the type pair is not registered), <c>exceeds_line_quantity</c>, <c>link_exists</c>, <c>over_fulfilment</c>, <c>idempotency_key_reused</c>. Idempotent: the same key with the same
    /// payload returns the original link (<c>Replayed</c>); concurrent requests for the last remaining quantity create exactly the allowed links (the lines are locked first).
    /// </summary>
    public async Task<ConfigResult<DocumentLinkView>> CreateAsync(Guid actor, Guid companyId, DocumentLinkInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<DocumentLinkView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        if (input.FromDocumentId is null || input.FromDocumentId == Guid.Empty) errors.Add(new("fromDocumentId", "required"));
        if (input.ToDocumentId is null || input.ToDocumentId == Guid.Empty) errors.Add(new("toDocumentId", "required"));
        if (input.FromLineNo is null or < 1 or > 5000) errors.Add(new("fromLineNo", "out_of_range"));
        if (input.ToLineNo is null or < 1 or > 5000) errors.Add(new("toLineNo", "out_of_range"));
        if (input.Kind is null or < 1 or > 4) errors.Add(new("kind", "out_of_range"));
        if (input.Quantity is not { } q || q <= 0 || q >= 1_000_000_000m || decimal.Round(q, 10) != q) errors.Add(new("quantity", "out_of_range"));
        var key = input.IdempotencyKey?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key)) errors.Add(new("idempotencyKey", "idempotency_key_invalid"));
        if (errors.Count > 0) return ConfigResult<DocumentLinkView>.Invalid(errors);
        var kind = (CommercialLinkKind)input.Kind!.Value;
        var quantity = input.Quantity!.Value;
        var hash = Canonical(input, input.Kind.Value, quantity);
        var id = Guid.NewGuid();
        ConfigResult<Guid> r;
        try
        {
            r = await Mutate(companyId, async work =>
            {
                if (await FindByKeyAsync(work.Connection, work.Transaction, actor, key, ct) is { } e) throw e.Hash == hash ? new Replay(e.Id) : new Abort(ConfigStatus.Conflict, "idempotency_key_reused");
                if (input.FromDocumentId == input.ToDocumentId) throw Invalid("toDocumentId", "same_document");
                // lock order = the document guard's order (documents first, then lines): the two documents are share-locked in id order, THEN the branch is compared (a source of another branch is "not found" before any line is looked at),
                // THEN the two lines are locked in id order, and only then is any sum read. A material edit holds the document row first and the lines second, so no cycle can form (F review M1).
                var docs = await LockDocumentsAsync(work, input.FromDocumentId!.Value, input.ToDocumentId!.Value, ct);
                var from = docs[input.FromDocumentId.Value]; var to = docs[input.ToDocumentId.Value];
                if (from.BranchId != to.BranchId) throw NotFoundDocument("fromDocumentId");
                var fromLine = await FindLineAsync(work, from.Id, input.FromLineNo!.Value, ct) ?? throw Invalid("fromLineNo", "line_not_found");
                var toLine = await FindLineAsync(work, to.Id, input.ToLineNo!.Value, ct) ?? throw Invalid("toLineNo", "line_not_found");
                await using (var lk = new NpgsqlCommand($"SELECT {CL.Id} FROM {CL.Table} WHERE {CL.Id} = ANY(@ids) ORDER BY {CL.Id} FOR NO KEY UPDATE", work.Connection, work.Transaction))
                {
                    lk.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { fromLine.Id, toLine.Id } });
                    await using var rd = await lk.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) { }
                }
                // the idempotency key is checked AGAIN now that the lines are locked: a concurrent request with the same key may have committed while this one waited for the line locks, and the first check (before any lock) could not
                // see it. Without this the pair check below would answer link_exists for a same-payload replay (the documented answer is the original link, Replayed). Each statement sees the rows committed so far (read committed).
                if (await FindByKeyAsync(work.Connection, work.Transaction, actor, key, ct) is { } again) throw again.Hash == hash ? new Replay(again.Id) : new Abort(ConfigStatus.Conflict, "idempotency_key_reused");
                if (from.Status != CommercialStatus.Approved) throw new Abort(ConfigStatus.Conflict, "document_not_approved");
                if (to.Status != CommercialStatus.Approved) throw new Abort(ConfigStatus.Conflict, "document_not_approved");
                if (from.PartyId != to.PartyId) throw new Abort(ConfigStatus.Conflict, "party_mismatch");
                if (from.Currency != to.Currency) throw new Abort(ConfigStatus.Conflict, "currency_mismatch");
                if (!types.IsLinkAllowed(from.DocType, to.DocType, kind)) throw Invalid("kind", "link_not_allowed");
                if (quantity > fromLine.Quantity || quantity > toLine.Quantity) throw Invalid("quantity", "exceeds_line_quantity");
                if (await Scalar<bool>(work, $"SELECT EXISTS (SELECT 1 FROM {DL.Table} WHERE {DL.FromLineId} = @a AND {DL.ToLineId} = @b AND {DL.Kind} = @k)", ("a", fromLine.Id), ("b", toLine.Id), ("k", (short)kind))) throw new Abort(ConfigStatus.Conflict, "link_exists");
                if (kind != CommercialLinkKind.Amends)
                {
                    var used = await Scalar<decimal>(work, $"SELECT COALESCE(sum({DL.Quantity}), 0) FROM {DL.Table} WHERE {DL.FromLineId} = @a AND {DL.Kind} = @k", ("a", fromLine.Id), ("k", (short)kind));
                    if (used + quantity > fromLine.Quantity) throw new Abort(ConfigStatus.Conflict, "over_fulfilment");
                    var usedTarget = await Scalar<decimal>(work, $"SELECT COALESCE(sum({DL.Quantity}), 0) FROM {DL.Table} WHERE {DL.ToLineId} = @a AND {DL.Kind} = @k", ("a", toLine.Id), ("k", (short)kind));
                    if (usedTarget + quantity > toLine.Quantity) throw new Abort(ConfigStatus.Conflict, "over_fulfilment");
                }
                await using (var ins = new NpgsqlCommand($"INSERT INTO {DL.Table} ({DL.Id}, {DL.FromDocumentId}, {DL.FromLineId}, {DL.ToDocumentId}, {DL.ToLineId}, {DL.Kind}, {DL.Quantity}, {DL.IdempotencyKey}, {DL.PayloadHash}, {DL.CreatedBy}, {DL.CreatedAtUtc}) " +
                    "VALUES (@id, @fd, @fl, @td, @tl, @k, @q, @key, @h, @by, @now)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("fd", from.Id); ins.Parameters.AddWithValue("fl", fromLine.Id); ins.Parameters.AddWithValue("td", to.Id); ins.Parameters.AddWithValue("tl", toLine.Id);
                    ins.Parameters.AddWithValue("k", (short)kind); ins.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Numeric) { Value = quantity }); ins.Parameters.AddWithValue("key", key); ins.Parameters.AddWithValue("h", hash);
                    ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", Now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(CommercialAuditEvents.LinkCreated, ActorUserId: actor, SubjectId: to.Id, Detail: $"document link created kind={(short)kind} from={from.Id}#{fromLine.LineNo} to={to.Id}#{toLine.LineNo} quantity={quantity.ToString("0.##########", CultureInfo.InvariantCulture)}"), ct);
                return id;
            }, ct);
        }
        catch (Replay replay) { return ConfigResult<DocumentLinkView>.Ok((await GetAsync(companyId, replay.Id, ct))! with { Replayed = true }); }
        if (r.IsOk) return ConfigResult<DocumentLinkView>.Ok((await GetAsync(companyId, r.Value, ct))!);
        if (r.Code == "idempotency_race") return await ResolveRaceAsync(companyId, actor, key, hash, ct) ?? ConfigResult<DocumentLinkView>.Fail(r.Status, r.Code!);
        return ConfigResult<DocumentLinkView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    private static async Task<(Guid Id, string Hash)?> FindByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid actor, string key, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {DL.Id}, {DL.PayloadHash} FROM {DL.Table} WHERE {DL.CreatedBy} = @u AND {DL.IdempotencyKey} = @k", conn, tx);
        q.Parameters.AddWithValue("u", actor); q.Parameters.AddWithValue("k", key);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1).Trim()) : null;
    }

    private async Task<ConfigResult<DocumentLinkView>?> ResolveRaceAsync(Guid companyId, Guid actor, string key, string hash, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        if (await FindByKeyAsync(conn, null, actor, key, ct) is not { } e) return null;
        if (e.Hash != hash) return ConfigResult<DocumentLinkView>.Fail(ConfigStatus.Conflict, "idempotency_key_reused");
        return ConfigResult<DocumentLinkView>.Ok((await LoadAsync(conn, e.Id, ct))! with { Replayed = true });
    }

    private static async Task<T> Scalar<T>(CompanyAuditedWork work, string sql, params (string Name, object Value)[] ps)
    {
        await using var q = new NpgsqlCommand(sql, work.Connection, work.Transaction);
        foreach (var (n, v) in ps) q.Parameters.AddWithValue(n, v);
        return (T)(await q.ExecuteScalarAsync())!;
    }

    private static async Task<LineRow?> FindLineAsync(CompanyAuditedWork work, Guid documentId, int lineNo, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {CL.Id}, {CL.DocumentId}, {CL.LineNo}, {CL.Quantity} FROM {CL.Table} WHERE {CL.DocumentId} = @d AND {CL.LineNo} = @n", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("d", documentId); q.Parameters.AddWithValue("n", lineNo);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new LineRow(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3)) : null;
    }

    private static async Task<Dictionary<Guid, DocRow>> LockDocumentsAsync(CompanyAuditedWork work, Guid a, Guid b, CancellationToken ct)
    {
        var map = new Dictionary<Guid, DocRow>();
        await using var q = new NpgsqlCommand($"SELECT {CD.Id}, {CD.DocType}, {CD.Status}, {CD.PartyId}, {CD.Currency}, {CD.BranchId} FROM {CD.Table} WHERE {CD.Id} = ANY(@ids) ORDER BY {CD.Id} FOR SHARE", work.Connection, work.Transaction);
        q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { a, b } });
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetGuid(0)] = new DocRow(r.GetGuid(0), r.GetString(1), (CommercialStatus)r.GetInt16(2), r.GetGuid(3), r.GetString(4).Trim(), r.GetGuid(5));
        if (!map.ContainsKey(a)) throw NotFoundDocument("fromDocumentId");
        if (!map.ContainsKey(b)) throw NotFoundDocument("toDocumentId");
        return map;
    }

    // ================================================================================================================ reading

    private const string LinkSelect = $"SELECT k.{DL.Id}, k.{DL.FromDocumentId}, fl.{CL.LineNo}, k.{DL.ToDocumentId}, tl.{CL.LineNo}, k.{DL.Kind}, k.{DL.Quantity}, k.{DL.CreatedBy}, k.{DL.CreatedAtUtc} " +
        $"FROM {DL.Table} k JOIN {CL.Table} fl ON fl.{CL.Id} = k.{DL.FromLineId} JOIN {CL.Table} tl ON tl.{CL.Id} = k.{DL.ToLineId}";

    private static DocumentLinkView ReadLink(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetInt32(4), (CommercialLinkKind)r.GetInt16(5), r.GetDecimal(6), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8));

    private static async Task<DocumentLinkView?> LoadAsync(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"{LinkSelect} WHERE k.{DL.Id} = @id", conn);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadLink(r) : null;
    }

    public async Task<DocumentLinkView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, id, ct);
    }

    /// <summary>Every link that touches the document, as source or as target, oldest first.</summary>
    public async Task<IReadOnlyList<DocumentLinkView>> ListAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"{LinkSelect} WHERE k.{DL.FromDocumentId} = @d OR k.{DL.ToDocumentId} = @d ORDER BY k.{DL.CreatedAtUtc}, k.{DL.Id}", conn);
        q.Parameters.AddWithValue("d", documentId);
        var list = new List<DocumentLinkView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadLink(r));
        return list;
    }

    /// <summary>The DERIVED quantities of every line of the document: ordered (the line), fulfilled, invoiced and returned (sums of the links FROM the line). Nothing is stored.</summary>
    public async Task<IReadOnlyList<LineQuantityView>> QuantitiesAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT l.{CL.LineNo}, l.{CL.Quantity}, " +
            $"COALESCE(sum(k.{DL.Quantity}) FILTER (WHERE k.{DL.Kind} = 1), 0), COALESCE(sum(k.{DL.Quantity}) FILTER (WHERE k.{DL.Kind} = 2), 0), COALESCE(sum(k.{DL.Quantity}) FILTER (WHERE k.{DL.Kind} = 3), 0), (SELECT COALESCE(sum(s.{SC.Quantity}), 0) FROM {SC.Table} s WHERE s.{SC.LineId} = l.{CL.Id}) " +
            $"FROM {CL.Table} l LEFT JOIN {DL.Table} k ON k.{DL.FromLineId} = l.{CL.Id} WHERE l.{CL.DocumentId} = @d GROUP BY l.{CL.Id}, l.{CL.LineNo}, l.{CL.Quantity} ORDER BY l.{CL.LineNo}", conn);
        q.Parameters.AddWithValue("d", documentId);
        var list = new List<LineQuantityView>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new LineQuantityView(r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)));
        return list;
    }

    /// <summary>Whether any link touches the document (used by the document service: a linked document stays approved, D-075).</summary>
    public static async Task<bool> HasLinksAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid documentId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {DL.Table} WHERE {DL.FromDocumentId} = @d OR {DL.ToDocumentId} = @d)", conn, tx);
        q.Parameters.AddWithValue("d", documentId);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }
}
