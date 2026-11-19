using System.Collections.Immutable;
using Hasm.Infrastructure.Access;
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
        IEnumerable<ClassifiedObligationReversal>? obligationReversals = null, HashSet<Guid>? adjusted = null)
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

    private static ClassifiedPosting Transfer() => Posting(none: true, docType: 3, value: 40) with { VersionId = null, SourceWarehouse = Guid.NewGuid(), TargetWarehouse = Guid.NewGuid() };

    [Fact] public void A_journal_less_transfer_is_accepted_because_its_effect_reaches_the_ledger_only_through_downstream_issues() =>
        Assert.Single(Run(Base(Transfer(), journals: [])).Postings);

    [Fact]
    public void A_transfer_with_a_journal_a_version_or_a_missing_leg_is_inconsistent()
    {
        Refuses("classification_inconsistent", Base(Transfer() with { JournalId = J }));
        Refuses("classification_inconsistent", Base(Transfer() with { VersionId = V }, journals: []));
        Refuses("classification_inconsistent", Base(Transfer() with { TargetWarehouse = null }, journals: []));
    }

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

    private static readonly Guid Issue = Guid.Parse("00000000-0000-0000-0000-000000000002"), Obl = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid S1 = Guid.Parse("00000000-0000-0000-0000-0000000000c1"), S2 = Guid.Parse("00000000-0000-0000-0000-0000000000c2"), S3 = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Guid Rj = Guid.Parse("00000000-0000-0000-0000-0000000000f3"), Sj = Guid.Parse("00000000-0000-0000-0000-0000000000f4");

    /// <summary>An issue posting with an obligation (journal J) and a receipt posting P that books nothing of its own and settles it.</summary>
    private static Loaded Chain(IEnumerable<ClassifiedSettlement>? settlements = null, IEnumerable<ClassifiedJournal>? extra = null, ClassifiedPosting? issue = null,
        IEnumerable<ClassifiedObligationReversal>? reversals = null, bool receiptWithoutVersion = false)
    {
        issue ??= new ClassifiedPosting(Issue, Doc, 2, Day, 50, V, J, null, null, null, null, null);
        var receipt = new ClassifiedPosting(P, Doc, 1, Day, 24, receiptWithoutVersion ? null : V, null, null, null, null, null, null);
        var obligation = new ClassifiedObligation(Obl, Issue, J, A1, A2, A2);
        var journals = (extra ?? []).ToDictionary(j => j.Id);
        journals.TryAdd(J, Journal(J)); journals.TryAdd(Sj, Journal(Sj));
        return new Loaded(new() { [Issue] = issue, [P] = receipt }, [obligation], [.. settlements ?? [new ClassifiedSettlement(S1, Obl, P, Sj, null, null, true)]],
            [.. reversals ?? []], journals, new[] { A1, A2 }.ToDictionary(x => x, x => Account(x)), [], new(), new() { [V] = [A1, A2] }, new());
    }

    [Fact]
    public void A_receipt_fully_matched_against_an_obligation_may_have_no_journal_of_its_own_but_its_settlement_journal_is_evidence()
    {
        var evidence = Run(Chain());
        Assert.Equal(2, evidence.Postings.Length); Assert.Single(evidence.Settlements); Assert.Contains(evidence.Journals, j => j.Id == Sj);
    }

    [Fact]
    public void A_settlement_journal_with_an_account_outside_the_obligation_and_the_receipt_version_is_ambiguous()
    {
        var stray = Guid.Parse("00000000-0000-0000-0000-0000000000a9");
        Refuses("classification_ambiguous", Chain(extra: [Journal(Sj, lines: [Line(1, stray, 5, 0), Line(2, A2, 0, 5)])]));
    }

    [Fact]
    public void A_zero_value_receipt_without_version_may_settle_with_a_journal_that_only_uses_the_obligation_accounts()
    {
        var obligationOnly = Journal(Sj, lines: [Line(1, A1, 5, 0), Line(2, A2, 0, 5)]);          // O and V only: no receipt counter account involved
        Assert.Contains(Run(Chain(extra: [obligationOnly], receiptWithoutVersion: true)).Journals, j => j.Id == Sj);
        var stray = Guid.Parse("00000000-0000-0000-0000-0000000000a9");
        var needsCounter = Journal(Sj, lines: [Line(1, stray, 5, 0), Line(2, A2, 0, 5)]);          // an account beyond O/V needs the receipt version, which is missing
        Refuses("classification_inconsistent", Chain(extra: [needsCounter], receiptWithoutVersion: true));
    }

    [Fact]
    public void A_reversed_issue_needs_its_obligation_reversal_row_and_an_empty_net_settlement_chain()
    {
        var reversedIssue = new ClassifiedPosting(Issue, Doc, 2, Day, 50, V, J, null, null, RV, 2, R);
        var reversing = Journal(R, status: 4, reverses: J, lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)]);
        var reversedOriginal = Journal(J, status: 5);
        var reversedSettlements = new[] { new ClassifiedSettlement(S1, Obl, P, Sj, null, null, false), new ClassifiedSettlement(S2, Obl, P, Sj, S1, null, false) };
        // no obligation reversal row
        Refuses("classification_inconsistent", Chain(reversedSettlements, [reversing, reversedOriginal], reversedIssue));
        // the row exists but a forward settlement is still in the net chain
        Refuses("classification_inconsistent", Chain([new ClassifiedSettlement(S1, Obl, P, Sj, null, null, true)], [reversing, reversedOriginal], reversedIssue,
            [new ClassifiedObligationReversal(Guid.NewGuid(), Obl, RV)]));
    }

    [Fact]
    public void A_settlement_cannot_be_reversed_twice_and_a_negating_row_is_never_itself_reversed()
    {
        var reversing = Journal(Rj, status: 4, reverses: Sj, lines: [Line(1, A1, 0, 5), Line(2, A2, 5, 0)]);
        var original = Journal(Sj, status: 5, lines: [Line(1, A1, 5, 0), Line(2, A2, 0, 5)]);
        var twice = new[] { new ClassifiedSettlement(S1, Obl, P, Sj, null, null, false), new ClassifiedSettlement(S2, Obl, P, Rj, S1, null, false), new ClassifiedSettlement(S3, Obl, P, Rj, S1, null, false) };
        Refuses("classification_inconsistent", Chain(twice, [reversing, original]));
        var ofNegating = new[] { new ClassifiedSettlement(S1, Obl, P, Sj, null, null, false), new ClassifiedSettlement(S2, Obl, P, Rj, S1, null, false), new ClassifiedSettlement(S3, Obl, P, Rj, S2, null, false) };
        Refuses("classification_inconsistent", Chain(ofNegating, [reversing, original]));
    }

    [Fact]
    public void A_journal_that_reverses_a_closure_journal_but_belongs_to_nothing_is_an_open_decision()
    {
        var manual = Journal(Guid.Parse("00000000-0000-0000-0000-0000000000f9"), status: 1, reverses: J, lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)]);
        Refuses("open_decision", Base(journals: [Journal(J), manual]));
    }

    [Fact] public void A_journal_without_a_recorded_accounting_version_is_inconsistent() =>
        Refuses("classification_inconsistent", Base(Posting() with { VersionId = null }));

    [Fact]
    public void The_fingerprint_binds_obligation_reversals_event_kinds_and_every_settlement_column_and_ignores_decimal_scale()
    {
        var reversing = Journal(R, status: 4, reverses: J, lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)]);
        var reversedIssue = new ClassifiedPosting(Issue, Doc, 2, Day, 50, V, J, null, null, RV, 2, R);
        var settled = new[] { new ClassifiedSettlement(S1, Obl, P, Sj, null, null, false), new ClassifiedSettlement(S2, Obl, P, Rj, S1, Guid.Parse("00000000-0000-0000-0000-0000000000a5"), false) };
        var sReversing = Journal(Rj, status: 4, reverses: Sj, lines: [Line(1, A1, 0, 50), Line(2, A2, 50, 0)]);
        ClassificationEvidence With(Guid obligationReversalId, Guid reversalRow, Guid? settlementReversal = null)
        {
            var rows = new[] { settled[0], settled[1] with { ReversalId = settlementReversal ?? settled[1].ReversalId } };
            return Run(Chain(rows, [reversing, Journal(J, status: 5), Journal(Sj, status: 5), sReversing], reversedIssue, [new ClassifiedObligationReversal(obligationReversalId, Obl, reversalRow)]));
        }
        var id = Guid.Parse("00000000-0000-0000-0000-0000000000b9");
        var baseline = With(id, RV).EvidenceFingerprint;
        Assert.Equal(baseline, With(id, RV).EvidenceFingerprint);
        Assert.NotEqual(baseline, With(Guid.Parse("00000000-0000-0000-0000-0000000000ba"), RV).EvidenceFingerprint);   // the reversal row identity is bound
        Assert.NotEqual(baseline, With(id, RV, Guid.NewGuid()).EvidenceFingerprint);                                 // NX.ReversalId is bound
        var one = Run(Base(journals: [Journal(J, lines: [Line(1, A1, 50.0m, 0), Line(2, A2, 0, 50.00m)])]));
        Assert.Equal(Run(Base()).EvidenceFingerprint, one.EvidenceFingerprint);                                      // 50.0 and 50.00 are the same evidence
        var withEvent = new Loaded(Base().Postings, [], [], [], Base().Journals, Base().Accounts, [], new() { new ClassifiedEvent(P, 1, J) }, Base().VersionAccounts, new());
        var otherKind = new Loaded(Base().Postings, [], [], [], Base().Journals, Base().Accounts, [], new() { new ClassifiedEvent(P, 2, J) }, Base().VersionAccounts, new());
        Assert.NotEqual(Run(withEvent).EvidenceFingerprint, Run(otherKind).EvidenceFingerprint);                    // the event kind is bound
    }

    private static EffectivePermissions Perms(params (ScopeKind Scope, Guid? Id, string[] Codes)[] grants) =>
        new(PermissionCatalog.Default, [.. grants.Select(g => new GrantedRole(Guid.NewGuid(), Guid.NewGuid(), g.Scope, g.Id, new HashSet<string>(g.Codes)))]);
    private static readonly string[] All = [PermissionCatalog.Codes.AccountsView, PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.StockDocsView, PermissionCatalog.Codes.CostCentersView];

    [Fact]
    public void The_bootstrap_needs_each_specific_permission_and_unrelated_branch_or_warehouse_grants_open_nothing()
    {
        Assert.True(ClassificationEvidenceService.Bootstrap(Perms((ScopeKind.Company, null, All))));
        // accounts company-wide, but the branch and warehouse grants carry OTHER permissions only
        Assert.False(ClassificationEvidenceService.Bootstrap(Perms((ScopeKind.Company, null, [PermissionCatalog.Codes.AccountsView]),
            (ScopeKind.Branch, Guid.NewGuid(), [PermissionCatalog.Codes.BranchesView]), (ScopeKind.Warehouse, Guid.NewGuid(), [PermissionCatalog.Codes.StockBalanceView]))));
        // the right permissions at branch and warehouse scope pass the bootstrap but are NOT fully visible
        var scoped = Perms((ScopeKind.Company, null, [PermissionCatalog.Codes.AccountsView]), (ScopeKind.Branch, Guid.NewGuid(), [PermissionCatalog.Codes.JournalView]),
            (ScopeKind.Warehouse, Guid.NewGuid(), [PermissionCatalog.Codes.StockDocsView]));
        Assert.True(ClassificationEvidenceService.Bootstrap(scoped)); Assert.False(ClassificationEvidenceService.FullyVisible(scoped));
        Assert.True(ClassificationEvidenceService.FullyVisible(Perms((ScopeKind.Company, null, All))));
        Assert.False(ClassificationEvidenceService.Bootstrap(Perms((ScopeKind.Company, null, [PermissionCatalog.Codes.JournalView, PermissionCatalog.Codes.StockDocsView]))));   // no accounts
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
        Refuses("classification_unavailable", Base(obligations: [right], obligationReversals: [new ClassifiedObligationReversal(Guid.NewGuid(), right.Id, Guid.NewGuid())]));
    }

    [Fact]
    public void A_negating_settlement_without_its_original_is_inconsistent()
    {
        var negating = new ClassifiedSettlement(Guid.NewGuid(), Guid.NewGuid(), P, null, Guid.NewGuid(), null, false);
        Refuses("classification_inconsistent", Base(settlements: [negating]));
    }
}
