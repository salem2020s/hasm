using Microsoft.Extensions.Logging;

namespace Hasm.Infrastructure.Identity;

public abstract record LoginOutcome
{
    /// <summary>Any failure that must look identical to the caller (unknown login, wrong password, disabled, locked).</summary>
    public sealed record InvalidCredentials : LoginOutcome;
    public sealed record Throttled(TimeSpan RetryAfter) : LoginOutcome;
    public sealed record Success(string Token, UserRecord User, Guid SessionId, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset AbsoluteExpiresAtUtc) : LoginOutcome;
    /// <summary>Phase 4.1: the password was right and a second factor is required. NO session exists; the challenge (single use, bound to client and source, five minutes) completes the login.</summary>
    public sealed record MfaRequired(string Challenge, DateTimeOffset ChallengeExpiresAtUtc) : LoginOutcome;
    /// <summary>Phase 4.1: the password was right and the user must enrol an authenticator first. NO session exists; the challenge only admits the enrolment steps.</summary>
    public sealed record MfaEnrolmentRequired(string Challenge, DateTimeOffset ChallengeExpiresAtUtc) : LoginOutcome;
    /// <summary>Phase 4.1: the password was right but the user has no usable authenticator (for example after a restore). NO session and NO challenge: only recovery or DR leads back.</summary>
    public sealed record MfaRecoveryRequired : LoginOutcome;
}

/// <summary>Result of a step-up (Phase 4.1 S3a). NotApplicable: the user's MFA state is None, so MFA is not required of them (pass-through, D-161). Refused covers a wrong, replayed or malformed code, a missing secret and a missing key: one answer for every cause.</summary>
public abstract record StepUpOutcome
{
    public sealed record Verified(DateTimeOffset VerifiedAtUtc) : StepUpOutcome;
    public sealed record Refused : StepUpOutcome;
    public sealed record Throttled(TimeSpan RetryAfter) : StepUpOutcome;
    public sealed record NotApplicable : StepUpOutcome;
}

/// <summary>The second-factor services the login uses. When absent (no encryption key ring configured, or a test that does not use MFA) the login behaves exactly as before and issues sessions after the password.</summary>
public sealed record MfaLoginServices(MfaChallengeStore Challenges, MfaVerifier Verifier, MfaStore State, IMfaAttemptLimiter Limiter);

/// <summary>The answers of the enrolment steps. Every refusal is the same <see cref="Refused"/> (no oracle).</summary>
public abstract record MfaEnrolOutcome
{
    /// <summary>The new secret, to be shown ONCE (QR code from the provisioning URI, the Base32 text as a fallback).</summary>
    public sealed record Started(string OtpauthUri, string SecretBase32) : MfaEnrolOutcome;
    /// <summary>The enrolment is complete. <see cref="NewSession"/> is set for an enrolment that came from a challenge (a session is created now); null when the caller's own session was upgraded.</summary>
    public sealed record Confirmed(LoginOutcome.Success? NewSession) : MfaEnrolOutcome;
    public sealed record Refused : MfaEnrolOutcome;
    public sealed record Throttled(TimeSpan RetryAfter) : MfaEnrolOutcome;
}

public sealed record AuthenticatedSession(
    Guid SessionId, Guid UserId, string Login, string DisplayName, bool IsIdentityAdministrator,
    SessionClient Client, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset AbsoluteExpiresAtUtc, DateTimeOffset? MfaVerifiedAtUtc = null, MfaState MfaState = MfaState.None, Guid? SelectedCompanyId = null);

public enum CreateUserStatus { Created, InvalidLogin, WeakPassword, InvalidDisplayName, LoginTaken }
public sealed record CreateUserResult(CreateUserStatus Status, UserRecord? User = null, IReadOnlyList<string>? PasswordProblems = null);

public enum ChangePasswordStatus { Changed, InvalidCurrentPassword, WeakPassword, Throttled }
public sealed record ChangePasswordResult(ChangePasswordStatus Status, IReadOnlyList<string>? PasswordProblems = null);

