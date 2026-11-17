using System.Globalization;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using NpgsqlTypes;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustment;
using AL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentLine;
using AP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPosting;
using APL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPostingLine;
using AR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentReversal;
using S = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Stocktake;
using I = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using U = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Unit;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;

namespace Hasm.Infrastructure.Inventory;

/// <summary>Business audit events of stock adjustments: 1163/1164 next to the stock posting events (1161/1162), 1178 next to the stocktake life cycle (1170-1177).</summary>
public static class StockAdjustmentAuditEvents
{
    public const short Posted = 1163;
    public const short Reversed = 1164;
    public const short Generated = 1178;
}

/// <summary>One line of an adjustment: <see cref="Direction"/> +1 surplus (inflow) / -1 shortage (outflow); <see cref="Quantity"/> = |counted - book| in the base unit; <see cref="Amount"/> is the value booked once posted.</summary>
public sealed record StockAdjustmentLineView(int LineNo, Guid ItemId, string ItemCode, string? ItemNameAr, string? ItemNameEn, Guid UnitId, string UnitCode, int Direction, decimal Quantity, decimal CountedQuantity,
    decimal BookQuantity, string? Reason, decimal? Amount);

public sealed record StockAdjustmentReversalView(Guid Id, Guid? IssueOperationId, Guid? ReceiptOperationId, int JournalAction, Guid? JournalEntryId, string EffectiveDate, DateTimeOffset ReversedAtUtc, Guid ReversedBy, string Reason);

/// <summary>The execution of an adjustment: its costing operations (shortages issued, surpluses received), the configuration version and journal entry used, and the DERIVED accounting state (<see cref="StockAccountingState"/>).</summary>
public sealed record StockAdjustmentPostingView(Guid Id, string EffectiveDate, DateTimeOffset PostedAtUtc, Guid PostedBy, Guid? IssueOperationId, Guid? ReceiptOperationId, Guid? AccountingVersionId, Guid? JournalEntryId,
    int? JournalStatus, decimal TotalValue, int AccountingState, StockAdjustmentReversalView? Reversal);

public sealed record StockAdjustmentView(Guid Id, Guid StocktakeId, string? StocktakeNumber, Guid BranchId, Guid WarehouseId, string AdjustmentDate, string Number, Guid FiscalYearId, Guid CreatedBy, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<StockAdjustmentLineView> Lines, StockAdjustmentPostingView? Posting, bool Replayed = false);

