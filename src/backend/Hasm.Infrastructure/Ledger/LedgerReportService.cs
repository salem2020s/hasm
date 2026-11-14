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

    /// <summary>Balance-sheet accounts are reported from the start of the latest fiscal year that received posted opening balances at or before the end date (its opening entry stands for everything earlier); null = no carryforward.</summary>
    private static async Task<DateOnly?> BsFloorAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ReportScope s, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {CompanySchema.ClosingLogic.FloorFunction}(@to)", conn, tx);
        q.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = s.To.ToDateTime(TimeOnly.MinValue) });
        return await q.ExecuteScalarAsync(ct) switch { DateTime d => DateOnly.FromDateTime(d), DateOnly d => d, _ => null };
    }

    private static void BindScope(NpgsqlParameterCollection p, ReportScope s, DateOnly? bsFloor)
    {
        p.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = s.From.ToDateTime(TimeOnly.MinValue) });
        p.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = s.To.ToDateTime(TimeOnly.MinValue) });
        p.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = (object?)s.BranchId ?? DBNull.Value });
        p.Add(new NpgsqlParameter("branches", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = s.Branches is null ? DBNull.Value : s.Branches.ToArray() });
        p.Add(new NpgsqlParameter("cc", NpgsqlDbType.Uuid) { Value = (object?)s.CostCenterId ?? DBNull.Value });
        p.Add(new NpgsqlParameter("vcc", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = s.CostCenters is null ? DBNull.Value : s.CostCenters.ToArray() });
        p.Add(new NpgsqlParameter("exclclose", NpgsqlDbType.Boolean) { Value = s.ExcludeClosing });
        p.Add(new NpgsqlParameter("bsfloor", NpgsqlDbType.Date) { Value = bsFloor is null ? DBNull.Value : bsFloor.Value.ToDateTime(TimeOnly.MinValue) });
    }

    /// <summary>
    /// The lines that count for a scope, as (account, entry date, base debit, base credit): posted or reversed entries up to the end date, filtered by branch restriction, branch and cost center.
    /// With a cost center (and its descendants), each line contributes its ALLOCATED base share on its own side; lines without allocation are outside that dimension.
    /// Viewer restriction (applied INSIDE the query): a line whose branch the viewer may see counts in full (company-wide viewers: every line); every other line counts only through
    /// its allocation shares inside the viewer's cost-center grants (@vcc = granted cost centers and descendants, base AND original-currency shares), so nothing outside the grant is ever exposed or summed.
    /// </summary>
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
      AND (acc.{AC.Statement} = 2 OR @bsfloor::date IS NULL OR e.{JE.Date} >= @bsfloor)
      AND (@branch::uuid IS NULL OR l.{JL.Branch} = @branch)
      AND ((x.full AND (@cc::uuid IS NULL OR a.line IS NOT NULL)) OR (NOT x.full AND v.line IS NOT NULL)))";

    public async Task<TrialBalanceView> TrialBalanceAsync(Guid companyId, ReportScope scope, bool includeZero, CancellationToken ct = default)
    {
        var (conn, tx, baseCurrency) = await BeginAsync(companyId, ct);
        await using var _c = conn; await using var _t = tx;
        var bsFloor = await BsFloorAsync(conn, tx, scope, ct);
        var sums = new Dictionary<Guid, (decimal Opening, decimal Debit, decimal Credit)>();
        await using (var cmd = new NpgsqlCommand(
            LinesCte + $@"
SELECT account, COALESCE(SUM(bd - bc) FILTER (WHERE d < @from), 0), COALESCE(SUM(bd) FILTER (WHERE d >= @from), 0), COALESCE(SUM(bc) FILTER (WHERE d >= @from), 0) FROM scoped GROUP BY account", conn, tx))
        {
            BindScope(cmd.Parameters, scope, bsFloor);
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
        var bsFloor = await BsFloorAsync(conn, tx, scope, ct);
        Guid[] accounts;
        bool group;
        await using (var q = new NpgsqlCommand(
            $"WITH RECURSIVE t(id) AS (SELECT {AC.Id} FROM {AC.Table} WHERE {AC.Id} = @a UNION SELECT c.{AC.Id} FROM {AC.Table} c JOIN t ON c.{AC.ParentId} = t.id) SELECT t.id, (SELECT {AC.IsGroup} FROM {AC.Table} WHERE {AC.Id} = @a) FROM t", conn, tx))
        {
            q.Parameters.AddWithValue("a", accountId);
            var ids = new List<Guid>(); group = false;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { ids.Add(r.GetGuid(0)); group = r.GetBoolean(1); }
            if (ids.Count == 0) return null;
            accounts = [.. ids];
        }
        decimal opening, debit, credit; int count;
        await using (var cmd = new NpgsqlCommand(
            LinesCte + @"
SELECT COALESCE(SUM(bd - bc) FILTER (WHERE d < @from), 0), COALESCE(SUM(bd) FILTER (WHERE d >= @from), 0), COALESCE(SUM(bc) FILTER (WHERE d >= @from), 0), count(*) FILTER (WHERE d >= @from)
FROM scoped WHERE account = ANY(@accounts)", conn, tx))
        {
            BindScope(cmd.Parameters, scope, bsFloor);
            cmd.Parameters.Add(new NpgsqlParameter("accounts", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = accounts });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            opening = r.GetDecimal(0); debit = r.GetDecimal(1); credit = r.GetDecimal(2); count = (int)r.GetInt64(3);
        }
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
            BindScope(cmd.Parameters, scope, bsFloor);
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

    // ---------------------------------------------------------------------------------------------------------------- CSV export

    private static string Csv(string? v) => v is null ? "" : v.Length > 0 && "=+-@\t\r".Contains(v[0]) ? "\"'" + v.Replace("\"", "\"\"") + "\"" : v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    private static string N(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>UTF-8 CSV (with BOM, so Arabic opens correctly) of the trial balance with exact decimals. Cells that could start a spreadsheet formula are neutralised.</summary>
    public static byte[] TrialBalanceCsv(TrialBalanceView v)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Code,Name (ar),Name (en),Level,Group,Opening,Debit,Credit,Closing," + v.BaseCurrency);
        foreach (var r in v.Rows) sb.AppendLine(string.Join(',', Csv(r.Code), Csv(r.NameAr), Csv(r.NameEn), r.Level, r.IsGroup ? "yes" : "no", N(r.OpeningBalance), N(r.PeriodDebit), N(r.PeriodCredit), N(r.ClosingBalance)));
        sb.AppendLine(string.Join(',', "TOTAL", "", "", "", "", N(v.TotalOpening), N(v.TotalDebit), N(v.TotalCredit), N(v.TotalClosing)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static byte[] GeneralLedgerCsv(GeneralLedgerView v)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Date,Number,Type,Description,Line description,Currency,Debit,Credit,Rate,Base debit,Base credit,Running balance ({v.BaseCurrency})");
        sb.AppendLine(string.Join(',', "", "", "", "Opening balance", "", "", "", "", "", "", "", N(v.OpeningBalance)));
        foreach (var l in v.Lines) sb.AppendLine(string.Join(',', l.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Csv(l.Number), l.EntryType, Csv(l.EntryDescription), Csv(l.LineDescription), l.Currency,
            N(l.Debit), N(l.Credit), l.Rate.ToString("0.0000000000", CultureInfo.InvariantCulture), N(l.BaseDebit), N(l.BaseCredit), N(l.RunningBalance)));
        sb.AppendLine(string.Join(',', "", "", "", "Closing balance", "", "", "", "", "", N(v.TotalDebit), N(v.TotalCredit), N(v.ClosingBalance)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}

/// <summary>
/// Authorized entry point for the ledger reports. <c>company.ledgerreports.view</c> and <c>.export</c> are branch-scoped: a company-wide grant sees every branch, a branch grant only
/// its own branches (the restriction is applied INSIDE the queries, so a figure never includes a branch the caller may not see). Permissions are re-read on every call.
/// </summary>
public sealed class LedgerReportManagementService(AccessService access, LedgerReportService reports, JournalService journal)
{
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

    private static ConfigResult<T>? Validate<T>(DateOnly from, DateOnly to, Guid? branchId, IReadOnlyList<Guid>? allowed, IReadOnlyList<Guid>? costCenters)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366 * 5) return ConfigResult<T>.Invalid([new("to", "invalid_range")]);
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
}
