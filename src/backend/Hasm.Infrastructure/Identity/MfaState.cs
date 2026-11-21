namespace Hasm.Infrastructure.Identity;

/// <summary>The MFA state held on the user row (Phase 4.1, section 4 of the plan). Numeric values are persisted; never renumber.</summary>
public enum MfaState : short { None = 0, EnrolmentRequired = 1, Enrolled = 2, RecoveryRequired = 3 }

/// <summary>Why a user's MFA state changes. Every change has exactly one of these causes; there is no cause that turns MFA off.</summary>
public enum MfaStateCause
{
    /// <summary>The user became subject to a requirement (privileged grant, company policy, creation of an administrator).</summary>
    Requirement,
    /// <summary>The user confirmed a new authenticator.</summary>
    EnrolmentConfirmed,
    /// <summary>An approved, executed recovery request (section 6).</summary>
    Recovery,
    /// <summary>A DR custodian-quorum reset (section 9).</summary>
    DrReset,
    /// <summary>The system-restore procedure found no usable secret for an enrolled user (D-106, D-107).</summary>
    Restore,
}

/// <summary>
/// The allowed MFA state graph, as a pure function (a transition test enumerates every combination). MFA is NEVER silently disabled: nothing leads back to <see cref="MfaState.None"/>,
/// and a user who lost the ability to authenticate (<see cref="MfaState.RecoveryRequired"/>) leaves that state only through recovery or DR, never by enrolling with a password.
/// </summary>
public static class MfaStateGraph
{
    public static bool IsAllowed(MfaState from, MfaState to, MfaStateCause cause) => (from, to, cause) switch
    {
        (MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement) => true,
        (MfaState.EnrolmentRequired, MfaState.Enrolled, MfaStateCause.EnrolmentConfirmed) => true,
        (MfaState.Enrolled, MfaState.EnrolmentRequired, MfaStateCause.Recovery or MfaStateCause.DrReset) => true,
        (MfaState.Enrolled, MfaState.RecoveryRequired, MfaStateCause.Restore) => true,
        (MfaState.RecoveryRequired, MfaState.EnrolmentRequired, MfaStateCause.Recovery or MfaStateCause.DrReset) => true,
        _ => false,
    };

    /// <summary>The state of a NEW user: a user created as (or by the same operation made) subject to a requirement starts in <see cref="MfaState.EnrolmentRequired"/>.</summary>
    public static MfaState InitialState(bool subjectToRequirement) => subjectToRequirement ? MfaState.EnrolmentRequired : MfaState.None;
}

/// <summary>Throttle keys of the second factor (<see cref="ThrottleScope.Mfa"/>): SHA-256 of a domain-separated string, never the raw user id or address.</summary>
public static class MfaThrottleKeys
{
    public static byte[] UserAndSource(Guid userId, string source) => SessionToken.HashKey("mfa-pair:" + userId.ToString("N") + "|" + source);
    public static byte[] User(Guid userId) => SessionToken.HashKey("mfa-user:" + userId.ToString("N"));
}
