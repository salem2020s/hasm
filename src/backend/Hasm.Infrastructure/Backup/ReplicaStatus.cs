using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The severity order of the backup commands' result codes (D-186, review M1 of #176): failure (exit 1) is worse than warning (exit 8), which is worse than ok (exit 0), written 1 &gt; 8 &gt; 0.
/// Results are combined by this order, never by their numeric value (a numeric maximum would turn a failure into a warning). The refusals 2 (usage), 3 (configuration) and 4 (lock held) are returned
/// alone and are never combined: passing one is a programming error.
/// </summary>
public static class BackupSeverity
{
    public static int Rank(int exitCode) => exitCode switch
    {
        0 => 0,
        8 => 1,
        1 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(exitCode), "Only the result codes 0, 8 and 1 are combined; 2, 3 and 4 are refusals and are returned alone."),
    };

    /// <summary>The worst of the given result codes by severity (1 &gt; 8 &gt; 0); 0 when there are none.</summary>
    public static int Worst(IEnumerable<int> exitCodes)
    {
        var worst = 0;
        foreach (var c in exitCodes) if (Rank(c) > Rank(worst)) worst = c;
        return worst;
    }

    public static int Worst(params int[] exitCodes) => Worst((IEnumerable<int>)exitCodes);
}

/// <summary>One finding of a destination's status contribution (D-186): a stable code and its result code (1 or 8).</summary>
public sealed record ReplicaFinding(string Code, int ExitCode);

/// <summary>
/// What the status function needs to know about one destination (read from its folder by <see cref="ReplicaStatus.Inspect"/>, or given by a test):
/// whether the folder could be read; its newest VERIFIED copy (run id and finish time); whether it holds the primary's newest replicable run; the run ids whose copy carries another status hash
/// than the primary's (<c>replica_conflict</c>); run folders that do not verify (<c>replica_corrupt</c>); staging folders older than 24 hours; tombstones and unfinished deletions that nothing explains
/// yet (<c>replica_tombstone_unexplained</c>); and whether it shares the primary's volume.
/// </summary>
public sealed record ReplicaState(BackupDestination Destination, bool Reachable, string? NewestCopyRunId, DateTimeOffset? NewestCopyFinishedUtc, bool HoldsPrimaryNewest,
    IReadOnlyList<string> ConflictRunIds, IReadOnlyList<string> CorruptRunIds, int StaleIncoming, int UnexplainedTombstones, bool SameVolumeAsPrimary)
{
    /// <summary>Copies the system audit stream last reported corrupt by a re-verification (2042) while their folder is still there (S8c); empty when the audit stream was not read.</summary>
    public IReadOnlyList<string> ReportedCorruptRunIds { get; init; } = [];
    /// <summary>When a copy at this destination was last RE-verified (a 2040 with <c>reverified=true</c>); null when never or when the audit stream was not read.</summary>
    public DateTimeOffset? LastReverifiedUtc { get; init; }
}

