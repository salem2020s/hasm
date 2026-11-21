using System.Globalization;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;
using D = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using PL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingLine;
using PE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingEvent;
using RV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;

namespace Hasm.Infrastructure.Inventory;

/// <summary>Business audit event types of stock posting (1160 is the inventory accounting configuration; 1161-1169 belong to posting).</summary>
public static class StockPostingAuditEvents
{
    public const short Posted = 1161;
    public const short Reversed = 1162;
}

/// <summary>
/// The accounting side of an executed document, DERIVED from facts (never stored as a flag that could drift): 1 = a draft journal entry exists that still has to follow the normal approval and posting workflow (accounting pending);
/// 2 = no journal is required (zero value, or a transfer that stays inside the same inventory accounts); 3 = the journal entry is posted; 4 = the journal entry was cancelled or reversed outside the stock reversal, so the stock effect has
/// no valid accounting side and needs attention (blocks the period close); 5 = the document was reversed. The physical stock effect and the financial journal status are separate facts: the stock effect is complete once a posting exists.
/// </summary>
public enum StockAccountingState : short { Pending = 1, NotRequired = 2, Posted = 3, Failed = 4, Reversed = 5 }

/// <summary>The cost of one document line of a RECEIPT, entered at posting time in the base currency (zero is allowed for free goods). Issues and transfers take their cost from the costing engine and accept no amounts.</summary>
public sealed record StockPostLineInput(int? LineNo, decimal? Amount);
public sealed record StockPostInput(IReadOnlyList<StockPostLineInput>? Lines);

/// <summary>0 = no journal existed, 1 = the journal had not been posted and was cancelled, 2 = the journal had been posted and a draft reversal entry (following the ordinary workflow) was created.</summary>
public sealed record StockReversalView(Guid Id, Guid CostOperationId, int JournalAction, Guid? JournalEntryId, string EffectiveDate, DateTimeOffset ReversedAtUtc, Guid ReversedBy, string Reason);
public sealed record StockReverseInput(string? EffectiveDate, string? Reason);

public sealed record StockPostingLineView(int LineNo, Guid ItemId, Guid WarehouseId, decimal BaseQuantity, decimal Amount);

/// <summary>An executed document: the costing operation that moved the stock, the accounting configuration version and the journal entry used (null when none is required), and the current accounting state.</summary>
public sealed record StockPostingView(Guid Id, Guid DocumentId, int DocType, string EffectiveDate, DateTimeOffset PostedAtUtc, Guid PostedBy, Guid CostOperationId, Guid? AccountingVersionId, Guid? JournalEntryId,
    decimal TotalValue, int AccountingState, bool Replayed, IReadOnlyList<StockPostingLineView> Lines, int? JournalStatus = null, StockReversalView? Reversal = null);

