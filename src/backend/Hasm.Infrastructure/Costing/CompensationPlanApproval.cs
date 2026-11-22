using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using EG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochGuard;

namespace Hasm.Infrastructure.Costing;

/// <summary>
/// Slice 2 of Phase 3.8D step 5: approval, rejection and cancellation of a stored plan (design section 4). Approval re-runs the read-only planner under the APPROVER's own permissions and compares the seven fingerprints
/// and the closure hash with the ones stored: any difference marks the plan stale (in its own transaction) and refuses. The approval row and the status change are written together in one audited transaction; the
/// database repeats every rule (independence of the approver, the event document approved at the planned revision and not posted, fingerprint and closure binding) and its refusals are mapped to result codes.
/// Reject and cancel are history events. Nothing executes and no endpoint exists.
/// </summary>
internal sealed class CompensationPlanApprovalService(CompanyDatabaseResolver resolver, AccessService access, CompanyAuditedTransactions transactions, CostFlowSnapshotService planner, TimeProvider clock)
{
    private static ConfigResult<StoredCompensationPlan> Fail(ConfigStatus status, string code, long? revision = null) => ConfigResult<StoredCompensationPlan>.Fail(status, code, revision);

    private async Task<StoredCompensationPlan?> ReadAsync(Guid company, Guid planId, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(company, ct);
        return await CompensationPlanStore.LoadAsync(conn, null, planId, false, ct);
    }

    /// <summary>The plan changed between the read and the lock: the unit of work is abandoned (nothing written, no audit event) and this result is returned.</summary>
    private sealed class Changed(ConfigResult<StoredCompensationPlan> result) : Exception { internal ConfigResult<StoredCompensationPlan> Result { get; } = result; }

    /// <summary>Common start of every action: the ids and the plan.</summary>
    private async Task<(StoredCompensationPlan? Plan, ConfigResult<StoredCompensationPlan>? Refusal)> StartAsync(Guid user, Guid company, Guid planId, long expectedRevision, CancellationToken ct)
    {
        if (user == Guid.Empty || company == Guid.Empty || planId == Guid.Empty || expectedRevision <= 0) return (null, Fail(ConfigStatus.Invalid, "invalid_plan_input"));
        var plan = await ReadAsync(company, planId, ct);
        if (plan is null) return (null, Fail(ConfigStatus.NotFound, "plan_not_found"));
        return (plan, null);
    }

    private static ConfigResult<StoredCompensationPlan>? Precondition(StoredCompensationPlan plan, long expectedRevision, params short[] statuses) =>
        plan.Revision != expectedRevision ? Fail(ConfigStatus.PreconditionFailed, "revision_mismatch", plan.Revision)
        : !statuses.Contains(plan.Status) ? Fail(ConfigStatus.Conflict, statuses.Contains(CompensationPlanStore.Proposed) && statuses.Length == 1 ? "plan_not_proposed" : "plan_not_open")
        : null;

    // ---------------------------------------------------------------------------------------------------------------- approve
    internal async Task<ConfigResult<StoredCompensationPlan>> ApproveAsync(Guid user, Guid company, Guid planId, long expectedRevision, string? note = null, CancellationToken ct = default)
    {
        var (plan, refusal) = await StartAsync(user, company, planId, expectedRevision, ct);
        if (refusal is not null) return refusal;
        if (!await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.CompensationPlansApprove, ScopeTarget.Warehouse(plan!.WarehouseId), ct)) return Fail(ConfigStatus.Forbidden, "forbidden");
        if (Precondition(plan, expectedRevision, CompensationPlanStore.Proposed) is { } wrong) return wrong;
        if (note is { Length: > 500 }) return Fail(ConfigStatus.Invalid, "invalid_plan_input");

        // The event document: approved at the planned revision and not posted (X7; PostgreSQL repeats both at the approval row). Checked before the planner, whose history a posting of the document would change.
        var document = await CompensationPlanStore.ReadDocumentAsync(resolver, company, plan.DocumentId, ct);
        if (document is null || document.Status != 3 || document.Revision != plan.DocumentRevision) return Fail(ConfigStatus.Conflict, "document_not_approved");
        if (document.Posted) return Fail(ConfigStatus.Conflict, "document_already_posted");

        // X3 (cheap, and the approver must not even learn the planner's view of a plan that is not theirs to approve): the same function the database trigger uses
        if (!await IndependentAsync(company, plan.Id, user, ct)) return Fail(ConfigStatus.Conflict, "approver_not_independent");

