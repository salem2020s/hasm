using System.Globalization;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using AC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostCenter;
using JA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalAllocation;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;

namespace Hasm.Infrastructure.Ledger;

/// <summary>What a report covers. Dates are entry dates (inclusive). <see cref="Branches"/> is the authorization-derived branch restriction (null = every branch).</summary>
public sealed record ReportScope(DateOnly From, DateOnly To, Guid? BranchId, Guid? CostCenterId, IReadOnlyList<Guid>? Branches, IReadOnlyList<Guid>? CostCenters = null, bool ExcludeClosing = false);

/// <summary>One trial-balance row. Balances are signed in base currency (debit minus credit); <c>Level</c> is the depth in the chart (groups are roll-ups of their leaves, never double counted).</summary>
public sealed record TrialBalanceRow(
    Guid AccountId, string Code, string? NameAr, string? NameEn, Guid? ParentId, int Level, bool IsGroup, short Type, short Nature,
    decimal OpeningBalance, decimal PeriodDebit, decimal PeriodCredit, decimal ClosingBalance);

/// <summary>The trial balance: only POSTED and REVERSED entries count (reversals are posted entries that net them out); totals are taken over leaf accounts only.</summary>
public sealed record TrialBalanceView(
    DateOnly From, DateOnly To, string BaseCurrency, IReadOnlyList<TrialBalanceRow> Rows, decimal TotalOpening, decimal TotalDebit, decimal TotalCredit, decimal TotalClosing, bool Balanced);

public sealed record GeneralLedgerLine(
    Guid EntryId, string Number, DateOnly EntryDate, string? EntryDescription, string? LineDescription, Guid AccountId, string Currency, decimal Debit, decimal Credit, decimal Rate,
    long? RateId, decimal BaseDebit, decimal BaseCredit, Guid BranchId, bool IsRounding, string EntryType, decimal RunningBalance);

public sealed record GeneralLedgerView(
    DateOnly From, DateOnly To, string BaseCurrency, Guid AccountId, bool IncludesDescendants, decimal OpeningBalance, decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance,
    int TotalLines, int Offset, IReadOnlyList<GeneralLedgerLine> Lines);

/// <summary>
/// Read-only ledger reports (Phase 3.2B S6) computed from the actual posted journal in one consistent query each (REPEATABLE READ snapshot): nothing is cached, derived or
/// invented, so reversals, later rate entries and master-data renames can never change a historical figure (amounts are the immutable base amounts stored on the lines).
/// <b>No authorization here by design:</b> <see cref="LedgerReportManagementService"/> wraps it and derives the branch restriction from the caller's grants.
/// </summary>
public sealed class LedgerReportService(CompanyDatabaseResolver resolver)
{
    private const string PostedStatuses = "(4, 5)";

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx, string BaseCurrency)> BeginAsync(Guid companyId, CancellationToken ct)
    {
        var conn = await resolver.OpenAsync(companyId, ct);
        var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var q = new NpgsqlCommand($"SELECT {CompanySchema.CompanyCurrency.Code} FROM {CompanySchema.CompanyCurrency.Table} WHERE {CompanySchema.CompanyCurrency.IsBase}", conn, tx);
        var code = (await q.ExecuteScalarAsync(ct)) as string ?? "";
        return (conn, tx, code.Trim());
    }

    private static void BindScope(NpgsqlParameterCollection p, ReportScope s)
    {
        p.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = s.From.ToDateTime(TimeOnly.MinValue) });
        p.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = s.To.ToDateTime(TimeOnly.MinValue) });
        p.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = (object?)s.BranchId ?? DBNull.Value });
        p.Add(new NpgsqlParameter("branches", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = s.Branches is null ? DBNull.Value : s.Branches.ToArray() });
        p.Add(new NpgsqlParameter("cc", NpgsqlDbType.Uuid) { Value = (object?)s.CostCenterId ?? DBNull.Value });
        p.Add(new NpgsqlParameter("vcc", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = s.CostCenters is null ? DBNull.Value : s.CostCenters.ToArray() });
        p.Add(new NpgsqlParameter("exclclose", NpgsqlDbType.Boolean) { Value = s.ExcludeClosing });
    }

    /// <summary>
    /// Year-end OPENING entries (and their reversals) never count: they only restate balances that the ledger history already holds, so every balance is the same whatever the carryforward state
    /// (no double counting, earlier years and comparatives never change, any date range is reproducible). Closing entries count (they move profit and loss into equity) unless the report excludes them.
    /// The lines that count for a scope, as (account, entry date, base debit, base credit): posted or reversed entries up to the end date, filtered by branch restriction, branch and cost center.
    /// With a cost center (and its descendants), each line contributes its ALLOCATED base share on its own side; lines without allocation are outside that dimension.
    /// Viewer restriction (applied INSIDE the query): a line whose branch the viewer may see counts in full (company-wide viewers: every line); every other line counts only through
    /// its allocation shares inside the viewer's cost-center grants (@vcc = granted cost centers and descendants, base AND original-currency shares), so nothing outside the grant is ever exposed or summed.
    /// </summary>
    internal static string LinesSql => LinesCte;
    internal static void Bind(NpgsqlParameterCollection p, ReportScope s) => BindScope(p, s);

    private static string LinesCte => $@"
