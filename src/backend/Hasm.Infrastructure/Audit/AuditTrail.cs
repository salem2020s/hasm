using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hasm.Infrastructure.Audit;

/// <summary>Everything the trail needs, resolved on first use so an unconfigured deployment starts but fails audited mutations closed.</summary>
public sealed record AuditServices(AuditKeyRing Keys, ICheckpointStore Checkpoints, int MaxUnanchoredEvents);

/// <summary>SQL for the audit table, shared by the writer and the read-only verifier.</summary>
internal static class AuditStorage
{
    /// <summary>Select list of the system audit table (kept for existing callers); other tables use <see cref="AuditTableMap.SelectColumns"/>.</summary>
    public static string Columns => AuditTableMap.System.SelectColumns;

    public static AuditRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetGuid(2), AuditCanonical.ToMicros(r.GetFieldValue<DateTimeOffset>(3)), r.GetInt16(4),
        r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.GetInt16(11), r.GetString(12), r.GetFieldValue<byte[]>(13), r.GetFieldValue<byte[]>(14), r.GetFieldValue<byte[]>(15));
}

/// <summary>
/// Writes tamper-evident audit events. An event is appended INSIDE the caller's database transaction, so the audited mutation and
/// its event commit or roll back together; if the event cannot be written (keys missing, chain damaged, checkpoint store
/// failing beyond the allowed lag) an exception is thrown and the caller's transaction is abandoned. After the commit the new
/// head is anchored in the external checkpoint store; a failure there is logged and the NEXT audited mutation refuses to run
/// (fail-safe) until the head is anchored again, so the unanchored window never exceeds <c>MaxUnanchoredEvents</c> events.
///
/// Concurrency: appends to one stream are serialized by a transaction-scoped advisory lock taken last in every transaction
/// (always after the company/identity locks), so sequence numbers are gap-free and the chain has a single order. Different
/// streams do not block each other. The lock is released at commit/rollback; a crash leaves nothing half-written.
/// </summary>
public sealed class AuditTrail
{
    private readonly Lazy<AuditServices> services;
    private readonly TimeProvider clock;
    private readonly ILogger? logger;

    // Streams this transaction has already appended to. Their head is our own uncommitted record: it was validated when the transaction
    // first touched the stream and must never be anchored before it commits (a rollback would leave a checkpoint for a record that never existed).
    // The state is keyed by the NpgsqlTransaction OBJECT, which Npgsql reuses for later transactions of the same connection, so it also records the
    // PostgreSQL transaction id (xid8, never reused) it belongs to: a reused object with another xid starts empty, so a rolled-back or abandoned
    // transaction can never make the next one skip the first-touch head and anchor checks.
    private sealed class TouchedState { public string Xid = ""; public readonly HashSet<string> Streams = []; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<NpgsqlTransaction, TouchedState> touched = new();

    /// <summary>The PostgreSQL transaction id (xid8, never reused; assigned on first use) of the transaction <paramref name="tx"/> currently is.</summary>
    private static async Task<string> CurrentXidAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var xid = new NpgsqlCommand("SELECT pg_current_xact_id()::text", conn, tx);
        return (string)(await xid.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// A savepoint's mark of the first-touch state: the transaction id TOGETHER with the streams touched so far. The streams count only if the recorded state belongs to THIS transaction
    /// (a recycled transaction object may still carry the streams of an earlier transaction, which must never be restored as "touched in this transaction").
    /// </summary>
    internal async Task<AuditTouchedMark> MarkTouchedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct = default)
    {
        var current = await CurrentXidAsync(conn, tx, ct);
        return new AuditTouchedMark(current, touched.TryGetValue(tx, out var state) && state.Xid == current ? [.. state.Streams] : []);
    }

    /// <summary>
    /// After a ROLLBACK TO SAVEPOINT: forgets the streams first touched after <paramref name="mark"/>. The rollback undid their events and released their advisory locks, so the next append must re-take
    /// the lock and re-run the first-touch head and anchor checks instead of trusting a head that may since have changed. A mark whose transaction id is not the current transaction's counts as EMPTY.
    /// </summary>
    internal async Task RestoreTouchedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditTouchedMark mark, CancellationToken ct = default)
    {
        var current = await CurrentXidAsync(conn, tx, ct);
        var state = touched.GetOrCreateValue(tx);
        state.Streams.Clear();
        state.Xid = current;
        if (mark.Xid == current) foreach (var k in mark.Streams) state.Streams.Add(k);
    }

    public AuditTrail(Func<AuditServices> factory, TimeProvider clock, ILogger? logger = null)
    {
        services = new Lazy<AuditServices>(factory, LazyThreadSafetyMode.PublicationOnly);
        this.clock = clock;
        this.logger = logger;
    }

    public AuditTrail(AuditServices fixedServices, TimeProvider clock, ILogger? logger = null) : this(() => fixedServices, clock, logger) { }

