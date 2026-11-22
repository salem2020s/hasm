using System.Diagnostics;
using Hasm.Infrastructure.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>What <c>backup-replica-verify</c> was asked to check: one destination, and every copy (<see cref="All"/>) instead of the default selection.</summary>
public sealed record ReplicaVerifyRequest(string? DestinationId = null, bool All = false);

/// <summary>The result for one copy: <c>verified</c>, <c>corrupt</c> (with the failing <see cref="Check"/>, a stable code) or <c>unreadable</c> (the destination could not be read).</summary>
public sealed record ReplicaVerifyOutcome(string DestinationId, string RunId, string Result, string? Check, bool Reported)
{
    public bool IsVerified => Result == "verified";
}

/// <summary>
/// Re-verification of the copies at the destinations (slice 8, S8c; D-185 "re-verification of old copies"). Each selected copy is checked alone against its own SIGNED inventory (no primary needed):
/// signatures, the inventory's binding to the signed status, every encrypted file's size, SHA-256 and HBE1 structure, and the folder's cleanliness (<see cref="ReplicaCopier.VerifyStoredAsync"/>). A copy
/// that verifies writes 2040 with <c>reverified=true</c>; one that does not writes 2042 (once, while it stays the last word on that copy) and is NEVER repaired, moved or deleted: the operator moves it
/// away and the next <c>backup-replicate</c> writes a fresh copy. Takes the shared restore lock and each destination's exclusive lock like a copy does. Never decrypts.
/// </summary>
public sealed class ReplicaVerifyService(HasmDatabaseOptions options, SigningTrust trust, IReplicaEvents events)
{
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The default selection at one destination: the newest replicable copy, the newest copy of every company (a company counts in a run whose outcome starts with <c>ok</c>), and every copy of a run a legal
    /// hold or a retention pin names. <paramref name="all"/> selects every run folder there, replicable or not.
    /// </summary>
    public static IReadOnlyList<RetentionRun> Select(IReadOnlyList<RetentionRun> copies, IEnumerable<string> protectedRunIds, bool all)
    {
        if (all) return [.. copies.OrderBy(r => r.RunId, StringComparer.Ordinal)];
        var replicable = copies.Where(ReplicaStatus.IsReplicable).OrderByDescending(r => r.RunId, StringComparer.Ordinal).ToList();
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        if (replicable.Count > 0) chosen.Add(replicable[0].RunId);
        foreach (var company in replicable.SelectMany(r => r.Companies).Where(c => c.Outcome.StartsWith("ok", StringComparison.Ordinal)).Select(c => c.CompanyId).Distinct())
            chosen.Add(replicable.First(r => r.Companies.Any(c => c.CompanyId == company && c.Outcome.StartsWith("ok", StringComparison.Ordinal))).RunId);
        foreach (var id in protectedRunIds) if (copies.Any(r => r.RunId == id)) chosen.Add(id);
        // a run folder that does not even verify its status is always examined: it is the first thing an operator needs to know
        foreach (var r in copies.Where(r => !ReplicaStatus.IsReplicable(r) && BackupRunFiles.IsRunId(r.RunId))) chosen.Add(r.RunId);
        return [.. copies.Where(r => chosen.Contains(r.RunId)).OrderBy(r => r.RunId, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<ReplicaVerifyOutcome>> VerifyAsync(IReadOnlyList<CheckedDestination> destinations, ReplicaVerifyRequest request, CancellationToken ct = default)
    {
        var selected = request.DestinationId is null ? destinations : [.. destinations.Where(d => d.Destination.Id == request.DestinationId)];
        if (selected.Count == 0) throw new BackupException("destination_unknown");
        await using var restore = await RetentionService.AcquireRestoreLockAsync(options, ct);
        var audit = await ReplicaAuditReader.ReadAsync(options.BuildSystemConnectionString(admin: true), ct);
        var protectedIds = await ReadProtectedRunIdsAsync(ct);
        var outcomes = new List<ReplicaVerifyOutcome>();
        foreach (var d in selected)
        {
            await using var guard = await DestinationLock.AcquireAsync(options, d.Destination.Id, LockWait, ct);
            if (!Directory.Exists(d.ResolvedPath)) { outcomes.Add(new(d.Destination.Id, "-", "unreadable", "destination_unreachable", false)); continue; }
            IReadOnlyList<RetentionRun> copies;
            try { copies = RetentionInventory.Scan(d.ResolvedPath, trust).Runs; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { outcomes.Add(new(d.Destination.Id, "-", "unreadable", "destination_unreachable", false)); continue; }
            foreach (var copy in Select(copies, protectedIds, request.All))
            {
                var watch = Stopwatch.StartNew();
                var dir = Path.Combine(d.ResolvedPath, copy.RunId);
                (string? Check, int Files, long Bytes, string? StatusSha256) result;
                try { result = await ReplicaCopier.VerifyStoredAsync(dir, trust, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { outcomes.Add(new(d.Destination.Id, copy.RunId, "unreadable", "destination_unreachable", false)); continue; }
                if (result.Check is null)
                {
                    await WriteAsync(() => events.VerifiedAsync(copy.RunId, result.StatusSha256!, d.Destination, result.Files, result.Bytes, (long)watch.Elapsed.TotalMilliseconds, true, ct));
                    outcomes.Add(new(d.Destination.Id, copy.RunId, "verified", null, false));
                }
                else
                {
                    // reported once: a copy that is already the last word as corrupt is not reported again every night
                    var already = audit.ReportedCorrupt(d.Destination.Id, copy.RunId);
                    if (!already) await WriteAsync(() => events.CorruptAsync(copy.RunId, d.Destination, result.Check, ct));
                    outcomes.Add(new(d.Destination.Id, copy.RunId, "corrupt", result.Check, !already));
                }
            }
        }
        return outcomes;
    }

    private async Task<IReadOnlyList<string>> ReadProtectedRunIdsAsync(CancellationToken ct)
    {
        try
        {
            await using var sys = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
            await sys.OpenAsync(ct);
            var holds = await BackupHoldStore.ListAsync(sys, ct);
            var pins = await BackupHoldStore.ListPinsAsync(sys, ct);
            return [.. holds.Where(h => !h.Released && h.RunId is not null).Select(h => h.RunId!), .. pins.Select(p => p.RunId)];
        }
        catch (NpgsqlException) { throw new BackupException("hold_check_failed"); }
    }

    private static async Task WriteAsync(Func<Task> write)
    {
        try { await write(); }
        catch (Exception ex) when (ex is Hasm.Infrastructure.Audit.AuditConfigurationException or Hasm.Infrastructure.Audit.AuditUnavailableException or NpgsqlException or InvalidOperationException or TimeoutException)
        { throw new BackupException("replica_audit_failed"); }
    }
}