/// <summary>
/// Local authentication: login, session validation, logout, password change and administrative user creation.
/// Log messages carry outcome codes and user ids only, never passwords, tokens, hashes or login strings of unknown accounts.
/// </summary>
public sealed class AuthService(IdentityStore store, PasswordService passwords, AuthOptions options, ILogger<AuthService>? logger = null, MfaLoginServices? mfa = null)
{
    private const int MaxPasswordInput = 1024;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<LoginOutcome> LoginAsync(string? login, string? password, SessionClient client, string? sourceAddress, CancellationToken ct = default)
    {
        password ??= "";
        if (password.Length > MaxPasswordInput) password = password[..MaxPasswordInput];

        var validLogin = LoginName.TryNormalize(login, out var normalized);
        // Throttle keys exist for every attempted login string, real or not, so behaviour is identical (no enumeration).
        // Three independent counters (see docs/DEVELOPMENT.md, "Login abuse protection"):
        //  * pair   (login + client address): small limit; blocks only that pair, so a third party cannot lock the real user out elsewhere;
        //  * source (client address): bounds credential stuffing by one client across all logins;
        //  * global (login, all addresses): high last-resort ceiling against distributed guessing.
        var rawKey = validLogin ? normalized : (login ?? "").Trim().ToLowerInvariant();
        if (rawKey.Length > 128) rawKey = rawKey[..128];
        var source = string.IsNullOrWhiteSpace(sourceAddress) ? SourceAddress.Unknown : sourceAddress;
        var globalKey = SessionToken.HashKey(ThrottleKeyDomains.LoginIdentifier + rawKey);
        var pairKey = SessionToken.HashKey(ThrottleKeyDomains.LoginPair + rawKey + "|" + source);
        var sourceKey = SessionToken.HashKey(ThrottleKeyDomains.LoginSource + source);
        var keys = new[] { (ThrottleScope.Login, pairKey), (ThrottleScope.Login, globalKey), (ThrottleScope.Source, sourceKey) };

        var now = store.UtcNow;
        if (await store.GetBlockedUntilAsync(keys, ct) is { } blockedUntil)
        {
            logger?.LogWarning("auth.login outcome={Outcome}", "throttled");
            return new LoginOutcome.Throttled(Max(blockedUntil - now, TimeSpan.FromSeconds(1)));
        }

        var user = validLogin ? await store.FindUserByLoginAsync(normalized, ct) : null;
        PasswordVerification verification;
        if (user is null)
        {
            passwords.BurnVerification(password); // equalize timing with the existing-user path
            verification = PasswordVerification.Failed;
        }
        else
        {
            verification = passwords.Verify(user.PasswordHash, password);
        }

        if (user is null || verification == PasswordVerification.Failed || user.State != UserState.Active)
        {
            var pairBlocked = await store.RecordFailureAsync(ThrottleScope.Login, pairKey, options.MaxFailuresPerLoginSource, options.FailureWindow, options.Block, ct);
            var globalBlocked = await store.RecordFailureAsync(ThrottleScope.Login, globalKey, options.MaxFailuresPerLoginGlobal, options.FailureWindow, options.Block, ct);
            var sourceBlocked = await store.RecordFailureAsync(ThrottleScope.Source, sourceKey, options.MaxFailuresPerSource, options.FailureWindow, options.Block, ct);
            var reason = user is null ? "unknown_login" : verification == PasswordVerification.Failed ? "bad_password" : "state_" + user.State.ToString().ToLowerInvariant();
            logger?.LogWarning("auth.login outcome=failed reason={Reason} user={UserId} pairBlocked={PairBlocked} sourceBlocked={SourceBlocked}", reason, user?.Id, pairBlocked, sourceBlocked);
            // Operator signal: many clients are guessing the same login at once; the login is now blocked for every client.
            if (globalBlocked) logger?.LogWarning("auth.login_distributed_attack_suspected user={UserId}", user?.Id);
            return new LoginOutcome.InvalidCredentials();
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
            await store.UpdatePasswordHashAsync(user.Id, passwords.Hash(password), passwordChanged: false, ct);

        // A success clears only this client's pair counter. The source and global counters are deliberately kept, so an attacker
        // who owns one valid account cannot launder failures against others, and a distributed guess cannot reset itself.
        await store.ResetThrottleAsync(ThrottleScope.Login, pairKey, ct);

        // Phase 4.1: the password step alone never yields a session for a user who needs the second factor. A service built WITHOUT the second-factor services fails CLOSED (review M1): it refuses a user whose MFA state is not None
        // instead of issuing a password-only session.
        if (mfa is null && await store.GetMfaStateAsync(user.Id, ct) != MfaState.None)
        {
            logger?.LogError("auth.login outcome=mfa_services_missing user={UserId}", user.Id);
            return new LoginOutcome.InvalidCredentials();
        }
        if (mfa is not null)
        {
            switch (await mfa.State.GetStateAsync(user.Id, ct))
            {
                case MfaState.Enrolled:
                    {
                        var c = await mfa.Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, client, source, ct);
                        logger?.LogInformation("auth.login outcome=mfa_required user={UserId} client={Client}", user.Id, client);
                        return new LoginOutcome.MfaRequired(c.Token, c.ExpiresAtUtc);
                    }
                case MfaState.EnrolmentRequired:
                    {
                        var c = await mfa.Challenges.IssueAsync(user.Id, MfaChallengePurpose.Enrol, client, source, ct);
                        logger?.LogInformation("auth.login outcome=mfa_enrolment_required user={UserId} client={Client}", user.Id, client);
                        return new LoginOutcome.MfaEnrolmentRequired(c.Token, c.ExpiresAtUtc);
                    }
                case MfaState.RecoveryRequired:
                    logger?.LogWarning("auth.login outcome=mfa_recovery_required user={UserId}", user.Id);
                    return new LoginOutcome.MfaRecoveryRequired();
            }
        }
        return await CreateSessionAsync(user, client, mfaVerifiedAt: null, ct);
    }

