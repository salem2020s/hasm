using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Attachments;

// Phase R, slice R1 (docs/PHASE-R-attachment-retention.md, owner decision D-082): the retention POLICY, LEGAL HOLDS and the pure ELIGIBILITY check for removed attachments.
// R1 deletes NOTHING. OWNER-PENDING defaults (the legal-policy questions of section 15 of the plan): the period is counted from the removal date in whole calendar months (UTC), metadata is kept, and there is no
// retrieval feature. Every uncertainty means "not eligible" (preserve by default).

/// <summary>
/// Business audit events of attachment retention (company stream, a NEW block 2000-2009, docs/PHASE-R-attachment-retention.md section 10). Persisted codes: never renumber. Related facts share a code with the
/// outcome in the detail. Details carry ids, counts, kinds, versions and reason codes, never file names, paths or digests.
/// </summary>
public static class AttachmentRetentionAuditEvents
{
    public const short PolicyDrafted = 2000;
    public const short PolicyApproved = 2001;
    public const short PolicyRevoked = 2002;
    public const short HoldPlaced = 2003;
    public const short HoldReleased = 2004;
    public const short PlanRequested = 2005;      // R2
    public const short PlanDecided = 2006;        // R2
    public const short PurgeRun = 2007;           // R2
    public const short PurgeItem = 2008;          // R2
    public const short EligibilityRefused = 2009; // R2
}

/// <summary>Life cycle of a policy version: drafted, approved by a DIFFERENT user, then revoked or superseded. Stored as a smallint.</summary>
public enum RetentionPolicyStatus : short { Draft = 1, Approved = 2, Revoked = 3, Superseded = 4 }

/// <summary>What a legal hold covers: the whole company, all attachments of one record, or one attachment. Stored as a smallint.</summary>
public enum LegalHoldScope : short { Company = 1, Record = 2, Attachment = 3 }

/// <summary>
/// One retention rule. <see cref="SubjectKind"/> is a registered attachment record kind or <c>*</c> (all); <see cref="Sensitivity"/> is 0 (any), 1 (normal) or 2 (sensitive). Exactly one of
/// <see cref="Months"/> (1..<see cref="RetentionRules.MaxMonths"/>) and <see cref="Indefinite"/> is set.
/// </summary>
public sealed record RetentionRule(string SubjectKind, short Sensitivity, int? Months, bool Indefinite);

/// <summary>A policy version with its rules. <see cref="EndedAtUtc"/> is when it was superseded or revoked (null while in force).</summary>
public sealed record RetentionPolicyVersionView(Guid Id, int Version, RetentionPolicyStatus Status, DateOnly EffectiveFrom, bool ShortensAnyPeriod, string? ShorteningAcknowledgement, string? Note,
    Guid DraftedBy, DateTimeOffset DraftedAtUtc, Guid? ApprovedBy, DateTimeOffset? ApprovedAtUtc, Guid? RevokedBy, DateTimeOffset? RevokedAtUtc, string? RevokeReason, DateTimeOffset? EndedAtUtc,
    long Revision, IReadOnlyList<RetentionRule> Rules);

/// <summary>A legal hold. Active while <see cref="ReleasedAtUtc"/> is null.</summary>
public sealed record LegalHoldView(Guid Id, LegalHoldScope Scope, string? SubjectKind, Guid? SubjectId, Guid? AttachmentId, string Reason, string? Reference, Guid PlacedBy, DateTimeOffset PlacedAtUtc,
    Guid? ReleasedBy, DateTimeOffset? ReleasedAtUtc, string? ReleaseReason, long Revision)
{
    public bool IsActive => ReleasedAtUtc is null;
}

public sealed record RetentionPolicyInput(DateOnly? EffectiveFrom, string? Note, IReadOnlyList<RetentionRule>? Rules);
public sealed record LegalHoldInput(LegalHoldScope? Scope, string? SubjectKind, Guid? SubjectId, Guid? AttachmentId, string? Reason, string? Reference);