WITH RECURSIVE cc_tree(id) AS (
    SELECT c.{CO.Id} FROM {CO.Table} c WHERE @cc::uuid IS NULL OR c.{CO.Id} = @cc
    UNION
    SELECT c.{CO.Id} FROM {CO.Table} c JOIN cc_tree t ON c.{CO.ParentId} = t.id),
alloc AS (
    SELECT ja.{JA.Line} AS line, SUM(ja.{JA.Base}) AS base_sum FROM {JA.Table} ja WHERE ja.{JA.CC} IN (SELECT id FROM cc_tree) GROUP BY ja.{JA.Line}),
alloc_v AS (
    SELECT ja.{JA.Line} AS line, SUM(ja.{JA.Base}) AS base_sum, SUM(ja.{JA.Amount}) AS amount_sum FROM {JA.Table} ja
    WHERE @vcc::uuid[] IS NOT NULL AND ja.{JA.CC} = ANY(@vcc) AND ja.{JA.CC} IN (SELECT id FROM cc_tree) GROUP BY ja.{JA.Line}),
scoped AS (
    SELECT l.{JL.Id} AS lid, l.{JL.Account} AS account, e.{JE.Date} AS d, e.{JE.Number} AS number, l.{JL.No} AS line_no,
           CASE WHEN x.full THEN (CASE WHEN @cc::uuid IS NULL THEN l.{JL.BaseDebit} WHEN l.{JL.BaseDebit} > 0 THEN a.base_sum ELSE 0 END) WHEN l.{JL.BaseDebit} > 0 THEN v.base_sum ELSE 0 END AS bd,
           CASE WHEN x.full THEN (CASE WHEN @cc::uuid IS NULL THEN l.{JL.BaseCredit} WHEN l.{JL.BaseCredit} > 0 THEN a.base_sum ELSE 0 END) WHEN l.{JL.BaseCredit} > 0 THEN v.base_sum ELSE 0 END AS bc,
           CASE WHEN x.full THEN l.{JL.Debit} WHEN l.{JL.Debit} > 0 THEN v.amount_sum ELSE 0 END AS dr,
           CASE WHEN x.full THEN l.{JL.Credit} WHEN l.{JL.Credit} > 0 THEN v.amount_sum ELSE 0 END AS cr
    FROM {JL.Table} l
    JOIN {JE.Table} e ON e.{JE.Id} = l.{JL.Entry}
    JOIN {AC.Table} acc ON acc.{AC.Id} = l.{JL.Account}
    CROSS JOIN LATERAL (SELECT (@branches::uuid[] IS NULL OR l.{JL.Branch} = ANY(@branches)) AS full) x
    LEFT JOIN alloc a ON a.line = l.{JL.Id}
    LEFT JOIN alloc_v v ON v.line = l.{JL.Id}
    WHERE e.{JE.Status} IN {PostedStatuses} AND e.{JE.Date} <= @to
      AND (NOT @exclclose OR COALESCE({CompanySchema.ClosingLogic.KindFunction}(e.{JE.Id}), 0) NOT IN (1, 3))
      AND NOT EXISTS (SELECT 1 FROM {CompanySchema.FiscalYearCarryforward.Table} k WHERE k.{CompanySchema.FiscalYearCarryforward.EntryId} IN (e.{JE.Id}, e.{JE.Reverses}))
      AND (@branch::uuid IS NULL OR l.{JL.Branch} = @branch)
      AND ((x.full AND (@cc::uuid IS NULL OR a.line IS NOT NULL)) OR (NOT x.full AND v.line IS NOT NULL)))";

    public async Task<TrialBalanceView> TrialBalanceAsync(Guid companyId, ReportScope scope, bool includeZero, CancellationToken ct = default)
    {
        var (conn, tx, baseCurrency) = await BeginAsync(companyId, ct);
        await using var _c = conn; await using var _t = tx;
        var sums = new Dictionary<Guid, (decimal Opening, decimal Debit, decimal Credit)>();
        await using (var cmd = new NpgsqlCommand(
            LinesCte + $@"
SELECT account, COALESCE(SUM(bd - bc) FILTER (WHERE d < @from), 0), COALESCE(SUM(bd) FILTER (WHERE d >= @from), 0), COALESCE(SUM(bc) FILTER (WHERE d >= @from), 0) FROM scoped GROUP BY account", conn, tx))
        {
            BindScope(cmd.Parameters, scope);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sums[r.GetGuid(0)] = (r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3));
        }
        var accounts = new List<(Guid Id, string Code, string? Ar, string? En, Guid? Parent, bool Group, short Type, short Nature)>();
        await using (var cmd = new NpgsqlCommand($"SELECT {AC.Id}, {AC.Code}, {AC.NameAr}, {AC.NameEn}, {AC.ParentId}, {AC.IsGroup}, {AC.Type}, {AC.Nature} FROM {AC.Table} ORDER BY upper({AC.Code})", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                accounts.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetBoolean(5), r.GetInt16(6), r.GetInt16(7)));
        await tx.CommitAsync(ct);

        // Roll the leaf figures up the tree (groups never hold postings, so nothing is counted twice).
        var byId = accounts.ToDictionary(a => a.Id);
        var children = accounts.Where(a => a.Parent is not null).GroupBy(a => a.Parent!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var total = new Dictionary<Guid, (decimal O, decimal D, decimal C)>();
        (decimal O, decimal D, decimal C) Sum(Guid id)
        {
            if (total.TryGetValue(id, out var cached)) return cached;
            var own = sums.TryGetValue(id, out var s) ? s : (0m, 0m, 0m);
            var acc = (own.Item1, own.Item2, own.Item3);
            if (children.TryGetValue(id, out var kids)) foreach (var k in kids) { var c = Sum(k); acc = (acc.Item1 + c.O, acc.Item2 + c.D, acc.Item3 + c.C); }
            return total[id] = acc;
        }
        var rows = new List<TrialBalanceRow>();
        void Walk(Guid id, int level)
        {
            var a = byId[id]; var t = Sum(id);
            if (includeZero || t.O != 0 || t.D != 0 || t.C != 0)
                rows.Add(new TrialBalanceRow(a.Id, a.Code, a.Ar, a.En, a.Parent, level, a.Group, a.Type, a.Nature, t.O, t.D, t.C, t.O + t.D - t.C));
            if (children.TryGetValue(id, out var kids)) foreach (var k in kids.OrderBy(k => byId[k].Code, StringComparer.OrdinalIgnoreCase)) Walk(k, level + 1);
        }
        foreach (var root in accounts.Where(a => a.Parent is null)) Walk(root.Id, 0);
        var leaves = accounts.Where(a => !a.Group).Select(a => sums.TryGetValue(a.Id, out var s) ? s : (0m, 0m, 0m)).ToList();
        var o = leaves.Sum(x => x.Item1); var d = leaves.Sum(x => x.Item2); var c2 = leaves.Sum(x => x.Item3);
        return new TrialBalanceView(scope.From, scope.To, baseCurrency, rows, o, d, c2, o + d - c2, o + d - c2 == 0);
    }

    /// <summary>Account activity with a running base balance (opening balance before <c>From</c>, then every line in date, number, line order). A group account includes all its descendants.</summary>
    public async Task<GeneralLedgerView?> GeneralLedgerAsync(Guid companyId, Guid accountId, ReportScope scope, int limit, int offset, CancellationToken ct = default)
    {
        var (conn, tx, baseCurrency) = await BeginAsync(companyId, ct);
        await using var _c = conn; await using var _t = tx;
        var (accounts, group, opening, debit, credit, count) = await GlHeaderAsync(conn, tx, accountId, scope, ct) ?? default;
        if (accounts is null) return null;
        var lines = new List<GeneralLedgerLine>();
        await using (var cmd = new NpgsqlCommand(
            $@"{LinesCte},
ranked AS (
    SELECT s.lid, s.d, s.number, s.line_no, s.bd, s.bc, s.dr, s.cr, SUM(s.bd - s.bc) OVER (ORDER BY s.d, s.number, s.line_no ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
    FROM scoped s WHERE s.account = ANY(@accounts) AND s.d >= @from)
SELECT e.{JE.Id}, e.{JE.Number}, e.{JE.Date}, e.{JE.Desc}, l.{JL.Desc}, l.{JL.Account}, l.{JL.Cur}, r.dr, r.cr, l.{JL.Rate}, l.{JL.RateId}, r.bd, r.bc, l.{JL.Branch}, l.{JL.IsRounding}, e.{JE.Type}, @opening + r.running
FROM ranked r JOIN {JL.Table} l ON l.{JL.Id} = r.lid JOIN {JE.Table} e ON e.{JE.Id} = l.{JL.Entry}
ORDER BY r.d, r.number, r.line_no LIMIT @l OFFSET @o", conn, tx))
        {
            BindScope(cmd.Parameters, scope);
            cmd.Parameters.Add(new NpgsqlParameter("accounts", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = accounts });
            cmd.Parameters.AddWithValue("opening", opening); cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 5000)); cmd.Parameters.AddWithValue("o", Math.Max(0, offset));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(new GeneralLedgerLine(r.GetGuid(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetGuid(5),
                    r.GetString(6).Trim(), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetDecimal(11), r.GetDecimal(12), r.GetGuid(13), r.GetBoolean(14),
                    ((JournalType)r.GetInt16(15)).ToString().ToLowerInvariant(), r.GetDecimal(16)));
        }
        await tx.CommitAsync(ct);
        return new GeneralLedgerView(scope.From, scope.To, baseCurrency, accountId, group, opening, debit, credit, opening + debit - credit, count, Math.Max(0, offset), lines);
    }

private async Task<(Guid[]? Accounts, bool Group, decimal Opening, decimal Debit, decimal Credit, int Count)?> GlHeaderAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid accountId, ReportScope scope, CancellationToken ct)
    {
        Guid[] accounts;
        bool group;
        await using (var q = new NpgsqlCommand(
            $"WITH RECURSIVE t(id) AS (SELECT {AC.Id} FROM {AC.Table} WHERE {AC.Id} = @a UNION SELECT c.{AC.Id} FROM {AC.Table} c JOIN t ON c.{AC.ParentId} = t.id) SELECT t.id, (SELECT {AC.IsGroup} FROM {AC.Table} WHERE {AC.Id} = @a) FROM t", conn, tx))
        {
            q.Parameters.AddWithValue("a", accountId);
            var ids = new List<Guid>(); group = false;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { ids.Add(r.GetGuid(0)); group = r.GetBoolean(1); }
            if (ids.Count == 0) return (null, false, 0, 0, 0, 0);
            accounts = [.. ids];
        }
        decimal opening, debit, credit; int count;
        await using (var cmd = new NpgsqlCommand(
            LinesCte + @"
SELECT COALESCE(SUM(bd - bc) FILTER (WHERE d < @from), 0), COALESCE(SUM(bd) FILTER (WHERE d >= @from), 0), COALESCE(SUM(bc) FILTER (WHERE d >= @from), 0), count(*) FILTER (WHERE d >= @from)
FROM scoped WHERE account = ANY(@accounts)", conn, tx))
        {
            BindScope(cmd.Parameters, scope);
            cmd.Parameters.Add(new NpgsqlParameter("accounts", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = accounts });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            opening = r.GetDecimal(0); debit = r.GetDecimal(1); credit = r.GetDecimal(2); count = (int)r.GetInt64(3);
        }
        return (accounts, group, opening, debit, credit, count);
    }

    /// <summary>
    /// The COMPLETE authorized line set of an account for an export, streamed from one consistent snapshot (no page limit, no row cap: the caller applies the documented export limit to <see cref="GeneralLedgerExport.Header"/>.TotalLines BEFORE reading).
    /// Dispose it to release the snapshot.
    /// </summary>
    public async Task<GeneralLedgerExport?> OpenGeneralLedgerExportAsync(Guid companyId, Guid accountId, ReportScope scope, CancellationToken ct = default)
    {
        var (conn, tx, baseCurrency) = await BeginAsync(companyId, ct);
        try
        {
            var h = await GlHeaderAsync(conn, tx, accountId, scope, ct);
            if (h is null || h.Value.Accounts is null) { await tx.DisposeAsync(); await conn.DisposeAsync(); return null; }
            var (accounts, group, opening, debit, credit, count) = h.Value;
            var header = new GeneralLedgerView(scope.From, scope.To, baseCurrency, accountId, group, opening, debit, credit, opening + debit - credit, count, 0, []);
            return new GeneralLedgerExport(conn, tx, header, accounts, scope);
        }
        catch { await tx.DisposeAsync(); await conn.DisposeAsync(); throw; }
    }

    /// <summary>CSV of an in-memory trial balance through the shared renderer (the same code path as an export).</summary>
    public static byte[] TrialBalanceCsv(TrialBalanceView v) => LedgerReportDocuments.ToCsv(LedgerReportDocuments.FromTrialBalance(v, new ReportScope(v.From, v.To, null, null, null), true));

    /// <summary>CSV of an in-memory general-ledger page through the shared renderer.</summary>
    public static byte[] GeneralLedgerCsv(GeneralLedgerView v) => LedgerReportDocuments.ToCsv(LedgerReportDocuments.FromGeneralLedgerView(v));

    // CSV (and XLSX/PDF) are produced by the shared export service from a ReportDocument (see Hasm.Infrastructure.Reporting); nothing here truncates or buffers an export.
}

/// <summary>An open consistent snapshot streaming every line of one account (see <see cref="LedgerReportService.OpenGeneralLedgerExportAsync"/>).</summary>
public sealed class GeneralLedgerExport(NpgsqlConnection conn, NpgsqlTransaction tx, GeneralLedgerView header, Guid[] accounts, ReportScope scope) : IAsyncDisposable
{
    public GeneralLedgerView Header { get; } = header;

    public async IAsyncEnumerable<GeneralLedgerLine> Lines([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(
            LedgerReportService.LinesSql + $@",
ranked AS (
    SELECT s.lid, s.d, s.number, s.line_no, s.bd, s.bc, s.dr, s.cr, SUM(s.bd - s.bc) OVER (ORDER BY s.d, s.number, s.line_no ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
    FROM scoped s WHERE s.account = ANY(@accounts) AND s.d >= @from)
SELECT e.{JE.Id}, e.{JE.Number}, e.{JE.Date}, e.{JE.Desc}, l.{JL.Desc}, l.{JL.Account}, l.{JL.Cur}, r.dr, r.cr, l.{JL.Rate}, l.{JL.RateId}, r.bd, r.bc, l.{JL.Branch}, l.{JL.IsRounding}, e.{JE.Type}, @opening + r.running
FROM ranked r JOIN {JL.Table} l ON l.{JL.Id} = r.lid JOIN {JE.Table} e ON e.{JE.Id} = l.{JL.Entry}
ORDER BY r.d, r.number, r.line_no", conn, tx);
        LedgerReportService.Bind(cmd.Parameters, scope);
        cmd.Parameters.Add(new NpgsqlParameter("accounts", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = accounts });
        cmd.Parameters.AddWithValue("opening", Header.OpeningBalance);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            yield return new GeneralLedgerLine(r.GetGuid(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetGuid(5),
                r.GetString(6).Trim(), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetDecimal(11), r.GetDecimal(12), r.GetGuid(13), r.GetBoolean(14),
                ((JournalType)r.GetInt16(15)).ToString().ToLowerInvariant(), r.GetDecimal(16));
    }

    public async ValueTask DisposeAsync()
    {
        try { await tx.CommitAsync(); } catch { /* read-only snapshot */ }
        await tx.DisposeAsync(); await conn.DisposeAsync();
    }
}

/// <summary>
/// Authorized entry point for the ledger reports. <c>company.ledgerreports.view</c> and <c>.export</c> are branch-scoped: a company-wide grant sees every branch, a branch grant only
/// its own branches (the restriction is applied INSIDE the queries, so a figure never includes a branch the caller may not see). Permissions are re-read on every call.
/// </summary>
public sealed class LedgerReportManagementService(AccessService access, LedgerReportService reports, JournalService journal, Hasm.Infrastructure.Reporting.ReportOptions? reportOptions = null, FiscalCalendarService? fiscal = null)
{
    private readonly int maxRangeYears = (reportOptions ?? new Hasm.Infrastructure.Reporting.ReportOptions()).MaxRangeYears;

    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    /// <summary>
    /// Null branches = every branch (company-wide grant); otherwise the branches the caller holds the permission for (possibly none) plus, when cost-center-scoped grants exist,
    /// the granted cost centers with their descendants. Fails (Denied) when the caller holds the permission nowhere.
    /// </summary>
    private async Task<(bool Allowed, IReadOnlyList<Guid>? Branches, IReadOnlyList<Guid>? CostCenters)> ScopeAsync(Guid userId, Guid companyId, string code, CancellationToken ct)
    {
        var eff = await access.GetEffectivePermissionsAsync(userId, companyId, ct);
        if (eff.Has(code, ScopeTarget.Company)) return (true, null, null);
        var branches = eff.Grants.Where(g => g.Scope == ScopeKind.Branch && g.ScopeId is not null && g.Permissions.Contains(code)).Select(g => g.ScopeId!.Value).Distinct().ToList();
        var roots = JournalManagementService.CostCenterRoots(eff, code);
        if (branches.Count == 0 && roots.Count == 0) return (false, null, null);
        return (true, branches, roots.Count == 0 ? null : await journal.CostCenterClosureAsync(companyId, roots, ct));
    }

    private ConfigResult<T>? Validate<T>(DateOnly from, DateOnly to, Guid? branchId, IReadOnlyList<Guid>? allowed, IReadOnlyList<Guid>? costCenters)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366 * maxRangeYears) return ConfigResult<T>.Invalid([new("to", "invalid_range")]);
        // A branch filter outside the caller's branches is refused for pure branch viewers; with cost-center grants the figures are limited by the grant inside the query anyway.
        if (branchId is { } b && allowed is not null && costCenters is null && !allowed.Contains(b)) return Denied<T>();
        return null;
    }

    public async Task<ConfigResult<TrialBalanceView>> TrialBalanceAsync(Guid userId, Guid companyId, DateOnly from, DateOnly to, Guid? branchId, Guid? costCenterId, bool includeZero, bool export, CancellationToken ct = default, bool excludeClosing = false)
    {
        var (ok, allowed, ccs) = await ScopeAsync(userId, companyId, export ? PermissionCatalog.Codes.LedgerReportsExport : PermissionCatalog.Codes.LedgerReportsView, ct);
        if (!ok) return Denied<TrialBalanceView>();
        if (Validate<TrialBalanceView>(from, to, branchId, allowed, ccs) is { } bad) return bad;
        return ConfigResult<TrialBalanceView>.Ok(await reports.TrialBalanceAsync(companyId, new ReportScope(from, to, branchId, costCenterId, allowed, ccs, excludeClosing), includeZero, ct));
    }

    public async Task<ConfigResult<GeneralLedgerView>> GeneralLedgerAsync(Guid userId, Guid companyId, Guid accountId, DateOnly from, DateOnly to, Guid? branchId, Guid? costCenterId, int limit, int offset, bool export, CancellationToken ct = default, bool excludeClosing = false)
    {
        var (ok, allowed, ccs) = await ScopeAsync(userId, companyId, export ? PermissionCatalog.Codes.LedgerReportsExport : PermissionCatalog.Codes.LedgerReportsView, ct);
        if (!ok) return Denied<GeneralLedgerView>();
        if (Validate<GeneralLedgerView>(from, to, branchId, allowed, ccs) is { } bad) return bad;
        var v = await reports.GeneralLedgerAsync(companyId, accountId, new ReportScope(from, to, branchId, costCenterId, allowed, ccs, excludeClosing), export ? 5000 : limit, offset, ct);
        return v is null ? ConfigResult<GeneralLedgerView>.Fail(ConfigStatus.NotFound, "account_not_found") : ConfigResult<GeneralLedgerView>.Ok(v);
    }

    /// <summary>The trial balance as an export document. Needs the EXPORT permission; the authorization and branch/cost-center restriction are applied inside the query, before anything is produced.</summary>
    public async Task<ConfigResult<Hasm.Infrastructure.Reporting.ReportDocument>> TrialBalanceDocumentAsync(Guid userId, Guid companyId, DateOnly from, DateOnly to, Guid? branchId, Guid? costCenterId, bool includeZero, bool excludeClosing, CancellationToken ct = default)
    {
        var (ok, allowed, ccs) = await ScopeAsync(userId, companyId, PermissionCatalog.Codes.LedgerReportsExport, ct);
        if (!ok) return Denied<Hasm.Infrastructure.Reporting.ReportDocument>();
        if (Validate<Hasm.Infrastructure.Reporting.ReportDocument>(from, to, branchId, allowed, ccs) is { } bad) return bad;
        var scope = new ReportScope(from, to, branchId, costCenterId, allowed, ccs, excludeClosing);
        var view = await reports.TrialBalanceAsync(companyId, scope, includeZero, ct);
        return ConfigResult<Hasm.Infrastructure.Reporting.ReportDocument>.Ok(LedgerReportDocuments.FromTrialBalance(view, scope, includeZero));
    }

    /// <summary>The COMPLETE general ledger of an account as a streamed export document (no page limit). The caller disposes the document.</summary>
    public async Task<ConfigResult<Hasm.Infrastructure.Reporting.ReportDocument>> GeneralLedgerDocumentAsync(Guid userId, Guid companyId, Guid accountId, DateOnly from, DateOnly to, Guid? branchId, Guid? costCenterId, bool excludeClosing, CancellationToken ct = default)
    {
        var (ok, allowed, ccs) = await ScopeAsync(userId, companyId, PermissionCatalog.Codes.LedgerReportsExport, ct);
        if (!ok) return Denied<Hasm.Infrastructure.Reporting.ReportDocument>();
        if (Validate<Hasm.Infrastructure.Reporting.ReportDocument>(from, to, branchId, allowed, ccs) is { } bad) return bad;
        var scope = new ReportScope(from, to, branchId, costCenterId, allowed, ccs, excludeClosing);
        var export = await reports.OpenGeneralLedgerExportAsync(companyId, accountId, scope, ct);
        return export is null
            ? ConfigResult<Hasm.Infrastructure.Reporting.ReportDocument>.Fail(ConfigStatus.NotFound, "account_not_found")
            : ConfigResult<Hasm.Infrastructure.Reporting.ReportDocument>.Ok(LedgerReportDocuments.FromGeneralLedger(export, scope));
    }

    /// <summary>The report definitions the caller may view, with whether they may also export. A definition is listed when its view permission is held anywhere (company, branch or cost-center scope).</summary>
    public async Task<IReadOnlyList<(Hasm.Infrastructure.Reporting.ReportDefinition Definition, bool CanExport)>> DefinitionsAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        var list = new List<(Hasm.Infrastructure.Reporting.ReportDefinition, bool)>();
        foreach (var d in Hasm.Infrastructure.Reporting.ReportDefinitions.All)
            if ((await ScopeAsync(userId, companyId, d.ViewPermission, ct)).Allowed) list.Add((d, (await ScopeAsync(userId, companyId, d.ExportPermission, ct)).Allowed));
        return list;
    }

    /// <summary>Resolves a fiscal year / fiscal period / custom range and its comparison (see <see cref="Hasm.Infrastructure.Reporting.ReportPeriods"/>). Needs a report view permission; nothing here reads financial data.</summary>
    public async Task<ConfigResult<Hasm.Infrastructure.Reporting.ResolvedPeriod>> ResolvePeriodAsync(Guid userId, Guid companyId, Hasm.Infrastructure.Reporting.ReportPeriodRequest request, CancellationToken ct = default)
    {
        if ((await DefinitionsAsync(userId, companyId, ct)).Count == 0) return Denied<Hasm.Infrastructure.Reporting.ResolvedPeriod>();
        var years = request.FiscalYearId is null && request.FiscalPeriodId is null && request.Comparison == Hasm.Infrastructure.Reporting.ComparisonMode.None || fiscal is null
            ? (IReadOnlyList<FiscalYearView>)[] : await fiscal.ListAsync(companyId, ct);
        return Hasm.Infrastructure.Reporting.ReportPeriods.Resolve(years, request, maxRangeYears);
    }
}