    private async Task<LoginOutcome.Success> CreateSessionAsync(UserRecord user, SessionClient client, DateTimeOffset? mfaVerifiedAt, CancellationToken ct)
    {
        var now = store.UtcNow;
        var token = SessionToken.Generate();
        var sessionId = Guid.NewGuid();
        var absolute = now + options.SessionAbsolute;
        var idle = Min(now + options.SessionIdle, absolute);
        await store.CreateSessionAsync(sessionId, user.Id, SessionToken.Hash(token), client, idle, absolute, mfaVerifiedAt, ct);
        logger?.LogInformation("auth.login outcome=success user={UserId} session={SessionId} client={Client} mfa={Mfa}", user.Id, sessionId, client, mfaVerifiedAt is not null);
        return new LoginOutcome.Success(token, user, sessionId, idle, absolute);
    }

    /// <summary>
    /// Completes a login that needs the second factor (Phase 4.1 section 5): one verification attempt of the challenge (every binding and the attempt budget checked atomically), then the code through the single
    /// <see cref="MfaVerifier"/> (window 1, replay compare-and-set, failure throttle), then the single-use consumption of the challenge, and only then the session, recorded as password+totp. Every refusal is the same
    /// <see cref="LoginOutcome.InvalidCredentials"/> (no oracle); a throttled user gets <see cref="LoginOutcome.Throttled"/>. With no MFA services configured nothing can complete.
    /// </summary>
    public async Task<LoginOutcome> CompleteMfaLoginAsync(string? challengeToken, string? code, SessionClient client, string? sourceAddress, CancellationToken ct = default)
    {
        if (mfa is null) return new LoginOutcome.InvalidCredentials();
        var attempt = await mfa.Challenges.TryBeginAttemptAsync(challengeToken, MfaChallengePurpose.Login, client, sourceAddress, ct);
        if (attempt is null) return new LoginOutcome.InvalidCredentials();
        var result = await mfa.Verifier.VerifyAsync(attempt.UserId, code, sourceAddress, ct);
        switch (result)
        {
            case MfaResult.Throttled t:
                logger?.LogWarning("auth.mfa outcome=throttled user={UserId}", attempt.UserId);
                return new LoginOutcome.Throttled(t.RetryAfter);
            case MfaResult.Verified:
                break;
            case MfaResult.Invalid inv:
                logger?.LogWarning("auth.mfa outcome=failed user={UserId} lockout={Lockout}", attempt.UserId, inv.UserLockoutReached);
                return new LoginOutcome.InvalidCredentials();
            default:
                logger?.LogError("auth.mfa outcome=unavailable user={UserId}", attempt.UserId);   // a missing key fails closed: no session, no downgrade
                return new LoginOutcome.InvalidCredentials();
        }
        if (!await mfa.Challenges.ConsumeAsync(attempt.Id, ct)) return new LoginOutcome.InvalidCredentials();      // a concurrent completion won
        var user = await store.FindUserByIdAsync(attempt.UserId, ct);
        if (user is null || user.State != UserState.Active) return new LoginOutcome.InvalidCredentials();
        return await CreateSessionAsync(user, client, store.UtcNow, ct);
    }

