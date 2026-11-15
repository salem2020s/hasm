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

namespace Hasm.Infrastructure.Inventory;

public sealed partial class StockPostingService
{
    public static string ReversalKeyOf(Guid documentId) => $"stock_document_reversal:{documentId}";

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
                // the stock effect first (the engine refuses when the goods were consumed or later movements exist), then the accounting side
                var operation = await costing.ReverseAsync(work, actor, new CostingReversalRequest(ReversalKeyOf(documentId), KeyOf(documentId), date, new CostingSource("stock_document_reversal", documentId)), ct);
                short action = 0; Guid? journalRef = null;
                if (posting.JournalEntryId is { } jid)
                {
                    var view = await JournalService.LoadViewAsync(work.Connection, work.Transaction, jid, true, ct) ?? throw Conflict("journal_not_found");
                    var description = $"Reversal of stock document {doc.Number}";
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
                        case JournalStatus.Cancelled:
                            action = 1; journalRef = jid;                                  // already cancelled outside: there is no accounting effect left to undo
                            break;
                        default:
                            throw Conflict("journal_already_reversed");
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
