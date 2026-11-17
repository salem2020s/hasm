using System.Globalization;
using System.Numerics;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using NX = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeSettlement;
using NR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligationReversal;
using PO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;

namespace Hasm.Infrastructure.Inventory;

/// <summary>
/// Phase 3.8B-3: the negative-stock exception inside the atomic posting and reversal of stock documents (design Revisions 2 to 4). The engine is never asked to go negative: an issue is split into the covered part (engine) and
/// the uncovered obligations; a receipt first settles open obligations of its positions oldest first and only the rest enters the position; reversals restore exactly.
/// </summary>
public sealed partial class StockPostingService
{
    private sealed record NegativeIssue(NegativeExceptionPlan Plan, Guid ApprovalId);

    private sealed record Match(NegativeStockPlanner.OpenObligation Obligation, int LineNo, decimal LineQuantity, decimal LineAmount, decimal Quantity, decimal Provisional, decimal Actual);
    private sealed record Remainder(int LineNo, Guid ItemId, decimal Quantity, decimal Amount);
    private sealed record NegativeReceipt(IReadOnlyList<Match> Matches, IReadOnlyList<Remainder> Remainders, IReadOnlyCollection<(Guid ItemId, Guid WarehouseId)> Positions);

    /// <summary>
    /// The exception of an issue, or null when every line is covered by the stock (the ordinary path). Refusals, in order: the company has not enabled it or a line's item is not eligible (the default
    /// <c>insufficient_stock</c>, R18); no approval (<c>negative_exception_approval_required</c>); the latest approval's snapshot differs from the live plan (<c>negative_exception_stale</c>); not enough distinct approvers
    /// other than the poster and the creator (<c>negative_exception_self_approval</c> when the poster approved, else <c>negative_exception_approval_required</c>); any other line problem (its code).
    /// </summary>
    private async Task<NegativeIssue?> PrepareNegativeIssueAsync(CompanyAuditedWork work, Guid actor, StockDocumentView doc, CancellationToken ct)
    {
        var first = await NegativeStockPlanner.PlanAsync(work, costing, doc, null, true, ct);
        if (!first.Needed) return null;
        var settings = await NegativeStockPlanner.SettingsAsync(work.Connection, work.Transaction, ct);
        if (!settings.Enabled || first.Lines.Any(l => l.Problem == "negative_exception_item_not_eligible")) throw Conflict("insufficient_stock");
        var approvals = await NegativeStockPlanner.ApprovalsAsync(work.Connection, work.Transaction, doc.Id, ct);
        if (approvals.Count == 0) throw Conflict("negative_exception_approval_required");
        var (plan, current) = await NegativeStockPlanner.CurrentAsync(work, costing, doc, approvals, true, ct);
        if (current is null) throw Conflict("negative_exception_stale");
        var valid = approvals.Where(a => a.Hash == plan.Hash && a.ApproverId != actor && a.ApproverId != doc.CreatedBy).ToList();
        if (valid.Select(a => a.ApproverId).Distinct().Count() < (settings.RequireSecondApprover ? 2 : 1))
            throw Conflict(approvals.Any(a => a.Hash == plan.Hash && a.ApproverId == actor) ? "negative_exception_self_approval" : "negative_exception_approval_required");
        if (plan.Problems.Count > 0) throw Conflict(plan.Problems[0]);
        return new NegativeIssue(plan, valid[0].Id);
    }

    private static async Task<Guid> PositionIdAsync(CompanyAuditedWork work, Guid item, Guid warehouse, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {PO.Id} FROM {PO.Table} WHERE {PO.ItemId} = @i AND {PO.WarehouseId} = @w", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("i", item); q.Parameters.AddWithValue("w", warehouse);
        return (Guid)(await q.ExecuteScalarAsync(ct))!;
    }