    /// <summary>Builds the trail from configuration: file-based keys and checkpoint directory. Validation is deferred to first use.</summary>
    public static AuditTrail FromOptions(AuditOptions options, TimeProvider clock, ILogger? logger = null) => new(() =>
    {
        options.EnsureValid(requireActiveKey: true, requireCheckpoints: true);
        return new AuditServices(new FileAuditKeyRing(options), new FileCheckpointStore(options.CheckpointDirectory!, !options.AllowInsecureFilePermissions), options.MaxUnanchoredEvents);
    }, clock, logger);

    internal AuditServices Services => services.Value;

    /// <summary>
    /// Appends one event to <paramref name="stream"/> in the caller's transaction and returns its receipt. Pass the receipt to
    /// <see cref="CommitAsync"/>. The caller must already hold its own business locks; this method takes the stream lock last.
    /// </summary>
    public async Task<AuditReceipt> AppendAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditStream stream, DateTimeOffset occurredAt,
        AuditEventData data, CancellationToken ct = default)
    {
        if (data.EventType < 1) throw new ArgumentOutOfRangeException(nameof(data), "Audit event types start at 1.");
        var map = AuditTableMap.For(stream);
        if (stream.IsBusiness)
        {
            // Business events live in the company database: only the reserved business range, and no legacy projection exists for them.
            if (data.EventType < AuditEventTypes.FutureRangeStart) throw new ArgumentOutOfRangeException(nameof(data), "Business audit event types start at 1000.");
            if (data.LegacyRef is not null) throw new ArgumentException("Business audit events have no legacy reference.", nameof(data));
        }
        var svc = services.Value;                      // throws AuditConfigurationException when unconfigured
        var key = svc.Keys.GetActive();                // throws when the active key is missing

        await using (var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 4))", conn, tx))
        {
            lck.Parameters.AddWithValue("k", "hasm.audit." + stream.Key);
            await lck.ExecuteNonQueryAsync(ct);
        }

        var head = await ReadHeadAsync(conn, tx, map, stream.Key, ct);
        var state = touched.GetOrCreateValue(tx);
        await using (var xid = new NpgsqlCommand("SELECT pg_current_xact_id()::text", conn, tx))       // assigns the transaction id (this transaction is about to write anyway)
        {
            var current = (string)(await xid.ExecuteScalarAsync(ct))!;
            if (state.Xid != current) { state.Streams.Clear(); state.Xid = current; }                // a recycled transaction object from an earlier transaction: forget its state
        }
        var firstTouch = state.Streams.Add(stream.Key);
        if (firstTouch) await EnsureHeadIsSoundAndAnchoredAsync(svc, stream.Key, head, ct);     // committed state only

        var detail = data.Detail is { Length: > AuditEventData.MaxDetailLength } d ? d[..(AuditEventData.MaxDetailLength - 3)] + "..." : data.Detail;
        var unsealed = new AuditRecord(stream.Key, (head?.Seq ?? 0) + 1, Guid.NewGuid(), AuditCanonical.ToMicros(occurredAt), data.EventType, stream.CompanyId,
            data.ActorUserId, data.TargetUserId, data.SubjectId, data.LegacyRef, detail, (short)AuditCanonical.CurrentVersion, key.Id,
            head?.Hash ?? AuditCanonical.GenesisHash(stream.Key), [], []);
        var rec = AuditCanonical.Seal(unsealed, key);

        await using var cmd = new NpgsqlCommand(map.InsertSql, conn, tx);
        cmd.Parameters.AddWithValue("s", rec.StreamKey);
        cmd.Parameters.AddWithValue("q", rec.Seq);
        cmd.Parameters.AddWithValue("id", rec.EventId);
        cmd.Parameters.AddWithValue("t", rec.OccurredAtUtc);
        cmd.Parameters.AddWithValue("e", rec.EventType);
        cmd.Parameters.AddWithValue("c", (object?)rec.CompanyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a", (object?)rec.ActorUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tu", (object?)rec.TargetUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("su", (object?)rec.SubjectId ?? DBNull.Value);
        if (map.HasLegacyRef) cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)rec.LegacyRef ?? DBNull.Value });
        cmd.Parameters.AddWithValue("d", (object?)rec.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("v", rec.FormatVersion);
        cmd.Parameters.AddWithValue("k", rec.KeyId);
        cmd.Parameters.AddWithValue("p", rec.PrevHash);
        cmd.Parameters.AddWithValue("h", rec.Hash);
        cmd.Parameters.AddWithValue("m", rec.Mac);
        await cmd.ExecuteNonQueryAsync(ct);
        return new AuditReceipt(rec.StreamKey, rec.Seq, rec.Hash, rec.OccurredMicros);
    }

    /// <summary>
    /// Commits the caller's transaction (the business change and its audit event(s) together), then anchors each stream's new head
    /// in the external checkpoint store. An anchoring failure never undoes the committed mutation; it is logged (error code only)
    /// and blocks later audited mutations until the head is anchored.
    /// </summary>
    public async Task CommitAsync(NpgsqlTransaction tx, IEnumerable<AuditReceipt> receipts, CancellationToken ct = default)
    {
        await tx.CommitAsync(ct);
        foreach (var latest in receipts.GroupBy(r => r.StreamKey).Select(g => g.MaxBy(r => r.Seq)!))
        {
            try { await AnchorAsync(latest, CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var code = ex is AuditUnavailableException a ? a.Code : "audit_anchor_failed";
                logger?.LogError("audit.anchor_failed code={Code} seq={Seq}", code, latest.Seq);
            }
        }
    }

    public Task CommitAsync(NpgsqlTransaction tx, AuditReceipt receipt, CancellationToken ct = default) => CommitAsync(tx, [receipt], ct);

    /// <summary>Anchors a committed head. A newer checkpoint already covers older records through the hash chain, so an older receipt is a no-op.</summary>
    internal async Task AnchorAsync(AuditReceipt receipt, CancellationToken ct)
    {
        var svc = services.Value;
        var last = await svc.Checkpoints.ReadLastAsync(receipt.StreamKey, ct);
        if (last is not null)
        {
            if (last.Seq > receipt.Seq) return;
            if (last.Seq == receipt.Seq && !CryptographicOperations.FixedTimeEquals(last.Hash, receipt.Hash))
                throw new AuditIntegrityException("audit_fork", "The stream head conflicts with its protected checkpoint.");
            if (last.Seq == receipt.Seq) return;
        }
        await svc.Checkpoints.AppendAsync(receipt.StreamKey, receipt.Seq, receipt.Hash, AuditCanonical.ToMicros(clock.GetUtcNow()), svc.Keys.GetActive(), ct);
    }

    private static async Task<AuditRecord?> ReadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditTableMap map, string streamKey, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {map.SelectColumns} FROM {map.Table} WHERE {map.StreamKey} = @s ORDER BY {map.Seq} DESC LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("s", streamKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? AuditStorage.Read(r) : null;
    }

    /// <summary>
    /// Refuses to extend a stream whose head is damaged, contradicts its protected checkpoint, or is further ahead than the
    /// allowed unanchored window (after first trying to catch the checkpoint up). The chain is never reset or silently repaired.
    /// </summary>
    private async Task EnsureHeadIsSoundAndAnchoredAsync(AuditServices svc, string streamKey, AuditRecord? head, CancellationToken ct)
    {
        var last = await svc.Checkpoints.ReadLastAsync(streamKey, ct);
        if (head is null)
        {
            if (last is not null)
                throw new AuditIntegrityException("audit_stream_missing", "A protected checkpoint exists but the audit stream is empty.");
            return;
        }

        switch (AuditVerifier.CheckRecord(head, svc.Keys))
        {
            case RecordCheck.Ok: break;
            case RecordCheck.KeyMissing: throw new AuditUnavailableException("audit_key_unavailable", "The key that authenticated the stream head is not available.");
            default: throw new AuditIntegrityException("audit_head_corrupt", "The audit stream head failed verification; refusing to extend the chain.");
        }

        if (last is null)
        {
            // The writer that just committed the first event(s) of a stream may not have anchored them yet (it anchors right after its
            // commit, and this transaction only got the lock because that commit released it): give an in-flight anchor a moment to land.
            for (var i = 0; i < 30 && last is null; i++)
            {
                await Task.Delay(100, ct);
                last = await svc.Checkpoints.ReadLastAsync(streamKey, ct);
            }
        }
        if (last is null)
        {
            // Events exist but no protected checkpoint does: the checkpoint store was lost, replaced or never written. Anchoring the
            // current head here would silently make whatever the database now contains the trusted baseline (e.g. after the tail was
            // deleted together with the checkpoints), so a person must do it deliberately: audit-anchor --establish-baseline.
            throw new AuditIntegrityException("audit_baseline_missing", "Audit events exist but no protected checkpoint does; an administrator must review the stream and establish a baseline.");
        }
        else
        {
            if (last.Seq > head.Seq)
                throw new AuditIntegrityException("audit_stream_truncated", "The audit stream is shorter than its protected checkpoint.");
            if (last.Seq == head.Seq && !CryptographicOperations.FixedTimeEquals(last.Hash, head.Hash))
                throw new AuditIntegrityException("audit_fork", "The stream head conflicts with its protected checkpoint.");
        }

        var lag = head.Seq - (last?.Seq ?? 0);
        if (lag > svc.MaxUnanchoredEvents)
            await AnchorAsync(new AuditReceipt(streamKey, head.Seq, head.Hash, head.OccurredMicros), ct);   // throws when the store is unavailable
    }
}

/// <summary>The first-touch state of one transaction at a savepoint: its PostgreSQL transaction id and the audit streams it had touched.</summary>
internal sealed record AuditTouchedMark(string Xid, string[] Streams);
