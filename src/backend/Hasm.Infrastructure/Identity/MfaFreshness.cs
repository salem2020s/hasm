namespace Hasm.Infrastructure.Identity;

/// <summary>What an operation that needs the second factor finds on the session.</summary>
public enum MfaRequirementOutcome { Satisfied, StepUpRequired }

/// <summary>
/// The ONE evaluation of the second-factor requirements of a session (Phase 4.1 S3a, D-160, D-161). A user whose MFA state is <see cref="MfaState.None"/> passes both
/// checks: MFA is not required of them, so every flow they have today is unchanged (D-096..D-107). Any other state needs a recorded proof; the fresh check also needs the
/// proof to be within the window (D-103, bounded 5..60 minutes by <see cref="AuthOptions"/>). A proof dated in the future is not fresh (clock skew fails closed).
/// </summary>
public static class MfaFreshness
{
    public static MfaRequirementOutcome RequireVerified(MfaState state, DateTimeOffset? verifiedAtUtc) =>
        state == MfaState.None || verifiedAtUtc is not null ? MfaRequirementOutcome.Satisfied : MfaRequirementOutcome.StepUpRequired;

    public static MfaRequirementOutcome RequireFresh(MfaState state, DateTimeOffset? verifiedAtUtc, DateTimeOffset nowUtc, TimeSpan window)
    {
        if (state == MfaState.None) return MfaRequirementOutcome.Satisfied;
        if (verifiedAtUtc is not { } at) return MfaRequirementOutcome.StepUpRequired;
        var age = nowUtc - at;
        return age >= TimeSpan.Zero && age <= window ? MfaRequirementOutcome.Satisfied : MfaRequirementOutcome.StepUpRequired;
    }
}
