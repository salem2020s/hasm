using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S2 against real PostgreSQL: the two-step login. The password step alone never yields a session for a user who needs the second factor. Disposable cluster only.</summary>
public class MfaLoginTests : IdentityTestBase
{
    private readonly InMemoryKekRing ring = new("k1");
    private MfaStore Mfa = null!;
    private AuthService MfaAuth = null!;

    private async Task Setup()
    {
        await Start();
        var connection = Options.BuildSystemConnectionString(admin: true);
        Mfa = new MfaStore(connection, Clock, Audit, new TotpSecretProtector(ring));
        var challenges = new MfaChallengeStore(connection, Clock);
        var limiter = new MfaAttemptLimiter(Store, Clock);
        var verifier = new MfaVerifier(Mfa, limiter, Clock);
        MfaAuth = new AuthService(Store, Passwords, AuthSettings, null, new MfaLoginServices(challenges, verifier, Mfa, limiter));
    }

    private long Step => Totp.StepFor(Clock.GetUtcNow());

    private async Task<(UserRecord User, byte[] Secret)> EnrolledUser(string login)
    {
        var user = await NewUser(login);
        var secret = Totp.GenerateSecret();
        var id = await Mfa.CreatePendingAsync(user.Id, secret, null);
        Assert.True(await Mfa.ActivatePendingAsync(user.Id, id, Step - 10, null));
        return (user, secret);
    }

    private string Code(byte[] secret, long offset = 0) => Totp.Compute(secret, Step + offset);
    private Task<LoginOutcome> PasswordStep(string login, string source = "10.0.0.1", SessionClient client = SessionClient.Web) => MfaAuth.LoginAsync(login, Pw, client, source);
    private Task<long> Sessions() => Scalar<long>($"SELECT count(*) FROM {S.Table}");
    private Task<long> Challenges() => Scalar<long>($"SELECT count(*) FROM {C.Table}");

    [PostgresFact]
    public async Task A_user_without_MFA_logs_in_exactly_as_before()
    {
        await Setup();
        await NewUser("plain");
        var ok = Assert.IsType<LoginOutcome.Success>(await PasswordStep("plain"));
        Assert.Equal(1L, await Scalar<long>($"SELECT {SM.AuthMethods} FROM {S.Table} WHERE {S.Id} = @i", ("i", ok.SessionId)));            // password only
        Assert.Equal(0L, await Challenges());
    }