    /// <summary>One immutable obligation per uncovered line, with the accounts of the version the issue used (R8) and its approval.</summary>
    private async Task RecordObligationsAsync(CompanyAuditedWork work, Guid actor, StockDocumentView doc, NegativeIssue negative, Guid postingId, Guid operationId, Guid journalId, CancellationToken ct)
    {
        var plan = negative.Plan;
        var date = DateOnly.ParseExact(doc.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var l in plan.Lines.Where(x => x.Uncovered > 0))
        {
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {NO.Table} ({NO.Id}, {NO.ItemId}, {NO.WarehouseId}, {NO.PositionId}, {NO.PostingId}, {NO.LineNo}, {NO.DocumentId}, {NO.IssueOperationId}, {NO.JournalEntryId}, {NO.AccountingVersionId}, " +
                $"{NO.ObligationAccountId}, {NO.VarianceAccountId}, {NO.CounterAccountId}, {NO.EffectiveDate}, {NO.OriginalQuantity}, {NO.ProvisionalTotal}, {NO.SourceCode}, {NO.SourceEntryId}, {NO.SourceQuantity}, {NO.SourceValue}, " +
                $"{NO.Justification}, {NO.ApprovalId}, {NO.CreatedBy}, {NO.CreatedAtUtc}) VALUES (@id, @i, @w, @pos, @p, @n, @d, @op, @j, @v, @o, @var, @k, @date, @q, @t, @s, @se, @sq, @sv, @just, @a, @by, @at)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("i", l.ItemId); ins.Parameters.AddWithValue("w", l.WarehouseId);
            ins.Parameters.AddWithValue("pos", await PositionIdAsync(work, l.ItemId, l.WarehouseId, ct)); ins.Parameters.AddWithValue("p", postingId); ins.Parameters.AddWithValue("n", l.LineNo); ins.Parameters.AddWithValue("d", doc.Id);
            ins.Parameters.AddWithValue("op", operationId); ins.Parameters.AddWithValue("j", journalId); ins.Parameters.AddWithValue("v", plan.AccountingVersionId!.Value);
            ins.Parameters.AddWithValue("o", plan.ObligationAccountId!.Value); ins.Parameters.AddWithValue("var", plan.VarianceAccountId!.Value); ins.Parameters.AddWithValue("k", plan.CounterAccountId!.Value);
            ins.Parameters.AddWithValue("date", date); ins.Parameters.AddWithValue("q", l.Uncovered); ins.Parameters.AddWithValue("t", l.ProvisionalTotal!.Value); ins.Parameters.AddWithValue("s", (short)l.Source!.Value);
            ins.Parameters.Add(new NpgsqlParameter("se", NpgsqlDbType.Bigint) { Value = (object?)l.SourceEntryId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("sq", NpgsqlDbType.Numeric) { Value = (object?)l.SourceQuantity ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("sv", NpgsqlDbType.Numeric) { Value = (object?)l.SourceValue ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("just", NpgsqlDbType.Varchar) { Value = (object?)l.Justification ?? DBNull.Value });
            ins.Parameters.AddWithValue("a", negative.ApprovalId); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("at", Now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.ObligationsCreated, ActorUserId: actor, SubjectId: doc.Id,
            Detail: $"negative stock obligations document={doc.Number} " + string.Join(",", plan.Lines.Where(x => x.Uncovered > 0).Select(x => $"{x.LineNo}:{x.Uncovered.ToString(CultureInfo.InvariantCulture)}@{x.ProvisionalTotal!.Value.ToString(CultureInfo.InvariantCulture)}/s{x.Source}"))), ct);
    }

    /// <summary>
    /// Matching of a receipt (5.2 to 5.4, R2, R13, R14): the open obligations of its positions, oldest first by (effective date, sequence), take each line's quantity before anything enters the position. A match takes the
    /// provisional value proportionally to the open quantity (the remainder on the final match of an obligation) and the actual value proportionally to the line's remaining quantity (the remainder on the last part of the
    /// line, matched or entering). Refused when an open obligation is dated after the receipt (<c>negative_obligation_older</c>), when its issue journal failed (<c>issue_accounting_failed</c>), or when the receipt is dated
    /// before the last movement of the position (<c>backdated_not_supported</c>). Null when no obligation is open (the ordinary receipt).
    /// </summary>
    private async Task<NegativeReceipt?> PrepareSettlementAsync(CompanyAuditedWork work, StockDocumentView doc, Guid warehouse, DateOnly date, IReadOnlyDictionary<int, decimal> amounts, CancellationToken ct)
    {
        var pairs = doc.Lines.Select(l => (l.ItemId, warehouse)).Distinct().ToList();
        var positions = await costing.LockPositionsInWorkAsync(work, pairs, ct);                          // R7: the position rows first, then their obligations
        var open = await NegativeStockPlanner.LockForPositionsAsync(work, pairs, true, ct);
        if (open.Count == 0) return null;
        if (open.Any(o => o.Date > date)) throw Conflict("negative_obligation_older");
        if (open.Any(o => o.JournalStatus is 5 or 6)) throw Conflict("issue_accounting_failed");
        if (pairs.Any(p => open.Any(o => (o.ItemId, o.WarehouseId) == p) && positions[p].LastDate is { } last && date < last)) throw Conflict("backdated_not_supported");
        var decimals = await NegativeStockPlanner.MoneyDecimalsAsync(work.Connection, work.Transaction, ct);
        BigInteger Qu(decimal v) => CostingMath.ToUnits(v, CostingMath.QuantityScale);
        BigInteger Mu(decimal v) => CostingMath.ToUnits(v, CostingMath.AmountScale);
        decimal Qd(BigInteger v) => CostingMath.FromUnits(v, CostingMath.QuantityScale);
        decimal Md(BigInteger v) => CostingMath.FromUnits(v, CostingMath.AmountScale);
        var state = open.ToDictionary(o => o.Id, o => (Q: Qu(o.OpenQuantity), P: Mu(o.OpenProvisional)));
        var matches = new List<Match>(); var rest = new List<Remainder>();
        foreach (var l in doc.Lines)
        {
            BigInteger q = Qu(l.BaseQuantity), a = Mu(amounts[l.LineNo]);
            foreach (var o in open.Where(o => o.ItemId == l.ItemId && o.WarehouseId == warehouse))
            {
                if (q.IsZero) break;
                var s = state[o.Id];
                if (s.Q.IsZero) continue;
                var m = BigInteger.Min(s.Q, q);
                var provisional = CostingMath.Proportion(s.P, m, s.Q, decimals);                         // the whole open quantity takes the exact open provisional value
                var actual = CostingMath.Proportion(a, m, q, decimals);                                  // the whole remaining line takes the exact remaining amount
                matches.Add(new Match(o, l.LineNo, l.BaseQuantity, amounts[l.LineNo], Qd(m), Md(provisional), Md(actual)));
                state[o.Id] = (s.Q - m, s.P - provisional);
                q -= m; a -= actual;
            }
            if (!q.IsZero) rest.Add(new Remainder(l.LineNo, l.ItemId, Qd(q), Md(a)));
            else if (!a.IsZero) throw new InvalidOperationException("settlement remainder lost");          // unreachable: a fully matched line hands its whole amount to its last match
        }
        if (matches.Count == 0) return null;
        return new NegativeReceipt(matches, rest, [.. matches.Select(m => (m.Obligation.ItemId, m.Obligation.WarehouseId)).Distinct()]);
    }

    /// <summary>One append-only settlement per match with its own balanced draft journal (5.2/5.3) under the idempotency key <c>stock_negative_settlement:&lt;obligation&gt;:&lt;posting&gt;:&lt;line&gt;</c>, dated on the receipt.</summary>
    private async Task RecordSettlementsAsync(CompanyAuditedWork work, Guid actor, StockDocumentView doc, NegativeReceipt settling, Guid postingId, Guid operationId, IReadOnlyDictionary<Guid, ResolvedInventoryAccounts>? config, DateOnly date, CancellationToken ct)
    {
        foreach (var m in settling.Matches)
        {
            Guid? journalId = null;
            var receiptCounter = config?.Values.First().ReceiptCounterAccountId;
            if (m.Actual > 0 && receiptCounter is null) throw Conflict("receipt_counter_account_not_configured");
            // review M3: the live receipt counter account G must stay apart from the O and V the obligation was issued with, or the settlement journal would net them (and the O/V reconciliation would be wrong)
            if (receiptCounter is { } g && (g == m.Obligation.VarianceAccount || g == m.Obligation.ObligationAccount)) throw Conflict("negative_settlement_account_conflict");
            if (NegativeStockPlanner.SettlementLines(m.Obligation.ObligationAccount, m.Obligation.VarianceAccount, receiptCounter ?? Guid.Empty, m.Provisional, m.Actual, doc.BranchId) is { } lines)
            {
                var entry = await journal.CreateInWorkAsync(work, actor, new JournalInput(date, $"Negative stock settlement: issue {m.Obligation.DocumentNumber} by receipt {doc.Number}", doc.BranchId, lines, null), false,
                    DocumentSource.Automatic, NegativeStockPlanner.SettlementKeyOf(m.Obligation.Id, postingId, m.LineNo), ct);
                journalId = entry.Header.Id;
            }
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {NX.Table} ({NX.Id}, {NX.ObligationId}, {NX.ReceiptPostingId}, {NX.ReceiptLineNo}, {NX.ReceiptOperationId}, {NX.LineQuantity}, {NX.LineAmount}, {NX.MatchedQuantity}, {NX.ProvisionalAmount}, {NX.ActualAmount}, " +
                $"{NX.Difference}, {NX.EffectiveDate}, {NX.JournalEntryId}, {NX.CreatedBy}, {NX.CreatedAtUtc}) VALUES (@id, @o, @p, @n, @op, @lq, @la, @q, @pv, @ac, @df, @d, @j, @by, @at)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("o", m.Obligation.Id); ins.Parameters.AddWithValue("p", postingId); ins.Parameters.AddWithValue("n", m.LineNo); ins.Parameters.AddWithValue("op", operationId);
            ins.Parameters.AddWithValue("lq", m.LineQuantity); ins.Parameters.AddWithValue("la", m.LineAmount); ins.Parameters.AddWithValue("q", m.Quantity); ins.Parameters.AddWithValue("pv", m.Provisional); ins.Parameters.AddWithValue("ac", m.Actual);
            ins.Parameters.AddWithValue("df", m.Actual - m.Provisional); ins.Parameters.AddWithValue("d", date);
            ins.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Uuid) { Value = (object?)journalId ?? DBNull.Value });
            ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("at", Now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.Settled, ActorUserId: actor, SubjectId: doc.Id,
            Detail: $"negative stock settled by receipt={doc.Number} " + string.Join(",", settling.Matches.Select(m => $"{m.Obligation.DocumentNumber}/{m.LineNo}:{m.Quantity.ToString(CultureInfo.InvariantCulture)} prov={m.Provisional.ToString(CultureInfo.InvariantCulture)} actual={m.Actual.ToString(CultureInfo.InvariantCulture)}"))), ct);
    }

    // ================================================================================================================ reversal

    private sealed record SettlementRow(Guid Id, Guid ObligationId, Guid PostingId, int LineNo, Guid OperationId, decimal LineQuantity, decimal LineAmount, decimal Quantity, decimal Provisional, decimal Actual, DateOnly Date, Guid? JournalId, long Seq);

    private static async Task<List<SettlementRow>> ForwardSettlementsAsync(CompanyAuditedWork work, string where, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT s.{NX.Id}, s.{NX.ObligationId}, s.{NX.ReceiptPostingId}, s.{NX.ReceiptLineNo}, s.{NX.ReceiptOperationId}, s.{NX.LineQuantity}, s.{NX.LineAmount}, s.{NX.MatchedQuantity}, s.{NX.ProvisionalAmount}, s.{NX.ActualAmount}, " +
            $"s.{NX.EffectiveDate}, s.{NX.JournalEntryId}, s.{NX.Seq} FROM {NX.Table} s WHERE s.{NX.ReversesSettlementId} IS NULL AND NOT EXISTS (SELECT 1 FROM {NX.Table} x WHERE x.{NX.ReversesSettlementId} = s.{NX.Id}) AND {where} " +
            $"ORDER BY s.{NX.Seq} DESC", work.Connection, work.Transaction);
        bind(q);
        var list = new List<SettlementRow>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SettlementRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetFieldValue<DateOnly>(10),
                r.IsDBNull(11) ? null : r.GetGuid(11), r.GetInt64(12)));
        return list;
    }

