using Hasm.Infrastructure.CompanyDatabase;

namespace Hasm.Infrastructure.Tests;

public class BoundedWaitTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task An_attempt_that_succeeds_at_once_never_waits()
    {
        var waits = new List<TimeSpan>(); var calls = 0;
        var ok = await BoundedWait.UntilAsync(() => Task.FromResult(++calls > 0), 10, Pause, default, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        Assert.True(ok);
        Assert.Equal(1, calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_condition_that_becomes_true_after_a_few_failures_is_waited_for()
    {
        var waits = new List<TimeSpan>(); var calls = 0;
        var ok = await BoundedWait.UntilAsync(() => Task.FromResult(++calls == 4), 10, Pause, default, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        Assert.True(ok);
        Assert.Equal(4, calls);
        Assert.Equal([Pause, Pause, Pause], waits);                       // a pause between failures only
    }

    [Fact]
    public async Task A_condition_that_never_becomes_true_is_reported_after_exactly_the_bounded_attempts_with_no_pause_after_the_last()
    {
        var waits = new List<TimeSpan>(); var calls = 0;
        var ok = await BoundedWait.UntilAsync(() => { calls++; return Task.FromResult(false); }, 10, Pause, default, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        Assert.False(ok);
        Assert.Equal(10, calls);
        Assert.Equal(9, waits.Count);
    }

    [Fact]
    public async Task A_single_attempt_never_pauses()
    {
        var waits = 0; var calls = 0;
        Assert.False(await BoundedWait.UntilAsync(() => { calls++; return Task.FromResult(false); }, 1, Pause, default, (_, _) => { waits++; return Task.CompletedTask; }));
        Assert.Equal((1, 0), (calls, waits));
    }

    [Fact]
    public async Task Cancellation_stops_the_wait_and_fewer_than_one_attempt_is_refused()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedWait.UntilAsync(() => { calls++; cts.Cancel(); return Task.FromResult(false); }, 10, Pause, cts.Token, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedWait.UntilAsync(() => Task.FromResult(true), 0, Pause, default));
    }

    [Fact]
    public async Task The_real_pause_is_used_by_default_and_stays_short()
    {
        var calls = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await BoundedWait.UntilAsync(() => Task.FromResult(++calls == 2), 3, TimeSpan.FromMilliseconds(50), default));
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_company_lock_acquisition_is_ten_attempts_300_ms_apart()
        => Assert.Equal((10, TimeSpan.FromMilliseconds(300)), (CompanyProvisioner.LockAttempts, CompanyProvisioner.LockRetryDelay));
}
