using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1: the second-factor limiter over a fake throttle store with the same window/block semantics as the real one.</summary>
public class MfaAttemptLimiterTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class FakeStore(Clock clock) : IThrottleStore
    {
        private readonly Dictionary<string, (int Failures, DateTimeOffset WindowStart, DateTimeOffset? BlockedUntil)> rows = [];
        public List<ThrottleScope> Scopes = [];
        private static string K(ThrottleScope s, byte[] k) => (short)s + ":" + Convert.ToHexString(k);
        public Task<DateTimeOffset?> GetBlockedUntilAsync(IReadOnlyList<(ThrottleScope Scope, byte[] KeyHash)> keys, CancellationToken ct = default)
        {
            DateTimeOffset? latest = null;
            foreach (var (s, k) in keys) if (rows.TryGetValue(K(s, k), out var r) && r.BlockedUntil is { } b && b > clock.Now && (latest is null || b > latest)) latest = b;
            return Task.FromResult(latest);
        }
        public Task<bool> RecordFailureAsync(ThrottleScope scope, byte[] keyHash, int limit, TimeSpan window, TimeSpan block, CancellationToken ct = default)
        {
            Scopes.Add(scope);
            var key = K(scope, keyHash);
            rows.TryGetValue(key, out var r);
            var fresh = r.Failures == 0 || r.WindowStart + window <= clock.Now;
            var failures = fresh ? 1 : r.Failures + 1;
            var start = fresh ? clock.Now : r.WindowStart;
            var until = failures >= limit ? clock.Now + block : r.BlockedUntil;
            rows[key] = (failures, start, until);
            return Task.FromResult(until is { } u && u > clock.Now);
        }
        public Task ResetThrottleAsync(ThrottleScope scope, byte[] keyHash, CancellationToken ct = default) { rows.Remove(K(scope, keyHash)); return Task.CompletedTask; }
    }

    private static readonly Guid User = Guid.NewGuid();
    private static (MfaAttemptLimiter L, FakeStore S, Clock C) Make()
    {
        var c = new Clock(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero)); var s = new FakeStore(c);
        return (new MfaAttemptLimiter(s, c), s, c);
    }

    [Fact]
    public async Task Five_failures_from_one_source_block_that_source_only()
    {
        var (l, _, _) = Make();
        for (var i = 0; i < 4; i++) { await l.RecordFailureAsync(User, "a", default); Assert.Null(await l.BlockedForAsync(User, "a", default)); }
        await l.RecordFailureAsync(User, "a", default);
        Assert.True(await l.BlockedForAsync(User, "a", default) is { } left && left > TimeSpan.FromMinutes(14));
        Assert.Null(await l.BlockedForAsync(User, "b", default));                                           // another source is not locked out by it
    }

    [Fact]
    public async Task The_block_is_time_based_and_ends()
    {
        var (l, _, c) = Make();
        for (var i = 0; i < 5; i++) await l.RecordFailureAsync(User, "a", default);
        c.Now = c.Now.AddMinutes(16);
        Assert.Null(await l.BlockedForAsync(User, "a", default));
    }

    [Fact]
    public async Task A_hundred_failures_across_sources_block_the_user_everywhere_and_report_the_lockout_once()
    {
        var (l, _, _) = Make();
        var reached = new List<int>();
        for (var i = 0; i < 100; i++) if (await l.RecordFailureAsync(User, "s" + i, default)) reached.Add(i);   // a fresh source each time: the pair bound never trips
        Assert.Equal([99], reached);                                                                          // reported exactly when the per-user bound is newly reached
        Assert.NotNull(await l.BlockedForAsync(User, "brand-new-source", default));
        Assert.False(await l.RecordFailureAsync(User, "x", default));                                         // further failures while blocked do not re-alert
    }

    [Fact]
    public async Task A_success_clears_only_the_pair_counter_and_keeps_the_per_user_count()
    {
        var (l, _, _) = Make();
        for (var i = 0; i < 4; i++) await l.RecordFailureAsync(User, "a", default);
        await l.ResetPairAsync(User, "a", default);
        for (var i = 0; i < 4; i++) await l.RecordFailureAsync(User, "a", default);                          // the pair counter restarted: still below 5
        Assert.Null(await l.BlockedForAsync(User, "a", default));
        await l.RecordFailureAsync(User, "a", default);                                                       // fifth since the reset
        Assert.NotNull(await l.BlockedForAsync(User, "a", default));
    }

    [Fact]
    public async Task All_counters_use_the_login_scope_with_mfa_domain_keys_and_other_users_are_independent()
    {
        var (l, s, _) = Make();
        for (var i = 0; i < 5; i++) await l.RecordFailureAsync(User, "a", default);
        Assert.All(s.Scopes, sc => Assert.Equal(ThrottleScope.Login, sc));
        Assert.Null(await l.BlockedForAsync(Guid.NewGuid(), "a", default));
    }
}
