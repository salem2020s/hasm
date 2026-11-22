namespace Hasm.Infrastructure.Backup;

/// <summary>One destination's state, findings and the console lines every command prints for it.</summary>
public sealed record DestinationReport(CheckedDestination Destination, ReplicaState State, IReadOnlyList<ReplicaFinding> Findings, IReadOnlyList<string> Lines)
{
    public int ExitCode => BackupSeverity.Worst(Findings.Select(f => f.ExitCode));
}

/// <summary>
/// The status of every configured destination through the ONE status function (D-186), shared by <c>backup-status</c>, <c>backup-run</c> and <c>backup-replicate</c> so the same state prints the same
/// lines and has the same severity in each (review L1 of #176: <c>backup-run</c> is never milder than <c>backup-status</c> for the same destinations).
/// </summary>
public static class ReplicaStatusReport
{
    public static IReadOnlyList<DestinationReport> Evaluate(IReadOnlyList<CheckedDestination> destinations, string primaryRoot, SigningTrust? trust, DateTimeOffset now, TimeSpan defaultMaxAge)
    {
        var primaryRuns = RetentionInventory.Scan(BackupSet.RealPath(primaryRoot), trust).Runs;
        var primaryNewest = primaryRuns.Where(ReplicaStatus.IsReplicable).OrderByDescending(r => r.RunId, StringComparer.Ordinal).FirstOrDefault()?.RunId;
        var reports = new List<DestinationReport>();
        foreach (var d in destinations)
        {
            var state = ReplicaStatus.Inspect(d, primaryRuns, trust, now);
            var findings = ReplicaStatus.Contribution(state, primaryNewest, now, defaultMaxAge);
            var lines = new List<string>
            {
                $"Destination {d.Destination.Id} ({d.Destination.KindName}): {(state.Reachable ? "reachable" : "NOT reachable or missing")}; newest verified copy "
                + (state.NewestCopyRunId is { } copy ? $"{copy}, finished {(now - state.NewestCopyFinishedUtc!.Value).TotalHours:0.0} hour(s) ago" : "none")
                + $"; primary's newest replicable run {(primaryNewest is null ? "none" : state.HoldsPrimaryNewest ? "present" : "NOT present")}; result exit {BackupSeverity.Worst(findings.Select(f => f.ExitCode))}.",
            };
            lines.AddRange(findings.Select(f => $"  [{(f.ExitCode == 1 ? "FAILURE" : "WARNING")}] {f.Code} destination={d.Destination.Id}"));
            reports.Add(new(d, state, findings, lines));
        }
        return reports;
    }
}
