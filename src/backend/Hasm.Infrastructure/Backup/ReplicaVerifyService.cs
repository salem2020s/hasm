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
    /// The default selection at one destination, a conservative superset of what retention hard-keeps (review M2 of #214), needing no company database for the selection itself:
    /// the newest replicable copy; the newest copy of every company; the newest copy WITH a valid recovery proof per recovery key; the newest RECOVERABLE copy per company (among runs whose recovery key has
    /// a valid proof); every copy of a run a legal hold or a retention pin names; every copy that contains a company in <paramref name="heldCompanies"/> (an unreleased company-scoped legal hold, dates
    /// ignored, or an attachment-hold keep, or a company that could not be read); and any run folder whose status does not verify. <paramref name="all"/> selects every run folder there.
    /// </summary>
    public static IReadOnlyList<RetentionRun> Select(IReadOnlyList<RetentionRun> copies, IEnumerable<string> protectedRunIds, bool all, IEnumerable<Guid>? heldCompanies = null)
    {
        if (all) return [.. copies.OrderBy(r => r.RunId, StringComparer.Ordinal)];
        var replicable = copies.Where(ReplicaStatus.IsReplicable).OrderByDescending(r => r.RunId, StringComparer.Ordinal).ToList();
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        bool Ok(RunStatusCompany c) => c.Outcome.StartsWith("ok", StringComparison.Ordinal);
        if (replicable.Count > 0) chosen.Add(replicable[0].RunId);
        foreach (var company in replicable.SelectMany(r => r.Companies).Where(Ok).Select(c => c.CompanyId).Distinct())
            chosen.Add(replicable.First(r => r.Companies.Any(c => c.CompanyId == company && Ok(c))).RunId);
        // the newest copy that carries a valid recovery proof, per recovery key
        var proven = replicable.Where(r => r.ProofValid && r.RecoveryKeyId is not null).ToList();
        foreach (var key in proven.Select(r => r.RecoveryKeyId!).Distinct(StringComparer.Ordinal)) chosen.Add(proven.First(r => r.RecoveryKeyId == key).RunId);
        // the newest RECOVERABLE copy per company: its recovery key has a valid proof (on any copy here) and the company's outcome is ok
        var testedKeys = proven.Select(r => r.RecoveryKeyId!).ToHashSet(StringComparer.Ordinal);
        var recoverable = replicable.Where(r => r.RecoveryKeyId is not null && testedKeys.Contains(r.RecoveryKeyId)).ToList();
        foreach (var company in recoverable.SelectMany(r => r.Companies).Where(Ok).Select(c => c.CompanyId).Distinct())
            chosen.Add(recoverable.First(r => r.Companies.Any(c => c.CompanyId == company && Ok(c))).RunId);
        foreach (var id in protectedRunIds) if (copies.Any(r => r.RunId == id)) chosen.Add(id);
        var held = heldCompanies?.ToHashSet() ?? [];
        if (held.Count > 0) foreach (var r in copies.Where(r => r.Companies.Any(c => held.Contains(c.CompanyId)))) chosen.Add(r.RunId);
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
        var (protectedIds, companyHolds) = await ReadHoldsAsync(ct);
        var outcomes = new List<ReplicaVerifyOutcome>();
        foreach (var d in selected)
        {
            await using var guard = await DestinationLock.AcquireAsync(options, d.Destination.Id, LockWait, ct);
            if (!Directory.Exists(d.ResolvedPath)) { outcomes.Add(new(d.Destination.Id, "-", "unreadable", "destination_unreachable", false)); continue; }
            IReadOnlyList<RetentionRun> copies;
            try { copies = RetentionInventory.Scan(d.ResolvedPath, trust).Runs; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { outcomes.Add(new(d.Destination.Id, "-", "unreadable", "destination_unreachable", false)); continue; }
            foreach (var copy in Select(copies, protectedIds, request.All, await HeldCompaniesAsync(copies, companyHolds, request.All, ct)))
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

    /// <summary>Run ids named by an unreleased legal hold or a retention pin, and the companies named by an unreleased company-scoped hold (dates ignored: a superset).</summary>
    private async Task<(IReadOnlyList<string> RunIds, IReadOnlySet<Guid> Companies)> ReadHoldsAsync(CancellationToken ct)
    {
        try
        {
            await using var sys = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
            await sys.OpenAsync(ct);
            var holds = await BackupHoldStore.ListAsync(sys, ct);
            var pins = await BackupHoldStore.ListPinsAsync(sys, ct);
            return ([.. holds.Where(h => !h.Released && h.RunId is not null).Select(h => h.RunId!), .. pins.Select(p => p.RunId)],
                holds.Where(h => !h.Released && h.CompanyId is not null).Select(h => h.CompanyId!.Value).ToHashSet());
        }
        catch (NpgsqlException) { throw new BackupException("hold_check_failed"); }
    }

    /// <summary>The companies whose copies are always selected: the company-scoped holds, plus every company of these copies that has an attachment-hold keep or cannot be read (retention keeps an unreadable company's runs).</summary>
    private async Task<IReadOnlyList<Guid>> HeldCompaniesAsync(IReadOnlyList<RetentionRun> copies, IReadOnlySet<Guid> companyHolds, bool all, CancellationToken ct)
    {
        var held = new HashSet<Guid>(companyHolds);
        if (all) return [.. held];
        var service = new RetentionService(options, new RetentionOptions());
        foreach (var id in copies.SelectMany(r => r.Companies).Select(c => c.CompanyId).Distinct().Where(id => !held.Contains(id)))
        {
            var company = await service.ReadCompanyAsync(id, ct);
            if (!company.Readable || company.HoldCode is not null) held.Add(id);
        }
        return [.. held];
    }

    private static async Task WriteAsync(Func<Task> write)
    {
        try { await write(); }
        catch (Exception ex) when (ex is Hasm.Infrastructure.Audit.AuditConfigurationException or Hasm.Infrastructure.Audit.AuditUnavailableException or NpgsqlException or InvalidOperationException or TimeoutException)
        { throw new BackupException("replica_audit_failed"); }
    }
}
