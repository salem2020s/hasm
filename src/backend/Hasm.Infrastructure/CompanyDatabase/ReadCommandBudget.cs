namespace Hasm.Infrastructure.CompanyDatabase;

internal enum ReadBudgetFailure { Exhausted, UnsupportedValidator }
internal sealed class ReadCommandBudgetException(ReadBudgetFailure failure) : Exception("Bounded read could not complete.")
{
    internal ReadBudgetFailure Failure { get; } = failure;
}

/// <summary>One operation's application-issued SQL quota; reserve BEFORE commands, including cleanup.</summary>
internal sealed class ReadCommandBudget
{
    private readonly int maximum;
    private readonly CancellationToken token;
    private int used;
    internal int Used => Volatile.Read(ref used);
    internal ReadCommandBudget(int maximum, CancellationToken token)
    {
        if (maximum is < 1 or > 20_000) throw new ArgumentOutOfRangeException(nameof(maximum));
        this.maximum = maximum; this.token = token;
    }
    internal void Reserve() => Reserve(1);
    internal void Reserve(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = Used;
            if (amount > maximum - current) throw new ReadCommandBudgetException(ReadBudgetFailure.Exhausted);
            if (Interlocked.CompareExchange(ref used, current + amount, current) == current) return;
        }
    }
}
