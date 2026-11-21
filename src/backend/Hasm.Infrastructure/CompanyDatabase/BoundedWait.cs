namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>
/// A bounded wait for a condition that is about to become true: the attempt is tried up to <c>attempts</c> times with a pause between failures (never after the last one), and the answer is
/// false only when every attempt failed. Used to acquire a company's advisory lock: the server releases the session lock of a just-closed connection ASYNCHRONOUSLY, so an immediate re-acquire
/// can fail although nobody holds the lock any more; a lock that is truly held keeps failing and is still reported busy, after a short, fixed, bounded delay.
/// </summary>
internal static class BoundedWait
{
    public static async Task<bool> UntilAsync(Func<Task<bool>> attempt, int attempts, TimeSpan delay, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        wait ??= Task.Delay;
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await attempt()) return true;
            if (i < attempts - 1) await wait(delay, ct);
        }
        return false;
    }
}
