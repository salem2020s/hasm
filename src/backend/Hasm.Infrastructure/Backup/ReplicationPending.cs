namespace Hasm.Infrastructure.Backup;

/// <summary>
/// <c>replication_pending</c> (S8d, D-187): which REPLICABLE runs of the primary some configured destination has no verified copy of. Read from the destination FOLDER, never from an audit event alone; a
/// destination that cannot be read has no copy. The one exception is a deletion at that destination that is fully accounted for (verified tombstone, the 2015/2016 pair, no later copy) and read through an
/// AUTHENTICATED audit state. Read only: this only ever makes the primary keep MORE, and it never touches a file.
/// <para>
/// This is the PREVIEW check (the "light" check of plan section 5.2): the destination folder is present, its signed status verifies and equals the primary's hash, its signed ciphertext inventory verifies and
/// is bound to that status, and the folder is clean. Where the primary root is known, a copy that shares storage with the primary's run (the same folder, or one hard-linked file), or whose file identity
/// cannot be read, is NOT a copy (the rule of <c>replica_not_independent</c>, <see cref="ReplicaIndependence"/>): the run stays pending. It does not re-hash every byte; the apply path (a later slice) re-verifies fully with <c>ReplicaCopier.VerifyStoredAsync</c> before anything is deleted.
/// </para>
/// </summary>
public static class ReplicationPending
{
    public const string Reason = "replication_pending";

    /// <summary>One run's pending state: the destination ids that lack a verified copy of it (empty = not pending).</summary>
    public sealed record Result(IReadOnlyDictionary<string, IReadOnlyList<string>> MissingAt)
    {
        public IReadOnlySet<string> RunIds => new HashSet<string>(MissingAt.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key), StringComparer.Ordinal);
    }

    public static Result Evaluate(IReadOnlyList<RetentionRun> primaryRuns, IReadOnlyList<CheckedDestination> destinations, SigningTrust? trust, ReplicaAuditState? audit,
        IReadOnlyDictionary<string, string>? primaryTombstoneHashes = null, string? primaryRoot = null, Func<string, FileIdentityInfo?>? identityProbe = null)
    {
        var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var replicable = primaryRuns.Where(ReplicaStatus.IsReplicable).ToList();
        var perRun = replicable.ToDictionary(r => r.RunId, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var d in destinations)
        {
            HashSet<string>? explained = null;
            IReadOnlyDictionary<string, RetentionRun>? copies = null;
            if (Directory.Exists(d.ResolvedPath))
            {
                try
                {
                    var (runs, deleting) = RetentionInventory.Scan(d.ResolvedPath, trust);
                    copies = runs.ToDictionary(r => r.RunId, StringComparer.Ordinal);
                    var hashes = new Dictionary<string, string>(primaryTombstoneHashes ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                    foreach (var r in primaryRuns.Where(r => r.StatusSha256 is not null)) hashes[r.RunId] = r.StatusSha256!;
                    explained = audit is { Authenticated: true }
                        ? [.. ReplicaTombstones.Classify(d.ResolvedPath, d.Destination.Id, hashes, trust, audit, deleting).Explained]
                        : [];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { copies = null; }
            }
            foreach (var run in replicable)
            {
                var ok = copies is not null && copies.TryGetValue(run.RunId, out var copy) && LightVerified(d.ResolvedPath, run, copy, trust, audit, d.Destination.Id, primaryRoot, identityProbe);
                if (!ok && copies is not null && explained is not null && explained.Contains(run.RunId) && !copies.ContainsKey(run.RunId)) ok = true;   // the tombstone exception
                if (!ok) perRun[run.RunId].Add(d.Destination.Id);
            }
        }
        foreach (var (run, ids) in perRun) missing[run] = ids;
        return new(missing);
    }

    private static bool LightVerified(string root, RetentionRun primary, RetentionRun copy, SigningTrust? trust, ReplicaAuditState? audit, string destinationId, string? primaryRoot,
        Func<string, FileIdentityInfo?>? identityProbe)
    {
        if (trust is null || !ReplicaStatus.IsReplicable(copy) || !string.Equals(copy.StatusSha256, primary.StatusSha256, StringComparison.Ordinal)) return false;
        if (audit is not null && audit.ReportedCorrupt(destinationId, copy.RunId)) return false;                // the last re-verification reported it corrupt: it no longer counts
        var dir = Path.Combine(root, copy.RunId);
        try
        {
            var status = SignedDocuments.ReadRunStatus(dir, trust);
            CiphertextDigests.ReadVerified(dir, copy.RunId, status.DigestListSha256, trust);                     // signed inventory present and bound to the status
            if (primaryRoot is not null)
            {
                // a copy that shares storage with the primary's run is no copy, and one whose identity cannot be read is not counted either: the run stays pending (keeps more, never less)
                var independence = ReplicaIndependence.Check(dir, Path.Combine(primaryRoot, copy.RunId), identityProbe);
                if (independence.Shares || independence.Unavailable) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException) { return false; }
    }
}
