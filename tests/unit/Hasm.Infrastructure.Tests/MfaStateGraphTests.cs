using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 S1: the MFA state graph (never silently off) and the second-factor throttle keys.</summary>
public class MfaStateGraphTests
{
    private static readonly (MfaState From, MfaState To, MfaStateCause Cause)[] Allowed =
    [
        (MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement),
        (MfaState.None, MfaState.Enrolled, MfaStateCause.EnrolmentConfirmed),                                  // a voluntary enrolment
        (MfaState.EnrolmentRequired, MfaState.Enrolled, MfaStateCause.EnrolmentConfirmed),
        (MfaState.Enrolled, MfaState.EnrolmentRequired, MfaStateCause.Recovery),
        (MfaState.Enrolled, MfaState.EnrolmentRequired, MfaStateCause.DrReset),
        (MfaState.Enrolled, MfaState.RecoveryRequired, MfaStateCause.Restore),
        (MfaState.RecoveryRequired, MfaState.EnrolmentRequired, MfaStateCause.Recovery),
        (MfaState.RecoveryRequired, MfaState.EnrolmentRequired, MfaStateCause.DrReset),
    ];

    [Fact]
    public void Exactly_the_listed_transitions_are_allowed_in_the_whole_combination_space()
    {
        var all = from f in Enum.GetValues<MfaState>() from t in Enum.GetValues<MfaState>() from c in Enum.GetValues<MfaStateCause>() select (f, t, c);
        var actual = all.Where(x => MfaStateGraph.IsAllowed(x.f, x.t, x.c)).ToHashSet();
        Assert.Equal(Allowed.ToHashSet(), actual);
    }

    [Fact]
    public void Nothing_ever_leads_back_to_None_so_MFA_is_never_silently_off()
    {
        foreach (var f in Enum.GetValues<MfaState>())
            foreach (var c in Enum.GetValues<MfaStateCause>())
                Assert.False(MfaStateGraph.IsAllowed(f, MfaState.None, c), $"{f} -> None by {c}");
    }

    [Fact]
    public void A_user_who_cannot_authenticate_never_enrols_with_a_password_only()
    {
        foreach (var c in Enum.GetValues<MfaStateCause>())
        {
            Assert.False(MfaStateGraph.IsAllowed(MfaState.RecoveryRequired, MfaState.Enrolled, c));
            if (c is not (MfaStateCause.Recovery or MfaStateCause.DrReset)) Assert.False(MfaStateGraph.IsAllowed(MfaState.RecoveryRequired, MfaState.EnrolmentRequired, c));
        }
    }

    [Fact]
    public void The_persisted_numeric_values_are_fixed()
    {
        Assert.Equal([0, 1, 2, 3], new[] { MfaState.None, MfaState.EnrolmentRequired, MfaState.Enrolled, MfaState.RecoveryRequired }.Select(s => (int)s));
    }

    [Fact]
    public void New_users_start_enrolment_required_only_when_subject_to_a_requirement()
    {
        Assert.Equal(MfaState.EnrolmentRequired, MfaStateGraph.InitialState(true));
        Assert.Equal(MfaState.None, MfaStateGraph.InitialState(false));
    }

    [Fact]
    public void Throttle_keys_are_deterministic_32_byte_hashes_that_differ_per_user_source_and_scope()
    {
        var u = Guid.NewGuid(); var v = Guid.NewGuid();
        Assert.Equal(32, MfaThrottleKeys.User(u).Length);
        Assert.Equal(MfaThrottleKeys.UserAndSource(u, "a"), MfaThrottleKeys.UserAndSource(u, "a"));
        Assert.NotEqual(MfaThrottleKeys.UserAndSource(u, "a"), MfaThrottleKeys.UserAndSource(u, "b"));
        Assert.NotEqual(MfaThrottleKeys.UserAndSource(u, "a"), MfaThrottleKeys.UserAndSource(v, "a"));
        Assert.NotEqual(MfaThrottleKeys.User(u), MfaThrottleKeys.UserAndSource(u, ""));
        Assert.NotEqual(MfaThrottleKeys.User(u), MfaThrottleKeys.User(v));
    }
}
