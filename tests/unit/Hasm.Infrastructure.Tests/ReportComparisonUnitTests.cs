using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

/// <summary>The pure parts of the comparative trial balance and of the ledger reconciliation: merging, sorting, depth limits and the detection of every kind of inconsistency.</summary>
public class ReportComparisonUnitTests
{
    private static readonly Guid Root = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid(), Only = Guid.NewGuid();

    private static TrialBalanceRow R(Guid id, string code, Guid? parent, bool group, decimal o, decimal d, decimal c, string en = "") =>
        new(id, code, null, en == "" ? code : en, parent, 0, group, 1, 1, o, d, c, o + d - c);

    private static TrialBalanceView Tb(string from, string to, params TrialBalanceRow[] rows)
    {
        var leaves = rows.Where(r => !r.IsGroup).ToList();
        decimal o = leaves.Sum(r => r.OpeningBalance), d = leaves.Sum(r => r.PeriodDebit), c = leaves.Sum(r => r.PeriodCredit);
        return new TrialBalanceView(DateOnly.Parse(from), DateOnly.Parse(to), "JOD", rows, o, d, c, o + d - c, o + d - c == 0);
    }

    private static TrialBalanceView Current() => Tb("2026-01-01", "2026-12-31", R(Root, "1", null, true, 0, 150, 0), R(A, "11", Root, false, 0, 100, 0, "Alpha"), R(B, "12", Root, false, 0, 50, 0, "Beta"), R(Only, "2", null, false, 0, 0, 150));
    private static TrialBalanceView Previous() => Tb("2025-01-01", "2025-12-31", R(Root, "1", null, true, 0, 30, 0), R(A, "11", Root, false, 0, 10, 0, "Alpha"), R(B, "12", Root, false, 0, 20, 0, "Beta"), R(Only, "2", null, false, 0, 0, 30));

    [Fact]
    public void Merging_pairs_each_account_with_its_comparative_figures_and_computes_the_change()
    {
        var rows = ComparativeTrialBalance.Merge(Current(), Previous(), false, null, null);
        Assert.Equal(["1", "11", "12", "2"], rows.Select(r => r.Code));
        Assert.Equal([0, 1, 1, 0], rows.Select(r => r.Level));
        var a = rows.Single(r => r.AccountId == A);
        Assert.Equal((100m, 10m, 90m), (a.Closing, a.CmpClosing, a.Change));
        Assert.Equal(-120m, rows.Single(r => r.AccountId == Only).Change);                                                // 150 credit now (-150) against 30 credit before (-30)
    }

    [Fact]
    public void An_account_that_exists_only_in_one_period_appears_with_zero_in_the_other_and_no_comparison_means_null_not_zero()
    {
        var gone = Tb("2025-01-01", "2025-12-31", R(Only, "2", null, false, 0, 0, 30), R(Guid.NewGuid(), "3", null, false, 0, 5, 0));
        var rows = ComparativeTrialBalance.Merge(Tb("2026-01-01", "2026-12-31", R(Only, "2", null, false, 0, 0, 10)), gone, false, null, null);
        Assert.Equal(["2", "3"], rows.Select(r => r.Code));
        Assert.Equal((0m, 5m), (rows[1].Closing, rows[1].CmpClosing));                                                     // existed only last year
        Assert.All(ComparativeTrialBalance.Merge(Current(), null, false, null, null), r => { Assert.Null(r.CmpClosing); Assert.Null(r.Change); });
    }

    [Fact]
    public void Zero_rows_are_hidden_unless_requested_and_the_depth_limit_cuts_levels_but_keeps_the_group_totals()
    {
        var withZero = Tb("2026-01-01", "2026-12-31", R(Root, "1", null, true, 0, 0, 0), R(A, "11", Root, false, 0, 0, 0));
        Assert.Empty(ComparativeTrialBalance.Merge(withZero, null, false, null, null));
        Assert.Equal(2, ComparativeTrialBalance.Merge(withZero, null, true, null, null).Count);
        var top = ComparativeTrialBalance.Merge(Current(), null, false, 0, null);
        Assert.Equal(["1", "2"], top.Select(r => r.Code));
        Assert.Equal(150m, top[0].Closing);
    }

    [Fact]
    public void Sorting_is_deterministic_among_siblings_with_ties_broken_by_code()
    {
        var tie = Tb("2026-01-01", "2026-12-31", R(Root, "1", null, true, 0, 200, 0), R(B, "12", Root, false, 0, 100, 0, "Same"), R(A, "11", Root, false, 0, 100, 0, "Same"));
        foreach (var sort in new[] { null, TrialBalanceSorts.Name, TrialBalanceSorts.ClosingDesc, TrialBalanceSorts.ClosingAsc })
            Assert.Equal(["1", "11", "12"], ComparativeTrialBalance.Merge(tie, null, false, null, sort).Select(r => r.Code));
        Assert.True(TrialBalanceSorts.IsValid(null) && TrialBalanceSorts.IsValid("closing_desc") && !TrialBalanceSorts.IsValid("x"));
    }

