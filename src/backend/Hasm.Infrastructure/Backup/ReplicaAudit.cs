using Hasm.Infrastructure.Audit;
using Npgsql;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// What the system audit stream says about the copies at the destinations (slice 8, S8c): per destination and run the LAST of 2040 (verified, possibly re-verified) and 2042 (corrupt) by stream sequence,
/// and per destination the time of the last RE-verification (a 2040 with <c>reverified=true</c>). The detail tokens are parsed EXACTLY (<c>nas-1</c> is never <c>nas-10</c>). An event is only evidence of what
/// was found at the time: whether a copy EXISTS is always read from the destination folder.
/// </summary>
public sealed record ReplicaAuditState(IReadOnlyDictionary<(string Destination, string Run), short> LastKind, IReadOnlyDictionary<string, DateTimeOffset> LastReverifiedUtc)
{
    public static readonly ReplicaAuditState Empty = new(new Dictionary<(string, string), short>(), new Dictionary<string, DateTimeOffset>());

    /// <summary>Per destination and run the 2015 (deletion started) and 2016 (deleted) facts in stream order (S8d). A fact without a <c>destination=</c> token belongs to the PRIMARY, never to a destination.</summary>
    public IReadOnlyDictionary<(string Destination, string Run), IReadOnlyList<DeletionFact>> Deletions { get; init; } = new Dictionary<(string, string), IReadOnlyList<DeletionFact>>();
    /// <summary>Per destination and run the stream sequence of the LAST 2040 (a copy was verified and publication attempted).</summary>
    public IReadOnlyDictionary<(string Destination, string Run), long> LastReplicaVerifiedSeq { get; init; } = new Dictionary<(string, string), long>();
    /// <summary>True only when the facts were read through the chain-verified prefix (<see cref="ReplicaAuditReader.ReadVerifiedAsync"/>, S8d section 4B). False for the raw, informational read of <c>backup-status</c>: such a state authorises nothing.</summary>
    public bool Authenticated { get; init; }
    /// <summary>The <c>VerifiedThroughSeq</c> of the pass that produced an authenticated state; null for a raw state.</summary>
    public long? VerifiedThroughSeq { get; init; }
    /// <summary>For an authenticated state: the system stream holds records ABOVE the verified prefix (a forged, spliced or gapped remainder); they were alerted, never used (<c>audit_unverified</c>).</summary>
    public bool UnverifiedRemainder { get; init; }

    private IReadOnlyList<DeletionFact> Facts(string destination, string run) => Deletions.TryGetValue((destination, run), out var f) ? f : [];

    /// <summary>
    /// Walks the 2015 and 2016 facts of one destination and run in stream order and pairs them: a 2016 that names a status closes the newest still-open 2015 with THAT status; a 2016 without one (the resume and
    /// recovery path names only the run) closes ONLY the newest still-open 2015, whatever its status (review A2 of PR 232: it never closes older open starts). Returns every start with the sequence of the 2016 that closed it, or null.
    /// </summary>
    private IEnumerable<(long StartSeq, string Status, long? ClosedBySeq)> Pairs(string destination, string run)
    {
        var starts = new List<(long Seq, string Status, long? Closed)>();
        foreach (var f in Facts(destination, run))
        {
            if (f.Kind == BackupAuditEvents.DeletionStarted) { starts.Add((f.Seq, f.Status!, null)); continue; }
            for (var i = starts.Count - 1; i >= 0; i--)
                if (starts[i].Closed is null && (f.Status is null || f.Status == starts[i].Status)) { starts[i] = starts[i] with { Closed = f.Seq }; break; }
        }
        return starts;
    }

    /// <summary>The newest completed 2015+2016 pair of this destination and run for the status hash: its 2015 sequence, or null. A 2016 of the recovery path (<c>recovered=audit</c>) counts only together with its 2015.</summary>
    public long? CompletedPairSeq(string destination, string run, string statusSha256)
    {
        long? best = null;
        foreach (var (start, status, closed) in Pairs(destination, run))
            if (closed is not null && status == statusSha256 && (best is null || start > best)) best = start;
        return best;
    }

    /// <summary>The newest 2015 of this destination and run for the status hash that no 2016 closes (a deletion started and not finished), or null.</summary>
    public long? OpenStartSeq(string destination, string run, string statusSha256)
    {
        long? open = null;
        foreach (var (start, status, closed) in Pairs(destination, run))
            if (closed is null && status == statusSha256 && (open is null || start > open)) open = start;
        return open;
    }

