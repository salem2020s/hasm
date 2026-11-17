using System.Globalization;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using PE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingEvent;
using RV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;

namespace Hasm.Infrastructure.Inventory;

public sealed partial class StockPostingService
{
    public static string ReversalKeyOf(Guid documentId) => $"stock_document_reversal:{documentId}";

    internal static async Task<Guid?> PeriodOfAsync(CompanyAuditedWork work, DateOnly date, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {FP.Id} FROM {FP.Table} WHERE {FP.StartDate} <= @d AND {FP.EndDate} >= @d", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("d", date);
        return await q.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>
    /// Reverses an EXECUTED document (Phase 3.7C) in one transaction: the costing engine takes the stock effect back with compensating entries (an outflow gets back exactly what it consumed; an inflow is taken out again,
    /// which is only possible while that stock is still there untouched, otherwise the engine refuses and nothing changes), and the accounting side is handled by what it is: a journal entry that was never posted is cancelled,
    /// a posted one gets a draft reversal entry that follows the ordinary approval and posting workflow (the ledger is never edited). The effective date must lie in an open fiscal period and not before the posting date.
    /// A posting is reversed at most once; repeating the identical request returns the original reversal. The document itself is never cancelled or edited: a correction is a new document.
    /// </summary>
    public async Task<ConfigResult<StockPostingView>> ReverseAsync(Guid actor, Guid companyId, Guid documentId, StockReverseInput? input, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        DateOnly date = default;
        if (string.IsNullOrWhiteSpace(input?.EffectiveDate)) errors.Add(new("effectiveDate", "required"));
        else if (!DateOnly.TryParseExact(input.EffectiveDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) errors.Add(new("effectiveDate", "invalid_date"));
        var reason = input?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) errors.Add(new("reason", "required"));
        else if (reason.Length > 500) errors.Add(new("reason", "too_long"));
        else if (reason.Any(char.IsControl)) errors.Add(new("reason", "invalid_characters"));
        if (errors.Count > 0) return ConfigResult<StockPostingView>.Invalid(errors);
        try
        {
            return ConfigResult<StockPostingView>.Ok(await transactions.ExecuteAsync(companyId, async work =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
                {
                    lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
                    lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                var doc = await StockDocumentService.Load(work.Connection, work.Transaction, documentId, true, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "document_not_found");
                var posting = await LoadByDocumentAsync(work.Connection, work.Transaction, documentId, false, ct) ?? throw Conflict("document_not_executed");
                if (posting.Reversal is { } done)
                {
                    if (done.EffectiveDate == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) && done.Reason == reason) throw new Replay(posting with { Replayed = true });
                    throw Conflict("already_reversed");
                }
                var postedOn = DateOnly.ParseExact(posting.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (date < postedOn) throw StockDocumentService.Invalid("effectiveDate", "before_posting_date");
                var gate = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, date, false, ct);
                if (!gate.Allowed) throw Conflict(gate.Reason!);
                // Phase 3.8B-3: the obligations of an issue, or the settlements of a receipt, are checked before anything is written; settlement journals follow the 3.7C rule like the document's own journal
                var negative = await PrepareReversalAsync(work, posting, ct);
                var settlementJournals = negative is { Settlements.Count: > 0 } ? await ReverseSettlementJournalsAsync(work, actor, doc, negative, postedOn, date, ct) : [];
                // the stock effect first (the engine refuses when the goods were consumed or later movements exist), then the accounting side
                var operation = await costing.ReverseAsync(work, actor, new CostingReversalRequest(ReversalKeyOf(documentId), KeyOf(documentId), date, new CostingSource("stock_document_reversal", documentId), negative?.Aware), ct);
                short action = 0; Guid? journalRef = null;
                if (posting.JournalEntryId is { } jid)
                {
                    var view = await JournalService.LoadViewAsync(work.Connection, work.Transaction, jid, true, ct) ?? throw Conflict("journal_not_found");
                    var description = $"Reversal of stock document {doc.Number}";
                    // while the journal entry is not posted the stock movement is not in the ledger, so the costing reversal must stay in the SAME fiscal period as the posting (otherwise the posting period would show a
                    // valuation difference forever); a posted entry is reversed by a draft reversal entry, so a later date is fine
                    if (view.Header.Status is not (JournalStatus.Posted or JournalStatus.Reversed) && await PeriodOfAsync(work, postedOn, ct) != await PeriodOfAsync(work, date, ct))
                        throw StockDocumentService.Invalid("effectiveDate", "must_stay_in_posting_period");
                    switch (view.Header.Status)
                    {
                        case JournalStatus.Draft or JournalStatus.PendingApproval or JournalStatus.Approved:
                            await journal.CancelInWorkAsync(work, actor, jid, view.Header.Revision, ct);
                            action = 1; journalRef = jid;
                            break;
                        case JournalStatus.Posted:
                            var reversal = await journal.ReverseInWorkAsync(work, actor, jid, view.Header.Revision, new ReversalInput(date, description), description, ct);
                            action = 2; journalRef = reversal.Header.Id;
                            break;
                        case JournalStatus.Cancelled or JournalStatus.Reversed:
                            action = 1; journalRef = jid;                                  // already cancelled or reversed outside the stock reversal: there is no accounting effect left to undo, and the stock side can now be repaired
                            break;
                        default:
                            throw Conflict("journal_state_not_reversible");
                    }
                }
                var id = Guid.NewGuid();
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {RV.Table} ({RV.Id}, {RV.PostingId}, {RV.CostOperationId}, {RV.JournalAction}, {RV.JournalEntryId}, {RV.EffectiveDate}, {RV.ReversedAtUtc}, {RV.ReversedBy}, {RV.Reason}) VALUES (@id, @p, @op, @a, @j, @e, @at, @by, @r)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("p", posting.Id); ins.Parameters.AddWithValue("op", operation.OperationId); ins.Parameters.AddWithValue("a", action);
                    ins.Parameters.AddWithValue("j", (object?)journalRef ?? DBNull.Value); ins.Parameters.AddWithValue("e", date); ins.Parameters.AddWithValue("at", Now); ins.Parameters.AddWithValue("by", actor);
                    ins.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Varchar) { Value = reason });
                    await ins.ExecuteNonQueryAsync(ct);
                }
                if (negative is not null) await RecordNegativeReversalAsync(work, actor, doc, negative, id, operation.OperationId, date, settlementJournals, ct);
                await using (var ev = new NpgsqlCommand($"INSERT INTO {PE.Table} ({PE.PostingId}, {PE.Kind}, {PE.JournalEntryId}, {PE.AtUtc}, {PE.ActorUserId}, {PE.Note}) VALUES (@p, 3, @j, @at, @a, @n)", work.Connection, work.Transaction))
                {
                    ev.Parameters.AddWithValue("p", posting.Id); ev.Parameters.AddWithValue("j", (object?)journalRef ?? DBNull.Value); ev.Parameters.AddWithValue("at", Now); ev.Parameters.AddWithValue("a", actor);
                    ev.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Varchar) { Value = reason });
                    await ev.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(StockPostingAuditEvents.Reversed, ActorUserId: actor, SubjectId: documentId,
                    Detail: $"stock document reversed number={doc.Number} journalAction={action} value={posting.TotalValue.ToString(CultureInfo.InvariantCulture)}"), ct);
                return (await LoadByDocumentAsync(work.Connection, work.Transaction, documentId, false, ct))!;
            }, ct));
        }
        catch (Replay r) { return ConfigResult<StockPostingView>.Ok(r.View); }
        catch (StockDocumentService.Abort a) { return a.To<StockPostingView>(); }
        catch (CostingException c) { return ConfigResult<StockPostingView>.Fail(ConfigStatus.Conflict, c.Code); }
        catch (JournalService.Abort j) { return j.To<StockPostingView>(); }
    }
}
