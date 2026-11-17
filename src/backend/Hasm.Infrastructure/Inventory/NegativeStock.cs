using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Npgsql;
using NpgsqlTypes;
using NS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeStockSetting;
using NF = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeStockItemFlag;
using NA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeExceptionApproval;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using NX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeSettlement;
using NR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligationReversal;
using NG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeStockGuard;
using EN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using D = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;

namespace Hasm.Infrastructure.Inventory;

/// <summary>Business audit events of the negative-stock exception (Phase 3.8B-3; 1180-1189 of the business stream).</summary>
public static class NegativeStockAuditEvents
{
    public const short SettingsSaved = 1180;
    public const short ItemFlagChanged = 1181;
    public const short ExceptionApproved = 1182;
    public const short ObligationsCreated = 1183;
    public const short Settled = 1184;
    public const short SettlementsReversed = 1185;
    public const short ObligationsReversed = 1186;
}

public sealed record NegativeStockSettingsView(int VersionNo, bool Enabled, bool RequireSecondApprover, string? Note, Guid? CreatedBy, DateTimeOffset? CreatedAtUtc);
public sealed record NegativeStockSettingsInput(bool? Enabled, bool? RequireSecondApprover, string? Note);
public sealed record NegativeItemFlagView(Guid ItemId, string Code, string? NameAr, string? NameEn, bool Eligible, int? EffectiveValuation, bool Tracked, bool IsActive, bool HasOpenObligation, string? Note, Guid? ChangedBy, DateTimeOffset? ChangedAtUtc);
public sealed record NegativeItemFlagInput(bool? Eligible, string? Note);

/// <summary>A manual provisional TOTAL for the uncovered quantity of one issue line (source 3): only when no last average and no stock-document receipt exist; never a unit cost; justification mandatory.</summary>
public sealed record NegativeManualCostInput(int? LineNo, decimal? Amount, string? Justification);
public sealed record NegativeApprovalInput(string? Reason, IReadOnlyList<NegativeManualCostInput>? ManualCosts);

/// <summary>
/// One line of the exception plan of an issue document: the quantity on hand of its position BEFORE the line, the covered part (engine, real moving average) and the uncovered part with its provisional value and source
/// (1 last average, 2 latest stock-document receipt, 3 manual). <see cref="Problem"/> is a stable refusal code when the line cannot use the exception. UnitCost is derived for display only.
/// </summary>
public sealed record NegativeExceptionLine(int LineNo, Guid ItemId, Guid WarehouseId, decimal Quantity, decimal OnHand, decimal Covered, decimal Uncovered, int? Source, long? SourceEntryId, decimal? SourceQuantity,
    decimal? SourceValue, decimal? ProvisionalTotal, decimal? ProvisionalUnitCost, string? Justification, string? Problem);

/// <summary>The snapshot an approver approves (and the posting recomputes): any change of document, stock, cost source, accounting version or accounts changes <see cref="Hash"/> and makes an approval stale.</summary>
public sealed record NegativeExceptionPlan(Guid DocumentId, long DocumentRevision, string DocumentDate, bool Needed, Guid? AccountingVersionId, Guid? ObligationAccountId, Guid? VarianceAccountId, Guid? CounterAccountId,
    IReadOnlyList<NegativeExceptionLine> Lines, IReadOnlyList<string> Problems, string Hash);

public sealed record NegativeApprovalView(Guid Id, Guid ApproverId, DateTimeOffset ApprovedAtUtc, string Reason, string SnapshotHash, bool Current);

/// <summary>The exception state of a document: the current plan, the switches, and the approvals (<see cref="ValidApprovals"/> counts the distinct approvers of the CURRENT snapshot).</summary>
public sealed record NegativeExceptionView(NegativeExceptionPlan Plan, bool Enabled, bool RequireSecondApprover, int ApprovalsRequired, int ValidApprovals, bool Posted, IReadOnlyList<NegativeApprovalView> Approvals);

public sealed record NegativeSettlementView(Guid Id, Guid ReceiptPostingId, Guid ReceiptDocumentId, string? ReceiptNumber, int ReceiptLineNo, string EffectiveDate, decimal MatchedQuantity, decimal ProvisionalAmount, decimal ActualAmount,
    decimal Difference, Guid? JournalEntryId, Guid? ReversesSettlementId, bool Reversed);

/// <summary>Status: 1 open, 2 settled, 3 reversed (the originating issue was reversed). Open figures are derived from the append-only settlement rows.</summary>
public sealed record NegativeObligationView(Guid Id, long Seq, Guid ItemId, string ItemCode, string? ItemNameAr, string? ItemNameEn, Guid WarehouseId, Guid DocumentId, string? DocumentNumber, int LineNo, string EffectiveDate,
    decimal OriginalQuantity, decimal ProvisionalTotal, decimal OpenQuantity, decimal OpenProvisional, int Source, string? Justification, int Status, int AgeDays, Guid JournalEntryId, short? JournalStatus,
    Guid ObligationAccountId, Guid VarianceAccountId, IReadOnlyList<NegativeSettlementView> Settlements);

/// <summary>
/// The negative-stock close report for one period end (N3): the obligations open now with a document date on or before it (they block Final Close; at soft close they are a warning), and the point-in-time reconciliation of
/// the obligation account O (open provisional total as of the period end versus the posted ledger balance of O) and of the variance account V (settlement differences versus the posted lines of their journals on V).
/// </summary>
public sealed record NegativeCloseReport(string PeriodEnd, IReadOnlyList<NegativeObligationView> Open, decimal ExpectedObligationBalance, decimal LedgerObligationBalance, decimal SettlementDifferences, decimal LedgerVariance,
    int FailedSettlementJournals, bool Reconciled);

/// <summary>Shared, transaction-bound logic of the exception (used by the service below, by stock posting and by its reversal). Every method runs inside the caller's locks.</summary>
internal static class NegativeStockPlanner
{
    internal sealed record Manual(decimal Amount, string Justification);

    internal sealed record OpenObligation(Guid Id, long Seq, Guid ItemId, Guid WarehouseId, DateOnly Date, decimal Original, decimal Total, decimal NetMatched, decimal NetProvisional, Guid ObligationAccount, Guid VarianceAccount,
        Guid CounterAccount, Guid JournalId, short? JournalStatus, Guid PostingId, Guid DocumentId, string? DocumentNumber, bool Reversed)
    {
        public decimal OpenQuantity => Reversed ? 0m : Original - NetMatched;
        public decimal OpenProvisional => Reversed ? 0m : Total - NetProvisional;
    }

    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Dec(decimal v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
    internal static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    internal static async Task<int> MoneyDecimalsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT c.{CompanySchema.Currency.MinorUnits} FROM {CompanySchema.CompanyCurrency.Table} cc JOIN {CompanySchema.Currency.Table} c ON c.{CompanySchema.Currency.Code} = cc.{CompanySchema.CompanyCurrency.Code} WHERE cc.{CompanySchema.CompanyCurrency.IsBase}", conn, tx);
        var v = await q.ExecuteScalarAsync(ct);
        if (v is null) throw new CostingException("base_currency_not_set");
        return v is DBNull ? 2 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    /// <summary>The current company enablement (version 0, off, when never configured).</summary>
    internal static async Task<NegativeStockSettingsView> SettingsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {NS.VersionNo}, {NS.Enabled}, {NS.RequireSecondApprover}, {NS.Note}, {NS.CreatedBy}, {NS.CreatedAtUtc} FROM {NS.Table} ORDER BY {NS.VersionNo} DESC LIMIT 1", conn, tx);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new NegativeStockSettingsView(0, false, false, null, null, null);
        return new NegativeStockSettingsView(r.GetInt32(0), r.GetBoolean(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5));
    }