/// <summary>The facts about one attachment the eligibility check needs (nothing about names or paths).</summary>
public sealed record AttachmentRetentionFacts(Guid Id, string SubjectKind, Guid SubjectId, bool IsSensitive, bool IsRemoved, DateTimeOffset? RemovedAtUtc, string? KeyId, bool AlreadyPurged);

/// <summary>
/// Facts about the environment at the moment of the check. <see cref="BasisConfirmed"/> is false while the owner has not answered D-109 (the retention basis, OWNER-PENDING): then NO item is eligible
/// (<see cref="EligibilityReason.RetentionBasisPending"/>), whatever the dates say. The services pass <see cref="RetentionBasis.OwnerConfirmed"/>.
/// </summary>
public sealed record RetentionEnvironment(bool CompanyBusy, bool BasisConfirmed = false);

/// <summary>The one switch that stays OFF until the owner answers D-109 (docs/PHASE-R-attachment-retention.md section 4). Nothing can be purged while it is false; only a recorded owner decision changes it (a code change reviewed with that decision).</summary>
public static class RetentionBasis
{
    public const bool OwnerConfirmed = false;
}

/// <summary>The one reason code the eligibility check answers with (never only a boolean).</summary>
public enum EligibilityReason
{
    Eligible,
    NotRemoved,
    AlreadyPurged,
    NoValidPolicy,
    Indefinite,
    NotYetEligible,
    LegalHold,
    KeyStateUnknown,
    CompanyBusy,
    RetentionBasisPending,
    CheckFailed,
}

/// <summary>The answer: the reason, the date the attachment becomes (or became) eligible when known, the hold that blocks it, and the policy version applied.</summary>
public sealed record EligibilityResult(EligibilityReason Reason, DateTimeOffset? EligibleAtUtc, Guid? HoldId, int? PolicyVersion)
{
    public bool IsEligible => Reason == EligibilityReason.Eligible;
    /// <summary>The stable snake_case code used in API answers and audit details.</summary>
    public string Code => Reason switch
    {
        EligibilityReason.Eligible => "eligible",
        EligibilityReason.NotRemoved => "not_removed",
        EligibilityReason.AlreadyPurged => "already_purged",
        EligibilityReason.NoValidPolicy => "no_valid_policy",
        EligibilityReason.Indefinite => "indefinite",
        EligibilityReason.NotYetEligible => "not_yet_eligible",
        EligibilityReason.LegalHold => "legal_hold",
        EligibilityReason.KeyStateUnknown => "key_state_unknown",
        EligibilityReason.CompanyBusy => "company_busy",
        EligibilityReason.RetentionBasisPending => "retention_basis_pending",
        _ => "check_failed",
    };
}

/// <summary>Pure rules of the policy: validation, rule matching and the "in force" test.</summary>
public static class RetentionRules
{
    public const int MaxMonths = 1200;
    public const int MaxRules = 200;
    public const int MaxNoteLength = 500;
    public const int MaxReasonLength = 500;
    public const int MaxReferenceLength = 100;
    public const int MaxAcknowledgementLength = 1000;
    /// <summary>The kind that matches every attachment record kind.</summary>
    public const string AnyKind = "*";

