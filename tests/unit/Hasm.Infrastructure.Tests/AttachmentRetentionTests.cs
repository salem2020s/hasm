using Hasm.Infrastructure.Attachments;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase R, slice R1: the pure retention rules and the eligibility check (owner decision D-082). Nothing here touches a database or a file.</summary>
public class AttachmentRetentionTests
{
    private static readonly DateTimeOffset Now = new(2030, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Author = Guid.NewGuid(), Approver = Guid.NewGuid();

    private static RetentionPolicyVersionView Policy(int version, IReadOnlyList<RetentionRule> rules, RetentionPolicyStatus status = RetentionPolicyStatus.Approved,
        DateTimeOffset? approved = null, DateOnly? effective = null, DateTimeOffset? ended = null, Guid? approvedBy = null, Guid? draftedBy = null) =>
        new(Guid.NewGuid(), version, status, effective ?? new DateOnly(2020, 1, 1), false, null, null, draftedBy ?? Author, new DateTimeOffset(2019, 12, 1, 0, 0, 0, TimeSpan.Zero),
            approvedBy ?? Approver, approved ?? new DateTimeOffset(2019, 12, 31, 0, 0, 0, TimeSpan.Zero), null, null, null, ended, 1, rules);

    private static RetentionRule Months(string kind, int months, short sens = 0) => new(kind, sens, months, false);
    private static RetentionRule Forever(string kind, short sens = 0) => new(kind, sens, null, true);

    private static AttachmentRetentionFacts Removed(DateTimeOffset at, string kind = "local_party", bool sensitive = false, string? keyId = null, bool purged = false, Guid? id = null, Guid? subject = null) =>
        new(id ?? Guid.NewGuid(), kind, subject ?? Guid.NewGuid(), sensitive, true, at, keyId, purged);

    private static EligibilityResult Check(AttachmentRetentionFacts a, IReadOnlyList<RetentionPolicyVersionView> policies, IReadOnlyList<LegalHoldView>? holds = null, bool busy = false, DateTimeOffset? now = null, bool basis = true) =>
        AttachmentRetentionEligibility.Evaluate(a, policies, holds ?? [], new RetentionEnvironment(busy, basis), now ?? Now);   // basis = true simulates a future owner answer; the services pass RetentionBasis.OwnerConfirmed

    private static LegalHoldView Hold(LegalHoldScope scope, string? kind = null, Guid? subject = null, Guid? attachment = null, bool released = false) =>
        new(Guid.NewGuid(), scope, kind, subject, attachment, "case", null, Author, Now.AddDays(-10), released ? Approver : null, released ? Now.AddDays(-1) : null, released ? "done" : null, 1);

    // ---- the default is preservation

    [Fact]
    public void Without_any_policy_nothing_is_eligible_however_old()
    {
        Assert.Equal("no_valid_policy", Check(Removed(Now.AddYears(-50)), []).Code);
    }

    [Fact]
    public void A_draft_a_self_approved_a_not_yet_effective_a_not_yet_approved_and_an_ended_version_are_not_valid_policies()
    {
        var rules = new[] { Months("*", 1) };
        var old = Removed(Now.AddYears(-10));
        Assert.Equal("no_valid_policy", Check(old, [Policy(1, rules, RetentionPolicyStatus.Draft)]).Code);
        Assert.Equal("no_valid_policy", Check(old, [Policy(1, rules, approvedBy: Author)]).Code);                                   // the approver is the author
        Assert.Equal("no_valid_policy", Check(old, [Policy(1, rules, effective: new DateOnly(2031, 1, 1))]).Code);                  // not effective yet
        Assert.Equal("no_valid_policy", Check(old, [Policy(1, rules, approved: Now.AddDays(1))]).Code);                             // approved in the future
        Assert.Equal("no_valid_policy", Check(old, [Policy(1, rules, RetentionPolicyStatus.Revoked, ended: Now.AddDays(-1))]).Code); // revoked: back to preservation
        Assert.Equal("eligible", Check(old, [Policy(1, rules)]).Code);
    }

    [Fact]
    public void No_matching_rule_and_an_indefinite_rule_both_mean_indefinite()
    {
        var old = Removed(Now.AddYears(-30), kind: "inventory_item");
        Assert.Equal("indefinite", Check(old, [Policy(1, [Months("local_party", 1)])]).Code);       // no rule for this kind and no *
        Assert.Equal("indefinite", Check(old, [Policy(1, [Forever("*")])]).Code);
    }

    // ---- rule precedence and the exact boundary

    [Fact]
    public void The_most_specific_rule_wins_kind_and_sensitivity_then_kind_then_sensitivity_then_any()
    {
        var rules = new[] { Months("*", 120), Months("*", 60, 2), Months("local_party", 36), Months("local_party", 12, 2) };
        Assert.Equal(12, RetentionRules.PeriodMonths(RetentionRules.Match(rules, "local_party", true)));
        Assert.Equal(36, RetentionRules.PeriodMonths(RetentionRules.Match(rules, "local_party", false)));
        Assert.Equal(60, RetentionRules.PeriodMonths(RetentionRules.Match(rules, "inventory_item", true)));
        Assert.Equal(120, RetentionRules.PeriodMonths(RetentionRules.Match(rules, "inventory_item", false)));
        Assert.Null(RetentionRules.Match([Months("local_party", 1)], "inventory_item", false));
    }

    [Fact]
    public void The_period_is_whole_calendar_months_from_the_removal_instant_and_the_boundary_is_exact()
    {
        var removed = new DateTimeOffset(2028, 1, 31, 10, 0, 0, TimeSpan.Zero);
        var policy = Policy(1, [Months("*", 1)]);
        var eligibleAt = removed.AddMonths(1);                                                        // 2028-02-29 (a leap year), the end of the month
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 10, 0, 0, TimeSpan.Zero), eligibleAt);
        var a = Removed(removed);
        var before = Check(a, [policy], now: eligibleAt.AddTicks(-1));
        Assert.Equal("not_yet_eligible", before.Code);
        Assert.Equal(eligibleAt, before.EligibleAtUtc);
        Assert.Equal("eligible", Check(a, [policy], now: eligibleAt).Code);
        Assert.Equal("eligible", Check(a, [policy], now: eligibleAt.AddDays(400)).Code);
    }

    // ---- no silent retroactive shortening

    [Fact]
    public void A_later_shorter_policy_never_makes_an_older_removal_eligible_earlier_than_its_original_period()
    {
        var long1 = Policy(1, [Months("*", 120)], effective: new DateOnly(2020, 1, 1), ended: new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero), status: RetentionPolicyStatus.Superseded);
        var short2 = Policy(2, [Months("*", 12)], effective: new DateOnly(2029, 1, 1), approved: new DateTimeOffset(2028, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var removedUnderV1 = Removed(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var a = Check(removedUnderV1, [long1, short2]);
        Assert.Equal("not_yet_eligible", a.Code);                                                     // 12 months would have passed; the original 120 apply
        Assert.Equal(new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero), a.EligibleAtUtc);
        var removedUnderV2 = Removed(new DateTimeOffset(2029, 2, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2030, 2, 1, 0, 0, 0, TimeSpan.Zero), Check(removedUnderV2, [long1, short2]).EligibleAtUtc);
    }

    [Fact]
    public void A_later_longer_policy_extends_the_period_and_a_removal_before_any_policy_stays_indefinite()
    {
        var short1 = Policy(1, [Months("*", 12)], ended: new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero), status: RetentionPolicyStatus.Superseded);
        var long2 = Policy(2, [Months("*", 60)], effective: new DateOnly(2029, 1, 1), approved: new DateTimeOffset(2028, 12, 31, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), Check(Removed(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)), [short1, long2]).EligibleAtUtc);   // 60 months (the longer) from 2025-01-01, not 12
        // removed in 2010, before version 1 existed: no period was ever committed to, so it is Indefinite (preserve): adopting a policy later never makes it eligible
        var early = Check(Removed(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero)), [short1, long2]);
        Assert.Equal("indefinite", early.Code);
        Assert.Equal("indefinite", Check(Removed(new DateTimeOffset(2019, 6, 1, 0, 0, 0, TimeSpan.Zero)), [Policy(1, [Months("*", 1)])]).Code);   // removed before the policy took effect (2020-01-01)
        // a version that was indefinite at removal keeps the attachment preserved even under a later finite one
        var forever = Policy(1, [Forever("*")], ended: new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero), status: RetentionPolicyStatus.Superseded);
        Assert.Equal("indefinite", Check(Removed(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)), [forever, long2]).Code);
    }

    [Fact]
    public void The_shortening_test_compares_what_actually_applies_for_every_registered_kind_and_sensitivity()
    {
        Assert.True(RetentionRules.Shortens([Months("*", 60)], [Months("*", 12)]));
        Assert.True(RetentionRules.Shortens([Forever("*")], [Months("*", 600)]));                      // indefinite becoming finite shortens
        Assert.True(RetentionRules.Shortens([Months("*", 60)], [Months("local_party", 12), Months("*", 60)]));
        Assert.False(RetentionRules.Shortens([Months("*", 12)], [Months("*", 60)]));
        Assert.False(RetentionRules.Shortens([Months("*", 12)], [Months("*", 12)]));
        Assert.False(RetentionRules.Shortens([Months("*", 12)], [Forever("*")]));
        Assert.True(RetentionRules.Shortens([Months("*", 12), Months("local_party", 120)], [Months("*", 12)]));   // a specific long rule dropped
    }

    // ---- legal holds

    [Fact]
    public void An_active_hold_blocks_by_scope_and_a_released_one_does_not()
    {
        var policy = Policy(1, [Months("*", 1)]);
        var id = Guid.NewGuid(); var subject = Guid.NewGuid();
        var a = Removed(Now.AddYears(-5), id: id, subject: subject);
        var company = Hold(LegalHoldScope.Company);
        var record = Hold(LegalHoldScope.Record, "local_party", subject);
        var one = Hold(LegalHoldScope.Attachment, attachment: id);
        foreach (var h in new[] { company, record, one })
        {
            var r = Check(a, [policy], [h]);
            Assert.Equal("legal_hold", r.Code);
            Assert.Equal(h.Id, r.HoldId);
        }
        Assert.Equal("eligible", Check(a, [policy], [Hold(LegalHoldScope.Record, "local_party", Guid.NewGuid())]).Code);              // another record
        Assert.Equal("eligible", Check(a, [policy], [Hold(LegalHoldScope.Record, "inventory_item", subject)]).Code);                  // another kind, same id
        Assert.Equal("eligible", Check(a, [policy], [Hold(LegalHoldScope.Attachment, attachment: Guid.NewGuid())]).Code);
        Assert.Equal("eligible", Check(a, [policy], [Hold(LegalHoldScope.Company, released: true)]).Code);
        Assert.Equal("legal_hold", Check(a, [policy], [Hold(LegalHoldScope.Company, released: true), record]).Code);
        Assert.Equal("legal_hold", Check(a, [policy], [Hold((LegalHoldScope)99)]).Code);                                              // an unknown scope blocks
    }

    [Fact]
    public void A_hold_on_a_record_covers_an_attachment_removed_later_and_a_hold_outranks_the_date_only_after_the_period()
    {
        var policy = Policy(1, [Months("*", 60)]);
        var subject = Guid.NewGuid();
        var fresh = Removed(Now.AddMonths(-1), subject: subject);
        Assert.Equal("not_yet_eligible", Check(fresh, [policy], [Hold(LegalHoldScope.Record, "local_party", subject)]).Code);       // not eligible anyway
    }

    // ---- the other refusals and the order of the checks

    [Fact]
    public void Not_removed_already_purged_unknown_key_busy_company_and_a_failure_are_all_refused_with_their_own_reason()
    {
        var policy = Policy(1, [Months("*", 1)]);
        var active = new AttachmentRetentionFacts(Guid.NewGuid(), "local_party", Guid.NewGuid(), false, false, null, null, false);
        Assert.Equal("not_removed", Check(active, [policy]).Code);
        Assert.Equal("already_purged", Check(Removed(Now.AddYears(-5), purged: true), [policy]).Code);
        Assert.Equal("key_state_unknown", Check(Removed(Now.AddYears(-5), sensitive: true, keyId: null), [policy]).Code);
        Assert.Equal("eligible", Check(Removed(Now.AddYears(-5), sensitive: true, keyId: "k1"), [policy]).Code);
        Assert.Equal("company_busy", Check(Removed(Now.AddYears(-5)), [policy], busy: true).Code);
        Assert.Equal("check_failed", AttachmentRetentionEligibility.Evaluate(Removed(Now.AddYears(-5)), null!, [], new RetentionEnvironment(false), Now).Code);   // an exception is never "eligible"
        Assert.All(new[] { "eligible", "not_removed", "already_purged", "no_valid_policy", "indefinite", "not_yet_eligible", "legal_hold", "key_state_unknown", "company_busy", "retention_basis_pending", "check_failed" }, c => Assert.Matches("^[a-z_]+$", c));
    }

    [Fact]
    public void The_newest_valid_version_is_the_current_one()
    {
        var v1 = Policy(1, [Months("*", 120)], ended: new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero), status: RetentionPolicyStatus.Superseded);
        var v2 = Policy(2, [Months("*", 120)], effective: new DateOnly(2029, 1, 1), approved: new DateTimeOffset(2028, 12, 31, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, Check(Removed(Now.AddYears(-30)), [v1, v2]).PolicyVersion);
        Assert.Equal(2, Check(Removed(Now.AddYears(-30)), [v2, v1]).PolicyVersion);                   // the order of the input does not matter
    }

    // ---- D-109 (the retention basis) is owner-pending: nothing is eligible

    [Fact]
    public void While_the_retention_basis_is_owner_pending_no_item_is_ever_eligible_whatever_the_dates_say()
    {
        Assert.False(RetentionBasis.OwnerConfirmed);                                               // the switch that only a recorded owner answer turns on
        var policy = Policy(1, [Months("*", 1)]);
        var old = Removed(Now.AddYears(-5));
        var pending = Check(old, [policy], basis: false);
        Assert.Equal("retention_basis_pending", pending.Code);
        Assert.False(pending.IsEligible);
        Assert.NotNull(pending.EligibleAtUtc);                                                       // the date is still shown for information
        Assert.Equal("eligible", Check(old, [policy], basis: true).Code);                            // only the (future) owner answer changes it
        Assert.Equal("retention_basis_pending", AttachmentRetentionEligibility.Evaluate(old, [policy], [], new RetentionEnvironment(false), Now).Code);   // the default environment is "pending"
        // the other refusals still come first
        Assert.Equal("not_yet_eligible", Check(Removed(Now.AddDays(-3)), [policy], basis: false).Code);
        Assert.Equal("legal_hold", Check(old, [policy], [Hold(LegalHoldScope.Company)], basis: false).Code);
        Assert.Equal("indefinite", Check(old, [Policy(1, [Forever("*")])], basis: false).Code);
    }

    // ---- validation of a draft

    private static readonly DateOnly Today = new(2030, 6, 15);

    [Fact]
    public void A_valid_draft_has_no_errors()
    {
        Assert.Empty(RetentionRules.Validate(new RetentionPolicyInput(Today, "note", [Months("*", 84), Months("local_party", 120, 2), Forever("inventory_item")]), Today));
        Assert.Empty(RetentionRules.Validate(new RetentionPolicyInput(Today.AddDays(30), null, [Months("*", 1200)]), Today));
    }

    [Theory]
    [InlineData("effectiveFrom", "required")]
    [InlineData("effectiveFrom", "in_the_past")]
    [InlineData("rules", "required")]
    [InlineData("rules", "too_many")]
    [InlineData("rules[0].subjectKind", "subject_kind_invalid")]
    [InlineData("rules[0].sensitivity", "invalid_value")]
    [InlineData("rules[0].months", "required")]
    [InlineData("rules[0].months", "indefinite_has_months")]
    [InlineData("rules[0].months", "out_of_range")]
    [InlineData("rules[1]", "duplicate_rule")]
    [InlineData("note", "too_long")]
    public void Each_invalid_draft_names_its_field_and_code(string field, string code)
    {
        RetentionPolicyInput input = (field, code) switch
        {
            ("effectiveFrom", "required") => new(null, null, [Months("*", 1)]),
            ("effectiveFrom", "in_the_past") => new(Today.AddDays(-1), null, [Months("*", 1)]),
            ("rules", "required") => new(Today, null, []),
            ("rules", "too_many") => new(Today, null, [.. Enumerable.Range(0, RetentionRules.MaxRules + 1).Select(_ => Months("*", 1))]),
            ("rules[0].subjectKind", _) => new(Today, null, [Months("nope", 1)]),
            ("rules[0].sensitivity", _) => new(Today, null, [new RetentionRule("*", 3, 1, false)]),
            ("rules[0].months", "required") => new(Today, null, [new RetentionRule("*", 0, null, false)]),
            ("rules[0].months", "indefinite_has_months") => new(Today, null, [new RetentionRule("*", 0, 5, true)]),
            ("rules[0].months", _) => new(Today, null, [Months("*", 0)]),
            ("rules[1]", _) => new(Today, null, [Months("*", 1), Months("*", 2)]),
            _ => new(Today, new string('x', RetentionRules.MaxNoteLength + 1), [Months("*", 1)]),
        };
        Assert.Contains(RetentionRules.Validate(input, Today), e => e.Field == field && e.Code == code);
        Assert.Contains(RetentionRules.Validate(new RetentionPolicyInput(Today, null, [Months("*", RetentionRules.MaxMonths + 1)]), Today), e => e.Code == "out_of_range");
    }

    [Fact]
    public void A_missing_body_is_refused()
    {
        Assert.Contains(RetentionRules.Validate(null, Today), e => e.Field == "body");
    }
}