/// <summary>
/// Atomic posting of an APPROVED stock document (Phase 3.7B). <b>No authorization here by design:</b> <see cref="StockPostingManagementService"/> wraps it.
/// <para>In ONE company transaction (after the same advisory lock that serialises warehouse structure changes and the row lock of the document) it: re-validates the document (references, units, active warehouses and branch,
/// an OPEN fiscal period, the live inventory accounts), costs the movement through <see cref="InventoryCostingService"/> with the idempotency key <c>stock_document:&lt;id&gt;</c> (receipts take the exact amounts the poster
/// entered per line; issues and transfers are costed by the engine at the actual cost), creates the BALANCED draft journal entry through the automatic journal path with the same key (an explicit accounting-pending
/// state: the entry then follows the ordinary approval and posting workflow), stores the posting record, its lines and its first accounting event, and appends the audit event. Any failure rolls back everything:
/// there is never a stock effect without its valuation and accounting state, and never a quantity-only posting.</para>
/// A document posts at most once (unique per document and per costing operation); repeating the same request returns the original posting unchanged. The document itself stays <i>approved</i>; an executed document can no
/// longer be cancelled (enforced by PostgreSQL).
/// </summary>
public sealed partial class StockPostingService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, InventoryCostingService costing, JournalService journal, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public static string KeyOf(Guid documentId) => $"stock_document:{documentId}";

    // ================================================================================================================ reads

    internal static async Task<StockPostingView?> LoadByDocumentAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid documentId, bool replayed, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT p.{P.Id}, p.{P.DocType}, p.{P.EffectiveDate}, p.{P.PostedAtUtc}, p.{P.PostedBy}, p.{P.CostOperationId}, p.{P.AccountingVersionId}, p.{P.JournalEntryId}, p.{P.TotalValue}, j.{JE.Status}, " +
            $"rv.{RV.Id}, rv.{RV.CostOperationId}, rv.{RV.JournalAction}, rv.{RV.JournalEntryId}, rv.{RV.EffectiveDate}, rv.{RV.ReversedAtUtc}, rv.{RV.ReversedBy}, rv.{RV.Reason} " +
            $"FROM {P.Table} p LEFT JOIN {JE.Table} j ON j.{JE.Id} = p.{P.JournalEntryId} LEFT JOIN {RV.Table} rv ON rv.{RV.PostingId} = p.{P.Id} WHERE p.{P.DocumentId} = @d", conn, tx);
        q.Parameters.AddWithValue("d", documentId);
        Guid id, op, by; Guid? ver, jr; short type; short? jstatus; DateOnly date; DateTimeOffset at; decimal total; StockReversalView? reversal = null;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0); type = r.GetInt16(1); date = r.GetFieldValue<DateOnly>(2); at = r.GetFieldValue<DateTimeOffset>(3); by = r.GetGuid(4); op = r.GetGuid(5);
            ver = r.IsDBNull(6) ? null : r.GetGuid(6); jr = r.IsDBNull(7) ? null : r.GetGuid(7); total = r.GetDecimal(8); jstatus = r.IsDBNull(9) ? null : r.GetInt16(9);
            if (!r.IsDBNull(10))
                reversal = new StockReversalView(r.GetGuid(10), r.GetGuid(11), r.GetInt16(12), r.IsDBNull(13) ? null : r.GetGuid(13), r.GetFieldValue<DateOnly>(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetFieldValue<DateTimeOffset>(15), r.GetGuid(16), r.GetString(17));
        }
        var state = DeriveState(jr is not null, jstatus, reversal is not null);
        var lines = new List<StockPostingLineView>();
        await using (var lq = new NpgsqlCommand($"SELECT {PL.LineNo}, {PL.ItemId}, {PL.WarehouseId}, {PL.BaseQuantity}, {PL.Amount} FROM {PL.Table} WHERE {PL.PostingId} = @p ORDER BY {PL.LineNo}", conn, tx))
        {
            lq.Parameters.AddWithValue("p", id);
            await using var lr = await lq.ExecuteReaderAsync(ct);
            while (await lr.ReadAsync(ct)) lines.Add(new StockPostingLineView(lr.GetInt32(0), lr.GetGuid(1), lr.GetGuid(2), lr.GetDecimal(3), lr.GetDecimal(4)));
        }
        return new StockPostingView(id, documentId, type, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), at, by, op, ver, jr, total, (int)state, replayed, lines, jstatus, reversal);
    }

    /// <summary>The accounting state from the facts: reversal first, then no journal, then the journal's own status (1 draft, 2 pending approval, 3 approved, 4 posted, 5 reversed, 6 cancelled).</summary>
    public static StockAccountingState DeriveState(bool hasJournal, short? journalStatus, bool reversed) =>
        reversed ? StockAccountingState.Reversed
        : !hasJournal ? StockAccountingState.NotRequired
        : journalStatus switch { 4 => StockAccountingState.Posted, 5 or 6 => StockAccountingState.Failed, _ => StockAccountingState.Pending };

    public async Task<StockPostingView?> GetAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadByDocumentAsync(conn, null, documentId, false, ct);
    }

    // ================================================================================================================ post

    private static StockDocumentService.Abort Conflict(string code) => new(ConfigStatus.Conflict, code);

    private sealed class Replay(StockPostingView view) : Exception("replayed") { public StockPostingView View { get; } = view; }

    /// <summary>Validates the entered receipt amounts against the document lines: every line exactly once for a receipt, none otherwise.</summary>
    private static Dictionary<int, decimal> ReadAmounts(StockDocumentView doc, StockPostInput? input)
    {
        var supplied = input?.Lines ?? [];
        if (doc.DocType != (int)StockDocType.Receipt)
        {
            if (supplied.Count > 0) throw StockDocumentService.Invalid("lines", "amounts_not_allowed");
            return [];
        }
        var map = new Dictionary<int, decimal>();
        for (var i = 0; i < supplied.Count; i++)
        {
            var l = supplied[i];
            if (l.LineNo is not { } no || doc.Lines.All(x => x.LineNo != no)) throw StockDocumentService.Invalid($"lines[{i}].lineNo", "line_not_found");
            if (l.Amount is not { } amount) throw StockDocumentService.Invalid($"lines[{i}].amount", "required");
            if (amount < 0) throw StockDocumentService.Invalid($"lines[{i}].amount", "negative_amount");
            if (!map.TryAdd(no, amount)) throw StockDocumentService.Invalid($"lines[{i}].lineNo", "duplicate_line");
        }
        foreach (var l in doc.Lines) if (!map.ContainsKey(l.LineNo)) throw StockDocumentService.Invalid("lines", "amount_required_for_every_line");
        return map;
    }

    public async Task<ConfigResult<StockPostingView>> PostAsync(Guid actor, Guid companyId, Guid documentId, long? expectedRevision, StockPostInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<StockPostingView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        try
        {
            return ConfigResult<StockPostingView>.Ok(await transactions.ExecuteAsync(companyId, work => PostCoreAsync(work, actor, documentId, expectedRevision.Value, input, ct), ct));
        }
        catch (Replay r) { return ConfigResult<StockPostingView>.Ok(r.View); }
        catch (StockDocumentService.Abort a) { return a.To<StockPostingView>(); }
        catch (CostingException c) when (c.Code is "amount_precision_exceeded" or "invalid_amount") { return ConfigResult<StockPostingView>.Invalid([new FieldError("lines", c.Code)]); }
        catch (CostingException c) { return ConfigResult<StockPostingView>.Fail(ConfigStatus.Conflict, c.Code); }
        catch (JournalService.Abort j) { return j.To<StockPostingView>(); }
        catch (InvalidOperationException e) when (e.Message is "inventory_accounting_not_configured" or "inventory_account_not_configured") { return ConfigResult<StockPostingView>.Fail(ConfigStatus.Conflict, e.Message); }
    }

    /// <summary>
    /// The posting INSIDE an ambient company transaction supplied by the caller (Phase 3.13 S3: an invoice approval executes its stock document in the SAME transaction as the approval), the same refactor
    /// pattern as <see cref="JournalService.CancelInWorkAsync"/> / <see cref="JournalService.ReverseInWorkAsync"/>: validation, locks, costing, journal creation and audit are exactly those of <see cref="PostAsync"/>
    /// (this is the same code), only the transaction owner differs. The caller owns the commit; nothing is committed here.
    /// <para><b>Atomic on its own:</b> the posting runs under a SAVEPOINT. The posting writes in steps (costing first, then the accounting configuration and the journal) and some refusals are thrown AFTER writes
    /// (for example <c>inventory_accounting_not_configured</c> after the issue was costed, <c>negative_exception_stale</c>, a later journal refusal). On ANY exception other than a replay the savepoint is rolled back
    /// (undoing every write and the audit receipts of the failed attempt, and clearing an aborted-transaction state left by a PostgreSQL error) and the exception is rethrown, so a caller that catches a refusal
    /// and carries on in its own transaction never commits a partial stock or cost effect. On success the savepoint is released.</para>
    /// <para>Refusals are thrown as the same exceptions <see cref="PostAsync"/> maps to a result (<see cref="StockDocumentService.Abort"/>, <see cref="CostingException"/>, <see cref="JournalService.Abort"/>,
    /// <see cref="InvalidOperationException"/> for a missing accounting configuration): the caller maps them as it maps its own, or lets them abort its transaction. No authorization here: the caller authorizes.</para>
    /// <para><b>Replay:</b> a repeated call for an already posted document returns the EXISTING posting (<c>Replayed</c> true) whoever posted it and writes nothing; the caller must check that the stock document belongs
    /// to its own business document (invoice) before relying on it.</para>
    /// <para><b>Lock order (deadlock avoidance):</b> the posting first takes the company's branch-change advisory lock (<c>CompanyBranchService.LockKey1/LockKey2</c>, "HBRC"), then row locks on the document and the
    /// stock positions, and the audit stream lock last. A caller that already holds row locks of its own must therefore call this EARLY (before taking them) or take that advisory lock FIRST; taking its row locks
    /// first and the advisory lock afterwards inverts the order against a concurrent posting or branch change and can deadlock (PostgreSQL 40P01).</para>
    /// </summary>
    internal async Task<StockPostingView> PostInWorkAsync(CompanyAuditedWork work, Guid actor, Guid documentId, long expectedRevision, StockPostInput? input, CancellationToken ct = default)
    {
        var savepoint = "hasm_post_inwork_" + Guid.NewGuid().ToString("N");            // unique, so nested or repeated calls in one transaction never clash
        var mark = work.ReceiptMark;
        await work.Transaction.SaveAsync(savepoint, ct);
        try
        {
            var view = await PostCoreAsync(work, actor, documentId, expectedRevision, input, ct);
            await work.Transaction.ReleaseAsync(savepoint, ct);
            return view;
        }
        catch (Replay r)
        {
            // the replay check precedes every write: nothing was written and the ambient transaction is untouched
            await work.Transaction.ReleaseAsync(savepoint, ct);
            return r.View;
        }
        catch
        {
            // undo every write of the failed attempt (also clears an aborted state after a database error), forget its audit receipts, and let the exception reach the caller
            try { await work.Transaction.RollbackAsync(savepoint, CancellationToken.None); work.DiscardReceiptsSince(mark); }
            catch (Exception) { /* the connection is unusable: the original exception still propagates and the caller's transaction ends without a commit */ }
            throw;
        }
    }

    /// <summary>The body of the posting transaction (formerly the lambda of <see cref="PostAsync"/>, unchanged): throws <see cref="Replay"/> for an already posted document, so that the public entry ends its own transaction without a mutation.</summary>
    private async Task<StockPostingView> PostCoreAsync(CompanyAuditedWork work, Guid actor, Guid documentId, long expectedRevision, StockPostInput? input, CancellationToken ct)
    {
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
        {
            lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
            lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
            await lk.ExecuteNonQueryAsync(ct);
        }
        var doc = await StockDocumentService.Load(work.Connection, work.Transaction, documentId, true, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "document_not_found");
        // a repeated request returns the original posting (idempotent retry after a lost response, also without resending the amounts); amounts that differ from the original are a conflict, never a second posting
        if (await LoadByDocumentAsync(work.Connection, work.Transaction, documentId, true, ct) is { } existing)
        {
            if (doc.DocType == (int)StockDocType.Receipt && input?.Lines is { Count: > 0 })
            {
                var again = ReadAmounts(doc, input);
                if (existing.Lines.Any(l => !again.TryGetValue(l.LineNo, out var a) || a != l.Amount)) throw Conflict("already_posted");
            }
            throw new Replay(existing);                              // nothing was written: the transaction ends without a mutation
        }
        var amounts = ReadAmounts(doc, input);
        if (doc.Status != (int)StockDocStatus.Approved) throw Conflict("document_not_approved");
        if (expectedRevision != doc.Revision) throw new StockDocumentService.Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, doc.Revision);
        return await ExecuteAsync(work, actor, doc, amounts, ct);
    }

    private async Task<StockPostingView> ExecuteAsync(CompanyAuditedWork work, Guid actor, StockDocumentView doc, IReadOnlyDictionary<int, decimal> amounts, CancellationToken ct)
    {
        var type = (StockDocType)doc.DocType;
        var date = DateOnly.ParseExact(doc.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // 1. the document is checked again against the live data: nothing that was valid at approval is trusted
        var header = new StockDocumentValidation.Header(type, doc.SourceWarehouseId, doc.TargetWarehouseId, date, doc.Reference, doc.Notes);
        await StockDocumentService.ResolveWarehouses(work, header, ct);
        var resolved = await StockDocumentService.ResolveLines(work, [.. doc.Lines.Select(l => new StockDocumentValidation.Line(l.ItemId, l.UnitId, l.Quantity, l.Notes))], ct);
        for (var i = 0; i < resolved.Count; i++)
            if (resolved[i].Base != doc.Lines[i].BaseQuantity) throw Conflict("line_conversion_changed");
        var gate = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, date, false, ct);
        if (!gate.Allowed) throw Conflict(gate.Reason!);
        var source = new CostingSource("stock_document", doc.Id);
        var key = KeyOf(doc.Id);
        var warehouse = (type == StockDocType.Receipt ? doc.TargetWarehouseId : doc.SourceWarehouseId)!.Value;
        // 2. accounts first (cheap and decisive): a receipt or issue with value needs the configuration; a transfer inside one inventory account and zero-value movements need none
        var groups = await ItemGroupsAsync(work, doc.Lines.Select(l => l.ItemId), ct);
        IReadOnlyDictionary<Guid, ResolvedInventoryAccounts>? config = null;
        // 3. the stock and its valuation
        CostingResult result;
        var postingLines = new List<StockPostingLineView>();
        NegativeIssue? negative = null;
        NegativeReceipt? settling = null;
        IReadOnlyList<StockPostingLineView>? journalLines = null;                       // the lines the journal books when they differ from what was posted (negative-stock exception, Phase 3.8B-3)
        if (type == StockDocType.Receipt)
        {
            // Phase 3.8B-3: open obligations of these positions are settled first (oldest first); only the rest of each line enters the position
            settling = await PrepareSettlementAsync(work, doc, warehouse, date, amounts, ct);
            var request = settling is null
                ? new CostingReceiptRequest(key, date, source, [.. doc.Lines.Select(l => new CostingReceiptLine(l.ItemId, warehouse, l.BaseQuantity, amounts[l.LineNo]))])
                : new CostingReceiptRequest(key, date, source, [.. settling.Remainders.Select(r => new CostingReceiptLine(r.ItemId, warehouse, r.Quantity, r.Amount))], settling.Remainders.Count == 0, settling.Positions);
            // accounting configuration is needed before the stock moves only when there is value to book
            if (doc.Lines.Sum(l => amounts[l.LineNo]) > 0) config = await EnsureConfiguredAsync(work, groups.Values.Distinct(), true, ct);
            result = await costing.ReceiveAsync(work, actor, request, ct);
            for (var i = 0; i < doc.Lines.Count; i++) postingLines.Add(new StockPostingLineView(doc.Lines[i].LineNo, doc.Lines[i].ItemId, warehouse, doc.Lines[i].BaseQuantity, settling is null ? result.Lines[i].Amount : amounts[doc.Lines[i].LineNo]));
            if (settling is not null)
                journalLines = [.. settling.Remainders.Select((r, i) => new StockPostingLineView(r.LineNo, r.ItemId, warehouse, r.Quantity, result.Lines[i].Amount))];
        }
        else if (type == StockDocType.Issue)
        {
            // Phase 3.8B-3: an issue above the stock of an eligible item may use the approved exception; otherwise the engine refuses it exactly as before (insufficient_stock)
            negative = await PrepareNegativeIssueAsync(work, actor, doc, ct);
            if (negative is null)
            {
                result = await costing.IssueAsync(work, actor, new CostingIssueRequest(key, date, source, [.. doc.Lines.Select(l => new CostingIssueLine(l.ItemId, warehouse, l.BaseQuantity))]), ct);
                for (var i = 0; i < doc.Lines.Count; i++) postingLines.Add(new StockPostingLineView(doc.Lines[i].LineNo, doc.Lines[i].ItemId, warehouse, doc.Lines[i].BaseQuantity, result.Lines[i].Amount));
                if (result.Lines.Sum(l => l.Amount) > 0) config = await EnsureConfiguredAsync(work, groups.Values.Distinct(), false, ct);
            }
            else
            {
                var covered = negative.Plan.Lines.Where(l => l.Covered > 0).ToList();
                result = await costing.IssueAsync(work, actor, new CostingIssueRequest(key, date, source, [.. covered.Select(l => new CostingIssueLine(l.ItemId, warehouse, l.Covered))], covered.Count == 0), ct);
                var coveredAmount = covered.Select((l, i) => (l.LineNo, result.Lines[i].Amount)).ToDictionary(x => x.LineNo, x => x.Amount);
                foreach (var l in negative.Plan.Lines) postingLines.Add(new StockPostingLineView(l.LineNo, l.ItemId, warehouse, l.Quantity, coveredAmount.GetValueOrDefault(l.LineNo) + (l.ProvisionalTotal ?? 0m)));
                journalLines = [.. negative.Plan.Lines.Select(l => new StockPostingLineView(l.LineNo, l.ItemId, warehouse, l.Covered, coveredAmount.GetValueOrDefault(l.LineNo)))];
                config = await EnsureConfiguredAsync(work, groups.Values.Distinct(), false, ct);
                if (config.Values.First().VersionId != negative.Plan.AccountingVersionId) throw Conflict("negative_exception_stale");
            }
        }
        else
        {
            // the engine refuses duplicate items in one transfer, so equal items of the document are summed first (the sum is exact decimal arithmetic)
            var perItem = doc.Lines.GroupBy(l => l.ItemId).Select(g => new CostingTransferLine(g.Key, g.Sum(l => l.BaseQuantity))).ToList();
            result = await costing.TransferAsync(work, actor, new CostingTransferRequest(key, date, source, doc.SourceWarehouseId!.Value, doc.TargetWarehouseId!.Value, perItem), ct);
            var n = 0;
            foreach (var o in result.Lines.Where(l => l.Direction < 0)) postingLines.Add(new StockPostingLineView(++n, o.ItemId, o.WarehouseId, o.Quantity, o.Amount));
        }
        var total = type == StockDocType.Transfer ? result.Lines.Where(l => l.Direction < 0).Sum(l => l.Amount) : result.NetValueChange < 0 ? -result.NetValueChange : result.NetValueChange;
        if (negative is not null || settling is not null) total = postingLines.Sum(l => l.Amount);         // the document's whole value: the covered cost plus the provisional value / every entered receipt amount
        var bookable = journalLines ?? postingLines;
        // 4. the accounting side: one balanced draft journal entry, or an explicit "no journal required"
        Guid? journalId = null; Guid? versionId = settling is not null && config is not null ? config.Values.First().VersionId : null;
        var state = StockAccountingState.NotRequired;
        if (type != StockDocType.Transfer && (negative is not null || bookable.Sum(l => l.Amount) > 0))
        {
            var plan = PlanJournal(doc, type, groups, config!, bookable, negative is null ? null : (negative.Plan.ObligationAccountId!.Value, negative.Plan.Lines.Sum(l => l.ProvisionalTotal ?? 0m)));
            versionId = plan.VersionId;
            var entry = await journal.CreateInWorkAsync(work, actor, new JournalInput(date, $"Stock {(type == StockDocType.Receipt ? "receipt" : "issue")} {doc.Number}", doc.BranchId, plan.Lines, null), false, DocumentSource.Automatic, key, ct);
            journalId = entry.Header.Id;
            state = StockAccountingState.Pending;
        }
        // 5. the durable record, its first accounting state and the audit event, in the same transaction
        var postingId = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {P.Table} ({P.Id}, {P.DocumentId}, {P.DocType}, {P.EffectiveDate}, {P.PostedAtUtc}, {P.PostedBy}, {P.CostOperationId}, {P.AccountingVersionId}, {P.JournalEntryId}, {P.TotalValue}) VALUES (@id, @d, @t, @e, @at, @by, @op, @v, @j, @tv)", work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("id", postingId); ins.Parameters.AddWithValue("d", doc.Id); ins.Parameters.AddWithValue("t", (short)doc.DocType); ins.Parameters.AddWithValue("e", date);
            ins.Parameters.AddWithValue("at", Now); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("op", result.OperationId);
            ins.Parameters.AddWithValue("v", (object?)versionId ?? DBNull.Value); ins.Parameters.AddWithValue("j", (object?)journalId ?? DBNull.Value); ins.Parameters.AddWithValue("tv", total);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var l in postingLines)
        {
            await using var li = new NpgsqlCommand($"INSERT INTO {PL.Table} ({PL.PostingId}, {PL.LineNo}, {PL.ItemId}, {PL.WarehouseId}, {PL.BaseQuantity}, {PL.Amount}) VALUES (@p, @n, @i, @w, @q, @a)", work.Connection, work.Transaction);
            li.Parameters.AddWithValue("p", postingId); li.Parameters.AddWithValue("n", l.LineNo); li.Parameters.AddWithValue("i", l.ItemId); li.Parameters.AddWithValue("w", l.WarehouseId);
            li.Parameters.AddWithValue("q", l.BaseQuantity); li.Parameters.AddWithValue("a", l.Amount);
            await li.ExecuteNonQueryAsync(ct);
        }
        await using (var ev = new NpgsqlCommand($"INSERT INTO {PE.Table} ({PE.PostingId}, {PE.Kind}, {PE.JournalEntryId}, {PE.AtUtc}, {PE.ActorUserId}, {PE.Note}) VALUES (@p, @k, @j, @at, @a, @n)", work.Connection, work.Transaction))
        {
            ev.Parameters.AddWithValue("p", postingId); ev.Parameters.AddWithValue("k", (short)state); ev.Parameters.AddWithValue("j", (object?)journalId ?? DBNull.Value);
            ev.Parameters.AddWithValue("at", Now); ev.Parameters.AddWithValue("a", actor);
            ev.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Varchar) { Value = state == StockAccountingState.Pending ? "draft journal entry created, awaiting approval and posting" : "no journal entry required" });
            await ev.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(StockPostingAuditEvents.Posted, ActorUserId: actor, SubjectId: doc.Id,
            Detail: $"stock document posted number={doc.Number} type={doc.DocType} value={total.ToString(CultureInfo.InvariantCulture)} accounting={(state == StockAccountingState.Pending ? "pending" : "not_required")}"), ct);
        // 6. Phase 3.8B-3: the obligations of the uncovered parts, or the settlements of the open obligations this receipt matched, in the same transaction
        if (negative is not null) await RecordObligationsAsync(work, actor, doc, negative, postingId, result.OperationId, journalId!.Value, ct);
        if (settling is not null) await RecordSettlementsAsync(work, actor, doc, settling, postingId, result.OperationId, config, date, ct);
        return (await LoadByDocumentAsync(work.Connection, work.Transaction, doc.Id, false, ct))!;
    }

    internal static async Task<Dictionary<Guid, Guid>> ItemGroupsAsync(CompanyAuditedWork work, IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Guid>();
        await using var q = new NpgsqlCommand($"SELECT {I.Id}, {I.GroupId} FROM {I.Table} WHERE {I.Id} = ANY(@ids)", work.Connection, work.Transaction);
        q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = itemIds.Distinct().ToArray() });
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetGuid(0)] = r.GetGuid(1);
        return map;
    }

    /// <summary>
    /// Resolves the accounting configuration ONCE for the posting and refuses with a stable code unless every item group resolves to a usable inventory account, the counter account of the document kind is configured and usable
    /// (checked against the live chart) and all groups were resolved under the SAME configuration version (a configuration saved in between is refused with <c>accounting_configuration_changed</c> and the posting can simply be repeated).
    /// The result is used for the journal, so the validated accounts are exactly the accounts booked.
    /// </summary>
    private static Task<IReadOnlyDictionary<Guid, ResolvedInventoryAccounts>> EnsureConfiguredAsync(CompanyAuditedWork work, IEnumerable<Guid> groupIds, bool receipt, CancellationToken ct) =>
        EnsureConfiguredAsync(work, groupIds, receipt ? a => a.ReceiptCounterAccountId : a => a.IssueCounterAccountId, receipt ? "receipt_counter_account_not_configured" : "issue_counter_account_not_configured", ct);

    /// <summary>The same resolution for any counter account (Phase 3.8B-2 uses it with the adjustment counter account, refused with <paramref name="missing"/> when that account is not configured).</summary>
    internal static async Task<IReadOnlyDictionary<Guid, ResolvedInventoryAccounts>> EnsureConfiguredAsync(CompanyAuditedWork work, IEnumerable<Guid> groupIds, Func<ResolvedInventoryAccounts, Guid?> counterOf, string missing, CancellationToken ct)
    {
        var resolved = new Dictionary<Guid, ResolvedInventoryAccounts>();
        foreach (var g in groupIds) resolved[g] = await InventoryAccountingService.ResolveAsync(work, g, ct);
        var accounts = resolved.Values.ToList();
        if (accounts.Select(a => a.VersionId).Distinct().Count() > 1) throw Conflict("accounting_configuration_changed");
        var counter = counterOf(accounts[0]);
        if (counter is null) throw Conflict(missing);
        if (await InventoryAccountingService.ValidateForPostingAsync(work, [.. accounts.Select(a => a.InventoryAccountId).Distinct()], counter, ct) is { } bad) throw Conflict(bad);
        return resolved;
    }

    private sealed record JournalPlan(Guid VersionId, IReadOnlyList<JournalLineInput> Lines);

    /// <summary>Receipt: debit the inventory account of each item group by the entered value, credit the receipt counter account by the total. Issue: debit the issue counter account by the costed total, credit each inventory account. Pure: uses the configuration resolved once for the posting.</summary>
    private static JournalPlan PlanJournal(StockDocumentView doc, StockDocType type, IReadOnlyDictionary<Guid, Guid> groupOfItem, IReadOnlyDictionary<Guid, ResolvedInventoryAccounts> config, IReadOnlyList<StockPostingLineView> lines,
        (Guid Account, decimal Amount)? obligation = null)
    {
        if (obligation is { } o)
        {
            // Phase 3.8B-3 (5.1): Dr issue counter K by the covered cost plus the provisional value / Cr each inventory account by its covered cost / Cr the obligation account O by the provisional value
            var inventory = new SortedDictionary<Guid, decimal>();
            ResolvedInventoryAccounts? first = null;
            foreach (var l in lines)
            {
                var r = config[groupOfItem[l.ItemId]];
                first ??= r;
                if (l.Amount > 0) inventory[r.InventoryAccountId] = inventory.GetValueOrDefault(r.InventoryAccountId) + l.Amount;
            }
            JournalLineInput L(Guid account, decimal? debit, decimal? credit) => new(account, null, null, debit, credit, doc.BranchId, null, null, null);
            var result = new List<JournalLineInput> { L(first!.IssueCounterAccountId!.Value, inventory.Values.Sum() + o.Amount, null) };
            foreach (var (account, amount) in inventory) result.Add(L(account, null, amount));
            result.Add(L(o.Account, null, o.Amount));
            return new JournalPlan(first.VersionId, result);
        }
        var perAccount = new SortedDictionary<Guid, decimal>();
        ResolvedInventoryAccounts? any = null;
        foreach (var l in lines.Where(x => x.Amount > 0))
        {
            var r = config[groupOfItem[l.ItemId]];
            any ??= r;
            perAccount[r.InventoryAccountId] = perAccount.GetValueOrDefault(r.InventoryAccountId) + l.Amount;
        }
        var counter = (type == StockDocType.Receipt ? any!.ReceiptCounterAccountId : any!.IssueCounterAccountId)!.Value;
        var total = perAccount.Values.Sum();
        var journalLines = new List<JournalLineInput>();
        JournalLineInput Line(Guid account, decimal? debit, decimal? credit) => new(account, null, null, debit, credit, doc.BranchId, null, null, null);
        if (type == StockDocType.Receipt)
        {
            foreach (var (account, amount) in perAccount) journalLines.Add(Line(account, amount, null));
            journalLines.Add(Line(counter, null, total));
        }
        else
        {
            journalLines.Add(Line(counter, total, null));
            foreach (var (account, amount) in perAccount) journalLines.Add(Line(account, null, amount));
        }
        return new JournalPlan(any!.VersionId, journalLines);
    }
}

