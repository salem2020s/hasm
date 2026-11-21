namespace Hasm.Infrastructure.Identity;

/// <summary>The failure-throttle storage the login step already uses (implemented by <see cref="IdentityStore"/>), as a seam so the second-factor limiter can be tested without a database.</summary>
public interface IThrottleStore
{
    Task<DateTimeOffset?> GetBlockedUntilAsync(IReadOnlyList<(ThrottleScope Scope, byte[] KeyHash)> keys, CancellationToken ct = default);
    Task<bool> RecordFailureAsync(ThrottleScope scope, byte[] keyHash, int limit, TimeSpan window, TimeSpan block, CancellationToken ct = default);
    Task ResetThrottleAsync(ThrottleScope scope, byte[] keyHash, CancellationToken ct = default);
}

/// <summary>
/// The second-factor failure limiter over <see cref="ThrottleScope.Mfa"/> (Phase 4.1, review L4): a small bound per (user, source) and a larger bound per user across sources, same window and block as the password step,
/// time-based blocks only. A success clears the (user, source) counter and keeps the per-user counter. The newly reached per-user bound is reported so the caller can raise the lockout alert.
/// </summary>
public sealed class MfaAttemptLimiter(IThrottleStore store, TimeProvider clock, MfaThrottleOptions? options = null) : IMfaAttemptLimiter
{
    private readonly MfaThrottleOptions o = options ?? new MfaThrottleOptions();
    private TimeSpan Window => TimeSpan.FromMinutes(o.WindowMinutes);
    private TimeSpan Block => TimeSpan.FromMinutes(o.BlockMinutes);

    public async Task<TimeSpan?> BlockedForAsync(Guid userId, string source, CancellationToken ct)
    {
        var until = await store.GetBlockedUntilAsync([(ThrottleScope.Mfa, MfaThrottleKeys.UserAndSource(userId, source)), (ThrottleScope.Mfa, MfaThrottleKeys.User(userId))], ct);
        if (until is not { } u) return null;
        var left = u - clock.GetUtcNow();
        return left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
    }

    public async Task<bool> RecordFailureAsync(Guid userId, string source, CancellationToken ct)
    {
        var userKey = MfaThrottleKeys.User(userId);
        var wasUserBlocked = await store.GetBlockedUntilAsync([(ThrottleScope.Mfa, userKey)], ct) is not null;
        await store.RecordFailureAsync(ThrottleScope.Mfa, MfaThrottleKeys.UserAndSource(userId, source), o.MaxFailuresPerUserSource, Window, Block, ct);
        var userBlocked = await store.RecordFailureAsync(ThrottleScope.Mfa, userKey, o.MaxFailuresPerUser, Window, Block, ct);
        return userBlocked && !wasUserBlocked;
    }

    public Task ResetPairAsync(Guid userId, string source, CancellationToken ct) => store.ResetThrottleAsync(ThrottleScope.Mfa, MfaThrottleKeys.UserAndSource(userId, source), ct);
}