    /// <summary>Any 2015 of this destination and run for the status hash (the database knows HASM began such a deletion).</summary>
    public bool HasStart(string destination, string run, string statusSha256) => Facts(destination, run).Any(f => f.Kind == BackupAuditEvents.DeletionStarted && f.Status == statusSha256);

    /// <summary>A 2040 for this destination and run with a sequence above <paramref name="seq"/> (the copy was published again after the deletion).</summary>
    public bool CopiedAfter(string destination, string run, long seq) => LastReplicaVerifiedSeq.TryGetValue((destination, run), out var s) && s > seq;

    public bool ReportedCorrupt(string destination, string run) => LastKind.TryGetValue((destination, run), out var k) && k == BackupAuditEvents.ReplicaCorrupt;
    public DateTimeOffset? LastReverified(string destination) => LastReverifiedUtc.TryGetValue(destination, out var t) ? t : null;
}

/// <summary>One 2015 or 2016 of the system stream: its sequence, kind, the status hash it names (null for a 2016 of the resume or recovery path, which names only the run) and whether it was written by the recovery path.</summary>
public sealed record DeletionFact(long Seq, short Kind, string? Status, bool Recovered);

public static class ReplicaAuditReader
{
    internal const string PrimaryId = "primary";

    /// <summary>The value of one <c>key=value</c> token of a detail, or null: the tokens are split on single spaces and compared exactly.</summary>
    public static string? Token(string detail, string key)
    {
        foreach (var t in detail.Split(' '))
            if (t.Length > key.Length && t.StartsWith(key, StringComparison.Ordinal) && t[key.Length] == '=') return t[(key.Length + 1)..];
        return null;
    }

    /// <summary>Builds the state from (event type, detail, time) rows in stream order (sequence numbers are then 1, 2, 3, ...). See the overload with sequences.</summary>
    public static ReplicaAuditState Build(IEnumerable<(short Type, string Detail, DateTimeOffset At)> rows) =>
        Build(rows.Select((r, i) => ((long)i + 1, r.Type, r.Detail, r.At)));

