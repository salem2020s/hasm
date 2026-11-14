using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;
using AD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using IB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using IR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportRow;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Submission, independent approval and controlled application (Phase 3.2D S3). The import approval is MANDATORY and independent: the creator and the submitter of a batch can never decide on it (service
/// and PostgreSQL), a decision is bound to the exact revision (and so to the exact plan hash) it was given for and every change of file, mapping, resolutions or plan voids it. Application is atomic: ONE
/// transaction re-locks the batch, takes the adapter's domain locks, re-validates the plan against the data as it is now, performs the approved operations through the adapter and records the result; any
/// failure rolls back everything the adapter and the framework did, the previous business state stays, and the failure is recorded safely in a second transaction so it can be corrected and retried.
/// </summary>
public sealed partial class ImportService
{
    public const string DocumentType = "import_batch";

    // ------------------------------------------------------------------------------------------------------------------------- submit / withdraw

    /// <summary>Submits a validated batch for approval. Needs a plan without blockers that still matches the data (a stale plan must be validated again) and something to apply.</summary>
    public async Task<ConfigResult<ImportBatchView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var batch = await GetAsync(companyId, id, ct);
        if (batch is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.NotFound, "import_not_found");
        if (batch.Revision != expectedRevision.Value) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict", batch.Revision);
        if (batch.Status != ImportStatus.Validated || batch.PlanHash is null || batch.Plan is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "batch_not_validated");
        var summary = batch.Plan["summary"]!;
        if (summary["blocking"]!.GetValue<int>() > 0) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "blocking_rows");
        if (!batch.Plan["canApply"]!.GetValue<bool>()) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "nothing_to_apply");
        var check = await CheckPlanAsync(companyId, id, ct);
        if (!check.IsOk) return ConfigResult<ImportBatchView>.Fail(check.Status, check.Code!);
        if (!check.Value!.Fresh) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "plan_stale");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status != ImportStatus.Validated || cur.PlanHash != batch.PlanHash) throw new Abort(ConfigStatus.Conflict, "batch_not_validated");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var up = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = 5, {IB.SubmittedBy} = @by, {IB.SubmittedAtUtc} = @t, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            { up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("by", actor); up.Parameters.AddWithValue("t", now); await up.ExecuteNonQueryAsync(ct); }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Submitted, view.Revision, actor, new JsonObject { ["planHash"] = view.PlanHash, ["operations"] = summary["operations"]!.DeepClone() }, ct);
            return view;
        }, ct);
    }

    /// <summary>
    /// Takes a submitted, approved or failed batch back to Validated. Every approval was bound to the revision it was given for, so all of them are void (a new submission and a new independent approval
    /// are needed); nothing business-side exists to undo.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status is not (ImportStatus.Submitted or ImportStatus.Approved or ImportStatus.Failed)) throw new Abort(ConfigStatus.Conflict, "batch_not_withdrawable");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var up = new NpgsqlCommand(
                $"UPDATE {IB.Table} SET {IB.Status} = 4, {IB.SubmittedBy} = NULL, {IB.SubmittedAtUtc} = NULL, {IB.ApprovalRevision} = NULL, {IB.Result} = NULL, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            { up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("t", now); await up.ExecuteNonQueryAsync(ct); }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.Withdrawn, view.Revision, actor, new JsonObject { ["from"] = ImportStatusRules.Name(cur.Status) }, ct);
            return view;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------- decisions

    public async Task<IReadOnlyList<ApprovalDecisionView>> DecisionsAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await DocumentApprovals.ListAsync(conn, null, DocumentType, id, ct);
    }

    /// <summary>The approvers whose APPROVING decision is the one the batch's approval rests on (the management layer re-checks that they are still authorized when the application starts).</summary>
    public async Task<IReadOnlyList<Guid>> ApprovingApproversAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand(
            $"SELECT d.{AD.Approver} FROM {AD.Table} d JOIN {IB.Table} b ON b.{IB.Id} = d.{AD.DocumentId} AND b.{IB.ApprovalRevision} = d.{AD.DocumentRevision} WHERE d.{AD.DocType} = @t AND d.{AD.DocumentId} = @id AND d.{AD.Decision} = 1", conn);
        q.Parameters.AddWithValue("t", DocumentType); q.Parameters.AddWithValue("id", id);
        var list = new List<Guid>(); await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    /// <summary>
    /// Records an independent decision on exactly the current revision of a submitted batch. Never the creator or the submitter (refused here and by PostgreSQL), one decision per approver per revision, a
    /// rejection needs a comment. An approval moves the batch to Approved and binds the approval to this revision; a rejection finishes the batch (Rejected).
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> DecideAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool? approve, string? comment, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (approve is null) return ConfigResult<ImportBatchView>.Invalid([new("approve", "required")]);
        if (approve == false && string.IsNullOrWhiteSpace(comment)) return ConfigResult<ImportBatchView>.Invalid([new("comment", "required")]);
        if (comment is { Length: > 500 }) return ConfigResult<ImportBatchView>.Invalid([new("comment", "too_long")]);
        return await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
            if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            if (cur.Status != ImportStatus.Submitted) throw new Abort(ConfigStatus.Conflict, "batch_not_submitted");
            var now = clock.GetUtcNow().UtcDateTime;
            var result = await DocumentApprovals.RecordAsync(work, new ApprovalDecisionRequest(DocumentType, id, cur.Revision, actor, cur.CreatedBy, cur.SubmittedBy, approve.Value, comment, 1), now, ct);
            switch (result.Status)
            {
                case ApprovalRecordStatus.SeparationOfDuties: throw new Abort(ConfigStatus.Forbidden, "separation_of_duties");
                case ApprovalRecordStatus.AlreadyDecided: throw new Abort(ConfigStatus.Conflict, "already_decided");
                case ApprovalRecordStatus.CommentRequired: throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("comment", "required")]);
            }
            if (approve.Value)
            {
                await using var up = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = 6, {IB.ApprovalRevision} = {IB.Revision}, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction);
                up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("t", now); await up.ExecuteNonQueryAsync(ct);
            }
            else await TerminalAsync(work, id, ImportStatus.Rejected, now, ct);
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, approve.Value ? ImportEventKind.Approved : ImportEventKind.Rejected, view.Revision, actor,
                new JsonObject { ["planHash"] = cur.PlanHash, ["decidedRevision"] = cur.Revision, ["comment"] = approve.Value ? null : comment!.Trim() }, ct);
            return view;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------- application

    private sealed record ApplyFailure(string Code, string? Detail);

    /// <summary>Another caller finished the application while this one waited for the batch lock: nothing to do and nothing to audit (the transaction ends without a mutation).</summary>
    private sealed class AlreadyApplied : Exception;

    /// <summary>
    /// Applies an approved (or previously failed, still approved) batch atomically and idempotently. Applying an applied batch returns it unchanged (a safe retry); concurrent calls serialize on the
    /// batch row. A stale plan, a changed adapter version, a missing approval or any failure of the adapter leaves the business data exactly as it was and records the failure on the batch.
    /// </summary>
    public async Task<ConfigResult<ImportBatchView>> ApplyAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var peek = await GetAsync(companyId, id, ct);
        if (peek is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.NotFound, "import_not_found");
        if (peek.Status == ImportStatus.Applied) return ConfigResult<ImportBatchView>.Ok(peek);                        // idempotent retry of a finished application
        var adapter = AdapterOf(peek);
        if (adapter is null) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "unknown_adapter");
        if (peek.Status is not (ImportStatus.Approved or ImportStatus.Failed)) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, "batch_not_approved");
        if (peek.Revision != expectedRevision.Value) return ConfigResult<ImportBatchView>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict", peek.Revision);

        var started = false;
        ApplyFailure? failure = null;
        try
        {
            var view = await transactions.ExecuteAsync(companyId, async work =>
            {
                var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "import_not_found");
                if (cur.Status == ImportStatus.Applied) throw new AlreadyApplied();                                      // another caller finished it while this one waited for the lock
                if (cur.Status is not (ImportStatus.Approved or ImportStatus.Failed)) throw new Abort(ConfigStatus.Conflict, "batch_not_approved");
                if (cur.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
                if (cur.PlanHash is null || cur.Mapping is null || cur.AdapterVersion != adapter.Version) throw new Abort(ConfigStatus.Conflict, cur.AdapterVersion != adapter.Version ? "adapter_version_changed" : "batch_not_validated");
                started = true;
                var now = clock.GetUtcNow().UtcDateTime;
                await using (var up = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = 7, {IB.Result} = NULL, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
                { up.Parameters.AddWithValue("id", id); up.Parameters.AddWithValue("t", now); await up.ExecuteNonQueryAsync(ct); }
                await adapter.BeginApplyAsync(work, ct);                                                                  // the adapter's domain locks first
                // re-validate against the data as it is NOW, under those locks: the approved plan must be exactly what would be validated today
                var recomputed = await ComputeInAsync(companyId, cur, adapter, work.Connection, work.Transaction, ct);
                if (recomputed.PlanHash != cur.PlanHash) throw new Abort(ConfigStatus.Conflict, "plan_stale");
                if (recomputed.Blocking > 0 || recomputed.Operations == 0) throw new Abort(ConfigStatus.Conflict, "plan_not_applicable");
                var ops = recomputed.Rows.Where(r => r.Operation is not null && r.Outcome is RowOutcome.Valid or RowOutcome.Resolved).OrderBy(r => r.Sequence).ThenBy(r => r.RowNo)
                    .Select(r => new ImportApplyOperation(r.RowNo, r.Sequence, (JsonObject)r.Operation!.DeepClone())).ToList();
                var applied = await adapter.ApplyAsync(new ImportApplyContext(work, actor, id, ops), ct);
                await using (var rows = new NpgsqlCommand($"UPDATE {IR.Table} SET {IR.Status} = 6 WHERE {IR.BatchId} = @id AND {IR.Status} IN (2, 5) AND jsonb_typeof({IR.Analysis} -> 'operation') = 'object'", work.Connection, work.Transaction))
                { rows.Parameters.AddWithValue("id", id); await rows.ExecuteNonQueryAsync(ct); }
                var result = new JsonObject { ["appliedAtUtc"] = now.ToString("O"), ["appliedBy"] = actor.ToString(), ["planHash"] = cur.PlanHash, ["operations"] = ops.Count, ["summary"] = applied.Summary.DeepClone() };
                await using (var fin = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = 8, {IB.TerminalAtUtc} = @t, {IB.Result} = @r, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
                {
                    fin.Parameters.AddWithValue("id", id); fin.Parameters.AddWithValue("t", now);
                    fin.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = result.ToJsonString() });
                    await fin.ExecuteNonQueryAsync(ct);
                }
                var done = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
                await EventAsync(work, id, ImportEventKind.Applied, done.Revision, actor, new JsonObject { ["planHash"] = cur.PlanHash, ["operations"] = ops.Count, ["summary"] = applied.Summary.DeepClone() }, ct);
                return done;
            }, ct);
            return ConfigResult<ImportBatchView>.Ok(view);
        }
        catch (AlreadyApplied) { return ConfigResult<ImportBatchView>.Ok((await GetAsync(companyId, id, ct))!); }
        catch (Abort abort) when (!started) { return abort.To<ImportBatchView>(); }                                      // nothing was attempted: nothing to record
        catch (Abort abort) { failure = new ApplyFailure(abort.Message, null); }
        catch (ImportApplyException ex) { failure = new ApplyFailure(ex.Code, ex.Message); }
        catch (PostgresException ex) when (started) { failure = new ApplyFailure(ex.SqlState == PostgresErrorCodes.RaiseException ? MapDatabaseRule(ex.MessageText) : "database_error", ex.ConstraintName is { } c ? $"{ex.SqlState}:{c}" : ex.SqlState); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, MapDatabaseRule(ex.MessageText)); }
        catch (Exception ex) when (started && ex is not (OperationCanceledException or Security.EncryptionUnavailableException)) { failure = new ApplyFailure("apply_failed", ex.GetType().Name); }

        // The transaction rolled back, so no business change and no half-applied state exists. Record the failure (diagnostics, never data) so it can be corrected and retried.
        await RecordFailureAsync(actor, companyId, id, failure!, ct);
        return ConfigResult<ImportBatchView>.Fail(ConfigStatus.Conflict, failure!.Code);
    }

    private async Task RecordFailureAsync(Guid actor, Guid companyId, Guid id, ApplyFailure failure, CancellationToken ct)
    {
        await MutateAsync(companyId, async work =>
        {
            var cur = await LoadAsync(work.Connection, work.Transaction, id, true, ct);
            if (cur is null || cur.Status is not (ImportStatus.Approved or ImportStatus.Failed)) return false;
            var now = clock.GetUtcNow().UtcDateTime;
            var result = new JsonObject { ["failedAtUtc"] = now.ToString("O"), ["code"] = failure.Code, ["detail"] = failure.Detail, ["planHash"] = cur.PlanHash };
            await using (var a = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = 7, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            { a.Parameters.AddWithValue("id", id); a.Parameters.AddWithValue("t", now); await a.ExecuteNonQueryAsync(ct); }
            await using (var b = new NpgsqlCommand($"UPDATE {IB.Table} SET {IB.Status} = 9, {IB.Result} = @r, {IB.Revision} = {IB.Revision} + 1, {IB.UpdatedAtUtc} = @t WHERE {IB.Id} = @id", work.Connection, work.Transaction))
            {
                b.Parameters.AddWithValue("id", id); b.Parameters.AddWithValue("t", now);
                b.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = result.ToJsonString() });
                await b.ExecuteNonQueryAsync(ct);
            }
            var view = (await LoadAsync(work.Connection, work.Transaction, id, false, ct))!;
            await EventAsync(work, id, ImportEventKind.ApplyFailed, view.Revision, actor, new JsonObject { ["code"] = failure.Code, ["detail"] = failure.Detail, ["planHash"] = cur.PlanHash }, ct);
            return true;
        }, ct);
    }
}
