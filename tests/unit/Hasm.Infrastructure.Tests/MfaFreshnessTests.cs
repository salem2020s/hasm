using Hasm.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 slice S3a: the one evaluation of the second-factor requirements (D-160, D-161) and the freshness window option (D-103).</summary>
public class MfaFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3600)]
    public void A_user_without_MFA_passes_both_checks_whatever_the_session_holds(int? proofAgeSeconds)
    {
        DateTimeOffset? proof = proofAgeSeconds is { } s ? Now.AddSeconds(-s) : null;
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireVerified(MfaState.None, proof));
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(MfaState.None, proof, Now, Window));
    }

    [Theory]
    [InlineData(MfaState.Enrolled)]
    [InlineData(MfaState.EnrolmentRequired)]
    [InlineData(MfaState.RecoveryRequired)]
    public void Every_other_state_needs_a_recorded_proof(MfaState state)
    {
        Assert.Equal(MfaRequirementOutcome.StepUpRequired, MfaFreshness.RequireVerified(state, null));
        Assert.Equal(MfaRequirementOutcome.StepUpRequired, MfaFreshness.RequireFresh(state, null, Now, Window));
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireVerified(state, Now.AddDays(-1)));          // verified is not about age
    }

    [Fact]
    public void Freshness_is_inclusive_at_the_window_edge_and_a_future_proof_fails_closed()
    {
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(MfaState.Enrolled, Now, Now, Window));
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(MfaState.Enrolled, Now - Window, Now, Window));
        Assert.Equal(MfaRequirementOutcome.StepUpRequired, MfaFreshness.RequireFresh(MfaState.Enrolled, Now - Window - TimeSpan.FromTicks(1), Now, Window));
        Assert.Equal(MfaRequirementOutcome.StepUpRequired, MfaFreshness.RequireFresh(MfaState.Enrolled, Now.AddSeconds(1), Now, Window));   // clock skew is never "fresh"
    }

    private static AuthOptions FromSetting(string? value) =>
        AuthOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { ["Hasm:Auth:MfaFreshnessMinutes"] = value }).Build());

    [Fact]
    public void The_window_defaults_to_15_minutes_and_is_bounded_5_to_60()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), FromSetting(null).MfaFreshness);
        Assert.Equal(TimeSpan.FromMinutes(5), FromSetting("5").MfaFreshness);
        Assert.Equal(TimeSpan.FromMinutes(60), FromSetting("60").MfaFreshness);
        foreach (var bad in new[] { "4", "61", "0", "-1", "abc" })
            Assert.Throws<AuthConfigurationException>(() => FromSetting(bad));
    }
}
