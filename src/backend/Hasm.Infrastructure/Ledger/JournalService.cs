using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using CU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Currency;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using JA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalAllocation;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using JV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEvent;
using LS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerSettings;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// The journal and the posting engine (Phase 3.2B, slice S5). <b>No authorization here by design:</b> <see cref="JournalManagementService"/> wraps it and decides, per
/// operation and per branch, what the caller may do. Every mutation runs in <see cref="CompanyAuditedTransactions"/>: the change, its event-log row and its authenticated
/// audit event commit or roll back together. <b>Posting is atomic:</b> the entry row is locked, the fiscal period is checked and share-locked through
/// <see cref="FiscalPeriodGate"/> inside the same transaction, the official number is allocated from the row-locked counter, and PostgreSQL re-validates everything
/// (balance, accounts, currencies, rates, cost centers, approvals for exactly this revision, period) in the trigger that performs the state change, so no caller can bypass it.
/// A failed validation rolls back the number, the audit event and the entry together.
/// </summary>
public sealed class JournalService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48434A45;   // "HCJE": advisory-lock namespace for journal operations that must be serialised company-wide (settings)
    internal const int LockKey2 = 1;

    internal sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
    }

    private sealed record Computed(
        Guid AccountId, string? Description, string Currency, decimal Debit, decimal Credit, decimal Rate, long? RateId, string? RateReason, decimal BaseDebit, decimal BaseCredit,
        Guid BranchId, bool IsRounding, Guid? ReversesLineId, List<(Guid CostCenterId, decimal Amount, decimal Base)> Allocations);

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<JournalEntryView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadViewAsync(conn, null, id, false, ct);
    }

    /// <summary>True when the entry is a year-end closing or opening entry or the reversal of one (the database function that defines the class, so the service and PostgreSQL always agree).</summary>
    public async Task<bool> IsYearEndAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {CompanySchema.ClosingLogic.KindFunction}(@id) IS NOT NULL", conn);
        q.Parameters.AddWithValue("id", id);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<JournalEntrySummary>> ListAsync(Guid companyId, JournalFilter filter, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {HeaderColumns} FROM {JE.Table} e WHERE (@st::smallint IS NULL OR e.{JE.Status} = @st) AND (@f::date IS NULL OR e.{JE.Date} >= @f) AND (@t::date IS NULL OR e.{JE.Date} <= @t) " +
            $"AND (@b::uuid IS NULL OR e.{JE.Branch} = @b) AND (@q::text IS NULL OR e.{JE.Number} ILIKE @q OR e.{JE.Desc} ILIKE @q) " +
            // Authorization is applied BEFORE ORDER/LIMIT/OFFSET (null @ab = company-wide, no restriction). An entry is visible through EITHER path (the same rule as opening it):
            // the branch path (the entry's branch and every line branch allowed) or the cost-center path (every line has allocations and all of them lie in the allowed cost centers, @ac = closure).
            $"AND (@ab::uuid[] IS NULL OR (e.{JE.Branch} = ANY(@ab) AND NOT EXISTS (SELECT 1 FROM {CompanySchema.JournalLine.Table} jl WHERE jl.{CompanySchema.JournalLine.Entry} = e.{JE.Id} AND jl.{CompanySchema.JournalLine.Branch} IS NOT NULL AND NOT (jl.{CompanySchema.JournalLine.Branch} = ANY(@ab)))) " +
            $"OR (@ac::uuid[] IS NOT NULL AND EXISTS (SELECT 1 FROM {CompanySchema.JournalLine.Table} jl WHERE jl.{CompanySchema.JournalLine.Entry} = e.{JE.Id}) AND NOT EXISTS (SELECT 1 FROM {CompanySchema.JournalLine.Table} jl WHERE jl.{CompanySchema.JournalLine.Entry} = e.{JE.Id} " +
            $"AND (NOT EXISTS (SELECT 1 FROM {CompanySchema.JournalAllocation.Table} ja WHERE ja.{CompanySchema.JournalAllocation.Line} = jl.{CompanySchema.JournalLine.Id}) OR EXISTS (SELECT 1 FROM {CompanySchema.JournalAllocation.Table} ja WHERE ja.{CompanySchema.JournalAllocation.Line} = jl.{CompanySchema.JournalLine.Id} AND NOT (ja.{CompanySchema.JournalAllocation.CC} = ANY(@ac))))))) " +
            $"ORDER BY e.{JE.Date} DESC, e.{JE.CreatedAt} DESC, e.{JE.Id} LIMIT @l OFFSET @o", conn);
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Smallint) { Value = filter.Status is null ? DBNull.Value : (short)filter.Status });
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = filter.From is null ? DBNull.Value : filter.From.Value.ToDateTime(TimeOnly.MinValue) });
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Date) { Value = filter.To is null ? DBNull.Value : filter.To.Value.ToDateTime(TimeOnly.MinValue) });
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)filter.BranchId ?? DBNull.Value });
        var q = filter.Text?.Trim();
        cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrEmpty(q) ? DBNull.Value : "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%" });
        cmd.Parameters.Add(new NpgsqlParameter("ab", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = filter.AllowedBranches is null ? DBNull.Value : filter.AllowedBranches.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("ac", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = filter.AllowedCostCenters is null ? DBNull.Value : filter.AllowedCostCenters.ToArray() });
        cmd.Parameters.AddWithValue("l", Math.Clamp(filter.Limit, 1, 500));
        cmd.Parameters.AddWithValue("o", Math.Max(0, filter.Offset));
        var list = new List<JournalEntrySummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadHeader(r));
        return list;
    }

    /// <summary>The given cost centers and all their descendants (what a cost-center-scoped grant covers).</summary>
    public async Task<IReadOnlyList<Guid>> CostCenterClosureAsync(Guid companyId, IReadOnlyCollection<Guid> roots, CancellationToken ct = default)
    {
        if (roots.Count == 0) return [];
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand(
            $"WITH RECURSIVE t(id) AS (SELECT c.{CompanySchema.CostCenter.Id} FROM {CompanySchema.CostCenter.Table} c WHERE c.{CompanySchema.CostCenter.Id} = ANY(@r) UNION SELECT c.{CompanySchema.CostCenter.Id} FROM {CompanySchema.CostCenter.Table} c JOIN t ON c.{CompanySchema.CostCenter.ParentId} = t.id) SELECT id FROM t", conn);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = roots.ToArray() });
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    /// <summary>The cost centers a user may allocate to: every ACTIVE LEAF (company-wide or branch access) or the active leaves inside the granted cost-center subtrees.</summary>
    public async Task<IReadOnlyList<CostCenterView>> AllocatableCostCentersAsync(Guid companyId, IReadOnlyCollection<Guid>? restrictToClosure, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var all = await CostCenterService.LoadAsync(conn, null, null, false, ct);
        return [.. all.Where(c => c.CanReceiveAllocations && (restrictToClosure is null || restrictToClosure.Contains(c.Id)))];
    }

    private static readonly string HeaderColumns =
        $"e.{JE.Id}, e.{JE.Number}, e.{JE.Status}, e.{JE.Type}, e.{JE.Source}, e.{JE.SourceKey}, e.{JE.Date}, e.{JE.Desc}, e.{JE.Branch}, e.{JE.BaseCur}, e.{JE.BaseTotal}, e.{JE.Rev}, " +
        $"e.{JE.CreatedBy}, e.{JE.CreatedAt}, e.{JE.UpdatedAt}, e.{JE.SubmittedBy}, e.{JE.SubmittedAt}, e.{JE.PostedBy}, e.{JE.PostedAt}, e.{JE.Period}, e.{JE.SoftPost}, e.{JE.Reverses}, e.{JE.Adjusts}";

    private static DateTimeOffset Utc(NpgsqlDataReader r, int i) => new(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));

    private static JournalEntrySummary ReadHeader(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), (JournalStatus)r.GetInt16(2), (JournalType)r.GetInt16(3), (DocumentSource)r.GetInt16(4), r.IsDBNull(5) ? null : r.GetString(5),
        DateOnly.FromDateTime(r.GetDateTime(6)), r.IsDBNull(7) ? null : r.GetString(7), r.GetGuid(8), r.GetString(9).Trim(), r.GetDecimal(10), r.GetInt64(11), r.GetGuid(12), Utc(r, 13), Utc(r, 14),
        r.IsDBNull(15) ? null : r.GetGuid(15), r.IsDBNull(16) ? null : Utc(r, 16), r.IsDBNull(17) ? null : r.GetGuid(17), r.IsDBNull(18) ? null : Utc(r, 18),
        r.IsDBNull(19) ? null : r.GetGuid(19), r.GetBoolean(20), r.IsDBNull(21) ? null : r.GetGuid(21), r.IsDBNull(22) ? null : r.GetGuid(22));

    internal static async Task<JournalEntrySummary?> LoadHeaderAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {HeaderColumns} FROM {JE.Table} e WHERE e.{JE.Id} = @id{(forUpdate ? " FOR UPDATE OF e" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHeader(r) : null;
    }

    internal static async Task<JournalEntryView?> LoadViewAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        var h = await LoadHeaderAsync(conn, tx, id, forUpdate, ct);
        if (h is null) return null;
        var lines = new List<JournalLineView>();
        var allocations = new Dictionary<Guid, List<JournalAllocationView>>();
        await using (var a = new NpgsqlCommand($"SELECT a.{JA.Line}, a.{JA.CC}, a.{JA.Amount}, a.{JA.Base} FROM {JA.Table} a JOIN {JL.Table} l ON l.{JL.Id} = a.{JA.Line} WHERE l.{JL.Entry} = @id ORDER BY a.{JA.Id}", conn, tx))
        {
            a.Parameters.AddWithValue("id", id);
            await using var r = await a.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!allocations.TryGetValue(r.GetGuid(0), out var list)) allocations[r.GetGuid(0)] = list = [];
                list.Add(new JournalAllocationView(r.GetGuid(1), r.GetDecimal(2), r.GetDecimal(3)));
            }
        }
        await using (var l = new NpgsqlCommand(
            $"SELECT {JL.Id}, {JL.No}, {JL.Account}, {JL.Desc}, {JL.Cur}, {JL.Debit}, {JL.Credit}, {JL.Rate}, {JL.RateId}, {JL.RateReason}, {JL.BaseDebit}, {JL.BaseCredit}, {JL.Branch}, {JL.IsRounding}, {JL.ReversesLine} FROM {JL.Table} WHERE {JL.Entry} = @id ORDER BY {JL.No}", conn, tx))
        {
            l.Parameters.AddWithValue("id", id);
            await using var r = await l.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(new JournalLineView(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4).Trim(), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                    r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetDecimal(10), r.GetDecimal(11), r.GetGuid(12), r.GetBoolean(13), r.IsDBNull(14) ? null : r.GetGuid(14), []));
        }
        lines = [.. lines.Select(x => allocations.TryGetValue(x.Id, out var al) ? x with { Allocations = al } : x)];
        var deb = lines.Sum(x => x.BaseDebit);
        var cre = lines.Sum(x => x.BaseCredit);
        var plan = await DocumentApprovals.PlanAsync(conn, tx, id, h.Revision, ct);
        var required = plan.Sum(p => p.Required);
        var approvals = await DocumentApprovals.ListAsync(conn, tx, DocumentTypes.JournalEntry, id, ct);
        var events = new List<JournalEventView>();
        await using (var ev = new NpgsqlCommand($"SELECT {JV.Id}, {JV.Event}, {JV.Actor}, {JV.At}, {JV.Rev}, {JV.Detail} FROM {JV.Table} WHERE {JV.Entry} = @id ORDER BY {JV.Id}", conn, tx))
        {
            ev.Parameters.AddWithValue("id", id);
            await using var r = await ev.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) events.Add(new JournalEventView(r.GetInt64(0), (JournalEventKind)r.GetInt16(1), r.GetGuid(2), Utc(r, 3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return new JournalEntryView(h, lines.Count >= 2 && deb == cre && deb > 0, deb, cre, required, lines, approvals, events, plan);
    }

    // ------------------------------------------------------------------------------------------------------------- create and edit drafts

    public Task<ConfigResult<JournalEntryView>> CreateDraftAsync(Guid actor, Guid companyId, JournalInput? input, bool mayOverrideRate, CancellationToken ct = default) =>
        CreateAsync(actor, companyId, input, mayOverrideRate, DocumentSource.Manual, null, ct);

    /// <summary>
    /// An automatic entry from a business event: carries the initiating user and a unique source key. A second call with the same key returns the EXISTING entry
    /// (idempotent: a retried event never creates a second entry). It is an ordinary draft: submission, approval policies and posting rules apply unchanged, so
    /// automatic posting can never bypass them; when approval is missing it simply stays a pending draft.
    /// </summary>
    public async Task<ConfigResult<JournalEntryView>> CreateAutomaticAsync(Guid actor, Guid companyId, AutomaticJournalInput? input, bool mayOverrideRate, CancellationToken ct = default)
    {
        var key = JournalValidation.NormalizeSourceKey(input?.SourceKey);
        if (key is null) return ConfigResult<JournalEntryView>.Invalid([new("sourceKey", "required")]);
        await using (var conn = await resolver.OpenAsync(companyId, ct))
        await using (var q = new NpgsqlCommand($"SELECT {JE.Id} FROM {JE.Table} WHERE {JE.SourceKey} = @k AND {JE.Status} <> 6", conn))
        {
            q.Parameters.AddWithValue("k", key);
            if (await q.ExecuteScalarAsync(ct) is Guid existing) return ConfigResult<JournalEntryView>.Ok((await LoadViewAsync(conn, null, existing, false, ct))!);
        }
        var r = await CreateAsync(actor, companyId, input!.Entry, mayOverrideRate, DocumentSource.Automatic, key, ct);
        if (r.Code != "duplicate_source") return r;
        return ConfigResult<JournalEntryView>.Ok((await FindBySourceKeyAsync(companyId, key, ct))!);   // lost a race with an identical retry: return the winner
    }

    private async Task<JournalEntryView?> FindBySourceKeyAsync(Guid companyId, string key, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {JE.Id} FROM {JE.Table} WHERE {JE.SourceKey} = @k AND {JE.Status} <> 6", conn);
        q.Parameters.AddWithValue("k", key);
        return await q.ExecuteScalarAsync(ct) is Guid id ? await LoadViewAsync(conn, null, id, false, ct) : null;
    }

    private async Task<ConfigResult<JournalEntryView>> CreateAsync(Guid actor, Guid companyId, JournalInput? input, bool mayOverrideRate, DocumentSource source, string? sourceKey, CancellationToken ct)
    {
        if (input is null) return ConfigResult<JournalEntryView>.Invalid([new("body", "required")]);
        return await MutateAsync(companyId, work => CreateInWorkAsync(work, actor, input, mayOverrideRate, source, sourceKey, ct), ct);
    }

    /// <summary>Creates a draft inside the caller's audited transaction (used by year-end processing so the entry and its closing record commit together). Throws <see cref="Abort"/> on a refusal.</summary>
    internal async Task<JournalEntryView> CreateInWorkAsync(CompanyAuditedWork work, Guid actor, JournalInput input, bool mayOverrideRate, DocumentSource source, string? sourceKey, CancellationToken ct, bool yearEnd = false)
    {
        var ctx = await PrepareHeaderAsync(work, input, ct, yearEnd ? int.MaxValue : JournalValidation.MaxLines);
        var type = input.AdjustsId is null ? JournalType.Normal : JournalType.Adjusting;
        if (input.AdjustsId is { } adj)
        {
            var target = await LoadHeaderAsync(work.Connection, work.Transaction, adj, false, ct);
            if (target is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("adjustsId", "entry_not_found")]);
            if (target.Status is not (JournalStatus.Posted or JournalStatus.Reversed)) throw new Abort(ConfigStatus.Conflict, "adjusted_entry_not_posted");
        }
        var lines = await ComputeLinesAsync(work, ctx, input.Lines, input.EntryDate!.Value, mayOverrideRate, ct, yearEnd);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow().UtcDateTime;
        var baseTotal = lines.Sum(l => l.BaseDebit);
        try
        {
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {JE.Table} ({JE.Id}, {JE.Status}, {JE.Type}, {JE.Source}, {JE.SourceKey}, {JE.Date}, {JE.Desc}, {JE.Branch}, {JE.BaseCur}, {JE.BaseTotal}, {JE.Rev}, {JE.CreatedBy}, {JE.CreatedAt}, {JE.UpdatedAt}, {JE.Adjusts}) " +
                "VALUES (@id, 1, @type, @src, @key, @date, @desc, @branch, @cur, @total, 1, @actor, @now, @now, @adj)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("type", (short)type); ins.Parameters.AddWithValue("src", (short)source);
            ins.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = (object?)sourceKey ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = input.EntryDate.Value.ToDateTime(TimeOnly.MinValue) });
            ins.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Varchar) { Value = (object?)ctx.Description ?? DBNull.Value });
            ins.Parameters.AddWithValue("branch", ctx.BranchId); ins.Parameters.AddWithValue("cur", ctx.BaseCurrency); ins.Parameters.AddWithValue("total", baseTotal);
            ins.Parameters.AddWithValue("actor", actor); ins.Parameters.AddWithValue("now", now);
            ins.Parameters.Add(new NpgsqlParameter("adj", NpgsqlDbType.Uuid) { Value = (object?)input.AdjustsId ?? DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new Abort(ConfigStatus.Conflict, "duplicate_source"); }
        await WriteLinesAsync(work, id, lines, ct);
        await RecordAsync(work, type == JournalType.Adjusting ? LedgerAuditEvents.JournalAdjustingDrafted : LedgerAuditEvents.JournalCreated, actor, id,
            type == JournalType.Adjusting ? JournalEventKind.AdjustingDrafted : JournalEventKind.Created, 1, $"{lines.Count} lines", ct);
        return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
    }

    public async Task<ConfigResult<JournalEntryView>> UpdateDraftAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, JournalInput? input, bool mayOverrideRate, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<JournalEntryView>.Invalid([new("body", "required")]);
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status != JournalStatus.Draft) throw new Abort(ConfigStatus.Conflict, "entry_not_draft");
            if (cur.Type == JournalType.Reversal) throw new Abort(ConfigStatus.Conflict, "reversal_not_editable");   // a reversal mirrors its original exactly
            var ctx = await PrepareHeaderAsync(work, input, ct);
            var lines = await ComputeLinesAsync(work, ctx, input.Lines, input.EntryDate!.Value, mayOverrideRate, ct);
            var rev = cur.Revision + 1;
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {JE.Table} SET {JE.Date} = @date, {JE.Desc} = @desc, {JE.Branch} = @branch, {JE.BaseTotal} = @total, {JE.Rev} = @rev, {JE.UpdatedAt} = @now WHERE {JE.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("rev", rev); upd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                upd.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = input.EntryDate!.Value.ToDateTime(TimeOnly.MinValue) });
                upd.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Varchar) { Value = (object?)ctx.Description ?? DBNull.Value });
                upd.Parameters.AddWithValue("branch", ctx.BranchId); upd.Parameters.AddWithValue("total", lines.Sum(l => l.BaseDebit));
                await upd.ExecuteNonQueryAsync(ct);
            }
            await WriteLinesAsync(work, id, lines, ct);
            await RecordAsync(work, LedgerAuditEvents.JournalUpdated, actor, id, JournalEventKind.Updated, rev, $"{lines.Count} lines", ct);
            return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- workflow

    /// <summary>Submits a draft: with no matching approval policy it becomes Approved at once; otherwise Pending approval. The database refuses an entry that is not balanced or valid.</summary>
    public async Task<ConfigResult<JournalEntryView>> SubmitAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status != JournalStatus.Draft) throw new Abort(ConfigStatus.Conflict, "entry_not_draft");
            // The plan comes from the SAME SQL function the database gate uses (risk conditions, stages); what this revision is submitted under is snapshotted as a floor.
            var plan = await DocumentApprovals.PlanAsync(work.Connection, work.Transaction, id, cur.Revision, ct);
            var required = plan.Sum(p => p.Required);
            var next = required == 0 ? JournalStatus.Approved : JournalStatus.PendingApproval;
            var now = clock.GetUtcNow().UtcDateTime;
            if (required > 0) await DocumentApprovals.BindAsync(work, id, cur.Revision, now, ct);
            await SetStatusAsync(work, id, next, $", {JE.SubmittedBy} = @actor, {JE.SubmittedAt} = @now", p => { p.AddWithValue("actor", actor); p.AddWithValue("now", now); }, ct);
            await RecordAsync(work, LedgerAuditEvents.JournalSubmitted, actor, id, JournalEventKind.Submitted, cur.Revision, required == 0 ? "approved: no approval required" : $"approvals required: {required}" + (plan.Count > 1 ? $" in {plan.Count} ordered stages" : ""), ct);
            return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>Returns a Pending or Approved entry to Draft. The revision advances, so every earlier approval stops counting.</summary>
    public async Task<ConfigResult<JournalEntryView>> WithdrawAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status is not (JournalStatus.PendingApproval or JournalStatus.Approved)) throw new Abort(ConfigStatus.Conflict, "entry_not_submitted");
            await ReturnToDraftAsync(work, cur, ct);
            await RecordAsync(work, LedgerAuditEvents.JournalReturnedToDraft, actor, id, JournalEventKind.ReturnedToDraft, cur.Revision + 1, "withdrawn", ct);
            return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>Cancels a Draft, Pending or Approved entry. Nothing is deleted; a cancelled entry keeps its history and frees its source key.</summary>
    public async Task<ConfigResult<JournalEntryView>> CancelAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status is not (JournalStatus.Draft or JournalStatus.PendingApproval or JournalStatus.Approved)) throw new Abort(ConfigStatus.Conflict, "entry_not_cancellable");
            await SetStatusAsync(work, id, JournalStatus.Cancelled, "", _ => { }, ct);
            await RecordAsync(work, LedgerAuditEvents.JournalCancelled, actor, id, JournalEventKind.Cancelled, cur.Revision, null, ct);
            return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    /// <summary>
    /// Records an approval or rejection of exactly the current revision. The caller has already been authorized for this entry's branch; this method enforces separation of duties
    /// (never the creator or submitter) and one decision per approver per revision. An approval that completes the requirement moves the entry to Approved; a rejection returns
    /// it to Draft (new revision, so the rejected content can never be approved by old decisions).
    /// </summary>
    public async Task<ConfigResult<JournalEntryView>> DecideAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, ApprovalDecisionInput? input, CancellationToken ct = default, IReadOnlySet<Guid>? approverRoles = null)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input?.Approve is null) return ConfigResult<JournalEntryView>.Invalid([new("approve", "required")]);
        var approve = input.Approve.Value;
        if (!approve && string.IsNullOrWhiteSpace(input.Comment)) return ConfigResult<JournalEntryView>.Invalid([new("comment", "required")]);
        if (input.Comment is { Length: > 500 }) return ConfigResult<JournalEntryView>.Invalid([new("comment", "too_long")]);
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status != JournalStatus.PendingApproval) throw new Abort(ConfigStatus.Conflict, "entry_not_pending_approval");
            // The decision is cast for the FIRST stage that is not complete (stages are strictly ordered); a stage bound to a role takes decisions only from holders of that role
            // (the caller supplies the roles the approver holds right now, read from the authorization store on this very call).
            var plan = await DocumentApprovals.PlanAsync(work.Connection, work.Transaction, id, cur.Revision, ct);
            var open = plan.FirstOrDefault(p => p.Approved < p.Required);
            if (open is null && plan.Count > 0) throw new Abort(ConfigStatus.Conflict, "approval_stage_complete");
            if (actor != cur.CreatedBy && actor != cur.SubmittedBy && open?.RoleId is { } stageRole && (approverRoles is null || !approverRoles.Contains(stageRole))) throw new Abort(ConfigStatus.Forbidden, "approver_role_required");
            var result = await DocumentApprovals.RecordAsync(work, new ApprovalDecisionRequest(DocumentTypes.JournalEntry, id, cur.Revision, actor, cur.CreatedBy, cur.SubmittedBy, approve, input.Comment, open?.Stage ?? 1), clock.GetUtcNow().UtcDateTime, ct);
            switch (result.Status)
            {
                case ApprovalRecordStatus.SeparationOfDuties: throw new Abort(ConfigStatus.Forbidden, "separation_of_duties");
                case ApprovalRecordStatus.AlreadyDecided: throw new Abort(ConfigStatus.Conflict, "already_decided");
                case ApprovalRecordStatus.CommentRequired: throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("comment", "required")]);
            }
            await AddEventAsync(work, id, JournalEventKind.DecisionRecorded, actor, cur.Revision, approve ? "approve" : "reject", ct, auditAlreadyWritten: true);
            if (!approve)
            {
                await ReturnToDraftAsync(work, cur, ct);
                await AddEventAsync(work, id, JournalEventKind.Rejected, actor, cur.Revision + 1, input.Comment!.Trim(), ct, auditAlreadyWritten: false, auditType: LedgerAuditEvents.JournalRejected);
            }
            else
            {
                if (await DocumentApprovals.PlanSatisfiedAsync(work.Connection, work.Transaction, id, ct))
                {
                    await SetStatusAsync(work, id, JournalStatus.Approved, "", _ => { }, ct);
                    await AddEventAsync(work, id, JournalEventKind.Approved, actor, cur.Revision, "required approvals complete", ct, auditAlreadyWritten: false, auditType: LedgerAuditEvents.JournalApproved);
                }
            }
            return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- posting

    /// <summary>
    /// Posts an Approved entry atomically. <paramref name="allowSoftClosed"/> is true only when the CALLER holds the separate soft-closed posting permission. Posting an entry that
    /// is already posted returns it unchanged (idempotent retry; never a second number or audit event).
    /// </summary>
    public async Task<ConfigResult<JournalEntryView>> PostAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool allowSoftClosed, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status is JournalStatus.Posted or JournalStatus.Reversed) throw new Abort(ConfigStatus.Ok, "unchanged");   // idempotent retry
            if (cur.Status != JournalStatus.Approved) throw new Abort(ConfigStatus.Conflict, cur.Status == JournalStatus.Cancelled ? "entry_cancelled" : "entry_not_approved");
            var check = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, cur.EntryDate, allowSoftClosed, ct);   // locks the period row FOR SHARE until commit
            if (!check.Allowed) throw new Abort(ConfigStatus.Conflict, check.Reason!);
            string branchCode; int yearLabel; Guid yearId;
            await using (var q = new NpgsqlCommand(
                $"SELECT (SELECT {BR.Code} FROM {BR.Table} WHERE {BR.Id} = @b), EXTRACT(YEAR FROM y.{FY.StartDate})::int, y.{FY.Id} FROM {FP.Table} p JOIN {FY.Table} y ON y.{FY.Id} = p.{FP.YearId} WHERE p.{FP.Id} = @p", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("b", cur.BranchId); q.Parameters.AddWithValue("p", check.PeriodId!.Value);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                branchCode = r.GetString(0); yearLabel = r.GetInt32(1); yearId = r.GetGuid(2);
            }
            var now = clock.GetUtcNow().UtcDateTime;
            var number = await NumberingService.IssueAsync(work.Connection, work.Transaction, DocumentTypes.JournalEntry,
                new NumberScope(cur.BranchId, branchCode, yearId, yearLabel.ToString()), id, actor, now, ct);
            await using (var upd = new NpgsqlCommand(
                $"UPDATE {JE.Table} SET {JE.Status} = 4, {JE.Number} = @n, {JE.PostedBy} = @a, {JE.PostedAt} = @t, {JE.Period} = @p, {JE.SoftPost} = @s, {JE.UpdatedAt} = @t WHERE {JE.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("n", number.Number); upd.Parameters.AddWithValue("a", actor); upd.Parameters.AddWithValue("t", now);
                upd.Parameters.AddWithValue("p", check.PeriodId!.Value); upd.Parameters.AddWithValue("s", check.State == FiscalPeriodState.SoftClosed);
                await upd.ExecuteNonQueryAsync(ct);
            }
            if (cur.Type == JournalType.Reversal)
            {
                await using var rv = new NpgsqlCommand($"UPDATE {JE.Table} SET {JE.Status} = 5, {JE.UpdatedAt} = @t WHERE {JE.Id} = @o AND {JE.Status} = 4", work.Connection, work.Transaction);
                rv.Parameters.AddWithValue("o", cur.ReversesId!.Value); rv.Parameters.AddWithValue("t", now);
                if (await rv.ExecuteNonQueryAsync(ct) != 1) throw new Abort(ConfigStatus.Conflict, "original_not_reversible");
            }
            await RecordAsync(work, LedgerAuditEvents.JournalPosted, actor, id, JournalEventKind.Posted, cur.Revision, number.Number, ct);
            if (cur.Type == JournalType.Reversal)
                await AddEventAsync(work, cur.ReversesId!.Value, JournalEventKind.Reversed, actor, (await LoadHeaderAsync(work.Connection, work.Transaction, cur.ReversesId.Value, false, ct))!.Revision, number.Number, ct, auditAlreadyWritten: true);
            return (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    /// <summary>Drafts the reversal of a posted entry: a new entry with the sides swapped, the same accounts, currencies, rates and allocations, referencing the original. It then follows the normal workflow.</summary>
    public async Task<ConfigResult<JournalEntryView>> ReverseAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, ReversalInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input?.EntryDate is null) return ConfigResult<JournalEntryView>.Invalid([new("entryDate", "required")]);
        var errors = new List<FieldError>();
        var description = JournalValidation.CleanText(input.Description, 500, "description", errors);
        if (errors.Count > 0) return ConfigResult<JournalEntryView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            var cur = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (cur.Status != JournalStatus.Posted || cur.Type == JournalType.Reversal) throw new Abort(ConfigStatus.Conflict, "entry_not_reversible");
            await using (var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {JE.Table} WHERE {JE.Reverses} = @id AND {JE.Status} <> 6)", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("id", id);
                if ((bool)(await q.ExecuteScalarAsync(ct))!) throw new Abort(ConfigStatus.Conflict, "already_reversed");
            }
            var original = (await LoadViewAsync(work.Connection, work.Transaction, id, false, ct))!;
            var rid = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var ins = new NpgsqlCommand(
                $"INSERT INTO {JE.Table} ({JE.Id}, {JE.Status}, {JE.Type}, {JE.Source}, {JE.Date}, {JE.Desc}, {JE.Branch}, {JE.BaseCur}, {JE.BaseTotal}, {JE.Rev}, {JE.CreatedBy}, {JE.CreatedAt}, {JE.UpdatedAt}, {JE.Reverses}) " +
                "VALUES (@id, 1, 2, 1, @date, @desc, @branch, @cur, @total, 1, @actor, @now, @now, @orig)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", rid);
                ins.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = input.EntryDate.Value.ToDateTime(TimeOnly.MinValue) });
                ins.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Varchar) { Value = (object?)description ?? $"Reversal of {original.Header.Number}" });
                ins.Parameters.AddWithValue("branch", original.Header.BranchId); ins.Parameters.AddWithValue("cur", original.Header.BaseCurrency);
                ins.Parameters.AddWithValue("total", original.Header.BaseTotal); ins.Parameters.AddWithValue("actor", actor); ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("orig", id);
                await ins.ExecuteNonQueryAsync(ct);
            }
            var no = 0;
            foreach (var l in original.Lines)
            {
                var lid = Guid.NewGuid();
                await using var li = new NpgsqlCommand(
                    $"INSERT INTO {JL.Table} ({JL.Id}, {JL.Entry}, {JL.No}, {JL.Account}, {JL.Desc}, {JL.Cur}, {JL.Debit}, {JL.Credit}, {JL.Rate}, {JL.RateId}, {JL.RateReason}, {JL.BaseDebit}, {JL.BaseCredit}, {JL.Branch}, {JL.IsRounding}, {JL.ReversesLine}) " +
                    "VALUES (@id, @e, @no, @acc, @desc, @cur, @deb, @cre, @rate, @rid, @rr, @bd, @bc, @br, @rnd, @rl)", work.Connection, work.Transaction);
                li.Parameters.AddWithValue("id", lid); li.Parameters.AddWithValue("e", rid); li.Parameters.AddWithValue("no", ++no); li.Parameters.AddWithValue("acc", l.AccountId);
                li.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Varchar) { Value = (object?)l.Description ?? DBNull.Value });
                li.Parameters.AddWithValue("cur", l.Currency); li.Parameters.AddWithValue("deb", l.Credit); li.Parameters.AddWithValue("cre", l.Debit); li.Parameters.AddWithValue("rate", l.Rate);
                li.Parameters.Add(new NpgsqlParameter("rid", NpgsqlDbType.Bigint) { Value = (object?)l.RateId ?? DBNull.Value });
                li.Parameters.Add(new NpgsqlParameter("rr", NpgsqlDbType.Varchar) { Value = (object?)l.RateOverrideReason ?? DBNull.Value });
                li.Parameters.AddWithValue("bd", l.BaseCredit); li.Parameters.AddWithValue("bc", l.BaseDebit); li.Parameters.AddWithValue("br", l.BranchId); li.Parameters.AddWithValue("rnd", l.IsRounding); li.Parameters.AddWithValue("rl", l.Id);
                await li.ExecuteNonQueryAsync(ct);
                foreach (var a in l.Allocations)
                {
                    await using var ai = new NpgsqlCommand($"INSERT INTO {JA.Table} ({JA.Line}, {JA.CC}, {JA.Amount}, {JA.Base}) VALUES (@l, @c, @a, @b)", work.Connection, work.Transaction);
                    ai.Parameters.AddWithValue("l", lid); ai.Parameters.AddWithValue("c", a.CostCenterId); ai.Parameters.AddWithValue("a", a.Amount); ai.Parameters.AddWithValue("b", a.BaseAmount);
                    await ai.ExecuteNonQueryAsync(ct);
                }
            }
            await RecordAsync(work, LedgerAuditEvents.JournalReversalDrafted, actor, rid, JournalEventKind.ReversalDrafted, 1, $"reverses {original.Header.Number}", ct);
            return (await LoadViewAsync(work.Connection, work.Transaction, rid, false, ct))!;
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- preparation and computation

    private sealed record HeaderContext(Guid BranchId, string BaseCurrency, int BaseMinor, string? Description, Dictionary<string, int?> Currencies, Guid? RoundingAccountId);

    private async Task<HeaderContext> PrepareHeaderAsync(CompanyAuditedWork work, JournalInput input, CancellationToken ct, int maxLines = JournalValidation.MaxLines)
    {
        var errors = new List<FieldError>();
        if (input.EntryDate is null) errors.Add(new("entryDate", "required"));
        else if (input.EntryDate < new DateOnly(2000, 1, 1) || input.EntryDate > new DateOnly(2100, 12, 31)) errors.Add(new("entryDate", "out_of_range"));
        var description = JournalValidation.CleanText(input.Description, 500, "description", errors);
        if (input.BranchId is null) errors.Add(new("branchId", "required"));
        if (input.Lines is null || input.Lines.Count == 0) errors.Add(new("lines", "required"));
        else if (input.Lines.Count > maxLines) errors.Add(new("lines", "too_many"));
        if (errors.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: errors);

        var currencies = new Dictionary<string, int?>();
        string? baseCode = null; int? baseMinor = null;
        await using (var c = new NpgsqlCommand($"SELECT cc.{CC.Code}, cu.{CU.MinorUnits}, cc.{CC.IsBase} FROM {CC.Table} cc JOIN {CU.Table} cu ON cu.{CU.Code} = cc.{CC.Code} WHERE cc.{CC.Enabled}", work.Connection, work.Transaction))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var code = r.GetString(0).Trim();
                int? minor = r.IsDBNull(1) ? null : r.GetInt16(1);
                currencies[code] = minor;
                if (r.GetBoolean(2)) { baseCode = code; baseMinor = minor; }
            }
        if (baseCode is null) throw new Abort(ConfigStatus.Conflict, "base_currency_not_selected");
        if (baseMinor is null) throw new Abort(ConfigStatus.Conflict, "base_currency_minor_units_undefined");
        await using (var b = new NpgsqlCommand($"SELECT {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = @id", work.Connection, work.Transaction))
        {
            b.Parameters.AddWithValue("id", input.BranchId!.Value);
            var v = await b.ExecuteScalarAsync(ct);
            if (v is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_not_found")]);
            if (!(bool)v) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("branchId", "branch_inactive")]);
        }
        Guid? rounding = null;
        await using (var s = new NpgsqlCommand($"SELECT {LS.Account} FROM {LS.Table}", work.Connection, work.Transaction))
            if (await s.ExecuteScalarAsync(ct) is Guid g) rounding = g;
        return new HeaderContext(input.BranchId!.Value, baseCode, baseMinor.Value, description, currencies, rounding);
    }

    private async Task<List<Computed>> ComputeLinesAsync(CompanyAuditedWork work, HeaderContext ctx, IReadOnlyList<JournalLineInput>? inputs, DateOnly date, bool mayOverrideRate, CancellationToken ct, bool historicalCostCenters = false)
    {
        var errors = new List<FieldError>();
        inputs ??= [];
        var accountIds = inputs.Where(l => l.AccountId is not null).Select(l => l.AccountId!.Value).Distinct().ToArray();
        var accounts = new Dictionary<Guid, (bool Active, bool Group, short Policy, string? Currency)>();
        if (accountIds.Length > 0)
        {
            await using var a = new NpgsqlCommand($"SELECT {AC.Id}, {AC.IsActive}, {AC.IsGroup}, {AC.CurrencyPolicy}, {AC.CurrencyCode} FROM {AC.Table} WHERE {AC.Id} = ANY(@ids)", work.Connection, work.Transaction);
            a.Parameters.AddWithValue("ids", accountIds);
            await using var r = await a.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) accounts[r.GetGuid(0)] = (r.GetBoolean(1), r.GetBoolean(2), r.GetInt16(3), r.IsDBNull(4) ? null : r.GetString(4).Trim());
        }
        var branchIds = inputs.Where(l => l.BranchId is not null).Select(l => l.BranchId!.Value).Distinct().ToArray();
        var branches = new Dictionary<Guid, bool>();
        if (branchIds.Length > 0)
        {
            await using var b = new NpgsqlCommand($"SELECT {BR.Id}, {BR.IsActive} FROM {BR.Table} WHERE {BR.Id} = ANY(@ids)", work.Connection, work.Transaction);
            b.Parameters.AddWithValue("ids", branchIds);
            await using var r = await b.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) branches[r.GetGuid(0)] = r.GetBoolean(1);
        }
        var ccIds = inputs.SelectMany(l => l.Allocations ?? []).Where(a => a.CostCenterId is not null).Select(a => a.CostCenterId!.Value).Distinct().ToArray();
        var centers = new Dictionary<Guid, (bool Group, bool Active)>();
        if (ccIds.Length > 0)
        {
            await using var c = new NpgsqlCommand($"SELECT {CO.Id}, {CO.IsGroup}, {CO.IsActive} FROM {CO.Table} WHERE {CO.Id} = ANY(@ids)", work.Connection, work.Transaction);
            c.Parameters.AddWithValue("ids", ccIds);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) centers[r.GetGuid(0)] = (r.GetBoolean(1), r.GetBoolean(2));
        }

        var result = new List<Computed>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var l = inputs[i];
            string F(string name) => $"lines[{i}].{name}";
            var before = errors.Count;
            if (l.AccountId is null) errors.Add(new(F("accountId"), "required"));
            else if (!accounts.TryGetValue(l.AccountId.Value, out var acc)) errors.Add(new(F("accountId"), "account_not_found"));
            else if (acc.Group || !acc.Active) errors.Add(new(F("accountId"), "account_not_postable"));
            var currency = string.IsNullOrWhiteSpace(l.Currency) ? ctx.BaseCurrency : l.Currency.Trim().ToUpperInvariant();
            int minor = ctx.BaseMinor;
            if (!ctx.Currencies.TryGetValue(currency, out var cm)) errors.Add(new(F("currency"), "currency_not_enabled"));
            else if (cm is null) errors.Add(new(F("currency"), "currency_minor_units_undefined"));
            else minor = cm.Value;
            if (l.AccountId is { } aid && accounts.TryGetValue(aid, out var ac2))
            {
                if (ac2.Policy == 1 && currency != ctx.BaseCurrency) errors.Add(new(F("currency"), "currency_policy_violation"));
                if (ac2.Policy == 3 && currency != ac2.Currency) errors.Add(new(F("currency"), "currency_policy_violation"));
            }
            var deb = l.Debit ?? 0m; var cre = l.Credit ?? 0m;
            if (deb < 0 || cre < 0) errors.Add(new(F("debit"), "must_not_be_negative"));
            else if ((deb > 0) == (cre > 0)) errors.Add(new(F(deb > 0 ? "credit" : "debit"), deb > 0 ? "debit_and_credit" : "amount_required"));
            else if (deb + cre > 999_999_999_999_999m) errors.Add(new(F("debit"), "out_of_range"));
            else if (deb + cre != decimal.Round(deb + cre, minor)) errors.Add(new(F(deb > 0 ? "debit" : "credit"), "too_many_decimals"));
            var branch = l.BranchId ?? ctx.BranchId;
            if (l.BranchId is not null && !branches.TryGetValue(branch, out var bActive)) errors.Add(new(F("branchId"), "branch_not_found"));
            else if (l.BranchId is not null && !branches[branch]) errors.Add(new(F("branchId"), "branch_inactive"));
            var amount = deb + cre;
            if (errors.Count > before) continue;

            decimal rate = 1m; long? rateId = null; string? reason = null;
            if (currency != ctx.BaseCurrency)
            {
                if (l.RateOverride is not null)
                {
                    if (!mayOverrideRate) throw new Abort(ConfigStatus.Forbidden, "rate_override_forbidden");
                    var why = l.RateOverrideReason?.Trim();
                    if (string.IsNullOrEmpty(why) || why.Length < 5) errors.Add(new(F("rateOverrideReason"), "required"));
                    else if (why.Length > 200) errors.Add(new(F("rateOverrideReason"), "too_long"));
                    if (l.RateOverride <= 0 || l.RateOverride > CurrencyMath.MaxRate) errors.Add(new(F("rateOverride"), "out_of_range"));
                    else if (l.RateOverride.Value != decimal.Round(l.RateOverride.Value, CurrencyMath.RateScale)) errors.Add(new(F("rateOverride"), "too_many_decimals"));
                    if (errors.Count > before) continue;
                    rate = decimal.Round(l.RateOverride!.Value, CurrencyMath.RateScale); reason = why;
                }
                else
                {
                    var applied = await ExchangeRateService.ResolveAsync(work.Connection, work.Transaction, currency, date, ct);
                    if (applied is null) { errors.Add(new(F("currency"), "rate_missing")); continue; }
                    rate = applied.Rate; rateId = applied.RateId;
                }
            }
            else if (l.RateOverride is not null) { errors.Add(new(F("rateOverride"), "not_allowed_for_base_currency")); continue; }
            decimal lineBase;
            try { lineBase = currency == ctx.BaseCurrency ? amount : CurrencyMath.ConvertToBase(amount, rate, ctx.BaseMinor); }
            catch (OverflowException) { errors.Add(new(F("debit"), "out_of_range")); continue; }
            if (lineBase <= 0) { errors.Add(new(F("debit"), "base_amount_rounds_to_zero")); continue; }

            var allocs = new List<(Guid, decimal, decimal)>();
            var allocInputs = l.Allocations;
            if (allocInputs is { Count: > 0 } && allocInputs.Any(a => a.Percent is not null))
            {
                // Percentage mode: every allocation uses a percentage (no amounts), valid distinct cost centers, percentages total exactly 100 with at most four decimals.
                if (allocInputs.Count > JournalValidation.MaxAllocationsPerLine) { errors.Add(new(F("allocations"), "too_many")); continue; }
                if (allocInputs.Any(a => a.Amount is not null || a.Percent is null)) { errors.Add(new(F("allocations"), "percent_and_amount_mixed")); continue; }
                if (allocInputs.Any(a => a.CostCenterId is null) || allocInputs.Select(a => a.CostCenterId).Distinct().Count() != allocInputs.Count) { errors.Add(new(F("allocations"), "duplicate_cost_center")); continue; }
                if (allocInputs.Any(a => a.Percent <= 0 || a.Percent != decimal.Round(a.Percent!.Value, 4))) { errors.Add(new(F("allocations"), "invalid_percent")); continue; }
                if (allocInputs.Sum(a => a.Percent!.Value) != 100m) { errors.Add(new(F("allocations"), "percent_sum_mismatch")); continue; }
                var amounts = AllocationMath.Distribute(amount, minor, [.. allocInputs.Select(a => (a.CostCenterId!.Value, a.Percent!.Value))]);
                if (amounts.Values.Any(v => v <= 0)) { errors.Add(new(F("allocations"), "allocation_rounds_to_zero")); continue; }
                allocInputs = [.. allocInputs.Select(a => new JournalAllocationInput(a.CostCenterId, amounts[a.CostCenterId!.Value]))];
            }
            if (allocInputs is { Count: > 0 })
            {
                if (allocInputs.Count > JournalValidation.MaxAllocationsPerLine) { errors.Add(new(F("allocations"), "too_many")); continue; }
                var seen = new HashSet<Guid>(); decimal sum = 0, baseSum = 0; var bad = false;
                for (var k = 0; k < allocInputs.Count; k++)
                {
                    var al = allocInputs[k];
                    string AF(string n) => $"lines[{i}].allocations[{k}].{n}";
                    if (al.CostCenterId is null) { errors.Add(new(AF("costCenterId"), "required")); bad = true; continue; }
                    if (!centers.TryGetValue(al.CostCenterId.Value, out var cc)) { errors.Add(new(AF("costCenterId"), "cost_center_not_found")); bad = true; continue; }
                    if (!historicalCostCenters && (cc.Group || !cc.Active)) { errors.Add(new(AF("costCenterId"), "cost_center_not_allocatable")); bad = true; continue; }
                    if (!seen.Add(al.CostCenterId.Value)) { errors.Add(new(AF("costCenterId"), "duplicate_cost_center")); bad = true; continue; }
                    if (al.Amount is null or <= 0) { errors.Add(new(AF("amount"), "must_be_positive")); bad = true; continue; }
                    if (al.Amount.Value != decimal.Round(al.Amount.Value, minor)) { errors.Add(new(AF("amount"), "too_many_decimals")); bad = true; continue; }
                    sum += al.Amount.Value;
                    var share = k == allocInputs.Count - 1 ? lineBase - baseSum : (currency == ctx.BaseCurrency ? al.Amount.Value : CurrencyMath.ConvertToBase(al.Amount.Value, rate, ctx.BaseMinor));
                    if (share <= 0) { errors.Add(new(AF("amount"), "base_amount_rounds_to_zero")); bad = true; continue; }
                    baseSum += share;
                    allocs.Add((al.CostCenterId.Value, al.Amount.Value, share));
                }
                if (bad) continue;
                if (sum != amount) { errors.Add(new(F("allocations"), "allocation_sum_mismatch")); continue; }   // allocations are complete and exact, never approximate
            }
            result.Add(new Computed(l.AccountId!.Value, string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(), currency, deb, cre, rate, rateId, reason,
                deb > 0 ? lineBase : 0m, cre > 0 ? lineBase : 0m, branch, false, null, [.. allocs]));
        }
        if (errors.Count > 0) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: errors);

        // Rounding: converted foreign lines can leave a difference of a few minor units. One explicit, system-marked line to the configured account absorbs it
        // (at most one minor unit per foreign line); otherwise the entry stays unbalanced and cannot be submitted. Nothing is hidden or silently adjusted.
        var diff = result.Sum(x => x.BaseDebit) - result.Sum(x => x.BaseCredit);
        var foreignLines = result.Count(x => x.Currency != ctx.BaseCurrency);
        var tolerance = foreignLines * (decimal)Math.Pow(10, -ctx.BaseMinor);
        if (diff != 0 && foreignLines > 0 && Math.Abs(diff) <= tolerance && ctx.RoundingAccountId is { } roundingAccount)
        {
            await using var q = new NpgsqlCommand($"SELECT {AC.IsActive} AND NOT {AC.IsGroup} FROM {AC.Table} WHERE {AC.Id} = @id", work.Connection, work.Transaction);
            q.Parameters.AddWithValue("id", roundingAccount);
            if (await q.ExecuteScalarAsync(ct) is true)
                result.Add(new Computed(roundingAccount, "Exchange rounding difference", ctx.BaseCurrency, diff < 0 ? -diff : 0m, diff > 0 ? diff : 0m, 1m, null, null,
                    diff < 0 ? -diff : 0m, diff > 0 ? diff : 0m, ctx.BranchId, true, null, []));
        }
        return result;
    }

    private async Task WriteLinesAsync(CompanyAuditedWork work, Guid entryId, List<Computed> lines, CancellationToken ct)
    {
        await using (var d1 = new NpgsqlCommand($"DELETE FROM {JA.Table} WHERE {JA.Line} IN (SELECT {JL.Id} FROM {JL.Table} WHERE {JL.Entry} = @e)", work.Connection, work.Transaction))
        { d1.Parameters.AddWithValue("e", entryId); await d1.ExecuteNonQueryAsync(ct); }
        await using (var d2 = new NpgsqlCommand($"DELETE FROM {JL.Table} WHERE {JL.Entry} = @e", work.Connection, work.Transaction))
        { d2.Parameters.AddWithValue("e", entryId); await d2.ExecuteNonQueryAsync(ct); }
        var no = 0;
        foreach (var l in lines)
        {
            var lid = Guid.NewGuid();
            await using var li = new NpgsqlCommand(
                $"INSERT INTO {JL.Table} ({JL.Id}, {JL.Entry}, {JL.No}, {JL.Account}, {JL.Desc}, {JL.Cur}, {JL.Debit}, {JL.Credit}, {JL.Rate}, {JL.RateId}, {JL.RateReason}, {JL.BaseDebit}, {JL.BaseCredit}, {JL.Branch}, {JL.IsRounding}, {JL.ReversesLine}) " +
                "VALUES (@id, @e, @no, @acc, @desc, @cur, @deb, @cre, @rate, @rid, @rr, @bd, @bc, @br, @rnd, NULL)", work.Connection, work.Transaction);
            li.Parameters.AddWithValue("id", lid); li.Parameters.AddWithValue("e", entryId); li.Parameters.AddWithValue("no", ++no); li.Parameters.AddWithValue("acc", l.AccountId);
            li.Parameters.Add(new NpgsqlParameter("desc", NpgsqlDbType.Varchar) { Value = (object?)l.Description ?? DBNull.Value });
            li.Parameters.AddWithValue("cur", l.Currency); li.Parameters.AddWithValue("deb", l.Debit); li.Parameters.AddWithValue("cre", l.Credit); li.Parameters.AddWithValue("rate", l.Rate);
            li.Parameters.Add(new NpgsqlParameter("rid", NpgsqlDbType.Bigint) { Value = (object?)l.RateId ?? DBNull.Value });
            li.Parameters.Add(new NpgsqlParameter("rr", NpgsqlDbType.Varchar) { Value = (object?)l.RateReason ?? DBNull.Value });
            li.Parameters.AddWithValue("bd", l.BaseDebit); li.Parameters.AddWithValue("bc", l.BaseCredit); li.Parameters.AddWithValue("br", l.BranchId); li.Parameters.AddWithValue("rnd", l.IsRounding);
            await li.ExecuteNonQueryAsync(ct);
            foreach (var (cc, amount, baseAmount) in l.Allocations)
            {
                await using var ai = new NpgsqlCommand($"INSERT INTO {JA.Table} ({JA.Line}, {JA.CC}, {JA.Amount}, {JA.Base}) VALUES (@l, @c, @a, @b)", work.Connection, work.Transaction);
                ai.Parameters.AddWithValue("l", lid); ai.Parameters.AddWithValue("c", cc); ai.Parameters.AddWithValue("a", amount); ai.Parameters.AddWithValue("b", baseAmount);
                await ai.ExecuteNonQueryAsync(ct);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct, Func<Task<T?>>? onUnchanged = null) where T : class
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) when (abort.Message == "unchanged" && onUnchanged is not null) { return ConfigResult<T>.Ok((await onUnchanged())!); }
        catch (Abort abort) { return abort.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is { } c && c.Contains(JE.KUSource.AsSpan(2), StringComparison.Ordinal)) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "duplicate_source"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, MapDatabaseRule(ex.MessageText)); }
    }

    /// <summary>Stable codes for the rules PostgreSQL enforces (the database message is never shown to clients).</summary>
    internal static string MapDatabaseRule(string message) => message switch
    {
        _ when message.Contains("independent approver") => "separation_of_duties",
        _ when message.Contains("not balanced") => "entry_not_balanced",
        _ when message.Contains("at least two lines") => "entry_needs_two_lines",
        _ when message.Contains("base currency") => "base_currency_mismatch",
        _ when message.Contains("not enabled") => "currency_not_enabled",
        _ when message.Contains("currency policy") => "currency_policy_violation",
        _ when message.Contains("active and postable") => "account_not_postable",
        _ when message.Contains("branch must be active") => "branch_inactive",
        _ when message.Contains("no longer matches the ledger") || message.Contains("do not match the balance sheet") => "year_end_stale",
        _ when message.Contains("reverse the opening balances") => "opening_balances_not_reversed",
        _ when message.Contains("requires a cost center") => "cost_center_required",
        _ when message.Contains("not eligible for the branch") => "cost_center_not_eligible",
        _ when message.Contains("leaf cost centers") => "cost_center_not_allocatable",
        _ when message.Contains("approvals") => "approval_required",
        _ when message.Contains("outside every fiscal period") => "no_fiscal_period",
        _ when message.Contains("final closed") => "fiscal_period_final_closed",
        _ when message.Contains("soft closed") => "fiscal_period_soft_closed",
        _ when message.Contains("no longer the effective rate") => "rate_changed",
        _ when message.Contains("rate") => "exchange_rate_invalid",
        _ when message.Contains("revers") => "reversal_invalid",
        _ when message.Contains("adjusting") => "adjusting_invalid",
        _ => "entry_invalid",
    };

    private static async Task<JournalEntrySummary> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var cur = await LoadHeaderAsync(work.Connection, work.Transaction, id, true, ct) ?? throw new Abort(ConfigStatus.NotFound, "journal_entry_not_found");
        if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
        return cur;
    }

    private static async Task SetStatusAsync(CompanyAuditedWork work, Guid id, JournalStatus status, string extraSet, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"UPDATE {JE.Table} SET {JE.Status} = @status{extraSet} WHERE {JE.Id} = @id", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("status", (short)status);
        bind(cmd.Parameters);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("journal entry not updated");
    }

    private async Task ReturnToDraftAsync(CompanyAuditedWork work, JournalEntrySummary cur, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {JE.Table} SET {JE.Status} = 1, {JE.Rev} = {JE.Rev} + 1, {JE.SubmittedBy} = NULL, {JE.SubmittedAt} = NULL, {JE.UpdatedAt} = @now WHERE {JE.Id} = @id", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("id", cur.Id); cmd.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private Task RecordAsync(CompanyAuditedWork work, short auditType, Guid actor, Guid entryId, JournalEventKind kind, long revision, string? detail, CancellationToken ct) =>
        AddEventAsync(work, entryId, kind, actor, revision, detail, ct, auditAlreadyWritten: false, auditType: auditType);

    /// <summary>Writes one event-log row linked to an authenticated audit event (a new one, or — for a decision — the event the approval record already wrote).</summary>
    private async Task AddEventAsync(CompanyAuditedWork work, Guid entryId, JournalEventKind kind, Guid actor, long revision, string? detail, CancellationToken ct, bool auditAlreadyWritten, short auditType = 0)
    {
        var text = detail is { Length: > 300 } ? detail[..300] : detail;
        AuditReceipt receipt;
        if (auditAlreadyWritten)
        {
            // The approval decision (or the reversal marking) is covered by an event already appended in this transaction; link a NEW event so every log row has its own audit event.
            receipt = await work.AppendAuditAsync(new AuditEventData(LedgerAuditEvents.JournalEventLogged, ActorUserId: actor, Detail: $"journal event {(short)kind} rev {revision}"), ct);
        }
        else
        {
            var json = $"{kind}|{revision}|{text}";
            receipt = await work.AppendAuditAsync(new AuditEventData(auditType, ActorUserId: actor,
                Detail: $"journal {kind} r{revision} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
        }
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {JV.Table} ({JV.Entry}, {JV.Event}, {JV.Actor}, {JV.At}, {JV.Rev}, {JV.Detail}, {JV.Stream}, {JV.Seq}) VALUES (@e, @k, @a, @t, @r, @d, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("e", entryId); cmd.Parameters.AddWithValue("k", (short)kind); cmd.Parameters.AddWithValue("a", actor); cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Varchar) { Value = (object?)text ?? DBNull.Value });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey); cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