    /// <summary>Builds the state from (sequence, event type, detail, time) rows in stream order. Rows that are not exactly a 2015, 2016, 2040, 2041 or 2042 of this shape, or lack a token, are ignored.</summary>
    public static ReplicaAuditState Build(IEnumerable<(long Seq, short Type, string Detail, DateTimeOffset At)> rows)
    {
        var last = new Dictionary<(string, string), short>();
        var before = new Dictionary<(string, string), short?>();      // what the last word was before the current 2040 (review L2 of #214)
        var reverified = new Dictionary<string, DateTimeOffset>();
        var deletions = new Dictionary<(string, string), List<DeletionFact>>();
        var lastCopy = new Dictionary<(string, string), long>();
        var beforeCopy = new Dictionary<(string, string), long?>();
        foreach (var (seq, type, detail, at) in rows)
        {
            if (type is BackupAuditEvents.DeletionStarted or BackupAuditEvents.Deleted)
            {
                var phrase = type == BackupAuditEvents.DeletionStarted ? "backup deletion started " : "backup deleted ";
                if (!detail.StartsWith(phrase, StringComparison.Ordinal)) continue;
                var delRun = Token(detail, "run");
                if (delRun is null || !BackupRunFiles.IsRunId(delRun)) continue;
                var delDestination = Token(detail, "destination") ?? PrimaryId;
                var status = Token(detail, "status");
                if (status is not null && !IsSha256(status)) continue;
                if (type == BackupAuditEvents.DeletionStarted && status is null) continue;                       // a 2015 always names the status it protects
                var key2 = (delDestination, delRun);
                if (!deletions.TryGetValue(key2, out var list)) deletions[key2] = list = [];
                list.Add(new DeletionFact(seq, type, status, Token(detail, "recovered") == "audit"));
                continue;
            }
            var expected = type switch
            {
                BackupAuditEvents.ReplicaVerified => "replica verified ",
                BackupAuditEvents.ReplicaCorrupt => "replica corrupt ",
                BackupAuditEvents.ReplicaFailed => "replica failed ",
                _ => null,
            };
            if (expected is null || !detail.Contains(expected, StringComparison.Ordinal)) continue;
            var destination = Token(detail, "destination"); var run = Token(detail, "run");
            if (destination is null || run is null || !BackupRunFiles.IsRunId(run)) continue;
            var key = (destination, run);
            if (type == BackupAuditEvents.ReplicaFailed)
            {
                // a 2041 that carries `status=` compensates a 2040 whose publish failed (D-185 step 5): that 2040 never produced a copy, so it is not the last word and clears nothing
                if (Token(detail, "reason") == "replica_publish_failed" && Token(detail, "status") is not null && last.TryGetValue(key, out var kind) && kind == BackupAuditEvents.ReplicaVerified)
                {
                    if (before.TryGetValue(key, out var previous) && previous is { } p) last[key] = p; else last.Remove(key);
                    if (beforeCopy.TryGetValue(key, out var pc) && pc is { } c) lastCopy[key] = c; else lastCopy.Remove(key);
                }
                continue;
            }
            before[key] = last.TryGetValue(key, out var prior) ? prior : null;
            last[key] = type;
            if (type == BackupAuditEvents.ReplicaVerified)
            {
                beforeCopy[key] = lastCopy.TryGetValue(key, out var pc2) ? pc2 : null;
                lastCopy[key] = seq;
            }
            if (type == BackupAuditEvents.ReplicaVerified && Token(detail, "reverified") == "true") reverified[destination] = at;
        }
        return new(last, reverified)
        {
            Deletions = deletions.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DeletionFact>)kv.Value),
            LastReplicaVerifiedSeq = lastCopy,
        };
    }

    private static bool IsSha256(string v) => v.Length == 64 && v.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static readonly short[] EventTypes = [BackupAuditEvents.DeletionStarted, BackupAuditEvents.Deleted, BackupAuditEvents.ReplicaVerified, BackupAuditEvents.ReplicaFailed, BackupAuditEvents.ReplicaCorrupt];

    private static async Task<List<(long Seq, short Type, string Detail, DateTimeOffset At)>> ReadRowsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long? throughSeq, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {E.Seq}, {E.EventType}, {E.Detail}, {E.OccurredAtUtc} FROM {E.Table} WHERE {E.StreamKey} = @s AND {E.EventType} = ANY(@t) AND (@max::bigint IS NULL OR {E.Seq} <= @max) ORDER BY {E.Seq}", conn, tx);
        cmd.Parameters.AddWithValue("s", AuditStream.SystemKey);
        cmd.Parameters.AddWithValue("t", EventTypes);
        cmd.Parameters.AddWithValue("max", throughSeq is { } m ? m : DBNull.Value);
        var rows = new List<(long, short, string, DateTimeOffset)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add((r.GetInt64(0), r.GetInt16(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        return rows;
    }

    /// <summary>
    /// The AUTHENTICATED read (S8d section 4B): ONE full <see cref="AuditVerifier"/> pass over the system stream from sequence 1, and the 2015, 2016, 2040, 2041 and 2042 rows read inside that pass's own
    /// READ ONLY snapshot, only at or below <c>VerifiedThroughSeq</c>. A forged, spliced or gapped row ends the prefix and is never used; facts below it are. There is no checkpoint shortcut: the pass always
    /// starts at sequence 1. Throws <see cref="BackupException"/> <c>audit_unreadable</c> when the stream cannot be read or verified (an unreadable snapshot or a missing audit key configuration).
    /// </summary>
    public static async Task<ReplicaAuditState> ReadVerifiedAsync(string systemConnectionString, AuditKeyRing keys, ICheckpointStore checkpoints, CancellationToken ct = default)
    {
        try
        {
            List<(long, short, string, DateTimeOffset)>? rows = null;
            var report = await new AuditVerifier(systemConnectionString, keys, checkpoints).VerifyAsync(AuditStream.System,
                async (conn, tx, through, c) => rows = await ReadRowsAsync(conn, tx, through, c), ct);
            var state = Build(rows ?? []);
            return state with { Authenticated = true, VerifiedThroughSeq = report.VerifiedThroughSeq, UnverifiedRemainder = report.LastSeq is { } last && last > report.VerifiedThroughSeq };
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException or AuditConfigurationException or IOException) { throw new BackupException("audit_unreadable"); }
    }

    /// <summary>Reads the backup rows of the system stream (read-only) AS STORED: the chain is NOT verified, so the state is informational and authorises nothing (<see cref="ReplicaAuditState.Authenticated"/> is false). Throws <see cref="BackupException"/> <c>audit_unreadable</c> when the database cannot be read.</summary>
    public static async Task<ReplicaAuditState> ReadAsync(string systemConnectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(systemConnectionString);
            await conn.OpenAsync(ct);
            // read-only by construction (review L3 of #214): a READ ONLY transaction. The rows are read as stored: the audit chain is NOT verified here (`audit-verify` does that); see docs/DEVELOPMENT.md
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync(ct);
            return Build(await ReadRowsAsync(conn, tx, null, ct));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException) { throw new BackupException("audit_unreadable"); }
    }
}