/// <summary>
/// Stock adjustments generated from an APPROVED stocktake (Phase 3.8B-2). <b>No authorization here by design:</b> <see cref="StockAdjustmentManagementService"/> wraps it.
/// <para><b>Generate</b>: one adjustment per stocktake (unique in PostgreSQL), dated on the count date in the stocktake's warehouse, one line per item whose variance against the CURRENT book quantity (costing ledger,
/// that warehouse, effective on or before the count date, read at generation; never the frozen snapshot of the sheet) is not zero. It is born approved (its content is fully determined by the approved count, whose
/// separation of duties already held) and numbered (<c>stock_adjustment</c>, default prefix SA). It moves nothing.</para>
/// <para><b>Post</b> (the 3.7B machinery, once, one transaction): shortages are ONE issue operation of the unchanged costing engine at the actual cost, surpluses ONE receipt operation at the amounts entered by the poster
/// (exactly like a receipt; zero allowed); one balanced draft journal entry against the configuration's dedicated adjustment counter account (refused while it is not configured and there is value to book), the
/// posting record and its accounting state. Any failure rolls everything back.</para>
/// <para><b>Reverse</b> (the 3.7C rules, once): both costing operations are taken back, the journal entry is cancelled while unposted or reversed by a draft reversal entry once posted.</para>
/// </summary>
public sealed class StockAdjustmentService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, InventoryCostingService costing, JournalService journal, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static StockDocumentService.Abort Conflict(string code) => new(ConfigStatus.Conflict, code);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static string IssueKeyOf(Guid id) => $"stock_adjustment:{id}:out";
    public static string ReceiptKeyOf(Guid id) => $"stock_adjustment:{id}:in";
    public static string JournalKeyOf(Guid id) => $"stock_adjustment:{id}";

    private sealed class Replay(StockAdjustmentView view) : Exception("replayed") { public StockAdjustmentView View { get; } = view; }

    private async Task<T> InLockAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> body, CancellationToken ct) =>
        await transactions.ExecuteAsync(companyId, async work =>
        {
            // the same lock that serialises warehouse structure changes and every stock posting: a warehouse cannot be deactivated underneath, and two requests for the same adjustment act once
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
            {
                lk.Parameters.AddWithValue("a", CompanyBranchService.LockKey1);
                lk.Parameters.AddWithValue("b", CompanyBranchService.LockKey2);
                await lk.ExecuteNonQueryAsync(ct);
            }
            return await body(work);
        }, ct);

    private static ConfigResult<StockAdjustmentView> Fail(Exception e) => e switch
    {
        StockDocumentService.Abort a => a.To<StockAdjustmentView>(),
        CostingException c when c.Code is "amount_precision_exceeded" or "invalid_amount" => ConfigResult<StockAdjustmentView>.Invalid([new FieldError("lines", c.Code)]),
        CostingException c => ConfigResult<StockAdjustmentView>.Fail(ConfigStatus.Conflict, c.Code),
        JournalService.Abort j => j.To<StockAdjustmentView>(),
        InvalidOperationException i when i.Message is "inventory_accounting_not_configured" or "inventory_account_not_configured" => ConfigResult<StockAdjustmentView>.Fail(ConfigStatus.Conflict, i.Message),
        _ => throw e,
    };

    // ================================================================================================================ reads

    internal static async Task<StockAdjustmentView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, Guid? stocktakeId, CancellationToken ct)
    {
        StockAdjustmentView? h;
        await using (var q = new NpgsqlCommand(
            $"SELECT a.{A.Id}, a.{A.StocktakeId}, s.{S.Number}, a.{A.BranchId}, a.{A.WarehouseId}, a.{A.AdjustmentDate}, a.{A.Number}, a.{A.FiscalYearId}, a.{A.CreatedBy}, a.{A.CreatedAtUtc} " +
            $"FROM {A.Table} a JOIN {S.Table} s ON s.{S.Id} = a.{A.StocktakeId} WHERE " + (id is not null ? $"a.{A.Id} = @id" : $"a.{A.StocktakeId} = @id"), conn, tx))
        {
            q.Parameters.AddWithValue("id", (id ?? stocktakeId)!.Value);
            await using var r = await q.ExecuteReaderAsync(ct);
            h = await r.ReadAsync(ct)
                ? new StockAdjustmentView(r.GetGuid(0), r.GetGuid(1), Str(r, 2), r.GetGuid(3), r.GetGuid(4), Iso(r.GetFieldValue<DateOnly>(5)), r.GetString(6), r.GetGuid(7), r.GetGuid(8), r.GetFieldValue<DateTimeOffset>(9), [], null)
                : null;
        }
        if (h is null) return null;
        StockAdjustmentPostingView? posting = null;
        await using (var q = new NpgsqlCommand(
            $"SELECT p.{AP.Id}, p.{AP.EffectiveDate}, p.{AP.PostedAtUtc}, p.{AP.PostedBy}, p.{AP.IssueOperationId}, p.{AP.ReceiptOperationId}, p.{AP.AccountingVersionId}, p.{AP.JournalEntryId}, j.{JE.Status}, p.{AP.TotalValue}, " +
            $"rv.{AR.Id}, rv.{AR.IssueOperationId}, rv.{AR.ReceiptOperationId}, rv.{AR.JournalAction}, rv.{AR.JournalEntryId}, rv.{AR.EffectiveDate}, rv.{AR.ReversedAtUtc}, rv.{AR.ReversedBy}, rv.{AR.Reason} " +
            $"FROM {AP.Table} p LEFT JOIN {JE.Table} j ON j.{JE.Id} = p.{AP.JournalEntryId} LEFT JOIN {AR.Table} rv ON rv.{AR.PostingId} = p.{AP.Id} WHERE p.{AP.AdjustmentId} = @a", conn, tx))
        {
            q.Parameters.AddWithValue("a", h.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var reversal = r.IsDBNull(10) ? null : new StockAdjustmentReversalView(r.GetGuid(10), G(r, 11), G(r, 12), r.GetInt16(13), G(r, 14), Iso(r.GetFieldValue<DateOnly>(15)), r.GetFieldValue<DateTimeOffset>(16), r.GetGuid(17), r.GetString(18));
                short? jstatus = r.IsDBNull(8) ? null : r.GetInt16(8);
                var journalId = G(r, 7);
                posting = new StockAdjustmentPostingView(r.GetGuid(0), Iso(r.GetFieldValue<DateOnly>(1)), r.GetFieldValue<DateTimeOffset>(2), r.GetGuid(3), G(r, 4), G(r, 5), G(r, 6), journalId, jstatus, r.GetDecimal(9),
                    (int)StockPostingService.DeriveState(journalId is not null, jstatus, reversal is not null), reversal);
            }
        }
        var lines = new List<StockAdjustmentLineView>();
        await using (var q = new NpgsqlCommand(
            $"SELECT l.{AL.LineNo}, l.{AL.ItemId}, i.{I.Code}, i.{I.NameAr}, i.{I.NameEn}, l.{AL.UnitId}, u.{U.Code}, l.{AL.Direction}, l.{AL.Quantity}, l.{AL.CountedQuantity}, l.{AL.BookQuantity}, l.{AL.Reason}, pl.{APL.Amount} " +
            $"FROM {AL.Table} l JOIN {I.Table} i ON i.{I.Id} = l.{AL.ItemId} JOIN {U.Table} u ON u.{U.Id} = l.{AL.UnitId} " +
            $"LEFT JOIN {APL.Table} pl ON pl.{APL.PostingId} = @p AND pl.{APL.LineNo} = l.{AL.LineNo} WHERE l.{AL.AdjustmentId} = @a ORDER BY l.{AL.LineNo}", conn, tx))
        {
            q.Parameters.AddWithValue("a", h.Id);
            q.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)posting?.Id ?? DBNull.Value });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(new StockAdjustmentLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), Str(r, 3), Str(r, 4), r.GetGuid(5), r.GetString(6), r.GetInt16(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), Str(r, 11),
                    r.IsDBNull(12) ? null : r.GetDecimal(12)));
        }
        return h with { Lines = lines, Posting = posting };
    }

    public async Task<StockAdjustmentView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, id, null, ct);
    }

    /// <summary>The warehouse of an adjustment (for the authorization of the caller), or null when it does not exist.</summary>
    public async Task<Guid?> WarehouseOfAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var q = new NpgsqlCommand($"SELECT {A.WarehouseId} FROM {A.Table} WHERE {A.Id} = @id", conn);
        q.Parameters.AddWithValue("id", id);
        return await q.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    // ================================================================================================================ generate

    public async Task<ConfigResult<StockAdjustmentView>> GenerateAsync(Guid actor, Guid companyId, Guid stocktakeId, long? expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<StockAdjustmentView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        try
        {
            return ConfigResult<StockAdjustmentView>.Ok(await InLockAsync(companyId, async work =>
            {
                var st = await StocktakeService.Load(work.Connection, work.Transaction, stocktakeId, true, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "stocktake_not_found");
                // a repeated request (lost response, double click, a concurrent caller that waited for the lock) returns the one adjustment that exists, unchanged
                if (st.AdjustmentId is { } existing) throw new Replay((await LoadAsync(work.Connection, work.Transaction, existing, null, ct))! with { Replayed = true });
                if (st.Revision != expectedRevision) throw new StockDocumentService.Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, st.Revision);
                if (st.Status != (int)StocktakeStatus.Approved) throw Conflict("stocktake_not_approved");
                var date = DateOnly.ParseExact(st.CountDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var (_, branchCode) = await StockDocumentService.ResolveWarehouses(work, new StockDocumentValidation.Header(StockDocType.Issue, st.WarehouseId, null, date, null, null), ct);
                // the CURRENT book quantity (3.8B-1/D2), never the frozen snapshot of the sheet
                var book = await StocktakeService.BookAsync(work.Connection, work.Transaction, st.WarehouseId, date, [.. st.Lines.Select(l => l.ItemId)], ct);
                var baseUnits = new Dictionary<Guid, Guid>();
                await using (var q = new NpgsqlCommand($"SELECT {I.Id}, {I.BaseUnitId} FROM {I.Table} WHERE {I.Id} = ANY(@ids)", work.Connection, work.Transaction))
                {
                    q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = st.Lines.Select(l => l.ItemId).Distinct().ToArray() });
                    await using var r = await q.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) baseUnits[r.GetGuid(0)] = r.GetGuid(1);
                }
                var planned = new List<(Guid Item, Guid Unit, short Direction, decimal Quantity, decimal Counted, decimal Book, string? Reason)>();
                foreach (var l in st.Lines)
                {
                    var counted = l.CountedBaseQuantity ?? throw Conflict("stocktake_incomplete");
                    var current = book.GetValueOrDefault(l.ItemId);
                    var variance = counted - current;                                         // exact decimal arithmetic in the base unit
                    if (variance == 0) continue;
                    planned.Add((l.ItemId, baseUnits[l.ItemId], (short)Math.Sign(variance), Math.Abs(variance), counted, current, l.Reason));
                }
                if (planned.Count == 0) throw Conflict("no_variance");
                // the same item and unit checks as any stock movement line (active, stockable, untracked item; active base unit; the base unit's precision)
                await StockDocumentService.ResolveLines(work, [.. planned.Select(p => new StockDocumentValidation.Line(p.Item, p.Unit, p.Quantity, null))], ct);
                var gate = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, date, false, ct);
                if (!gate.Allowed) throw Conflict(gate.Reason!);
                Guid yearId; int yearLabel;
                await using (var q = new NpgsqlCommand($"SELECT y.{FY.Id}, EXTRACT(YEAR FROM y.{FY.StartDate})::int FROM {CompanySchema.FiscalPeriod.Table} p JOIN {FY.Table} y ON y.{FY.Id} = p.{CompanySchema.FiscalPeriod.YearId} WHERE p.{CompanySchema.FiscalPeriod.Id} = @p", work.Connection, work.Transaction))
                {
                    q.Parameters.AddWithValue("p", gate.PeriodId!.Value);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);
                    yearId = r.GetGuid(0); yearLabel = r.GetInt32(1);
                }
                var id = Guid.NewGuid();
                var now = Now;
                var number = await NumberingService.IssueAsync(work.Connection, work.Transaction, DocumentTypes.StockAdjustment, new NumberScope(st.BranchId, branchCode, yearId, yearLabel.ToString(CultureInfo.InvariantCulture)), id, actor, now, ct);
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {A.Table} ({A.Id}, {A.StocktakeId}, {A.BranchId}, {A.WarehouseId}, {A.AdjustmentDate}, {A.Number}, {A.FiscalYearId}, {A.CreatedBy}, {A.CreatedAtUtc}) VALUES (@id, @s, @b, @w, @d, @n, @y, @a, @now)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("s", stocktakeId); ins.Parameters.AddWithValue("b", st.BranchId); ins.Parameters.AddWithValue("w", st.WarehouseId);
                    ins.Parameters.AddWithValue("d", date); ins.Parameters.AddWithValue("n", number.Number); ins.Parameters.AddWithValue("y", yearId); ins.Parameters.AddWithValue("a", actor); ins.Parameters.AddWithValue("now", now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                for (var i = 0; i < planned.Count; i++)
                {
                    var p = planned[i];
                    await using var li = new NpgsqlCommand(
                        $"INSERT INTO {AL.Table} ({AL.AdjustmentId}, {AL.LineNo}, {AL.ItemId}, {AL.UnitId}, {AL.Direction}, {AL.Quantity}, {AL.CountedQuantity}, {AL.BookQuantity}, {AL.Reason}) VALUES (@a, @n, @i, @u, @dir, @q, @c, @bk, @r)", work.Connection, work.Transaction);
                    li.Parameters.AddWithValue("a", id); li.Parameters.AddWithValue("n", i + 1); li.Parameters.AddWithValue("i", p.Item); li.Parameters.AddWithValue("u", p.Unit); li.Parameters.AddWithValue("dir", p.Direction);
                    li.Parameters.AddWithValue("q", p.Quantity); li.Parameters.AddWithValue("c", p.Counted); li.Parameters.AddWithValue("bk", p.Book);
                    li.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Varchar) { Value = (object?)p.Reason ?? DBNull.Value });
                    await li.ExecuteNonQueryAsync(ct);
                }
                var shortages = planned.Count(p => p.Direction < 0);
                await work.AppendAuditAsync(new AuditEventData(StockAdjustmentAuditEvents.Generated, ActorUserId: actor, SubjectId: id,
                    Detail: $"stock adjustment generated number={number.Number} stocktake={st.Number} lines={planned.Count} shortages={shortages} surpluses={planned.Count - shortages}"), ct);
                return (await LoadAsync(work.Connection, work.Transaction, id, null, ct))!;
            }, ct));
        }
        catch (Replay r) { return ConfigResult<StockAdjustmentView>.Ok(r.View); }
        catch (Exception e) when (e is StockDocumentService.Abort) { return Fail(e); }
    }

    // ================================================================================================================ post

    /// <summary>Every surplus line exactly once with its amount (zero allowed, never negative); no shortage line (a shortage is costed by the engine, never by the poster).</summary>
    private static Dictionary<int, decimal> ReadAmounts(StockAdjustmentView adj, StockPostInput? input)
    {
        var supplied = input?.Lines ?? [];
        var map = new Dictionary<int, decimal>();
        for (var i = 0; i < supplied.Count; i++)
        {
            var l = supplied[i];
            if (l.LineNo is not { } no || adj.Lines.FirstOrDefault(x => x.LineNo == no) is not { } line) throw StockDocumentService.Invalid($"lines[{i}].lineNo", "line_not_found");
            if (line.Direction < 0) throw StockDocumentService.Invalid($"lines[{i}].lineNo", "amount_not_allowed_for_shortage");
            if (l.Amount is not { } amount) throw StockDocumentService.Invalid($"lines[{i}].amount", "required");
            if (amount < 0) throw StockDocumentService.Invalid($"lines[{i}].amount", "negative_amount");
            if (!map.TryAdd(no, amount)) throw StockDocumentService.Invalid($"lines[{i}].lineNo", "duplicate_line");
        }
        foreach (var l in adj.Lines) if (l.Direction > 0 && !map.ContainsKey(l.LineNo)) throw StockDocumentService.Invalid("lines", "amount_required_for_every_surplus_line");
        return map;
    }

    public async Task<ConfigResult<StockAdjustmentView>> PostAsync(Guid actor, Guid companyId, Guid id, StockPostInput? input, CancellationToken ct = default)
    {
        try
        {
            return ConfigResult<StockAdjustmentView>.Ok(await InLockAsync(companyId, async work =>
            {
                var adj = await LoadAsync(work.Connection, work.Transaction, id, null, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "adjustment_not_found");
                if (adj.Posting is not null)
                {
                    // the identical retry returns the original posting; different amounts are a conflict, never a second posting
                    if (input?.Lines is { Count: > 0 } sent)
                    {
                        // compared with what was booked, line by line (also for a shortage-only adjustment, whose retry may carry amounts): any difference is a conflict, never a second posting
                        var byLine = adj.Lines.ToDictionary(l => l.LineNo, l => l.Amount);
                        var seen = new HashSet<int>();
                        foreach (var s in sent)
                            if (s.LineNo is not { } no || !byLine.TryGetValue(no, out var booked) || s.Amount != booked || !seen.Add(no)) throw Conflict("already_posted");
                        if (adj.Lines.Any(l => l.Direction > 0 && !seen.Contains(l.LineNo))) throw Conflict("already_posted");
                    }
                    throw new Replay(adj with { Replayed = true });
                }
                var amounts = ReadAmounts(adj, input);
                return await ExecuteAsync(work, actor, adj, amounts, ct);
            }, ct));
        }
        catch (Replay r) { return ConfigResult<StockAdjustmentView>.Ok(r.View); }
        catch (Exception e) when (e is StockDocumentService.Abort or CostingException or JournalService.Abort or InvalidOperationException) { return Fail(e); }
    }

    private async Task<StockAdjustmentView> ExecuteAsync(CompanyAuditedWork work, Guid actor, StockAdjustmentView adj, IReadOnlyDictionary<int, decimal> amounts, CancellationToken ct)
    {
        var date = DateOnly.ParseExact(adj.AdjustmentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // 1. checked again against the live data, exactly as a stock document is at posting
        await StockDocumentService.ResolveWarehouses(work, new StockDocumentValidation.Header(StockDocType.Issue, adj.WarehouseId, null, date, null, null), ct);
        var resolved = await StockDocumentService.ResolveLines(work, [.. adj.Lines.Select(l => new StockDocumentValidation.Line(l.ItemId, l.UnitId, l.Quantity, null))], ct);
        for (var i = 0; i < resolved.Count; i++)
            if (resolved[i].Base != adj.Lines[i].Quantity) throw Conflict("line_conversion_changed");
        var gate = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, date, false, ct);
        if (!gate.Allowed) throw Conflict(gate.Reason!);
        // the book quantity up to the count date must still be the one the adjustment was computed from (read under the stock lock, in this transaction): a movement on or before the count date posted or reversed
        // since generation would make the adjustment book a wrong difference, so it is refused and nothing is written (a new stocktake is needed). Later-dated movements are caught by the engine (backdated_not_supported).
        var book = await StocktakeService.BookAsync(work.Connection, work.Transaction, adj.WarehouseId, date, [.. adj.Lines.Select(l => l.ItemId)], ct);
        if (adj.Lines.Any(l => book.GetValueOrDefault(l.ItemId) != l.BookQuantity)) throw Conflict("book_changed");
        var groups = await StockPostingService.ItemGroupsAsync(work, adj.Lines.Select(l => l.ItemId), ct);
        IReadOnlyDictionary<Guid, ResolvedInventoryAccounts>? config = null;
        Task<IReadOnlyDictionary<Guid, ResolvedInventoryAccounts>> Configure() =>
            StockPostingService.EnsureConfiguredAsync(work, groups.Values.Distinct(), a => a.AdjustmentCounterAccountId, "adjustment_counter_account_not_configured", ct);
        var shortages = adj.Lines.Where(l => l.Direction < 0).ToList();
        var surpluses = adj.Lines.Where(l => l.Direction > 0).ToList();
        // the configuration is needed before the stock moves when entered surplus value must be booked; after costing when the shortages turned out to have value
        if (surpluses.Sum(l => amounts[l.LineNo]) > 0) config = await Configure();
        // 2. the stock and its valuation: the unchanged costing engine, one issue and one receipt operation in this transaction
        var source = new CostingSource("stock_adjustment", adj.Id);
        var value = new Dictionary<int, decimal>();
        Guid? issueOp = null, receiptOp = null;
        if (shortages.Count > 0)
        {
            var issued = await costing.IssueAsync(work, actor, new CostingIssueRequest(IssueKeyOf(adj.Id), date, source, [.. shortages.Select(l => new CostingIssueLine(l.ItemId, adj.WarehouseId, l.Quantity))]), ct);
            for (var i = 0; i < shortages.Count; i++) value[shortages[i].LineNo] = issued.Lines[i].Amount;
            issueOp = issued.OperationId;
        }
        if (surpluses.Count > 0)
        {
            var received = await costing.ReceiveAsync(work, actor, new CostingReceiptRequest(ReceiptKeyOf(adj.Id), date, source, [.. surpluses.Select(l => new CostingReceiptLine(l.ItemId, adj.WarehouseId, l.Quantity, amounts[l.LineNo]))]), ct);
            for (var i = 0; i < surpluses.Count; i++) value[surpluses[i].LineNo] = received.Lines[i].Amount;
            receiptOp = received.OperationId;
        }
        // the GROSS value moved: a mixed adjustment whose two sides happen to be equal still books both sides
        var total = value.Values.Sum();
        if (config is null && total > 0) config = await Configure();
        // 3. the accounting side: one balanced draft journal entry, or an explicit "no journal required" (nothing of value moved)
        Guid? journalId = null, versionId = null;
        var state = StockAccountingState.NotRequired;
        if (total > 0)
        {
            var (version, lines) = PlanJournal(adj, groups, config!, value);
            versionId = version;
            var entry = await journal.CreateInWorkAsync(work, actor, new JournalInput(date, $"Stock adjustment {adj.Number} (stocktake {adj.StocktakeNumber})", adj.BranchId, lines, null), false, DocumentSource.Automatic, JournalKeyOf(adj.Id), ct);
            journalId = entry.Header.Id;
            state = StockAccountingState.Pending;
        }
        // 4. the durable record and the audit event, in the same transaction
        var postingId = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {AP.Table} ({AP.Id}, {AP.AdjustmentId}, {AP.EffectiveDate}, {AP.PostedAtUtc}, {AP.PostedBy}, {AP.IssueOperationId}, {AP.ReceiptOperationId}, {AP.AccountingVersionId}, {AP.JournalEntryId}, {AP.TotalValue}) " +
            "VALUES (@id, @a, @e, @at, @by, @io, @ro, @v, @j, @tv)", work.Connection, work.Transaction))
        {
            ins.Parameters.AddWithValue("id", postingId); ins.Parameters.AddWithValue("a", adj.Id); ins.Parameters.AddWithValue("e", date); ins.Parameters.AddWithValue("at", Now); ins.Parameters.AddWithValue("by", actor);
            ins.Parameters.Add(new NpgsqlParameter("io", NpgsqlDbType.Uuid) { Value = (object?)issueOp ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("ro", NpgsqlDbType.Uuid) { Value = (object?)receiptOp ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Uuid) { Value = (object?)versionId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Uuid) { Value = (object?)journalId ?? DBNull.Value });
            ins.Parameters.AddWithValue("tv", total);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var l in adj.Lines)
        {
            await using var li = new NpgsqlCommand($"INSERT INTO {APL.Table} ({APL.PostingId}, {APL.LineNo}, {APL.Amount}) VALUES (@p, @n, @a)", work.Connection, work.Transaction);
            li.Parameters.AddWithValue("p", postingId); li.Parameters.AddWithValue("n", l.LineNo); li.Parameters.AddWithValue("a", value[l.LineNo]);
            await li.ExecuteNonQueryAsync(ct);
        }
        await work.AppendAuditAsync(new AuditEventData(StockAdjustmentAuditEvents.Posted, ActorUserId: actor, SubjectId: adj.Id,
            Detail: $"stock adjustment posted number={adj.Number} value={total.ToString(CultureInfo.InvariantCulture)} accounting={(state == StockAccountingState.Pending ? "pending" : "not_required")}"), ct);
        return (await LoadAsync(work.Connection, work.Transaction, adj.Id, null, ct))!;
    }

    /// <summary>
    /// One GROSS entry: each surplus debits the inventory account of its item group and the adjustment counter account is credited by the surplus total; the adjustment counter account is debited by the shortage
    /// total and each shortage credits the inventory account of its item group. Zero-value lines book nothing. Balanced by construction.
    /// </summary>
    private static (Guid VersionId, IReadOnlyList<JournalLineInput> Lines) PlanJournal(StockAdjustmentView adj, IReadOnlyDictionary<Guid, Guid> groupOfItem, IReadOnlyDictionary<Guid, ResolvedInventoryAccounts> config,
        IReadOnlyDictionary<int, decimal> value)
    {
        var inflow = new SortedDictionary<Guid, decimal>(); var outflow = new SortedDictionary<Guid, decimal>();
        ResolvedInventoryAccounts? any = null;
        foreach (var l in adj.Lines.Where(x => value[x.LineNo] > 0))
        {
            var r = config[groupOfItem[l.ItemId]];
            any ??= r;
            var side = l.Direction > 0 ? inflow : outflow;
            side[r.InventoryAccountId] = side.GetValueOrDefault(r.InventoryAccountId) + value[l.LineNo];
        }
        var counter = any!.AdjustmentCounterAccountId!.Value;
        JournalLineInput Line(Guid account, decimal? debit, decimal? credit) => new(account, null, null, debit, credit, adj.BranchId, null, null, null);
        var lines = new List<JournalLineInput>();
        foreach (var (account, amount) in inflow) lines.Add(Line(account, amount, null));
        if (outflow.Count > 0) lines.Add(Line(counter, outflow.Values.Sum(), null));
        if (inflow.Count > 0) lines.Add(Line(counter, null, inflow.Values.Sum()));
        foreach (var (account, amount) in outflow) lines.Add(Line(account, null, amount));
        return (any.VersionId, lines);
    }

    // ================================================================================================================ reverse

    /// <summary>
    /// Reverses a POSTED adjustment with the 3.7C rules: the effective date lies in an open period and not before the posting date; both costing operations are taken back (an outflow gets back exactly what it consumed;
    /// an inflow only while that stock is untouched, otherwise the engine refuses and nothing changes); the journal entry is cancelled while not posted (the reversal must then stay in the posting's period) or reversed
    /// by a draft reversal entry once posted. Once only; the identical retry returns the original reversal.
    /// </summary>
    public async Task<ConfigResult<StockAdjustmentView>> ReverseAsync(Guid actor, Guid companyId, Guid id, StockReverseInput? input, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        DateOnly date = default;
        if (string.IsNullOrWhiteSpace(input?.EffectiveDate)) errors.Add(new("effectiveDate", "required"));
        else if (!DateOnly.TryParseExact(input.EffectiveDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) errors.Add(new("effectiveDate", "invalid_date"));
        var reason = input?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) errors.Add(new("reason", "required"));
        else if (reason.Length > 500) errors.Add(new("reason", "too_long"));
        else if (reason.Any(char.IsControl)) errors.Add(new("reason", "invalid_characters"));
        if (errors.Count > 0) return ConfigResult<StockAdjustmentView>.Invalid(errors);
        try
        {
            return ConfigResult<StockAdjustmentView>.Ok(await InLockAsync(companyId, async work =>
            {
                var adj = await LoadAsync(work.Connection, work.Transaction, id, null, ct) ?? throw new StockDocumentService.Abort(ConfigStatus.NotFound, "adjustment_not_found");
                var posting = adj.Posting ?? throw Conflict("adjustment_not_posted");
                if (posting.Reversal is { } done)
                {
                    if (done.EffectiveDate == Iso(date) && done.Reason == reason) throw new Replay(adj with { Replayed = true });
                    throw Conflict("already_reversed");
                }
                var postedOn = DateOnly.ParseExact(posting.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (date < postedOn) throw StockDocumentService.Invalid("effectiveDate", "before_posting_date");
                var gate = await FiscalPeriodGate.CheckAsync(work.Connection, work.Transaction, date, false, ct);
                if (!gate.Allowed) throw Conflict(gate.Reason!);
                var source = new CostingSource("stock_adjustment_reversal", adj.Id);
                Guid? issueBack = null, receiptBack = null;
                // the inflow first: it is the side the engine may refuse (goods consumed or later movements), and nothing is written before that answer
                if (posting.ReceiptOperationId is not null)
                    receiptBack = (await costing.ReverseAsync(work, actor, new CostingReversalRequest($"stock_adjustment_reversal:{adj.Id}:in", ReceiptKeyOf(adj.Id), date, source), ct)).OperationId;
                if (posting.IssueOperationId is not null)
                    issueBack = (await costing.ReverseAsync(work, actor, new CostingReversalRequest($"stock_adjustment_reversal:{adj.Id}:out", IssueKeyOf(adj.Id), date, source), ct)).OperationId;
                short action = 0; Guid? journalRef = null;
                if (posting.JournalEntryId is { } jid)
                {
                    var view = await JournalService.LoadViewAsync(work.Connection, work.Transaction, jid, true, ct) ?? throw Conflict("journal_not_found");
                    var description = $"Reversal of stock adjustment {adj.Number}";
                    // while the entry is not posted the movement is not in the ledger: the costing reversal must stay in the posting's fiscal period (3.7C)
                    if (view.Header.Status is not (JournalStatus.Posted or JournalStatus.Reversed) && await StockPostingService.PeriodOfAsync(work, postedOn, ct) != await StockPostingService.PeriodOfAsync(work, date, ct))
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
                            action = 1; journalRef = jid;                                  // already undone outside: nothing posted left to undo, the stock side is now repaired
                            break;
                        default:
                            throw Conflict("journal_state_not_reversible");
                    }
                }
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {AR.Table} ({AR.Id}, {AR.PostingId}, {AR.IssueOperationId}, {AR.ReceiptOperationId}, {AR.JournalAction}, {AR.JournalEntryId}, {AR.EffectiveDate}, {AR.ReversedAtUtc}, {AR.ReversedBy}, {AR.Reason}) " +
                    "VALUES (@id, @p, @io, @ro, @a, @j, @e, @at, @by, @r)", work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("p", posting.Id);
                    ins.Parameters.Add(new NpgsqlParameter("io", NpgsqlDbType.Uuid) { Value = (object?)issueBack ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter("ro", NpgsqlDbType.Uuid) { Value = (object?)receiptBack ?? DBNull.Value });
                    ins.Parameters.AddWithValue("a", action);
                    ins.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Uuid) { Value = (object?)journalRef ?? DBNull.Value });
                    ins.Parameters.AddWithValue("e", date); ins.Parameters.AddWithValue("at", Now); ins.Parameters.AddWithValue("by", actor);
                    ins.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Varchar) { Value = reason });
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await work.AppendAuditAsync(new AuditEventData(StockAdjustmentAuditEvents.Reversed, ActorUserId: actor, SubjectId: adj.Id,
                    Detail: $"stock adjustment reversed number={adj.Number} journalAction={action} value={posting.TotalValue.ToString(CultureInfo.InvariantCulture)}"), ct);
                return (await LoadAsync(work.Connection, work.Transaction, adj.Id, null, ct))!;
            }, ct));
        }
        catch (Replay r) { return ConfigResult<StockAdjustmentView>.Ok(r.View); }
        catch (Exception e) when (e is StockDocumentService.Abort or CostingException or JournalService.Abort) { return Fail(e); }
    }
}