    // ---- Step-up (Phase 4.1 S3a; D-160) ----

    /// <summary>
    /// Re-proves the second factor on a live session: the code goes through the single <see cref="MfaVerifier"/> (same window, replay protection and throttling as login),
    /// and on success the session's proof time moves to now in one transaction with its audit event. A user whose MFA state is None is not asked (NotApplicable).
    /// </summary>
    public async Task<StepUpOutcome> StepUpAsync(AuthenticatedSession session, string? code, string? sourceAddress, CancellationToken ct = default)
    {
        if (session.MfaState == MfaState.None) return new StepUpOutcome.NotApplicable();
        if (mfa is null) return new StepUpOutcome.Refused();
        var result = await mfa.Verifier.VerifyAsync(session.UserId, code, sourceAddress, ct);
        switch (result)
        {
            case MfaResult.Throttled t:
                return new StepUpOutcome.Throttled(t.RetryAfter);
            case MfaResult.Verified:
                var at = await mfa.State.RecordStepUpAsync(session.SessionId, session.UserId, ct);
                if (at is null) return new StepUpOutcome.Refused();                           // the session ended meanwhile
                logger?.LogInformation("auth.mfa_step_up outcome=verified user={UserId} session={SessionId}", session.UserId, session.SessionId);
                return new StepUpOutcome.Verified(at.Value);
            case MfaResult.Invalid inv:
                await mfa.State.RecordStepUpFailureAsync(session.SessionId, session.UserId, "invalid", ct);
                logger?.LogWarning("auth.mfa_step_up outcome=failed user={UserId} lockout={Lockout}", session.UserId, inv.UserLockoutReached);
                return new StepUpOutcome.Refused();
            default:                                                                          // not audited: an unavailable key is an operator condition and an authenticated caller must not be able to append audit rows at will
                logger?.LogError("auth.mfa_step_up outcome=unavailable user={UserId}", session.UserId);
                return new StepUpOutcome.Refused();
        }
    }

    // ---- Enrolment (Phase 4.1 section 4; D-142) ----

    private static (byte[] Pair, byte[] Global, byte[] Source) LoginKeys(string normalizedLogin, string source) =>
        (SessionToken.HashKey(ThrottleKeyDomains.LoginPair + normalizedLogin + "|" + source), SessionToken.HashKey(ThrottleKeyDomains.LoginIdentifier + normalizedLogin), SessionToken.HashKey(ThrottleKeyDomains.LoginSource + source));

    private async Task RecordLoginFailureAsync(byte[] pairKey, byte[] globalKey, byte[] sourceKey, CancellationToken ct)
    {
        await store.RecordFailureAsync(ThrottleScope.Login, pairKey, options.MaxFailuresPerLoginSource, options.FailureWindow, options.Block, ct);
        await store.RecordFailureAsync(ThrottleScope.Login, globalKey, options.MaxFailuresPerLoginGlobal, options.FailureWindow, options.Block, ct);
        await store.RecordFailureAsync(ThrottleScope.Source, sourceKey, options.MaxFailuresPerSource, options.FailureWindow, options.Block, ct);
    }

    private const string Issuer = "HASM";

