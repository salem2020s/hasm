namespace Hasm.Database.IntegrationTests;

/// <summary>
/// A start barrier for race tests (#165 G L1, #168 H L1): the first <c>count</c> callers wait until all of them have arrived, then go on together; later callers pass straight through.
/// Installed on a test-only hook between a read and the write it guards, it makes every racer read before any of them writes, so a lost race is deterministic instead of probable.
/// A caller that waits longer than <c>timeout</c> fails the test instead of hanging it.
/// </summary>
internal sealed class StartBarrier(int count, TimeSpan timeout)
{
    private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    /// <summary>How many callers reached the barrier (a test asserts it equals <c>count</c> or more, so the barrier really held the racers).</summary>
    public int Arrived => Volatile.Read(ref arrived);

    public async Task WaitAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref arrived);
        if (n > count) return;
        if (n == count) all.TrySetResult();
        await all.Task.WaitAsync(timeout, ct);
    }
}