    /// <summary>Validates a draft; returns the field errors (empty when valid). Kinds must be registered (or <c>*</c>); duplicates and out-of-range periods are refused.</summary>
    public static IReadOnlyList<FieldError> Validate(RetentionPolicyInput? input, DateOnly today)
    {
        var errors = new List<FieldError>();
        if (input is null) return [new FieldError("body", "required")];
        if (input.EffectiveFrom is null) errors.Add(new FieldError("effectiveFrom", "required"));
        else if (input.EffectiveFrom.Value < today) errors.Add(new FieldError("effectiveFrom", "in_the_past"));
        if (input.Note is { } note && note.Trim().Length > MaxNoteLength) errors.Add(new FieldError("note", "too_long"));
        var rules = input.Rules;
        if (rules is null || rules.Count == 0) { errors.Add(new FieldError("rules", "required")); return errors; }
        if (rules.Count > MaxRules) { errors.Add(new FieldError("rules", "too_many")); return errors; }
        var seen = new HashSet<(string, short)>();
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (r is null) { errors.Add(new FieldError($"rules[{i}]", "required")); continue; }
            if (r.SubjectKind != AnyKind && !AttachmentSubjects.Kinds.ContainsKey(r.SubjectKind ?? "")) errors.Add(new FieldError($"rules[{i}].subjectKind", "subject_kind_invalid"));
            if (r.Sensitivity is < 0 or > 2) errors.Add(new FieldError($"rules[{i}].sensitivity", "invalid_value"));
            if (r.Indefinite == r.Months.HasValue) errors.Add(new FieldError($"rules[{i}].months", r.Indefinite ? "indefinite_has_months" : "required"));   // exactly one of the two
            else if (r.Months is { } m && (m < 1 || m > MaxMonths)) errors.Add(new FieldError($"rules[{i}].months", "out_of_range"));
            if (r.SubjectKind is not null && !seen.Add((r.SubjectKind, r.Sensitivity))) errors.Add(new FieldError($"rules[{i}]", "duplicate_rule"));
        }
        return errors;
    }

    /// <summary>The rule that applies to an attachment: the most specific match (kind and sensitivity, then kind, then sensitivity, then <c>*</c>); null when none matches (the effective rule is then indefinite).</summary>
    public static RetentionRule? Match(IReadOnlyList<RetentionRule> rules, string subjectKind, bool isSensitive)
    {
        var sens = (short)(isSensitive ? 2 : 1);
        return rules.FirstOrDefault(r => r.SubjectKind == subjectKind && r.Sensitivity == sens)
            ?? rules.FirstOrDefault(r => r.SubjectKind == subjectKind && r.Sensitivity == 0)
            ?? rules.FirstOrDefault(r => r.SubjectKind == AnyKind && r.Sensitivity == sens)
            ?? rules.FirstOrDefault(r => r.SubjectKind == AnyKind && r.Sensitivity == 0);
    }

    /// <summary>Months of a rule, or null for indefinite (also for no matching rule).</summary>
    public static int? PeriodMonths(RetentionRule? rule) => rule is null || rule.Indefinite ? null : rule.Months;

    /// <summary>
    /// Was this version in force at <paramref name="at"/>? Approved by someone other than its author, effective on that date, not yet ended. A revoked or superseded version was in force until its end.
    /// A draft never was.
    /// </summary>
    public static bool InForceAt(RetentionPolicyVersionView v, DateTimeOffset at) =>
        v.Status != RetentionPolicyStatus.Draft && v.ApprovedBy is not null && v.ApprovedBy != v.DraftedBy && v.ApprovedAtUtc is { } approved && approved <= at
        && v.EffectiveFrom <= DateOnly.FromDateTime(at.UtcDateTime) && (v.EndedAtUtc is null || at < v.EndedAtUtc);

    /// <summary>
    /// True when the proposed rules give ANY registered record kind and sensitivity a SHORTER period than the current ones (a rule removed or shortened, indefinite becoming finite). Compared through
    /// <see cref="Match"/> for every registered kind, so a <c>*</c> rule and specific rules are judged by what actually applies.
    /// </summary>
    public static bool Shortens(IReadOnlyList<RetentionRule> current, IReadOnlyList<RetentionRule> proposed)
    {
        foreach (var kind in AttachmentSubjects.Kinds.Keys)
            foreach (var sensitive in new[] { false, true })
                if (Less(PeriodMonths(Match(proposed, kind, sensitive)), PeriodMonths(Match(current, kind, sensitive)))) return true;
        return false;
    }

    /// <summary>Compares periods where null (indefinite) is the longest.</summary>
    public static bool Less(int? a, int? b) => (a, b) switch { (null, _) => false, (_, null) => true, var (x, y) => x < y };
    public static int? Max(int? a, int? b) => a is null || b is null ? null : Math.Max(a.Value, b.Value);
}