    private async Task<MfaEnrolOutcome> StartEnrolmentAsync(UserRecord user, CancellationToken ct)
    {
        var secret = Totp.GenerateSecret();
        try
        {
            await mfa!.State.CreatePendingAsync(user.Id, secret, null, ct);
            return new MfaEnrolOutcome.Started(Totp.ProvisioningUri(Issuer, user.Login, secret), Totp.ToBase32(secret));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>
    /// Begins an enrolment that came from the password step (an <c>enrol</c> challenge): the user is EnrolmentRequired and presents the one-time enrolment code that was given to them out of band. With no stored code (anyone who is not a
    /// bootstrap administrator before S4/S5) nothing can start: the user stays without a session (fail closed, D-142 b). Wrong codes count against the second-factor throttle.
    /// </summary>
    public async Task<MfaEnrolOutcome> BeginEnrolmentWithChallengeAsync(string? challengeToken, string? enrolmentCode, SessionClient client, string? source, CancellationToken ct = default)
    {
        if (mfa is null) return new MfaEnrolOutcome.Refused();
        var attempt = await mfa.Challenges.TryBeginAttemptAsync(challengeToken, MfaChallengePurpose.Enrol, client, source, ct);
        if (attempt is null) return new MfaEnrolOutcome.Refused();
        var src = string.IsNullOrWhiteSpace(source) ? SourceAddress.Unknown : source;
        if (await mfa.Limiter.BlockedForAsync(attempt.UserId, src, ct) is { } retry) return new MfaEnrolOutcome.Throttled(retry);
        var user = await store.FindUserByIdAsync(attempt.UserId, ct);
        if (user is null || user.State != UserState.Active || await mfa.State.GetStateAsync(user.Id, ct) != MfaState.EnrolmentRequired) return new MfaEnrolOutcome.Refused();
        if (!await mfa.State.CheckEnrolmentCodeAsync(user.Id, enrolmentCode, ct))
        {
            await mfa.Limiter.RecordFailureAsync(user.Id, src, ct);
            return new MfaEnrolOutcome.Refused();
        }
        return await StartEnrolmentAsync(user, ct);
    }

    /// <summary>
    /// Begins a VOLUNTARY enrolment of a user who has a session and no MFA yet (D-142 c). Because a hijacked session must not be able to bind an attacker's authenticator, the request re-confirms the PASSWORD, checked through the login
    /// throttle path: a blocked login is throttled, and a wrong password counts as a login failure. A user who is Enrolled (a change of device needs step-up, slice S3), EnrolmentRequired or RecoveryRequired cannot use this path.
    /// </summary>
    public async Task<MfaEnrolOutcome> BeginEnrolmentWithSessionAsync(AuthenticatedSession session, string? password, string? sourceAddress, CancellationToken ct = default)
    {
        if (mfa is null) return new MfaEnrolOutcome.Refused();
        var user = await store.FindUserByIdAsync(session.UserId, ct);
        if (user is null || user.State != UserState.Active || await mfa.State.GetStateAsync(user.Id, ct) != MfaState.None) return new MfaEnrolOutcome.Refused();
        password ??= "";
        if (password.Length > MaxPasswordInput) password = password[..MaxPasswordInput];
        var source = string.IsNullOrWhiteSpace(sourceAddress) ? SourceAddress.Unknown : sourceAddress;
        var (pairKey, globalKey, sourceKey) = LoginKeys(user.Login, source);
        if (await store.GetBlockedUntilAsync([(ThrottleScope.Login, pairKey), (ThrottleScope.Login, globalKey), (ThrottleScope.Source, sourceKey)], ct) is { } blockedUntil)
            return new MfaEnrolOutcome.Throttled(Max(blockedUntil - store.UtcNow, TimeSpan.FromSeconds(1)));
        if (password.Length == 0 || passwords.Verify(user.PasswordHash, password) == PasswordVerification.Failed)
        {
            await RecordLoginFailureAsync(pairKey, globalKey, sourceKey, ct);                                    // counts as a login failure
            logger?.LogWarning("auth.mfa_enrol outcome=bad_password user={UserId}", user.Id);
            return new MfaEnrolOutcome.Refused();
        }
        return await StartEnrolmentAsync(user, ct);
    }

    /// <summary>
    /// Confirms an enrolment that came from a challenge. The code is verified against the PENDING secret through the single <see cref="MfaVerifier"/>; the challenge is consumed FIRST (single use, review M3); then ONE transaction activates the
    /// secret, spends the one-time enrolment code, revokes every session of the user and creates the new session (password + totp). A failure inside the transaction leaves nothing half done.
    /// </summary>
    public async Task<MfaEnrolOutcome> ConfirmEnrolmentWithChallengeAsync(string? challengeToken, string? code, SessionClient client, string? source, CancellationToken ct = default)
    {
        if (mfa is null) return new MfaEnrolOutcome.Refused();
        var attempt = await mfa.Challenges.TryBeginAttemptAsync(challengeToken, MfaChallengePurpose.Enrol, client, source, ct);
        if (attempt is null) return new MfaEnrolOutcome.Refused();
        var user = await store.FindUserByIdAsync(attempt.UserId, ct);
        if (user is null || user.State != UserState.Active) return new MfaEnrolOutcome.Refused();
        var verified = await VerifyPendingAsync(user.Id, code, source, ct);
        if (verified.Refusal is { } refusal) return refusal;
        if (!await mfa.Challenges.ConsumeAsync(attempt.Id, ct)) return new MfaEnrolOutcome.Refused();
        return await ActivateAndIssueAsync(user, verified.SecretId, verified.Step, client, consumeEnrolmentCode: true, ct);
    }

    /// <summary>
    /// Confirms a voluntary enrolment in a live session (D-142 d): ONE transaction activates the secret, revokes EVERY session of the user, the confirming one included, and creates a NEW session verified now (password + totp), returned like a login
    /// so the Web gets a new cookie and the Desktop a new bearer token. The old token no longer works.
    /// </summary>
    public async Task<MfaEnrolOutcome> ConfirmEnrolmentWithSessionAsync(AuthenticatedSession session, string? code, string? source, CancellationToken ct = default)
    {
        if (mfa is null) return new MfaEnrolOutcome.Refused();
        if (await mfa.State.GetStateAsync(session.UserId, ct) != MfaState.None) return new MfaEnrolOutcome.Refused();
        var user = await store.FindUserByIdAsync(session.UserId, ct);
        if (user is null || user.State != UserState.Active) return new MfaEnrolOutcome.Refused();
        var verified = await VerifyPendingAsync(user.Id, code, source, ct);
        if (verified.Refusal is { } refusal) return refusal;
        return await ActivateAndIssueAsync(user, verified.SecretId, verified.Step, session.Client, consumeEnrolmentCode: false, ct);
    }

    private async Task<MfaEnrolOutcome> ActivateAndIssueAsync(UserRecord user, Guid secretId, long step, SessionClient client, bool consumeEnrolmentCode, CancellationToken ct)
    {
        var now = store.UtcNow;
        var token = SessionToken.Generate();
        var sessionId = Guid.NewGuid();
        var absolute = now + options.SessionAbsolute;
        var idle = Min(now + options.SessionIdle, absolute);
        var issue = new SessionIssue(sessionId, SessionToken.Hash(token), client, idle, absolute, now);
        if (!await mfa!.State.ActivatePendingAsync(user.Id, secretId, step, null, ct, consumeEnrolmentCode, issue)) return new MfaEnrolOutcome.Refused();
        logger?.LogInformation("auth.mfa_enrol outcome=confirmed user={UserId} session={SessionId} client={Client}", user.Id, sessionId, client);
        return new MfaEnrolOutcome.Confirmed(new LoginOutcome.Success(token, user, sessionId, idle, absolute));
    }

    private readonly record struct PendingVerification(MfaEnrolOutcome? Refusal, Guid SecretId, long Step);

    /// <summary>Verifies the code against the user's pending secret (the shared first half of both confirmations). A refusal is the uniform one; nothing is changed except the replay and throttle counters.</summary>
    private async Task<PendingVerification> VerifyPendingAsync(Guid userId, string? code, string? source, CancellationToken ct)
    {
        MfaSecret? pending;
        try { pending = await mfa!.State.FindPendingSecretAsync(userId, ct); }
        catch (MfaUnavailableException) { return new PendingVerification(new MfaEnrolOutcome.Refused(), default, 0); }
        if (pending is null) return new PendingVerification(new MfaEnrolOutcome.Refused(), default, 0);
        var secretId = pending.SecretId;
        pending.Dispose();
        return await mfa.Verifier.VerifyPendingAsync(userId, code, source, ct) switch
        {
            MfaResult.Throttled t => new PendingVerification(new MfaEnrolOutcome.Throttled(t.RetryAfter), default, 0),
            MfaResult.Verified v => new PendingVerification(null, secretId, v.Step),
            _ => new PendingVerification(new MfaEnrolOutcome.Refused(), default, 0),
        };
    }

    /// <summary>Validates a presented token; null when malformed, unknown, revoked, expired or the user is not Active.</summary>
    public async Task<AuthenticatedSession?> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (!SessionToken.IsWellFormed(token)) return null;
        var s = await store.FindActiveSessionAsync(SessionToken.Hash(token!), ct);
        if (s is null) return null;
        // defence in depth (review M1): a session of a user who is not MFA-free but lacks the second-factor verification (for example a password-only session from before enforcement) is not a session
        if (s.MfaState != MfaState.None && s.MfaVerifiedAtUtc is null) return null;

        var now = store.UtcNow;
        var idle = s.IdleExpiresAtUtc;
        if (now - s.LastSeenAtUtc >= TouchInterval)
        {
            idle = Min(now + options.SessionIdle, s.AbsoluteExpiresAtUtc);
            await store.TouchSessionAsync(s.SessionId, idle, ct);
        }
        return new AuthenticatedSession(s.SessionId, s.UserId, s.Login, s.DisplayName, s.IsIdentityAdministrator, s.Client, idle, s.AbsoluteExpiresAtUtc, s.MfaVerifiedAtUtc, s.MfaState, s.SelectedCompanyId);
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken ct = default)
    {
        await store.RevokeSessionAsync(sessionId, SessionRevocation.Logout, ct);
        logger?.LogInformation("auth.logout session={SessionId}", sessionId);
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(AuthenticatedSession session, string? current, string? replacement, CancellationToken ct = default)
    {
        var user = await store.FindUserByIdAsync(session.UserId, ct);
        if (user is null || user.State != UserState.Active) return new ChangePasswordResult(ChangePasswordStatus.InvalidCurrentPassword);

        // A stolen session must not be able to brute-force the current password. Its counter is per user and separate from the
        // sign-in counters, so failed sign-in attempts by others can never block a legitimate password change.
        var key = SessionToken.HashKey(ThrottleKeyDomains.ChangePassword + user.Id.ToString("N"));
        if (await store.GetBlockedUntilAsync([(ThrottleScope.Login, key)], ct) is not null)
            return new ChangePasswordResult(ChangePasswordStatus.Throttled);

        var ok = current is { Length: > 0 and <= MaxPasswordInput } && passwords.Verify(user.PasswordHash, current) != PasswordVerification.Failed;
        if (!ok)
        {
            await store.RecordFailureAsync(ThrottleScope.Login, key, options.MaxFailuresPerLoginSource, options.FailureWindow, options.Block, ct);
            logger?.LogWarning("auth.change_password outcome=bad_current user={UserId}", user.Id);
            return new ChangePasswordResult(ChangePasswordStatus.InvalidCurrentPassword);
        }

        var problems = PasswordPolicy.Validate(replacement, user.Login);
        if (problems.Count > 0) return new ChangePasswordResult(ChangePasswordStatus.WeakPassword, problems);

        await store.UpdatePasswordHashAsync(user.Id, passwords.Hash(replacement!), passwordChanged: true, ct);
        await store.RevokeUserSessionsAsync(user.Id, SessionRevocation.PasswordChanged, exceptSessionId: session.SessionId, ct);
        logger?.LogInformation("auth.change_password outcome=changed user={UserId}", user.Id);
        return new ChangePasswordResult(ChangePasswordStatus.Changed);
    }

    /// <summary>Administrative creation of a user (there is no self-registration). The caller must already be authorized.</summary>
    public async Task<CreateUserResult> CreateUserAsync(string? login, string? displayName, string? password, bool isIdentityAdministrator, CancellationToken ct = default, Guid? actorUserId = null)
    {
        var (precheck, normalized, name) = Precheck(login, displayName, password);
        if (precheck is not null) return precheck;
        try
        {
            var user = await store.CreateUserAsync(normalized, name, passwords.Hash(password!), isIdentityAdministrator, actorUserId, ct);
            logger?.LogInformation("auth.user_created user={UserId} administrator={Admin}", user.Id, isIdentityAdministrator);
            return new CreateUserResult(CreateUserStatus.Created, user);
        }
        catch (DuplicateLoginException)
        {
            return new CreateUserResult(CreateUserStatus.LoginTaken);
        }
    }

    /// <summary>
    /// Initial administrator bootstrap (administrative tool only). Returns LoginTaken-free <see cref="CreateUserStatus.Created"/>,
    /// or null when an identity administrator already exists.
    /// </summary>
    public async Task<CreateUserResult?> BootstrapAdministratorAsync(string? login, string? displayName, string? password, CancellationToken ct = default)
    {
        var (precheck, normalized, name) = Precheck(login, displayName, password);
        if (precheck is not null) return precheck;
        try
        {
            var user = await store.CreateInitialAdministratorAsync(normalized, name, passwords.Hash(password!), ct);
            if (user is null) return null;
            logger?.LogInformation("auth.bootstrap user={UserId}", user.Id);
            return new CreateUserResult(CreateUserStatus.Created, user);
        }
        catch (DuplicateLoginException)
        {
            return new CreateUserResult(CreateUserStatus.LoginTaken);
        }
    }

    public enum SetStateStatus { Changed, NotFound, OwnAccount, LastCompanyAdministrator, LastIdentityAdministrator, ActorNotActive }

    /// <summary>
    /// Administrative lifecycle change. An administrator cannot change their own state, and no change may leave a company without a usable
    /// Company Administrator or the system without an Active identity administrator (see <see cref="IdentityStore.SetStateAsync"/>).
    /// </summary>
    public async Task<SetStateStatus> SetUserStateAsync(Guid actorUserId, Guid targetUserId, UserState state, CancellationToken ct = default)
    {
        if (actorUserId == targetUserId) return SetStateStatus.OwnAccount;
        var outcome = await store.SetStateAsync(actorUserId, targetUserId, state, ct);
        if (outcome != SetStateOutcome.Changed)
        {
            logger?.LogWarning("auth.user_state outcome={Outcome} actor={Actor} user={UserId} state={State}", outcome, actorUserId, targetUserId, state);
            return outcome switch
            {
                SetStateOutcome.LastCompanyAdministrator => SetStateStatus.LastCompanyAdministrator,
                SetStateOutcome.LastIdentityAdministrator => SetStateStatus.LastIdentityAdministrator,
                SetStateOutcome.ActorNotActive => SetStateStatus.ActorNotActive,
                _ => SetStateStatus.NotFound,
            };
        }
        logger?.LogInformation("auth.user_state actor={Actor} user={UserId} state={State}", actorUserId, targetUserId, state);
        return SetStateStatus.Changed;
    }

    private static (CreateUserResult? Failure, string Login, string DisplayName) Precheck(string? login, string? displayName, string? password)
    {
        if (!LoginName.TryNormalize(login, out var normalized)) return (new CreateUserResult(CreateUserStatus.InvalidLogin), "", "");
        var name = (displayName ?? "").Trim();
        if (name.Length is 0 or > 100 || name.Any(char.IsControl)) return (new CreateUserResult(CreateUserStatus.InvalidDisplayName), "", "");
        var problems = PasswordPolicy.Validate(password, normalized);
        if (problems.Count > 0) return (new CreateUserResult(CreateUserStatus.WeakPassword, null, problems), "", "");
        return (null, normalized, name);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
