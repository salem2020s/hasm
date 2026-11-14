using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ClosingLogic;
using CS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ClosingSettings;
using FC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYearClosing;
using FK = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYearCarryforward;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using BR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Fiscal-year closing and opening balances (Phase 3.2C S3). <b>No authorization here by design:</b> <see cref="FiscalYearClosingManagementService"/> wraps it.
/// <para><b>Sequence.</b> The closing entry (profit and loss to the company's chosen equity account, per branch) is an ordinary journal entry dated the LAST DAY of the year and the
/// opening-balance entry (balance-sheet balances per account and branch) one dated the FIRST DAY of the next year; both are drafted here and then follow the normal journal workflow
/// (numbering, approvals, FiscalPeriodGate, atomic posting), so nothing bypasses a financial control. The last period is Final Closed only after the closing entry is posted. PostgreSQL
/// refuses to post an entry that no longer matches the ledger (<c>stale</c>), refuses duplicates, and a correction is a reversal of the opening balances and of the closing followed
/// by a new version: posted entries are never edited. Foreign-currency balance-sheet positions are NOT carried forward (that needs the foreign-exchange policy of a later phase); the
/// closing reports them as blockers instead of inventing a rate.</para>
/// </summary>
public sealed class FiscalYearClosingService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, JournalService journal, PeriodCloseGuard closeGuard, TimeProvider clock)
{
    internal const int LockKey1 = 0x48434659;   // "HCFY": advisory-lock namespace of year-end processing
    internal const int LockKey2 = 1;
    private const int MaxLines = JournalValidation.MaxLines;

    // ------------------------------------------------------------------------------------------------------------------------- settings

