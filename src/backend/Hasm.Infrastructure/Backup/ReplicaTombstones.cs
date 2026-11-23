using System.Security.Cryptography;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// What a destination's tombstones and unfinished deletions mean (S8d, D-187; read only). <see cref="Explained"/> are run ids whose deletion at the destination is fully accounted for (the three-condition
/// exception: the run is NOT pending there); <see cref="Incomplete"/> are deletions HASM started and did not finish; <see cref="Unexplained"/> are tombstones and <c>.deleting-*</c> folders that nothing in
/// the audit stream accounts for (never resumed, never touched, alerted).
/// </summary>
public sealed record TombstoneScan(IReadOnlyList<string> Explained, IReadOnlyList<string> Incomplete, IReadOnlyList<string> Unexplained)
{
    public static readonly TombstoneScan None = new([], [], []);
}

public static class ReplicaTombstones
{
    /// <summary>
    /// The status hash of the tombstone of <paramref name="runId"/> under <paramref name="root"/>/.retention/tombstones, or null when it is absent, is not a plain folder, or its signed status does not verify
    /// against the trust list. Never writes.
    /// </summary>
    public static string? VerifiedHash(string root, string runId, SigningTrust? trust)
    {
        if (trust is null || !BackupRunFiles.IsRunId(runId)) return null;
        var dir = new DirectoryInfo(Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, runId));
        if (!dir.Exists || dir.LinkTarget is not null) return null;
        try
        {
            var status = SignedDocuments.ReadRunStatus(dir.FullName, trust);
            if (!string.Equals(status.RunId, runId, StringComparison.Ordinal)) return null;
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir.FullName, SignedDocuments.RunStatusFileName))));
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The verified status hash of every tombstone at the PRIMARY root, by run id: the hash a destination's tombstone must equal when the primary has since deleted the run itself.</summary>
    public static IReadOnlyDictionary<string, string> PrimaryHashes(string primaryRoot, SigningTrust? trust)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var dir = Path.Combine(primaryRoot, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder);
        if (trust is null || !Directory.Exists(dir)) return map;
        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
            if (VerifiedHash(primaryRoot, d.Name, trust) is { } h) map[d.Name] = h;
        return map;
    }

    /// <summary>
    /// Classifies every tombstone and <c>.deleting-*</c> folder of the destination root (the rules of D-187, plan section 5.3). With no audit state (<paramref name="audit"/> null) nothing can be explained and
    /// every one is unexplained (the S8a behaviour). <paramref name="primaryHashes"/> is the status hash of each run id at the primary (its run folder, or its verified tombstone).
    /// </summary>
    public static TombstoneScan Classify(string root, string destinationId, IReadOnlyDictionary<string, string> primaryHashes, SigningTrust? trust, ReplicaAuditState? audit,
        IReadOnlyList<string> deletingFolders)
    {
        var tombstoneRoot = Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder);
        var explained = new SortedSet<string>(StringComparer.Ordinal);
        var incomplete = new SortedSet<string>(StringComparer.Ordinal);
        var unexplained = new SortedSet<string>(StringComparer.Ordinal);
        string Folder(string run) => Path.Combine(root, run);
        bool FolderExists(string run) => Directory.Exists(Folder(run));
        bool Deleting(string run) => deletingFolders.Any(f => f.StartsWith(run + ".deleting-", StringComparison.Ordinal));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (Directory.Exists(tombstoneRoot))
            foreach (var entry in new DirectoryInfo(tombstoneRoot).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var run = entry.Name;
                seen.Add(run);
                // condition 1: a plain folder, named like a run, whose signed status verifies and whose hash is the primary's
                var hash = entry is DirectoryInfo && BackupRunFiles.IsRunId(run) ? VerifiedHash(root, run, trust) : null;
                if (audit is null || hash is null || !primaryHashes.TryGetValue(run, out var primary) || !string.Equals(primary, hash, StringComparison.Ordinal) || !audit.HasStart(destinationId, run, hash))
                { unexplained.Add(run); continue; }
                var folderThere = FolderExists(run); var deleting = Deleting(run);
                if (folderThere && !deleting) continue;                                           // a matching 2015 and the folder still present: nothing to complete, an ordinary copy
                if (audit.OpenStartSeq(destinationId, run, hash) is not null || deleting) { incomplete.Add(run); continue; }
                // the pair exists; condition 2: no 2040 after the pair's 2015; condition 3: no folder, no .deleting-*
                if (audit.CompletedPairSeq(destinationId, run, hash) is { } pair && !audit.CopiedAfter(destinationId, run, pair)) explained.Add(run);
            }
        foreach (var f in deletingFolders)
        {
            var run = f.Length >= 25 ? f[..25] : f;
            if (!seen.Contains(run)) unexplained.Add(run);                                         // an unfinished deletion with no tombstone that HASM wrote
        }
        return new([.. explained], [.. incomplete], [.. unexplained]);
    }
}