/// <summary>Authorized entry point: <c>company.stockdocs.post</c> on EVERY warehouse the document involves (and the right to see the document at all, so a document cannot be probed).</summary>
public sealed class StockPostingManagementService(Hasm.Infrastructure.Access.AccessService access, StockDocumentService documents, StockPostingService posting)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private static IEnumerable<Guid> Involved(StockDocumentView d) => new[] { d.SourceWarehouseId, d.TargetWarehouseId }.Where(x => x is not null).Select(x => x!.Value).Distinct();

    private async Task<bool> AllAsync(Guid user, Guid company, string code, IEnumerable<Guid> warehouses, CancellationToken ct)
    {
        var any = false;
        foreach (var w in warehouses)
        {
            any = true;
            if (!await access.IsAllowedAsync(user, company, code, Hasm.Infrastructure.Access.ScopeTarget.Warehouse(w), ct)) return false;
        }
        return any;
    }

    private async Task<bool> AnyAsync(Guid user, Guid company, string code, IEnumerable<Guid> warehouses, CancellationToken ct)
    {
        foreach (var w in warehouses) if (await access.IsAllowedAsync(user, company, code, Hasm.Infrastructure.Access.ScopeTarget.Warehouse(w), ct)) return true;
        return false;
    }

    public async Task<ConfigResult<StockPostingView>> PostAsync(Guid user, Guid company, Guid documentId, long? expectedRevision, StockPostInput? input, CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(company, documentId, ct);
        if (doc is null) return Denied<StockPostingView>();   // never reveals whether the document exists to someone who cannot see it
        if (!await AnyAsync(user, company, Hasm.Infrastructure.Access.PermissionCatalog.Codes.StockDocsView, Involved(doc), ct)) return Denied<StockPostingView>();
        if (!await AllAsync(user, company, Hasm.Infrastructure.Access.PermissionCatalog.Codes.StockDocsPost, Involved(doc), ct)) return Denied<StockPostingView>();
        return await posting.PostAsync(user, company, documentId, expectedRevision, input, ct);
    }

    /// <summary>Reversal of an executed document: <c>company.stockdocs.reverse</c> on EVERY involved warehouse plus the right to see the document.</summary>
    public async Task<ConfigResult<StockPostingView>> ReverseAsync(Guid user, Guid company, Guid documentId, StockReverseInput? input, CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(company, documentId, ct);
        if (doc is null) return Denied<StockPostingView>();
        if (!await AnyAsync(user, company, Hasm.Infrastructure.Access.PermissionCatalog.Codes.StockDocsView, Involved(doc), ct)) return Denied<StockPostingView>();
        if (!await AllAsync(user, company, Hasm.Infrastructure.Access.PermissionCatalog.Codes.StockDocsReverse, Involved(doc), ct)) return Denied<StockPostingView>();
        return await posting.ReverseAsync(user, company, documentId, input, ct);
    }

    /// <summary>The posting of a document (null when it has not been executed); needs the right to see the document.</summary>
    public async Task<ConfigResult<StockPostingView?>> GetAsync(Guid user, Guid company, Guid documentId, CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(company, documentId, ct);
        if (doc is null || !await AnyAsync(user, company, Hasm.Infrastructure.Access.PermissionCatalog.Codes.StockDocsView, Involved(doc), ct)) return Denied<StockPostingView?>();
        return ConfigResult<StockPostingView?>.Ok(await posting.GetAsync(company, documentId, ct));
    }
}
