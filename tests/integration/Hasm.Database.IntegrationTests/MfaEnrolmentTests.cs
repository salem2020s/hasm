using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;
using TO = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S2, decision D-142: the enrolment steps against real PostgreSQL. Disposable cluster only.</summary>
public class MfaEnrolmentTests : IdentityTestBase
{
    private readonly InMemoryKekRing ring = new("k1");
    private MfaStore Mfa = null!;
    private AuthService MfaAuth = null!;
    private const string Source = "10.0.0.1";

    private async Task Setup()
    {
        await Start();
        var connection = Options.BuildSystemConnectionString(admin: true);
        Mfa = new MfaStore(connection, Clock, Audit, new TotpSecretProtector(ring));
        var challenges = new MfaChallengeStore(connection, Clock);
        var limiter = new MfaAttemptLimiter(Store, Clock);
        MfaAuth = new AuthService(Store, Passwords, AuthSettings, null, new MfaLoginServices(challenges, new MfaVerifier(Mfa, limiter, Clock), Mfa, limiter));
    }

    private long Step => Totp.StepFor(Clock.GetUtcNow());
    private static byte[] SecretOf(MfaEnrolOutcome.Started s) { Assert.True(Totp.TryFromBase32(s.SecretBase32, out var secret)); return secret; }
    private string Code(byte[] secret, long offset = 0) => Totp.Compute(secret, Step + offset);
    private Task SetState(Guid user, int state) => Exec($"UPDATE {U.Table} SET {M.State} = @s WHERE {U.Id} = @u", ("s", (short)state), ("u", user));
    private Task<long> Pending(Guid user) => Scalar<long>($"SELECT count(*) FROM {TO.Table} WHERE {TO.UserId} = @u AND {TO.State} = 0", ("u", user));

    private async Task<LoginOutcome.Success> Session(string login, string source = Source) => Assert.IsType<LoginOutcome.Success>(await MfaAuth.LoginAsync(login, Pw, SessionClient.Web, source));
    private async Task<AuthenticatedSession> Authenticated(LoginOutcome.Success s) => (await MfaAuth.AuthenticateAsync(s.Token))!;

