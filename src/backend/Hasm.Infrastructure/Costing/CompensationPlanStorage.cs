using System.Collections.Immutable;
using System.Data;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanPosition;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanEvent;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanLine;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using CX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanExecution;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using SL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentLine;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Infrastructure.Costing;

/// <summary>What the creator asks for: the item and the ROOT warehouse of the plan, the already approved stock document of the back-dated event, and the event as the planner takes it (the amounts of a receipt are
/// part of the proposal because they are entered when the document is posted, not stored on it).</summary>
internal sealed record CompensationPlanInput(Guid ItemId, Guid WarehouseId, Guid DocumentId, ProposedFlowOperation Proposal);

/// <summary>A stored plan as PostgreSQL holds it. Status: 1 proposed, 2 approved, 3 executed, 4 rejected, 5 cancelled, 6 stale.</summary>
internal sealed record StoredCompensationPlan(Guid Id, Guid ItemId, Guid WarehouseId, Guid DocumentId, long DocumentRevision, short Status, long Revision, string ClosureHash,
    CompensationPlanFingerprints Fingerprints, ImmutableArray<(Guid Item, Guid Warehouse)> Closure, string Payload, Guid CreatedBy, Guid? ApprovalId, Guid? ApprovedBy);

/// <summary>
/// Reading of stored plans and the mapping of the database refusals of migration 0036 to result codes. A rule that PostgreSQL enforces is never replaced by the service: the service checks first so that the caller
/// gets a precise code, and still maps what PostgreSQL refuses (a race, or a rule the service did not pre-check) to the same codes.
/// </summary>
internal static class CompensationPlanStore
{
    internal const short Proposed = 1, Approved = 2, Executed = 3, Rejected = 4, Cancelled = 5, Stale = 6;
    internal const short EventCreated = 1, EventApproved = 2, EventRejected = 3, EventCancelled = 4, EventStaled = 6;

