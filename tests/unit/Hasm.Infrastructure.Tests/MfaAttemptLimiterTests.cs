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

    // ---- D-121: the second-factor counters share the login throttle table but are separate in every way ----

    private static byte[] LoginPairKey(string login, string source) => SessionToken.HashKey(ThrottleKeyDomains.LoginPair + login + "|" + source);
    private static byte[] LoginGlobalKey(string login) => SessionToken.HashKey(ThrottleKeyDomains.LoginIdentifier + login);
    private static byte[] LoginSourceKey(string source) => SessionToken.HashKey(ThrottleKeyDomains.LoginSource + source);
    private static readonly TimeSpan Win = TimeSpan.FromMinutes(15);

    [Fact]
    public void The_hash_domains_are_prefix_free_so_no_two_domains_can_ever_produce_the_same_key_input()
    {
        var all = ThrottleKeyDomains.All;
        Assert.Equal(all.Count, all.Distinct().Count());
        foreach (var a in all)
            foreach (var b in all.Where(b => b != a))
                Assert.False(a.StartsWith(b, StringComparison.Ordinal), $"{a} starts with {b}");                // prefix-free: a+x can never equal b+y for any x and y
    }

    [Fact]
    public void Mfa_keys_never_equal_any_login_source_or_change_password_key_even_for_adversarial_content()
    {
        var u = Guid.NewGuid();
        var mfa = new[] { MfaThrottleKeys.User(u), MfaThrottleKeys.UserAndSource(u, "a") };
        var hostile = new[] { u.ToString("N"), "mfa-pair:" + u.ToString("N") + "|a", "mfa-user:" + u.ToString("N"), "|a", "", "x" };
        foreach (var text in hostile)
        {
            var others = new[] { LoginGlobalKey(text), LoginPairKey(text, "a"), LoginSourceKey(text), SessionToken.HashKey(ThrottleKeyDomains.ChangePassword + text) };
            foreach (var m in mfa) foreach (var o in others) Assert.NotEqual(Convert.ToHexString(o), Convert.ToHexString(m));
        }
        Assert.NotEqual(Convert.ToHexString(mfa[0]), Convert.ToHexString(mfa[1]));
    }

    [Fact]
    public async Task Mfa_failures_never_count_toward_or_reset_the_password_login_counters()
    {
        var (l, s, _) = Make();
        var login = "alice";
        for (var i = 0; i < 4; i++) await s.RecordFailureAsync(ThrottleScope.Login, LoginPairKey(login, "a"), 5, Win, Win);                    // four password failures
        for (var i = 0; i < 100; i++) await l.RecordFailureAsync(User, "a", default);                                                           // the user's MFA is locked out
        await l.ResetPairAsync(User, "a", default);                                                                                             // and one MFA pair counter was cleared
        Assert.Null(await s.GetBlockedUntilAsync([(ThrottleScope.Login, LoginPairKey(login, "a")), (ThrottleScope.Login, LoginGlobalKey(login)), (ThrottleScope.Source, LoginSourceKey("a"))]));   // the login keys are untouched
        Assert.True(await s.RecordFailureAsync(ThrottleScope.Login, LoginPairKey(login, "a"), 5, Win, Win));                                   // the FIFTH password failure still blocks: MFA neither raised nor reset the count
    }

    [Fact]
    public async Task Password_login_failures_and_resets_never_count_toward_or_reset_the_mfa_counters()
    {
        var (l, s, _) = Make();
        var login = "alice";
        for (var i = 0; i < 4; i++) await l.RecordFailureAsync(User, "a", default);                                                             // four MFA failures
        for (var i = 0; i < 100; i++)
        {
            await s.RecordFailureAsync(ThrottleScope.Login, LoginPairKey(login, "a"), 5, Win, Win);
            await s.RecordFailureAsync(ThrottleScope.Login, LoginGlobalKey(login), 100, Win, Win);
            await s.RecordFailureAsync(ThrottleScope.Source, LoginSourceKey("a"), 20, Win, Win);
        }
        await s.ResetThrottleAsync(ThrottleScope.Login, LoginPairKey(login, "a"));                                                              // a successful login clears its pair counter
        Assert.Null(await l.BlockedForAsync(User, "a", default));                                                                               // MFA is not blocked by any of it
        await l.RecordFailureAsync(User, "a", default);                                                                                         // the fifth MFA failure still blocks: the MFA count survived the login traffic
        Assert.NotNull(await l.BlockedForAsync(User, "a", default));
    }

    [Fact]
    public void The_mfa_bounds_are_their_own_constants_and_the_limiter_cannot_see_the_login_options()
    {
        var o = new MfaThrottleOptions();
        Assert.Equal((5, 100, 15, 15), (o.MaxFailuresPerUserSource, o.MaxFailuresPerUser, o.WindowMinutes, o.BlockMinutes));                   // pinned: 5, 100, 15 minutes
        Assert.DoesNotContain(typeof(MfaAttemptLimiter).GetConstructors().SelectMany(ctor => ctor.GetParameters()), p => p.ParameterType == typeof(AuthOptions));
        Assert.DoesNotContain(typeof(MfaThrottleOptions).GetProperties(), p => p.PropertyType == typeof(AuthOptions));
    }

    [Fact]
    public async Task The_mfa_limiter_blocks_at_exactly_five_whatever_the_login_options_are()
    {
        // a deployment that tightens the PASSWORD bounds (here to 1) changes AuthOptions only; the limiter takes no AuthOptions, so its bounds are fixed
        _ = new AuthOptions { MaxFailuresPerLoginSource = 1, MaxFailuresPerLoginGlobal = 1, MaxFailuresPerSource = 1 };
        var (l, _, _) = Make();
        for (var i = 0; i < 4; i++) await l.RecordFailureAsync(User, "a", default);
        Assert.Null(await l.BlockedForAsync(User, "a", default));
        await l.RecordFailureAsync(User, "a", default);
        Assert.NotNull(await l.BlockedForAsync(User, "a", default));
    }
}