    /// <summary>The current eligibility flag of each item (false when never flagged).</summary>
    internal static async Task<Dictionary<Guid, bool>> FlagsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IEnumerable<Guid> items, CancellationToken ct)
    {
        var ids = items.Distinct().ToArray();
        var map = ids.ToDictionary(i => i, _ => false);
        await using var q = new NpgsqlCommand($"SELECT DISTINCT ON ({NF.ItemId}) {NF.ItemId}, {NF.Eligible} FROM {NF.Table} WHERE {NF.ItemId} = ANY(@ids) ORDER BY {NF.ItemId}, {NF.Id} DESC", conn, tx);
        q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetGuid(0)] = r.GetBoolean(1);
        return map;
    }

    private static string ObligationSelect(string where, string tail) =>
        $"SELECT o.{NO.Id}, o.{NO.Seq}, o.{NO.ItemId}, o.{NO.WarehouseId}, o.{NO.EffectiveDate}, o.{NO.OriginalQuantity}, o.{NO.ProvisionalTotal}, " +
        $"coalesce((SELECT sum(s.{NX.MatchedQuantity}) FROM {NX.Table} s WHERE s.{NX.ObligationId} = o.{NO.Id}), 0), coalesce((SELECT sum(s.{NX.ProvisionalAmount}) FROM {NX.Table} s WHERE s.{NX.ObligationId} = o.{NO.Id}), 0), " +
        $"o.{NO.ObligationAccountId}, o.{NO.VarianceAccountId}, o.{NO.CounterAccountId}, o.{NO.JournalEntryId}, (SELECT j.{JE.Status} FROM {JE.Table} j WHERE j.{JE.Id} = o.{NO.JournalEntryId}), o.{NO.PostingId}, o.{NO.DocumentId}, " +
        $"(SELECT d.{D.Number} FROM {D.Table} d WHERE d.{D.Id} = o.{NO.DocumentId}), EXISTS (SELECT 1 FROM {NR.Table} r WHERE r.{NR.ObligationId} = o.{NO.Id}) " +
        $"FROM {NO.Table} o WHERE {where} {tail}";

    private static OpenObligation ReadObligation(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetInt64(1), r.GetGuid(2), r.GetGuid(3), r.GetFieldValue<DateOnly>(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetGuid(9), r.GetGuid(10), r.GetGuid(11), r.GetGuid(12),
            r.IsDBNull(13) ? null : r.GetInt16(13), r.GetGuid(14), r.GetGuid(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetBoolean(17));

    /// <summary>The obligations of these positions in the matching order (effective date, sequence); with <paramref name="openOnly"/> only those still open. The CALLER holds the company warehouse-structure lock and the position rows (every writer of obligations does: R7 serialises on the position row, which needs no write privilege on the append-only obligation table).</summary>
    internal static async Task<List<OpenObligation>> LockForPositionsAsync(CompanyAuditedWork work, IEnumerable<(Guid ItemId, Guid WarehouseId)> pairs, bool openOnly, CancellationToken ct)
    {
        var list = pairs.Distinct().ToList();
        if (list.Count == 0) return [];
        await using var q = new NpgsqlCommand(ObligationSelect($"(o.{NO.ItemId}, o.{NO.WarehouseId}) IN (SELECT * FROM unnest(@i, @w))", $"ORDER BY o.{NO.EffectiveDate}, o.{NO.Seq}"), work.Connection, work.Transaction);
        q.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = list.Select(p => p.ItemId).ToArray() });
        q.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = list.Select(p => p.WarehouseId).ToArray() });
        var result = new List<OpenObligation>();
        await using (var r = await q.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) result.Add(ReadObligation(r));
        return openOnly ? [.. result.Where(o => o.OpenQuantity > 0)] : result;
    }

    /// <summary>The obligations created by one issue posting (the caller holds the company warehouse-structure lock).</summary>
    internal static async Task<List<OpenObligation>> LockForPostingAsync(CompanyAuditedWork work, Guid postingId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(ObligationSelect($"o.{NO.PostingId} = @p", $"ORDER BY o.{NO.EffectiveDate}, o.{NO.Seq}"), work.Connection, work.Transaction);
        q.Parameters.AddWithValue("p", postingId);
        var result = new List<OpenObligation>();
        await using (var r = await q.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) result.Add(ReadObligation(r));
        return result;
    }

    /// <summary>The latest date the position moved or an obligation or settlement of it was dated (R22).</summary>
    private static async Task<DateOnly?> LatestObligationDateAsync(CompanyAuditedWork work, Guid item, Guid warehouse, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT greatest((SELECT max(o.{NO.EffectiveDate}) FROM {NO.Table} o WHERE o.{NO.ItemId} = @i AND o.{NO.WarehouseId} = @w), " +
            $"(SELECT max(s.{NX.EffectiveDate}) FROM {NX.Table} s JOIN {NO.Table} o ON o.{NO.Id} = s.{NX.ObligationId} WHERE o.{NO.ItemId} = @i AND o.{NO.WarehouseId} = @w))", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", warehouse);
        var v = await q.ExecuteScalarAsync(ct);
        return v is DateOnly d ? d : v is DateTime dt ? DateOnly.FromDateTime(dt) : null;
    }

    /// <summary>D5 narrowed by R15 (3): the latest non-reversed costing RECEIPT entry produced by the posting of a STOCK-DOCUMENT receipt of this item and warehouse, with positive quantity and value.</summary>
    private static async Task<(long Entry, decimal Quantity, decimal Amount)?> LatestReceiptAsync(CompanyAuditedWork work, Guid item, Guid warehouse, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT e.{EN.Id}, e.{EN.Quantity}, e.{EN.Amount} FROM {EN.Table} e JOIN {P.Table} p ON p.{P.CostOperationId} = e.{EN.OperationId} AND p.{P.DocType} = 1 " +
            $"WHERE e.{EN.ItemId} = @i AND e.{EN.WarehouseId} = @w AND e.{EN.Kind} = 1 AND e.{EN.Direction} = 1 AND e.{EN.ReversesEntryId} IS NULL AND e.{EN.Quantity} > 0 AND e.{EN.Amount} > 0 " +
            $"AND NOT EXISTS (SELECT 1 FROM {EN.Table} x WHERE x.{EN.ReversesEntryId} = e.{EN.Id}) ORDER BY e.{EN.EffectiveDate} DESC, e.{EN.Seq} DESC LIMIT 1", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", warehouse);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetDecimal(1), r.GetDecimal(2)) : null;
    }

    /// <summary>
    /// The exception plan of an ISSUE document, computed under the posting locks from the live data (positions locked and, when needed, created with their fixed method). Lines of the same position are covered in document
    /// order from the quantity on hand BEFORE the document; the uncovered rest is priced by source 1 (the position's current average, else its stored last pair; a zero value is no valid average), 2 (latest
    /// stock-document receipt) or 3 (<paramref name="manual"/>, only when 1 and 2 are unavailable). Nothing is written except a missing position row.
    /// </summary>
    internal static async Task<NegativeExceptionPlan> PlanAsync(CompanyAuditedWork work, InventoryCostingService costing, StockDocumentView doc, IReadOnlyDictionary<int, Manual>? manual, CancellationToken ct)
    {
        var date = DateOnly.ParseExact(doc.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var problems = new List<string>();
        var lines = new List<NegativeExceptionLine>();
        if (doc.DocType != (int)StockDocType.Issue || doc.SourceWarehouseId is not { } wh)
            return new NegativeExceptionPlan(doc.Id, doc.Revision, doc.DocumentDate, false, null, null, null, null, [], [doc.DocType == (int)StockDocType.Transfer ? "negative_exception_transfer_not_allowed" : "negative_exception_issue_only"], "");
        var decimals = await MoneyDecimalsAsync(work.Connection, work.Transaction, ct);
        var positions = await costing.LockPositionsInWorkAsync(work, doc.Lines.Select(l => (l.ItemId, wh)), ct);
        var onHand = positions.ToDictionary(p => p.Key, p => p.Value.Quantity);
        foreach (var l in doc.Lines)
        {
            var key = (l.ItemId, wh);
            var before = onHand[key];
            var covered = Math.Min(before, l.BaseQuantity);
            onHand[key] = before - covered;
            lines.Add(new NegativeExceptionLine(l.LineNo, l.ItemId, wh, l.BaseQuantity, before, covered, l.BaseQuantity - covered, null, null, null, null, null, null, null, null));
        }
        var needed = lines.Any(x => x.Uncovered > 0);
        Guid? versionId = null, obligation = null, variance = null, counter = null;
        if (needed)
        {
            var flags = await FlagsAsync(work.Connection, work.Transaction, lines.Where(x => x.Uncovered > 0).Select(x => x.ItemId), ct);
            var cfg = await InventoryAccountingService.ReadAsync(work.Connection, work.Transaction, null, ct);
            if (cfg.Version is null) problems.Add("inventory_accounting_not_configured");
            else
            {
                versionId = cfg.Version.Id; obligation = cfg.ObligationAccountId; counter = cfg.IssueCounterAccountId;
                variance = cfg.VarianceAccountId ?? cfg.IssueCounterAccountId;
                if (obligation is null) problems.Add("negative_obligation_account_not_configured");
                if (counter is null) problems.Add("issue_counter_account_not_configured");
                if (obligation is not null && counter is not null && (variance == cfg.ReceiptCounterAccountId || variance == obligation || obligation == counter)) problems.Add("negative_variance_account_invalid");
            }
            var latest = new Dictionary<(Guid, Guid), DateOnly?>();
            for (var i = 0; i < lines.Count; i++)
            {
                var x = lines[i];
                if (x.Uncovered <= 0) continue;
                var pos = positions[(x.ItemId, wh)];
                string? problem = null;
                if (!flags[x.ItemId] || pos.Method != CostMethod.MovingAverage) problem = "negative_exception_item_not_eligible";
                if (!latest.TryGetValue((x.ItemId, wh), out var last)) latest[(x.ItemId, wh)] = last = await LatestObligationDateAsync(work, x.ItemId, wh, ct);
                if (problem is null && ((pos.LastDate is { } pd && date < pd) || (last is { } od && date < od))) problem = "backdated_not_supported";
                int? source = null; long? entry = null; decimal? sq = null, sv = null, total = null; string? justification = null;
                if (pos.Quantity > 0 && pos.Value > 0) { source = 1; sq = pos.Quantity; sv = pos.Value; }
                else if (pos.Quantity == 0 && pos.LastAverageQuantity is > 0 && pos.LastAverageValue is > 0) { source = 1; sq = pos.LastAverageQuantity; sv = pos.LastAverageValue; }
                else if (await LatestReceiptAsync(work, x.ItemId, wh, ct) is { } rc) { source = 2; entry = rc.Entry; sq = rc.Quantity; sv = rc.Amount; }
                if (manual is not null && manual.TryGetValue(x.LineNo, out var m))
                {
                    if (source is not null) problem ??= "manual_cost_not_allowed";
                    else if (m.Amount <= 0 || !CostingMath.FitsDecimals(m.Amount, decimals) || m.Amount >= 1_000_000_000_000_000_000m) problem ??= "invalid_amount";
                    else { source = 3; total = m.Amount; justification = m.Justification; }
                }
                if (source is 1 or 2)
                {
                    var units = CostingMath.Scale(CostingMath.ToUnits(sv!.Value, CostingMath.AmountScale), CostingMath.ToUnits(x.Uncovered, CostingMath.QuantityScale), CostingMath.ToUnits(sq!.Value, CostingMath.QuantityScale), decimals);
                    total = CostingMath.FromUnits(units, CostingMath.AmountScale);
                }
                if (source is null || total is not > 0) problem ??= "provisional_cost_unavailable";       // a zero or missing cost is never assumed
                lines[i] = x with { Source = source, SourceEntryId = entry, SourceQuantity = sq, SourceValue = sv, ProvisionalTotal = total, ProvisionalUnitCost = total is > 0 ? CostingMath.UnitCost(total.Value, x.Uncovered) : null, Justification = justification, Problem = problem };
                if (problem is not null && !problems.Contains(problem)) problems.Add(problem);
            }
        }
        var canonical = $"v1|{doc.Id}|{doc.Revision}|{doc.DocumentDate}|{wh}|{versionId}|{obligation}|{variance}|{counter}|" +
            string.Join(";", lines.Select(x => $"{x.LineNo}:{x.ItemId}:{Dec(x.Quantity)}:{Dec(x.OnHand)}:{Dec(x.Covered)}:{Dec(x.Uncovered)}:{x.Source}:{x.SourceEntryId}:{(x.SourceQuantity is { } a ? Dec(a) : "")}:{(x.SourceValue is { } b ? Dec(b) : "")}:{(x.ProvisionalTotal is { } c ? Dec(c) : "")}:{x.Justification}"));
        return new NegativeExceptionPlan(doc.Id, doc.Revision, doc.DocumentDate, needed, versionId, obligation, variance, counter, lines, problems, Sha(canonical));
    }

    internal sealed record ApprovalRow(Guid Id, Guid ApproverId, DateTimeOffset At, string Reason, string Hash, string Snapshot);

    internal static async Task<List<ApprovalRow>> ApprovalsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid documentId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {NA.Id}, {NA.ApproverId}, {NA.ApprovedAtUtc}, {NA.Reason}, {NA.SnapshotHash}, {NA.Snapshot} FROM {NA.Table} WHERE {NA.DocumentId} = @d ORDER BY {NA.ApprovedAtUtc} DESC, {NA.Id}", conn, tx);
        q.Parameters.AddWithValue("d", documentId);
        var list = new List<ApprovalRow>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ApprovalRow(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetString(4).Trim(), r.GetString(5)));
        return list;
    }

    /// <summary>
    /// The plan as it stands now for a document that may have approvals: each distinct approved snapshot is recomputed with ITS manual inputs, and the first one whose live hash equals the approved hash is current
    /// (independent of the approval times); otherwise the live plan without manual inputs, and no approval is current.
    /// </summary>
    internal static async Task<(NegativeExceptionPlan Plan, ApprovalRow? Approved)> CurrentAsync(CompanyAuditedWork work, InventoryCostingService costing, StockDocumentView doc, IReadOnlyList<ApprovalRow> approvals, CancellationToken ct)
    {
        foreach (var a in approvals.GroupBy(x => x.Hash).Select(g => g.First()))
        {
            var p = await PlanAsync(work, costing, doc, ManualOf(a.Snapshot), ct);
            if (p.Hash == a.Hash) return (p, a);
        }
        return (await PlanAsync(work, costing, doc, null, ct), null);
    }

    /// <summary>The manual inputs recorded in an approval snapshot (only source-3 lines carry them).</summary>
    internal static Dictionary<int, Manual> ManualOf(string snapshot)
    {
        var plan = JsonSerializer.Deserialize<NegativeExceptionPlan>(snapshot, Json)!;
        return plan.Lines.Where(l => l.Source == 3 && l.ProvisionalTotal is not null).ToDictionary(l => l.LineNo, l => new Manual(l.ProvisionalTotal!.Value, l.Justification ?? ""));
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The CURRENT period state of a date (null when no period covers it).</summary>
    internal static async Task<short?> PeriodStateAsync(CompanyAuditedWork work, DateOnly date, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {FP.Status} FROM {FP.Table} WHERE {FP.StartDate} <= @d AND {FP.EndDate} >= @d", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("d", date);
        return await q.ExecuteScalarAsync(ct) is short s ? s : null;
    }

    /// <summary>The settlement journal of one match (5.2/5.3): Dr O provisional, Dr V a positive difference / Cr G actual, Cr V a negative difference. Lines of zero are omitted; null when nothing has value.</summary>
    internal static IReadOnlyList<Ledger.JournalLineInput>? SettlementLines(Guid obligationAccount, Guid varianceAccount, Guid receiptCounter, decimal provisional, decimal actual, Guid branch)
    {
        var lines = new List<Ledger.JournalLineInput>();
        Ledger.JournalLineInput L(Guid a, decimal? dr, decimal? cr) => new(a, null, null, dr, cr, branch, null, null, null);
        var diff = actual - provisional;
        if (provisional > 0) lines.Add(L(obligationAccount, provisional, null));
        if (diff > 0) lines.Add(L(varianceAccount, diff, null));
        if (actual > 0) lines.Add(L(receiptCounter, null, actual));
        if (diff < 0) lines.Add(L(varianceAccount, null, -diff));
        return lines.Count == 0 ? null : lines;
    }

    internal static string SettlementKeyOf(Guid obligation, Guid posting, int lineNo) => $"stock_negative_settlement:{obligation:N}:{posting:N}:{lineNo}";
}

