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
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicyConditions;
using AS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalPolicyStage;
using AB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentApprovalBinding;
using AG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalStaging;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ClosingLogic;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Approval-policy configuration (Phase 3.2B, slice S4). <b>No authorization here by design:</b> <see cref="ApprovalPolicyManagementService"/> wraps it.
/// Policies are audited, revisioned and never deleted; PostgreSQL enforces their shape and the evaluation function used by the posting engine.
/// </summary>
public sealed class ApprovalPolicyService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock, Hasm.Infrastructure.Commercial.ICommercialDocumentTypeRegistry? commercialTypes = null)
{
    internal const int LockKey1 = 0x48434150;   // "HCAP": advisory-lock namespace of approval-policy changes
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    private static readonly string Columns =
        $"p.{AP.Id}, p.{AP.DocType}, p.{AP.Source}, p.{AP.BranchId}, p.{AP.MinAmount}, p.{AP.RequiredApprovals}, p.{AP.NameAr}, p.{AP.NameEn}, p.{AP.IsActive}, p.{AP.Revision}, p.{AP.CreatedAtUtc}, p.{AP.UpdatedAtUtc}, p.{AC.JournalType}, p.{AC.RateOverride}, p.{AC.SoftClosed}, p.{CL.PolicyClosingKind}";

    internal static string ClosingKindName(short? t) => t switch { 1 => "closing", 2 => "opening", _ => "any" };

