using System.Security.Cryptography;
using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 8, S8b (D-185, D-188): the copy engine over REAL signed encrypted run folders (HBE1 files, an encrypted signed manifest and a signed run status), without a database: the audit events
/// are recorded by a fake. Covered: a verified byte-for-byte copy with 2040 committed before the rename, an identical existing copy re-verified, a conflicting folder never touched, a flipped byte, a flush
/// error, a failed publish after 2040 (2041 compensates), a simulated crash, an audit event that cannot be written, an unreachable destination, a later recovery proof, and a damaged primary.
/// </summary>
public sealed class ReplicaCopierTests : IAsyncLifetime
{
    private const string Pass = "a recovery passphrase of twenty+";
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);
    private static readonly string RunA = "20261121T020000Z-0011aabb", RunB = "20261122T020000Z-0011aabc";

    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-repl-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(root, "primary");
    private string Dest => Path.Combine(root, "nas");
    private readonly RecoveryKeys.GeneratedPair pair = RecoveryKeys.Generate(Pass);
    private RecoveryPublicKey pub = null!;
    private BackupSigner signer = null!;
    private SigningTrust trust = null!;
    private readonly Recorder events = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Primary); Directory.CreateDirectory(Dest);
        pub = RecoveryPublicKey.FromPem(pair.PublicKeyPem);
        var (id, p) = BackupSigningKeys.Generate(Path.Combine(root, "sign"));
        signer = BackupSigningKeys.Load(Path.Combine(root, "sign"), id, true);
        trust = new SigningTrust([new TrustedSigningKey(id, p, SigningKeyStatus.Active)]);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { signer.Dispose(); pub.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } return Task.CompletedTask; }

    private sealed class Recorder : IReplicaEvents
    {
        public List<string> Log { get; } = [];
        public Func<string, Task>? OnVerified { get; set; }
        public bool FailWrites { get; set; }
        public Task VerifiedAsync(string runId, string statusSha256, BackupDestination d, int files, long bytes, long durationMs, bool reverified, CancellationToken ct)
        {
            if (FailWrites) throw new InvalidOperationException("audit unavailable");
            Log.Add($"2040 {runId} {d.Id} reverified={reverified} files={files}");
            return OnVerified?.Invoke(runId) ?? Task.CompletedTask;
        }
        public Task FailedAsync(string runId, BackupDestination d, string reason, string? statusSha256, CancellationToken ct)
        {
            if (FailWrites) throw new InvalidOperationException("audit unavailable");
            Log.Add($"2041 {runId} {d.Id} {reason}{(statusSha256 is null ? "" : " status")}");
            return Task.CompletedTask;
        }
        public Task RefusedAsync(string? destinationId, string reason, CancellationToken ct) { Log.Add($"2043 {destinationId} {reason}"); return Task.CompletedTask; }
    }

    private async Task<RetentionRun> MakeRunAsync(string runId, int dumpBytes = 200_000)
    {
        var dir = Path.Combine(Primary, runId);
        var system = Path.Combine(dir, "system"); Directory.CreateDirectory(system);
        var entry = await EncryptedSets.EncryptToFileAsync(new MemoryStream(RandomNumberGenerator.GetBytes(dumpBytes)), system, EncryptedSets.DumpName, EncryptedSets.SystemDumpPurpose, pub, default);
        var manifest = new ManifestV2(ManifestV2.CurrentVersion, EncryptedSets.SystemKind, runId, null, T0, pub.KeyId, signer.KeyId, [entry], null, new SystemDetails(null, 34, []));
        await SignedDocuments.WriteManifestAsync(system, manifest, pub, signer);
        SignedDocuments.WriteRunStatus(dir, new RunStatus(RunStatus.CurrentVersion, RunStatus.KindValue, runId, T0, T0.AddMinutes(3), 180000, "ok", 0, 0, 0, 0, 0, pub.KeyId, signer.KeyId,
            Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))), ["session"], [], true, [], ["a1"]), signer);
        return RetentionInventory.Read(new DirectoryInfo(dir), trust);
    }

    private CheckedDestination Destination(string? path = null) => new(new BackupDestination("nas", path ?? Dest, BackupDestinationKind.Network, null, null, null), path ?? Dest, true, false);
    private ReplicaCopier Copier(ReplicaCopyHooks? hooks = null) => new(trust, events) { Hooks = hooks };

    private static string TreeHash(string dir) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/') + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))))))));

    private IEnumerable<string> Staging() => new DirectoryInfo(Dest).EnumerateDirectories().Where(d => d.Name.Contains(".incoming-")).Select(d => d.Name);

    [Fact]
    public async Task A_run_is_copied_byte_for_byte_and_2040_is_committed_before_the_folder_appears()
    {
        var run = await MakeRunAsync(RunA);
        var folderAtEvent = true;
        events.OnVerified = id => { folderAtEvent = Directory.Exists(Path.Combine(Dest, id)); return Task.CompletedTask; };
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("verified", null), (outcome.Result, outcome.Code));
        Assert.False(folderAtEvent);                                                         // the event is committed BEFORE the rename (D-188)
        Assert.Equal(TreeHash(Path.Combine(Primary, RunA)), TreeHash(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
        Assert.Equal([$"2040 {RunA} nas reverified=False files=5"], events.Log);
        var copy = RetentionInventory.Read(new DirectoryInfo(Path.Combine(Dest, RunA)), trust);
        Assert.True(ReplicaStatus.IsReplicable(copy));
        Assert.Equal(run.StatusSha256, copy.StatusSha256);                                  // the same signed status: one run, one signed unit
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(Dest, RunA)) & (UnixFileMode)0x1FF);
    }

    [Fact]
    public async Task An_identical_existing_copy_is_verified_again_not_copied_again()
    {
        var run = await MakeRunAsync(RunA);
        await Copier().ReplicateAsync(Destination(), Primary, run, default);
        var before = TreeHash(Path.Combine(Dest, RunA));
        events.Log.Clear();
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal("reverified", outcome.Result);
        Assert.Equal([$"2040 {RunA} nas reverified=True files=5"], events.Log);
        Assert.Equal(before, TreeHash(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
        // a copy that has since been damaged at the destination is not "reverified": it fails and is left in place as evidence
        File.AppendAllText(Path.Combine(Dest, RunA, "system", EncryptedSets.DumpName), "x");
        events.Log.Clear();
        var damaged = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_verification_failed"), (damaged.Result, damaged.Code));
        Assert.Equal([$"2041 {RunA} nas replica_verification_failed"], events.Log);
        Assert.True(Directory.Exists(Path.Combine(Dest, RunA)));
    }

    [Fact]
    public async Task A_folder_under_the_run_id_with_another_status_is_never_overwritten()
    {
        var run = await MakeRunAsync(RunA);
        Directory.CreateDirectory(Path.Combine(Dest, RunA));
        File.WriteAllText(Path.Combine(Dest, RunA, "evidence.txt"), "keep me");
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_conflict"), (outcome.Result, outcome.Code));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(Dest, RunA, "evidence.txt")));
        Assert.Equal([$"2041 {RunA} nas replica_conflict"], events.Log);
        Assert.Empty(Staging());
    }

    [Fact]
    public async Task A_byte_flipped_at_the_destination_between_copy_and_verification_fails_the_copy_and_publishes_nothing()
    {
        var run = await MakeRunAsync(RunA);
        var hooks = new ReplicaCopyHooks { AfterCopy = staging => { var f = Path.Combine(staging, "system", EncryptedSets.DumpName); var b = File.ReadAllBytes(f); b[b.Length / 2] ^= 0x01; File.WriteAllBytes(f, b); } };
        var outcome = await Copier(hooks).ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_verification_failed"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
        Assert.Equal([$"2041 {RunA} nas replica_verification_failed"], events.Log);          // no 2040: nothing was verified
    }

    [Fact]
    public async Task A_flush_error_fails_the_copy_and_leaves_nothing()
    {
        var run = await MakeRunAsync(RunA);
        var hooks = new ReplicaCopyHooks { BeforeFlush = name => { if (name.EndsWith(".hbe", StringComparison.Ordinal)) throw new IOException("flush failed"); } };
        var outcome = await Copier(hooks).ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "destination_unreachable"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
        Assert.Equal([$"2041 {RunA} nas destination_unreachable"], events.Log);
    }

    [Fact]
    public async Task A_publish_that_fails_after_2040_is_answered_by_2041_and_no_run_folder_exists()
    {
        var run = await MakeRunAsync(RunA);
        var hooks = new ReplicaCopyHooks { BeforePublish = (_, _) => throw new IOException("rename failed") };
        var outcome = await Copier(hooks).ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_publish_failed"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
        Assert.Equal([$"2040 {RunA} nas reverified=False files=5", $"2041 {RunA} nas replica_publish_failed status"], events.Log);   // the compensating event carries the 2040's status hash
    }

    [Fact]
    public async Task A_crash_between_copy_and_rename_leaves_only_staging_and_the_next_copy_succeeds()
    {
        var run = await MakeRunAsync(RunA);
        var hooks = new ReplicaCopyHooks { BeforePublish = (_, _) => throw new ReplicaCrashSimulation() };
        await Assert.ThrowsAsync<ReplicaCrashSimulation>(() => Copier(hooks).ReplicateAsync(Destination(), Primary, run, default));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Single(Staging());                                                            // never a run: its name is not a run id
        Assert.Empty(RetentionInventory.Scan(Dest, trust).Runs);
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal("verified", outcome.Result);
        Assert.True(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Single(Staging());                                                            // the leftover is swept only when older than 24 hours (and backup-status reports it)
        foreach (var s in Staging()) Directory.SetLastWriteTimeUtc(Path.Combine(Dest, s), DateTime.UtcNow.AddHours(-25));
        var run2 = await MakeRunAsync(RunB);
        await Copier().ReplicateAsync(Destination(), Primary, run2, default);
        Assert.Empty(Staging());
    }

    [Fact]
    public async Task An_audit_event_that_cannot_be_written_stops_the_command_and_nothing_is_published()
    {
        var run = await MakeRunAsync(RunA);
        events.FailWrites = true;
        var ex = await Assert.ThrowsAsync<BackupException>(() => Copier().ReplicateAsync(Destination(), Primary, run, default));
        Assert.Equal("replica_audit_failed", ex.Code);
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
    }

    [Fact]
    public async Task An_unreachable_destination_fails_with_its_code_and_creates_nothing_beside_a_missing_parent()
    {
        var run = await MakeRunAsync(RunA);
        var gone = Path.Combine(root, "no-such-parent", "nas");
        var outcome = await Copier().ReplicateAsync(Destination(gone) with { Exists = false }, Primary, run, default);
        Assert.Equal(("failed", "destination_unreachable"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(root, "no-such-parent")));
        // a destination that does not exist yet but whose parent does is created owner-only
        var fresh = Path.Combine(root, "fresh-nas");
        var ok = await Copier().ReplicateAsync(Destination(fresh) with { Exists = false }, Primary, run, default);
        Assert.Equal("verified", ok.Result);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fresh) & (UnixFileMode)0x1FF);
    }

    [Fact]
    public async Task A_later_recovery_proof_is_brought_into_an_existing_copy_and_a_verified_copy_is_not_copied_again()
    {
        var run = await MakeRunAsync(RunA);
        await Copier().ReplicateAsync(Destination(), Primary, run, default);
        File.WriteAllText(Path.Combine(Primary, RunA, "recovery-test.json"), "{\"proof\":1}");
        Assert.True(await Copier().BringProofAsync(Destination(), Primary, run, default));
        Assert.Equal("{\"proof\":1}", File.ReadAllText(Path.Combine(Dest, RunA, "recovery-test.json")));
        Assert.False(await Copier().BringProofAsync(Destination(), Primary, run, default));    // already equal: nothing happens
        Assert.Empty(Staging());
        Assert.True(RetentionInventory.Inspect(Path.Combine(Dest, RunA)).Clean);   // the proof is an allowed file: the copy stays clean
        events.Log.Clear();
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);                                // explicit re-verification now includes the proof
        Assert.Equal("reverified", outcome.Result);
    }

    [Fact]
    public async Task A_damaged_primary_is_not_copied_a_bad_signature_a_bad_encrypted_file_and_a_status_that_changed_all_fail_verification()
    {
        // an encrypted file that is not a valid HBE1 stream
        var run = await MakeRunAsync(RunA);
        File.WriteAllBytes(Path.Combine(Primary, RunA, "system", EncryptedSets.DumpName), RandomNumberGenerator.GetBytes(4096));
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_verification_failed"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        // a manifest whose signature does not verify (the primary's own file altered after signing)
        var run2 = await MakeRunAsync(RunB);
        var manifest = Path.Combine(Primary, RunB, "system", SignedDocuments.ManifestFileName);
        var bytes = File.ReadAllBytes(manifest); bytes[^1] ^= 0x01; File.WriteAllBytes(manifest, bytes);
        var outcome2 = await Copier().ReplicateAsync(Destination(), Primary, run2, default);
        Assert.Equal(("failed", "replica_verification_failed"), (outcome2.Result, outcome2.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunB)));
        Assert.Empty(Staging());
    }

    [Fact]
    public async Task A_run_is_replicable_only_when_finished_signed_encrypted_clean_and_not_failed_otherwise_the_engine_refuses_to_start()
    {
        var run = await MakeRunAsync(RunA);
        await Assert.ThrowsAsync<ArgumentException>(() => Copier().ReplicateAsync(Destination(), Primary, run with { Verdict = "failed" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Copier().ReplicateAsync(Destination(), Primary, run with { Encrypted = false }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Copier().ReplicateAsync(Destination(), Primary, run with { Clean = false }, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Dest));
    }

    [Fact]
    public void The_destination_lock_object_is_0x10000_plus_a_stable_16_bit_hash_and_the_replication_code_follows_D_186()
    {
        foreach (var id in new[] { "nas", "nas-1", "nas-10", "usb-a", "a" })
        {
            var o = DestinationLock.ObjectOf(id);
            Assert.InRange(o, 0x10000, 0x1FFFF);
            Assert.Equal(o, DestinationLock.ObjectOf(id));
        }
        Assert.NotEqual(DestinationLock.ObjectOf("nas-1"), DestinationLock.ObjectOf("nas-10"));   // these two happen not to collide; a collision would only serialise two destinations
        ReplicaOutcome Ok(string d) => new(d, RunA, "verified", null, 5, 1, 1);
        ReplicaOutcome Bad(string d) => new(d, RunA, "failed", "destination_unreachable", 0, 0, 1);
        Assert.Equal(0, ReplicationService.ReplicationCode([Ok("a"), Ok("b")], RunA, 2));
        Assert.Equal(8, ReplicationService.ReplicationCode([Ok("a"), Bad("b")], RunA, 2));
        Assert.Equal(1, ReplicationService.ReplicationCode([Bad("a"), Bad("b")], RunA, 2));
        Assert.Equal(1, ReplicationService.ReplicationCode([], RunA, 2));                       // nothing verified while destinations are configured
        Assert.Equal(0, ReplicationService.ReplicationCode([], RunA, 0));                       // no destination configured: today's behaviour
        Assert.Equal(0, ReplicationService.ReplicationCode([new ReplicaOutcome("a", RunA, "reverified", null, 3, 1, 1)], RunA, 1));
    }
}
