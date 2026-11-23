namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The deletion-readiness lines and the <c>Deletion blocked:</c> notice (S8d section 5.9; read only). HASM has no push channel, so a blocked deletion is made unmistakable through what exists: the output of
/// <c>backup-status</c> and <c>backup-retention-plan</c>, with every reason code and the location it concerns (a location is <c>primary</c> or a destination id; never a path). While the code gate is
/// closed (always, today) the first reason is always <c>retention_deletion_inactive</c>: readiness never reads as ready.
/// </summary>
public static class DeletionReadiness
{
    /// <summary>The reasons deletion is blocked at one location, in a fixed order; empty only when nothing blocks it (never true while the gate is closed).</summary>
    public static IReadOnlyList<string> Reasons(RetentionOptions options, bool anyDestinationConfigured, ReplicaAuditState? audit, bool auditReadable, int pendingRunCount)
    {
        var reasons = new List<string>();
        if (!BackupRetention.AutomaticDeletionActivated) reasons.Add("retention_deletion_inactive");
        if (!options.OptedIn) reasons.Add("opt_in_absent");
        if (!RestoreSafeguards.CutoverAvailable) reasons.Add("restore_safeguards_missing");
        if (!anyDestinationConfigured) reasons.Add("destination_protection_missing");
        if (!auditReadable) reasons.Add("audit_unreadable");
        else if (anyDestinationConfigured && audit is not { Authenticated: true }) reasons.Add("audit_unverified");
        else if (audit is { UnverifiedRemainder: true }) reasons.Add("audit_unverified");
        if (pendingRunCount > 0) reasons.Add("replication_pending");
        return reasons;
    }

    /// <summary>One line per location: <c>Deletion readiness: primary: blocked (a, b)</c>. Codes only.</summary>
    public static IReadOnlyList<string> Lines(IEnumerable<(string Location, IReadOnlyList<string> Reasons)> locations) =>
        [.. locations.Select(l => $"Deletion readiness: {l.Location}: {(l.Reasons.Count == 0 ? "ready" : "blocked (" + string.Join(", ", l.Reasons) + ")")}")];

    /// <summary>The <c>Deletion blocked:</c> block (empty when no location is blocked): every reason code with its location.</summary>
    public static IReadOnlyList<string> Notice(IEnumerable<(string Location, IReadOnlyList<string> Reasons)> locations)
    {
        var blocked = locations.Where(l => l.Reasons.Count > 0).ToList();
        if (blocked.Count == 0) return [];
        var lines = new List<string> { "Deletion blocked:" };
        foreach (var (location, reasons) in blocked) lines.AddRange(reasons.Select(r => $"  {r} location={location}"));
        return lines;
    }

    /// <summary>
    /// The readiness of the primary and of every configured destination (read only; reads no database itself: the audit state is given). The primary also reports the runs some destination has no verified
    /// copy of (<c>replication_pending</c>); a destination also reports <c>destination_unreachable</c> when its folder cannot be read.
    /// </summary>
    public static IReadOnlyList<(string Location, IReadOnlyList<string> Reasons)> Locations(RetentionOptions options, IReadOnlyList<CheckedDestination> destinations, string primaryRoot,
        SigningTrust? trust, ReplicaAuditState? audit, bool auditReadable)
    {
        var resolved = BackupSet.RealPath(primaryRoot);
        var runs = RetentionInventory.Scan(resolved, trust).Runs;
        var live = destinations.Select(d => d with { Exists = Directory.Exists(d.ResolvedPath) }).ToList();
        var pending = ReplicationPending.Evaluate(runs, live, trust, audit, ReplicaTombstones.PrimaryHashes(resolved, trust)).RunIds.Count;
        var locations = new List<(string, IReadOnlyList<string>)> { ("primary", Reasons(options, live.Count > 0, audit, auditReadable, pending)) };
        foreach (var d in live)
        {
            var reasons = Reasons(options, live.Count > 0, audit, auditReadable, 0).ToList();
            if (!d.Exists) reasons.Add("destination_unreachable");
            locations.Add((d.Destination.Id, reasons));
        }
        return locations;
    }
}
