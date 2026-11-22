using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 8, S8a (D-186): the severity order 1 &gt; 8 &gt; 0 (never a numeric maximum; refusals never combined), the ONE status function of a destination, and the read-only inspection of a
/// destination folder (unreachable, corrupt run folders, stale staging, unexplained tombstones). Real signed copies are covered by the PostgreSQL class BackupReplicationTests.
/// </summary>
public sealed class ReplicaStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly BackupDestination Nas = new("nas", "/mnt/nas", BackupDestinationKind.Network, null, null, null);
    private static ReplicaState Good(BackupDestination? d = null) => new(d ?? Nas, true, "20260310T020000Z-0a0b0c0d", Now.AddHours(-10), true, [], [], 0, 0, false);
    private static IReadOnlyList<string> Codes(ReplicaState s, string? primaryNewest = "20260310T020000Z-0a0b0c0d") =>
        [.. ReplicaStatus.Contribution(s, primaryNewest, Now, TimeSpan.FromHours(26)).Select(f => $"{f.Code}:{f.ExitCode}")];

    [Fact]
    public void Results_are_combined_by_severity_never_by_numeric_value()
    {
        int[] codes = [0, 8, 1];
        foreach (var a in codes)
            foreach (var b in codes)
                foreach (var c in codes)
                {
                    var expected = new[] { a, b, c }.Contains(1) ? 1 : new[] { a, b, c }.Contains(8) ? 8 : 0;
                    foreach (var order in new[] { new[] { a, b, c }, [c, b, a], [b, a, c] })
                        Assert.Equal(expected, BackupSeverity.Worst(order));
                }
        Assert.Equal(1, BackupSeverity.Worst(8, 1));          // a numeric maximum would give 8
        Assert.Equal(1, BackupSeverity.Worst(1, 8, 8, 0));
        Assert.Equal(0, BackupSeverity.Worst());
        foreach (var refusal in new[] { 2, 3, 4, 5, -1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => BackupSeverity.Worst(0, refusal));   // refusals are returned alone, never combined
    }

    [Fact]
    public void A_good_destination_contributes_nothing_and_every_finding_has_its_severity()
    {
        Assert.Empty(Codes(Good()));
        Assert.Equal(["replica_missing:1"], Codes(Good() with { Reachable = false }));
        Assert.Equal(["replica_missing:1"], Codes(Good() with { NewestCopyRunId = null, NewestCopyFinishedUtc = null, HoldsPrimaryNewest = false }));
        Assert.Equal(["replica_stale:1"], Codes(Good() with { NewestCopyFinishedUtc = Now.AddHours(-27) }));
        Assert.Equal(["replica_lagging:8"], Codes(Good() with { HoldsPrimaryNewest = false }));
        Assert.Equal(["replica_stale:1"], Codes(Good() with { NewestCopyFinishedUtc = Now.AddHours(-27), HoldsPrimaryNewest = false }));   // stale wins over lagging
        Assert.Empty(Codes(Good() with { HoldsPrimaryNewest = false }, primaryNewest: null));                                              // the primary has nothing replicable
        Assert.Equal(["replica_conflict:1"], Codes(Good() with { ConflictRunIds = ["20260309T020000Z-0a0b0c0d"] }));
        Assert.Equal(["replica_corrupt:1"], Codes(Good() with { CorruptRunIds = ["20260309T020000Z-0a0b0c0d"] }));
        Assert.Equal(["replica_tombstone_unexplained:1"], Codes(Good() with { UnexplainedTombstones = 1 }));
        Assert.Equal(["replica_incoming_stale:8"], Codes(Good() with { StaleIncoming = 2 }));
        Assert.Equal(["destination_same_volume:8"], Codes(Good() with { SameVolumeAsPrimary = true }));
    }

    [Fact]
    public void The_destinations_own_max_age_replaces_the_default()
    {
        var strict = Nas with { MaxAgeHours = 6 };
        Assert.Equal(["replica_stale:1"], Codes(Good(strict)));                                         // 10 hours old against 6
        var lenient = Nas with { MaxAgeHours = 72 };
        Assert.Empty(Codes(Good(lenient) with { NewestCopyFinishedUtc = Now.AddHours(-48) }));
    }

    [Fact]
    public void The_overall_status_is_the_worst_of_the_primary_and_every_destination()
    {
        IReadOnlyList<ReplicaFinding> none = [];
        IReadOnlyList<ReplicaFinding> warning = [new("destination_same_volume", 8)];
        IReadOnlyList<ReplicaFinding> failure = [new("replica_missing", 1)];
        Assert.Equal(0, ReplicaStatus.Overall(0, [none, none]));
        Assert.Equal(8, ReplicaStatus.Overall(0, [none, warning]));
        Assert.Equal(8, ReplicaStatus.Overall(8, [none]));                // e.g. recovery_untested at the primary
        Assert.Equal(1, ReplicaStatus.Overall(8, [warning, failure]));    // an exit-1 destination with an exit-8 primary and an exit-8 destination is 1, never 8
        Assert.Equal(1, ReplicaStatus.Overall(1, [none]));
        Assert.Equal(1, ReplicaStatus.Overall(0, [failure, warning]));
    }

    [Fact]
    public void Only_a_finished_verified_encrypted_clean_ok_or_partial_run_is_replicable()
    {
        var at = new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero);
        var good = new RetentionRun("20260301T020000Z-0a0b0c0d", at, at, Complete: true, Verified: true, Encrypted: true, "ok", "r1", false, [], "hash", true, false, null);
        Assert.True(ReplicaStatus.IsReplicable(good));
        Assert.True(ReplicaStatus.IsReplicable(good with { Verdict = "partial" }));
        Assert.False(ReplicaStatus.IsReplicable(good with { Verdict = "failed" }));
        Assert.False(ReplicaStatus.IsReplicable(good with { Complete = false }));
        Assert.False(ReplicaStatus.IsReplicable(good with { Verified = false }));
        Assert.False(ReplicaStatus.IsReplicable(good with { Encrypted = false }));     // never an unencrypted DEV/TEST run (D-088)
        Assert.False(ReplicaStatus.IsReplicable(good with { Clean = false }));
        Assert.False(ReplicaStatus.IsReplicable(good with { StatusSha256 = null }));
    }

    [Fact]
    public void Inspecting_a_folder_finds_missing_unverifiable_runs_stale_staging_and_tombstones()
    {
        var work = Path.Combine(Path.GetTempPath(), "hasm-rst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var missing = ReplicaStatus.Inspect(new CheckedDestination(Nas, Path.Combine(work, "absent"), false, false), [], null, Now);
            Assert.False(missing.Reachable);
            Assert.Equal(["replica_missing:1"], Codes(missing));

            var d = Path.Combine(work, "nas"); Directory.CreateDirectory(d);
            var dest = new CheckedDestination(Nas, d, true, true);
            Assert.Equal(["replica_missing:1", "destination_same_volume:8"], Codes(ReplicaStatus.Inspect(dest, [], null, Now)));

            Directory.CreateDirectory(Path.Combine(d, "20260309T020000Z-0a0b0c0d"));                                   // a run folder with no signed status: never a verified copy
            var fresh = Path.Combine(d, "20260310T020000Z-0a0b0c0d.incoming-0123456789abcdef"); Directory.CreateDirectory(fresh);
            var old = Path.Combine(d, "20260308T020000Z-0a0b0c0d.incoming-0123456789abcdef"); Directory.CreateDirectory(old);
            Directory.SetLastWriteTimeUtc(old, Now.AddHours(-25).UtcDateTime);
            Directory.SetLastWriteTimeUtc(fresh, Now.AddHours(-1).UtcDateTime);
            Directory.CreateDirectory(Path.Combine(d, ".retention", "tombstones", "20260301T020000Z-0a0b0c0d"));
            Directory.CreateDirectory(Path.Combine(d, "20260302T020000Z-0a0b0c0d.deleting-0123456789abcdef"));
            var s = ReplicaStatus.Inspect(dest, [], null, Now);
            Assert.True(s.Reachable);
            Assert.Equal(["20260309T020000Z-0a0b0c0d"], s.CorruptRunIds);
            Assert.Equal(1, s.StaleIncoming);                                                                       // only the one older than 24 hours
            Assert.Equal(2, s.UnexplainedTombstones);                                                               // a tombstone and an unfinished deletion
            Assert.Equal(["replica_missing:1", "replica_corrupt:1", "replica_tombstone_unexplained:1", "replica_incoming_stale:8", "destination_same_volume:8"], Codes(s));
        }
        finally { Directory.Delete(work, true); }
    }
}