/// <summary>
/// Authorized entry point for stock adjustments. WAREHOUSE-scoped and re-read on every call; no new permission: seeing an adjustment needs <c>company.stocktake.view</c> or <c>company.stockdocs.view</c> on its warehouse,
/// generating it needs <c>company.stocktake.view</c> and <c>company.stockdocs.create</c> (it creates a stock movement document), posting <c>company.stockdocs.post</c>, reversing <c>company.stockdocs.reverse</c>.
/// An adjustment or stocktake the caller cannot see is never disclosed (forbidden, not "not found").
/// </summary>
public sealed class StockAdjustmentManagementService(AccessService access, StocktakeService stocktakes, StockAdjustmentService adjustments)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Allowed(Guid user, Guid company, string code, Guid warehouse, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Warehouse(warehouse), ct);

    private async Task<bool> CanSee(Guid user, Guid company, Guid warehouse, CancellationToken ct) =>
        await Allowed(user, company, PermissionCatalog.Codes.StocktakeView, warehouse, ct) || await Allowed(user, company, PermissionCatalog.Codes.StockDocsView, warehouse, ct);

    public async Task<ConfigResult<StockAdjustmentView>> GenerateAsync(Guid user, Guid company, Guid stocktakeId, long? rev, CancellationToken ct = default)
    {
        var wh = await stocktakes.WarehouseOfAsync(company, stocktakeId, ct);
        if (wh is null || !await Allowed(user, company, PermissionCatalog.Codes.StocktakeView, wh.Value, ct)) return Denied<StockAdjustmentView>();
        if (!await Allowed(user, company, PermissionCatalog.Codes.StockDocsCreate, wh.Value, ct)) return Denied<StockAdjustmentView>();
        return await adjustments.GenerateAsync(user, company, stocktakeId, rev, ct);
    }

    private async Task<ConfigResult<StockAdjustmentView>> OnAsync(Guid user, Guid company, Guid id, string? code, Func<Task<ConfigResult<StockAdjustmentView>>> run, CancellationToken ct)
    {
        var wh = await adjustments.WarehouseOfAsync(company, id, ct);
        if (wh is null || !await CanSee(user, company, wh.Value, ct)) return Denied<StockAdjustmentView>();
        if (code is not null && !await Allowed(user, company, code, wh.Value, ct)) return Denied<StockAdjustmentView>();
        return await run();
    }

    public Task<ConfigResult<StockAdjustmentView>> GetAsync(Guid user, Guid company, Guid id, CancellationToken ct = default) =>
        OnAsync(user, company, id, null, async () => await adjustments.GetAsync(company, id, ct) is { } v ? ConfigResult<StockAdjustmentView>.Ok(v) : ConfigResult<StockAdjustmentView>.Fail(ConfigStatus.NotFound, "adjustment_not_found"), ct);

    public Task<ConfigResult<StockAdjustmentView>> PostAsync(Guid user, Guid company, Guid id, StockPostInput? input, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsPost, () => adjustments.PostAsync(user, company, id, input, ct), ct);

    public Task<ConfigResult<StockAdjustmentView>> ReverseAsync(Guid user, Guid company, Guid id, StockReverseInput? input, CancellationToken ct = default) =>
        OnAsync(user, company, id, PermissionCatalog.Codes.StockDocsReverse, () => adjustments.ReverseAsync(user, company, id, input, ct), ct);
}
