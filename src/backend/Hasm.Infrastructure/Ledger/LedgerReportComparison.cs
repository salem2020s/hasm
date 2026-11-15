using Hasm.Infrastructure.Reporting;

namespace Hasm.Infrastructure.Ledger;

/// <summary>One account of a (possibly comparative) trial balance. The comparative figures are null when no comparison was requested or none exists; amounts are base-currency, debit minus credit.</summary>
public sealed record ComparativeTrialBalanceRow(
    Guid AccountId, string Code, string? NameAr, string? NameEn, Guid? ParentId, int Level, bool IsGroup, short Type, short Nature,
    decimal Opening, decimal Debit, decimal Credit, decimal Closing, decimal? CmpOpening, decimal? CmpDebit, decimal? CmpCredit, decimal? CmpClosing)
{
    public decimal? Change => CmpClosing is null ? null : Closing - CmpClosing;
}

/// <summary>The trial balance with an optional comparative period computed by the SAME engine over the comparative range (actual posted values only).</summary>
public sealed record ComparativeTrialBalanceView(
    TrialBalanceView Current, TrialBalanceView? Comparative, IReadOnlyList<ComparativeTrialBalanceRow> Rows, ResolvedPeriod Period, string Sort, int? MaxLevel);

public static class TrialBalanceSorts
{
    public const string Code = "code", Name = "name", ClosingDesc = "closing_desc", ClosingAsc = "closing_asc";
    public static bool IsValid(string? s) => s is null or "" or Code or Name or ClosingDesc or ClosingAsc;
}

/// <summary>Pure merge of the current and comparative trial balances: one ordered hierarchy, deterministic sorting among siblings (ties by code), an optional depth limit and the zero-row filter.</summary>
public static class ComparativeTrialBalance
{
    public static IReadOnlyList<ComparativeTrialBalanceRow> Merge(TrialBalanceView current, TrialBalanceView? comparative, bool includeZero, int? maxLevel, string? sort)
    {
        var cmp = comparative?.Rows.ToDictionary(r => r.AccountId);
        var cur = current.Rows.ToDictionary(r => r.AccountId);
        var ids = new HashSet<Guid>(cur.Keys);
        if (cmp is not null) foreach (var id in cmp.Keys) ids.Add(id);
        var merged = new Dictionary<Guid, ComparativeTrialBalanceRow>();
        foreach (var id in ids)
        {
            var c = cur.GetValueOrDefault(id); var p = cmp?.GetValueOrDefault(id);
            var basis = c ?? p!;
            var row = new ComparativeTrialBalanceRow(id, basis.Code, basis.NameAr, basis.NameEn, basis.ParentId, basis.Level, basis.IsGroup, basis.Type, basis.Nature,
                c?.OpeningBalance ?? 0, c?.PeriodDebit ?? 0, c?.PeriodCredit ?? 0, c?.ClosingBalance ?? 0,
                comparative is null ? null : p?.OpeningBalance ?? 0, comparative is null ? null : p?.PeriodDebit ?? 0, comparative is null ? null : p?.PeriodCredit ?? 0, comparative is null ? null : p?.ClosingBalance ?? 0);
            var zero = row.Opening == 0 && row.Debit == 0 && row.Credit == 0 && (row.CmpOpening ?? 0) == 0 && (row.CmpDebit ?? 0) == 0 && (row.CmpCredit ?? 0) == 0;
            if (includeZero || !zero) merged[id] = row;
        }
        var children = merged.Values.Where(r => r.ParentId is not null && merged.ContainsKey(r.ParentId.Value)).GroupBy(r => r.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        IEnumerable<ComparativeTrialBalanceRow> Order(IEnumerable<ComparativeTrialBalanceRow> rows) => sort switch
        {
            TrialBalanceSorts.Name => rows.OrderBy(r => r.NameEn ?? r.NameAr ?? r.Code, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase),
            TrialBalanceSorts.ClosingDesc => rows.OrderByDescending(r => r.Closing).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase),
            TrialBalanceSorts.ClosingAsc => rows.OrderBy(r => r.Closing).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase),
        };
        var result = new List<ComparativeTrialBalanceRow>(merged.Count);
        void Walk(ComparativeTrialBalanceRow r, int level)
        {
            if (maxLevel is { } m && level > m) return;
            result.Add(r with { Level = level });
            if (children.TryGetValue(r.AccountId, out var kids)) foreach (var k in Order(kids)) Walk(k, level + 1);
        }
        // roots: rows without a parent in the merged set (an ancestor is always present when a descendant is, because roll-ups are non-zero too)
        foreach (var root in Order(merged.Values.Where(r => r.ParentId is null || !merged.ContainsKey(r.ParentId.Value)))) Walk(root, 0);
        return result;
    }
}

public enum ReconciliationStatus { Passed = 1, Failed = 2, NotApplicable = 3 }

public sealed record ReconciliationDifference(Guid AccountId, string Code, string Item, decimal Expected, decimal Actual);

public sealed record ReconciliationCheck(string Code, ReconciliationStatus Status, int Differences, IReadOnlyList<ReconciliationDifference> Samples);

