using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Hasm.Infrastructure.Audit;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The result of copying one run to one destination (Phase 4.3 slice 8, S8b; D-185). <see cref="Result"/> is <c>verified</c> (a new copy was published), <c>reverified</c> (an identical copy was already
/// there and was verified again) or <c>failed</c> with a stable <see cref="Code"/> (never a path).
/// </summary>
public sealed record ReplicaOutcome(string DestinationId, string RunId, string Result, string? Code, int Files, long Bytes, long DurationMs)
{
    public bool IsVerified => Result is "verified" or "reverified";
}

/// <summary>The system-stream audit events of slice 8 (D-188), behind an interface so the copy engine can be tested without a database.</summary>
public interface IReplicaEvents
{
    /// <summary>2040: the copy was verified by read-back after flush and a publish is attempted next (committed BEFORE the rename).</summary>
    Task VerifiedAsync(string runId, string statusSha256, BackupDestination destination, int files, long bytes, long durationMs, bool reverified, CancellationToken ct);
    /// <summary>2041: the copy of a run failed (or a publish failed after 2040: <paramref name="statusSha256"/> is then the hash of the 2040 it compensates).</summary>
    Task FailedAsync(string runId, BackupDestination destination, string reason, string? statusSha256, CancellationToken ct);
    /// <summary>2042: a copy found by re-verification to no longer verify; <paramref name="check"/> is the failing check (a stable code, never a path).</summary>
    Task CorruptAsync(string runId, BackupDestination destination, string check, CancellationToken ct);
    /// <summary>2043: a destination was refused before anything was written (<paramref name="destinationId"/> is null when the configuration names none).</summary>
    Task RefusedAsync(string? destinationId, string reason, CancellationToken ct);
}

/// <summary>Writes 2040, 2041 and 2043 to the system audit stream. The detail is exact <c>key=value</c> tokens separated by single spaces (parse them exactly, never with a LIKE match: <c>nas-1</c> is not <c>nas-10</c>).</summary>
public sealed class SystemReplicaEvents(string systemConnectionString, AuditTrail trail) : IReplicaEvents
{
    public Task VerifiedAsync(string runId, string statusSha256, BackupDestination d, int files, long bytes, long durationMs, bool reverified, CancellationToken ct) =>
        BackupAudit.WriteAsync(systemConnectionString, trail, BackupAuditEvents.ReplicaVerified,
            $"replica verified operator={BackupAudit.Operator()} run={runId} destination={d.Id} kind={d.KindName} status={statusSha256} files={files} bytes={bytes} duration_ms={durationMs} reverified={(reverified ? "true" : "false")}", ct: ct);

    public Task FailedAsync(string runId, BackupDestination d, string reason, string? statusSha256, CancellationToken ct) =>
        BackupAudit.WriteAsync(systemConnectionString, trail, BackupAuditEvents.ReplicaFailed,
            $"replica failed operator={BackupAudit.Operator()} run={runId} destination={d.Id} reason={reason}{(statusSha256 is null ? "" : " status=" + statusSha256)}", ct: ct);

    public Task CorruptAsync(string runId, BackupDestination d, string check, CancellationToken ct) =>
        BackupAudit.WriteAsync(systemConnectionString, trail, BackupAuditEvents.ReplicaCorrupt,
            $"replica corrupt operator={BackupAudit.Operator()} run={runId} destination={d.Id} check={check}", ct: ct);

    public Task RefusedAsync(string? destinationId, string reason, CancellationToken ct) =>
        BackupAudit.WriteAsync(systemConnectionString, trail, BackupAuditEvents.DestinationRefused,
            $"destination refused operator={BackupAudit.Operator()} destination={destinationId ?? "none"} reason={reason}", ct: ct);
}

/// <summary>Test seams of the copy engine (never set in production): each runs at one fixed point of D-185.</summary>
internal sealed class ReplicaCopyHooks
{
    /// <summary>Runs for every file just before it is flushed (throw to simulate a flush error).</summary>
    public Action<string>? BeforeFlush { get; init; }
    /// <summary>Runs after the copy and before the read-back verification, with the staging folder (alter a byte to simulate corruption at the destination).</summary>
    public Action<string>? AfterCopy { get; init; }
    /// <summary>Runs after 2040 and before the rename (staging folder, final folder): throw <see cref="IOException"/> to simulate a failed publish, or <see cref="ReplicaCrashSimulation"/> to simulate a crash.</summary>
    public Action<string, string>? BeforePublish { get; init; }
    /// <summary>Runs for every folder that is flushed (staging subfolders, staging, the destination root).</summary>
    public Action<string>? OnFlushDirectory { get; init; }
}

/// <summary>Test only: stops the copy exactly where a crash would, leaving the staging folder in place and writing nothing else.</summary>
internal sealed class ReplicaCrashSimulation : Exception;

internal sealed class ReplicaDigestException(string check) : Exception("replica digest mismatch")
{
    public string Check { get; } = check;
}

internal sealed class ReplicaVerificationException(string check) : Exception("replica verification failed")
{
    public string Check { get; } = check;
}

/// <summary>
/// The copy engine of slice 8 (D-182, D-185). A copy is a byte-for-byte copy of a COMPLETE signed encrypted run; it reads the primary's files only, never decrypts and never needs the recovery private key.
/// For each run and destination: stage into <c>&lt;run&gt;.incoming-&lt;16 hex&gt;</c> (owner-only), flush every file, CLOSE it, REOPEN it through a new handle and verify the copy as the operating system
/// presents it (signatures of the status and the manifests against the trust list, every file equal to the primary's by size and SHA-256, the HBE1 structure of every encrypted file, nothing else in the
/// folder), write audit 2040, then rename into place and flush the parent. Anything else is removed and answered by 2041. The caller holds the destination lock and the shared restore lock.
/// <para>
/// What the verification proves is stated, not hidden (review L4 of #176): the copy as the operating system presents it after the flush, which catches truncation, substitution, alteration, a wrong status,
/// an unexpected file and a failed flush; NOT the bytes on the medium. The manifests are encrypted to the offline recovery key, so a keyless check cannot read the digests inside them; it relies on their
/// signatures (over the ciphertext) and on the equality with the primary's files. The primary itself is checked against its manifests by the recovery test (which holds the key).
/// </para>
/// </summary>
public sealed class ReplicaCopier(SigningTrust trust, IReplicaEvents events, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    internal ReplicaCopyHooks? Hooks { get; init; }
    /// <summary>
    /// Repeats the link, overlap and duplicate checks of the destination (D-184); throws <see cref="BackupException"/> with the refusal code. Called before the copy starts, before the event 2040 and immediately
    /// before the rename, so a destination swapped for a link after the first check is not written through (review L2 of #213).
    /// </summary>
    public Action<CheckedDestination>? Recheck { get; init; }
    private static bool IsDestinationRefusal(string code) => code is "symbolic_link_refused" or "destination_overlaps_source" or "destination_duplicate";

    private sealed record FileRecord(string Name, long Size, string Sha256);

    public async Task<ReplicaOutcome> ReplicateAsync(CheckedDestination d, string primaryRoot, RetentionRun run, CancellationToken ct)
    {
        if (!ReplicaStatus.IsReplicable(run)) throw new ArgumentException("Only a replicable run is copied.", nameof(run));
        var watch = Stopwatch.StartNew();
        var runDir = Path.Combine(primaryRoot, run.RunId);
        var final = Path.Combine(d.ResolvedPath, run.RunId);
        string? staging = null;
        CiphertextDigestList list;
        try
        {
            // the signed inventory of the run's encrypted files, bound to the signed status: nothing is copied or reported verified without it (D-185 as amended)
            list = CiphertextDigests.ReadVerified(runDir, run.RunId, SignedDocuments.ReadRunStatus(runDir, trust).DigestListSha256, trust);
        }
        catch (BackupException ex) { return await FailAsync(d, run, ex.Code is "replica_digest_list_missing" or "replica_digest_list_invalid" ? ex.Code : "replica_verification_failed", watch, ct); }
        try
        {
            try { Recheck?.Invoke(d); }
            catch (BackupException ex) when (IsDestinationRefusal(ex.Code)) { return await RefuseAsync(d, run, ex.Code, watch, ct); }
            if (!EnsureDestinationFolder(d)) return await FailAsync(d, run, "destination_unreachable", watch, ct);
            SweepStaleStaging(d.ResolvedPath);

            if (Directory.Exists(final) || File.Exists(final))
            {
                var existing = RetentionInventory.Read(new DirectoryInfo(final), trust);
                // a folder under this run id that is not the primary's verified run is evidence and is never touched (D-185 step 2)
                if (!ReplicaStatus.IsReplicable(existing) || !string.Equals(existing.StatusSha256, run.StatusSha256, StringComparison.Ordinal)) return await FailAsync(d, run, "replica_conflict", watch, ct);
                // review U1 of PR 214: a destination folder that shares storage with the primary's run (a bind mount, a mount above the destination, a link, a hard-linked file) is no copy
                if (ReplicaIndependence.Check(final, runDir).Shares) return await FailAsync(d, run, "replica_not_independent", watch, ct);
                return await ReverifyAsync(d, runDir, final, run, list, watch, ct);
            }

            staging = Path.Combine(d.ResolvedPath, $"{run.RunId}.incoming-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
            CreateOwnerOnlyDirectory(staging);
            var copied = await CopyFilesAsync(runDir, staging, ct);
            // the bytes READ from the primary must be the ones the signed inventory names: ciphertext altered after the run is never copied as "verified" (review M1 of #213)
            CheckAgainstList(copied, list);
            FlushTree(staging);
            Hooks?.AfterCopy?.Invoke(staging);
            await VerifyCopyAsync(staging, copied, run.StatusSha256!, ct);   // the copy equals what was read from the primary, and so (by the check above) the signed inventory

            try { Recheck?.Invoke(d); }
            catch (BackupException ex) when (IsDestinationRefusal(ex.Code)) { RemoveBestEffort(staging); return await RefuseAsync(d, run, ex.Code, watch, ct); }
            // D-188: nothing is renamed into place before the event of its verification is committed; an event that cannot be written fails the copy closed
            await WriteEventAsync(() => events.VerifiedAsync(run.RunId, run.StatusSha256!, d.Destination, copied.Count, copied.Sum(f => f.Size), (long)watch.Elapsed.TotalMilliseconds, false, ct));
            try
            {
                Hooks?.BeforePublish?.Invoke(staging, final);
                Recheck?.Invoke(d);                                                         // immediately before the rename
                Directory.Move(staging, final);
                staging = null;
                FlushDir(d.ResolvedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || (ex is BackupException be && IsDestinationRefusal(be.Code)))
            {
                // compensating event of D-185 step 5: the order stays audit first, so a failed publish is answered by 2041 and no run folder is left under the run id
                if (staging is null) TryMove(final, Path.Combine(d.ResolvedPath, $"{run.RunId}.incoming-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}"), out staging);
                RemoveBestEffort(staging);
                await WriteEventAsync(() => events.FailedAsync(run.RunId, d.Destination, "replica_publish_failed", run.StatusSha256, ct));
                return new(d.Destination.Id, run.RunId, "failed", "replica_publish_failed", copied.Count, copied.Sum(f => f.Size), (long)watch.Elapsed.TotalMilliseconds);
            }
            return new(d.Destination.Id, run.RunId, "verified", null, copied.Count, copied.Sum(f => f.Size), (long)watch.Elapsed.TotalMilliseconds);
        }
        catch (ReplicaCrashSimulation) { throw; }                                           // a simulated crash leaves everything exactly as it is
        catch (ReplicaDigestException) { RemoveBestEffort(staging); return await FailAsync(d, run, "replica_digest_mismatch", watch, ct); }
        catch (ReplicaVerificationException) { RemoveBestEffort(staging); return await FailAsync(d, run, "replica_verification_failed", watch, ct); }
        catch (IOException ex) { RemoveBestEffort(staging); return await FailAsync(d, run, IsDiskFull(ex) ? "destination_full" : "destination_unreachable", watch, ct); }
        catch (UnauthorizedAccessException) { RemoveBestEffort(staging); return await FailAsync(d, run, "destination_unreachable", watch, ct); }
        catch (BackupException ex) when (ex.Code is "replica_audit_failed") { RemoveBestEffort(staging); throw; }
        catch (BackupException) { RemoveBestEffort(staging); return await FailAsync(d, run, "replica_verification_failed", watch, ct); }   // a document that does not verify
    }

    /// <summary>The copy is already there with the primary's status hash: it is verified again (every file against the primary's) and a later <c>recovery-test.json</c> is brought over through staging.</summary>
    private async Task<ReplicaOutcome> ReverifyAsync(CheckedDestination d, string runDir, string final, RetentionRun run, CiphertextDigestList list, Stopwatch watch, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            await BringProofAsync(d, Path.GetDirectoryName(runDir)!, run, ct);
            var records = await DescribeAsync(runDir, ct);
            CheckAgainstList(records, list);                                                // the primary against the signed inventory, then the copy against the primary
            await VerifyCopyAsync(final, records, run.StatusSha256!, ct);
            await WriteEventAsync(() => events.VerifiedAsync(run.RunId, run.StatusSha256!, d.Destination, records.Count, records.Sum(f => f.Size), (long)watch.Elapsed.TotalMilliseconds, true, ct));
            return new(d.Destination.Id, run.RunId, "reverified", null, records.Count, records.Sum(f => f.Size), (long)watch.Elapsed.TotalMilliseconds);
        }
        catch (ReplicaDigestException) { RemoveBestEffort(staging); return await FailAsync(d, run, "replica_digest_mismatch", watch, ct); }
        catch (ReplicaVerificationException) { RemoveBestEffort(staging); return await FailAsync(d, run, "replica_verification_failed", watch, ct); }
        catch (IOException ex) { RemoveBestEffort(staging); return await FailAsync(d, run, IsDiskFull(ex) ? "destination_full" : "destination_unreachable", watch, ct); }
        catch (UnauthorizedAccessException) { RemoveBestEffort(staging); return await FailAsync(d, run, "destination_unreachable", watch, ct); }
        catch (BackupException ex) when (ex.Code is "replica_audit_failed") { RemoveBestEffort(staging); throw; }
        catch (BackupException) { RemoveBestEffort(staging); return await FailAsync(d, run, "replica_verification_failed", watch, ct); }
    }

    private const string RetentionProofName = "recovery-test.json";

    /// <summary>
    /// A <c>recovery-test.json</c> written to the primary run after the run is brought into an existing verified copy through the same staging rule: copy into a staging folder beside the destination root,
    /// flush, read back and compare with the bytes read from the primary, then rename into place. It never changes a signed file. Without a difference nothing happens.
    /// </summary>
    public async Task<bool> BringProofAsync(CheckedDestination d, string primaryRoot, RetentionRun run, CancellationToken ct)
    {
        var runDir = Path.Combine(primaryRoot, run.RunId);
        var final = Path.Combine(d.ResolvedPath, run.RunId);
        var proof = Path.Combine(runDir, RetentionProofName);
        var copyProof = Path.Combine(final, RetentionProofName);
        if (!File.Exists(proof) || (File.Exists(copyProof) && FilesEqual(proof, copyProof))) return false;
        var staging = Path.Combine(d.ResolvedPath, $"{run.RunId}.incoming-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
        try
        {
            CreateOwnerOnlyDirectory(staging);
            var staged = Path.Combine(staging, RetentionProofName);
            var rec = await CopyOneAsync(proof, staged, RetentionProofName, ct);
            FlushDir(staging);
            if (!string.Equals(await HashFileAsync(staged, ct), rec.Sha256, StringComparison.Ordinal)) throw new ReplicaVerificationException("proof_hash");
            File.Move(staged, copyProof, true);
            FlushDir(final);
            return true;
        }
        finally { RemoveBestEffort(staging); }
    }

    private async Task<ReplicaOutcome> FailAsync(CheckedDestination d, RetentionRun run, string code, Stopwatch watch, CancellationToken ct)
    {
        await WriteEventAsync(() => events.FailedAsync(run.RunId, d.Destination, code, null, ct));
        return new(d.Destination.Id, run.RunId, "failed", code, 0, 0, (long)watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>A destination that failed the repeated safety check: 2043 (refused) and a failed outcome with the refusal code; nothing is written to or through it.</summary>
    private async Task<ReplicaOutcome> RefuseAsync(CheckedDestination d, RetentionRun run, string code, Stopwatch watch, CancellationToken ct)
    {
        await WriteEventAsync(() => events.RefusedAsync(d.Destination.Id, code, ct));
        return new(d.Destination.Id, run.RunId, "failed", code, 0, 0, (long)watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// The encrypted files read from the primary must be exactly the ones the signed inventory names, with its sizes and SHA-256 (D-185 as amended): a listed file that is absent or different, or an
    /// encrypted file the inventory does not list, is <c>replica_digest_mismatch</c>.
    /// </summary>
    private static void CheckAgainstList(IReadOnlyList<FileRecord> records, CiphertextDigestList list)
    {
        var encrypted = records.Where(r => r.Name.EndsWith(".hbe", StringComparison.Ordinal)).ToDictionary(r => r.Name, StringComparer.Ordinal);
        if (encrypted.Count != list.Files.Count) throw new ReplicaDigestException("file_set");
        foreach (var e in list.Files)
            if (!encrypted.TryGetValue(e.Name, out var r) || r.Size != e.Size || !string.Equals(r.Sha256, e.Sha256, StringComparison.Ordinal)) throw new ReplicaDigestException("hash");
    }

    /// <summary>Flushes every folder created inside <paramref name="root"/> (deepest first) and then the folder itself, so no entry of a published copy can be missing after a power loss (review L1 of #213).</summary>
    private void FlushTree(string root)
    {
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.AllDirectories).Select(x => x.FullName).OrderByDescending(x => x.Length)) FlushDir(dir);
        FlushDir(root);
    }

    private void FlushDir(string path) { Hooks?.OnFlushDirectory?.Invoke(path); FlushDirectory(path); }

    /// <summary>An audit event that cannot be written fails the command closed (D-188): never a copy reported without its event.</summary>
    private static async Task WriteEventAsync(Func<Task> write)
    {
        try { await write(); }
        catch (Exception ex) when (ex is AuditConfigurationException or AuditUnavailableException or Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            throw new BackupException("replica_audit_failed");
        }
    }

    // ---- destination folder ------------------------------------------------------------------------------

    /// <summary>The destination folder exists, or is created owner-only when its parent exists (D-184); false when it can be neither.</summary>
    private static bool EnsureDestinationFolder(CheckedDestination d)
    {
        if (Directory.Exists(d.ResolvedPath)) return true;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(d.ResolvedPath));
        if (parent is null || !Directory.Exists(parent)) return false;
        try { CreateOwnerOnlyDirectory(d.ResolvedPath); return Directory.Exists(d.ResolvedPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes staging folders older than 24 hours (the slice 3 sweep rule); younger ones may belong to a running copy and are left alone.</summary>
    private void SweepStaleStaging(string root)
    {
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
            if (ReplicaStatus.IncomingFolder().IsMatch(dir.Name) && dir.LinkTarget is null && time.GetUtcNow() - new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero) > ReplicaStatus.StaleIncomingAge)
                RemoveBestEffort(dir.FullName);
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // ---- copy ------------------------------------------------------------------------------------------------

    /// <summary>Every file of the run folder (the run is clean: only the files slice 5 writes, no link) with its size and SHA-256, read from the PRIMARY.</summary>
    private static async Task<List<FileRecord>> DescribeAsync(string runDir, CancellationToken ct)
    {
        var list = new List<FileRecord>();
        foreach (var rel in Names(runDir)) list.Add(new(rel, new FileInfo(Path.Combine(runDir, rel.Replace('/', Path.DirectorySeparatorChar))).Length, await HashFileAsync(Path.Combine(runDir, rel.Replace('/', Path.DirectorySeparatorChar)), ct)));
        return list;
    }

    /// <summary>The relative names (slash-separated) of every file under a folder; a link anywhere is a verification failure.</summary>
    private static List<string> Names(string dir)
    {
        var names = new List<string>();
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (e.LinkTarget is not null) throw new ReplicaVerificationException("link");
            if (e is FileInfo) names.Add(Path.GetRelativePath(dir, e.FullName).Replace(Path.DirectorySeparatorChar, '/'));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private async Task<List<FileRecord>> CopyFilesAsync(string runDir, string staging, CancellationToken ct)
    {
        var copied = new List<FileRecord>();
        foreach (var rel in Names(runDir))
        {
            var to = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            CreateOwnerOnlyDirectory(Path.GetDirectoryName(to)!);
            copied.Add(await CopyOneAsync(Path.Combine(runDir, rel.Replace('/', Path.DirectorySeparatorChar)), to, rel, ct));
        }
        return copied;
    }

    /// <summary>Streams one file into its final name inside the staging folder, hashing the bytes READ from the primary, then flushes it to disk and closes it.</summary>
    private async Task<FileRecord> CopyOneAsync(string from, string to, string name, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.SequentialScan };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var dst = new FileStream(to, options))
        {
            var buffer = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                size += n;
            }
            await dst.FlushAsync(ct);
            Hooks?.BeforeFlush?.Invoke(name);
            dst.Flush(flushToDisk: true);                                                  // fsync / FlushFileBuffers: an error fails the copy
        }                                                                                    // the write handle is CLOSED here; verification reopens the file
        return new(name, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Re-verification of a copy that is already published (S8c, D-185 "re-verification of old copies"): the copy alone, against its own SIGNED inventory, without the primary. Returns null when it
    /// verifies, or the failing check: <c>status</c> (the status does not verify), <c>digest_list_missing</c>, <c>digest_list</c> (the inventory does not verify or is not the one the status names),
    /// <c>unexpected_file</c>, <c>link</c>, <c>file_set</c> (an encrypted file the inventory does not list, or a listed one absent), <c>size</c>, <c>hbe_structure</c>, <c>hash</c> or <c>signature</c> (a manifest). Never repairs.
    /// </summary>
    internal static async Task<(string? Check, int Files, long Bytes, string? StatusSha256, bool LinkCheckAvailable)> VerifyStoredAsync(string dir, SigningTrust trust, CancellationToken ct, Func<string, FileIdentityInfo?>? identity = null)
    {
        // review M1 of #214: the run folder itself must be a real folder of this destination. A symbolic link, a junction (reparse point) or a mount point (a bind mount) there can lead to the primary's own run or
        // to any genuine signed run, which would verify as a "copy" that is no independent copy at all. Checked before anything is read.
        var folder = new DirectoryInfo(dir);
        if (folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || BackupDestinations.IsMountPoint(dir)) return ("link", 0, 0, null, true);
        RunStatus status;
        try { status = SignedDocuments.ReadRunStatus(dir, trust); }
        catch (BackupException) { return ("status", 0, 0, null, true); }
        var statusSha = await HashFileAsync(Path.Combine(dir, SignedDocuments.RunStatusFileName), ct);
        CiphertextDigestList list;
        try { list = CiphertextDigests.ReadVerified(dir, status.RunId, status.DigestListSha256, trust); }
        catch (BackupException ex) { return (ex.Code == "replica_digest_list_missing" ? "digest_list_missing" : "digest_list", 0, 0, statusSha, true); }
        try
        {
            var names = Names(dir);
            // a file with more than one hard link may be the very file of the primary's run (or of another run): never an independent copy (Linux; where the link count cannot be read nothing is claimed)
            // a link count that cannot be read is NOT silently skipped (review U2 of PR 214): the result says the check was unavailable and the command warns (link_check_unavailable, exit 8)
            identity ??= FileIdentity.TryGet;
            var linkCheckAvailable = true;
            foreach (var n in names)
            {
                var id = identity(Path.Combine(dir, n.Replace('/', Path.DirectorySeparatorChar)));
                if (id is null) linkCheckAvailable = false;
                else if (id.Value.Links > 1) return ("link", 0, 0, statusSha, true);
            }
            if (!RetentionInventory.Inspect(dir).Clean) return ("unexpected_file", 0, 0, statusSha, true);
            var encrypted = names.Where(n => n.EndsWith(".hbe", StringComparison.Ordinal)).ToList();
            if (!encrypted.Order(StringComparer.Ordinal).SequenceEqual(list.Files.Select(f => f.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal)) return ("file_set", 0, 0, statusSha, true);
            long bytes = 0;
            foreach (var e in list.Files)
            {
                var path = Path.Combine(dir, e.Name.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(path).Length != e.Size) return ("size", 0, 0, statusSha, true);
                string actual;
                try { actual = await HashAndCheckHbeAsync(path, ct); }
                catch (ReplicaVerificationException) { return ("hbe_structure", 0, 0, statusSha, true); }
                if (!string.Equals(actual, e.Sha256, StringComparison.Ordinal)) return ("hash", 0, 0, statusSha, true);
                bytes += e.Size;
                if (e.Name == SignedDocuments.ManifestFileName || e.Name.EndsWith("/" + SignedDocuments.ManifestFileName, StringComparison.Ordinal))
                {
                    try { SignedDocuments.ReadVerified(path, trust); }
                    catch (BackupException) { return ("signature", 0, 0, statusSha, true); }
                }
            }
            return (null, names.Count, bytes, statusSha, linkCheckAvailable);
        }
        catch (ReplicaVerificationException ex) { return (ex.Check, 0, 0, statusSha, true); }
    }

    // ---- verification (read back from the destination through NEW handles) ----------------------------------------

    private async Task VerifyCopyAsync(string dir, IReadOnlyList<FileRecord> expected, string primaryStatusSha256, CancellationToken ct)
    {
        var names = Names(dir);
        var expectedNames = expected.Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
        if (!names.SequenceEqual(expectedNames, StringComparer.Ordinal)) throw new ReplicaVerificationException("file_set");
        // the same cleanliness rule as a run at the primary: only the files slice 5 writes (plus the proof), no link, no other folder
        if (!RetentionInventory.Inspect(dir).Clean) throw new ReplicaVerificationException("unexpected_file");
        foreach (var f in expected)
        {
            var path = Path.Combine(dir, f.Name.Replace('/', Path.DirectorySeparatorChar));
            if (new FileInfo(path).Length != f.Size) throw new ReplicaVerificationException("size");
            string actual;
            if (f.Name.EndsWith(".hbe", StringComparison.Ordinal)) actual = await HashAndCheckHbeAsync(path, ct);
            else actual = await HashFileAsync(path, ct);
            if (!string.Equals(actual, f.Sha256, StringComparison.Ordinal)) throw new ReplicaVerificationException("hash");
        }
        var status = expected.Single(f => f.Name == SignedDocuments.RunStatusFileName);
        if (!string.Equals(status.Sha256, primaryStatusSha256, StringComparison.Ordinal)) throw new ReplicaVerificationException("status_hash");
        // signatures, verified on the COPY against the trust list: the run status, and every manifest over its ciphertext
        foreach (var f in expected.Where(f => f.Name == SignedDocuments.RunStatusFileName || f.Name.EndsWith("/" + SignedDocuments.ManifestFileName, StringComparison.Ordinal) || f.Name == SignedDocuments.ManifestFileName))
        {
            try { SignedDocuments.ReadVerified(Path.Combine(dir, f.Name.Replace('/', Path.DirectorySeparatorChar)), trust); }
            catch (BackupException) { throw new ReplicaVerificationException("signature"); }
        }
    }

    /// <summary>Hashes a file read from the destination after a best-effort request to drop its cached pages (Linux), through a handle opened just now.</summary>
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        DropCachedPages(s);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, n);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>One read pass over an encrypted file: its SHA-256 and its HBE1 structure (header, chunk framing); <c>hbe_malformed</c> and the like become a verification failure.</summary>
    private static async Task<string> HashAndCheckHbeAsync(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        DropCachedPages(s);
        await using var tee = new HashingReadStream(s, hash);
        try { await Hbe1.ComputeDigestAsync(tee, ct); }
        catch (BackupException) { throw new ReplicaVerificationException("hbe_structure"); }
        var buffer = new byte[1 << 16];
        while (await tee.ReadAsync(buffer, ct) > 0) { }                                       // anything after the last chunk is part of the file hash too
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool FilesEqual(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    // ---- operating system helpers ----------------------------------------------------------------------------

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) == 112 /* ERROR_DISK_FULL */ || ex.HResult == 28 /* ENOSPC */ || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    private static void RemoveBestEffort(string? path)
    {
        if (path is null) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next copy sweeps it (older than 24 hours) and backup-status reports it */ }
    }

    private static void TryMove(string from, string to, out string? moved)
    {
        try { Directory.Move(from, to); moved = to; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moved = null; }
    }

    /// <summary>Linux: <c>fsync</c> of a folder so a rename or a new entry survives a crash. Windows has no directory flush; the file flushes of <c>FlushFileBuffers</c> stand. An error is an <see cref="IOException"/>.</summary>
    internal static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd;
        try { fd = Native.open(path, 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return; }
        if (fd < 0) throw new IOException("directory flush failed");
        try { if (Native.fsync(fd) != 0) throw new IOException("directory flush failed"); }
        finally { Native.close(fd); }
    }

    private static void DropCachedPages(FileStream s)
    {
        if (!OperatingSystem.IsLinux()) return;
        try { Native.posix_fadvise((int)s.SafeFileHandle.DangerousGetHandle(), 0, 0, 4 /* POSIX_FADV_DONTNEED */); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* best effort, never relied on */ }
    }

    /// <summary>The number of hard links of a file, or null when it cannot be read (see <see cref="FileIdentity"/>). A null never counts as a link.</summary>
    internal static long? HardLinkCount(string path) => FileIdentity.TryGet(path)?.Links;

    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int posix_fadvise(int fd, long offset, long len, int advice);
    }

    /// <summary>A read-only pass-through stream that feeds every byte read into a hash.</summary>
    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); if (n > 0) hash.AppendData(buffer, offset, n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            if (n > 0) hash.AppendData(buffer.Span[..n]);
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
