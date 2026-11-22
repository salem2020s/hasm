using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>What <c>backup-replicate</c> was asked to copy: one run (<see cref="RunId"/>, copied or re-verified even when a copy exists), one destination, and the order of catch-up.</summary>
public sealed record ReplicationRequest(string? RunId = null, string? DestinationId = null, bool NewestFirst = false);

/// <summary>The outcomes of a replication pass, in the order they were attempted, and the number of primary runs that are never replicated (failed, unsigned, unencrypted or unclean).</summary>
public sealed record ReplicationResult(IReadOnlyList<ReplicaOutcome> Outcomes, int NotReplicable);

/// <summary>
/// Replication of the primary root to the configured destinations (D-182, D-185, D-186). The shared restore lock (slice 6) is held for the whole pass, so a retention deletion at the primary cannot
/// remove a run while it is copied; each destination is locked exclusively (<see cref="DestinationLock"/>) so two replicators never write the same destination. One destination's failure never stops the others
/// and never changes the primary run; a failure to write an audit event stops the command (fail closed, D-188).
/// </summary>
public sealed class ReplicationService(HasmDatabaseOptions options, SigningTrust trust, IReplicaEvents events, TimeProvider? clock = null)
{
    /// <summary>How long a destination lock is waited for before <c>destination_busy</c> (exit 4).</summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(30);
    internal ReplicaCopyHooks? Hooks { get; init; }

    public async Task<ReplicationResult> ReplicateAsync(string primaryRoot, IReadOnlyList<CheckedDestination> destinations, ReplicationRequest request, CancellationToken ct = default)
    {
        var selected = request.DestinationId is null ? destinations : [.. destinations.Where(d => d.Destination.Id == request.DestinationId)];
        if (selected.Count == 0) throw new BackupException("destination_unknown");
        if (request.RunId is not null && !BackupRunFiles.IsRunId(request.RunId)) throw new BackupException("invalid_run_id");
        var root = BackupSet.RealPath(primaryRoot);
        await using var restore = await RetentionService.AcquireRestoreLockAsync(options, ct);
        var runs = RetentionInventory.Scan(root, trust).Runs;
        var ordered = runs.OrderBy(r => r.RunId, StringComparer.Ordinal).ToList();
        if (request.NewestFirst) ordered.Reverse();
        RetentionRun? explicitRun = null;
        if (request.RunId is not null) explicitRun = runs.SingleOrDefault(r => r.RunId == request.RunId) ?? throw new BackupException("run_not_found");
        var notReplicable = runs.Count(r => !ReplicaStatus.IsReplicable(r));
        var outcomes = new List<ReplicaOutcome>();
        var copier = new ReplicaCopier(trust, events, clock) { Hooks = Hooks };
        foreach (var d in selected)
        {
            await using var guard = await DestinationLock.AcquireAsync(options, d.Destination.Id, LockWait, ct);
            if (explicitRun is not null)
            {
                if (ReplicaStatus.IsReplicable(explicitRun)) outcomes.Add(await copier.ReplicateAsync(d, root, explicitRun, ct));
                else if (explicitRun is { Complete: true, Encrypted: false })
                {
                    // an unencrypted DEV/TEST run is never copied (D-088, D-095): a copy would multiply plaintext
                    await WriteAsync(() => events.FailedAsync(explicitRun.RunId, d.Destination, "replica_unencrypted_refused", null, ct));
                    outcomes.Add(new(d.Destination.Id, explicitRun.RunId, "failed", "replica_unencrypted_refused", 0, 0, 0));
                }
                else throw new BackupException("run_not_replicable");
                continue;
            }
            foreach (var run in ordered.Where(ReplicaStatus.IsReplicable))
            {
                var final = Path.Combine(d.ResolvedPath, run.RunId);
                if (Directory.Exists(final))
                {
                    var existing = RetentionInventory.Read(new DirectoryInfo(final), trust);
                    if (ReplicaStatus.IsReplicable(existing) && string.Equals(existing.StatusSha256, run.StatusSha256, StringComparison.Ordinal))
                    {
                        try { await copier.BringProofAsync(d, root, run, ct); }       // a verified copy is not copied again; only a later recovery-test.json is brought over
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ReplicaVerificationException) { /* reported by the next verification */ }
                        continue;
                    }
                }
                outcomes.Add(await copier.ReplicateAsync(d, root, run, ct));
            }
        }
        return new(outcomes, notReplicable);
    }

    private static async Task WriteAsync(Func<Task> write)
    {
        try { await write(); }
        catch (Exception ex) when (ex is Hasm.Infrastructure.Audit.AuditConfigurationException or Hasm.Infrastructure.Audit.AuditUnavailableException or NpgsqlException or InvalidOperationException or TimeoutException)
        { throw new BackupException("replica_audit_failed"); }
    }

    /// <summary>
    /// The replication code of ONE run (D-186): 0 when every configured destination verified its copy, 8 when one or more did not, 1 when none did while at least one is configured. A run that is
    /// not replicated at all (nothing configured) has no replication code (0).
    /// </summary>
    public static int ReplicationCode(IEnumerable<ReplicaOutcome> outcomes, string runId, int configuredDestinations)
    {
        if (configuredDestinations == 0) return 0;
        var verified = outcomes.Where(o => o.RunId == runId).Select(o => o.DestinationId).Where(id => outcomes.Any(o => o.RunId == runId && o.DestinationId == id && o.IsVerified)).Distinct().Count();
        return verified >= configuredDestinations ? 0 : verified == 0 ? 1 : 8;
    }
}

/// <summary>
/// The exclusive per-destination advisory lock (D-185 step 1): the run-lock namespace with object <c>0x10000</c> plus a 16-bit stable hash of the destination id. A hash collision only serialises two
/// destinations. Waits up to a bound and then refuses with <c>destination_busy</c> (exit 4). Disposal unlocks explicitly before the session closes.
/// </summary>
public sealed class DestinationLock : IAsyncDisposable
{
    private readonly NpgsqlConnection conn;
    private readonly int obj;
    private bool disposed;

    private DestinationLock(NpgsqlConnection conn, int obj) { this.conn = conn; this.obj = obj; }

    /// <summary>The lock object of a destination: <c>0x10000</c> plus the first 16 bits of the SHA-256 of its id.</summary>
    public static int ObjectOf(string destinationId) => 0x10000 + BitConverter.ToUInt16(SHA256.HashData(Encoding.UTF8.GetBytes(destinationId)), 0);

    public static async Task<DestinationLock> AcquireAsync(HasmDatabaseOptions options, string destinationId, TimeSpan wait, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(options.BuildSystemConnectionString(admin: true));
        try
        {
            await conn.OpenAsync(ct);
            var obj = ObjectOf(destinationId);
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@ns, @o)", conn);
                cmd.Parameters.AddWithValue("ns", BackupRunService.RunLockNamespace);
                cmd.Parameters.AddWithValue("o", obj);
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return new DestinationLock(conn, obj);
                if (DateTime.UtcNow >= deadline) throw new BackupException("destination_busy");
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
        catch { await conn.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@ns, @o)", conn);
            unlock.Parameters.AddWithValue("ns", BackupRunService.RunLockNamespace);
            unlock.Parameters.AddWithValue("o", obj);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { /* a broken session ends its locks with the backend anyway */ }
        finally { await conn.DisposeAsync(); }
    }
}
