using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S3a against real PostgreSQL: step-up moves the session's second-factor proof, in one transaction with its audit event, through the single verifier. Disposable cluster only.</summary>
public class MfaStepUpTests : IdentityTestBase
{
    private readonly InMemoryKekRing ring = new("k1", TestIdentityKeys.Master);
    private MfaStore Mfa = null!;
    private AuthService MfaAuth = null!;

    private async Task Setup()
    {
        await Start();
        var connection = Options.BuildSystemConnectionString(admin: true);
        Mfa = new MfaStore(connection, Clock, Audit, new TotpSecretProtector(ring), keys: TestIdentityKeys.Instance);
        var limiter = new MfaAttemptLimiter(Store, Clock);
        MfaAuth = new AuthService(Store, Passwords, AuthSettings, null, new MfaLoginServices(new MfaChallengeStore(connection, Clock), new MfaVerifier(Mfa, limiter, Clock), Mfa, limiter));
    }

    private long Step => Totp.StepFor(Clock.GetUtcNow());
    private string Code(byte[] secret, long offset = 0) => Totp.Compute(secret, Step + offset);
    private Task<long> Events(short type) => Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} = @t", ("t", type));

    /// <summary>An enrolled user with a live, MFA-verified session (the code used at login is the one for the current step).</summary>
    private async Task<(AuthenticatedSession Session, byte[] Secret, string Token)> SignedIn(string login)
    {
        var user = await NewUser(login);
        var secret = Totp.GenerateSecret();
        Assert.True(await Mfa.ActivatePendingAsync(user.Id, await Mfa.CreatePendingAsync(user.Id, secret, null), Step - 10, null));
        var challenge = Assert.IsType<LoginOutcome.MfaRequired>(await MfaAuth.LoginAsync(login, Pw, SessionClient.Web, "10.0.0.1"));
        var ok = Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(challenge.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));
        return ((await MfaAuth.AuthenticateAsync(ok.Token))!, secret, ok.Token);
    }

    private async Task<DateTimeOffset?> ProofOf(Guid sessionId) =>
        await Scalar<object>($"SELECT {SM.VerifiedAtUtc} FROM {S.Table} WHERE {S.Id} = @i", ("i", sessionId)) switch
        {
            DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
            DateTimeOffset o => o,
            _ => null,
        };

    [PostgresFact]
    public async Task A_successful_step_up_moves_the_proof_and_is_audited_and_a_stale_session_becomes_fresh_again()
    {
        await Setup();
        var (session, secret, token) = await SignedIn("alice");
        var window = AuthSettings.MfaFreshness;
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(session.MfaState, session.MfaVerifiedAtUtc, Clock.GetUtcNow(), window));

        Clock.Advance(window + TimeSpan.FromMinutes(1));                                                  // the proof is now older than the window (and the next TOTP step is a new code)
        var stale = (await MfaAuth.AuthenticateAsync(token))!;
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireVerified(stale.MfaState, stale.MfaVerifiedAtUtc));   // still a verified session...
        Assert.Equal(MfaRequirementOutcome.StepUpRequired, MfaFreshness.RequireFresh(stale.MfaState, stale.MfaVerifiedAtUtc, Clock.GetUtcNow(), window));   // ...but not fresh

        var done = Assert.IsType<StepUpOutcome.Verified>(await MfaAuth.StepUpAsync(stale, Code(secret), "10.0.0.1"));
        Assert.Equal(Clock.GetUtcNow(), done.VerifiedAtUtc);
        Assert.Equal(Clock.GetUtcNow(), await ProofOf(session.SessionId));
        var fresh = (await MfaAuth.AuthenticateAsync(token))!;
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(fresh.MfaState, fresh.MfaVerifiedAtUtc, Clock.GetUtcNow(), window));
        Assert.Equal(1L, await Events(MfaAuditEvents.StepUpSucceeded));
    }

    [PostgresFact]
    public async Task A_wrong_or_replayed_code_is_refused_audited_and_leaves_the_proof_unchanged()
    {
        await Setup();
        var (session, secret, _) = await SignedIn("alice");
        var before = await ProofOf(session.SessionId);
        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsType<StepUpOutcome.Refused>(await MfaAuth.StepUpAsync(session, "000000", "10.0.0.1"));
        Assert.Equal(before, await ProofOf(session.SessionId));
        Assert.Equal(1L, await Events(MfaAuditEvents.StepUpFailed));
        Assert.Equal(0L, await Events(MfaAuditEvents.StepUpSucceeded));

        Assert.IsType<StepUpOutcome.Verified>(await MfaAuth.StepUpAsync(session, Code(secret), "10.0.0.1"));
        Assert.IsType<StepUpOutcome.Refused>(await MfaAuth.StepUpAsync(session, Code(secret), "10.0.0.1"));       // the same step cannot be used twice (the verifier's replay protection)
        Assert.Equal(2L, await Events(MfaAuditEvents.StepUpFailed));
        Assert.Equal(1L, await Events(MfaAuditEvents.StepUpSucceeded));
    }

    [PostgresFact]
    public async Task A_code_accepted_at_login_cannot_be_reused_for_a_step_up()
    {
        await Setup();
        var (session, secret, _) = await SignedIn("alice");                                              // login consumed the code of the current step
        Assert.IsType<StepUpOutcome.Refused>(await MfaAuth.StepUpAsync(session, Code(secret), "10.0.0.1"));
        Assert.Equal(1L, await Events(MfaAuditEvents.StepUpFailed));
        Assert.Equal(0L, await Events(MfaAuditEvents.StepUpSucceeded));
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));                                             // the next step is a new code
        Assert.IsType<StepUpOutcome.Verified>(await MfaAuth.StepUpAsync(session, Code(secret), "10.0.0.1"));
    }

    [PostgresFact]
    public async Task A_step_up_on_one_session_leaves_another_session_of_the_same_user_stale()
    {
        await Setup();
        var (a, secret, tokenA) = await SignedIn("alice");
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));                                             // a new step, so the second login's code is not a replay
        var challenge = Assert.IsType<LoginOutcome.MfaRequired>(await MfaAuth.LoginAsync("alice", Pw, SessionClient.Desktop, "10.0.0.2"));
        var okB = Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(challenge.Challenge, Code(secret), SessionClient.Desktop, "10.0.0.2"));
        var bBefore = await ProofOf(okB.SessionId);

        Clock.Advance(AuthSettings.MfaFreshness + TimeSpan.FromMinutes(2));                                // both proofs are now older than the window
        var staleA = (await MfaAuth.AuthenticateAsync(tokenA))!;
        Assert.IsType<StepUpOutcome.Verified>(await MfaAuth.StepUpAsync(staleA, Code(secret), "10.0.0.1"));

        var freshA = (await MfaAuth.AuthenticateAsync(tokenA))!;
        var staleB = (await MfaAuth.AuthenticateAsync(okB.Token))!;
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(freshA.MfaState, freshA.MfaVerifiedAtUtc, Clock.GetUtcNow(), AuthSettings.MfaFreshness));
        Assert.Equal(MfaRequirementOutcome.StepUpRequired, MfaFreshness.RequireFresh(staleB.MfaState, staleB.MfaVerifiedAtUtc, Clock.GetUtcNow(), AuthSettings.MfaFreshness));
        Assert.Equal(bBefore, await ProofOf(okB.SessionId));                                               // B's stored proof did not move
        Assert.NotEqual(a.SessionId, okB.SessionId);
    }

    [PostgresFact]
    public async Task Repeated_wrong_codes_are_throttled_by_the_shared_mfa_limiter()
    {
        await Setup();
        var (session, _, _) = await SignedIn("alice");
        for (var i = 0; i < 5; i++) Assert.IsType<StepUpOutcome.Refused>(await MfaAuth.StepUpAsync(session, "00000" + i, "10.0.0.1"));
        var failed = await Events(MfaAuditEvents.StepUpFailed);
        var throttled = Assert.IsType<StepUpOutcome.Throttled>(await MfaAuth.StepUpAsync(session, "000009", "10.0.0.1"));
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);
        for (var i = 0; i < 3; i++) Assert.IsType<StepUpOutcome.Throttled>(await MfaAuth.StepUpAsync(session, "000009", "10.0.0.1"));
        Assert.Equal(failed, await Events(MfaAuditEvents.StepUpFailed));                                  // throttled answers append no audit rows
    }

    [PostgresFact]
    public async Task A_user_without_MFA_is_not_asked_and_nothing_is_written()
    {
        await Setup();
        await NewUser("plain");
        var ok = Assert.IsType<LoginOutcome.Success>(await MfaAuth.LoginAsync("plain", Pw, SessionClient.Web, "10.0.0.1"));
        var session = (await MfaAuth.AuthenticateAsync(ok.Token))!;
        Assert.Equal(MfaState.None, session.MfaState);
        Assert.IsType<StepUpOutcome.NotApplicable>(await MfaAuth.StepUpAsync(session, "123456", "10.0.0.1"));
        Assert.Equal(0L, await Events(MfaAuditEvents.StepUpSucceeded) + await Events(MfaAuditEvents.StepUpFailed));
        Assert.Null(await ProofOf(session.SessionId));
        // pass-through: both requirements are satisfied for this session, whatever its age
        Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireVerified(session.MfaState, session.MfaVerifiedAtUtc));
        Assert.Equal(MfaRequirementOutcome.Satisfied, MfaFreshness.RequireFresh(session.MfaState, session.MfaVerifiedAtUtc, Clock.GetUtcNow(), AuthSettings.MfaFreshness));
    }

    [PostgresFact]
    public async Task A_revoked_session_or_another_users_session_cannot_be_stepped_up()
    {
        await Setup();
        var (alice, secret, _) = await SignedIn("alice");
        var (bob, _, _) = await SignedIn("bob");
        Assert.Null(await Mfa.RecordStepUpAsync(bob.SessionId, alice.UserId));                           // not the user's session: nothing changes
        await Auth.LogoutAsync(alice.SessionId);
        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsType<StepUpOutcome.Refused>(await MfaAuth.StepUpAsync(alice, Code(secret), "10.0.0.1"));  // the session ended meanwhile
        Assert.Equal(0L, await Events(MfaAuditEvents.StepUpSucceeded));
    }
}
