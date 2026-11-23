using System.Security.Cryptography;
using Npgsql;
using AA = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Infrastructure.Audit;

public enum RecordCheck { Ok, UnsupportedFormat, HashMismatch, KeyMissing, MacInvalid }

/// <summary>
/// <see cref="Verified"/>: every record and checkpoint was checked and the stream head is anchored. <see cref="Incomplete"/>: nothing
/// contradicts the evidence, but it cannot prove integrity (missing key or checkpoint, unanchored tail, unknown format).
/// <see cref="Failed"/>: tampering or corruption was detected.
/// </summary>
public enum AuditVerdict { Verified, Incomplete, Failed }

public enum FindingSeverity { Failure, Incomplete, Note }

/// <summary>A verification finding. Messages carry codes and sequence numbers only, never keys, payloads or credentials.</summary>
public sealed record AuditFinding(string Code, FindingSeverity Severity, long? Seq, string Message);

public sealed record AuditVerificationReport(
    string Stream, AuditVerdict Verdict, long Events, long? FirstSeq, long? LastSeq, long VerifiedThroughSeq, int Checkpoints,
    long? LastCheckpointSeq, long UnanchoredEvents, long LegacyUnprotectedRows, IReadOnlyList<AuditFinding> Findings, int FindingsOmitted)
{
    /// <summary>Hash of the last stored record (not printed by the tool; used to anchor after a successful check).</summary>
    internal byte[]? LastHash { get; init; }

    public IEnumerable<AuditFinding> Failures => Findings.Where(f => f.Severity == FindingSeverity.Failure);
}

/// <summary>
/// Read-only verification of one audit stream: the whole chain (contiguous sequence from 1, links, hashes, key-authenticated
/// codes), the protected external checkpoints (authenticity, chaining, agreement with the records, tail truncation) and, for company
/// streams, the cross-check against the legacy access-audit projection. It runs in a READ ONLY, snapshot-consistent transaction,
/// never writes, repairs or deletes anything, and reads only the selected stream (company isolation).
/// </summary>
public sealed class AuditVerifier(string connectionString, AuditKeyRing keys, ICheckpointStore checkpoints)
{
    public const int MaxReportedFindings = 100;

    public static RecordCheck CheckRecord(AuditRecord r, AuditKeyRing keys)
    {
        if (!AuditCanonical.IsSupported(r.FormatVersion)) return RecordCheck.UnsupportedFormat;
        if (r.PrevHash.Length != 32 || r.Hash.Length != 32 || r.Mac.Length != 32) return RecordCheck.HashMismatch;
        var canonical = AuditCanonical.Serialize(r with { Hash = [], Mac = [] });
        if (!CryptographicOperations.FixedTimeEquals(AuditCanonical.ComputeHash(canonical), r.Hash)) return RecordCheck.HashMismatch;
        var key = keys.Find(r.KeyId);
        if (key is null) return RecordCheck.KeyMissing;
        return key.VerifyEventMac(canonical, r.Mac) ? RecordCheck.Ok : RecordCheck.MacInvalid;
    }

    public Task<AuditVerificationReport> VerifyAsync(AuditStream stream, CancellationToken ct = default) => VerifyAsync(stream, null, ct);