public sealed record ReconciliationView(DateOnly From, DateOnly To, string BaseCurrency, IReadOnlyList<ReconciliationCheck> Checks)
{
    public bool AllPassed => Checks.All(c => c.Status != ReconciliationStatus.Failed);
}

/// <summary>Account figures computed by the line-by-line running-balance path of the general ledger (independent of the grouped sums of the trial balance).</summary>
public sealed record LedgerAccountFigures(decimal Opening, decimal Debit, decimal Credit, decimal Closing);

/// <summary>
/// The reconciliation checks as a pure function over the figures the engine produced: the trial balance against the general ledger's own running balances, group roll-ups, the balanced total and the continuity of
/// balances across the period start. A failing check lists the accounts and amounts that differ; nothing is adjusted or hidden.
/// </summary>
public static class ReconciliationChecks
{
    private const int MaxSamples = 50;

    public static IReadOnlyList<ReconciliationCheck> Evaluate(TrialBalanceView tb, TrialBalanceView? dayBefore, IReadOnlyDictionary<Guid, LedgerAccountFigures> ledger, bool restrictedScope)
    {
        var checks = new List<ReconciliationCheck>();
        var byId = tb.Rows.ToDictionary(r => r.AccountId);

        // 1) debits equal credits (a branch or cost-center restricted view legitimately shows only part of a document, so the check does not apply)
        checks.Add(restrictedScope
            ? new ReconciliationCheck("trial_balance_balances", ReconciliationStatus.NotApplicable, 0, [])
            : tb.TotalClosing == 0 && tb.TotalDebit == tb.TotalCredit
                ? new ReconciliationCheck("trial_balance_balances", ReconciliationStatus.Passed, 0, [])
                : new ReconciliationCheck("trial_balance_balances", ReconciliationStatus.Failed, 1, [new(Guid.Empty, "", "closing", 0, tb.TotalClosing)]));

        // 2) every group equals the sum of its children (nothing counted twice or dropped)
        var roll = new List<ReconciliationDifference>();
        foreach (var g in tb.Rows.Where(r => r.IsGroup))
        {
            var kids = tb.Rows.Where(r => r.ParentId == g.AccountId).ToList();
            void Cmp(string item, decimal expected, decimal actual) { if (expected != actual) roll.Add(new ReconciliationDifference(g.AccountId, g.Code, item, expected, actual)); }
            Cmp("opening", kids.Sum(k => k.OpeningBalance), g.OpeningBalance); Cmp("debit", kids.Sum(k => k.PeriodDebit), g.PeriodDebit);
            Cmp("credit", kids.Sum(k => k.PeriodCredit), g.PeriodCredit); Cmp("closing", kids.Sum(k => k.ClosingBalance), g.ClosingBalance);
        }
        checks.Add(Result("group_roll_up", roll));

        // 3) the general ledger of every postable account (its own running balance chain) equals the trial balance row
        var gl = new List<ReconciliationDifference>();
        foreach (var r in tb.Rows.Where(r => !r.IsGroup))
        {
            var f = ledger.GetValueOrDefault(r.AccountId) ?? new LedgerAccountFigures(0, 0, 0, 0);
            void Cmp(string item, decimal expected, decimal actual) { if (expected != actual) gl.Add(new ReconciliationDifference(r.AccountId, r.Code, item, expected, actual)); }
            Cmp("opening", r.OpeningBalance, f.Opening); Cmp("debit", r.PeriodDebit, f.Debit); Cmp("credit", r.PeriodCredit, f.Credit); Cmp("closing", r.ClosingBalance, f.Closing);
        }
        foreach (var (id, f) in ledger)
            if (!byId.ContainsKey(id) && (f.Opening != 0 || f.Debit != 0 || f.Credit != 0 || f.Closing != 0)) gl.Add(new ReconciliationDifference(id, "", "missing_in_trial_balance", 0, f.Closing));
        checks.Add(Result("ledger_matches_trial_balance", gl));

        // 4) the opening balance of the period equals the closing balance of the day before
        if (dayBefore is null) checks.Add(new ReconciliationCheck("opening_equals_previous_closing", ReconciliationStatus.NotApplicable, 0, []));
        else
        {
            var prev = dayBefore.Rows.ToDictionary(r => r.AccountId);
            var cont = new List<ReconciliationDifference>();
            foreach (var r in tb.Rows) { var p = prev.GetValueOrDefault(r.AccountId)?.ClosingBalance ?? 0; if (p != r.OpeningBalance) cont.Add(new ReconciliationDifference(r.AccountId, r.Code, "opening", p, r.OpeningBalance)); }
            foreach (var p in dayBefore.Rows) if (!byId.ContainsKey(p.AccountId) && p.ClosingBalance != 0) cont.Add(new ReconciliationDifference(p.AccountId, p.Code, "opening", p.ClosingBalance, 0));
            checks.Add(Result("opening_equals_previous_closing", cont));
        }
        return checks;
    }

    private static ReconciliationCheck Result(string code, List<ReconciliationDifference> diffs) =>
        new(code, diffs.Count == 0 ? ReconciliationStatus.Passed : ReconciliationStatus.Failed, diffs.Count, [.. diffs.Take(MaxSamples)]);
}
