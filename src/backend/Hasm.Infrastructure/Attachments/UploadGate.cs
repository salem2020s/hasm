namespace Hasm.Infrastructure.Attachments;

/// <summary>
/// Non-blocking limit on concurrent uploads: a process-wide ceiling and a per-user ceiling (one user cannot occupy every slot). Each slot stands for one upload that holds a bounded in-memory
/// copy of its file. <see cref="TryEnter"/> never waits; the returned slot must be disposed (disposal is idempotent) and is released on every path, including exceptions.
/// </summary>
public sealed class UploadGate(int maxTotal, int maxPerUser)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, int> perUser = [];
    private int total;

    public int InUse { get { lock (sync) return total; } }

    public IDisposable? TryEnter(Guid userId)
    {
        lock (sync)
        {
            perUser.TryGetValue(userId, out var mine);
            if (total >= maxTotal || mine >= maxPerUser) return null;
            total++;
            perUser[userId] = mine + 1;
        }
        return new Slot(this, userId);
    }

    private void Exit(Guid userId)
    {
        lock (sync)
        {
            total--;
            if (perUser.TryGetValue(userId, out var mine) && mine > 1) perUser[userId] = mine - 1; else perUser.Remove(userId);
        }
    }

    private sealed class Slot(UploadGate gate, Guid userId) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Exit(userId); }
    }
}