    internal static async Task<StoredCompensationPlan?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid planId, bool forUpdate, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {CP.ItemId},{CP.WarehouseId},{CP.DocumentId},{CP.DocumentRevision},{CP.Payload},{CP.PlanFingerprint},{CP.ComparisonFingerprint},{CP.EvidenceFingerprint},{CP.PeriodsFingerprint}," +
            $"{CP.PositionFingerprint},{CP.PolicyFingerprint},{CP.AccountingFingerprint},{CP.ClosureHash},{CP.Status},{CP.Revision},{CP.CreatedBy},{CP.ApprovalId},{CP.ApprovedBy} FROM {CP.Table} WHERE {CP.Id}=@id" + (forUpdate ? " FOR UPDATE" : ""), conn, tx);
        q.Parameters.AddWithValue("id", planId);
        StoredCompensationPlan? plan = null;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            plan = new StoredCompensationPlan(planId, r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt64(3), r.GetInt16(13), r.GetInt64(14), r.GetString(12).Trim(),
                new(r.GetString(5).Trim(), r.GetString(6).Trim(), r.GetString(7).Trim(), r.GetString(8).Trim(), r.GetString(9).Trim(), r.GetString(10).Trim(), r.GetString(11).Trim()), [], r.GetString(4), r.GetGuid(15),
                r.IsDBNull(16) ? null : r.GetGuid(16), r.IsDBNull(17) ? null : r.GetGuid(17));
        }
        var closure = new List<(Guid, Guid)>();
        await using (var c = new NpgsqlCommand($"SELECT {CC.ItemId},{CC.WarehouseId} FROM {CC.Table} WHERE {CC.PlanId}=@id ORDER BY {CC.ItemId},{CC.WarehouseId}", conn, tx))
        {
            c.Parameters.AddWithValue("id", planId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) closure.Add((r.GetGuid(0), r.GetGuid(1)));
        }
        return plan with { Closure = [.. closure] };
    }

    /// <summary>The refusals of the plan tables, as (status, code); null when the exception is none of them (the caller rethrows).</summary>
    internal static (ConfigStatus Status, string Code)? Refusal(PostgresException e)
    {
        if (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == CP.UqLiveDocument) return (ConfigStatus.Conflict, "plan_exists_for_document");
        if (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == CA.UqPlanRevision) return (ConfigStatus.Conflict, "plan_not_proposed");
        if (e.SqlState != PostgresErrorCodes.RaiseException) return null;
        var text = e.MessageText;
        foreach (var code in new[] { "document_already_posted", "document_not_approved", "approver_not_independent", "approval_fingerprint_mismatch", "closure_hash_mismatch", "plan_on_epoch_position", "plan_not_found", "approval_not_found" })
            if (text.Contains(code, StringComparison.Ordinal)) return (ConfigStatus.Conflict, code);
        return null;
    }

    /// <summary>The state of an event document that a plan binds: status, revision, type, warehouses, date, base quantity per item, and whether a stock posting exists. One consistent (RepeatableRead) view.</summary>
    internal sealed record DocumentState(short Status, long Revision, short Type, Guid? Source, Guid? Target, DateOnly Date, Dictionary<Guid, decimal> Quantities, bool Posted);

    internal static async Task<DocumentState?> ReadDocumentAsync(CompanyDatabaseResolver resolver, Guid company, Guid documentId, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(company, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);   // one consistent view of the document, its lines and its posting
        DocumentState? doc;
        await using (var q = new NpgsqlCommand($"SELECT {SD.Status},{SD.Revision},{SD.DocType},{SD.SourceWarehouseId},{SD.TargetWarehouseId},{SD.DocumentDate} FROM {SD.Table} WHERE {SD.Id}=@id", conn, tx))
        {
            q.Parameters.AddWithValue("id", documentId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            doc = new DocumentState(r.GetInt16(0), r.GetInt64(1), r.GetInt16(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetFieldValue<DateOnly>(5), [], false);
        }
        await using (var q = new NpgsqlCommand($"SELECT {SL.ItemId},sum({SL.BaseQuantity}) FROM {SL.Table} WHERE {SL.DocumentId}=@id GROUP BY {SL.ItemId}", conn, tx))
        {
            q.Parameters.AddWithValue("id", documentId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) doc.Quantities[r.GetGuid(0)] = r.GetDecimal(1);
        }
        await using (var q = new NpgsqlCommand($"SELECT 1 FROM {SP.Table} WHERE {SP.DocumentId}=@id", conn, tx))
        {
            q.Parameters.AddWithValue("id", documentId);
            doc = doc with { Posted = await q.ExecuteScalarAsync(ct) is not null };
        }
        return doc;
    }

    internal static async Task AddEventAsync(CompanyAuditedWork work, Guid planId, short kind, Guid actor, string? note, DateTimeOffset now, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"INSERT INTO {CE.Table} ({CE.Id},{CE.PlanId},{CE.Kind},{CE.ActorUserId},{CE.AtUtc},{CE.Note}) VALUES (@id,@plan,@kind,@actor,@at,@note)", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("id", Guid.NewGuid()); q.Parameters.AddWithValue("plan", planId); q.Parameters.AddWithValue("kind", kind); q.Parameters.AddWithValue("actor", actor);
        q.Parameters.AddWithValue("at", now); q.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        await q.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Slice 1 of Phase 3.8D step 5: the plan storage service. From the read-only planner output (run under the CREATOR's permissions) it writes the plan, its closure, its compensation lines and its created event in ONE
/// transaction, reads the PostgreSQL-computed closure hash back and compares it with the byte-exact hash computed here, and appends the audit event. It writes nothing else and executes nothing: no endpoint, no
/// approval, no posting. The event's stock document must already be approved (X7) and not yet posted; the planner's refusals are returned unchanged; a position inside the closure of an executed plan is refused
/// with <c>plan_on_epoch_position</c> (X5, which PostgreSQL also enforces at execution and at every epoch entry).
/// </summary>
internal sealed class CompensationPlanService(CompanyDatabaseResolver resolver, AccessService access, CompanyAuditedTransactions transactions, CostFlowSnapshotService planner, TimeProvider clock)
{
    private static ConfigResult<T> Fail<T>(ConfigStatus status, string code) => ConfigResult<T>.Fail(status, code);

    internal async Task<ConfigResult<StoredCompensationPlan>> CreateAsync(Guid user, Guid company, CompensationPlanInput input, CancellationToken ct = default)
    {
        if (input?.Proposal is null || input.ItemId == Guid.Empty || input.WarehouseId == Guid.Empty || input.DocumentId == Guid.Empty || user == Guid.Empty || company == Guid.Empty || input.Proposal.Lines.IsDefaultOrEmpty)
            return Fail<StoredCompensationPlan>(ConfigStatus.Invalid, "invalid_plan_input");
        // Permission first, on the plan's root warehouse. The document is then read only for a caller who may create the plan and see documents on that root warehouse, and its state (or the fact that it exists) is
        // revealed only when the root warehouse is the document's source or target; for any other document the answer is the same document_not_found as for an id that does not exist (no status oracle across warehouses).
        if (!await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.CompensationPlansCreate, ScopeTarget.Warehouse(input.WarehouseId), ct)
            || !await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockDocsView, ScopeTarget.Warehouse(input.WarehouseId), ct))
            return Fail<StoredCompensationPlan>(ConfigStatus.Forbidden, "forbidden");

        var pre = await PrecheckAsync(company, input, ct);
        if (pre is not null) return pre;

        // X5 on the root first: a position of an executed epoch is never planned again, whatever the planner would say about its history (an epoch's own entries are not ordinary history).
        if (await OnExecutedEpochAsync(company, [(input.ItemId, input.WarehouseId)], ct)) return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "plan_on_epoch_position");

        // The read-only planner, under the creator's own permissions. Its refusals are returned unchanged.
        var planned = await planner.LoadCompensationPlanAsync(user, company, input.ItemId, [input.WarehouseId], input.Proposal, ct);
        if (!planned.IsOk) return Fail<StoredCompensationPlan>(planned.Status, planned.Code!);
        var result = planned.Value!;
        var closure = CompensationPlanBinding.Closure(input.ItemId, result);
        if (!closure.Contains((input.ItemId, input.WarehouseId))) return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "plan_closure_invalid");
        if (result.Plan.Lines.Any(l => l.PeriodId is null)) return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "plan_line_period_missing");
        var fingerprints = CompensationPlanBinding.Fingerprint(company, input.ItemId, result);
        var expectedHash = CompensationPlanBinding.ClosureHash(closure);

        // X5 again, on the whole closure the planner found (the root was checked before the planner ran)
        if (await OnExecutedEpochAsync(company, closure, ct)) return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "plan_on_epoch_position");

        var planId = Guid.NewGuid();
        var payload = CompensationPlanBinding.PayloadOf(input.Proposal);
        var doc = (await CompensationPlanStore.ReadDocumentAsync(resolver, company, input.DocumentId, ct))!;
        try
        {
            return await transactions.ExecuteAsync<ConfigResult<StoredCompensationPlan>>(company, async work =>
            {
                var now = clock.GetUtcNow();
                await using (var q = new NpgsqlCommand($"INSERT INTO {CP.Table} ({CP.Id},{CP.ItemId},{CP.WarehouseId},{CP.DocumentId},{CP.DocumentRevision},{CP.Payload},{CP.PlanFingerprint},{CP.ComparisonFingerprint},{CP.EvidenceFingerprint}," +
                    $"{CP.PeriodsFingerprint},{CP.PositionFingerprint},{CP.PolicyFingerprint},{CP.AccountingFingerprint},{CP.Status},{CP.Revision},{CP.CreatedBy},{CP.CreatedAtUtc},{CP.UpdatedAtUtc}) " +
                    "VALUES (@id,@item,@wh,@doc,@rev,@payload,@f1,@f2,@f3,@f4,@f5,@f6,@f7,1,1,@by,@at,@at)", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("id", planId); q.Parameters.AddWithValue("item", input.ItemId); q.Parameters.AddWithValue("wh", input.WarehouseId); q.Parameters.AddWithValue("doc", input.DocumentId);
                    q.Parameters.AddWithValue("rev", doc.Revision); q.Parameters.AddWithValue("payload", payload);
                    for (var i = 0; i < 7; i++) q.Parameters.AddWithValue($"f{i + 1}", fingerprints.All[i]);
                    q.Parameters.AddWithValue("by", user); q.Parameters.AddWithValue("at", now);
                    await q.ExecuteNonQueryAsync(ct);
                }
                foreach (var (item, warehouse) in closure)
                {
                    await using var q = new NpgsqlCommand($"INSERT INTO {CC.Table} ({CC.PlanId},{CC.ItemId},{CC.WarehouseId}) VALUES (@plan,@item,@wh)", work.Connection, work.Transaction);
                    q.Parameters.AddWithValue("plan", planId); q.Parameters.AddWithValue("item", item); q.Parameters.AddWithValue("wh", warehouse);
                    await q.ExecuteNonQueryAsync(ct);
                }
                var no = 0;
                foreach (var line in result.Plan.Lines)
                {
                    await using var q = new NpgsqlCommand($"INSERT INTO {CL.Table} ({CL.PlanId},{CL.LineNo},{CL.PostingId},{CL.EntryDate},{CL.PeriodId},{CL.BranchId},{CL.AccountId},{CL.Debit},{CL.Credit}) VALUES (@plan,@no,@posting,@date,@period,@branch,@account,@debit,@credit)", work.Connection, work.Transaction);
                    q.Parameters.AddWithValue("plan", planId); q.Parameters.AddWithValue("no", ++no); q.Parameters.AddWithValue("posting", line.PostingId); q.Parameters.AddWithValue("date", line.Date);
                    q.Parameters.AddWithValue("period", line.PeriodId!.Value); q.Parameters.AddWithValue("branch", line.BranchId); q.Parameters.AddWithValue("account", line.AccountId);
                    q.Parameters.AddWithValue("debit", line.Debit); q.Parameters.AddWithValue("credit", line.Credit);
                    await q.ExecuteNonQueryAsync(ct);
                }
                await CompensationPlanStore.AddEventAsync(work, planId, CompensationPlanStore.EventCreated, user, null, now, ct);
                // PostgreSQL computed the closure hash as the closure rows were written; it must equal the byte-exact hash computed here, or nothing is stored.
                var stored = await CompensationPlanStore.LoadAsync(work.Connection, work.Transaction, planId, false, ct) ?? throw new CostingException("plan_not_stored");
                if (stored.ClosureHash != expectedHash) throw new CostingException("closure_hash_mismatch");
                if (!stored.Closure.SequenceEqual(closure.OrderBy(c => c.Item).ThenBy(c => c.Warehouse))) throw new CostingException("plan_closure_invalid");
                await work.AppendAuditAsync(new AuditEventData(CostingAuditEvents.PlanProposed, user, null, planId, $"closure={stored.ClosureHash}; plan={fingerprints.Plan}; document={input.DocumentId}@{doc.Revision}"), ct);
                return ConfigResult<StoredCompensationPlan>.Ok(stored);
            }, ct);
        }
        catch (PostgresException e) when (CompensationPlanStore.Refusal(e) is { } refusal) { return Fail<StoredCompensationPlan>(refusal.Status, refusal.Code); }
        catch (CostingException e) { return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, e.Code); }
    }

    /// <summary>X5: true when any of the positions lies in the closure of a plan that has an execution (an epoch is never rewritten by another epoch).</summary>
    private async Task<bool> OnExecutedEpochAsync(Guid company, IReadOnlyCollection<(Guid Item, Guid Warehouse)> positions, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(company, ct);
        await using var q = new NpgsqlCommand($"SELECT 1 FROM {CC.Table} c JOIN {CX.Table} x ON x.{CX.PlanId}=c.{CC.PlanId} JOIN unnest(@items, @whs) AS p(i,w) ON p.i=c.{CC.ItemId} AND p.w=c.{CC.WarehouseId} LIMIT 1", conn);
        q.Parameters.AddWithValue("items", positions.Select(c => c.Item).ToArray()); q.Parameters.AddWithValue("whs", positions.Select(c => c.Warehouse).ToArray());
        return await q.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>The event document must exist, be approved (not posted, not bound to a live plan) and be exactly the event the plan proposes. Null when it is.</summary>
    private async Task<ConfigResult<StoredCompensationPlan>?> PrecheckAsync(Guid company, CompensationPlanInput input, CancellationToken ct)
    {
        var doc = await CompensationPlanStore.ReadDocumentAsync(resolver, company, input.DocumentId, ct);
        if (doc is null || (doc.Source != input.WarehouseId && doc.Target != input.WarehouseId)) return Fail<StoredCompensationPlan>(ConfigStatus.NotFound, "document_not_found");
        if (doc.Status != 3) return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "document_not_approved");
        if (doc.Posted) return Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "document_already_posted");
        var p = input.Proposal;
        // one item, one document type, one line: the shape the planner takes; anything else is a different event than the document says
        var line = p.Lines.Length == 1 ? p.Lines[0] : (ProposedFlowLine?)null;
        var matches = line is { } l && (short)p.Kind == doc.Type && p.EffectiveDate == doc.Date && l.WarehouseId == input.WarehouseId
            && doc.Quantities.Count == 1 && doc.Quantities.TryGetValue(input.ItemId, out var quantity) && quantity == l.Quantity
            && (p.Kind switch
            {
                CostOperationKind.Receipt => doc.Target == l.WarehouseId && l.DestinationWarehouseId is null,
                CostOperationKind.Issue => doc.Source == l.WarehouseId && l.DestinationWarehouseId is null,
                CostOperationKind.Transfer => doc.Source == l.WarehouseId && doc.Target == l.DestinationWarehouseId,
                _ => false,
            });
        return matches ? null : Fail<StoredCompensationPlan>(ConfigStatus.Conflict, "document_plan_mismatch");
    }
}