/// <summary>
/// The pure eligibility check (docs/PHASE-R-attachment-retention.md section 5). Used when a plan is built, when it is approved and per item right before deletion (R2); R1 only previews.
/// ALL of these must hold for <see cref="EligibilityReason.Eligible"/>: removed; not already purged; a valid policy history; a finite applicable period; the period (the longer of the one in force at removal
/// and the one in force now) has elapsed; no active hold covers it; the key situation is known; the company is not busy; the owner has confirmed the retention basis (D-109, owner-pending). Any exception is <see cref="EligibilityReason.CheckFailed"/>.
/// </summary>
public static class AttachmentRetentionEligibility
{
    public static EligibilityResult Evaluate(AttachmentRetentionFacts a, IReadOnlyList<RetentionPolicyVersionView> policies, IReadOnlyList<LegalHoldView> holds, RetentionEnvironment env, DateTimeOffset now)
    {
        try
        {
            if (!a.IsRemoved || a.RemovedAtUtc is null) return new(EligibilityReason.NotRemoved, null, null, null);
            if (a.AlreadyPurged) return new(EligibilityReason.AlreadyPurged, null, null, null);
            var current = policies.Where(p => RetentionRules.InForceAt(p, now)).OrderByDescending(p => p.Version).FirstOrDefault();
            if (current is null) return new(EligibilityReason.NoValidPolicy, null, null, null);
            var removedAt = a.RemovedAtUtc.Value;
            // A REVOKED or SUPERSEDED version still counts here: it was in force on the removal date (InForceAt), so the period promised at removal is never shortened by a later revocation.
            var atRemoval = policies.Where(p => RetentionRules.InForceAt(p, removedAt)).OrderByDescending(p => p.Version).FirstOrDefault();
            var nowPeriod = RetentionRules.PeriodMonths(RetentionRules.Match(current.Rules, a.SubjectKind, a.IsSensitive));
            // an attachment removed BEFORE any policy existed is Indefinite (no period was ever committed to; the default is preservation). Otherwise no silent retroactive shortening:
            // the longer of the period in force at removal and the current one; indefinite is the longest
            var period = atRemoval is null ? null : RetentionRules.Max(RetentionRules.PeriodMonths(RetentionRules.Match(atRemoval.Rules, a.SubjectKind, a.IsSensitive)), nowPeriod);
            if (period is null) return new(EligibilityReason.Indefinite, null, null, current.Version);
            var eligibleAt = removedAt.AddMonths(period.Value);
            if (now < eligibleAt) return new(EligibilityReason.NotYetEligible, eligibleAt, null, current.Version);
            var hold = holds.FirstOrDefault(h => h.IsActive && Covers(h, a));
            if (hold is not null) return new(EligibilityReason.LegalHold, eligibleAt, hold.Id, current.Version);
            if (a.IsSensitive && string.IsNullOrEmpty(a.KeyId)) return new(EligibilityReason.KeyStateUnknown, eligibleAt, null, current.Version);
            if (env.CompanyBusy) return new(EligibilityReason.CompanyBusy, eligibleAt, null, current.Version);
            if (!env.BasisConfirmed) return new(EligibilityReason.RetentionBasisPending, eligibleAt, null, current.Version);   // D-109 owner-pending: never eligible
            return new(EligibilityReason.Eligible, eligibleAt, null, current.Version);
        }
        catch (Exception)
        {
            return new(EligibilityReason.CheckFailed, null, null, null);
        }
    }

    /// <summary>Does the hold cover the attachment: company-wide, its record, or itself.</summary>
    public static bool Covers(LegalHoldView h, AttachmentRetentionFacts a) => h.Scope switch
    {
        LegalHoldScope.Company => true,
        LegalHoldScope.Record => h.SubjectKind == a.SubjectKind && h.SubjectId == a.SubjectId,
        LegalHoldScope.Attachment => h.AttachmentId == a.Id,
        _ => true,   // an unknown scope blocks (fail closed)
    };
}