    /// <summary>
    /// As <see cref="VerifyAsync(AuditStream, CancellationToken)"/>, and when <paramref name="readEvidence"/> is given it is called ONCE, inside the verifier's own READ ONLY snapshot transaction and after
    /// the chain was walked, with <c>VerifiedThroughSeq</c>: the evidence it reads is therefore read in the SAME snapshot as the verification, so no row can appear between the two (S8d section 4B).
    /// The callback must only read, and must use only rows at or below the sequence it is given.
    /// </summary>
    public async Task<AuditVerificationReport> VerifyAsync(AuditStream stream, Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task>? readEvidence, CancellationToken ct = default)
    {
        var findings = new List<AuditFinding>();
        void Add(string code, FindingSeverity sev, long? seq, string message) => findings.Add(new AuditFinding(code, sev, seq, message));

        var cpRead = await checkpoints.ReadAllAsync(stream.Key, ct);
        var cps = cpRead.Checkpoints;
        var cpBySeq = new Dictionary<long, AuditCheckpoint>();
        var lastValidCpSeq = (long?)null;
        VerifyCheckpointChain(stream.Key, cps, cpBySeq, Add, ref lastValidCpSeq);
        if (cpRead.UnreadableLines > 0)
            Add("checkpoint_unreadable", FindingSeverity.Incomplete, null, $"{cpRead.UnreadableLines} checkpoint line(s) could not be read.");

        long events = 0, expectedSeq = 1, verifiedThrough = 0;
        long? first = null, last = null;
        byte[]? prevHash = null, lastHash = null;
        var chainClean = true;
        var legacyLinks = new List<(long Seq, long Ref)>();
        var matchedCps = new HashSet<long>();

        var map = AuditTableMap.For(stream);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand($"SELECT {map.SelectColumns} FROM {map.Table} WHERE {map.StreamKey} = @s ORDER BY {map.Seq}", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", stream.Key);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var reportedMissingKeys = new HashSet<string>();
            while (await rd.ReadAsync(ct))
            {
                var rec = AuditStorage.Read(rd);
                events++;
                first ??= rec.Seq;
                last = rec.Seq;
                var recordOk = true;

                if (rec.StreamKey != stream.Key || rec.CompanyId != stream.CompanyId)
                { Add("stream_mismatch", FindingSeverity.Failure, rec.Seq, "Record does not belong to the selected stream."); recordOk = false; }

                if (rec.Seq != expectedSeq)
                {
                    if (rec.Seq > expectedSeq)
                        Add("seq_gap", FindingSeverity.Failure, rec.Seq, expectedSeq == rec.Seq - 1
                            ? $"Record {expectedSeq} is missing."
                            : $"Records {expectedSeq}-{rec.Seq - 1} are missing.");
                    else
                        Add("seq_order", FindingSeverity.Failure, rec.Seq, "Sequence number out of order or duplicated.");
                    recordOk = false;
                    expectedSeq = rec.Seq;
                }

                var expectedPrev = prevHash ?? AuditCanonical.GenesisHash(stream.Key);
                if (!CryptographicOperations.FixedTimeEquals(rec.PrevHash, expectedPrev))
                { Add("link_broken", FindingSeverity.Failure, rec.Seq, "Hash-chain link to the previous record is broken."); recordOk = false; }

                switch (CheckRecord(rec, keys))
                {
                    case RecordCheck.Ok: break;
                    case RecordCheck.HashMismatch: Add("hash_mismatch", FindingSeverity.Failure, rec.Seq, "Record content does not match its hash."); recordOk = false; break;
                    case RecordCheck.MacInvalid: Add("mac_invalid", FindingSeverity.Failure, rec.Seq, "Record authentication code is invalid."); recordOk = false; break;
                    case RecordCheck.KeyMissing:
                        if (reportedMissingKeys.Add(rec.KeyId)) Add("key_missing", FindingSeverity.Incomplete, rec.Seq, $"Key '{rec.KeyId}' is not available; records authenticated with it cannot be verified.");
                        recordOk = false; break;
                    case RecordCheck.UnsupportedFormat: Add("unsupported_format", FindingSeverity.Incomplete, rec.Seq, "Record uses an audit format version this tool does not support."); recordOk = false; break;
                }

                if (cpBySeq.TryGetValue(rec.Seq, out var cp))
                {
                    matchedCps.Add(rec.Seq);
                    if (!CryptographicOperations.FixedTimeEquals(cp.Hash, rec.Hash))
                    { Add("checkpoint_mismatch", FindingSeverity.Failure, rec.Seq, "Record differs from the protected checkpoint of this sequence number."); recordOk = false; }
                }

                if (rec.LegacyRef is { } lr) legacyLinks.Add((rec.Seq, lr));

                // Contiguous verified prefix: only advances while every record so far is fully authenticated.
                if (recordOk && verifiedThrough == rec.Seq - 1 && chainClean) verifiedThrough = rec.Seq;
                else chainClean = false;
                prevHash = rec.Hash;
                lastHash = rec.Hash;
                expectedSeq = rec.Seq + 1;
            }
        }

        if (readEvidence is not null) await readEvidence(conn, tx, verifiedThrough, ct);

        // Checkpoints that no stored record answers: the tail (or a middle record) was removed after it was anchored.
        foreach (var cp in cpBySeq.Values.Where(c => !matchedCps.Contains(c.Seq)).OrderBy(c => c.Seq))
            Add(last is null || cp.Seq > last ? "tail_truncated" : "checkpoint_unmatched", FindingSeverity.Failure, cp.Seq,
                last is null || cp.Seq > last ? "The stream ends before a protected checkpoint: trailing records were deleted." : "A protected checkpoint refers to a record that is missing.");

        long unanchored = 0;
        if (events > 0)
        {
            if (!cpRead.FileExists || cps.Count == 0) Add("no_checkpoints", FindingSeverity.Incomplete, null, "No protected checkpoints exist for this stream; deletion of the final records cannot be ruled out.");
            else if (lastValidCpSeq is { } lcs && last is { } l && lcs < l)
            {
                unanchored = l - lcs;
                Add("unanchored_tail", FindingSeverity.Incomplete, l, $"{unanchored} newest record(s) are not covered by a protected checkpoint.");
            }
        }

        long legacyUnprotected = 0;
        // Only the access stream of a company has a legacy projection (in the system database); business streams have none.
        if (stream.CompanyId is { } companyId && !stream.IsBusiness)
            legacyUnprotected = await CheckLegacyProjectionAsync(conn, tx, companyId, legacyLinks, Add, ct);

        // A stream with no records proves nothing about history that exists elsewhere: never report "Verified" next to unauthenticated history.
        if (events == 0)
        {
            var hasHistory = stream.IsBusiness ? false : stream.CompanyId is not null ? legacyUnprotected > 0 : await ScalarExistsAsync(conn, tx, $"SELECT EXISTS (SELECT 1 FROM {U.Table})", ct);
            if (hasHistory)
                Add("no_authenticated_history", FindingSeverity.Incomplete, null, "The stream has no authenticated records although records or users predate the integrity chain.");
        }
        if (await VerifierCanModifyAuditAsync(conn, tx, map.Table, ct))
            Add("verifier_account_can_modify_audit", FindingSeverity.Note, null, "The database account used for verification owns the audit table or can modify it; the API runtime account must not.");

        var verdict = findings.Any(f => f.Severity == FindingSeverity.Failure) ? AuditVerdict.Failed
            : findings.Any(f => f.Severity == FindingSeverity.Incomplete) ? AuditVerdict.Incomplete : AuditVerdict.Verified;
        var omitted = Math.Max(0, findings.Count - MaxReportedFindings);
        return new AuditVerificationReport(stream.Key, verdict, events, first, last, verifiedThrough, cps.Count, lastValidCpSeq, unanchored,
            legacyUnprotected, findings.OrderBy(f => f.Severity).ThenBy(f => f.Seq).Take(MaxReportedFindings).ToList(), omitted)
        { LastHash = lastHash };
    }