    internal static string JournalTypeName(short? t) => t switch { 1 => "normal", 2 => "reversal", 3 => "adjusting", _ => "any" };

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
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                list.Add(new ApprovalPolicyView(r.GetGuid(0), r.GetString(1), (PolicySource)r.GetInt16(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetDecimal(4), r.GetInt16(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetInt64(9), new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero), new DateTimeOffset(r.GetDateTime(11), TimeSpan.Zero),
                    r.IsDBNull(12) ? null : JournalTypeName(r.GetInt16(12)), r.IsDBNull(13) ? null : r.GetBoolean(13), r.IsDBNull(14) ? null : r.GetBoolean(14), null, r.IsDBNull(15) ? null : ClosingKindName(r.GetInt16(15))));
        if (list.Count == 0) return list;
        // The stages of each policy's CURRENT revision (none = a classic single-stage policy).
        var stages = new Dictionary<Guid, List<ApprovalStageView>>();
        await using (var sc = new NpgsqlCommand(
            $"SELECT st.{AS.PolicyId}, st.{AS.StageNo}, st.{AS.Required}, st.{AS.RoleId} FROM {AS.Table} st JOIN {AP.Table} p ON p.{AP.Id} = st.{AS.PolicyId} AND p.{AP.Revision} = st.{AS.PolicyRevision} {(id is null ? "" : $"WHERE p.{AP.Id} = @id")} ORDER BY st.{AS.PolicyId}, st.{AS.StageNo}", conn, tx))
        {
            if (id is not null) sc.Parameters.AddWithValue("id", id.Value);
            await using var r = await sc.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!stages.TryGetValue(r.GetGuid(0), out var l)) stages[r.GetGuid(0)] = l = [];
                l.Add(new ApprovalStageView(r.GetInt16(1), r.GetInt16(2), r.IsDBNull(3) ? null : r.GetGuid(3)));
            }
        }
        return [.. list.Select(p => stages.TryGetValue(p.Id, out var s) ? p with { Stages = s, RequiredApprovals = s.Sum(x => x.Required) } : p)];
    }

    public async Task<ConfigResult<ApprovalPolicyView>> CreateAsync(Guid actor, Guid companyId, ApprovalPolicyInput? input, CancellationToken ct = default)
    {
        if (!ApprovalValidation.TryNormalizeFull(input, out var np, out var errors, commercialTypes is null ? null : commercialTypes.IsRegistered)) return ConfigResult<ApprovalPolicyView>.Invalid(errors);
        var (docType, source, min, required, nameAr, nameEn) = (np.DocType, np.Source, np.MinAmount, np.Required, np.NameAr, np.NameEn);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            if (input!.BranchId is { } bid) await RequireActiveBranchAsync(work, bid, ct);
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            await ExecAsync(work, $"INSERT INTO {AP.Table} ({AP.Id}, {AP.DocType}, {AP.Source}, {AP.BranchId}, {AP.MinAmount}, {AP.RequiredApprovals}, {AP.NameAr}, {AP.NameEn}, {AP.IsActive}, {AP.Revision}, {AP.CreatedAtUtc}, {AP.UpdatedAtUtc}, {AC.JournalType}, {AC.RateOverride}, {AC.SoftClosed}, {CL.PolicyClosingKind}) VALUES (@id, @doc, @src, @branch, @min, @req, @ar, @en, true, 1, @now, @now, @jt, @ro, @sc, @ck)",
                p => { Bind(p, id, docType, source, input.BranchId, min, required, nameAr, nameEn, now); BindConditions(p, np); });
            await InsertStagesAsync(work, id, 1, np.Stages, ct);
            var changes = new JsonObject { ["docType"] = docType, ["source"] = source.ToString(), ["branchId"] = input.BranchId?.ToString(), ["minAmount"] = min.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), ["requiredApprovals"] = np.Total, ["nameAr"] = nameAr, ["nameEn"] = nameEn,
                ["journalType"] = JournalTypeName(np.JournalType), ["rateOverride"] = np.RateOverride?.ToString(), ["softClosed"] = np.SoftClosed?.ToString(), ["closingKind"] = ClosingKindName(np.ClosingKind), ["stages"] = StagesText(np.Stages) };
            await RecordAsync(work, LedgerAuditEvents.ApprovalPolicyCreated, actor, id, 1, changes, "approval policy created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct);
    }

    public async Task<ConfigResult<ApprovalPolicyView>> UpdateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, ApprovalPolicyDetailsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ApprovalPolicyView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ApprovalValidation.TryNormalizeDetailsFull(input, "", out var np, out var errors)) return ConfigResult<ApprovalPolicyView>.Invalid(errors);
        var (source, min, required, nameAr, nameEn) = (np.Source, np.MinAmount, np.Required, np.NameAr, np.NameEn);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (!DocumentTypes.UsesApprovalPolicies(cur.DocType))   // a policy of a commercial document type never carries a journal-only condition (3.13 S5a); the journal path is unchanged
            {
                var notApplicable = new List<FieldError>();
                ApprovalValidation.JournalOnlyConditions(np.JournalType, np.RateOverride, np.SoftClosed, np.ClosingKind, notApplicable);
                if (notApplicable.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: notApplicable);
            }
            var curStages = cur.Stages ?? [];
            var sameStages = curStages.Count == np.Stages.Count && curStages.Zip(np.Stages).All(z => z.First.Required == z.Second.Required && z.First.RoleId == z.Second.RoleId);
            var sameRequired = np.Stages.Count > 0 || cur.IsStaged ? sameStages : cur.RequiredApprovals == required;
            if (cur.Source == source && cur.BranchId == input!.BranchId && cur.MinAmount == min && sameRequired && sameStages && cur.NameAr == nameAr && cur.NameEn == nameEn
                && cur.JournalType == (np.JournalType is null ? null : JournalTypeName(np.JournalType)) && cur.RateOverride == np.RateOverride && cur.SoftClosed == np.SoftClosed
                && cur.ClosingKind == (np.ClosingKind is null ? null : ClosingKindName(np.ClosingKind))) throw new Abort(ConfigStatus.Ok, "unchanged");
            if (input!.BranchId is { } bid && bid != cur.BranchId) await RequireActiveBranchAsync(work, bid, ct);
            await ExecAsync(work, $"UPDATE {AP.Table} SET {AP.Source} = @src, {AP.BranchId} = @branch, {AP.MinAmount} = @min, {AP.RequiredApprovals} = @req, {AP.NameAr} = @ar, {AP.NameEn} = @en, {AC.JournalType} = @jt, {AC.RateOverride} = @ro, {AC.SoftClosed} = @sc, {CL.PolicyClosingKind} = @ck, {AP.Revision} = {AP.Revision} + 1, {AP.UpdatedAtUtc} = @now WHERE {AP.Id} = @id",
                p => { Bind(p, id, cur.DocType, source, input.BranchId, min, required, nameAr, nameEn, clock.GetUtcNow().UtcDateTime); BindConditions(p, np); });
            await InsertStagesAsync(work, id, cur.Revision + 1, np.Stages, ct);
            var changes = new JsonObject();
            Diff(changes, "source", cur.Source.ToString(), source.ToString()); Diff(changes, "branchId", cur.BranchId?.ToString(), input.BranchId?.ToString());
            Diff(changes, "minAmount", cur.MinAmount.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), min.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            Diff(changes, "requiredApprovals", cur.RequiredApprovals.ToString(), np.Total.ToString()); Diff(changes, "nameAr", cur.NameAr, nameAr); Diff(changes, "nameEn", cur.NameEn, nameEn);
            Diff(changes, "journalType", cur.JournalType ?? "any", JournalTypeName(np.JournalType)); Diff(changes, "rateOverride", cur.RateOverride?.ToString(), np.RateOverride?.ToString()); Diff(changes, "softClosed", cur.SoftClosed?.ToString(), np.SoftClosed?.ToString()); Diff(changes, "closingKind", cur.ClosingKind ?? "any", ClosingKindName(np.ClosingKind));
            Diff(changes, "stages", StagesText(curStages), StagesText(np.Stages));
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
            await InsertStagesAsync(work, id, cur.Revision + 1, cur.Stages ?? [], ct);   // the stage set follows every revision of the policy
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

    private static void BindConditions(NpgsqlParameterCollection p, NormalizedPolicy np)
    {
        p.Add(new NpgsqlParameter("jt", NpgsqlDbType.Smallint) { Value = (object?)np.JournalType ?? DBNull.Value });
        p.Add(new NpgsqlParameter("ro", NpgsqlDbType.Boolean) { Value = (object?)np.RateOverride ?? DBNull.Value });
        p.Add(new NpgsqlParameter("sc", NpgsqlDbType.Boolean) { Value = (object?)np.SoftClosed ?? DBNull.Value });
        p.Add(new NpgsqlParameter("ck", NpgsqlDbType.Smallint) { Value = (object?)np.ClosingKind ?? DBNull.Value });
    }

    private static string StagesText(IReadOnlyList<ApprovalStageView> stages) => string.Join(" > ", stages.Select(s => s.RoleId is null ? s.Required.ToString() : $"{s.Required}@{s.RoleId}"));

    private static async Task InsertStagesAsync(CompanyAuditedWork work, Guid policyId, long revision, IReadOnlyList<ApprovalStageView> stages, CancellationToken ct)
    {
        foreach (var s in stages)
        {
            await using var cmd = new NpgsqlCommand($"INSERT INTO {AS.Table} ({AS.PolicyId}, {AS.PolicyRevision}, {AS.StageNo}, {AS.Required}, {AS.RoleId}) VALUES (@p, @r, @n, @q, @role)", work.Connection, work.Transaction);
            cmd.Parameters.AddWithValue("p", policyId); cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.AddWithValue("n", (short)s.StageNo); cmd.Parameters.AddWithValue("q", (short)s.Required);
            cmd.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Uuid) { Value = (object?)s.RoleId ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
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

    /// <summary>
    /// The effective approval plan of a journal entry for its current revision with the progress of every stage: the governing policy right now raised to the plan the revision was
    /// submitted under (the same SQL function the database gate uses). Empty = no approval needed.
    /// </summary>
    public static async Task<IReadOnlyList<ApprovalStageProgress>> PlanAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid entryId, long revision, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT pl.stage_no, pl.required, pl.role_id, (SELECT count(DISTINCT d.{AD.Approver}) FROM {AD.Table} d WHERE d.{AD.DocType} = 'journal_entry' AND d.{AD.DocumentId} = @e AND d.{AD.DocumentRevision} = @r AND d.{AD.Decision} = 1 AND d.{AG.DecisionStage} = pl.stage_no) " +
            $"FROM {AG.PlanFunction}(@e) pl ORDER BY pl.stage_no", conn, tx);
        cmd.Parameters.AddWithValue("e", entryId); cmd.Parameters.AddWithValue("r", revision);
        var list = new List<ApprovalStageProgress>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ApprovalStageProgress(r.GetInt32(0), r.GetInt32(1), (int)r.GetInt64(3), r.IsDBNull(2) ? null : r.GetGuid(2)));
        return list;
    }

    /// <summary>Snapshots the effective plan under which this revision was submitted (an append-only floor: a later policy change can only add requirements).</summary>
    public static async Task BindAsync(CompanyAuditedWork work, Guid entryId, long revision, DateTime nowUtc, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {AB.Table} ({AB.DocType}, {AB.DocumentId}, {AB.DocumentRevision}, {AB.StageNo}, {AB.Required}, {AB.RoleId}, {AB.PolicyId}, {AB.PolicyRevision}, {AB.BoundAtUtc}) " +
            $"SELECT 'journal_entry', @e, @r, pl.stage_no, pl.required, pl.role_id, pk.policy_id, pk.policy_revision, @t FROM {AG.PlanFunction}(@e) pl LEFT JOIN {AG.PickFunction}(@e) pk ON true " +
            $"ON CONFLICT ({AB.DocType}, {AB.DocumentId}, {AB.DocumentRevision}, {AB.StageNo}) DO NOTHING", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("e", entryId); cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.AddWithValue("t", nowUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>True when nobody rejected the current revision and every stage of the effective plan is complete (the database gate uses the same function).</summary>
    public static async Task<bool> PlanSatisfiedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid entryId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {AG.SatisfiedFunction}(@e)", conn, tx);
        cmd.Parameters.AddWithValue("e", entryId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
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
            $"SELECT {AD.Id}, {AD.DocType}, {AD.DocumentId}, {AD.DocumentRevision}, {AD.Approver}, {AD.Decision}, {AD.DecidedAtUtc}, {AD.Comment}, {AG.DecisionStage} FROM {AD.Table} WHERE {AD.DocType} = @d AND {AD.DocumentId} = @i ORDER BY {AD.Id}", conn, tx);
        cmd.Parameters.AddWithValue("d", docType); cmd.Parameters.AddWithValue("i", documentId);
        var list = new List<ApprovalDecisionView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ApprovalDecisionView(r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetInt64(3), r.GetGuid(4), r.GetInt16(5) == 1, new DateTimeOffset(r.GetDateTime(6), TimeSpan.Zero), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt16(8)));
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
            $"INSERT INTO {AD.Table} ({AD.DocType}, {AD.DocumentId}, {AD.DocumentRevision}, {AD.Approver}, {AD.Decision}, {AD.DecidedAtUtc}, {AD.Comment}, {AD.AuditStream}, {AD.AuditSeq}, {AG.DecisionStage}) VALUES (@d, @i, @r, @a, @x, @t, @c, @s, @q, @st) RETURNING {AD.Id}",
            work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("d", request.DocType); ins.Parameters.AddWithValue("i", request.DocumentId); ins.Parameters.AddWithValue("r", request.DocumentRevision);
            ins.Parameters.AddWithValue("a", request.ApproverId); ins.Parameters.AddWithValue("x", (short)(request.Approve ? 1 : 2)); ins.Parameters.AddWithValue("t", nowUtc);
            ins.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Varchar) { Value = (object?)comment ?? DBNull.Value });
            ins.Parameters.AddWithValue("s", receipt.StreamKey); ins.Parameters.AddWithValue("q", receipt.Seq); ins.Parameters.AddWithValue("st", (short)request.Stage);
            id = (long)(await ins.ExecuteScalarAsync(ct))!;
        }
        return new(ApprovalRecordStatus.Recorded, new ApprovalDecisionView(id, request.DocType, request.DocumentId, request.DocumentRevision, request.ApproverId, request.Approve, new DateTimeOffset(nowUtc, TimeSpan.Zero), comment, request.Stage));
    }
}
