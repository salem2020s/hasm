using Microsoft.Extensions.Logging;

namespace Hasm.Infrastructure.Identity;

public abstract record LoginOutcome
{
    /// <summary>Any failure that must look identical to the caller (unknown login, wrong password, disabled, locked).</summary>
    public sealed record InvalidCredentials : LoginOutcome;
    public sealed record Throttled(TimeSpan RetryAfter) : LoginOutcome;
    public sealed record Success(string Token, UserRecord User, Guid SessionId, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset AbsoluteExpiresAtUtc) : LoginOutcome;
}

public sealed record AuthenticatedSession(
    Guid SessionId, Guid UserId, string Login, string DisplayName, bool IsIdentityAdministrator,
    SessionClient Client, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset AbsoluteExpiresAtUtc);

public enum CreateUserStatus { Created, InvalidLogin, WeakPassword, InvalidDisplayName, LoginTaken }
public sealed record CreateUserResult(CreateUserStatus Status, UserRecord? User = null, IReadOnlyList<string>? PasswordProblems = null);

public enum ChangePasswordStatus { Changed, InvalidCurrentPassword, WeakPassword, Throttled }
public sealed record ChangePasswordResult(ChangePasswordStatus Status, IReadOnlyList<string>? PasswordProblems = null);

/// <summary>
/// Local authentication: login, session validation, logout, password change and administrative user creation.
/// Log messages carry outcome codes and user ids only, never passwords, tokens, hashes or login strings of unknown accounts.
/// </summary>
public sealed class AuthService(IdentityStore store, PasswordService passwords, AuthOptions options, ILogger<AuthService>? logger = null)
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

        var token = SessionToken.Generate();
        var sessionId = Guid.NewGuid();
        var absolute = now + options.SessionAbsolute;
        var idle = Min(now + options.SessionIdle, absolute);
        await store.CreateSessionAsync(sessionId, user.Id, SessionToken.Hash(token), client, idle, absolute, ct);
        logger?.LogInformation("auth.login outcome=success user={UserId} session={SessionId} client={Client}", user.Id, sessionId, client);
        return new LoginOutcome.Success(token, user, sessionId, idle, absolute);
    }

    /// <summary>Validates a presented token; null when malformed, unknown, revoked, expired or the user is not Active.</summary>
    public async Task<AuthenticatedSession?> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (!SessionToken.IsWellFormed(token)) return null;
        var s = await store.FindActiveSessionAsync(SessionToken.Hash(token!), ct);
        if (s is null) return null;

        var now = store.UtcNow;
        var idle = s.IdleExpiresAtUtc;
        if (now - s.LastSeenAtUtc >= TouchInterval)
        {
            idle = Min(now + options.SessionIdle, s.AbsoluteExpiresAtUtc);
            await store.TouchSessionAsync(s.SessionId, idle, ct);
        }
        return new AuthenticatedSession(s.SessionId, s.UserId, s.Login, s.DisplayName, s.IsIdentityAdministrator, s.Client, idle, s.AbsoluteExpiresAtUtc);
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