    [PostgresFact]
    public async Task An_enrolled_user_gets_a_challenge_and_no_session_until_the_code_is_verified()
    {
        await Setup();
        var (user, secret) = await EnrolledUser("alice");
        var required = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        Assert.Equal(0L, await Sessions());                                                                                                  // no session after the password step
        Assert.Null(await MfaAuth.AuthenticateAsync(required.Challenge));                                                                   // a challenge is not a session token

        var done = Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(required.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));
        Assert.Equal(1L, await Sessions());
        Assert.Equal(3L, await Scalar<long>($"SELECT {SM.AuthMethods} FROM {S.Table} WHERE {S.Id} = @i", ("i", done.SessionId)));            // password + totp
        Assert.True(await Scalar<bool>($"SELECT {SM.VerifiedAtUtc} IS NOT NULL FROM {S.Table} WHERE {S.Id} = @i", ("i", done.SessionId)));
        Assert.NotNull(await MfaAuth.AuthenticateAsync(done.Token));
        Assert.Equal(user.Id, done.User.Id);
    }

    [PostgresFact]
    public async Task A_wrong_a_replayed_or_reused_code_and_a_reused_challenge_all_fail_identically()
    {
        await Setup();
        var (_, secret) = await EnrolledUser("alice");
        var first = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(first.Challenge, "000000", SessionClient.Web, "10.0.0.1"));   // wrong code
        var code = Code(secret);
        Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(first.Challenge, code, SessionClient.Web, "10.0.0.1"));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(first.Challenge, code, SessionClient.Web, "10.0.0.1"));      // the challenge was consumed
        var second = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(second.Challenge, code, SessionClient.Web, "10.0.0.1"));     // the used code (step) is a replay
        Assert.Equal(1L, await Sessions());                                                                                                  // exactly one session was ever created
        var third = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(third.Challenge, Code(secret, 1), SessionClient.Web, "10.0.0.1"));        // the next step is fine
    }

    [PostgresFact]
    public async Task A_challenge_is_bound_to_its_client_and_source_expires_and_is_refused_when_the_user_is_disabled()
    {
        await Setup();
        var (user, secret) = await EnrolledUser("alice");
        var c = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(c.Challenge, Code(secret), SessionClient.Desktop, "10.0.0.1"));   // another client kind
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(c.Challenge, Code(secret), SessionClient.Web, "10.0.0.9"));       // another source
        Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(c.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));       // expired

        var d = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        await Exec($"UPDATE {U.Table} SET {Hasm.Infrastructure.SystemDatabase.SystemSchema.User.State} = 1 WHERE {U.Id} = @u", ("u", user.Id));              // disabled between the two steps
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(d.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));
        Assert.Equal(0L, await Sessions());
    }

    [PostgresFact]
    public async Task A_user_who_must_enrol_gets_only_an_enrolment_challenge_that_cannot_complete_a_login_and_a_RecoveryRequired_user_gets_nothing()
    {
        await Setup();
        var enrol = await NewUser("newbie");
        await Exec($"UPDATE {U.Table} SET {M.State} = 1 WHERE {U.Id} = @u", ("u", enrol.Id));
        var e = Assert.IsType<LoginOutcome.MfaEnrolmentRequired>(await PasswordStep("newbie"));
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(e.Challenge, "123456", SessionClient.Web, "10.0.0.1"));         // purpose enrol, not login

        var lost = await NewUser("lost");
        await Exec($"UPDATE {U.Table} SET {M.State} = 2 WHERE {U.Id} = @u", ("u", lost.Id));
        await Exec($"UPDATE {U.Table} SET {M.State} = 3 WHERE {U.Id} = @u", ("u", lost.Id));
        var before = await Challenges();
        Assert.IsType<LoginOutcome.MfaRecoveryRequired>(await PasswordStep("lost"));
        Assert.Equal(before, await Challenges());                                                                                             // no challenge for a RecoveryRequired user
        Assert.Equal(0L, await Sessions());
    }

    [PostgresFact]
    public async Task Second_factor_failures_are_throttled_per_source_but_never_block_the_password_step_and_the_reverse()
    {
        await Setup();
        var (_, secret) = await EnrolledUser("alice");
        for (var i = 0; i < 5; i++)
        {
            var c = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));                                                        // a fresh challenge each time (one attempt each)
            Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(c.Challenge, "000000", SessionClient.Web, "10.0.0.1"));
        }
        var blocked = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));                                                     // five MFA failures did not touch the password throttle
        Assert.IsType<LoginOutcome.Throttled>(await MfaAuth.CompleteMfaLoginAsync(blocked.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));   // the pair is blocked, even for the right code
        var other = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice", "10.0.0.2"));
        Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(other.Challenge, Code(secret), SessionClient.Web, "10.0.0.2"));   // another source is not locked out

        // and wrong PASSWORDS never count against the second factor
        await NewUser("bob");
        for (var i = 0; i < 5; i++) Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.LoginAsync("bob", "wrong-password-xyz", SessionClient.Web, "10.0.0.3"));
        var (_, bobSecret) = await EnrolledUser("carol");
        var cc = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("carol", "10.0.0.3"));
        Assert.IsType<LoginOutcome.Success>(await MfaAuth.CompleteMfaLoginAsync(cc.Challenge, Code(bobSecret), SessionClient.Web, "10.0.0.3"));
    }

    /// <summary>
    /// G's L6 of PR #133 (won't fix: an INSERT trigger on the user row would break the COPY-based system restore, D-106/D-107): a user row INSERTED as Enrolled with no secret is not a bypass, because the second factor fails closed.
    /// The password step issues a challenge (never a session), and no code can ever verify, so no session is created.
    /// </summary>
    [PostgresFact]
    public async Task A_user_inserted_as_Enrolled_without_a_secret_fails_closed_at_the_second_factor()
    {
        await Setup();
        var template = await NewUser("template");
        await Exec($"INSERT INTO {U.Table} ({U.Id}, {U.Login}, {U.DisplayName}, {U.PasswordHash}, {U.State}, {U.IsIdentityAdministrator}, {U.CreatedAtUtc}, {U.StateChangedAtUtc}, {U.PasswordChangedAtUtc}, {M.State}) " +
                   $"SELECT gen_random_uuid(), 'inserted', 'Inserted', {U.PasswordHash}, 0, false, {U.CreatedAtUtc}, {U.StateChangedAtUtc}, {U.PasswordChangedAtUtc}, 2 FROM {U.Table} WHERE {U.Id} = @t", ("t", template.Id));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp.Table} t JOIN {U.Table} u ON u.{U.Id} = t.{Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp.UserId} WHERE u.{U.Login} = 'inserted'"));

        var required = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("inserted"));                                              // a challenge, never a session
        Assert.Equal(0L, await Sessions());
        foreach (var code in new[] { "000000", "123456", Totp.Compute(Totp.GenerateSecret(), Step) })
            Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(required.Challenge, code, SessionClient.Web, "10.0.0.1"));   // no code can ever verify
        Assert.Equal(0L, await Sessions());
    }

    [PostgresFact]
    public async Task A_missing_key_fails_closed_without_a_session_and_the_service_without_MFA_services_cannot_complete_anything()
    {
        await Setup();
        var (_, secret) = await EnrolledUser("alice");
        var c = Assert.IsType<LoginOutcome.MfaRequired>(await PasswordStep("alice"));
        ring.Unavailable = true;
        Assert.IsType<LoginOutcome.InvalidCredentials>(await MfaAuth.CompleteMfaLoginAsync(c.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));
        ring.Unavailable = false;
        Assert.Equal(0L, await Sessions());
        Assert.IsType<LoginOutcome.InvalidCredentials>(await Auth.CompleteMfaLoginAsync(c.Challenge, Code(secret), SessionClient.Web, "10.0.0.1"));      // the plain service: nothing completes
    }
}