        // Staleness: the planner again, under the APPROVER's permissions. Its refusals (forbidden, a closed period, ...) are returned unchanged and mark nothing stale.
        var proposal = CompensationPlanBinding.Proposal(plan.Payload);
        if (proposal is null) return Fail(ConfigStatus.Conflict, "plan_payload_invalid");
        var planned = await planner.LoadCompensationPlanAsync(user, company, plan.ItemId, [plan.WarehouseId], proposal, ct);
        if (!planned.IsOk) return Fail(planned.Status, planned.Code!);
        var fresh = CompensationPlanBinding.Fingerprint(company, plan.ItemId, planned.Value!);
        var closure = CompensationPlanBinding.Closure(plan.ItemId, planned.Value!);
        if (!CompensationPlanBinding.Matches(plan, fresh, CompensationPlanBinding.ClosureHash(closure)))
        {
            await MarkStaleAsync(user, company, plan, ct);
            return Fail(ConfigStatus.Conflict, "plan_stale");
        }

        try
        {
            return await transactions.ExecuteAsync<ConfigResult<StoredCompensationPlan>>(company, async work =>
            {
                var locked = await CompensationPlanStore.LoadAsync(work.Connection, work.Transaction, plan.Id, true, ct) ?? throw new CostingException("plan_not_found");
                if (Precondition(locked, expectedRevision, CompensationPlanStore.Proposed) is { } changed) throw new Changed(changed);   // changed since it was read: nothing is written
                var now = clock.GetUtcNow();
                var approval = Guid.NewGuid();
                await using (var q = new NpgsqlCommand($"INSERT INTO {CA.Table} ({CA.Id},{CA.PlanId},{CA.PlanRevision},{CA.PlanFingerprint},{CA.ComparisonFingerprint},{CA.EvidenceFingerprint},{CA.PeriodsFingerprint},{CA.PositionFingerprint}," +
                    $"{CA.PolicyFingerprint},{CA.AccountingFingerprint},{CA.ClosureHash},{CA.Approver},{CA.ApprovedAtUtc},{CA.Note}) VALUES (@id,@plan,@rev,@f1,@f2,@f3,@f4,@f5,@f6,@f7,@closure,@by,@at,@note)", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("id", approval); q.Parameters.AddWithValue("plan", plan.Id); q.Parameters.AddWithValue("rev", locked.Revision);
                    for (var i = 0; i < 7; i++) q.Parameters.AddWithValue($"f{i + 1}", locked.Fingerprints.All[i]);
                    q.Parameters.AddWithValue("closure", locked.ClosureHash); q.Parameters.AddWithValue("by", user); q.Parameters.AddWithValue("at", now); q.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
                    await q.ExecuteNonQueryAsync(ct);
                }
                await using (var q = new NpgsqlCommand($"UPDATE {CP.Table} SET {CP.Status}=2,{CP.ApprovalId}=@approval,{CP.ApprovedBy}=@by,{CP.ApprovedAtUtc}=@at,{CP.Revision}={CP.Revision}+1,{CP.UpdatedAtUtc}=@at WHERE {CP.Id}=@plan", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("approval", approval); q.Parameters.AddWithValue("by", user); q.Parameters.AddWithValue("at", now); q.Parameters.AddWithValue("plan", plan.Id);
                    await q.ExecuteNonQueryAsync(ct);
                }
                await CompensationPlanStore.AddEventAsync(work, plan.Id, CompensationPlanStore.EventApproved, user, note, now, ct);
                await work.AppendAuditAsync(new AuditEventData(CostingAuditEvents.PlanApproved, user, null, plan.Id, $"approval={approval}; revision={locked.Revision}; closure={locked.ClosureHash}"), ct);
                return ConfigResult<StoredCompensationPlan>.Ok((await CompensationPlanStore.LoadAsync(work.Connection, work.Transaction, plan.Id, false, ct))!);
            }, ct);
        }
        catch (Changed c) { return c.Result; }
        catch (PostgresException e) when (CompensationPlanStore.Refusal(e) is { } mapped) { return Fail(mapped.Status, mapped.Code); }
        catch (CostingException e) { return Fail(ConfigStatus.Conflict, e.Code); }
    }

    // ---------------------------------------------------------------------------------------------------------------- reject and cancel
    /// <summary>Rejection of a proposed plan by an approver (a history event; the document is released).</summary>
    internal Task<ConfigResult<StoredCompensationPlan>> RejectAsync(Guid user, Guid company, Guid planId, long expectedRevision, string? note = null, CancellationToken ct = default) =>
        TransitionAsync(user, company, planId, expectedRevision, note, CompensationPlanStore.Rejected, CompensationPlanStore.EventRejected, CostingAuditEvents.PlanRejected, [CompensationPlanStore.Proposed], approverOnly: true, ct);

    /// <summary>Cancellation of a proposed or approved plan by its creator (who still holds the plan permission) or by an approver (a history event; the document is released).</summary>
    internal Task<ConfigResult<StoredCompensationPlan>> CancelAsync(Guid user, Guid company, Guid planId, long expectedRevision, string? note = null, CancellationToken ct = default) =>
        TransitionAsync(user, company, planId, expectedRevision, note, CompensationPlanStore.Cancelled, CompensationPlanStore.EventCancelled, CostingAuditEvents.PlanCancelled, [CompensationPlanStore.Proposed, CompensationPlanStore.Approved], approverOnly: false, ct);

    private async Task<ConfigResult<StoredCompensationPlan>> TransitionAsync(Guid user, Guid company, Guid planId, long expectedRevision, string? note, short status, short eventKind, short auditType, short[] from, bool approverOnly, CancellationToken ct)
    {
        var (plan, refusal) = await StartAsync(user, company, planId, expectedRevision, ct);
        if (refusal is not null) return refusal;
        var scope = ScopeTarget.Warehouse(plan!.WarehouseId);
        var allowed = await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.CompensationPlansApprove, scope, ct)
            || (!approverOnly && plan.CreatedBy == user && await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.CompensationPlansCreate, scope, ct));
        if (!allowed) return Fail(ConfigStatus.Forbidden, "forbidden");
        if (Precondition(plan, expectedRevision, from) is { } wrong) return wrong;
        if (note is { Length: > 500 }) return Fail(ConfigStatus.Invalid, "invalid_plan_input");
        try
        {
            return await transactions.ExecuteAsync<ConfigResult<StoredCompensationPlan>>(company, async work =>
            {
                var locked = await CompensationPlanStore.LoadAsync(work.Connection, work.Transaction, plan.Id, true, ct) ?? throw new CostingException("plan_not_found");
                if (Precondition(locked, expectedRevision, from) is { } changed) throw new Changed(changed);
                await SetStatusAsync(work, locked, status, clock.GetUtcNow(), ct);
                await CompensationPlanStore.AddEventAsync(work, plan.Id, eventKind, user, note, clock.GetUtcNow(), ct);
                await work.AppendAuditAsync(new AuditEventData(auditType, user, null, plan.Id, note), ct);
                return ConfigResult<StoredCompensationPlan>.Ok((await CompensationPlanStore.LoadAsync(work.Connection, work.Transaction, plan.Id, false, ct))!);
            }, ct);
        }
        catch (Changed c) { return c.Result; }
        catch (PostgresException e) when (CompensationPlanStore.Refusal(e) is { } mapped) { return Fail(mapped.Status, mapped.Code); }
        catch (CostingException e) { return Fail(ConfigStatus.Conflict, e.Code); }
    }

    private static async Task SetStatusAsync(CompanyAuditedWork work, StoredCompensationPlan plan, short status, DateTimeOffset now, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"UPDATE {CP.Table} SET {CP.Status}=@status,{CP.Revision}={CP.Revision}+1,{CP.UpdatedAtUtc}=@at WHERE {CP.Id}=@plan AND {CP.Revision}=@rev", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("status", status); q.Parameters.AddWithValue("at", now); q.Parameters.AddWithValue("plan", plan.Id); q.Parameters.AddWithValue("rev", plan.Revision);
        if (await q.ExecuteNonQueryAsync(ct) != 1) throw new CostingException("revision_mismatch");
    }

    // ---------------------------------------------------------------------------------------------------------------- stale
    /// <summary>The plan no longer matches what the planner computes now: marked stale in its OWN transaction (a stale plan is never approved or executed; a new plan and approval are needed). A concurrent change wins.</summary>
    private async Task MarkStaleAsync(Guid user, Guid company, StoredCompensationPlan plan, CancellationToken ct)
    {
        try
        {
            await transactions.ExecuteAsync<bool>(company, async work =>
            {
                var locked = await CompensationPlanStore.LoadAsync(work.Connection, work.Transaction, plan.Id, true, ct);
                if (locked is null || locked.Revision != plan.Revision || locked.Status != CompensationPlanStore.Proposed)
                    throw new CostingException("revision_mismatch");   // nothing to mark; the audit requirement then rolls the (empty) unit back
                var now = clock.GetUtcNow();
                await SetStatusAsync(work, locked, CompensationPlanStore.Stale, now, ct);
                await CompensationPlanStore.AddEventAsync(work, plan.Id, CompensationPlanStore.EventStaled, user, "the plan no longer matches the planner", now, ct);
                await work.AppendAuditAsync(new AuditEventData(CostingAuditEvents.PlanStaled, user, null, plan.Id, "fingerprint or closure mismatch at approval"), ct);
                return true;
            }, ct);
        }
        catch (CostingException) { /* changed concurrently: the caller still refuses with plan_stale */ }
    }

    private async Task<bool> IndependentAsync(Guid company, Guid planId, Guid user, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(company, ct);
        await using var q = new NpgsqlCommand($"SELECT {EG.FnIndependent}(@plan,@user)", conn);
        q.Parameters.AddWithValue("plan", planId); q.Parameters.AddWithValue("user", user);
        return await q.ExecuteScalarAsync(ct) is true;
    }
}
