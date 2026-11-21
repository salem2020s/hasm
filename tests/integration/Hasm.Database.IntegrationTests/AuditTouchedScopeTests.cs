using Hasm.Infrastructure.Audit;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The audit trail remembers, per transaction, which streams the transaction already touched (their head is its own uncommitted record, so the first-touch head and anchor checks must not repeat).
/// Npgsql reuses the transaction OBJECT for later transactions of the same connection; that memory must therefore belong to one PostgreSQL transaction, never to the object. Disposable cluster only.
/// </summary>
public class AuditTouchedScopeTests : AuditTestBase
{
    [PostgresFact]
    public async Task A_transaction_object_reused_by_a_later_transaction_on_the_same_connection_does_not_skip_the_first_touch_checks()
    {
        var (_, admin, stream) = await Scenario(1);
        var head = (await Events(stream))[^1];
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        var data = new AuditEventData(1, ActorUserId: admin.Id, Detail: "touch");

        // transaction 1 touches the stream (full first-touch check) and is rolled back
        var tx1 = await conn.BeginTransactionAsync();
        await Audit.AppendAsync(conn, tx1, stream, Clock.GetUtcNow(), data);
        await tx1.RollbackAsync();
        await tx1.DisposeAsync();

        // between the two transactions an attacker with database access plants a forged head (no key: the MAC and hash cannot be right)
        await InsertRaw(head with { Seq = head.Seq + 1, EventId = Guid.NewGuid(), Detail = "forged" });

        // transaction 2 on the same connection: Npgsql hands back the same transaction object
        var tx2 = await conn.BeginTransactionAsync();
        Assert.Same(tx1, tx2);
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Audit.AppendAsync(conn, tx2, stream, Clock.GetUtcNow(), data));
        Assert.Equal("audit_head_corrupt", ex.Code);                                                       // the full first-touch check ran again
        await tx2.RollbackAsync();
    }

    /// <summary>Review L1 of the adaptation delta: the restore-side check of the savepoint mark. A mark taken in ANOTHER transaction (same recycled object) must count as empty when restored.</summary>
    [PostgresFact]
    public async Task A_mark_taken_in_another_transaction_restores_as_empty_even_on_a_recycled_transaction_object()
    {
        var (_, admin, stream) = await Scenario(1);
        var head = (await Events(stream))[^1];
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        var data = new AuditEventData(1, ActorUserId: admin.Id, Detail: "touch");

        var tx1 = await conn.BeginTransactionAsync();
        await Audit.AppendAsync(conn, tx1, stream, Clock.GetUtcNow(), data);                                  // transaction 1 touches the stream
        var foreign = await Audit.MarkTouchedAsync(conn, tx1);                                                // its mark names the stream
        Assert.Contains(stream.Key, foreign.Streams);
        await tx1.RollbackAsync();
        await tx1.DisposeAsync();
        await InsertRaw(head with { Seq = head.Seq + 1, EventId = Guid.NewGuid(), Detail = "forged" });

        var tx2 = await conn.BeginTransactionAsync();
        Assert.Same(tx1, tx2);
        await Audit.RestoreTouchedAsync(conn, tx2, foreign);                                                  // restoring a FOREIGN mark must not mark the stream as touched
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Audit.AppendAsync(conn, tx2, stream, Clock.GetUtcNow(), data));
        Assert.Equal("audit_head_corrupt", ex.Code);
        await tx2.RollbackAsync();
    }
}
