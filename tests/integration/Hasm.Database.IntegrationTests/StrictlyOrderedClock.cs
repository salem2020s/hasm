namespace Hasm.Database.IntegrationTests;

/// <summary>
/// A <see cref="TimeProvider"/> for tests that create several backup runs: every reading of the time is exactly one second after the previous one, starting a few hours in the past, so the run ids of
/// consecutive runs (which begin with the start time to the second and end with a random suffix) are STRICTLY ordered by creation. With the system clock, runs created within one second order by
/// their random suffix and a test that names "the newest run" by creation order would be flaky. Durations still use the real timestamps; production ordering is unchanged.
/// </summary>
public sealed class StrictlyOrderedClock : TimeProvider
{
    private long ticks;
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-3);

    public override DateTimeOffset GetUtcNow() => start.AddSeconds(Interlocked.Increment(ref ticks));
}
