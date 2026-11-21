using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LegalHold;
using RP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.RetentionPolicy;
using RR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.RetentionRule;

namespace Hasm.Infrastructure.Attachments;

/// <summary>What the caller may do with retention (each is a server-checked permission; the screen only reflects them).</summary>
public sealed record RetentionCapabilities(bool View, bool Configure, bool Hold, bool Approve, bool Execute);
/// <summary>One removed attachment in the retention preview: ids and kinds only (never a file name or a digest) and the eligibility answer.</summary>
public sealed record RetentionPreviewItem(Guid AttachmentId, string SubjectKind, Guid SubjectId, bool IsSensitive, DateTimeOffset RemovedAtUtc, string Reason, DateTimeOffset? EligibleAtUtc, Guid? HoldId, int? PolicyVersion);
/// <summary>A page of the preview: the removed attachments in removal order, the total of removed attachments and the count per reason code of THIS page.</summary>
public sealed record RetentionPreviewPage(IReadOnlyList<RetentionPreviewItem> Items, long Total, IReadOnlyDictionary<string, int> ReasonCounts, bool BasisConfirmed);

/// <summary>
/// The audited company-side store of the retention POLICY and LEGAL HOLDS (Phase R slice R1, owner decision D-082, docs/PHASE-R-attachment-retention.md). <b>No authorization here by design:</b>
/// <see cref="AttachmentRetentionManagementService"/> wraps it. R1 deletes nothing: it records policy versions (a draft, approved by a DIFFERENT user, then superseded or revoked) and holds, and previews
/// eligibility. While the retention basis (D-109) is owner-pending every item answers <c>retention_basis_pending</c>.
/// </summary>
public sealed class AttachmentRetentionService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }
    private DateTimeOffset Now => clock.GetUtcNow();

    // ================================================================================================================ reads

    private const string PolicyCols = $"{RP.Id}, {RP.Version}, {RP.Status}, {RP.EffectiveFrom}, {RP.ShortensAnyPeriod}, {RP.ShorteningAck}, {RP.Note}, {RP.DraftedBy}, {RP.DraftedAtUtc}, {RP.ApprovedBy}, {RP.ApprovedAtUtc}, {RP.RevokedBy}, {RP.RevokedAtUtc}, {RP.RevokeReason}, {RP.EndedAtUtc}, {RP.Revision}";
    private const string HoldCols = $"{LH.Id}, {LH.Scope}, {LH.SubjectKind}, {LH.SubjectId}, {LH.AttachmentId}, {LH.Reason}, {LH.Reference}, {LH.PlacedBy}, {LH.PlacedAtUtc}, {LH.ReleasedBy}, {LH.ReleasedAtUtc}, {LH.ReleaseReason}, {LH.Revision}";

    private static async Task<List<RetentionPolicyVersionView>> LoadPolicies(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, Action<NpgsqlCommand> bind, bool forUpdate, CancellationToken ct)
    {
        var heads = new List<(Guid Id, int Version, short Status, DateOnly Eff, bool Shortens, string? Ack, string? Note, Guid Drafted, DateTimeOffset DraftedAt, Guid? Approved, DateTimeOffset? ApprovedAt, Guid? Revoked, DateTimeOffset? RevokedAt, string? Why, DateTimeOffset? Ended, long Rev)>();
        await using (var cmd = new NpgsqlCommand($"SELECT {PolicyCols} FROM {RP.Table}{(where == "" ? "" : " WHERE " + where)} ORDER BY {RP.Version}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                heads.Add((r.GetGuid(0), r.GetInt32(1), r.GetInt16(2), r.GetFieldValue<DateOnly>(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8),
                    r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
                    r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14), r.GetInt64(15)));
        }
        var rules = new Dictionary<Guid, List<RetentionRule>>();
        if (heads.Count > 0)
        {
            await using var cmd = new NpgsqlCommand($"SELECT {RR.PolicyId}, {RR.SubjectKind}, {RR.Sensitivity}, {RR.Months}, {RR.Indefinite} FROM {RR.Table} WHERE {RR.PolicyId} = ANY(@ids) ORDER BY {RR.SubjectKind}, {RR.Sensitivity}", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = heads.Select(h => h.Id).ToArray() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!rules.TryGetValue(r.GetGuid(0), out var list)) rules[r.GetGuid(0)] = list = [];
                list.Add(new RetentionRule(r.GetString(1), r.GetInt16(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetBoolean(4)));
            }
        }
        return [.. heads.Select(h => new RetentionPolicyVersionView(h.Id, h.Version, (RetentionPolicyStatus)h.Status, h.Eff, h.Shortens, h.Ack, h.Note, h.Drafted, h.DraftedAt, h.Approved, h.ApprovedAt, h.Revoked, h.RevokedAt, h.Why, h.Ended, h.Rev,
            rules.TryGetValue(h.Id, out var l) ? l : []))];
    }

    private static async Task<List<LegalHoldView>> LoadHolds(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, Action<NpgsqlCommand> bind, bool forUpdate, CancellationToken ct)
    {
        var list = new List<LegalHoldView>();
        await using var cmd = new NpgsqlCommand($"SELECT {HoldCols} FROM {LH.Table}{(where == "" ? "" : " WHERE " + where)} ORDER BY {LH.PlacedAtUtc}, {LH.Id}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LegalHoldView(r.GetGuid(0), (LegalHoldScope)r.GetInt16(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt64(12)));
        return list;
    }

    public async Task<IReadOnlyList<RetentionPolicyVersionView>> ListPoliciesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadPolicies(conn, null, "", _ => { }, false, ct);
    }

    public async Task<IReadOnlyList<LegalHoldView>> ListHoldsAsync(Guid companyId, bool includeReleased, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadHolds(conn, null, includeReleased ? "" : $"{LH.ReleasedAtUtc} IS NULL", _ => { }, false, ct);
    }

    /// <summary>
    /// Read-only preview (nothing is changed): the REMOVED attachments in removal order with their eligibility answer. Ids and kinds only. The environment says "basis not confirmed" until the owner answers D-109, so
    /// every attachment that would otherwise be eligible answers <c>retention_basis_pending</c>.
    /// </summary>
    public async Task<RetentionPreviewPage> PreviewAsync(Guid companyId, int? offset, int? limit, string? subjectKind = null, Guid? subjectId = null, CancellationToken ct = default)
    {
        // Optional ids-only filter: the removed attachments of one record kind, or of one record. The caller validates the kind (a registered attachment subject kind) and passes a parsed id; both are bound as parameters.
        var filter = (subjectKind is null ? "" : $" AND {AT.SubjectKind} = @k") + (subjectId is null ? "" : $" AND {AT.SubjectId} = @s");
        void Bind(NpgsqlCommand c) { if (subjectKind is not null) c.Parameters.AddWithValue("k", subjectKind); if (subjectId is not null) c.Parameters.AddWithValue("s", subjectId.Value); }
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var policies = await LoadPolicies(conn, null, "", _ => { }, false, ct);
        var holds = await LoadHolds(conn, null, $"{LH.ReleasedAtUtc} IS NULL", _ => { }, false, ct);
        var off = ListPaging.ClampOffset(offset);
        var lim = ListPaging.ClampLimit(limit);
        long total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {AT.Table} WHERE {AT.State} = 2{filter}", conn)) { Bind(count); total = Convert.ToInt64(await count.ExecuteScalarAsync(ct)); }
        var facts = new List<AttachmentRetentionFacts>();
        await using (var cmd = new NpgsqlCommand($"SELECT {AT.Id}, {AT.SubjectKind}, {AT.SubjectId}, {AT.Sensitivity}, {AT.RemovedAtUtc}, {AT.KeyId} FROM {AT.Table} WHERE {AT.State} = 2{filter} ORDER BY {AT.RemovedAtUtc}, {AT.Id} LIMIT @lim OFFSET @off", conn))
        {
            Bind(cmd); cmd.Parameters.AddWithValue("lim", lim); cmd.Parameters.AddWithValue("off", off);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                facts.Add(new AttachmentRetentionFacts(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetInt16(3) == 2, true, r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetString(5), false));   // R1 has no purge: nothing is purged
        }
        var env = new RetentionEnvironment(CompanyBusy: false, BasisConfirmed: RetentionBasis.OwnerConfirmed);
        var now = Now;
        var items = new List<RetentionPreviewItem>();
        foreach (var f in facts)
        {
            var e = AttachmentRetentionEligibility.Evaluate(f, policies, holds, env, now);
            items.Add(new RetentionPreviewItem(f.Id, f.SubjectKind, f.SubjectId, f.IsSensitive, f.RemovedAtUtc!.Value, e.Code, e.EligibleAtUtc, e.HoldId, e.PolicyVersion));
        }
        return new RetentionPreviewPage(items, total, items.GroupBy(i => i.Reason).ToDictionary(g => g.Key, g => g.Count()), RetentionBasis.OwnerConfirmed);
    }

    // ================================================================================================================ policy writes

    /// <summary>Creates the next policy version as a DRAFT with its rules (validated). Audit 2000.</summary>
    public async Task<ConfigResult<RetentionPolicyVersionView>> DraftPolicyAsync(Guid actor, Guid companyId, RetentionPolicyInput? input, CancellationToken ct = default)
    {
        var errors = RetentionRules.Validate(input, DateOnly.FromDateTime(Now.UtcDateTime));
        if (errors.Count > 0) return ConfigResult<RetentionPolicyVersionView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate<bool>(companyId, async work =>
        {
            await using (var lockCmd = new NpgsqlCommand($"LOCK TABLE {RP.Table} IN SHARE ROW EXCLUSIVE MODE", work.Connection, work.Transaction)) await lockCmd.ExecuteNonQueryAsync(ct);   // versions are numbered one after the other
            int next;
            await using (var max = new NpgsqlCommand($"SELECT COALESCE(max({RP.Version}), 0) + 1 FROM {RP.Table}", work.Connection, work.Transaction)) next = Convert.ToInt32(await max.ExecuteScalarAsync(ct));
            await using (var ins = new NpgsqlCommand($"INSERT INTO {RP.Table} ({RP.Id}, {RP.Version}, {RP.Status}, {RP.EffectiveFrom}, {RP.ShortensAnyPeriod}, {RP.Note}, {RP.DraftedBy}, {RP.DraftedAtUtc}, {RP.Revision}, {RP.UpdatedAtUtc}) " +
                $"VALUES (@id, @v, 1, @eff, false, @note, @by, @now, 1, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("v", next); ins.Parameters.AddWithValue("eff", input!.EffectiveFrom!.Value);
                ins.Parameters.AddWithValue("note", (object?)input.Note?.Trim() ?? DBNull.Value); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            foreach (var rule in input.Rules!)
            {
                await using var ri = new NpgsqlCommand($"INSERT INTO {RR.Table} ({RR.Id}, {RR.PolicyId}, {RR.SubjectKind}, {RR.Sensitivity}, {RR.Months}, {RR.Indefinite}) VALUES (@id, @p, @k, @s, @m, @i)", work.Connection, work.Transaction);
                ri.Parameters.AddWithValue("id", Guid.NewGuid()); ri.Parameters.AddWithValue("p", id); ri.Parameters.AddWithValue("k", rule.SubjectKind); ri.Parameters.AddWithValue("s", rule.Sensitivity);
                ri.Parameters.AddWithValue("m", (object?)rule.Months ?? DBNull.Value); ri.Parameters.AddWithValue("i", rule.Indefinite);
                await ri.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(AttachmentRetentionAuditEvents.PolicyDrafted, ActorUserId: actor, SubjectId: id, Detail: $"retention policy drafted version={next} rules={input.Rules!.Count}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<RetentionPolicyVersionView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<RetentionPolicyVersionView>.Ok((await ListPoliciesAsync(companyId, ct)).Single(p => p.Id == id));
    }

    /// <summary>
    /// Approves a draft by a user OTHER than its author (also a database CHECK). The version that was approved before is superseded (it ends when the new one takes effect, or now when that is today); a version that
    /// SHORTENS any period needs a written acknowledgement. Audit 2001.
    /// </summary>
    public async Task<ConfigResult<RetentionPolicyVersionView>> ApprovePolicyAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? shorteningAcknowledgement, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<RetentionPolicyVersionView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var ack = shorteningAcknowledgement?.Trim();
        if (ack is { Length: > RetentionRules.MaxAcknowledgementLength }) return ConfigResult<RetentionPolicyVersionView>.Invalid([new FieldError("shorteningAcknowledgement", "too_long")]);
        var r = await Mutate<bool>(companyId, async work =>
        {
            var all = await LoadPolicies(work.Connection, work.Transaction, "", _ => { }, true, ct);
            var cur = all.SingleOrDefault(p => p.Id == id) ?? throw new Abort(ConfigStatus.NotFound, "policy_not_found");
            if (cur.Status != RetentionPolicyStatus.Draft) throw new Abort(ConfigStatus.Conflict, "not_a_draft");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.DraftedBy == actor) throw new Abort(ConfigStatus.Forbidden, "approver_must_differ");
            var now = Now; var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (cur.EffectiveFrom < today) throw new Abort(ConfigStatus.Invalid, "effective_date_passed", [new FieldError("effectiveFrom", "in_the_past")]);
            var approved = all.SingleOrDefault(p => p.Status == RetentionPolicyStatus.Approved);
            var shortens = approved is not null && RetentionRules.Shortens(approved.Rules, cur.Rules);
            if (shortens && string.IsNullOrEmpty(ack)) throw new Abort(ConfigStatus.Invalid, "shortening_acknowledgement_required", [new FieldError("shorteningAcknowledgement", "required")]);
            if (approved is not null)
            {
                var ends = new DateTimeOffset(cur.EffectiveFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                await using var sup = new NpgsqlCommand($"UPDATE {RP.Table} SET {RP.Status} = 4, {RP.EndedAtUtc} = @end, {RP.Revision} = {RP.Revision} + 1, {RP.UpdatedAtUtc} = @now WHERE {RP.Id} = @id", work.Connection, work.Transaction);
                sup.Parameters.AddWithValue("end", ends > now ? ends : now); sup.Parameters.AddWithValue("now", now); sup.Parameters.AddWithValue("id", approved.Id);
                await sup.ExecuteNonQueryAsync(ct);
            }
            await using (var upd = new NpgsqlCommand($"UPDATE {RP.Table} SET {RP.Status} = 2, {RP.ApprovedBy} = @by, {RP.ApprovedAtUtc} = @now, {RP.ShortensAnyPeriod} = @sh, {RP.ShorteningAck} = @ack, {RP.Revision} = {RP.Revision} + 1, {RP.UpdatedAtUtc} = @now WHERE {RP.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", now); upd.Parameters.AddWithValue("sh", shortens); upd.Parameters.AddWithValue("ack", shortens ? ack! : (object)DBNull.Value); upd.Parameters.AddWithValue("id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(AttachmentRetentionAuditEvents.PolicyApproved, ActorUserId: actor, SubjectId: id,
                Detail: $"retention policy approved version={cur.Version} effective={cur.EffectiveFrom:yyyy-MM-dd} superseded={(approved is null ? "none" : approved.Version.ToString())} shortens={shortens}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<RetentionPolicyVersionView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<RetentionPolicyVersionView>.Ok((await ListPoliciesAsync(companyId, ct)).Single(p => p.Id == id));
    }

    /// <summary>Revokes the APPROVED version (reason required): the company returns to preservation until a new version is approved. Audit 2002.</summary>
    public async Task<ConfigResult<RetentionPolicyVersionView>> RevokePolicyAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<RetentionPolicyVersionView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) return ConfigResult<RetentionPolicyVersionView>.Invalid([new FieldError("reason", "required")]);
        if (why.Length > RetentionRules.MaxReasonLength) return ConfigResult<RetentionPolicyVersionView>.Invalid([new FieldError("reason", "too_long")]);
        var r = await Mutate<bool>(companyId, async work =>
        {
            var cur = (await LoadPolicies(work.Connection, work.Transaction, $"{RP.Id} = @id", c => c.Parameters.AddWithValue("id", id), true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "policy_not_found");
            if (cur.Status != RetentionPolicyStatus.Approved) throw new Abort(ConfigStatus.Conflict, "not_approved");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            var now = Now;
            await using (var upd = new NpgsqlCommand($"UPDATE {RP.Table} SET {RP.Status} = 3, {RP.RevokedBy} = @by, {RP.RevokedAtUtc} = @now, {RP.RevokeReason} = @why, {RP.EndedAtUtc} = @now, {RP.Revision} = {RP.Revision} + 1, {RP.UpdatedAtUtc} = @now WHERE {RP.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", now); upd.Parameters.AddWithValue("why", why); upd.Parameters.AddWithValue("id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(AttachmentRetentionAuditEvents.PolicyRevoked, ActorUserId: actor, SubjectId: id, Detail: $"retention policy revoked version={cur.Version}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<RetentionPolicyVersionView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<RetentionPolicyVersionView>.Ok((await ListPoliciesAsync(companyId, ct)).Single(p => p.Id == id));
    }

    // ================================================================================================================ legal holds

    /// <summary>Places a legal hold (effective at once for every later eligibility check). Audit 2003.</summary>
    public async Task<ConfigResult<LegalHoldView>> PlaceHoldAsync(Guid actor, Guid companyId, LegalHoldInput? input, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        if (input is null) return ConfigResult<LegalHoldView>.Invalid([new FieldError("body", "required")]);
        var reason = input.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) errors.Add(new FieldError("reason", "required"));
        else if (reason.Length > RetentionRules.MaxReasonLength) errors.Add(new FieldError("reason", "too_long"));
        if (input.Reference is { } rf && rf.Trim().Length > RetentionRules.MaxReferenceLength) errors.Add(new FieldError("reference", "too_long"));
        switch (input.Scope)
        {
            case LegalHoldScope.Company:
                if (input.SubjectKind is not null || input.SubjectId is not null || input.AttachmentId is not null) errors.Add(new FieldError("scope", "scope_mismatch"));
                break;
            case LegalHoldScope.Record:
                if (input.SubjectKind is null || !AttachmentSubjects.Kinds.ContainsKey(input.SubjectKind)) errors.Add(new FieldError("subjectKind", "subject_kind_invalid"));
                if (input.SubjectId is null) errors.Add(new FieldError("subjectId", "required"));
                if (input.AttachmentId is not null) errors.Add(new FieldError("scope", "scope_mismatch"));
                break;
            case LegalHoldScope.Attachment:
                if (input.AttachmentId is null) errors.Add(new FieldError("attachmentId", "required"));
                if (input.SubjectKind is not null || input.SubjectId is not null) errors.Add(new FieldError("scope", "scope_mismatch"));
                break;
            default: errors.Add(new FieldError("scope", "invalid_value")); break;
        }
        if (errors.Count > 0) return ConfigResult<LegalHoldView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate<bool>(companyId, async work =>
        {
            if (input.Scope == LegalHoldScope.Attachment)
            {
                await using var exists = new NpgsqlCommand($"SELECT 1 FROM {AT.Table} WHERE {AT.Id} = @id", work.Connection, work.Transaction);
                exists.Parameters.AddWithValue("id", input.AttachmentId!.Value);
                if (await exists.ExecuteScalarAsync(ct) is null) throw new Abort(ConfigStatus.Invalid, "attachment_not_found", [new FieldError("attachmentId", "attachment_not_found")]);
            }
            await using (var ins = new NpgsqlCommand($"INSERT INTO {LH.Table} ({LH.Id}, {LH.Scope}, {LH.SubjectKind}, {LH.SubjectId}, {LH.AttachmentId}, {LH.Reason}, {LH.Reference}, {LH.PlacedBy}, {LH.PlacedAtUtc}, {LH.Revision}, {LH.UpdatedAtUtc}) " +
                $"VALUES (@id, @sc, @k, @s, @a, @why, @ref, @by, @now, 1, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("sc", (short)input.Scope!.Value);
                ins.Parameters.AddWithValue("k", (object?)input.SubjectKind ?? DBNull.Value); ins.Parameters.AddWithValue("s", (object?)input.SubjectId ?? DBNull.Value); ins.Parameters.AddWithValue("a", (object?)input.AttachmentId ?? DBNull.Value);
                ins.Parameters.AddWithValue("why", reason!); ins.Parameters.AddWithValue("ref", (object?)input.Reference?.Trim() ?? DBNull.Value); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", Now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(AttachmentRetentionAuditEvents.HoldPlaced, ActorUserId: actor, SubjectId: id, Detail: $"legal hold placed scope={input.Scope} kind={input.SubjectKind ?? "-"}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<LegalHoldView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<LegalHoldView>.Ok((await ListHoldsAsync(companyId, true, ct)).Single(h => h.Id == id));
    }

    /// <summary>Releases a hold: needs a reason and a user OTHER than the one who placed it (also a database CHECK); a released hold never changes again. Audit 2004.</summary>
    public async Task<ConfigResult<LegalHoldView>> ReleaseHoldAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, string? reason, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<LegalHoldView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) return ConfigResult<LegalHoldView>.Invalid([new FieldError("reason", "required")]);
        if (why.Length > RetentionRules.MaxReasonLength) return ConfigResult<LegalHoldView>.Invalid([new FieldError("reason", "too_long")]);
        var r = await Mutate<bool>(companyId, async work =>
        {
            var cur = (await LoadHolds(work.Connection, work.Transaction, $"{LH.Id} = @id", c => c.Parameters.AddWithValue("id", id), true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "hold_not_found");
            if (!cur.IsActive) throw new Abort(ConfigStatus.Conflict, "already_released");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (cur.PlacedBy == actor) throw new Abort(ConfigStatus.Forbidden, "releaser_must_differ");
            await using (var upd = new NpgsqlCommand($"UPDATE {LH.Table} SET {LH.ReleasedBy} = @by, {LH.ReleasedAtUtc} = @now, {LH.ReleaseReason} = @why, {LH.Revision} = {LH.Revision} + 1, {LH.UpdatedAtUtc} = @now WHERE {LH.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("by", actor); upd.Parameters.AddWithValue("now", Now); upd.Parameters.AddWithValue("why", why); upd.Parameters.AddWithValue("id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(AttachmentRetentionAuditEvents.HoldReleased, ActorUserId: actor, SubjectId: id, Detail: $"legal hold released scope={cur.Scope}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<LegalHoldView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        return ConfigResult<LegalHoldView>.Ok((await ListHoldsAsync(companyId, true, ct)).Single(h => h.Id == id));
    }

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }
    }
}

/// <summary>
/// Permission-checked entry (company-wide): <c>company.attachmentretention.view</c> for the policy, holds and the preview; <c>configure</c> to draft a policy; <c>hold</c> to place a legal hold; <c>approve</c> to approve
/// or revoke a policy version and to release a hold (never in the Company Administrator preset: the Retention Approver preset, a sensitive grant). Independence (author or placer never approves or releases) is enforced
/// by the store and by PostgreSQL. <c>execute</c> belongs to R2 (purge).
/// </summary>
public sealed class AttachmentRetentionManagementService(AccessService access, AttachmentRetentionService store)
{
    public async Task<RetentionCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default) =>
        new(await Can(user, company, PermissionCatalog.Codes.RetentionView, ct), await Can(user, company, PermissionCatalog.Codes.RetentionConfigure, ct), await Can(user, company, PermissionCatalog.Codes.RetentionHold, ct),
            await Can(user, company, PermissionCatalog.Codes.RetentionApprove, ct), await Can(user, company, PermissionCatalog.Codes.RetentionExecute, ct));

    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<IReadOnlyList<RetentionPolicyVersionView>>> ListPoliciesAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionView, ct) ? ConfigResult<IReadOnlyList<RetentionPolicyVersionView>>.Ok(await store.ListPoliciesAsync(company, ct)) : Denied<IReadOnlyList<RetentionPolicyVersionView>>();

    public async Task<ConfigResult<IReadOnlyList<LegalHoldView>>> ListHoldsAsync(Guid user, Guid company, bool includeReleased, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionView, ct) ? ConfigResult<IReadOnlyList<LegalHoldView>>.Ok(await store.ListHoldsAsync(company, includeReleased, ct)) : Denied<IReadOnlyList<LegalHoldView>>();

    public async Task<ConfigResult<RetentionPreviewPage>> PreviewAsync(Guid user, Guid company, int? offset, int? limit, string? subjectKind = null, Guid? subjectId = null, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionView, ct) ? ConfigResult<RetentionPreviewPage>.Ok(await store.PreviewAsync(company, offset, limit, subjectKind, subjectId, ct)) : Denied<RetentionPreviewPage>();

    public async Task<ConfigResult<RetentionPolicyVersionView>> DraftPolicyAsync(Guid user, Guid company, RetentionPolicyInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionConfigure, ct) ? await store.DraftPolicyAsync(user, company, input, ct) : Denied<RetentionPolicyVersionView>();

    public async Task<ConfigResult<RetentionPolicyVersionView>> ApprovePolicyAsync(Guid user, Guid company, Guid id, long? revision, string? shorteningAcknowledgement, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionApprove, ct) ? await store.ApprovePolicyAsync(user, company, id, revision, shorteningAcknowledgement, ct) : Denied<RetentionPolicyVersionView>();

    public async Task<ConfigResult<RetentionPolicyVersionView>> RevokePolicyAsync(Guid user, Guid company, Guid id, long? revision, string? reason, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionApprove, ct) ? await store.RevokePolicyAsync(user, company, id, revision, reason, ct) : Denied<RetentionPolicyVersionView>();

    public async Task<ConfigResult<LegalHoldView>> PlaceHoldAsync(Guid user, Guid company, LegalHoldInput? input, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionHold, ct) ? await store.PlaceHoldAsync(user, company, input, ct) : Denied<LegalHoldView>();

    public async Task<ConfigResult<LegalHoldView>> ReleaseHoldAsync(Guid user, Guid company, Guid id, long? revision, string? reason, CancellationToken ct = default) =>
        await Can(user, company, PermissionCatalog.Codes.RetentionApprove, ct) ? await store.ReleaseHoldAsync(user, company, id, revision, reason, ct) : Denied<LegalHoldView>();
}

/// <summary>Registration of the retention services.</summary>
public static class AttachmentRetentionServiceCollectionExtensions
{
    public static IServiceCollection AddAttachmentRetention(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AttachmentRetentionService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AttachmentRetentionManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<AttachmentRetentionService>()));
        return services;
    }
}