    private static async Task<bool> ScalarExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>True when the connected account owns the audit table or holds UPDATE, DELETE or TRUNCATE on it (informational).</summary>
    private static async Task<bool> VerifierCanModifyAuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_get_userbyid(c.relowner) = current_user OR has_table_privilege(current_user, c.oid, 'UPDATE') " +
            "OR has_table_privilege(current_user, c.oid, 'DELETE') OR has_table_privilege(current_user, c.oid, 'TRUNCATE') FROM pg_class c WHERE c.oid = to_regclass(@t)", conn, tx);
        cmd.Parameters.AddWithValue("t", table);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private void VerifyCheckpointChain(string streamKey, IReadOnlyList<AuditCheckpoint> cps, Dictionary<long, AuditCheckpoint> bySeq,
        Action<string, FindingSeverity, long?, string> add, ref long? lastValidSeq)
    {
        byte[] expectedPrev = AuditCheckpoint.GenesisPrevMac(streamKey);
        long previousSeq = 0;
        foreach (var cp in cps)
        {
            var ok = true;
            if (cp.Seq <= previousSeq) { add("checkpoint_order", FindingSeverity.Failure, cp.Seq, "Checkpoint sequence numbers are not increasing."); ok = false; }
            if (!CryptographicOperations.FixedTimeEquals(cp.PrevMac, expectedPrev))
            { add("checkpoint_chain_broken", FindingSeverity.Failure, cp.Seq, "A checkpoint is missing or reordered in the checkpoint file."); ok = false; }
            var key = keys.Find(cp.KeyId);
            if (key is null) { add("checkpoint_key_missing", FindingSeverity.Incomplete, cp.Seq, $"Key '{cp.KeyId}' is not available; its checkpoints cannot be verified."); ok = false; }
            else if (!key.VerifyCheckpointMac(cp.Canonical(), cp.Mac)) { add("checkpoint_mac_invalid", FindingSeverity.Failure, cp.Seq, "Checkpoint authentication code is invalid."); ok = false; }
            if (ok) { bySeq[cp.Seq] = cp; lastValidSeq = cp.Seq; }
            expectedPrev = cp.Mac;
            previousSeq = Math.Max(previousSeq, cp.Seq);
        }
    }

    /// <summary>
    /// The access-audit table that predates the chain stays the projection served by the access API. Every chained event that points
    /// at a projection row must match it, and no projection row written after the chain began may lack a chained event. Rows older than
    /// the first chained event are reported as legacy (unauthenticated) history, never as an error.
    /// </summary>
    private static async Task<long> CheckLegacyProjectionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid companyId,
        List<(long Seq, long Ref)> links, Action<string, FindingSeverity, long?, string> add, CancellationToken ct)
    {
        foreach (var chunk in links.Chunk(500))
        {
            var refs = chunk.Select(l => l.Ref).ToArray();
            var seen = new Dictionary<long, (DateTimeOffset At, Guid? Actor, Guid Company, Guid? Target, Guid? Role, short Event, string? Detail)>();
            await using (var cmd = new NpgsqlCommand(
                $"SELECT {AA.Id}, {AA.OccurredAtUtc}, {AA.ActorUserId}, {AA.CompanyId}, {AA.TargetUserId}, {AA.RoleId}, {AA.EventCode}, {AA.Detail} FROM {AA.Table} WHERE {AA.Id} = ANY(@ids)", conn, tx))
            {
                cmd.Parameters.AddWithValue("ids", refs);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    seen[r.GetInt64(0)] = (r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                        r.IsDBNull(5) ? null : r.GetGuid(5), r.GetInt16(6), r.IsDBNull(7) ? null : r.GetString(7));
            }
            foreach (var (seq, reference) in chunk)
                if (!seen.ContainsKey(reference)) add("projection_missing", FindingSeverity.Failure, seq, "The access-audit row mirrored by this event is missing.");
            // Field comparison needs the chained record again; done in a second targeted read to keep memory flat.
            await using var cmp = new NpgsqlCommand(
                $"SELECT e.{E.Seq}, e.{E.LegacyRef}, e.{E.OccurredAtUtc}, e.{E.ActorUserId}, e.{E.TargetUserId}, e.{E.SubjectId}, e.{E.EventType}, e.{E.Detail} FROM {E.Table} e " +
                $"WHERE e.{E.StreamKey} = @s AND e.{E.LegacyRef} = ANY(@ids)", conn, tx);
            cmp.Parameters.AddWithValue("s", "company:" + companyId.ToString("N"));
            cmp.Parameters.AddWithValue("ids", refs);
            await using var rd = await cmp.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var seq = rd.GetInt64(0);
                if (!seen.TryGetValue(rd.GetInt64(1), out var row)) continue;
                var same = AuditCanonical.ToMicros(row.At) == AuditCanonical.ToMicros(rd.GetFieldValue<DateTimeOffset>(2))
                    && row.Company == companyId && row.Actor == (rd.IsDBNull(3) ? null : rd.GetGuid(3)) && row.Target == (rd.IsDBNull(4) ? null : rd.GetGuid(4))
                    && row.Role == (rd.IsDBNull(5) ? null : rd.GetGuid(5)) && row.Event == rd.GetInt16(6) && row.Detail == (rd.IsDBNull(7) ? null : rd.GetString(7));
                if (!same) add("projection_mismatch", FindingSeverity.Failure, seq, "The access-audit row differs from its authenticated event.");
            }
        }

        long? firstLinked = links.Count > 0 ? links.Min(l => l.Ref) : null;
        await using (var unchained = new NpgsqlCommand(
            $"SELECT count(*) FROM {AA.Table} a WHERE a.{AA.CompanyId} = @c AND a.{AA.Id} > @min AND NOT EXISTS (SELECT 1 FROM {E.Table} e WHERE e.{E.LegacyRef} = a.{AA.Id})", conn, tx))
        {
            unchained.Parameters.AddWithValue("c", companyId);
            unchained.Parameters.AddWithValue("min", firstLinked ?? long.MaxValue);
            var n = (long)(await unchained.ExecuteScalarAsync(ct))!;
            if (n > 0) add("projection_unchained", FindingSeverity.Failure, null, $"{n} access-audit row(s) written after the chain began have no authenticated event.");
        }
        await using var legacy = new NpgsqlCommand(
            $"SELECT count(*) FROM {AA.Table} a WHERE a.{AA.CompanyId} = @c AND a.{AA.Id} <= @min AND NOT EXISTS (SELECT 1 FROM {E.Table} e WHERE e.{E.LegacyRef} = a.{AA.Id})", conn, tx);
        legacy.Parameters.AddWithValue("c", companyId);
        legacy.Parameters.AddWithValue("min", firstLinked ?? long.MaxValue);
        var legacyCount = (long)(await legacy.ExecuteScalarAsync(ct))!;
        if (legacyCount > 0) add("legacy_unprotected_rows", FindingSeverity.Note, null, $"{legacyCount} access-audit row(s) predate the integrity chain and cannot be authenticated.");
        return legacyCount;
    }

    /// <summary>
    /// Administrative catch-up: records the current head of an otherwise sound stream in the checkpoint store (never touches the
    /// database). Refused when the stream shows any failure or unverifiable record, so a damaged chain is never blessed.
    /// Returns the anchored sequence number, or null for an empty stream.
    /// </summary>
    public async Task<long?> AnchorHeadAsync(AuditStream stream, TimeProvider clock, bool establishBaseline = false, CancellationToken ct = default)
    {
        var report = await VerifyAsync(stream, ct);
        // With no checkpoint at all, anchoring defines what is trusted from now on; a truncated chain would look perfect. Needs an explicit decision.
        if (!establishBaseline && report.Findings.Any(f => f.Code == "no_checkpoints"))
            throw new AuditIntegrityException("audit_baseline_requires_confirmation", "No protected checkpoint exists for this stream; anchoring would establish a new trust baseline. Review the stream, then repeat with --establish-baseline.");
        var blocking = report.Findings.Any(f => f.Severity == FindingSeverity.Failure
            || (f.Severity == FindingSeverity.Incomplete && f.Code is not ("no_checkpoints" or "unanchored_tail" or "no_authenticated_history")));
        if (blocking || report.FindingsOmitted > 0)
            throw new AuditIntegrityException("audit_verification_failed", "The stream did not verify; refusing to anchor it.");
        if (report.LastSeq is not { } seq || report.LastHash is not { } hash) return null;
        await checkpoints.AppendAsync(stream.Key, seq, hash, AuditCanonical.ToMicros(clock.GetUtcNow()), keys.GetActive(), ct);
        return seq;
    }
}