/// <summary>
/// The negative-stock exception (Phase 3.8B-3) apart from posting: the versioned company enablement, the per-item eligibility flag, the exception approval of an issue document (segregated, audited, snapshot based), and the
/// obligation list and close report. <b>No authorization here by design:</b> <see cref="NegativeStockManagementService"/> wraps it. Every write takes the company warehouse-structure advisory lock (the same lock as posting, R7).
/// </summary>
public sealed class NegativeStockService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, InventoryCostingService costing, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static StockDocumentService.Abort Conflict(string code) => new(ConfigStatus.Conflict, code);
    private sealed class Unchanged(object value) : Exception("unchanged") { public object Value { get; } = value; }
    private sealed class Rollback(object value) : Exception("rollback") { public object Value { get; } = value; }

    private async Task<T> InLockAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> body, CancellationToken ct) =>
        await transactions.ExecuteAsync(companyId, async work =>
        {
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
            {
                lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
                lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
                await lk.ExecuteNonQueryAsync(ct);
            }
            return await body(work);
        }, ct);

    private static ConfigResult<T> Fail<T>(Exception e) => e switch
    {
        StockDocumentService.Abort a => a.To<T>(),
        CostingException c => ConfigResult<T>.Fail(ConfigStatus.Conflict, c.Code),
        InvalidOperationException i when i.Message is "inventory_accounting_not_configured" or "inventory_account_not_configured" => ConfigResult<T>.Fail(ConfigStatus.Conflict, i.Message),
        PostgresException p when p.MessageText.StartsWith("negative_", StringComparison.Ordinal) => ConfigResult<T>.Fail(ConfigStatus.Conflict, p.MessageText.Split(':')[0]),
        _ => throw e,
    };

    // ================================================================================================================ enablement

    public async Task<NegativeStockSettingsView> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await NegativeStockPlanner.SettingsAsync(conn, null, ct);
    }

    /// <summary>Saves a new immutable version (optimistic by version number; an identical save is a no-op). Off by default; turning it off stops new exceptions at once and never blocks settlement.</summary>
    public async Task<ConfigResult<NegativeStockSettingsView>> SaveSettingsAsync(Guid actor, Guid companyId, long? expectedVersion, NegativeStockSettingsInput? input, CancellationToken ct = default)
    {
        if (expectedVersion is null) return ConfigResult<NegativeStockSettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var errors = new List<FieldError>();
        if (input?.Enabled is null) errors.Add(new("enabled", "required"));
        var note = string.IsNullOrWhiteSpace(input?.Note) ? null : input!.Note!.Trim();
        if (note is { Length: > 500 }) errors.Add(new("note", "too_long"));
        else if (note is not null && note.Any(char.IsControl)) errors.Add(new("note", "invalid_characters"));
        if (errors.Count > 0) return ConfigResult<NegativeStockSettingsView>.Invalid(errors);
        try
        {
            return ConfigResult<NegativeStockSettingsView>.Ok(await InLockAsync(companyId, async work =>
            {
                var cur = await NegativeStockPlanner.SettingsAsync(work.Connection, work.Transaction, ct);
                if (cur.VersionNo != expectedVersion) throw new StockDocumentService.Abort(ConfigStatus.PreconditionFailed, "settings_version_conflict", null, cur.VersionNo);
                var second = input!.RequireSecondApprover ?? false;
                if (cur.VersionNo > 0 && cur.Enabled == input.Enabled && cur.RequireSecondApprover == second && (note is null || note == cur.Note)) throw new Unchanged(cur);
                await using (var ins = new NpgsqlCommand($"INSERT INTO {NS.Table} ({NS.Id}, {NS.VersionNo}, {NS.Enabled}, {NS.RequireSecondApprover}, {NS.Note}, {NS.CreatedBy}, {NS.CreatedAtUtc}) VALUES (@id, @n, @e, @s, @note, @by, @at)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("n", cur.VersionNo + 1); ins.Parameters.AddWithValue("e", input.Enabled!.Value); ins.Parameters.AddWithValue("s", second);
                    ins.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
                    ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("at", Now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.SettingsSaved, ActorUserId: actor, Detail: $"negative stock settings v{cur.VersionNo + 1} enabled={input.Enabled} secondApprover={second}"), ct);
                return await NegativeStockPlanner.SettingsAsync(work.Connection, work.Transaction, ct);
            }, ct));
        }
        catch (Unchanged u) { return ConfigResult<NegativeStockSettingsView>.Ok((NegativeStockSettingsView)u.Value); }
        catch (Exception e) when (e is StockDocumentService.Abort) { return Fail<NegativeStockSettingsView>(e); }
    }

    // ================================================================================================================ eligibility flag

    private static async Task<List<NegativeItemFlagView>> FlagViewsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? itemId, CancellationToken ct)
    {
        var list = new List<NegativeItemFlagView>();
        await using var q = new NpgsqlCommand(
            $"SELECT i.{I.Id}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, coalesce(f.{NF.Eligible}, false), {NG.ResolvedMethodFunction}(i.{I.Id}), (i.{I.IsLot} OR i.{I.IsSerial} OR i.{I.HasExpiry}), i.{I.IsActive}, {NG.HasOpenFunction}(i.{I.Id}, NULL), " +
            $"f.{NF.Note}, f.{NF.ChangedBy}, f.{NF.ChangedAtUtc} FROM {I.Table} i LEFT JOIN LATERAL (SELECT * FROM {NF.Table} x WHERE x.{NF.ItemId} = i.{I.Id} ORDER BY x.{NF.Id} DESC LIMIT 1) f ON true " +
            $"WHERE i.{I.ItemType} = 1" + (itemId is null ? "" : $" AND i.{I.Id} = @i") + $" ORDER BY i.{I.Code}", conn, tx);
        if (itemId is not null) q.Parameters.AddWithValue("i", itemId.Value);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new NegativeItemFlagView(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetInt16(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)));
        return list;
    }

    public async Task<IReadOnlyList<NegativeItemFlagView>> ListItemFlagsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await FlagViewsAsync(conn, null, null, ct);
    }

    /// <summary>Sets or clears the eligibility of one goods item (append-only history, audited). PostgreSQL refuses an ineligible item and clearing while an obligation is open.</summary>
    public async Task<ConfigResult<NegativeItemFlagView>> SetItemFlagAsync(Guid actor, Guid companyId, Guid itemId, NegativeItemFlagInput? input, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        if (input?.Eligible is null) errors.Add(new("eligible", "required"));
        var note = string.IsNullOrWhiteSpace(input?.Note) ? null : input!.Note!.Trim();
        if (note is { Length: > 500 }) errors.Add(new("note", "too_long"));
        else if (note is not null && note.Any(char.IsControl)) errors.Add(new("note", "invalid_characters"));
        if (errors.Count > 0) return ConfigResult<NegativeItemFlagView>.Invalid(errors);
        try
        {
            return ConfigResult<NegativeItemFlagView>.Ok(await InLockAsync(companyId, async work =>
            {
                var cur = (await FlagViewsAsync(work.Connection, work.Transaction, itemId, ct)).SingleOrDefault() ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "item_not_found");
                if (cur.Eligible == input!.Eligible) throw new Unchanged(cur);
                if (cur.Eligible && cur.HasOpenObligation) throw Conflict("negative_obligation_open");
                if (input.Eligible!.Value && (cur.Tracked || !cur.IsActive || cur.EffectiveValuation != (int)ValuationMethod.MovingAverage)) throw Conflict("negative_exception_item_not_eligible");
                await using (var ins = new NpgsqlCommand($"INSERT INTO {NF.Table} ({NF.ItemId}, {NF.Eligible}, {NF.Note}, {NF.ChangedBy}, {NF.ChangedAtUtc}) VALUES (@i, @e, @n, @by, @at)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("i", itemId); ins.Parameters.AddWithValue("e", input.Eligible.Value);
                    ins.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
                    ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("at", Now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.ItemFlagChanged, ActorUserId: actor, SubjectId: itemId, Detail: $"negative stock eligibility item={cur.Code} eligible={input.Eligible.Value}"), ct);
                return (await FlagViewsAsync(work.Connection, work.Transaction, itemId, ct)).Single();
            }, ct));
        }
        catch (Unchanged u) { return ConfigResult<NegativeItemFlagView>.Ok((NegativeItemFlagView)u.Value); }
        catch (Exception e) when (e is StockDocumentService.Abort or PostgresException) { return Fail<NegativeItemFlagView>(e); }
    }

    // ================================================================================================================ exception approval

    private async Task<NegativeExceptionView> ViewAsync(CompanyAuditedWork work, StockDocumentView doc, IReadOnlyDictionary<int, NegativeStockPlanner.Manual>? manual, CancellationToken ct)
    {
        var settings = await NegativeStockPlanner.SettingsAsync(work.Connection, work.Transaction, ct);
        var approvals = await NegativeStockPlanner.ApprovalsAsync(work.Connection, work.Transaction, doc.Id, ct);
        var plan = manual is null ? (await NegativeStockPlanner.CurrentAsync(work, costing, doc, approvals, ct)).Plan : await NegativeStockPlanner.PlanAsync(work, costing, doc, manual, ct);
        var posted = await StockPostingService.LoadByDocumentAsync(work.Connection, work.Transaction, doc.Id, false, ct) is not null;
        var valid = approvals.Where(a => a.Hash == plan.Hash).Select(a => a.ApproverId).Distinct().Count();
        return new NegativeExceptionView(plan, settings.Enabled, settings.RequireSecondApprover, settings.RequireSecondApprover ? 2 : 1, valid, posted,
            [.. approvals.Select(a => new NegativeApprovalView(a.Id, a.ApproverId, a.At, a.Reason, a.Hash, a.Hash == plan.Hash))]);
    }

    /// <summary>The exception state of a document now (a preview: computed in a transaction that is rolled back, so nothing is written).</summary>
    public async Task<ConfigResult<NegativeExceptionView>> GetExceptionAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        try
        {
            await InLockAsync<int>(companyId, async work =>
            {
                var doc = await StockDocumentService.Load(work.Connection, work.Transaction, documentId, false, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "document_not_found");
                throw new Rollback(await ViewAsync(work, doc, null, ct));
            }, ct);
            throw new InvalidOperationException("unreachable");
        }
        catch (Rollback r) { return ConfigResult<NegativeExceptionView>.Ok((NegativeExceptionView)r.Value); }
        catch (Exception e) when (e is StockDocumentService.Abort or CostingException or InvalidOperationException { Message: "inventory_accounting_not_configured" or "inventory_account_not_configured" }) { return Fail<NegativeExceptionView>(e); }
    }

    /// <summary>
    /// Records an exception approval of an APPROVED, not executed ISSUE document by <paramref name="actor"/> (who is never its creator; the poster is checked at posting): the plan computed now under the posting locks is the
    /// approved snapshot (with the manual provisional totals when the cost sources are missing). Refused while the company has not enabled the exception, for transfers and receipts, when no line exceeds the stock, and
    /// when a line cannot use it (not eligible, no provisional cost, accounts not configured, back-dated). No background process ever creates an approval.
    /// </summary>
    public async Task<ConfigResult<NegativeExceptionView>> ApproveAsync(Guid actor, Guid companyId, Guid documentId, long? expectedRevision, NegativeApprovalInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<NegativeExceptionView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var errors = new List<FieldError>();
        var reason = input?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) errors.Add(new("reason", "required"));
        else if (reason.Length > 500) errors.Add(new("reason", "too_long"));
        else if (reason.Any(char.IsControl)) errors.Add(new("reason", "invalid_characters"));
        Dictionary<int, NegativeStockPlanner.Manual>? manual = null;
        var supplied = input?.ManualCosts ?? [];
        for (var i = 0; i < supplied.Count; i++)
        {
            manual ??= [];
            var m = supplied[i];
            var just = m.Justification?.Trim();
            if (m.LineNo is not { } no) { errors.Add(new($"manualCosts[{i}].lineNo", "required")); continue; }
            if (m.Amount is not { } amount) { errors.Add(new($"manualCosts[{i}].amount", "required")); continue; }
            if (amount <= 0) { errors.Add(new($"manualCosts[{i}].amount", "must_be_positive")); continue; }
            if (string.IsNullOrEmpty(just)) { errors.Add(new($"manualCosts[{i}].justification", "required")); continue; }
            if (just.Length > 500 || just.Any(char.IsControl)) { errors.Add(new($"manualCosts[{i}].justification", "invalid")); continue; }
            if (!manual.TryAdd(no, new NegativeStockPlanner.Manual(amount, just))) errors.Add(new($"manualCosts[{i}].lineNo", "duplicate_line"));
        }
        if (errors.Count > 0) return ConfigResult<NegativeExceptionView>.Invalid(errors);
        try
        {
            return ConfigResult<NegativeExceptionView>.Ok(await InLockAsync(companyId, async work =>
            {
                var doc = await StockDocumentService.Load(work.Connection, work.Transaction, documentId, true, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "document_not_found");
                if (doc.DocType == (int)StockDocType.Transfer) throw Conflict("negative_exception_transfer_not_allowed");
                if (doc.DocType != (int)StockDocType.Issue) throw Conflict("negative_exception_issue_only");
                if (await StockPostingService.LoadByDocumentAsync(work.Connection, work.Transaction, doc.Id, false, ct) is not null) throw Conflict("already_posted");
                if (doc.Status != (int)StockDocStatus.Approved) throw Conflict("document_not_approved");
                if (expectedRevision != doc.Revision) throw new StockDocumentService.Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, doc.Revision);
                if (doc.CreatedBy == actor) throw Conflict("negative_exception_self_approval");
                var settings = await NegativeStockPlanner.SettingsAsync(work.Connection, work.Transaction, ct);
                if (!settings.Enabled) throw Conflict("negative_exception_disabled");
                var approvals = await NegativeStockPlanner.ApprovalsAsync(work.Connection, work.Transaction, doc.Id, ct);
                // a second approver confirms the current approved snapshot (manual totals included) unless they enter their own
                var plan = manual is null ? (await NegativeStockPlanner.CurrentAsync(work, costing, doc, approvals, ct)).Plan : await NegativeStockPlanner.PlanAsync(work, costing, doc, manual, ct);
                manual ??= plan.Lines.Where(l => l.Source == 3 && l.ProvisionalTotal is not null).ToDictionary(l => l.LineNo, l => new NegativeStockPlanner.Manual(l.ProvisionalTotal!.Value, l.Justification ?? ""));
                if (!plan.Needed) throw Conflict("negative_exception_not_needed");
                if (plan.Problems.Count > 0)
                    throw new StockDocumentService.Abort(ConfigStatus.Conflict, plan.Problems[0], [.. plan.Lines.Select((l, i) => (l, i)).Where(x => x.l.Problem is not null).Select(x => new FieldError($"lines[{x.i}]", x.l.Problem!))]);
                if (approvals.Any(a => a.ApproverId == actor && a.Hash == plan.Hash)) throw Conflict("already_approved");
                var id = Guid.NewGuid();
                await using (var ins = new NpgsqlCommand($"INSERT INTO {NA.Table} ({NA.Id}, {NA.DocumentId}, {NA.DocumentRevision}, {NA.ApproverId}, {NA.ApprovedAtUtc}, {NA.Reason}, {NA.SnapshotHash}, {NA.Snapshot}) VALUES (@id, @d, @r, @a, @at, @reason, @h, @s)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("d", doc.Id); ins.Parameters.AddWithValue("r", doc.Revision); ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("at", Now);
                    ins.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Varchar) { Value = reason! });
                    ins.Parameters.AddWithValue("h", plan.Hash);
                    ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(plan, NegativeStockPlanner.Json) });
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.ExceptionApproved, ActorUserId: actor, SubjectId: doc.Id,
                    Detail: $"negative stock exception approved document={doc.Number} uncovered={string.Join(",", plan.Lines.Where(l => l.Uncovered > 0).Select(l => $"{l.LineNo}:{l.Uncovered.ToString(CultureInfo.InvariantCulture)}@{l.ProvisionalTotal?.ToString(CultureInfo.InvariantCulture)}/s{l.Source}"))} hash={plan.Hash[..16]}"), ct);
                return await ViewAsync(work, doc, manual, ct);
            }, ct));
        }
        catch (Exception e) when (e is StockDocumentService.Abort or CostingException or PostgresException or InvalidOperationException { Message: "inventory_accounting_not_configured" or "inventory_account_not_configured" }) { return Fail<NegativeExceptionView>(e); }
    }

    // ================================================================================================================ obligations and the close report

    internal static async Task<List<NegativeObligationView>> ObligationViewsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, Action<NpgsqlCommand> bind, DateOnly today, CancellationToken ct)
    {
        var heads = new List<NegativeObligationView>();
        await using (var q = new NpgsqlCommand(
            $"SELECT o.{NO.Id}, o.{NO.Seq}, o.{NO.ItemId}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, o.{NO.WarehouseId}, o.{NO.DocumentId}, d.{D.Number}, o.{NO.LineNo}, o.{NO.EffectiveDate}, o.{NO.OriginalQuantity}, o.{NO.ProvisionalTotal}, " +
            $"coalesce((SELECT sum(s.{NX.MatchedQuantity}) FROM {NX.Table} s WHERE s.{NX.ObligationId} = o.{NO.Id}), 0), coalesce((SELECT sum(s.{NX.ProvisionalAmount}) FROM {NX.Table} s WHERE s.{NX.ObligationId} = o.{NO.Id}), 0), " +
            $"EXISTS (SELECT 1 FROM {NR.Table} r WHERE r.{NR.ObligationId} = o.{NO.Id}), o.{NO.SourceCode}, o.{NO.Justification}, o.{NO.JournalEntryId}, j.{JE.Status}, o.{NO.ObligationAccountId}, o.{NO.VarianceAccountId} " +
            $"FROM {NO.Table} o JOIN {I.Table} i ON i.{I.Id} = o.{NO.ItemId} JOIN {D.Table} d ON d.{D.Id} = o.{NO.DocumentId} LEFT JOIN {JE.Table} j ON j.{JE.Id} = o.{NO.JournalEntryId} WHERE {where} ORDER BY o.{NO.EffectiveDate}, o.{NO.Seq}", conn, tx))
        {
            bind(q);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var date = r.GetFieldValue<DateOnly>(10);
                decimal orig = r.GetDecimal(11), total = r.GetDecimal(12), nq = r.GetDecimal(13), np = r.GetDecimal(14);
                var reversed = r.GetBoolean(15);
                var openQ = reversed ? 0 : orig - nq; var openP = reversed ? 0 : total - np;
                heads.Add(new NegativeObligationView(r.GetGuid(0), r.GetInt64(1), r.GetGuid(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetGuid(6), r.GetGuid(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.GetInt32(9), NegativeStockPlanner.Iso(date), orig, total, openQ, openP, r.GetInt16(16), r.IsDBNull(17) ? null : r.GetString(17),
                    reversed ? 3 : openQ > 0 ? 1 : 2, Math.Max(0, today.DayNumber - date.DayNumber), r.GetGuid(18), r.IsDBNull(19) ? null : r.GetInt16(19), r.GetGuid(20), r.GetGuid(21), []));
            }
        }
        if (heads.Count == 0) return heads;
        var settlements = new Dictionary<Guid, List<NegativeSettlementView>>();
        await using (var q = new NpgsqlCommand(
            $"SELECT s.{NX.ObligationId}, s.{NX.Id}, s.{NX.ReceiptPostingId}, p.{P.DocumentId}, d.{D.Number}, s.{NX.ReceiptLineNo}, s.{NX.EffectiveDate}, s.{NX.MatchedQuantity}, s.{NX.ProvisionalAmount}, s.{NX.ActualAmount}, s.{NX.Difference}, " +
            $"s.{NX.JournalEntryId}, s.{NX.ReversesSettlementId}, EXISTS (SELECT 1 FROM {NX.Table} x WHERE x.{NX.ReversesSettlementId} = s.{NX.Id}) " +
            $"FROM {NX.Table} s JOIN {P.Table} p ON p.{P.Id} = s.{NX.ReceiptPostingId} JOIN {D.Table} d ON d.{D.Id} = p.{P.DocumentId} WHERE s.{NX.ObligationId} = ANY(@o) ORDER BY s.{NX.Seq}", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = heads.Select(h => h.Id).ToArray() });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var o = r.GetGuid(0);
                if (!settlements.TryGetValue(o, out var list)) settlements[o] = list = [];
                list.Add(new NegativeSettlementView(r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), NegativeStockPlanner.Iso(r.GetFieldValue<DateOnly>(6)), r.GetDecimal(7), r.GetDecimal(8),
                    r.GetDecimal(9), r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.GetBoolean(13)));
            }
        }
        return [.. heads.Select(h => h with { Settlements = settlements.GetValueOrDefault(h.Id) ?? [] })];
    }

    public async Task<IReadOnlyList<Guid>> WarehouseIdsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {CompanySchema.Warehouse.Id} FROM {CompanySchema.Warehouse.Table}", conn);
        var list = new List<Guid>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    /// <summary>The obligations of the given warehouses (null = all), open only or all, oldest first.</summary>
    public async Task<IReadOnlyList<NegativeObligationView>> ListObligationsAsync(Guid companyId, IReadOnlyCollection<Guid>? warehouses, bool openOnly, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var where = (warehouses is null ? "true" : $"o.{NO.WarehouseId} = ANY(@w)") + (openOnly ? $" AND {NG.OpenQuantityFunction}(o.{NO.Id}) > 0" : "");
        return await ObligationViewsAsync(conn, null, where, q => { if (warehouses is not null) q.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = warehouses.ToArray() }); },
            DateOnly.FromDateTime(Now), ct);
    }

    /// <summary>The close report for a period end (see <see cref="NegativeCloseReport"/>), computed with exact decimal arithmetic in the database.</summary>
    public async Task<NegativeCloseReport> CloseReportAsync(Guid companyId, DateOnly periodEnd, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var open = await ObligationViewsAsync(conn, null, $"o.{NO.EffectiveDate} <= @e AND {NG.OpenQuantityFunction}(o.{NO.Id}) > 0", q => q.Parameters.AddWithValue("e", periodEnd), periodEnd, ct);
        var r = await NegativeStockReconciliation.ComputeAsync(conn, null, periodEnd, ct);
        return new NegativeCloseReport(NegativeStockPlanner.Iso(periodEnd), open, r.Expected, r.Ledger, r.Differences, r.LedgerVariance, r.FailedJournals,
            r.Expected == r.Ledger && r.Differences == r.LedgerVariance && r.FailedJournals == 0);
    }
}

