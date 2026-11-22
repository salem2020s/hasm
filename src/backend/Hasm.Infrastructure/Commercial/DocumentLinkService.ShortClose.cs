using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// The short close (Phase 3.13, owner decision D-201): "An explicitly authorised short-close action may close the remaining quantity with a reason and audit, but must not invent deliveries, invoices or stock
/// movements." A quantity of a line of an APPROVED document of a short-closable type is recorded as closed in one append-only row (<see cref="SC"/>); NO link, stock document, journal entry or reservation is
/// created. The open quantity of a line is DERIVED: ordered - max(fulfilled, invoiced) - closed. Tolerance 0 stays (D-076): what was delivered or invoiced is never closed, and after a close a link cannot take more than
/// ordered - closed (PostgreSQL enforces both). The whole operation (every row, the audit event) is ONE company transaction, idempotent per (creator, key). There is no authorization here by design:
/// <see cref="DocumentLinkManagementService"/> wraps it. The lock order is the one of a link and of a document edit: the document row (shared) first, its lines second, in id order.
/// </summary>
public sealed partial class DocumentLinkService
{
    private async Task<ConfigResult<T>> MutateShortClose<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == SC.UqIdempotency) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "idempotency_race"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // safety net, as for links
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }   // a database guard refused a racing close: nothing was written
    }

    private static string CanonicalShortClose(Guid documentId, string reason, IReadOnlyList<(int LineNo, decimal Quantity)>? lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', documentId, reason, lines is null ? "whole" : string.Join(',', lines.OrderBy(l => l.LineNo).Select(l => $"{l.LineNo}:{l.Quantity.ToString("0.##########", CultureInfo.InvariantCulture)}")))))).ToLowerInvariant();

    /// <summary>
    /// Closes the open remainder of an approved order short. <paramref name="input"/>.<c>Lines</c> null closes the whole open remainder of every line (<c>nothing_to_close</c> when none is open), otherwise exactly the named
    /// quantities. Refusals: <c>commercial_document_not_found</c>, <c>revision_conflict</c> (412), <c>document_not_approved</c>, <c>short_close_not_allowed_for_type</c>, <c>line_not_found</c>, <c>exceeds_open_quantity</c>,
    /// <c>nothing_to_close</c>, <c>reason_required</c>, <c>idempotency_key_reused</c>. A replay (same creator, key and payload) returns the original operation (<c>Replayed</c>). The document row is NOT changed
    /// (its revision stays), so an approved document is never touched by a close; the revision is checked only so that a client acts on the version it saw.
    /// </summary>
    public async Task<ConfigResult<ShortCloseView>> ShortCloseAsync(Guid actor, Guid companyId, Guid documentId, long? expectedRevision, ShortCloseInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ShortCloseView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<ShortCloseView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        var reason = input.Reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 500 || reason.Any(char.IsControl)) errors.Add(new("reason", "reason_required"));
        var key = input.IdempotencyKey?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key)) errors.Add(new("idempotencyKey", "idempotency_key_invalid"));
        List<(int LineNo, decimal Quantity)>? named = null;
        if (input.Lines is not null)
        {
            if (input.Lines.Count is < 1 or > 5000) errors.Add(new("lines", "out_of_range"));
            else
            {
                named = [];
                foreach (var l in input.Lines)
                {
                    if (l.LineNo is null or < 1 or > 5000) { errors.Add(new("lines", "out_of_range")); break; }
                    if (l.Quantity is not { } q || q <= 0 || q >= 1_000_000_000m || decimal.Round(q, 10) != q) { errors.Add(new("lines", "out_of_range")); break; }
                    if (named.Any(n => n.LineNo == l.LineNo)) { errors.Add(new("lines", "duplicate_line")); break; }
                    named.Add((l.LineNo.Value, q));
                }
            }
        }
        if (errors.Count > 0) return ConfigResult<ShortCloseView>.Invalid(errors);
        var hash = CanonicalShortClose(documentId, reason, named);
        var operation = Guid.NewGuid();
        ConfigResult<Guid> r;
        try
        {
            r = await MutateShortClose(companyId, async work =>
            {
                if (await FindShortCloseByKeyAsync(work.Connection, work.Transaction, actor, key, ct) is { } e) throw e.Hash == hash ? new Replay(e.Operation) : new Abort(ConfigStatus.Conflict, "idempotency_key_reused");
                // the document row first (shared: a concurrent cancel or re-approval waits, a link or another close does not), THEN every line in id order, and only then any sum
                DocRow doc;
                long revision;
                await using (var q = new NpgsqlCommand($"SELECT {CD.Id}, {CD.DocType}, {CD.Status}, {CD.PartyId}, {CD.Currency}, {CD.BranchId}, {CD.Revision} FROM {CD.Table} WHERE {CD.Id} = @id FOR SHARE", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("id", documentId);
                    await using var rd = await q.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) throw new Abort(ConfigStatus.NotFound, "commercial_document_not_found");
                    doc = new DocRow(rd.GetGuid(0), rd.GetString(1), (CommercialStatus)rd.GetInt16(2), rd.GetGuid(3), rd.GetString(4).Trim(), rd.GetGuid(5));
                    revision = rd.GetInt64(6);
                }
                var lines = new List<(Guid Id, int LineNo, decimal Quantity)>();
                await using (var lk = new NpgsqlCommand($"SELECT {CL.Id}, {CL.LineNo}, {CL.Quantity} FROM {CL.Table} WHERE {CL.DocumentId} = @d ORDER BY {CL.Id} FOR NO KEY UPDATE", work.Connection, work.Transaction))
                {
                    lk.Parameters.AddWithValue("d", documentId);
                    await using var rd = await lk.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) lines.Add((rd.GetGuid(0), rd.GetInt32(1), rd.GetDecimal(2)));
                }
                // the key is checked AGAIN with the locks held (a concurrent same-key request may have committed while this one waited)
                if (await FindShortCloseByKeyAsync(work.Connection, work.Transaction, actor, key, ct) is { } again) throw again.Hash == hash ? new Replay(again.Operation) : new Abort(ConfigStatus.Conflict, "idempotency_key_reused");
                if (revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict");
                if (doc.Status != CommercialStatus.Approved) throw new Abort(ConfigStatus.Conflict, "document_not_approved");
                if (!types.IsShortCloseAllowed(doc.DocType)) throw new Abort(ConfigStatus.Conflict, "short_close_not_allowed_for_type");
                var state = new Dictionary<Guid, decimal>();   // open quantity per line
                foreach (var l in lines)
                {
                    var fulfilled = await Scalar<decimal>(work, $"SELECT COALESCE(sum({DL.Quantity}), 0) FROM {DL.Table} WHERE {DL.FromLineId} = @a AND {DL.Kind} = 1", ("a", l.Id));
                    var invoiced = await Scalar<decimal>(work, $"SELECT COALESCE(sum({DL.Quantity}), 0) FROM {DL.Table} WHERE {DL.FromLineId} = @a AND {DL.Kind} = 2", ("a", l.Id));
                    var closed = await Scalar<decimal>(work, $"SELECT COALESCE(sum({SC.Quantity}), 0) FROM {SC.Table} WHERE {SC.LineId} = @a", ("a", l.Id));
                    state[l.Id] = Math.Max(0, l.Quantity - Math.Max(fulfilled, invoiced) - closed);
                }
                var plan = new List<(Guid LineId, int LineNo, decimal Quantity)>();
                if (named is null)
                {
                    foreach (var l in lines.Where(l => state[l.Id] > 0)) plan.Add((l.Id, l.LineNo, state[l.Id]));
                    if (plan.Count == 0) throw new Abort(ConfigStatus.Conflict, "nothing_to_close");
                }
                else
                {
                    foreach (var (lineNo, quantity) in named)
                    {
                        var line = lines.FirstOrDefault(l => l.LineNo == lineNo);
                        if (line.Id == Guid.Empty) throw Invalid("lines", "line_not_found");
                        if (quantity > state[line.Id]) throw new Abort(ConfigStatus.Conflict, "exceeds_open_quantity");
                        plan.Add((line.Id, lineNo, quantity));
                    }
                }
                foreach (var p in plan.OrderBy(p => p.LineId))   // id order, as every other writer
                {
                    await using var ins = new NpgsqlCommand($"INSERT INTO {SC.Table} ({SC.Id}, {SC.OperationId}, {SC.DocumentId}, {SC.LineId}, {SC.Quantity}, {SC.Reason}, {SC.IdempotencyKey}, {SC.PayloadHash}, {SC.CreatedBy}, {SC.CreatedAtUtc}) " +
                        "VALUES (@id, @op, @d, @l, @q, @reason, @key, @h, @by, @now)", work.Connection, work.Transaction);
                    ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("op", operation); ins.Parameters.AddWithValue("d", documentId); ins.Parameters.AddWithValue("l", p.LineId);
                    ins.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Numeric) { Value = p.Quantity }); ins.Parameters.AddWithValue("reason", reason); ins.Parameters.AddWithValue("key", key);
                    ins.Parameters.AddWithValue("h", hash); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", Now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                // the reason text is in the record, not in the audit detail (the detail names counts and quantities only)
                await work.AppendAuditAsync(new AuditEventData(CommercialAuditEvents.ShortClosed, ActorUserId: actor, SubjectId: documentId,
                    Detail: $"commercial document closed short operation={operation} lines={plan.Count} quantity={plan.Sum(p => p.Quantity).ToString("0.##########", CultureInfo.InvariantCulture)}"));
                return operation;
            }, ct);
        }
        catch (Replay replay) { return ConfigResult<ShortCloseView>.Ok((await GetShortCloseAsync(companyId, replay.Id, ct))! with { Replayed = true }); }
        if (r.IsOk) return ConfigResult<ShortCloseView>.Ok((await GetShortCloseAsync(companyId, r.Value, ct))!);
        if (r.Code == "idempotency_race")
        {
            await using var conn = await resolver.OpenAsync(companyId, ct);
            if (await FindShortCloseByKeyAsync(conn, null, actor, key, ct) is { } e)
                return e.Hash == hash ? ConfigResult<ShortCloseView>.Ok((await GetShortCloseAsync(companyId, e.Operation, ct))! with { Replayed = true }) : ConfigResult<ShortCloseView>.Fail(ConfigStatus.Conflict, "idempotency_key_reused");
        }
        return ConfigResult<ShortCloseView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    private static async Task<(Guid Operation, string Hash)?> FindShortCloseByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid actor, string key, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {SC.OperationId}, {SC.PayloadHash} FROM {SC.Table} WHERE {SC.CreatedBy} = @u AND {SC.IdempotencyKey} = @k LIMIT 1", conn, tx);
        q.Parameters.AddWithValue("u", actor); q.Parameters.AddWithValue("k", key);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1).Trim()) : null;
    }

    private const string ShortCloseSelect = $"SELECT s.{SC.OperationId}, s.{SC.DocumentId}, s.{SC.Reason}, s.{SC.CreatedBy}, s.{SC.CreatedAtUtc}, l.{CL.LineNo}, s.{SC.Quantity} FROM {SC.Table} s JOIN {CL.Table} l ON l.{CL.Id} = s.{SC.LineId}";

    private static IReadOnlyList<ShortCloseView> Group(NpgsqlDataReader r)
    {
        var map = new Dictionary<Guid, (ShortCloseView Head, List<ShortCloseLineView> Lines)>();
        var order = new List<Guid>();
        while (r.Read())
        {
            var op = r.GetGuid(0);
            if (!map.TryGetValue(op, out var e)) { e = (new ShortCloseView(op, r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4), []), []); map[op] = e; order.Add(op); }
            e.Lines.Add(new ShortCloseLineView(r.GetInt32(5), r.GetDecimal(6)));
        }
        return [.. order.Select(op => map[op].Head with { Lines = [.. map[op].Lines.OrderBy(l => l.LineNo)] })];
    }

    private async Task<ShortCloseView?> GetShortCloseAsync(Guid companyId, Guid operation, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"{ShortCloseSelect} WHERE s.{SC.OperationId} = @op", conn);
        q.Parameters.AddWithValue("op", operation);
        await using var r = await q.ExecuteReaderAsync(ct);
        return Group(r).FirstOrDefault();
    }

    /// <summary>Every short-close operation of the document, oldest first (the history of the document shows them; append-only).</summary>
    public async Task<IReadOnlyList<ShortCloseView>> ListShortClosesAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"{ShortCloseSelect} WHERE s.{SC.DocumentId} = @d ORDER BY s.{SC.CreatedAtUtc}, s.{SC.OperationId}, l.{CL.LineNo}", conn);
        q.Parameters.AddWithValue("d", documentId);
        await using var r = await q.ExecuteReaderAsync(ct);
        return Group(r);
    }

    /// <summary>Whether the document was closed short at all (used by the document service: such a document stays approved, like a linked one, D-201).</summary>
    public static async Task<bool> HasShortCloseAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid documentId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {SC.Table} WHERE {SC.DocumentId} = @d)", conn, tx);
        q.Parameters.AddWithValue("d", documentId);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }
}