    [PostgresFact]
    public async Task A_required_enrolment_with_the_one_time_code_ends_in_a_session_spends_the_code_and_revokes_every_earlier_session()
    {
        await Setup();
        var user = await NewUser("boss");
        var early = await Session("boss", "10.9.9.9");                                                         // a session that exists before the enrolment is required
        await SetState(user.Id, 1);
        var code = await Mfa.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(24), null);
        var challenge = Assert.IsType<LoginOutcome.MfaEnrolmentRequired>(await MfaAuth.LoginAsync("boss", Pw, SessionClient.Web, Source));

        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithChallengeAsync(challenge.Challenge, "WRONGWRONGWRONG1", SessionClient.Web, Source));   // a wrong code
        Assert.Equal(0L, await Pending(user.Id));
        var started = Assert.IsType<MfaEnrolOutcome.Started>(await MfaAuth.BeginEnrolmentWithChallengeAsync(challenge.Challenge, code, SessionClient.Web, Source));
        Assert.StartsWith("otpauth://totp/", started.OtpauthUri);
        var secret = SecretOf(started);
        Assert.Equal(MfaState.EnrolmentRequired, await Mfa.GetStateAsync(user.Id));                            // not yet: the confirmation activates

        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.ConfirmEnrolmentWithChallengeAsync(challenge.Challenge, "000000", SessionClient.Web, Source));   // a wrong TOTP
        var done = Assert.IsType<MfaEnrolOutcome.Confirmed>(await MfaAuth.ConfirmEnrolmentWithChallengeAsync(challenge.Challenge, Code(secret), SessionClient.Web, Source));
        Assert.NotNull(done.NewSession);
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
        Assert.Equal(3L, await Scalar<long>($"SELECT {SM.AuthMethods}::bigint FROM {S.Table} WHERE {S.Id} = @i", ("i", done.NewSession!.SessionId)));   // password + totp
        Assert.Null(await MfaAuth.AuthenticateAsync(early.Token));                                             // the earlier session was revoked
        Assert.NotNull(await MfaAuth.AuthenticateAsync(done.NewSession.Token));
        Assert.False(await Mfa.CheckEnrolmentCodeAsync(user.Id, code));                                        // the one-time code is spent
        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.ConfirmEnrolmentWithChallengeAsync(challenge.Challenge, Code(secret, 1), SessionClient.Web, Source));   // the challenge is consumed
    }

    [PostgresFact]
    public async Task A_required_enrolment_without_a_stored_code_cannot_start_and_the_user_stays_without_a_session()
    {
        await Setup();
        var user = await NewUser("nocode");
        await SetState(user.Id, 1);
        var challenge = Assert.IsType<LoginOutcome.MfaEnrolmentRequired>(await MfaAuth.LoginAsync("nocode", Pw, SessionClient.Web, Source));
        foreach (var guess in new[] { "", "ABCDEFGHIJKLMNOP", "AAAAAAAAAAAAAAAA" })
            Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithChallengeAsync(challenge.Challenge, guess, SessionClient.Web, Source));
        Assert.Equal(0L, await Pending(user.Id));
        Assert.Equal(MfaState.EnrolmentRequired, await Mfa.GetStateAsync(user.Id));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.UserId} = @u", ("u", user.Id)));
        // an expired code never works either
        var code = await Mfa.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(1), null);
        Clock.Advance(TimeSpan.FromHours(2));
        var fresh = Assert.IsType<LoginOutcome.MfaEnrolmentRequired>(await MfaAuth.LoginAsync("nocode", Pw, SessionClient.Web, Source));
        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithChallengeAsync(fresh.Challenge, code, SessionClient.Web, Source));
    }

    [PostgresFact]
    public async Task A_voluntary_enrolment_needs_a_fresh_password_and_a_wrong_password_counts_as_a_login_failure()
    {
        await Setup();
        var user = await NewUser("vol");
        var s = await Session("vol");
        var session = await Authenticated(s);
        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, null, Source));            // no password: failure 1
        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, "", Source));              // failure 2
        for (var i = 0; i < 2; i++) Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, "wrong-password-" + i, Source));   // failures 3 and 4
        Assert.Equal(0L, await Pending(user.Id));
        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, "wrong-password-4", Source));   // the fifth failure blocks the login pair ...
        Assert.IsType<LoginOutcome.Throttled>(await MfaAuth.LoginAsync("vol", Pw, SessionClient.Web, Source));                            // ... exactly as five failed logins would
        Assert.IsType<MfaEnrolOutcome.Throttled>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, Pw, Source));                       // and the right password is refused while blocked
    }

    [PostgresFact]
    public async Task A_voluntary_enrolment_confirms_in_the_live_session_revokes_the_others_and_upgrades_the_confirming_session()
    {
        await Setup();
        var user = await NewUser("vol");
        var other = await Session("vol", "10.8.8.8");
        var s = await Session("vol");
        var session = await Authenticated(s);
        var started = Assert.IsType<MfaEnrolOutcome.Started>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, Pw, Source));
        var secret = SecretOf(started);
        Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.ConfirmEnrolmentWithSessionAsync(session, "000000", Source));
        Assert.Equal(MfaState.None, await Mfa.GetStateAsync(user.Id));
        var done = Assert.IsType<MfaEnrolOutcome.Confirmed>(await MfaAuth.ConfirmEnrolmentWithSessionAsync(session, Code(secret), Source));
        Assert.NotNull(done.NewSession);                                                                       // D-142 (d): a NEW session (a new cookie or bearer token), returned like a login
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
        Assert.Null(await MfaAuth.AuthenticateAsync(other.Token));                                             // every other session is revoked ...
        Assert.Null(await MfaAuth.AuthenticateAsync(s.Token));                                                 // ... and so is the confirming one: its old token no longer works
        var fresh = (await MfaAuth.AuthenticateAsync(done.NewSession!.Token))!;                                // the new session is password + totp
        Assert.Equal(MfaState.Enrolled, fresh.MfaState);
        Assert.NotNull(fresh.MfaVerifiedAtUtc);
        Assert.Equal(3L, await Scalar<long>($"SELECT {SM.AuthMethods}::bigint FROM {S.Table} WHERE {S.Id} = @i", ("i", done.NewSession.SessionId)));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL", ("u", user.Id)));   // exactly one live session remains
        // the used code cannot sign in again (replay protection carries over from the confirmation)
        var login = Assert.IsType<LoginOutcome.MfaRequired>(await MfaAuth.LoginAsync("vol", Pw, SessionClient.Web, "10.7.7.7"));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(login.Challenge, Code(secret), SessionClient.Web, "10.7.7.7"));
    }

    [PostgresFact]
    public async Task A_session_cannot_start_a_voluntary_enrolment_when_the_user_is_enrolled_must_enrol_or_needs_recovery_and_a_missing_session_is_refused()
    {
        await Setup();
        foreach (var (login, state) in new[] { ("enrolled", 2), ("required", 1), ("recover", 3) })
        {
            var u = await NewUser(login);
            var s = await Session(login);
            var session = await Authenticated(s);
            if (state == 3) { await SetState(u.Id, 2); await SetState(u.Id, 3); } else await SetState(u.Id, state);
            Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, Pw, Source));
            Assert.IsType<MfaEnrolOutcome.Refused>(await MfaAuth.ConfirmEnrolmentWithSessionAsync(session, "123456", Source));
            Assert.Equal(0L, await Pending(u.Id));
        }
        Assert.IsType<MfaEnrolOutcome.Refused>(await Auth.BeginEnrolmentWithChallengeAsync("x", "y", SessionClient.Web, Source));       // the plain service (no MFA services) refuses everything
    }

    private async Task<long> LiveSessions(Guid user) => await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL", ("u", user));

    /// <summary>Review M3 (challenge mode): the challenge is consumed first; then ONE transaction activates, spends the code, revokes the sessions and creates the new one. A failure at either seam undoes ALL of it.</summary>
    [PostgresTheory]
    [InlineData("after_activate")]
    [InlineData("after_revoke")]
    public async Task A_failure_between_the_steps_of_a_challenge_mode_confirmation_leaves_nothing_half_done(string failAt)
    {
        await Setup();
        var user = await NewUser("atomic");
        var early = await Session("atomic", "10.9.9.9");
        await SetState(user.Id, 1);
        var code = await Mfa.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(24), null);
        var challenge = Assert.IsType<LoginOutcome.MfaEnrolmentRequired>(await MfaAuth.LoginAsync("atomic", Pw, SessionClient.Web, Source));
        var secret = SecretOf(Assert.IsType<MfaEnrolOutcome.Started>(await MfaAuth.BeginEnrolmentWithChallengeAsync(challenge.Challenge, code, SessionClient.Web, Source)));

        Mfa.FaultProbe = step => step == failAt ? Task.FromException(new InvalidOperationException("injected")) : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(() => MfaAuth.ConfirmEnrolmentWithChallengeAsync(challenge.Challenge, Code(secret), SessionClient.Web, Source));
        Mfa.FaultProbe = null;

        Assert.Equal(MfaState.EnrolmentRequired, await Mfa.GetStateAsync(user.Id));                            // the state did not move
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {TO.Table} WHERE {TO.UserId} = @u AND {TO.State} = 1", ("u", user.Id)));   // no active secret
        Assert.Equal(1L, await Pending(user.Id));                                                               // the enrolment is still pending
        Assert.True(await Mfa.CheckEnrolmentCodeAsync(user.Id, code));                                          // the one-time code is NOT spent
        Assert.Null(await MfaAuth.AuthenticateAsync(early.Token));                                              // the earlier password-only session is rejected by the defence in depth (the user now needs MFA) ...
        Assert.Equal(1L, await LiveSessions(user.Id));                                                          // ... but the failed attempt revoked nothing and created nothing
        // the challenge was consumed first (single use): the user simply logs in again and completes the enrolment
        var again = Assert.IsType<LoginOutcome.MfaEnrolmentRequired>(await MfaAuth.LoginAsync("atomic", Pw, SessionClient.Web, Source));
        Assert.IsType<MfaEnrolOutcome.Started>(await MfaAuth.BeginEnrolmentWithChallengeAsync(again.Challenge, code, SessionClient.Web, Source));
        var pendingSecret = (await Mfa.FindPendingSecretAsync(user.Id, default))!;
        var finalSecret = pendingSecret.Secret.ToArray();
        pendingSecret.Dispose();
        Assert.IsType<MfaEnrolOutcome.Confirmed>(await MfaAuth.ConfirmEnrolmentWithChallengeAsync(again.Challenge, Code(finalSecret), SessionClient.Web, Source));
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
    }

    [PostgresTheory]
    [InlineData("after_activate")]
    [InlineData("after_revoke")]
    public async Task A_failure_between_the_steps_of_a_session_mode_confirmation_leaves_nothing_half_done(string failAt)
    {
        await Setup();
        var user = await NewUser("atomic2");
        var other = await Session("atomic2", "10.8.8.8");
        var s = await Session("atomic2");
        var session = await Authenticated(s);
        var secret = SecretOf(Assert.IsType<MfaEnrolOutcome.Started>(await MfaAuth.BeginEnrolmentWithSessionAsync(session, Pw, Source)));

        Mfa.FaultProbe = step => step == failAt ? Task.FromException(new InvalidOperationException("injected")) : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(() => MfaAuth.ConfirmEnrolmentWithSessionAsync(session, Code(secret), Source));
        Mfa.FaultProbe = null;

        Assert.Equal(MfaState.None, await Mfa.GetStateAsync(user.Id));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {TO.Table} WHERE {TO.UserId} = @u AND {TO.State} = 1", ("u", user.Id)));
        Assert.Equal(1L, await Pending(user.Id));
        Assert.NotNull(await MfaAuth.AuthenticateAsync(s.Token));                                              // the confirming session and the other one are both still alive
        Assert.NotNull(await MfaAuth.AuthenticateAsync(other.Token));
        Assert.Equal(2L, await LiveSessions(user.Id));
        // retry on the same session completes (the code of the next step)
        Assert.IsType<MfaEnrolOutcome.Confirmed>(await MfaAuth.ConfirmEnrolmentWithSessionAsync(session, Code(secret, 1), Source));
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
        Assert.Equal(1L, await LiveSessions(user.Id));
    }
}