/// <summary>Point-in-time reconciliation of the obligation and variance accounts (N3, R9), shared by the close probe and the close report. Exact decimal arithmetic in the database.</summary>
internal static class NegativeStockReconciliation
{
    internal sealed record Figures(decimal Expected, decimal Ledger, decimal Differences, decimal LedgerVariance, int FailedJournals, bool Any);

    internal static async Task<Figures> ComputeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, DateOnly end, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            // expected credit balance of O at the end: provisional totals of obligations dated up to it, minus the provisional settled (net of settlement reversals) and the totals of reversed obligations, all dated up to it
            $"SELECT EXISTS (SELECT 1 FROM {NO.Table} WHERE {NO.EffectiveDate} <= @e), " +
            $"coalesce((SELECT sum({NO.ProvisionalTotal}) FROM {NO.Table} WHERE {NO.EffectiveDate} <= @e), 0) " +
            $"- coalesce((SELECT sum({NX.ProvisionalAmount}) FROM {NX.Table} WHERE {NX.EffectiveDate} <= @e), 0) " +
            $"- coalesce((SELECT sum(o.{NO.ProvisionalTotal}) FROM {NR.Table} r JOIN {NO.Table} o ON o.{NO.Id} = r.{NR.ObligationId} WHERE r.{NR.EffectiveDate} <= @e), 0), " +
            // the posted ledger balance (credit - debit) of every obligation account, entries dated up to the end
            $"coalesce((SELECT sum(l.{JL.BaseCredit} - l.{JL.BaseDebit}) FROM {JL.Table} l JOIN {JE.Table} j ON j.{JE.Id} = l.{JL.Entry} WHERE j.{JE.Status} IN (4, 5) AND j.{JE.Date} <= @e " +
            $"AND l.{JL.Account} IN (SELECT DISTINCT {NO.ObligationAccountId} FROM {NO.Table})), 0), " +
            // settlement differences whose journal is in the ledger, and the lines of those journals on their variance account
            $"coalesce((SELECT sum(s.{NX.Difference}) FROM {NX.Table} s JOIN {JE.Table} j ON j.{JE.Id} = s.{NX.JournalEntryId} WHERE s.{NX.EffectiveDate} <= @e AND j.{JE.Status} IN (4, 5) AND j.{JE.Date} <= @e), 0), " +
            $"coalesce((SELECT sum(l.{JL.BaseDebit} - l.{JL.BaseCredit}) FROM {NX.Table} s JOIN {NO.Table} o ON o.{NO.Id} = s.{NX.ObligationId} JOIN {JE.Table} j ON j.{JE.Id} = s.{NX.JournalEntryId} " +
            $"JOIN {JL.Table} l ON l.{JL.Entry} = j.{JE.Id} AND l.{JL.Account} = o.{NO.VarianceAccountId} WHERE s.{NX.EffectiveDate} <= @e AND j.{JE.Status} IN (4, 5) AND j.{JE.Date} <= @e), 0), " +
            // a forward settlement whose journal was cancelled or reversed outside a receipt reversal has no valid accounting side (3.7C)
            $"(SELECT count(*) FROM {NX.Table} s JOIN {JE.Table} j ON j.{JE.Id} = s.{NX.JournalEntryId} WHERE s.{NX.EffectiveDate} <= @e AND s.{NX.ReversesSettlementId} IS NULL AND j.{JE.Status} IN (5, 6) " +
            $"AND NOT EXISTS (SELECT 1 FROM {NX.Table} x WHERE x.{NX.ReversesSettlementId} = s.{NX.Id}))", conn, tx);
        q.Parameters.AddWithValue("e", end);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new Figures(r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), (int)r.GetInt64(5), r.GetBoolean(0));
    }
}

