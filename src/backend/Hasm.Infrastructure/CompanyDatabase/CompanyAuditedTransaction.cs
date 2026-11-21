using Hasm.Infrastructure.Audit;
using Npgsql;

namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>The unit of work handed to a business service: the company transaction and the way to append its authenticated audit events.</summary>
public sealed class CompanyAuditedWork
{
    private readonly AuditTrail trail;
    private readonly TimeProvider clock;
    private readonly List<AuditReceipt> receipts = [];

    internal CompanyAuditedWork(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, AuditTrail trail, TimeProvider clock)
    {
        CompanyId = companyId;
        Connection = connection;
        Transaction = transaction;
        this.trail = trail;
        this.clock = clock;
    }

    public Guid CompanyId { get; }
    /// <summary>The company-database connection (runtime role). Business statements must run on it, inside <see cref="Transaction"/>.</summary>
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    internal IReadOnlyList<AuditReceipt> Receipts => receipts;
    /// <summary>The number of audit receipts so far: a savepoint's mark, so that a rollback to it can forget exactly the receipts of the events it rolled back.</summary>
    internal int ReceiptMark => receipts.Count;
    /// <summary>Forgets the receipts added since <paramref name="mark"/>: used after a ROLLBACK TO SAVEPOINT, because the events they describe no longer exist and must not be committed or anchored.</summary>
    internal void DiscardReceiptsSince(int mark) { if (receipts.Count > mark) receipts.RemoveRange(mark, receipts.Count - mark); }
    /// <summary>The audit streams this transaction has touched so far: the second half of a savepoint's mark.</summary>
    internal string[] TouchedMark() => trail.TouchedMark(Transaction);
    /// <summary>Forgets the streams first touched after <paramref name="mark"/> (after ROLLBACK TO SAVEPOINT), so the caller's next append re-takes the stream lock and re-runs the first-touch head and anchor checks.</summary>
    internal void RestoreTouched(string[] mark) => trail.RestoreTouched(Transaction, mark);

    /// <summary>
    /// Appends one authenticated event to the company's business stream INSIDE the transaction. Take every business lock first: the
    /// stream lock is taken here and must be the last lock of the transaction. Event types are 1000 and above.
    /// </summary>
    public async Task<AuditReceipt> AppendAuditAsync(AuditEventData data, CancellationToken ct = default)
    {
        var receipt = await trail.AppendAsync(Connection, Transaction, AuditStream.ForBusiness(CompanyId), clock.GetUtcNow(), data, ct);
        receipts.Add(receipt);
        return receipt;
    }
}

/// <summary>
/// The only sanctioned way for business services to change company data. The business change and its audit event(s) are committed by ONE
/// PostgreSQL transaction in the company database (<see cref="AuditTrail.CommitAsync(NpgsqlTransaction, IEnumerable{AuditReceipt}, CancellationToken)"/>);
/// a failure of either rolls both back, and a mutation without any audit event is refused and rolled back. After the commit the new
/// head is anchored in the external checkpoint store (a failure there blocks the NEXT mutation, never undoes this one). Runs through
/// <see cref="CompanyDatabaseResolver"/>, so only a Ready company, with a compatible schema, through its own least-privilege role.
/// </summary>
public sealed class CompanyAuditedTransactions(CompanyDatabaseResolver resolver, AuditTrail trail, TimeProvider clock)
{
    public async Task<T> ExecuteAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var unit = new CompanyAuditedWork(companyId, conn, tx, trail, clock);
        var result = await work(unit);      // an exception disposes the transaction without committing: nothing is persisted
        if (unit.Receipts.Count == 0)
            throw new InvalidOperationException("A company business mutation must append at least one audit event.");
        await trail.CommitAsync(tx, unit.Receipts, ct);
        return result;
    }

    public async Task ExecuteAsync(Guid companyId, Func<CompanyAuditedWork, Task> work, CancellationToken ct = default) =>
        await ExecuteAsync<object?>(companyId, async w => { await work(w); return null; }, ct);
}