/// <summary>
/// The ONE status function of slice 8 (D-186): the contribution of a configured destination, used by every command that reports or depends on it, so the same state has the same severity
/// everywhere. Read-only; never decrypts; needs no database in this slice (see <see cref="Inspect"/>).
/// </summary>
public static partial class ReplicaStatus
{
    public static readonly TimeSpan StaleIncomingAge = TimeSpan.FromHours(24);

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\.incoming-[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    internal static partial Regex IncomingFolder();

    /// <summary>
    /// The findings of one destination, worst first is NOT implied (combine with <see cref="BackupSeverity.Worst(IEnumerable{int})"/>):
    /// <list type="bullet">
    /// <item>exit 1 <c>replica_missing</c>: no verified copy (also when the folder cannot be read or does not exist);</item>
    /// <item>exit 1 <c>replica_stale</c>: the newest verified copy finished longer ago than the destination's <c>MaxAgeHours</c> (default <paramref name="defaultMaxAge"/>);</item>
    /// <item>exit 8 <c>replica_lagging</c>: a fresh copy exists but not of the primary's newest replicable run (<paramref name="primaryNewestReplicable"/>);</item>
    /// <item>exit 1 <c>replica_conflict</c>, <c>replica_corrupt</c>, <c>replica_tombstone_unexplained</c>;</item>
    /// <item>exit 8 <c>replica_incoming_stale</c> and <c>destination_same_volume</c>.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<ReplicaFinding> Contribution(ReplicaState s, string? primaryNewestReplicable, DateTimeOffset now, TimeSpan defaultMaxAge)
    {
        var findings = new List<ReplicaFinding>();
        var maxAge = s.Destination.MaxAgeHours is { } h ? TimeSpan.FromHours(h) : defaultMaxAge;
        if (!s.Reachable || s.NewestCopyRunId is null || s.NewestCopyFinishedUtc is null) findings.Add(new("replica_missing", 1));
        else if (now - s.NewestCopyFinishedUtc.Value > maxAge) findings.Add(new("replica_stale", 1));
        else if (primaryNewestReplicable is not null && !s.HoldsPrimaryNewest) findings.Add(new("replica_lagging", 8));
        if (s.ConflictRunIds.Count > 0) findings.Add(new("replica_conflict", 1));
        if (s.CorruptRunIds.Count > 0 || s.ReportedCorruptRunIds.Count > 0) findings.Add(new("replica_corrupt", 1));
        if (s.UnexplainedTombstones > 0) findings.Add(new("replica_tombstone_unexplained", 1));
        if (s.StaleIncoming > 0) findings.Add(new("replica_incoming_stale", 8));
        if (s.SameVolumeAsPrimary) findings.Add(new("destination_same_volume", 8));
        return findings;
    }

    /// <summary>The OVERALL status (D-186): the worst (1 &gt; 8 &gt; 0) of the primary's root status and every destination's findings.</summary>
    public static int Overall(int primaryRootExitCode, IEnumerable<IReadOnlyList<ReplicaFinding>> destinations) =>
        BackupSeverity.Worst(destinations.SelectMany(f => f.Select(x => x.ExitCode)).Prepend(primaryRootExitCode));

    /// <summary>A run that is replicated (D-182): finished, its signed status verified, encrypted, verdict <c>ok</c> or <c>partial</c>, and holding only the files a run holds.</summary>
    public static bool IsReplicable(RetentionRun r) => r is { Complete: true, Verified: true, Encrypted: true, Clean: true, StatusSha256: not null, Verdict: "ok" or "partial" };

    /// <summary>
    /// Reads one destination folder (read-only, no database): every run folder there is read through its signed status, exactly as at the primary (<see cref="RetentionInventory.Scan"/>).
    /// A copy counts as VERIFIED when it is replicable and, if the primary still holds that run id, carries the primary's status hash; a different hash is a conflict, and a run folder that does not
    /// verify is corrupt (a destination folder only ever holds published, verified copies, D-185). The full re-hash of every file is <c>backup-replica-verify</c> (later slice).
    /// Any tombstone under <c>.retention/tombstones</c> or any <c>.deleting-*</c> folder counts as unexplained: no deletion at a destination exists in this slice, and the audit evidence that can
    /// explain one (the 2015/2016 pair with the destination id, D-187) is read when retention per destination is built.
    /// </summary>
    public static ReplicaState Inspect(CheckedDestination d, IReadOnlyList<RetentionRun> primaryRuns, SigningTrust? trust, DateTimeOffset now, ReplicaAuditState? audit = null)
    {
        ReplicaState Unreachable() => new(d.Destination, false, null, null, false, [], [], 0, 0, d.SameVolumeAsPrimary);
        if (!d.Exists) return Unreachable();
        try { return Read(d, primaryRuns, trust, now, audit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Unreachable(); }   // an unreadable share holds no verified copy
    }

    private static ReplicaState Read(CheckedDestination d, IReadOnlyList<RetentionRun> primaryRuns, SigningTrust? trust, DateTimeOffset now, ReplicaAuditState? audit)
    {
        var (copies, deletingFolders) = RetentionInventory.Scan(d.ResolvedPath, trust);
        var primaryHash = primaryRuns.Where(r => r.StatusSha256 is not null).ToDictionary(r => r.RunId, r => r.StatusSha256!, StringComparer.Ordinal);
        var newestPrimary = primaryRuns.Where(IsReplicable).OrderByDescending(r => r.RunId, StringComparer.Ordinal).FirstOrDefault()?.RunId;
        var verified = new List<RetentionRun>();
        var conflicts = new List<string>();
        var corrupt = new List<string>();
        var reported = new List<string>();
        foreach (var c in copies)
        {
            if (!IsReplicable(c)) { corrupt.Add(c.RunId); continue; }
            // S8c: a copy that the last re-verification reported corrupt no longer counts as a verified copy while its folder is there (the operator moves it away; it stays evidence)
            if (audit is not null && audit.ReportedCorrupt(d.Destination.Id, c.RunId)) { reported.Add(c.RunId); continue; }
            if (primaryHash.TryGetValue(c.RunId, out var h) && !string.Equals(h, c.StatusSha256, StringComparison.Ordinal)) { conflicts.Add(c.RunId); continue; }
            verified.Add(c);
        }
        var newest = verified.OrderByDescending(r => r.RunId, StringComparer.Ordinal).FirstOrDefault();
        var root = new DirectoryInfo(d.ResolvedPath);
        var staleIncoming = root.EnumerateDirectories().Count(x => IncomingFolder().IsMatch(x.Name) && now - new DateTimeOffset(x.LastWriteTimeUtc, TimeSpan.Zero) > StaleIncomingAge);
        var tombstones = Path.Combine(d.ResolvedPath, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder);
        var unexplained = deletingFolders.Count + (Directory.Exists(tombstones) ? new DirectoryInfo(tombstones).EnumerateFileSystemInfos().Count() : 0);
        return new(d.Destination, true, newest?.RunId, newest?.FinishedUtc, newestPrimary is not null && verified.Any(v => v.RunId == newestPrimary), conflicts, corrupt, staleIncoming, unexplained,
            d.SameVolumeAsPrimary) { ReportedCorruptRunIds = reported, LastReverifiedUtc = audit?.LastReverified(d.Destination.Id) };
    }
}
