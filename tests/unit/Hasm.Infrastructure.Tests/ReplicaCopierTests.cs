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
        public Task CorruptAsync(string runId, BackupDestination d, string check, CancellationToken ct) { Log.Add($"2042 {runId} {d.Id} {check}"); return Task.CompletedTask; }
        public Task RefusedAsync(string? destinationId, string reason, CancellationToken ct) { Log.Add($"2043 {destinationId} {reason}"); return Task.CompletedTask; }
    }

    /// <summary>
    /// A real signed encrypted run. <paramref name="beforeList"/> runs after the encrypted files are written and before the signed ciphertext inventory is made (the inventory then describes the altered
    /// state); <paramref name="withList"/> false writes the run as it was before the inventory existed (the status names none).
    /// </summary>
    private async Task<RetentionRun> MakeRunAsync(string runId, int dumpBytes = 200_000, Action<string>? beforeList = null, bool withList = true)
    {
        var dir = Path.Combine(Primary, runId);
        var system = Path.Combine(dir, "system"); Directory.CreateDirectory(system);
        var entry = await EncryptedSets.EncryptToFileAsync(new MemoryStream(RandomNumberGenerator.GetBytes(dumpBytes)), system, EncryptedSets.DumpName, EncryptedSets.SystemDumpPurpose, pub, default);
        var manifest = new ManifestV2(ManifestV2.CurrentVersion, EncryptedSets.SystemKind, runId, null, T0, pub.KeyId, signer.KeyId, [entry], null, new SystemDetails(null, 34, []));
        await SignedDocuments.WriteManifestAsync(system, manifest, pub, signer);
        beforeList?.Invoke(dir);
        var listSha = withList ? await CiphertextDigests.WriteAsync(dir, runId, signer, default) : null;
        SignedDocuments.WriteRunStatus(dir, new RunStatus(RunStatus.CurrentVersion, RunStatus.KindValue, runId, T0, T0.AddMinutes(3), 180000, "ok", 0, 0, 0, 0, 0, pub.KeyId, signer.KeyId,
            Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))), ["session"], [], true, [], ["a1"], listSha), signer);
        return RetentionInventory.Read(new DirectoryInfo(dir), trust);
    }

    private static void FlipBit(string path) { var b = File.ReadAllBytes(path); b[b.Length / 2] ^= 0x01; File.WriteAllBytes(path, b); }

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
        Assert.Equal([$"2040 {RunA} nas reverified=False files=7"], events.Log);
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
        Assert.Equal([$"2040 {RunA} nas reverified=True files=7"], events.Log);
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
        Assert.Equal([$"2040 {RunA} nas reverified=False files=7", $"2041 {RunA} nas replica_publish_failed status"], events.Log);   // the compensating event carries the 2040's status hash
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
    public async Task A_primary_with_a_malformed_encrypted_file_or_a_manifest_that_does_not_verify_is_not_copied_even_when_its_inventory_describes_it()
    {
        // an encrypted file that is not a valid HBE1 stream, listed as it is: the inventory matches, the structure check refuses
        var run = await MakeRunAsync(RunA, beforeList: dir => File.WriteAllBytes(Path.Combine(dir, "system", EncryptedSets.DumpName), RandomNumberGenerator.GetBytes(4096)));
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_verification_failed"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        // a manifest whose signature does not verify (altered after signing, and listed as altered)
        var run2 = await MakeRunAsync(RunB, beforeList: dir => FlipBit(Path.Combine(dir, "system", SignedDocuments.ManifestFileName)));
        var outcome2 = await Copier().ReplicateAsync(Destination(), Primary, run2, default);
        Assert.Equal(("failed", "replica_verification_failed"), (outcome2.Result, outcome2.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunB)));
        Assert.Empty(Staging());
    }

    [Fact]
    public async Task Ciphertext_altered_in_the_primary_after_signing_is_refused_and_nothing_is_published()
    {
        // review M1 of #213: one bit flipped in the middle of the primary's encrypted dump, HBE1 framing intact
        var run = await MakeRunAsync(RunA);
        FlipBit(Path.Combine(Primary, RunA, "system", EncryptedSets.DumpName));
        var outcome = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_digest_mismatch"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Dest, RunA)));
        Assert.Empty(Staging());
        Assert.Equal([$"2041 {RunA} nas replica_digest_mismatch"], events.Log);                // no 2040: nothing was verified
        // an encrypted file that the inventory does not list (added after the run) is refused as well
        var run2 = await MakeRunAsync(RunB);
        File.Copy(Path.Combine(Primary, RunB, "system", EncryptedSets.DumpName), Path.Combine(Primary, RunB, "canary.hbe"));
        Assert.Equal("replica_digest_mismatch", (await Copier().ReplicateAsync(Destination(), Primary, run2, default)).Code);
        Assert.False(Directory.Exists(Path.Combine(Dest, RunB)));
    }

    [Fact]
    public async Task A_copy_made_earlier_is_not_verified_again_when_the_primary_was_altered_since()
    {
        var run = await MakeRunAsync(RunA);
        Assert.Equal("verified", (await Copier().ReplicateAsync(Destination(), Primary, run, default)).Result);
        FlipBit(Path.Combine(Primary, RunA, "system", EncryptedSets.DumpName));
        events.Log.Clear();
        var again = await Copier().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_digest_mismatch"), (again.Result, again.Code));
        Assert.Equal([$"2041 {RunA} nas replica_digest_mismatch"], events.Log);
    }

    [Fact]
    public async Task A_tampered_or_foreign_or_missing_inventory_fails_closed_with_its_own_code()
    {
        string List(string id) => Path.Combine(Primary, id, CiphertextDigests.FileName);
        // bad signature
        var a = await MakeRunAsync(RunA);
        FlipBit(List(RunA) + ".sig");
        Assert.Equal("replica_digest_list_invalid", (await Copier().ReplicateAsync(Destination(), Primary, a, default)).Code);
        // an altered entry without a new signature
        var b = await MakeRunAsync(RunB);
        File.WriteAllText(List(RunB), File.ReadAllText(List(RunB)).Replace("\"size\": ", "\"size\": 1"));
        Assert.Equal("replica_digest_list_invalid", (await Copier().ReplicateAsync(Destination(), Primary, b, default)).Code);
        // an altered entry re-signed by the signing key: the signed status names the hash of the original, so the swap is found
        var c = await MakeRunAsync("20261123T020000Z-0011aabd");
        var path = List("20261123T020000Z-0011aabd");
        var altered = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\"size\": ", "\"size\": 1"));
        File.Delete(path); File.Delete(path + ".sig");
        SignedDocuments.WriteSigned(path, altered, signer);
        Assert.Equal("replica_digest_list_invalid", (await Copier().ReplicateAsync(Destination(), Primary, c, default)).Code);
        // the list of ANOTHER run (a valid signature, the wrong run)
        var d1 = await MakeRunAsync("20261124T020000Z-0011aabe");
        var d2 = await MakeRunAsync("20261125T020000Z-0011aabf");
        File.Copy(List("20261125T020000Z-0011aabf"), List("20261124T020000Z-0011aabe"), true);
        File.Copy(List("20261125T020000Z-0011aabf") + ".sig", List("20261124T020000Z-0011aabe") + ".sig", true);
        Assert.Equal("replica_digest_list_invalid", (await Copier().ReplicateAsync(Destination(), Primary, d1, default)).Code);
        // a missing inventory file, and a run written before the inventory existed (its status names none): never verified, never copied
        var e = await MakeRunAsync("20261126T020000Z-0011aac0");
        File.Delete(List("20261126T020000Z-0011aac0"));
        Assert.Equal("replica_digest_list_missing", (await Copier().ReplicateAsync(Destination(), Primary, e, default)).Code);
        var old = await MakeRunAsync("20261127T020000Z-0011aac1", withList: false);
        var o = await Copier().ReplicateAsync(Destination(), Primary, old, default);
        Assert.Equal(("failed", "replica_digest_list_missing"), (o.Result, o.Code));
        Assert.Empty(Directory.GetDirectories(Dest));                                          // nothing was published or staged for any of them
        Assert.DoesNotContain(events.Log, l => l.StartsWith("2040", StringComparison.Ordinal));
        Assert.Equal(6, events.Log.Count(l => l.StartsWith("2041", StringComparison.Ordinal)));
        Assert.Equal(d2.RunId, "20261125T020000Z-0011aabf");
    }

    [Fact]
    public async Task Every_folder_created_inside_staging_is_flushed_before_the_copy_is_published()
    {
        var run = await MakeRunAsync(RunA);
        var flushed = new List<string>();
        var hooks = new ReplicaCopyHooks { OnFlushDirectory = p => flushed.Add(p), BeforePublish = (_, _) => flushed.Add("PUBLISH") };
        Assert.Equal("verified", (await Copier(hooks).ReplicateAsync(Destination(), Primary, run, default)).Result);
        var publish = flushed.IndexOf("PUBLISH");
        var before = flushed.Take(publish).Select(p => Path.GetFileName(p)).ToList();
        Assert.Contains("system", before);                                                     // the subfolder of the set
        Assert.Contains(before, n => n.Contains(".incoming-"));                                // and the staging folder itself
        Assert.True(before.IndexOf("system") < before.FindIndex(n => n.Contains(".incoming-")));   // deepest first
        Assert.Equal("nas", Path.GetFileName(flushed[^1]));                                    // and the destination root after the rename
    }

    [Fact]
    public async Task A_destination_swapped_for_a_link_after_the_first_check_is_refused_before_anything_is_written_and_again_before_the_rename()
    {
        var run = await MakeRunAsync(RunA);
        var other = Path.Combine(root, "elsewhere"); Directory.CreateDirectory(other);
        var all = new[] { Destination().Destination };
        ReplicaCopier WithRecheck(ReplicaCopyHooks? hooks = null) => new(trust, events) { Hooks = hooks, Recheck = _ => BackupDestinations.Check(all, Primary, []) };
        // swapped before the copy starts
        Directory.Delete(Dest); Directory.CreateSymbolicLink(Dest, other);
        var first = await WithRecheck().ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "symbolic_link_refused"), (first.Result, first.Code));
        Assert.Empty(Directory.GetFileSystemEntries(other));                                   // nothing was written through the link
        Assert.Equal(["2043 nas symbolic_link_refused"], events.Log);
        // swapped between the verification and the rename (after 2040)
        Directory.Delete(Dest); Directory.CreateDirectory(Dest); events.Log.Clear();
        var real = Path.Combine(root, "nas-real");
        var hooks = new ReplicaCopyHooks { BeforePublish = (_, _) => { Directory.Move(Dest, real); Directory.CreateSymbolicLink(Dest, real); } };
        var second = await WithRecheck(hooks).ReplicateAsync(Destination(), Primary, run, default);
        Assert.Equal(("failed", "replica_publish_failed"), (second.Result, second.Code));
        Assert.False(Directory.Exists(Path.Combine(real, RunA)));
        Assert.Empty(Directory.GetDirectories(real));
        Assert.Equal([$"2040 {RunA} nas reverified=False files=7", $"2041 {RunA} nas replica_publish_failed status"], events.Log);
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
        ReplicaOutcome Ok(string d) => new(d, RunA, "verified", null, 7, 1, 1);
        ReplicaOutcome Bad(string d) => new(d, RunA, "failed", "destination_unreachable", 0, 0, 1);
        Assert.Equal(0, ReplicationService.ReplicationCode([Ok("a"), Ok("b")], RunA, 2));
        Assert.Equal(8, ReplicationService.ReplicationCode([Ok("a"), Bad("b")], RunA, 2));
        Assert.Equal(1, ReplicationService.ReplicationCode([Bad("a"), Bad("b")], RunA, 2));
        Assert.Equal(1, ReplicationService.ReplicationCode([], RunA, 2));                       // nothing verified while destinations are configured
        Assert.Equal(0, ReplicationService.ReplicationCode([], RunA, 0));                       // no destination configured: today's behaviour
        Assert.Equal(0, ReplicationService.ReplicationCode([new ReplicaOutcome("a", RunA, "reverified", null, 3, 1, 1)], RunA, 1));
    }

    // ---- S8c: re-verification of a published copy, alone, against its own signed inventory ----------------------

    private async Task<string> PublishedCopyAsync(string runId = "20261121T020000Z-0011aabb")
    {
        var run = await MakeRunAsync(runId);
        Assert.Equal("verified", (await Copier().ReplicateAsync(Destination(), Primary, run, default)).Result);
        return Path.Combine(Dest, runId);
    }

    [Fact]
    public async Task A_good_copy_verifies_alone_without_the_primary_and_every_damage_is_named_by_its_check()
    {
        var copy = await PublishedCopyAsync();
        Directory.Delete(Path.Combine(Primary, RunA), true);                                    // the primary is not needed
        var good = await ReplicaCopier.VerifyStoredAsync(copy, trust, default);
        Assert.Null(good.Check);
        Assert.Equal(7, good.Files);
        string Dump() => Path.Combine(copy, "system", EncryptedSets.DumpName);
        var original = File.ReadAllBytes(Dump());
        FlipBit(Dump());
        Assert.Equal("hash", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.WriteAllBytes(Dump(), original[..^10]);
        Assert.Equal("size", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.WriteAllBytes(Dump(), RandomNumberGenerator.GetBytes(original.Length));
        Assert.Equal("hbe_structure", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.WriteAllBytes(Dump(), original);
        Assert.Null((await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);       // restored: verifies again
        File.WriteAllText(Path.Combine(copy, "notes.txt"), "x");
        Assert.Equal("unexpected_file", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.Delete(Path.Combine(copy, "notes.txt"));
        File.Copy(Dump(), Path.Combine(copy, "canary.hbe"));                                    // an encrypted file the inventory does not list
        Assert.Equal("file_set", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.Delete(Path.Combine(copy, "canary.hbe"));
        var manifest = Path.Combine(copy, "system", SignedDocuments.ManifestFileName + ".sig");
        FlipBit(manifest);
        Assert.Equal("signature", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
    }

    [Fact]
    public async Task A_copy_with_a_damaged_status_or_a_missing_or_foreign_inventory_is_named_corrupt_with_its_check()
    {
        var copy = await PublishedCopyAsync();
        var list = Path.Combine(copy, CiphertextDigests.FileName);
        var listBytes = File.ReadAllBytes(list); var sigBytes = File.ReadAllBytes(list + ".sig");
        File.Delete(list); File.Delete(list + ".sig");
        Assert.Equal("digest_list_missing", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.WriteAllBytes(list, listBytes); File.WriteAllBytes(list + ".sig", sigBytes);
        FlipBit(list + ".sig");
        Assert.Equal("digest_list", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        File.WriteAllBytes(list + ".sig", sigBytes);
        Assert.Null((await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        FlipBit(Path.Combine(copy, "run.status.json.sig"));
        Assert.Equal("status", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
    }

    private static RetentionRun Run(string id, params (Guid Company, string Outcome)[] companies) =>
        new(id, T0, T0, true, true, true, "ok", "r1", false, [.. companies.Select(c => new RunStatusCompany(c.Company, c.Outcome))], "hash-" + id, true, false, null);

    [Fact]
    public void The_default_selection_is_the_newest_copy_the_newest_per_company_and_held_or_pinned_runs_and_all_selects_every_folder()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var r1 = Run("20261101T020000Z-00000001", (a, "ok"), (b, "ok"));
        var r2 = Run("20261102T020000Z-00000002", (a, "ok"), (b, "failed"));
        var r3 = Run("20261103T020000Z-00000003", (a, "ok"), (c, "ok_missing_keys"));
        var r4 = Run("20261104T020000Z-00000004", (a, "ok"));
        var broken = r2 with { Verified = false, Verdict = null, StatusSha256 = null, Complete = true };
        IReadOnlyList<RetentionRun> all = [r1, broken, r3, r4];
        string[] Ids(IEnumerable<RetentionRun> runs) => [.. runs.Select(r => r.RunId)];
        // newest copy r4; newest with company a is r4, with b is r1, with c is r3; the unverifiable folder is always examined
        Assert.Equal([r1.RunId, r2.RunId, r3.RunId, r4.RunId], Ids(ReplicaVerifyService.Select(all, [], all: false)));
        Assert.Equal([r1.RunId, r2.RunId, r3.RunId, r4.RunId], Ids(ReplicaVerifyService.Select(all, [], all: true)));
        IReadOnlyList<RetentionRun> healthy = [r1, r2, r3, r4];
        Assert.Equal([r1.RunId, r3.RunId, r4.RunId], Ids(ReplicaVerifyService.Select(healthy, [], all: false)));                  // r2 only holds b "failed" and a (newer elsewhere)
        Assert.Equal([r1.RunId, r2.RunId, r3.RunId, r4.RunId], Ids(ReplicaVerifyService.Select(healthy, [r2.RunId], all: false)));  // held or pinned
        Assert.Equal([r1.RunId, r3.RunId, r4.RunId], Ids(ReplicaVerifyService.Select(healthy, ["20200101T000000Z-00000000"], all: false)));   // a hold on a run that is not there
        Assert.Empty(ReplicaVerifyService.Select([], [], all: false));
    }

    [Fact]
    public void The_audit_state_takes_the_last_word_per_copy_parses_tokens_exactly_and_ignores_foreign_rows()
    {
        var t = DateTimeOffset.UtcNow;
        string V(string run, string dest, bool re) => $"replica verified operator=os:x run={run} destination={dest} kind=network status=abc files=1 bytes=1 duration_ms=1 reverified={(re ? "true" : "false")}";
        string C(string run, string dest) => $"replica corrupt operator=os:x run={run} destination={dest} check=hash";
        var r = "20261121T020000Z-0011aabb";
        var state = ReplicaAuditReader.Build([
            (BackupAuditEvents.ReplicaVerified, V(r, "nas-1", false), t.AddHours(-5)),
            (BackupAuditEvents.ReplicaCorrupt, C(r, "nas-1"), t.AddHours(-4)),
            (BackupAuditEvents.ReplicaVerified, V(r, "nas-10", true), t.AddHours(-3)),          // another destination whose id starts with the first
            (BackupAuditEvents.ReplicaVerified, "garbage without tokens", t),
            (BackupAuditEvents.ReplicaVerified, V("not-a-run", "nas-1", true), t),
            (BackupAuditEvents.ReplicaFailed, V(r, "nas-1", true), t),                          // another event type is not read
        ]);
        Assert.True(state.ReportedCorrupt("nas-1", r));
        Assert.False(state.ReportedCorrupt("nas-10", r));                                       // exact match: nas-10 is not nas-1
        Assert.Null(state.LastReverified("nas-1"));
        Assert.Equal(t.AddHours(-3), state.LastReverified("nas-10"));
        // a later verified copy (a fresh copy after the corrupt one was moved away) supersedes the corrupt report
        var cleared = ReplicaAuditReader.Build([(BackupAuditEvents.ReplicaCorrupt, C(r, "nas-1"), t.AddHours(-2)), (BackupAuditEvents.ReplicaVerified, V(r, "nas-1", false), t.AddHours(-1))]);
        Assert.False(cleared.ReportedCorrupt("nas-1", r));
        Assert.Equal("nas-1", ReplicaAuditReader.Token("destination=nas-1 run=x", "destination"));
        Assert.Null(ReplicaAuditReader.Token("xdestination=nas-1", "destination"));
    }

    [Fact]
    public async Task A_run_folder_that_is_a_link_or_holds_a_hard_linked_file_is_never_a_verified_copy()
    {
        // review M1 of #214: the destination's run folder replaced by a symbolic link to the primary's own run
        var copy = await PublishedCopyAsync();
        Assert.Null((await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        var moved = copy + ".moved";
        Directory.Move(copy, moved);
        Directory.CreateSymbolicLink(copy, Path.Combine(Primary, RunA));
        Assert.Equal("link", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);   // the primary's run verifies by itself; as a "copy" it is none
        Directory.Delete(copy); Directory.Move(moved, copy);
        Assert.Null((await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
        if (!OperatingSystem.IsLinux()) return;
        // a file of the copy that is hard-linked to the primary's file shares its storage with it
        var theirs = Path.Combine(Primary, RunA, "system", EncryptedSets.DumpName);
        var ours = Path.Combine(copy, "system", EncryptedSets.DumpName);
        Assert.Equal(1L, ReplicaCopier.HardLinkCount(ours));
        File.Delete(ours);
        using (var ln = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { theirs, ours }, UseShellExecute = false })!) { ln.WaitForExit(); Assert.Equal(0, ln.ExitCode); }
        Assert.Equal(2L, ReplicaCopier.HardLinkCount(ours));
        Assert.Equal("link", (await ReplicaCopier.VerifyStoredAsync(copy, trust, default)).Check);
    }

    [Fact]
    public void A_proc_mount_is_a_mount_point_and_an_ordinary_folder_is_not()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(BackupDestinations.IsMountPoint("/proc"));
        Assert.False(BackupDestinations.IsMountPoint(Dest));
    }

    [Fact]
    public void The_default_selection_adds_held_companies_the_newest_proven_copy_per_key_and_the_newest_recoverable_copy_per_company()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        RetentionRun R(string id, string key, bool proof, params (Guid Company, string Outcome)[] companies) =>
            Run(id, companies) with { RecoveryKeyId = key, ProofValid = proof };
        string[] Ids(IEnumerable<RetentionRun> runs) => [.. runs.Select(r => r.RunId)];
        // held companies: every copy that contains the held company is selected, however old
        var h1 = R("20261101T020000Z-00000001", "k1", false, (a, "ok"));
        var h2 = R("20261102T020000Z-00000002", "k1", false, (b, "ok"));
        var h3 = R("20261103T020000Z-00000003", "k1", false, (b, "ok"));
        var h4 = R("20261104T020000Z-00000004", "k1", false, (b, "ok"));
        Assert.Equal([h1.RunId, h4.RunId], Ids(ReplicaVerifyService.Select([h1, h2, h3, h4], [], false)).Concat([]).Where(x => x == h1.RunId || x == h4.RunId).ToArray());
        Assert.DoesNotContain(h2.RunId, Ids(ReplicaVerifyService.Select([h1, h2, h3, h4], [], false)));
        Assert.Contains(h2.RunId, Ids(ReplicaVerifyService.Select([h1, h2, h3, h4], [], false, heldCompanies: [b])));
        Assert.Contains(h3.RunId, Ids(ReplicaVerifyService.Select([h1, h2, h3, h4], [], false, heldCompanies: [b])));
        // the newest copy with a valid recovery proof, per recovery key (older, other key, proofs on both)
        var p1 = R("20261101T020000Z-00000011", "k1", true, (a, "failed"));
        var p2 = R("20261102T020000Z-00000012", "k1", false, (a, "failed"));
        var p3 = R("20261103T020000Z-00000013", "k2", true, (a, "failed"));
        var p4 = R("20261104T020000Z-00000014", "k2", false, (a, "failed"));
        var proven = Ids(ReplicaVerifyService.Select([p1, p2, p3, p4], [], false));
        Assert.Contains(p1.RunId, proven); Assert.Contains(p3.RunId, proven); Assert.Contains(p4.RunId, proven);   // p4 is the newest copy overall
        Assert.DoesNotContain(p2.RunId, proven);
        // the newest RECOVERABLE copy per company: the newest copy's key was never tested, an older run of a tested key is the one that can be restored
        var q1 = R("20261101T020000Z-00000021", "k1", true, (a, "ok"), (b, "ok"));
        var q2 = R("20261102T020000Z-00000022", "k1", false, (a, "ok"));
        var q3 = R("20261103T020000Z-00000023", "k2", false, (a, "ok"), (b, "ok"));
        var recoverable = Ids(ReplicaVerifyService.Select([q1, q2, q3], [], false));
        Assert.Contains(q1.RunId, recoverable);                                                 // k1 is tested through q1: q2 and q1 are recoverable, q2 is the newest recoverable for a
        Assert.Contains(q2.RunId, recoverable);
        Assert.Contains(q3.RunId, recoverable);                                                 // the newest copy (and the newest of both companies)
    }

    [Fact]
    public void A_2040_compensated_by_a_2041_is_not_the_last_word_and_clears_no_corrupt_report()
    {
        var t = DateTimeOffset.UtcNow; var r = "20261121T020000Z-0011aabb";
        string V(bool re) => $"replica verified operator=os:x run={r} destination=nas kind=network status=abc files=1 bytes=1 duration_ms=1 reverified={(re ? "true" : "false")}";
        var corrupt = $"replica corrupt operator=os:x run={r} destination=nas check=hash";
        string F(string reason, bool status) => $"replica failed operator=os:x run={r} destination=nas reason={reason}{(status ? " status=abc" : "")}";
        // corrupt, then a 2040 whose publish failed (compensated): still corrupt
        var state = ReplicaAuditReader.Build([(BackupAuditEvents.ReplicaCorrupt, corrupt, t.AddHours(-3)), (BackupAuditEvents.ReplicaVerified, V(false), t.AddHours(-2)), (BackupAuditEvents.ReplicaFailed, F("replica_publish_failed", true), t.AddHours(-1))]);
        Assert.True(state.ReportedCorrupt("nas", r));
        // an ordinary 2041 (no status) and a 2041 of another reason change nothing; a real 2040 after them clears the report
        var plain = ReplicaAuditReader.Build([(BackupAuditEvents.ReplicaCorrupt, corrupt, t.AddHours(-3)), (BackupAuditEvents.ReplicaFailed, F("destination_unreachable", false), t.AddHours(-2))]);
        Assert.True(plain.ReportedCorrupt("nas", r));
        var cleared = ReplicaAuditReader.Build([(BackupAuditEvents.ReplicaCorrupt, corrupt, t.AddHours(-3)), (BackupAuditEvents.ReplicaVerified, V(false), t.AddHours(-2))]);
        Assert.False(cleared.ReportedCorrupt("nas", r));
        // a compensated first copy leaves no last word at all
        var none = ReplicaAuditReader.Build([(BackupAuditEvents.ReplicaVerified, V(false), t.AddHours(-2)), (BackupAuditEvents.ReplicaFailed, F("replica_publish_failed", true), t.AddHours(-1))]);
        Assert.False(none.LastKind.ContainsKey(("nas", r)));
    }
}
