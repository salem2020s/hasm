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

    public bool ReportedCorrupt(string destination, string run) => LastKind.TryGetValue((destination, run), out var k) && k == BackupAuditEvents.ReplicaCorrupt;
    public DateTimeOffset? LastReverified(string destination) => LastReverifiedUtc.TryGetValue(destination, out var t) ? t : null;
}

public static class ReplicaAuditReader
{
    /// <summary>The value of one <c>key=value</c> token of a detail, or null: the tokens are split on single spaces and compared exactly.</summary>
    public static string? Token(string detail, string key)
    {
        foreach (var t in detail.Split(' '))
            if (t.Length > key.Length && t.StartsWith(key, StringComparison.Ordinal) && t[key.Length] == '=') return t[(key.Length + 1)..];
        return null;
    }

    /// <summary>Builds the state from (event type, detail, time) rows in stream order. Rows that are not exactly a 2040 or 2042 of this slice, or lack a token, are ignored.</summary>
    public static ReplicaAuditState Build(IEnumerable<(short Type, string Detail, DateTimeOffset At)> rows)
    {
        var last = new Dictionary<(string, string), short>();
        var before = new Dictionary<(string, string), short?>();      // what the last word was before the current 2040 (review L2 of #214)
        var reverified = new Dictionary<string, DateTimeOffset>();
        foreach (var (type, detail, at) in rows)
        {
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
                }
                continue;
            }
            before[key] = last.TryGetValue(key, out var prior) ? prior : null;
            last[key] = type;
            if (type == BackupAuditEvents.ReplicaVerified && Token(detail, "reverified") == "true") reverified[destination] = at;
        }
        return new(last, reverified);
    }

    /// <summary>Reads 2040 and 2042 of the system stream (read-only). Throws <see cref="BackupException"/> <c>audit_unreadable</c> when the database cannot be read.</summary>
    public static async Task<ReplicaAuditState> ReadAsync(string systemConnectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(systemConnectionString);
            await conn.OpenAsync(ct);
            // read-only by construction (review L3 of #214): a READ ONLY transaction. The rows are read as stored: the audit chain is NOT verified here (`audit-verify` does that); see docs/DEVELOPMENT.md
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand($"SELECT {E.EventType}, {E.Detail}, {E.OccurredAtUtc} FROM {E.Table} WHERE {E.StreamKey} = @s AND {E.EventType} IN (@v, @c, @f) ORDER BY {E.Seq}", conn, tx);
            cmd.Parameters.AddWithValue("s", AuditStream.SystemKey);
            cmd.Parameters.AddWithValue("v", BackupAuditEvents.ReplicaVerified);
            cmd.Parameters.AddWithValue("c", BackupAuditEvents.ReplicaCorrupt);
            cmd.Parameters.AddWithValue("f", BackupAuditEvents.ReplicaFailed);
            var rows = new List<(short, string, DateTimeOffset)>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetInt16(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
            return Build(rows);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException) { throw new BackupException("audit_unreadable"); }
    }
}