    private static IReadOnlyDictionary<Guid, LedgerAccountFigures> Ledger(TrialBalanceView tb) =>
        tb.Rows.Where(r => !r.IsGroup).ToDictionary(r => r.AccountId, r => new LedgerAccountFigures(r.OpeningBalance, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance));

    private static ReconciliationCheck Check(IReadOnlyList<ReconciliationCheck> all, string code) => all.Single(c => c.Code == code);

    [Fact]
    public void Consistent_figures_pass_every_check()
    {
        var tb = Current();
        var before = Tb("2025-12-31", "2025-12-31", R(Root, "1", null, true, 0, 0, 0), R(A, "11", Root, false, 0, 0, 0), R(B, "12", Root, false, 0, 0, 0), R(Only, "2", null, false, 0, 0, 0));
        var checks = ReconciliationChecks.Evaluate(tb, before, Ledger(tb), false);
        Assert.All(checks, c => Assert.NotEqual(ReconciliationStatus.Failed, c.Status));
        Assert.Equal(4, checks.Count);
    }

    [Fact]
    public void Every_kind_of_inconsistency_is_detected_and_reported_with_the_accounts_and_amounts()
    {
        var tb = Current();
        // an unbalanced trial balance
        var unbalanced = Tb("2026-01-01", "2026-12-31", R(A, "11", null, false, 0, 100, 0));
        Assert.Equal(ReconciliationStatus.Failed, Check(ReconciliationChecks.Evaluate(unbalanced, null, Ledger(unbalanced), false), "trial_balance_balances").Status);
        Assert.Equal(ReconciliationStatus.NotApplicable, Check(ReconciliationChecks.Evaluate(unbalanced, null, Ledger(unbalanced), true), "trial_balance_balances").Status);
        // a group that does not equal its children
        var badGroup = Tb("2026-01-01", "2026-12-31", R(Root, "1", null, true, 0, 999, 0), R(A, "11", Root, false, 0, 100, 0), R(B, "12", Root, false, 0, 50, 0), R(Only, "2", null, false, 0, 0, 150));
        var roll = Check(ReconciliationChecks.Evaluate(badGroup, null, Ledger(badGroup), false), "group_roll_up");
        Assert.Equal((ReconciliationStatus.Failed, "1", "debit", 150m, 999m), (roll.Status, roll.Samples[0].Code, roll.Samples[0].Item, roll.Samples[0].Expected, roll.Samples[0].Actual));
        // a ledger that disagrees with the trial balance, and a ledger account the trial balance does not know
        var ledger = Ledger(tb).ToDictionary(kv => kv.Key, kv => kv.Value);
        ledger[A] = new LedgerAccountFigures(0, 100, 0, 101);
        ledger[Guid.NewGuid()] = new LedgerAccountFigures(0, 5, 0, 5);
        var glCheck = Check(ReconciliationChecks.Evaluate(tb, null, ledger, false), "ledger_matches_trial_balance");
        Assert.Equal(ReconciliationStatus.Failed, glCheck.Status);
        Assert.Contains(glCheck.Samples, s => s.AccountId == A && s.Item == "closing" && s.Expected == 100m && s.Actual == 101m);
        Assert.Contains(glCheck.Samples, s => s.Item == "missing_in_trial_balance");
        // balances that are not continuous across the period start
        var withOpening = Tb("2026-01-01", "2026-12-31", R(A, "11", null, false, 50, 100, 0), R(Only, "2", null, false, -50, 0, 150));
        var before = Tb("2025-12-31", "2025-12-31", R(A, "11", null, false, 40, 0, 0), R(Only, "2", null, false, -40, 0, 0));
        var cont = Check(ReconciliationChecks.Evaluate(withOpening, before, Ledger(withOpening), false), "opening_equals_previous_closing");
        Assert.Equal(ReconciliationStatus.Failed, cont.Status);
        Assert.Equal(2, cont.Differences);
        Assert.Equal(ReconciliationStatus.NotApplicable, Check(ReconciliationChecks.Evaluate(tb, null, Ledger(tb), false), "opening_equals_previous_closing").Status);
        Assert.False(new ReconciliationView(DateOnly.MinValue, DateOnly.MaxValue, "JOD", ReconciliationChecks.Evaluate(tb, null, ledger, false)).AllPassed);
    }

    [Fact]
    public void A_failing_check_reports_at_most_fifty_samples_but_the_true_number_of_differences()
    {
        var rows = Enumerable.Range(0, 80).Select(i => R(Guid.NewGuid(), $"9{i:000}", null, false, 0, 1, 0)).ToArray();
        var tb = Tb("2026-01-01", "2026-12-31", rows);
        var empty = new Dictionary<Guid, LedgerAccountFigures>();
        var gl = Check(ReconciliationChecks.Evaluate(tb, null, empty, false), "ledger_matches_trial_balance");
        Assert.Equal((160, 50), (gl.Differences, gl.Samples.Count));                                                       // 80 accounts x (debit and closing)
    }
}