    private sealed record NegativeReversal(List<NegativeStockPlanner.OpenObligation> Obligations, List<SettlementRow> Settlements, IReadOnlyCollection<(Guid ItemId, Guid WarehouseId)> Aware);

    /// <summary>
    /// What the reversal of an executed document must do for the exception, checked BEFORE anything is written (R4, R5, R11, R21): an issue with obligations is reversible only while none has a net settlement
    /// (<c>negative_obligation_settled</c>; reverse the settling receipts first, newest first); a settling receipt is reversible only while no obligation it settled has a newer settlement by another receipt
    /// (<c>negative_settlement_newer_exists</c>) and no obligation it would re-open lies in a Final Closed period (<c>negative_obligation_period_closed</c>).
    /// </summary>
    private async Task<NegativeReversal?> PrepareReversalAsync(CompanyAuditedWork work, StockPostingView posting, CancellationToken ct)
    {
        if (posting.DocType == (int)StockDocType.Issue)
        {
            var obligations = await NegativeStockPlanner.LockForPostingAsync(work, posting.Id, ct);
            if (obligations.Count == 0) return null;
            if (obligations.Any(o => o.NetMatched != 0)) throw Conflict("negative_obligation_settled");
            // the covered part comes back into positions whose obligations are reversed in the same transaction; another document's open obligation on such a position still refuses it (the engine)
            var others = await NegativeStockPlanner.LockForPositionsAsync(work, obligations.Select(o => (o.ItemId, o.WarehouseId)), true, ct);
            var aware = obligations.Select(o => (o.ItemId, o.WarehouseId)).Distinct().Where(p => others.All(x => (x.ItemId, x.WarehouseId) != p || x.PostingId == posting.Id)).ToList();
            return new NegativeReversal(obligations, [], aware);
        }
        if (posting.DocType != (int)StockDocType.Receipt) return null;
        var settlements = await ForwardSettlementsAsync(work, $"s.{NX.ReceiptPostingId} = @p", q => q.Parameters.AddWithValue("p", posting.Id), ct);
        if (settlements.Count == 0) return null;
        var ids = settlements.Select(s => s.ObligationId).Distinct().ToArray();
        var all = await ForwardSettlementsAsync(work, $"s.{NX.ObligationId} = ANY(@o)", q => q.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids }), ct);
        foreach (var s in settlements)
            if (all.Any(x => x.ObligationId == s.ObligationId && x.PostingId != posting.Id && x.Seq > s.Seq)) throw Conflict("negative_settlement_newer_exists");
        await using (var q = new NpgsqlCommand($"SELECT DISTINCT {NO.EffectiveDate} FROM {NO.Table} WHERE {NO.Id} = ANY(@o)", work.Connection, work.Transaction))
        {
            q.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            var dates = new List<DateOnly>();
            await using (var r = await q.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) dates.Add(r.GetFieldValue<DateOnly>(0));
            foreach (var d in dates) if (await NegativeStockPlanner.PeriodStateAsync(work, d, ct) == (short)FiscalPeriodState.FinalClosed) throw Conflict("negative_obligation_period_closed");
        }
        return new NegativeReversal([], settlements, []);
    }

    /// <summary>The settlement journals of a reversed receipt follow the 3.7C rule: an unposted one is cancelled (the reversal must stay in the posting's period), a posted one gets a draft reversal entry.</summary>
    private async Task<Dictionary<Guid, Guid?>> ReverseSettlementJournalsAsync(CompanyAuditedWork work, Guid actor, StockDocumentView doc, NegativeReversal negative, DateOnly postedOn, DateOnly date, CancellationToken ct)
    {
        var refs = new Dictionary<Guid, Guid?>();
        foreach (var s in negative.Settlements)
        {
            if (s.JournalId is not { } jid) { refs[s.Id] = null; continue; }
            var view = await JournalService.LoadViewAsync(work.Connection, work.Transaction, jid, true, ct) ?? throw Conflict("journal_not_found");
            if (view.Header.Status is not (JournalStatus.Posted or JournalStatus.Reversed) && await PeriodOfAsync(work, postedOn, ct) != await PeriodOfAsync(work, date, ct))
                throw StockDocumentService.Invalid("effectiveDate", "must_stay_in_posting_period");
            switch (view.Header.Status)
            {
                case JournalStatus.Draft or JournalStatus.PendingApproval or JournalStatus.Approved:
                    await journal.CancelInWorkAsync(work, actor, jid, view.Header.Revision, ct);
                    refs[s.Id] = jid;
                    break;
                case JournalStatus.Posted:
                    var description = $"Reversal of negative stock settlement by receipt {doc.Number}";
                    refs[s.Id] = (await journal.ReverseInWorkAsync(work, actor, jid, view.Header.Revision, new ReversalInput(date, description), description, ct)).Header.Id;
                    break;
                case JournalStatus.Cancelled or JournalStatus.Reversed:
                    refs[s.Id] = jid;
                    break;
                default:
                    throw Conflict("journal_state_not_reversible");
            }
        }
        return refs;
    }

    /// <summary>Writes the append-only records of the reversal: an obligation reversal per obligation of a reversed issue, a negating settlement per settlement of a reversed receipt (re-opening the obligations exactly).</summary>
    private async Task RecordNegativeReversalAsync(CompanyAuditedWork work, Guid actor, StockDocumentView doc, NegativeReversal negative, Guid reversalId, Guid operationId, DateOnly date, Dictionary<Guid, Guid?> journals, CancellationToken ct)
    {
        foreach (var o in negative.Obligations)
        {
            await using var ins = new NpgsqlCommand($"INSERT INTO {NR.Table} ({NR.Id}, {NR.ObligationId}, {NR.StockReversalId}, {NR.ReversalOperationId}, {NR.EffectiveDate}, {NR.CreatedBy}, {NR.CreatedAtUtc}) VALUES (@id, @o, @r, @op, @d, @by, @at)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("o", o.Id); ins.Parameters.AddWithValue("r", reversalId); ins.Parameters.AddWithValue("op", operationId);
            ins.Parameters.AddWithValue("d", date); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("at", Now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (negative.Obligations.Count > 0)
            await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.ObligationsReversed, ActorUserId: actor, SubjectId: doc.Id, Detail: $"negative stock obligations reversed document={doc.Number} count={negative.Obligations.Count}"), ct);
        foreach (var s in negative.Settlements)
        {
            await using var ins = new NpgsqlCommand(
                $"INSERT INTO {NX.Table} ({NX.Id}, {NX.ObligationId}, {NX.ReceiptPostingId}, {NX.ReceiptLineNo}, {NX.ReceiptOperationId}, {NX.LineQuantity}, {NX.LineAmount}, {NX.MatchedQuantity}, {NX.ProvisionalAmount}, {NX.ActualAmount}, " +
                $"{NX.Difference}, {NX.EffectiveDate}, {NX.JournalEntryId}, {NX.ReversesSettlementId}, {NX.ReversalId}, {NX.CreatedBy}, {NX.CreatedAtUtc}) VALUES (@id, @o, @p, @n, @op, @lq, @la, @q, @pv, @ac, @df, @d, @j, @rs, @rv, @by, @at)", work.Connection, work.Transaction);
            ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("o", s.ObligationId); ins.Parameters.AddWithValue("p", s.PostingId); ins.Parameters.AddWithValue("n", s.LineNo); ins.Parameters.AddWithValue("op", s.OperationId);
            ins.Parameters.AddWithValue("lq", s.LineQuantity); ins.Parameters.AddWithValue("la", s.LineAmount); ins.Parameters.AddWithValue("q", -s.Quantity); ins.Parameters.AddWithValue("pv", -s.Provisional); ins.Parameters.AddWithValue("ac", -s.Actual);
            ins.Parameters.AddWithValue("df", -(s.Actual - s.Provisional)); ins.Parameters.AddWithValue("d", date);
            ins.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Uuid) { Value = (object?)journals.GetValueOrDefault(s.Id) ?? DBNull.Value });
            ins.Parameters.AddWithValue("rs", s.Id); ins.Parameters.AddWithValue("rv", reversalId); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("at", Now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (negative.Settlements.Count > 0)
        {
            // R3: a re-opened obligation needs an empty position; a later movement that brought stock in since makes the reversal impossible
            await using var q = new NpgsqlCommand(
                $"SELECT EXISTS (SELECT 1 FROM {NO.Table} o JOIN {PO.Table} p ON p.{PO.Id} = o.{NO.PositionId} WHERE o.{NO.Id} = ANY(@o) AND p.{PO.Quantity} > 0)", work.Connection, work.Transaction);
            q.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = negative.Settlements.Select(s => s.ObligationId).Distinct().ToArray() });
            if ((bool)(await q.ExecuteScalarAsync(ct))!) throw Conflict("reversal_not_possible_later_movements");
            await work.AppendAuditAsync(new AuditEventData(NegativeStockAuditEvents.SettlementsReversed, ActorUserId: actor, SubjectId: doc.Id, Detail: $"negative stock settlements reversed receipt={doc.Number} count={negative.Settlements.Count}"), ct);
        }
    }
}