    public async Task<ClosingSettingsView> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadSettingsAsync(conn, null, ct);
    }

    private static async Task<ClosingSettingsView> ReadSettingsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT s.{CS.EquityAccountId}, a.{AC.Code}, a.{AC.NameAr}, a.{AC.NameEn}, s.{CS.RequireApproval}, s.{CS.Revision} FROM {CS.Table} s JOIN {AC.Table} a ON a.{AC.Id} = s.{CS.EquityAccountId}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetInt64(5), true) : new(null, null, null, null, true, 0, false);
    }

    public async Task<ConfigResult<ClosingSettingsView>> PutSettingsAsync(Guid actor, Guid companyId, long? expectedRevision, ClosingSettingsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ClosingSettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input?.EquityAccountId is null) return ConfigResult<ClosingSettingsView>.Invalid([new("equityAccountId", "required")]);
        var requireApproval = input.RequireApproval ?? true;
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var cur = await ReadSettingsAsync(work.Connection, work.Transaction, ct);
            if (cur.Revision != expectedRevision.Value) throw new JournalService.Abort(ConfigStatus.PreconditionFailed, "revision_conflict", cur.Revision);
            await using (var q = new NpgsqlCommand($"SELECT {AC.Type}, {AC.IsGroup}, {AC.IsActive} FROM {AC.Table} WHERE {AC.Id} = @a", work.Connection, work.Transaction))
            {
                q.Parameters.AddWithValue("a", input.EquityAccountId.Value);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) throw new JournalService.Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("equityAccountId", "account_not_found")]);
                if (r.GetInt16(0) != (short)AccountType.Equity) throw new JournalService.Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("equityAccountId", "account_not_equity")]);
                if (r.GetBoolean(1) || !r.GetBoolean(2)) throw new JournalService.Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("equityAccountId", "account_not_postable")]);
            }
            if (cur.Configured && cur.EquityAccountId == input.EquityAccountId && cur.RequireApproval == requireApproval) throw new JournalService.Abort(ConfigStatus.Ok, "unchanged");
            var now = clock.GetUtcNow().UtcDateTime;
            await using (var w = new NpgsqlCommand(cur.Configured
                ? $"UPDATE {CS.Table} SET {CS.EquityAccountId} = @a, {CS.RequireApproval} = @r, {CS.Revision} = {CS.Revision} + 1, {CS.UpdatedAtUtc} = @t"
                : $"INSERT INTO {CS.Table} ({CS.EquityAccountId}, {CS.RequireApproval}, {CS.Revision}, {CS.UpdatedAtUtc}) VALUES (@a, @r, 1, @t)", work.Connection, work.Transaction))
            {
                w.Parameters.AddWithValue("a", input.EquityAccountId.Value); w.Parameters.AddWithValue("r", requireApproval); w.Parameters.AddWithValue("t", now);
                await w.ExecuteNonQueryAsync(ct);
            }
            var changes = new JsonObject
            {
                ["equityAccountId"] = new JsonObject { ["old"] = cur.EquityAccountId?.ToString(), ["new"] = input.EquityAccountId.Value.ToString() },
                ["requireApproval"] = new JsonObject { ["old"] = cur.Configured ? cur.RequireApproval.ToString() : null, ["new"] = requireApproval.ToString() },
            };
            await PolicyHistoryWriter.RecordAsync(work, clock, LedgerAuditEvents.ClosingSettingsChanged, 4, "closing_settings", actor, cur.Revision + 1, changes, "year-end closing settings changed", ct);
            return await ReadSettingsAsync(work.Connection, work.Transaction, ct);
        }, ct, onUnchanged: async () => await GetSettingsAsync(companyId, ct));
    }

    // ------------------------------------------------------------------------------------------------------------------------- the computation

    private sealed record YearRow(Guid Id, string Name, DateOnly Start, DateOnly End);

    private static async Task<YearRow?> LoadYearAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid yearId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {FY.Id}, {FY.Name}, {FY.StartDate}, {FY.EndDate} FROM {FY.Table} WHERE {FY.Id} = @y", conn, tx);
        cmd.Parameters.AddWithValue("y", yearId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetFieldValue<DateOnly>(3)) : null;
    }

    private static string Hash(IEnumerable<string> parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)))).ToLowerInvariant();

    private static string Num(decimal v) => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<List<(Guid Account, Guid Branch, decimal Balance)>> ReadBalancesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        bind(cmd.Parameters);
        var list = new List<(Guid, Guid, decimal)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2)));
        return list;
    }

    /// <summary>The dry run: every figure, line and validation, computed in the caller's transaction (so a start re-computes it under its lock).</summary>
    internal async Task<ClosingPreview?> ComputeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid yearId, CancellationToken ct)
    {
        var year = await LoadYearAsync(conn, tx, yearId, ct);
        if (year is null) return null;
        string baseCurrency;
        await using (var q = new NpgsqlCommand($"SELECT {CC.Code} FROM {CC.Table} WHERE {CC.IsBase}", conn, tx)) baseCurrency = ((await q.ExecuteScalarAsync(ct)) as string ?? "").Trim();
        var settings = await ReadSettingsAsync(conn, tx, ct);
        var checks = new List<ClosingCheck>();

        // Profit-and-loss balances of THIS year (closing entries excluded: the figure is the same before and after closing).
        var pnl = await ReadBalancesAsync(conn, tx, $"SELECT account, branch, balance FROM {CL.PnlFunction}(@y) ORDER BY account, branch", p => p.AddWithValue("y", yearId), ct);
        var net = pnl.Sum(x => x.Balance);
        var closingLines = new List<YearEndLine>();
        foreach (var (acc, br, bal) in pnl) closingLines.Add(bal < 0 ? new(acc, br, -bal, 0m) : new(acc, br, 0m, bal));
        foreach (var g in pnl.GroupBy(x => x.Branch).OrderBy(g => g.Key))
        {
            var eb = g.Sum(x => x.Balance);
            if (eb == 0 || settings.EquityAccountId is not { } eq) continue;
            closingLines.Add(eb > 0 ? new(eq, g.Key, eb, 0m) : new(eq, g.Key, 0m, -eb));
        }
        // Balance-sheet balances at the end of the year (cumulative over the whole ledger up to the year end).
        var bs = await ReadBalancesAsync(conn, tx,
            $"SELECT l.{JL.Account}, l.{JL.Branch}, SUM(l.{JL.BaseDebit} - l.{JL.BaseCredit}) FROM {JL.Table} l JOIN {JE.Table} e ON e.{JE.Id} = l.{JL.Entry} JOIN {AC.Table} a ON a.{AC.Id} = l.{JL.Account} AND a.{AC.Statement} = 1 " +
            $"WHERE e.{JE.Status} IN (4, 5) AND e.{JE.Date} <= @end AND e.{JE.Date} >= COALESCE({CL.FloorFunction}(@end), '-infinity'::date) GROUP BY l.{JL.Account}, l.{JL.Branch} HAVING SUM(l.{JL.BaseDebit} - l.{JL.BaseCredit}) <> 0 ORDER BY 1, 2",
            p => p.Add(new NpgsqlParameter("end", NpgsqlDbType.Date) { Value = year.End.ToDateTime(TimeOnly.MinValue) }), ct);
        var openingLines = bs.Select(x => x.Balance > 0 ? new YearEndLine(x.Account, x.Branch, x.Balance, 0m) : new YearEndLine(x.Account, x.Branch, 0m, -x.Balance)).ToList();

        // ---- validations -------------------------------------------------------------------------------------------------------------------------------
        checks.Add(settings.Configured ? new("settings", "passed", settings.EquityCode, "close") : new("settings", "blocked", "profit_loss_account_not_configured", "close"));
        // earlier years must be closed first (otherwise their profit and loss is not yet in equity and the opening balances could not balance)
        var earlier = new List<(Guid Id, string Name)>();
        await using (var q = new NpgsqlCommand($"SELECT {FY.Id}, {FY.Name} FROM {FY.Table} WHERE {FY.EndDate} < @s ORDER BY {FY.StartDate}", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Date) { Value = year.Start.ToDateTime(TimeOnly.MinValue) });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) earlier.Add((r.GetGuid(0), r.GetString(1)));
        }
        var openYears = new List<string>();
        foreach (var (eid, ename) in earlier)
        {
            bool hasActivity;
            await using (var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {CL.PnlFunction}(@y))", conn, tx)) { q.Parameters.AddWithValue("y", eid); hasActivity = (bool)(await q.ExecuteScalarAsync(ct))!; }
            if (!hasActivity) continue;
            await using var c = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {FC.Table} cl JOIN {JE.Table} ce ON ce.{JE.Id} = cl.{FC.ClosingEntryId} WHERE cl.{FC.YearId} = @y AND ce.{JE.Status} = 4 AND NOT {CL.StaleFunction}(cl.{FC.Id}))", conn, tx);
            c.Parameters.AddWithValue("y", eid);
            if (!(bool)(await c.ExecuteScalarAsync(ct))!) openYears.Add(ename);
        }
        checks.Add(openYears.Count == 0 ? new("earlier_years_closed", "passed", null, "both") : new("earlier_years_closed", "blocked", string.Join(", ", openYears), "both"));
        long pending;
        await using (var q = new NpgsqlCommand($"SELECT count(*) FROM {JE.Table} e WHERE e.{JE.Status} IN (1, 2, 3) AND e.{JE.Date} BETWEEN @s AND @e AND COALESCE({CL.KindFunction}(e.{JE.Id}), 0) NOT IN (1, 3)", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Date) { Value = year.Start.ToDateTime(TimeOnly.MinValue) });
            q.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = year.End.ToDateTime(TimeOnly.MinValue) });
            pending = (long)(await q.ExecuteScalarAsync(ct))!;
        }
        checks.Add(pending == 0 ? new("pending_entries", "passed", null, "close") : new("pending_entries", "blocked", pending.ToString(), "close"));
        // the last period of the year must still accept the closing entry
        short? lastState = null;
        await using (var q = new NpgsqlCommand($"SELECT {FP.Status} FROM {FP.Table} WHERE @e BETWEEN {FP.StartDate} AND {FP.EndDate}", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = year.End.ToDateTime(TimeOnly.MinValue) });
            lastState = await q.ExecuteScalarAsync(ct) as short?;
        }
        checks.Add(lastState is null ? new("last_period", "blocked", "no_fiscal_period", "close")
            : lastState == (short)FiscalPeriodState.FinalClosed ? new("last_period", "blocked", "final_closed_reopen_through_approval", "close")
            : new("last_period", "passed", lastState == (short)FiscalPeriodState.SoftClosed ? "soft_closed" : "open", "close"));
        // accounts that carry a balance must still be usable by the posting engine
        var inactive = new List<string>();
        await using (var q = new NpgsqlCommand($"SELECT a.{AC.Code} FROM {AC.Table} a WHERE NOT a.{AC.IsActive} AND a.{AC.Id} = ANY(@ids) ORDER BY a.{AC.Code}", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = pnl.Select(x => x.Account).Concat(bs.Select(x => x.Account)).Distinct().ToArray() });
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) inactive.Add(r.GetString(0));
        }
        checks.Add(inactive.Count == 0 ? new("accounts_active", "passed") : new("accounts_active", "blocked", string.Join(", ", inactive)));
        // profit-and-loss accounts tied to one non-base currency cannot receive a base-currency closing line
        var pinned = new List<string>();
        await using (var q = new NpgsqlCommand($"SELECT a.{AC.Code} FROM {AC.Table} a WHERE a.{AC.Id} = ANY(@ids) AND a.{AC.CurrencyPolicy} = 3 AND a.{AC.CurrencyCode} <> @b ORDER BY a.{AC.Code}", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = pnl.Select(x => x.Account).Concat(bs.Select(x => x.Account)).Distinct().ToArray() });
            q.Parameters.AddWithValue("b", baseCurrency);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pinned.Add(r.GetString(0));
        }
        checks.Add(pinned.Count == 0 ? new("single_currency_accounts", "passed") : new("single_currency_accounts", "blocked", string.Join(", ", pinned) + " (foreign-exchange policy needed)"));
        // foreign-currency balance-sheet positions are not carried forward (no revaluation policy yet)
        var foreign = new List<string>();
        await using (var q = new NpgsqlCommand(
            $"SELECT a.{AC.Code} || '/' || btrim(l.{JL.Cur}) FROM {JL.Table} l JOIN {JE.Table} e ON e.{JE.Id} = l.{JL.Entry} JOIN {AC.Table} a ON a.{AC.Id} = l.{JL.Account} AND a.{AC.Statement} = 1 " +
            $"WHERE e.{JE.Status} IN (4, 5) AND e.{JE.Date} <= @end AND btrim(l.{JL.Cur}) <> @b GROUP BY a.{AC.Code}, btrim(l.{JL.Cur}) HAVING SUM(l.{JL.Debit} - l.{JL.Credit}) <> 0 ORDER BY 1", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.Date) { Value = year.End.ToDateTime(TimeOnly.MinValue) });
            q.Parameters.AddWithValue("b", baseCurrency);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) foreign.Add(r.GetString(0));
        }
        checks.Add(foreign.Count == 0 ? new("foreign_currency_balances", "passed", null, "carry") : new("foreign_currency_balances", "blocked", string.Join(", ", foreign) + " (revaluation policy needed)", "carry"));
        if (closingLines.Count > MaxLines) checks.Add(new("entry_size", "blocked", $"closing {closingLines.Count} lines (limit {MaxLines})", "close"));
        if (openingLines.Count > MaxLines) checks.Add(new("entry_size_opening", "blocked", $"opening {openingLines.Count} lines (limit {MaxLines})", "carry"));
        // the other close-check domains: real probes run when a period closes; modules that do not exist are reported as such, never as passed
        foreach (var d in closeGuard.UnimplementedDomains) checks.Add(new($"domain:{d}", "blocked", "unimplemented", "both"));
        foreach (var d in closeGuard.NotApplicableDomains) checks.Add(new($"domain:{d}", "not_applicable", "module_not_part_of_this_release", "both"));

        // existing closing / next year
        Guid? active = null; int version = 1;
        await using (var q = new NpgsqlCommand(
            $"SELECT cl.{FC.Id}, cl.{FC.Version}, (ce.{JE.Status} NOT IN (5, 6) OR EXISTS (SELECT 1 FROM {FK.Table} k JOIN {JE.Table} ke ON ke.{JE.Id} = k.{FK.EntryId} WHERE k.{FK.ClosingId} = cl.{FC.Id} AND ke.{JE.Status} NOT IN (5, 6))) FROM {FC.Table} cl JOIN {JE.Table} ce ON ce.{JE.Id} = cl.{FC.ClosingEntryId} WHERE cl.{FC.YearId} = @y ORDER BY cl.{FC.Version}", conn, tx))
        {
            q.Parameters.AddWithValue("y", yearId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { version = r.GetInt32(1) + 1; if (r.GetBoolean(2)) active = r.GetGuid(0); }
        }
        checks.Add(active is null ? new("no_active_closing", "passed", null, "close") : new("no_active_closing", "blocked", "already_closed_reverse_first", "close"));
        Guid? nextYear = null; DateOnly? nextStart = null;
        await using (var q = new NpgsqlCommand($"SELECT {FY.Id}, {FY.StartDate} FROM {FY.Table} WHERE {FY.StartDate} = @n", conn, tx))
        {
            q.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Date) { Value = year.End.AddDays(1).ToDateTime(TimeOnly.MinValue) });
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { nextYear = r.GetGuid(0); nextStart = r.GetFieldValue<DateOnly>(1); }
        }
        short? firstState = null;
        if (nextStart is { } ns)
            await using (var q = new NpgsqlCommand($"SELECT {FP.Status} FROM {FP.Table} WHERE @d BETWEEN {FP.StartDate} AND {FP.EndDate}", conn, tx)) { q.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = ns.ToDateTime(TimeOnly.MinValue) }); firstState = await q.ExecuteScalarAsync(ct) as short?; }
        checks.Add(nextYear is null ? new("next_year", "blocked", "create_the_next_fiscal_year_first", "carry")
            : firstState == (short)FiscalPeriodState.FinalClosed ? new("next_year", "blocked", "first_period_final_closed", "carry") : new("next_year", "passed", null, "carry"));
        // the closing must be posted (and current) before the balances are carried forward
        var needsClosing = pnl.Count > 0;
        var closedAndCurrent = false;
        await using (var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {FC.Table} cl JOIN {JE.Table} ce ON ce.{JE.Id} = cl.{FC.ClosingEntryId} WHERE cl.{FC.YearId} = @y AND ce.{JE.Status} = 4 AND NOT {CL.StaleFunction}(cl.{FC.Id}))", conn, tx))
        { q.Parameters.AddWithValue("y", yearId); closedAndCurrent = (bool)(await q.ExecuteScalarAsync(ct))!; }
        checks.Add(!needsClosing || closedAndCurrent ? new("closing_posted", "passed", needsClosing ? null : "no_profit_and_loss_activity", "carry") : new("closing_posted", "blocked", "post_the_year_end_closing_first", "carry"));
        var carried = false;
        await using (var q = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {FK.Table} k JOIN {JE.Table} ke ON ke.{JE.Id} = k.{FK.EntryId} WHERE k.{FK.FromYearId} = @y AND ke.{JE.Status} NOT IN (5, 6))", conn, tx)) { q.Parameters.AddWithValue("y", yearId); carried = (bool)(await q.ExecuteScalarAsync(ct))!; }
        checks.Add(!carried ? new("not_carried_forward", "passed", null, "carry") : new("not_carried_forward", "blocked", "already_carried_forward_reverse_first", "carry"));
        if (openingLines.Count == 0) checks.Add(new("balances_to_carry", "blocked", "no_balance_sheet_balances", "carry"));
        if (closingLines.Count == 0) checks.Add(new("profit_and_loss", "passed", "no_profit_and_loss_activity", "close"));

        bool Blocked(string phase) => checks.Any(c => c.Status == "blocked" && (c.Phase == "both" || c.Phase == phase));
        var canClose = !Blocked("close") && closingLines.Count > 0;
        var canCarry = !Blocked("carry") && openingLines.Count > 0;
        var evidence = Hash([year.Id.ToString(), Num(net), .. closingLines.Select(l => $"C|{l.AccountId}|{l.BranchId}|{Num(l.Debit)}|{Num(l.Credit)}"), .. openingLines.Select(l => $"O|{l.AccountId}|{l.BranchId}|{Num(l.Debit)}|{Num(l.Credit)}")]);
        return new ClosingPreview(year.Id, year.Name, year.Start, year.End, baseCurrency, settings.EquityAccountId, net, closingLines, openingLines, nextYear, nextStart, checks, canClose, canCarry, version, evidence, active);
    }

    public async Task<ClosingPreview?> PreviewAsync(Guid companyId, Guid yearId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);   // one consistent snapshot
        var p = await ComputeAsync(conn, tx, yearId, ct);
        await tx.CommitAsync(ct);
        return p;
    }

    // ------------------------------------------------------------------------------------------------------------------------- workflow

    /// <summary>The header branch of year-end entries is the main branch (every line carries its own branch, so branch figures stay exact).</summary>
    private static async Task<Guid> MainBranchAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {BR.Id} FROM {BR.Table} WHERE {BR.IsMain} AND {BR.IsActive}", work.Connection, work.Transaction);
        return await q.ExecuteScalarAsync(ct) as Guid? ?? throw new JournalService.Abort(ConfigStatus.Conflict, "no_main_branch");
    }

    private static JournalLineInput Line(YearEndLine l) => new(l.AccountId, null, null, l.Debit > 0 ? l.Debit : null, l.Credit > 0 ? l.Credit : null, l.BranchId, null, null, null);

    /// <summary>Generates the draft closing entry of a year (dated its last day) and records the closing. The entry then follows the normal journal workflow.</summary>
    public async Task<ConfigResult<FiscalYearClosingView>> StartClosingAsync(Guid actor, Guid companyId, Guid yearId, CancellationToken ct = default) =>
        await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var p = await ComputeAsync(work.Connection, work.Transaction, yearId, ct) ?? throw new JournalService.Abort(ConfigStatus.NotFound, "fiscal_year_not_found");
            if (!p.CanClose) throw Blocked(p, "close");
            var key = $"yearclose:{yearId}:{p.NextVersion}";
            var entry = await journal.CreateInWorkAsync(work, actor, new JournalInput(p.YearEnd, $"Year-end closing {p.YearName} (v{p.NextVersion})", await MainBranchAsync(work, ct), [.. p.ClosingLines.Select(Line)], null), false, DocumentSource.Automatic, key, ct);
            var id = Guid.NewGuid();
            var evidence = new JsonObject { ["hash"] = p.EvidenceHash, ["netResult"] = Num(p.NetResult), ["lines"] = p.ClosingLines.Count, ["equityAccountId"] = p.EquityAccountId!.Value.ToString() };
            await using (var ins = new NpgsqlCommand(
                $"INSERT INTO {FC.Table} ({FC.Id}, {FC.YearId}, {FC.Version}, {FC.NetResult}, {FC.EquityAccountId}, {FC.ClosingEntryId}, {FC.Evidence}, {FC.CreatedBy}, {FC.CreatedAtUtc}) VALUES (@id, @y, @v, @n, @a, @e, @ev, @by, @t)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("y", yearId); ins.Parameters.AddWithValue("v", p.NextVersion); ins.Parameters.AddWithValue("n", p.NetResult);
                ins.Parameters.AddWithValue("a", p.EquityAccountId.Value); ins.Parameters.AddWithValue("e", entry.Header.Id);
                ins.Parameters.Add(new NpgsqlParameter("ev", NpgsqlDbType.Jsonb) { Value = evidence.ToJsonString() });
                ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
                try { await ins.ExecuteNonQueryAsync(ct); }
                catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.RaiseException or PostgresErrorCodes.UniqueViolation) { throw new JournalService.Abort(ConfigStatus.Conflict, "closing_already_exists"); }
            }
            var changes = new JsonObject { ["version"] = p.NextVersion, ["netResult"] = Num(p.NetResult), ["entryId"] = entry.Header.Id.ToString(), ["evidence"] = p.EvidenceHash };
            await PolicyHistoryWriter.RecordAsync(work, clock, LedgerAuditEvents.FiscalYearClosingStarted, 5, $"close:{yearId}", actor, p.NextVersion, changes, "fiscal year closing generated", ct);
            return (await LoadClosingsAsync(work.Connection, work.Transaction, null, id, ct)).Single();
        }, ct);

    /// <summary>Generates the draft opening-balance entry (dated the first day of the next year) from the balance sheet at the end of the year, after its closing is posted.</summary>
    public async Task<ConfigResult<FiscalYearCarryforwardView>> CarryForwardAsync(Guid actor, Guid companyId, Guid yearId, CancellationToken ct = default) =>
        await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var p = await ComputeAsync(work.Connection, work.Transaction, yearId, ct) ?? throw new JournalService.Abort(ConfigStatus.NotFound, "fiscal_year_not_found");
            if (!p.CanCarryForward || p.NextYearId is null || p.NextYearStart is null) throw Blocked(p, "carry");
            Guid? closingId = null;
            await using (var q = new NpgsqlCommand($"SELECT cl.{FC.Id} FROM {FC.Table} cl JOIN {JE.Table} ce ON ce.{JE.Id} = cl.{FC.ClosingEntryId} WHERE cl.{FC.YearId} = @y AND ce.{JE.Status} = 4 ORDER BY cl.{FC.Version} DESC LIMIT 1", work.Connection, work.Transaction))
            { q.Parameters.AddWithValue("y", yearId); closingId = await q.ExecuteScalarAsync(ct) as Guid?; }
            long n;
            await using (var q = new NpgsqlCommand($"SELECT count(*) FROM {FK.Table} WHERE {FK.FromYearId} = @y", work.Connection, work.Transaction)) { q.Parameters.AddWithValue("y", yearId); n = (long)(await q.ExecuteScalarAsync(ct))! + 1; }
            var entry = await journal.CreateInWorkAsync(work, actor, new JournalInput(p.NextYearStart.Value, $"Opening balances {p.YearName} → next year (#{n})", await MainBranchAsync(work, ct), [.. p.OpeningLines.Select(Line)], null), false, DocumentSource.Automatic, $"yearopen:{yearId}:{n}", ct);
            var id = Guid.NewGuid();
            var evidence = new JsonObject { ["hash"] = p.EvidenceHash, ["lines"] = p.OpeningLines.Count };
            await using (var ins = new NpgsqlCommand(
                $"INSERT INTO {FK.Table} ({FK.Id}, {FK.FromYearId}, {FK.ClosingId}, {FK.ToYearId}, {FK.EntryId}, {FK.Evidence}, {FK.CreatedBy}, {FK.CreatedAtUtc}) VALUES (@id, @f, @c, @to, @e, @ev, @by, @t)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("f", yearId); ins.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)closingId ?? DBNull.Value });
                ins.Parameters.AddWithValue("to", p.NextYearId.Value); ins.Parameters.AddWithValue("e", entry.Header.Id);
                ins.Parameters.Add(new NpgsqlParameter("ev", NpgsqlDbType.Jsonb) { Value = evidence.ToJsonString() });
                ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
                try { await ins.ExecuteNonQueryAsync(ct); }
                catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.RaiseException or PostgresErrorCodes.UniqueViolation) { throw new JournalService.Abort(ConfigStatus.Conflict, "carryforward_already_exists"); }
            }
            var changes = new JsonObject { ["fromYear"] = yearId.ToString(), ["toYear"] = p.NextYearId.Value.ToString(), ["entryId"] = entry.Header.Id.ToString(), ["evidence"] = p.EvidenceHash, ["lines"] = p.OpeningLines.Count };
            await PolicyHistoryWriter.RecordAsync(work, clock, LedgerAuditEvents.FiscalYearCarryforwardStarted, 5, $"open:{yearId}", actor, n, changes, "opening balances generated", ct);
            return (await LoadCarryforwardsAsync(work.Connection, work.Transaction, null, id, ct)).Single();
        }, ct);

    private static JournalService.Abort Blocked(ClosingPreview p, string phase) =>
        new(ConfigStatus.Conflict, "closing_blocked", fields: [.. p.Checks.Where(c => c.Status == "blocked" && (c.Phase == "both" || c.Phase == phase)).Select(c => new FieldError("checks", c.Code))]);

    // ------------------------------------------------------------------------------------------------------------------------- reads

    public async Task<YearEndStatus?> StatusAsync(Guid companyId, Guid yearId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var year = await LoadYearAsync(conn, null, yearId, ct);
        if (year is null) return null;
        return new YearEndStatus(year.Id, year.Name, year.Start, year.End, await LoadClosingsAsync(conn, null, yearId, null, ct), await LoadCarryforwardsAsync(conn, null, yearId, null, ct));
    }

    public async Task<IReadOnlyList<YearEndStatus>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var ids = new List<Guid>();
        await using (var q = new NpgsqlCommand($"SELECT {FY.Id} FROM {FY.Table} ORDER BY {FY.StartDate}", conn)) { await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0)); }
        var list = new List<YearEndStatus>();
        foreach (var id in ids) { var year = (await LoadYearAsync(conn, null, id, ct))!; list.Add(new(year.Id, year.Name, year.Start, year.End, await LoadClosingsAsync(conn, null, id, null, ct), await LoadCarryforwardsAsync(conn, null, id, null, ct))); }
        return list;
    }

    private static string StateOf(JournalStatus closing, JournalStatus? opening) => closing switch
    {
        JournalStatus.Posted => opening switch { JournalStatus.Posted => "carried_forward", JournalStatus.Draft or JournalStatus.PendingApproval or JournalStatus.Approved => "opening_prepared", _ => "closed" },
        JournalStatus.Reversed => "reversed",
        JournalStatus.Cancelled => "cancelled",
        _ => "prepared",
    };

    internal static async Task<List<FiscalYearClosingView>> LoadClosingsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? yearId, Guid? id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT cl.{FC.Id}, cl.{FC.YearId}, cl.{FC.Version}, cl.{FC.NetResult}, cl.{FC.EquityAccountId}, cl.{FC.ClosingEntryId}, ce.{JE.Status}, ce.{JE.Number}, k.{FK.Id}, k.{FK.EntryId}, ke.{JE.Status}, ke.{JE.Number}, k.{FK.ToYearId}, cl.{FC.Evidence}->>'hash', cl.{FC.CreatedAtUtc}, cl.{FC.CreatedBy}, " +
            $"CASE WHEN ce.{JE.Status} = 4 THEN {CL.StaleFunction}(cl.{FC.Id}) ELSE false END " +
            $"FROM {FC.Table} cl JOIN {JE.Table} ce ON ce.{JE.Id} = cl.{FC.ClosingEntryId} " +
            $"LEFT JOIN LATERAL (SELECT x.* FROM {FK.Table} x JOIN {JE.Table} xe ON xe.{JE.Id} = x.{FK.EntryId} WHERE x.{FK.ClosingId} = cl.{FC.Id} ORDER BY x.{FK.CreatedAtUtc} DESC LIMIT 1) k ON true " +
            $"LEFT JOIN {JE.Table} ke ON ke.{JE.Id} = k.{FK.EntryId} WHERE (@y::uuid IS NULL OR cl.{FC.YearId} = @y) AND (@id::uuid IS NULL OR cl.{FC.Id} = @id) ORDER BY cl.{FC.YearId}, cl.{FC.Version}", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("y", NpgsqlDbType.Uuid) { Value = (object?)yearId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
        var list = new List<FiscalYearClosingView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var closing = (JournalStatus)r.GetInt16(6);
            JournalStatus? opening = r.IsDBNull(10) ? null : (JournalStatus)r.GetInt16(10);
            list.Add(new FiscalYearClosingView(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3), r.GetGuid(4), r.GetGuid(5), closing, r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetGuid(9), opening, r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetGuid(12),
                StateOf(closing, opening), r.GetBoolean(16), r.GetString(13), new DateTimeOffset(r.GetDateTime(14), TimeSpan.Zero), r.GetGuid(15)));
        }
        return list;
    }

    internal static async Task<List<FiscalYearCarryforwardView>> LoadCarryforwardsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? yearId, Guid? id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT k.{FK.Id}, k.{FK.FromYearId}, k.{FK.ClosingId}, k.{FK.ToYearId}, k.{FK.EntryId}, ke.{JE.Status}, ke.{JE.Number}, k.{FK.Evidence}->>'hash', k.{FK.CreatedAtUtc}, k.{FK.CreatedBy}, " +
            $"CASE WHEN ke.{JE.Status} = 4 THEN NOT {CL.MatchOpeningFunction}(ke.{JE.Id}) ELSE false END " +
            $"FROM {FK.Table} k JOIN {JE.Table} ke ON ke.{JE.Id} = k.{FK.EntryId} WHERE (@y::uuid IS NULL OR k.{FK.FromYearId} = @y) AND (@id::uuid IS NULL OR k.{FK.Id} = @id) ORDER BY k.{FK.CreatedAtUtc}", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("y", NpgsqlDbType.Uuid) { Value = (object?)yearId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
        var list = new List<FiscalYearCarryforwardView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new FiscalYearCarryforwardView(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), (JournalStatus)r.GetInt16(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(10), r.GetString(7),
                new DateTimeOffset(r.GetDateTime(8), TimeSpan.Zero), r.GetGuid(9)));
        return list;
    }

    // ------------------------------------------------------------------------------------------------------------------------- plumbing

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct, Func<Task<T?>>? onUnchanged = null) where T : class
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (JournalService.Abort abort) when (abort.Message == "unchanged" && onUnchanged is not null) { return ConfigResult<T>.Ok((await onUnchanged())!); }
        catch (JournalService.Abort abort) { return abort.To<T>(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Authorized entry point of year-end processing. Company-wide permissions re-read on every call: view, configure, create (generate closing / opening entries) and reverse are separate.
/// Generating the entries also needs company-wide <c>company.journal.create</c> (the entries touch every branch), and their approval and posting follow the journal permissions and the
/// approval policies; reversing also needs the journal reverse permission. The initiator of an entry can never approve it (separation of duties).
/// </summary>
public sealed class FiscalYearClosingManagementService(AccessService access, FiscalYearClosingService closing, JournalManagementService journal)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Allowed(Guid userId, Guid companyId, string code, CancellationToken ct) => access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    public async Task<ConfigResult<ClosingSettingsView>> GetSettingsAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingView, ct) ? ConfigResult<ClosingSettingsView>.Ok(await closing.GetSettingsAsync(companyId, ct)) : Denied<ClosingSettingsView>();

    public async Task<ConfigResult<ClosingSettingsView>> PutSettingsAsync(Guid userId, Guid companyId, long? revision, ClosingSettingsInput? input, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingConfigure, ct) ? await closing.PutSettingsAsync(userId, companyId, revision, input, ct) : Denied<ClosingSettingsView>();

    public async Task<ConfigResult<ClosingPreview>> PreviewAsync(Guid userId, Guid companyId, Guid yearId, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingView, ct)) return Denied<ClosingPreview>();
        var p = await closing.PreviewAsync(companyId, yearId, ct);
        return p is null ? ConfigResult<ClosingPreview>.Fail(ConfigStatus.NotFound, "fiscal_year_not_found") : ConfigResult<ClosingPreview>.Ok(p);
    }

    public async Task<ConfigResult<IReadOnlyList<YearEndStatus>>> ListAsync(Guid userId, Guid companyId, CancellationToken ct = default) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingView, ct) ? ConfigResult<IReadOnlyList<YearEndStatus>>.Ok(await closing.ListAsync(companyId, ct)) : Denied<IReadOnlyList<YearEndStatus>>();

    private async Task<bool> MayGenerate(Guid userId, Guid companyId, CancellationToken ct) =>
        await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingCreate, ct) && await Allowed(userId, companyId, PermissionCatalog.Codes.JournalCreate, ct);

    public async Task<ConfigResult<FiscalYearClosingView>> StartClosingAsync(Guid userId, Guid companyId, Guid yearId, CancellationToken ct = default) =>
        await MayGenerate(userId, companyId, ct) ? await closing.StartClosingAsync(userId, companyId, yearId, ct) : Denied<FiscalYearClosingView>();

    public async Task<ConfigResult<FiscalYearCarryforwardView>> CarryForwardAsync(Guid userId, Guid companyId, Guid yearId, CancellationToken ct = default) =>
        await MayGenerate(userId, companyId, ct) ? await closing.CarryForwardAsync(userId, companyId, yearId, ct) : Denied<FiscalYearCarryforwardView>();

    /// <summary>Drafts the reversal of the POSTED opening balances of a year (the newest active carryforward). Posting it follows the journal workflow.</summary>
    public async Task<ConfigResult<JournalEntryView>> ReverseOpeningAsync(Guid userId, Guid companyId, Guid yearId, YearEndReversalInput? input, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingReverse, ct)) return Denied<JournalEntryView>();
        var status = await closing.StatusAsync(companyId, yearId, ct);
        var target = status?.Carryforwards.LastOrDefault(c => c.EntryStatus == JournalStatus.Posted);
        if (target is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.Conflict, "nothing_to_reverse");
        var entry = (await journal.GetAsync(userId, companyId, target.EntryId, ct));
        if (!entry.IsOk) return entry;
        return await journal.ReverseAsync(userId, companyId, target.EntryId, entry.Value!.Header.Revision, new ReversalInput(input?.EntryDate, "Reversal of opening balances"), ct);
    }

    /// <summary>Drafts the reversal of the POSTED closing entry of a year (its opening balances must be reversed first; PostgreSQL enforces that when the reversal is posted).</summary>
    public async Task<ConfigResult<JournalEntryView>> ReverseClosingAsync(Guid userId, Guid companyId, Guid yearId, YearEndReversalInput? input, CancellationToken ct = default)
    {
        if (!await Allowed(userId, companyId, PermissionCatalog.Codes.FiscalClosingReverse, ct)) return Denied<JournalEntryView>();
        var status = await closing.StatusAsync(companyId, yearId, ct);
        var target = status?.Closings.LastOrDefault(c => c.ClosingStatus == JournalStatus.Posted);
        if (target is null) return ConfigResult<JournalEntryView>.Fail(ConfigStatus.Conflict, "nothing_to_reverse");
        var entry = (await journal.GetAsync(userId, companyId, target.ClosingEntryId, ct));
        if (!entry.IsOk) return entry;
        return await journal.ReverseAsync(userId, companyId, target.ClosingEntryId, entry.Value!.Header.Revision, new ReversalInput(input?.EntryDate, "Reversal of year-end closing"), ct);
    }
}