/// <summary>
/// Authorized entry points. Enablement and item eligibility: <c>company.inventorysettings.view</c> to read, <c>company.inventorysettings.configure</c> to change (company-wide). Exception approval:
/// <c>company.stockdocs.negativeexception</c> on the document's warehouse AND the right to see the document. Obligations: <c>company.stockdocs.view</c>, filtered to the warehouses the user may see. Close report:
/// <c>company.inventoryaccounting.view</c> (company-wide). Re-read on every call.
/// </summary>
public sealed class NegativeStockManagementService(AccessService access, StockDocumentService documents, NegativeStockService negative)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Company(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);
    private async Task<bool> SettingsView(Guid user, Guid company, CancellationToken ct) =>
        await Company(user, company, PermissionCatalog.Codes.InventorySettingsView, ct) || await Company(user, company, PermissionCatalog.Codes.InventorySettingsConfigure, ct);

    public async Task<ConfigResult<NegativeStockSettingsView>> GetSettingsAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await SettingsView(user, company, ct) ? ConfigResult<NegativeStockSettingsView>.Ok(await negative.GetSettingsAsync(company, ct)) : Denied<NegativeStockSettingsView>();

    public async Task<ConfigResult<NegativeStockSettingsView>> SaveSettingsAsync(Guid user, Guid company, long? expectedVersion, NegativeStockSettingsInput? input, CancellationToken ct = default) =>
        await Company(user, company, PermissionCatalog.Codes.InventorySettingsConfigure, ct) ? await negative.SaveSettingsAsync(user, company, expectedVersion, input, ct) : Denied<NegativeStockSettingsView>();

    public async Task<ConfigResult<IReadOnlyList<NegativeItemFlagView>>> ListItemFlagsAsync(Guid user, Guid company, CancellationToken ct = default) =>
        await SettingsView(user, company, ct) ? ConfigResult<IReadOnlyList<NegativeItemFlagView>>.Ok(await negative.ListItemFlagsAsync(company, ct)) : Denied<IReadOnlyList<NegativeItemFlagView>>();

    public async Task<ConfigResult<NegativeItemFlagView>> SetItemFlagAsync(Guid user, Guid company, Guid itemId, NegativeItemFlagInput? input, CancellationToken ct = default) =>
        await Company(user, company, PermissionCatalog.Codes.InventorySettingsConfigure, ct) ? await negative.SetItemFlagAsync(user, company, itemId, input, ct) : Denied<NegativeItemFlagView>();

    private async Task<StockDocumentView?> VisibleAsync(Guid user, Guid company, Guid documentId, CancellationToken ct)
    {
        var doc = await documents.GetAsync(company, documentId, ct);
        if (doc is null) return null;
        foreach (var w in new[] { doc.SourceWarehouseId, doc.TargetWarehouseId }.Where(x => x is not null))
            if (await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockDocsView, ScopeTarget.Warehouse(w!.Value), ct)) return doc;
        return null;
    }

    public async Task<ConfigResult<NegativeExceptionView>> GetExceptionAsync(Guid user, Guid company, Guid documentId, CancellationToken ct = default) =>
        await VisibleAsync(user, company, documentId, ct) is null ? Denied<NegativeExceptionView>() : await negative.GetExceptionAsync(company, documentId, ct);

    public async Task<ConfigResult<NegativeExceptionView>> ApproveAsync(Guid user, Guid company, Guid documentId, long? expectedRevision, NegativeApprovalInput? input, CancellationToken ct = default)
    {
        var doc = await VisibleAsync(user, company, documentId, ct);
        if (doc is null) return Denied<NegativeExceptionView>();
        foreach (var w in new[] { doc.SourceWarehouseId, doc.TargetWarehouseId }.Where(x => x is not null))
            if (!await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockDocsNegativeException, ScopeTarget.Warehouse(w!.Value), ct)) return Denied<NegativeExceptionView>();
        return await negative.ApproveAsync(user, company, documentId, expectedRevision, input, ct);
    }

    /// <summary>The warehouses for which the user may approve an exception (drives the screen; the server re-checks every call).</summary>
    public async Task<IReadOnlyList<Guid>> ApprovableWarehousesAsync(Guid user, Guid company, IEnumerable<Guid> warehouses, CancellationToken ct = default)
    {
        var list = new List<Guid>();
        foreach (var w in warehouses) if (await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockDocsNegativeException, ScopeTarget.Warehouse(w), ct)) list.Add(w);
        return list;
    }

    public async Task<ConfigResult<IReadOnlyList<NegativeObligationView>>> ListObligationsAsync(Guid user, Guid company, bool openOnly, CancellationToken ct = default)
    {
        var visible = new List<Guid>();
        foreach (var w in await negative.WarehouseIdsAsync(company, ct)) if (await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockDocsView, ScopeTarget.Warehouse(w), ct)) visible.Add(w);
        if (visible.Count == 0) return Denied<IReadOnlyList<NegativeObligationView>>();
        return ConfigResult<IReadOnlyList<NegativeObligationView>>.Ok(await negative.ListObligationsAsync(company, visible, openOnly, ct));
    }

    public async Task<ConfigResult<NegativeCloseReport>> CloseReportAsync(Guid user, Guid company, DateOnly periodEnd, CancellationToken ct = default) =>
        await Company(user, company, PermissionCatalog.Codes.InventoryAccountingView, ct) || await Company(user, company, PermissionCatalog.Codes.InventoryAccountingConfigure, ct)
            ? ConfigResult<NegativeCloseReport>.Ok(await negative.CloseReportAsync(company, periodEnd, ct)) : Denied<NegativeCloseReport>();
}
