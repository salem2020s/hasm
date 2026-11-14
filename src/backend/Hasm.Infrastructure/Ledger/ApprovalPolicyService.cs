using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using AP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicy;
using AD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Approval-policy configuration (Phase 3.2B, slice S4). <b>No authorization here by design:</b> <see cref="ApprovalPolicyManagementService"/> wraps it.
/// Policies are audited, revisioned and never deleted; PostgreSQL enforces their shape and the evaluation function used by the posting engine.
/// </summary>
public sealed class ApprovalPolicyService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48434150;   // "HCAP": advisory-lock namespace of approval-policy changes
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    private static readonly string Columns =
        $"p.{AP.Id}, p.{AP.DocType}, p.{AP.Source}, p.{AP.BranchId}, p.{AP.MinAmount}, p.{AP.RequiredApprovals}, p.{AP.NameAr}, p.{AP.NameEn}, p.{AP.IsActive}, p.{AP.Revision}, p.{AP.CreatedAtUtc}, p.{AP.UpdatedAtUtc}";

    public async Task<IReadOnlyList<ApprovalPolicyView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, null, false, ct);
    }

    public async Task<ApprovalPolicyView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadAsync(conn, null, id, false, ct)).SingleOrDefault();
    }

    /// <summary>Approvals a document needs under the CURRENT active policies (0 = none): the same SQL function the posting trigger uses.</summary>
    public async Task<int> RequiredApprovalsAsync(Guid companyId, string docType, DocumentSource source, Guid? branchId, decimal baseAmount, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await DocumentApprovals.RequiredAsync(conn, null, docType, source, branchId, baseAmount, ct);
    }

    internal static async Task<List<ApprovalPolicyView>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {AP.Table} p {(id is null ? "" : $"WHERE p.{AP.Id} = @id")} ORDER BY p.{AP.DocType}, p.{AP.MinAmount}, p.{AP.CreatedAtUtc}, p.{AP.Id}{(forUpdate ? " FOR UPDATE OF p" : "")}", conn, tx);
        if (id is not null) cmd.Parameters.AddWithValue("id", id.Value);
        var list = new List<ApprovalPolicyView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ApprovalPolicyView(r.GetGuid(0), r.GetString(1), (PolicySource)r.GetInt16(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetDecimal(4), r.GetInt16(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetInt64(9), new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero), new DateTimeOffset(r.GetDateTime(11), TimeSpan.Zero)));
        return list;
    }

    public async Task<ConfigResult<ApprovalPolicyView>> CreateAsync(Guid actor, Guid companyId, ApprovalPolicyInput? input, CancellationToken ct = default)
    {
        if (!ApprovalValidation.TryNormalize(input, out var docType, out var source, out var min, out var required, out var nameAr, out var nameEn, out var errors)) return ConfigResult<ApprovalPolicyView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            if (input!.BranchId is { } bid) await RequireActiveBranchAsync(work, bid, ct);
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            await ExecAsync(work, $"INSERT INTO {AP.Table} ({AP.Id}, {AP.DocType}, {AP.Source}, {AP.BranchId}, {AP.MinAmount}, {AP.RequiredApprovals}, {AP.NameAr}, {AP.NameEn}, {AP.IsActive}, {AP.Revision}, {AP.CreatedAtUtc}, {AP.UpdatedAtUtc}) VALUES (@id, @doc, @src, @branch, @min, @req, @ar, @en, true, 1, @now, @now)",
                p => Bind(p, id, docType, source, input.BranchId, min, required, nameAr, nameEn, now));
            var changes = new JsonObject { ["docType"] = docType, ["source"] = source.ToString(), ["branchId"] = input.BranchId?.ToString(), ["minAmount"] = min.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), ["requiredApprovals"] = required, ["nameAr"] = nameAr, ["nameEn"] = nameEn };
            await RecordAsync(work, LedgerAuditEvents.ApprovalPolicyCreated, actor, id, 1, changes, "approval policy created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct);
    }

    public async Task<ConfigResult<ApprovalPolicyView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, ApprovalPolicyDetailsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ApprovalPolicyView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ApprovalValidation.TryNormalizeDetails(input, out var source, out var min, out var required, out var nameAr, out var nameEn, out var errors)) return ConfigResult<ApprovalPolicyView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Source == source && cur.BranchId == input!.BranchId && cur.MinAmount == min && cur.RequiredApprovals == required && cur.NameAr == nameAr && cur.NameEn == nameEn) throw new Abort(ConfigStatus.Ok, "unchanged");
            if (input!.BranchId is { } bid && bid != cur.BranchId) await RequireActiveBranchAsync(work, bid, ct);
            await ExecAsync(work, $"UPDATE {AP.Table} SET {AP.Source} = @src, {AP.BranchId} = @branch, {AP.MinAmount} = @min, {AP.RequiredApprovals} = @req, {AP.NameAr} = @ar, {AP.NameEn} = @en, {AP.Revision} = {AP.Revision} + 1, {AP.UpdatedAtUtc} = @now WHERE {AP.Id} = @id",
                p => Bind(p, id, cur.DocType, source, input.BranchId, min, required, nameAr, nameEn, clock.GetUtcNow().UtcDateTime));
            var changes = new JsonObject();
            Diff(changes, "source", cur.Source.ToString(), source.ToString()); Diff(changes, "branchId", cur.BranchId?.ToString(), input.BranchId?.ToString());
            Diff(changes, "minAmount", cur.MinAmount.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), min.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            Diff(changes, "requiredApprovals", cur.RequiredApprovals.ToString(), required.ToString()); Diff(changes, "nameAr", cur.NameAr, nameAr); Diff(changes, "nameEn", cur.NameEn, nameEn);
            await RecordAsync(work, LedgerAuditEvents.ApprovalPolicyUpdated, actor, id, cur.Revision + 1, changes, "approval policy updated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    public Task<ConfigResult<ApprovalPolicyView>> DeactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) => ToggleAsync(actor, companyId, id, expectedRevision, false, ct);

    public Task<ConfigResult<ApprovalPolicyView>> ReactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) => ToggleAsync(actor, companyId, id, expectedRevision, true, ct);

    private async Task<ConfigResult<ApprovalPolicyView>> ToggleAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool active, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<ApprovalPolicyView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.IsActive == active) throw new Abort(ConfigStatus.Ok, "unchanged");
            await ExecAsync(work, $"UPDATE {AP.Table} SET {AP.IsActive} = @a, {AP.Revision} = {AP.Revision} + 1, {AP.UpdatedAtUtc} = @now WHERE {AP.Id} = @id",
                p => { p.AddWithValue("id", id); p.AddWithValue("a", active); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            var changes = new JsonObject(); Diff(changes, "isActive", cur.IsActive.ToString(), active.ToString());
            await RecordAsync(work, active ? LedgerAuditEvents.ApprovalPolicyReactivated : LedgerAuditEvents.ApprovalPolicyDeactivated, actor, id, cur.Revision + 1, changes, active ? "approval policy reactivated" : "approval policy deactivated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private static void Bind(NpgsqlParameterCollection p, Guid id, string docType, PolicySource source, Guid? branch, decimal min, int required, string? ar, string? en, DateTime now)
    {
        p.AddWithValue("id", id); p.AddWithValue("doc", docType); p.AddWithValue("src", (short)source);
        p.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = (object?)branch ?? DBNull.Value });
        p.AddWithValue("min", min); p.AddWithValue("req", (short)required);
        p.Add(new NpgsqlParameter("ar", NpgsqlDbType.Varchar) { Value = (object?)ar ?? DBNull.Value });
        p.Add(new NpgsqlParameter("en", NpgsqlDbType.Varchar) { Value = (object?)en ?? DBNull.Value });
        p.AddWithValue("now", now);
    }

    private static void Diff(JsonObject changes, string name, string? old, string? @new)
    {
        if (!string.Equals(old, @new, StringComparison.Ordinal)) changes[name] = new JsonObject { ["old"] = old, ["new"] = @new };
    }

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct, Func<Task<T?>>? onUnchanged = null) where T : class
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) when (abort.Message == "unchanged" && onUnchanged is not null) { return ConfigResult<T>.Ok((await onUnchanged())!); }
        catch (Abort abort) { return abort.To<T>(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(CompanyAuditedWork work, string sql, Action<NpgsqlParameterCollection> bind)
    {
        await using var cmd = new NpgsqlCommand(sql, work.Connection, work.Transaction);
        bind(cmd.Parameters);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("approval policy row not written");
    }

    private static async Task<ApprovalPolicyView> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var cur = (await LoadAsync(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "approval_policy_not_found");
        if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
        return cur;
    }

    private static async Task RequireActiveBranchAsync(CompanyAuditedWork work, Guid branchId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = @id", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("id", branchId);
        var v = await q.ExecuteScalarAsync(ct);
        if (v is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_not_found")]);
        if (!(bool)v) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_inactive")]);
    }

    private async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, Guid id, long revision, JsonObject changes, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', changes.Select(c => c.Key))} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {LH.Table} ({LH.Entity}, {LH.EntityKey}, {LH.Revision}, {LH.ActorUserId}, {LH.ChangedAtUtc}, {LH.Changes}, {LH.AuditStream}, {LH.AuditSeq}) VALUES (4, @k, @r, @a, @t, @c, @s, @q)",
            work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("k", id.ToString()); cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey); cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Evaluation and recording of document approvals, used INSIDE the transaction of the document module (the journal, slice S5). Authorization (the approve
/// permission) is the caller's job; what this class guarantees is the separation of duties and the one-decision-per-approver-per-revision rule, and that the
/// requirement is always computed by the same SQL function the posting trigger uses, so automatic documents cannot bypass it.
/// </summary>
public static class DocumentApprovals
{
    public static async Task<int> RequiredAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string docType, DocumentSource source, Guid? branchId, decimal baseAmount, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {AP.RequiredFunction}(@d, @s, @b, @a)", conn, tx);
        cmd.Parameters.AddWithValue("d", docType);
        cmd.Parameters.AddWithValue("s", (short)source);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)branchId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("a", baseAmount);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<bool> SatisfiedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string docType, Guid documentId, long documentRevision, int required, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {AP.SatisfiedFunction}(@d, @i, @r, @n)", conn, tx);
        cmd.Parameters.AddWithValue("d", docType); cmd.Parameters.AddWithValue("i", documentId); cmd.Parameters.AddWithValue("r", documentRevision); cmd.Parameters.AddWithValue("n", required);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<IReadOnlyList<ApprovalDecisionView>> ListAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string docType, Guid documentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {AD.Id}, {AD.DocType}, {AD.DocumentId}, {AD.DocumentRevision}, {AD.Approver}, {AD.Decision}, {AD.DecidedAtUtc}, {AD.Comment} FROM {AD.Table} WHERE {AD.DocType} = @d AND {AD.DocumentId} = @i ORDER BY {AD.Id}", conn, tx);
        cmd.Parameters.AddWithValue("d", docType); cmd.Parameters.AddWithValue("i", documentId);
        var list = new List<ApprovalDecisionView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ApprovalDecisionView(r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetInt64(3), r.GetGuid(4), r.GetInt16(5) == 1, new DateTimeOffset(r.GetDateTime(6), TimeSpan.Zero), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    /// <summary>
    /// Records one decision inside the caller's audited transaction. Refuses (without writing) an approver who created or submitted the document, a second decision by
    /// the same approver on the same revision, and a rejection without a comment. The audit event commits or rolls back with the document's own change.
    /// </summary>
    public static async Task<ApprovalRecordResult> RecordAsync(CompanyAuditedWork work, ApprovalDecisionRequest request, DateTime nowUtc, CancellationToken ct)
    {
        if (request.ApproverId == request.CreatorId || (request.SubmitterId is { } s && request.ApproverId == s)) return new(ApprovalRecordStatus.SeparationOfDuties, null);
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > 500 }) throw new ArgumentException("comment too long", nameof(request));
        if (!request.Approve && comment is null) return new(ApprovalRecordStatus.CommentRequired, null);
        await using (var dup = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {AD.Table} WHERE {AD.DocType} = @d AND {AD.DocumentId} = @i AND {AD.DocumentRevision} = @r AND {AD.Approver} = @a)", work.Connection, work.Transaction))
        {
            dup.Parameters.AddWithValue("d", request.DocType); dup.Parameters.AddWithValue("i", request.DocumentId); dup.Parameters.AddWithValue("r", request.DocumentRevision); dup.Parameters.AddWithValue("a", request.ApproverId);
            if ((bool)(await dup.ExecuteScalarAsync(ct))!) return new(ApprovalRecordStatus.AlreadyDecided, null);
        }
        var receipt = await work.AppendAuditAsync(new AuditEventData(LedgerAuditEvents.ApprovalDecisionRecorded, ActorUserId: request.ApproverId,
            Detail: $"approval {(request.Approve ? "approved" : "rejected")} {request.DocType} rev {request.DocumentRevision}"), ct);
        long id;
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {AD.Table} ({AD.DocType}, {AD.DocumentId}, {AD.DocumentRevision}, {AD.Approver}, {AD.Decision}, {AD.DecidedAtUtc}, {AD.Comment}, {AD.AuditStream}, {AD.AuditSeq}) VALUES (@d, @i, @r, @a, @x, @t, @c, @s, @q) RETURNING {AD.Id}",
            work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("d", request.DocType); ins.Parameters.AddWithValue("i", request.DocumentId); ins.Parameters.AddWithValue("r", request.DocumentRevision);
            ins.Parameters.AddWithValue("a", request.ApproverId); ins.Parameters.AddWithValue("x", (short)(request.Approve ? 1 : 2)); ins.Parameters.AddWithValue("t", nowUtc);
            ins.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Varchar) { Value = (object?)comment ?? DBNull.Value });
            ins.Parameters.AddWithValue("s", receipt.StreamKey); ins.Parameters.AddWithValue("q", receipt.Seq);
            id = (long)(await ins.ExecuteScalarAsync(ct))!;
        }
        return new(ApprovalRecordStatus.Recorded, new ApprovalDecisionView(id, request.DocType, request.DocumentId, request.DocumentRevision, request.ApproverId, request.Approve, new DateTimeOffset(nowUtc, TimeSpan.Zero), comment));
    }
}
