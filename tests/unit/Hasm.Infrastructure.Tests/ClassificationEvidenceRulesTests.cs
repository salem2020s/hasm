using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;
using Loaded = Hasm.Infrastructure.Costing.ClassificationEvidenceService.Loaded;

namespace Hasm.Infrastructure.Tests;

/// <summary>Pure decision rules of the classification evidence (contract revision 4): fail-closed refusals and fingerprint behaviour, no database.</summary>
public class ClassificationEvidenceRulesTests
{
    private static readonly Guid Company = Guid.Parse("00000000-0000-0000-0000-0000000000c0");
    private static readonly Guid A1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), A2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid P = Guid.Parse("00000000-0000-0000-0000-000000000001"), J = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid V = Guid.Parse("00000000-0000-0000-0000-0000000000e1"), Branch = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Doc = Guid.Parse("00000000-0000-0000-0000-0000000000d0");
    private static readonly DateOnly Day = new(2026, 3, 10);

    private static ClassifiedLine Line(int no, Guid account, decimal debit, decimal credit, string cur = "USD", bool rounding = false) =>
        new(no, account, cur, debit, credit, Branch, rounding, ImmutableArray<ClassifiedAllocation>.Empty);
    private static ClassifiedJournal Journal(Guid id, short status = 4, short type = 1, Guid? reverses = null, long rev = 1, params ClassifiedLine[] lines) =>
        new(id, status, type, rev, Day, Branch, "USD", new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), null, reverses, null,
            [.. (lines.Length == 0 ? [Line(1, A1, 50, 0), Line(2, A2, 0, 50)] : lines)]);
    private static ClassifiedPosting Posting(bool none = false, int docType = 1, decimal value = 50, Guid? reversalId = null, short? action = null, Guid? reversalJournal = null) =>
        new(P, Doc, docType, Day, value, V, none ? null : J, null, null, reversalId, action, reversalJournal);
    private static ClassifiedAccount Account(Guid id, bool active = true, bool group = false, short policy = 1) => new(id, active, group, policy, 1);

    private static Loaded Base(ClassifiedPosting? posting = null, IEnumerable<ClassifiedJournal>? journals = null, IEnumerable<ClassifiedAccount>? accounts = null,
        IEnumerable<ClassifiedObligation>? obligations = null, IEnumerable<ClassifiedSettlement>? settlements = null,
        IEnumerable<(Guid, Guid, Guid)>? obligationReversals = null, HashSet<Guid>? adjusted = null)
    {
        posting ??= Posting();
        return new Loaded(new() { [posting.Id] = posting }, [.. obligations ?? []], [.. settlements ?? []], [.. obligationReversals ?? []],
            (journals ?? [Journal(J)]).ToDictionary(j => j.Id), (accounts ?? [Account(A1), Account(A2)]).ToDictionary(a => a.Id), adjusted ?? [],
            new(), new() { [V] = [A1, A2] }, new());
    }
    private static ClassificationEvidence Run(Loaded loaded) => ClassificationEvidenceService.Evaluate(Company, [P], loaded);
    private static void Refuses(string code, Loaded loaded) => Assert.Equal(code, Assert.Throws<ClassificationEvidenceService.Refusal>(() => Run(loaded)).Code);

    [Fact]
    public void A_consistent_posted_journal_is_accepted_and_the_fingerprint_is_stable_and_sensitive()
    {
        var one = Run(Base()); var two = Run(Base());
        Assert.Equal(one.EvidenceFingerprint, two.EvidenceFingerprint);
        Assert.NotEqual(one.EvidenceFingerprint, Run(Base(journals: [Journal(J, rev: 2)])).EvidenceFingerprint);            // a journal revision changes the fingerprint
        Assert.NotEqual(one.EvidenceFingerprint, Run(Base(journals: [Journal(J, status: 1)])).EvidenceFingerprint);         // post/cancel after the snapshot invalidates the plan
        Assert.NotEqual(one.EvidenceFingerprint, Run(Base(accounts: [Account(A1), Account(A2) with { Revision = 2 }])).EvidenceFingerprint); // current account state is bound
    }

    [Fact] public void Transfers_have_no_stored_classification_and_are_refused() => Refuses("classification_unavailable", Base(Posting(none: true, docType: 3, value: 0)));

    [Theory]
    [InlineData(true, "USD")]
    [InlineData(false, "EUR")]
    public void Rounding_lines_and_foreign_currency_lines_are_unexpected_in_a_stock_journal(bool rounding, string currency) =>
        Refuses("classification_unexpected_line", Base(journals: [Journal(J, lines: [Line(1, A1, 50, 0, currency, rounding), Line(2, A2, 0, 50)])]));

    [Fact] public void An_unbalanced_journal_is_inconsistent() => Refuses("classification_inconsistent", Base(journals: [Journal(J, lines: [Line(1, A1, 50, 0), Line(2, A2, 0, 49)])]));
    [Fact] public void A_single_line_journal_is_inconsistent() => Refuses("classification_inconsistent", Base(journals: [Journal(J, lines: [Line(1, A1, 50, 50)])]));

    [Theory]
    [InlineData(false, false, (short)1)]
    [InlineData(true, true, (short)1)]
    [InlineData(true, false, (short)3)]
    public void An_account_that_is_inactive_a_group_or_foreign_currency_limited_requires_an_owner_decision(bool active, bool group, short policy) =>
        Refuses("open_decision", Base(accounts: [Account(A1, active: active, group: group, policy: policy), Account(A2)]));

    [Fact] public void An_adjusting_entry_on_a_stock_journal_requires_an_owner_decision() => Refuses("open_decision", Base(adjusted: [J]));

    [Theory]
    [InlineData((short)1, true)]
    [InlineData((short)2, true)]
    [InlineData((short)3, true)]
    [InlineData((short)4, true)]
    [InlineData((short)5, false)]
    [InlineData((short)6, false)]
    public void A_live_effect_accepts_not_yet_posted_journals_and_refuses_a_dead_one(short status, bool accepted)
    {
        var loaded = Base(journals: [Journal(J, status: status)]);
        if (accepted) Assert.Single(Run(loaded).Journals); else Refuses("open_decision", loaded);
    }

    [Fact] public void An_unknown_journal_status_is_inconsistent() => Refuses("classification_inconsistent", Base(journals: [Journal(J, status: 9)]));
    [Fact] public void A_missing_journal_with_value_is_inconsistent() => Refuses("classification_inconsistent", Base(Posting(none: true)));
    [Fact] public void A_zero_value_posting_without_journal_is_not_required() => Assert.Empty(Run(Base(Posting(none: true, value: 0), journals: [])).Journals);

    [Fact]
    public void A_receipt_fully_matched_against_obligations_may_have_no_journal_of_its_own()
    {
        var settlement = new ClassifiedSettlement(Guid.NewGuid(), Guid.NewGuid(), P, null, null, true);
        // only the receipt rule is exercised here; the obligation side has its own tests
        var loaded = Base(Posting(none: true), journals: [], settlements: [settlement]);
        Assert.Single(Run(loaded).Settlements);
    }

    private static readonly Guid R = Guid.Parse("00000000-0000-0000-0000-0000000000f2"), RV = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    [Fact]
    public void A_reversed_posting_needs_a_posted_reversing_journal_linked_by_reverses_and_a_reversed_original()
    {
        var posting = Posting(reversalId: RV, action: 2, reversalJournal: R);
        var original = Journal(J, status: 5); var reversing = Journal(R, status: 4, reverses: J, lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)]);
        Assert.Equal(2, Run(Base(posting, [original, reversing])).Journals.Length);
        Refuses("open_decision", Base(posting, [original, Journal(R, status: 1, reverses: J, lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)])]));   // reversing journal not posted
        Refuses("open_decision", Base(posting, [Journal(J, status: 4), reversing]));                                                             // original not marked reversed
        Refuses("classification_inconsistent", Base(posting, [original, Journal(R, status: 4, reverses: Guid.NewGuid(), lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)])]));
    }

    [Theory]
    [InlineData((short)1)]
    [InlineData((short)0)]
    public void A_cancelled_draft_or_an_action_without_a_reversing_journal_is_an_open_decision(short action) =>
        Refuses("open_decision", Base(Posting(reversalId: RV, action: action, reversalJournal: J), [Journal(J, status: 6)]));

    [Fact] public void A_reversed_posting_without_any_journal_is_a_normal_journal_less_case() =>
        Assert.Empty(Run(Base(Posting(none: true, value: 0, reversalId: RV, action: 0), journals: [])).Journals);

    [Fact]
    public void A_posted_journal_with_an_account_its_version_could_not_produce_is_ambiguous_and_never_silently_resolved()
    {
        var stray = Guid.Parse("00000000-0000-0000-0000-0000000000a9");
        Refuses("classification_ambiguous", Base(journals: [Journal(J, lines: [Line(1, stray, 50, 0), Line(2, A2, 0, 50)])], accounts: [Account(A1), Account(A2), Account(stray)]));
    }

    [Fact]
    public void An_obligation_journal_must_be_the_issue_posting_journal_and_an_obligation_reversal_path_must_be_traceable()
    {
        var wrong = new ClassifiedObligation(Guid.NewGuid(), P, Guid.NewGuid(), null, null, null);
        Refuses("classification_inconsistent", Base(obligations: [wrong]));
        var right = new ClassifiedObligation(Guid.NewGuid(), P, J, null, null, null);
        Refuses("classification_unavailable", Base(obligations: [right], obligationReversals: [(Guid.NewGuid(), right.Id, Guid.NewGuid())]));
    }

    [Fact]
    public void A_negating_settlement_without_its_original_is_inconsistent()
    {
        var negating = new ClassifiedSettlement(Guid.NewGuid(), Guid.NewGuid(), P, null, Guid.NewGuid(), false);
        Refuses("classification_inconsistent", Base(settlements: [negating]));
    }
}
