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
        var rawKey = validLogin ? normalized : (login ?? "").Trim().ToLowerInvariant();
        if (rawKey.Length > 128) rawKey = rawKey[..128];
        var loginKey = SessionToken.HashKey("login:" + rawKey);
        var sourceKey = SessionToken.HashKey("source:" + (string.IsNullOrWhiteSpace(sourceAddress) ? "unknown" : sourceAddress));
        var keys = new[] { (ThrottleScope.Login, loginKey), (ThrottleScope.Source, sourceKey) };

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
            var loginBlocked = await store.RecordFailureAsync(ThrottleScope.Login, loginKey, options.MaxFailuresPerLogin, options.FailureWindow, options.Block, ct);
            await store.RecordFailureAsync(ThrottleScope.Source, sourceKey, options.MaxFailuresPerSource, options.FailureWindow, options.Block, ct);
            var reason = user is null ? "unknown_login" : verification == PasswordVerification.Failed ? "bad_password" : "state_" + user.State.ToString().ToLowerInvariant();
            logger?.LogWarning("auth.login outcome=failed reason={Reason} user={UserId} nowBlocked={Blocked}", reason, user?.Id, loginBlocked);
            return new LoginOutcome.InvalidCredentials();
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
            await store.UpdatePasswordHashAsync(user.Id, passwords.Hash(password), passwordChanged: false, ct);

        await store.ResetThrottleAsync(ThrottleScope.Login, loginKey, ct); // the source counter is deliberately not reset by a success

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

        // A stolen session must not be able to brute-force the current password: share the login throttle.
        var key = SessionToken.HashKey("login:" + user.Login);
        if (await store.GetBlockedUntilAsync([(ThrottleScope.Login, key)], ct) is not null)
            return new ChangePasswordResult(ChangePasswordStatus.Throttled);

        var ok = current is { Length: > 0 and <= MaxPasswordInput } && passwords.Verify(user.PasswordHash, current) != PasswordVerification.Failed;
        if (!ok)
        {
            await store.RecordFailureAsync(ThrottleScope.Login, key, options.MaxFailuresPerLogin, options.FailureWindow, options.Block, ct);
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
    public async Task<CreateUserResult> CreateUserAsync(string? login, string? displayName, string? password, bool isIdentityAdministrator, CancellationToken ct = default)
    {
        var (precheck, normalized, name) = Precheck(login, displayName, password);
        if (precheck is not null) return precheck;
        try
        {
            var user = await store.CreateUserAsync(normalized, name, passwords.Hash(password!), isIdentityAdministrator, ct);
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

    public enum SetStateStatus { Changed, NotFound, OwnAccount }

    /// <summary>Administrative lifecycle change. An administrator cannot change their own state (no lock-out of the last administrator).</summary>
    public async Task<SetStateStatus> SetUserStateAsync(Guid actorUserId, Guid targetUserId, UserState state, CancellationToken ct = default)
    {
        if (actorUserId == targetUserId) return SetStateStatus.OwnAccount;
        if (!await store.SetStateAsync(targetUserId, state, ct)) return SetStateStatus.NotFound;
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
