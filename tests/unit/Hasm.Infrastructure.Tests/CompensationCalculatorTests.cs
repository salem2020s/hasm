using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>Matrix cells M1/M2 of the 3.8D transition matrix: the pure compensation calculation and every fail-closed refusal (no database).</summary>
public class CompensationCalculatorTests
{
    private static readonly Guid Company = Guid.Parse("00000000-0000-0000-0000-0000000000c0");
    private static readonly Guid K = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), I = Guid.Parse("00000000-0000-0000-0000-0000000000a2"), I2 = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Branch = Guid.Parse("00000000-0000-0000-0000-0000000000b1"), Period1 = Guid.Parse("00000000-0000-0000-0000-0000000000e1"), Period2 = Guid.Parse("00000000-0000-0000-0000-0000000000e2");
    private static readonly Guid P1 = Guid.Parse("00000000-0000-0000-0000-000000000001"), P2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid J1 = Guid.Parse("00000000-0000-0000-0000-0000000000f1"), J2 = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly Guid Op1 = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Op2 = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid W = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly DateOnly Mar = new(2026, 3, 12), Apr = new(2026, 4, 12);

    private static ClassifiedLine Line(int no, Guid account, decimal debit, decimal credit, bool allocation = false) =>
        new(no, account, "USD", debit, credit, Branch, false, allocation ? [new ClassifiedAllocation(Guid.NewGuid(), debit + credit, debit + credit)] : []);
    private static ClassifiedJournal Journal(Guid id, Guid? period, decimal value, short status = 4, params ClassifiedLine[] lines) =>
        new(id, status, 1, 1, Mar, Branch, "USD", new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero), period, null, null,
            [.. lines.Length == 0 ? [Line(1, K, value, 0), Line(2, I, 0, value)] : lines]);
    private static ClassifiedPosting Issue(Guid id, Guid journal, Guid op, DateOnly date, decimal value) =>
        new(id, Guid.NewGuid(), 2, date, value, Guid.NewGuid(), journal, W, null, null, null, null, op, null);
    private static ClassificationEvidence Evidence(IEnumerable<ClassifiedPosting> postings, IEnumerable<ClassifiedJournal> journals,
        IEnumerable<ClassifiedObligation>? obligations = null, IEnumerable<ClassifiedSettlement>? settlements = null) =>
        new([.. postings], [.. obligations ?? []], [.. settlements ?? []], [.. journals], [], "evidence-fp");
    private static CostFlowEntryImpact Change(Guid op, decimal original, decimal proposed, long id = 1) =>
        new(id, op, W, original, proposed, 0, 0, 0, 0);
    private static CompensationPlan Plan(ClassificationEvidence evidence, params CostFlowEntryImpact[] changes) =>
        CompensationCalculator.Calculate(Company, "comparison-fp", [.. changes], evidence);
    private static void Refuses(string code, ClassificationEvidence evidence, params CostFlowEntryImpact[] changes) =>
        Assert.Equal(code, Assert.Throws<CostingException>(() => Plan(evidence, changes)).Code);
    private static ClassificationEvidence One(decimal value = 40m, short status = 4, params ClassifiedLine[] lines) =>
        Evidence([Issue(P1, J1, Op1, Mar, value)], [Journal(J1, Period1, value, status, lines)]);

    [Fact]
    public void A_higher_issue_cost_debits_the_original_cost_account_and_credits_the_original_inventory_account_in_the_original_period()
    {
        // example E1: the issue booked 40 now costs 48
        var plan = Plan(One(), Change(Op1, 40m, 48m));
        Assert.Equal([new CompensationLine(P1, Mar, Period1, Branch, K, 8m, 0m), new CompensationLine(P1, Mar, Period1, Branch, I, 0m, 8m)], plan.Lines.ToArray());
        Assert.Equal([P1], plan.AffectedPostingIds.ToArray());
    }

    [Fact]
    public void A_lower_issue_cost_mirrors_the_lines()
    {
        var plan = Plan(One(), Change(Op1, 40m, 25m));
        Assert.Equal([new CompensationLine(P1, Mar, Period1, Branch, I, 15m, 0m), new CompensationLine(P1, Mar, Period1, Branch, K, 0m, 15m)], plan.Lines.ToArray());
    }

    [Fact]
    public void Several_entries_of_one_operation_add_up_and_several_postings_keep_their_own_period_and_date()
    {
        var evidence = Evidence([Issue(P1, J1, Op1, Mar, 40m), Issue(P2, J2, Op2, Apr, 30m)],
            [Journal(J1, Period1, 40m), Journal(J2, Period2, 30m, 4, Line(1, K, 30m, 0), Line(2, I2, 0, 30m))]);
        var plan = Plan(evidence, Change(Op1, 25m, 30m, 1), Change(Op1, 15m, 18m, 2), Change(Op2, 30m, 33m, 3));
        Assert.Equal(8m, plan.Lines.Where(l => l.PostingId == P1).Sum(l => l.Debit));            // 5 + 3
        var april = plan.Lines.Where(l => l.PostingId == P2).ToArray();
        Assert.All(april, l => Assert.Equal((Apr, (Guid?)Period2), (l.Date, l.PeriodId)));
        Assert.Contains(april, l => l.AccountId == I2 && l.Credit == 3m);                        // the inventory account of THAT journal
        Assert.Equal(plan.Lines.Sum(l => l.Debit), plan.Lines.Sum(l => l.Credit));
    }

    [Fact] public void A_trajectory_change_without_a_cost_change_needs_no_line() => Assert.Empty(Plan(One(), Change(Op1, 40m, 40m)).Lines);
    [Fact] public void No_change_at_all_is_an_empty_balanced_plan() => Assert.Empty(CompensationCalculator.Calculate(Company, "c", [], One()).Lines);

    [Fact]
    public void A_transfer_needs_no_line_and_a_transfer_with_a_journal_is_inconsistent()
    {
        var transfer = new ClassifiedPosting(P1, Guid.NewGuid(), 3, Mar, 0m, null, null, W, W, null, null, null, Op1, null);
        Assert.Empty(Plan(Evidence([transfer], []), Change(Op1, 40m, 48m)).Lines);
        Refuses("compensation_inconsistent", Evidence([transfer with { JournalId = J1 }], [Journal(J1, Period1, 40m)]), Change(Op1, 40m, 48m));
    }

    [Fact]
    public void A_receipt_cannot_be_re_costed()
    {
        var receipt = new ClassifiedPosting(P1, Guid.NewGuid(), 1, Mar, 40m, Guid.NewGuid(), J1, null, W, null, null, null, Op1, null);
        Refuses("compensation_inconsistent", Evidence([receipt], [Journal(J1, Period1, 40m)]), Change(Op1, 40m, 41m));
    }

    [Fact] public void An_unposted_journal_is_never_compensated_against() => Refuses("unposted_effect", One(status: 1), Change(Op1, 40m, 48m));
    [Fact] public void A_missing_journal_is_inconsistent() => Refuses("compensation_inconsistent", Evidence([Issue(P1, J1, Op1, Mar, 40m)], []), Change(Op1, 40m, 48m));

    [Fact]
    public void More_than_one_debit_or_credit_account_means_item_group_provenance_is_not_provable()
    {
        var mixed = One(40m, 4, Line(1, K, 40m, 0), Line(2, I, 0, 25m), Line(3, I2, 0, 15m));
        Refuses("classification_ambiguous", mixed, Change(Op1, 40m, 48m));
    }

    [Fact] public void A_journal_that_does_not_match_the_posting_total_is_inconsistent() => Refuses("compensation_inconsistent", Evidence([Issue(P1, J1, Op1, Mar, 45m)], [Journal(J1, Period1, 40m)]), Change(Op1, 45m, 50m));
    [Fact] public void A_user_added_allocation_is_unexpected() => Refuses("classification_unexpected_line", One(40m, 4, Line(1, K, 40m, 0, allocation: true), Line(2, I, 0, 40m)), Change(Op1, 40m, 48m));
    [Fact] public void An_operation_without_evidence_is_unavailable() => Refuses("classification_unavailable", One(), Change(Guid.NewGuid(), 40m, 48m));

    [Fact]
    public void Obligations_settlements_and_reversals_are_refused_until_their_replay_exists()
    {
        var obligation = new ClassifiedObligation(Guid.NewGuid(), P1, J1, K, I, I);
        Refuses("compensation_obligation_unsupported", Evidence([Issue(P1, J1, Op1, Mar, 40m)], [Journal(J1, Period1, 40m)], [obligation]), Change(Op1, 40m, 48m));
        var reversed = Issue(P1, J1, Op1, Mar, 40m) with { ReversalId = Guid.NewGuid(), ReversalAction = 2 };
        Refuses("compensation_reversal_unsupported", Evidence([reversed], [Journal(J1, Period1, 40m)]), Change(Op1, 40m, 48m));
    }

    [Fact]
    public void The_fingerprint_binds_the_comparison_the_evidence_and_every_line()
    {
        var evidence = One();
        var baseline = CompensationCalculator.Calculate(Company, "c1", [Change(Op1, 40m, 48m)], evidence).Fingerprint;
        Assert.Equal(baseline, CompensationCalculator.Calculate(Company, "c1", [Change(Op1, 40m, 48m)], evidence).Fingerprint);
        Assert.NotEqual(baseline, CompensationCalculator.Calculate(Company, "c2", [Change(Op1, 40m, 48m)], evidence).Fingerprint);
        Assert.NotEqual(baseline, CompensationCalculator.Calculate(Company, "c1", [Change(Op1, 40m, 49m)], evidence).Fingerprint);
        Assert.NotEqual(baseline, CompensationCalculator.Calculate(Company, "c1", [Change(Op1, 40m, 48m)], evidence with { EvidenceFingerprint = "other" }).Fingerprint);
        Assert.Equal(baseline, CompensationCalculator.Calculate(Company, "c1", [Change(Op1, 40.0m, 48.00m)], evidence).Fingerprint);   // decimal scale is not evidence
    }
}
