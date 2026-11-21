using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 (review M1): the single TOTP verifier, window 1, atomic last_step compare-and-set, second-factor throttle.</summary>
public class MfaVerifierTests
{
    private static readonly byte[] SecretBytes = Totp.GenerateSecret();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid SecretId = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Store : IMfaSecretStore
    {
        private readonly object gate = new();
        public long? LastStep;
        public bool HasSecret = true, KeyMissing;
        public int Reads, Advances;
        public Task<MfaSecret?> FindActiveSecretAsync(Guid userId, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (KeyMissing) throw new MfaUnavailableException("totp_key_unavailable");
            lock (gate) return Task.FromResult<MfaSecret?>(HasSecret && userId == User ? new MfaSecret(SecretId, (byte[])SecretBytes.Clone(), LastStep) : null);
        }
        public Task<bool> TryAdvanceLastStepAsync(Guid secretId, long matchedStep, CancellationToken ct)
        {
            lock (gate)
            {
                Interlocked.Increment(ref Advances);
                if (secretId != SecretId || (LastStep is { } l && l >= matchedStep)) return Task.FromResult(false);
                LastStep = matchedStep;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Limiter : IMfaAttemptLimiter
    {
        public TimeSpan? Block; public int Failures, Resets; public bool LockoutOnNext;
        public Task<TimeSpan?> BlockedForAsync(Guid userId, string source, CancellationToken ct) => Task.FromResult(Block);
        public Task<bool> RecordFailureAsync(Guid userId, string source, CancellationToken ct) { Interlocked.Increment(ref Failures); return Task.FromResult(LockoutOnNext); }
        public Task ResetPairAsync(Guid userId, string source, CancellationToken ct) { Interlocked.Increment(ref Resets); return Task.CompletedTask; }
    }

    private static (MfaVerifier V, Store S, Limiter L, Clock C) Make() { var s = new Store(); var l = new Limiter(); var c = new Clock(T0); return (new MfaVerifier(s, l, c), s, l, c); }
    private static string CodeAt(DateTimeOffset t, int stepOffset = 0) => Totp.Compute(SecretBytes, Totp.StepFor(t) + stepOffset);

    [Fact]
    public async Task A_correct_code_is_accepted_records_the_step_and_clears_only_the_pair_counter()
    {
        var (v, s, l, _) = Make();
        var r = Assert.IsType<MfaResult.Verified>(await v.VerifyAsync(User, CodeAt(T0), "10.0.0.1"));
        Assert.Equal(Totp.StepFor(T0), r.Step);
        Assert.Equal(r.Step, s.LastStep);
        Assert.Equal((0, 1), (l.Failures, l.Resets));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    [InlineData(-2, false)]
    [InlineData(2, false)]
    public async Task The_window_is_exactly_one_step_either_side(int offset, bool accepted)
    {
        var (v, _, l, _) = Make();
        var r = await v.VerifyAsync(User, CodeAt(T0, offset), null);
        Assert.Equal(accepted, r is MfaResult.Verified);
        Assert.Equal(accepted ? 0 : 1, l.Failures);
    }

    [Fact]
    public async Task A_used_code_and_any_older_step_are_refused()
    {
        var (v, s, l, c) = Make();
        Assert.IsType<MfaResult.Verified>(await v.VerifyAsync(User, CodeAt(T0), "a"));
        Assert.IsType<MfaResult.Invalid>(await v.VerifyAsync(User, CodeAt(T0), "a"));                         // replay of the same step
        Assert.IsType<MfaResult.Invalid>(await v.VerifyAsync(User, CodeAt(T0, -1), "a"));                     // an older step inside the window
        Assert.Equal(2, l.Failures);
        c.Now = T0.AddSeconds(Totp.StepSeconds);
        Assert.IsType<MfaResult.Verified>(await v.VerifyAsync(User, CodeAt(c.Now), "a"));                     // the next step works
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_code_let_exactly_one_win()
    {
        var (v, s, l, _) = Make();
        var code = CodeAt(T0);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => v.VerifyAsync(User, code, "a"))));
        Assert.Equal(1, results.Count(r => r is MfaResult.Verified));
        Assert.Equal(15, results.Count(r => r is MfaResult.Invalid));
        Assert.Equal(15, l.Failures);
    }

    [Fact]
    public async Task A_blocked_user_is_throttled_before_any_secret_is_read()
    {
        var (v, s, l, _) = Make();
        l.Block = TimeSpan.FromMinutes(7);
        var r = Assert.IsType<MfaResult.Throttled>(await v.VerifyAsync(User, CodeAt(T0), "a"));
        Assert.Equal(TimeSpan.FromMinutes(7), r.RetryAfter);
        Assert.Equal(0, s.Reads);
        Assert.Null(s.LastStep);
    }

    [Fact]
    public async Task Wrong_malformed_and_missing_codes_fail_identically_and_are_counted()
    {
        var (v, _, l, _) = Make();
        foreach (var code in new string?[] { "000000", "12345", "1234567", "abcdef", "", null, " 123456" })
            Assert.IsType<MfaResult.Invalid>(await v.VerifyAsync(User, code, "a"));
        Assert.Equal(7, l.Failures);
    }

    [Fact]
    public async Task A_user_without_a_secret_looks_exactly_like_a_wrong_code()
    {
        var (v, s, l, _) = Make();
        s.HasSecret = false;
        Assert.Equal(new MfaResult.Invalid(), await v.VerifyAsync(User, CodeAt(T0), "a"));
        Assert.Equal(1, l.Failures);
        Assert.Equal(0, s.Advances);
    }

    [Fact]
    public async Task A_missing_key_fails_closed_without_counting_a_failure_or_accepting()
    {
        var (v, s, l, _) = Make();
        s.KeyMissing = true;
        var r = Assert.IsType<MfaResult.Unavailable>(await v.VerifyAsync(User, CodeAt(T0), "a"));
        Assert.Equal("totp_key_unavailable", r.Code);
        Assert.Equal((0, 0, 0), (l.Failures, l.Resets, s.Advances));
    }

    [Fact]
    public async Task The_per_user_lockout_is_reported_to_the_caller_for_the_alert()
    {
        var (v, _, l, _) = Make();
        l.LockoutOnNext = true;
        Assert.Equal(new MfaResult.Invalid(true), await v.VerifyAsync(User, "000000", "a"));
    }

    [Fact]
    public void No_code_outside_the_verifier_calls_Totp_Verify()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "backend", "Hasm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var allowed = new[] { "Totp.cs", "MfaVerifier.cs" };
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f => File.ReadAllText(f).Contains("Totp.Verify(", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Empty(offenders);
    }
}
