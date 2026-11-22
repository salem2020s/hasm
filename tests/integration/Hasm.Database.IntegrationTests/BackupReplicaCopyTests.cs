using System.Text.RegularExpressions;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 8, S8b (D-182, D-185, D-186, D-188) on the disposable PostgreSQL server: the copy engine, <c>backup-replicate</c> and replication at the end of <c>backup-run</c>, over REAL encrypted signed
/// runs, with audit 2040, 2041 and 2043 read back from the system stream. Every temp folder lies on one volume, so a destination always reports <c>destination_same_volume</c> (exit 8, the only finding of a
/// healthy copy here); the failure paths run through the service with the test seams. Deletion is not involved. Runs in the backup collection.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class BackupReplicaCopyTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for tests";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-brc-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private string AuditKeys => Path.Combine(work, "audit-keys");
    private string SigningDir => Path.Combine(work, "signing");
    private string Root => Path.Combine(work, "runs");
    private string Nas => Path.Combine(work, "nas");
    private string Usb => Path.Combine(work, "usb");
    private RecoveryKeys.GeneratedPair pair = null!;
    private string publicKeyFile = null!, signingId = null!, signingPem = null!;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };
    private AttachmentOptions Attachments => new() { StorageDirectory = Storage };
    private SigningTrust Trust => new([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Active)]);
    private BackupMaterial Material() => new(RecoveryPublicKey.FromPem(pair.PublicKeyPem), BackupSigningKeys.Load(SigningDir, signingId, true));
    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);
    private AuditTrail Trail() => AuditTrail.FromOptions(new AuditOptions { KeyDirectory = AuditKeys, ActiveKeyId = "a1", CheckpointDirectory = Path.Combine(work, "checkpoints"), AllowInsecureFilePermissions = true }, TimeProvider.System);
    private ReplicationService Service(TimeSpan? wait = null, ReplicaCopyHooks? hooks = null) =>
        new(Options, Trust, new SystemReplicaEvents(SystemConnection, Trail())) { LockWait = wait ?? TimeSpan.FromSeconds(5), Hooks = hooks };

    private Dictionary<string, string?> Env(params (string Key, string Value)[] destinations)
    {
        var env = new Dictionary<string, string?>
        {
            ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
            ["Hasm__Audit__KeyDirectory"] = AuditKeys, ["Hasm__Audit__ActiveKeyId"] = "a1", ["Hasm__Audit__CheckpointDirectory"] = Path.Combine(work, "checkpoints"),
            ["Hasm__Backup__RecoveryPublicKeyFile"] = publicKeyFile, ["Hasm__Backup__SigningKeyDirectory"] = SigningDir, ["Hasm__Backup__SigningKeyId"] = signingId,
            ["Hasm__Backup__AllowInsecureFilePermissions"] = "true",
        };
        foreach (var (k, v) in destinations) env["Hasm__Backup__Destinations__" + k] = v;
        return env;
    }

    private Dictionary<string, string?> TwoDestinations() => Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"), ("1__Id", "usb"), ("1__Path", Usb), ("1__Kind", "local"));
    private Dictionary<string, string?> NasOnly() => Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"));

    private async Task Init()
    {
        Directory.CreateDirectory(work); Directory.CreateDirectory(Storage);
        FileKekRing.Generate(Keys, "k1");
        FileAuditKeyRing.Generate(AuditKeys, "a1");
        pair = RecoveryKeys.Generate(Pass);
        RecoveryKit.Write(Path.Combine(work, "recovery"), pair);
        publicKeyFile = Path.Combine(work, "recovery", pair.KeyId + ".pub.pem");
        (signingId, signingPem) = BackupSigningKeys.Generate(SigningDir);
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        var registry = new CompanyRegistry(SystemConnection, NewTestName);
        var company = await registry.RegisterPendingAsync();
        await new CompanyProvisioner(Options, registry).ProvisionAsync(company.Id);
    }

    /// <summary>One encrypted run into the primary root through the service (no destination involved); returns its run id.</summary>
    private async Task<string> RunAsync()
    {
        var (report, dir) = await new BackupRunService(Options, Attachments, Encryption, TimeProvider.System).RunAsync(Root, Material());
        Assert.Equal("ok", report.Verdict);
        return Path.GetFileName(dir);
    }

    private CheckedDestination Destination(string id, string path, string kind = "network") =>
        new(new BackupDestination(id, path, kind == "local" ? BackupDestinationKind.Local : BackupDestinationKind.Network, null, null, null), path, Directory.Exists(path), false);

    private async Task<List<string>> Events(short type)
    {
        await using var conn = new Npgsql.NpgsqlConnection(SystemConnection);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT {E.Detail} FROM {E.Table} WHERE {E.StreamKey} = @s AND {E.EventType} = @t ORDER BY {E.Seq}", conn);
        cmd.Parameters.AddWithValue("s", AuditStream.SystemKey);
        cmd.Parameters.AddWithValue("t", type);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private static string Tree(string dir) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/') + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))))))));

    /// <summary>The exact value of one <c>key=value</c> token of an audit detail (the tokens are parsed exactly, never matched with LIKE).</summary>
    private static string Token(string detail, string key) => detail.Split(' ').Single(t => t.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];

    [PostgresFact]
    public async Task Backup_run_replicates_to_two_destinations_verified_byte_for_byte_with_2040_each_and_the_same_severity_as_backup_status()
    {
        await Init();
        var run = await AdminTool.RunWith(Options, TwoDestinations(), "backup-run", Root);
        // every folder of this test lies on one volume: the only finding is the same-volume warning, so the exit is 8 (never 0) and nothing failed
        Assert.True(run.Exit == 8, run.All);
        Assert.Contains("VERIFIED", run.Out);
        Assert.DoesNotContain("FAILED", run.Out);
        Assert.Equal(2, Regex.Matches(run.Out, @"\[WARNING\] destination_same_volume").Count);
        Assert.DoesNotContain("[FAILURE]", run.Out);
        var runId = Directory.GetDirectories(Root).Single().Split(Path.DirectorySeparatorChar).Last();
        Assert.Equal(Tree(Path.Combine(Root, runId)), Tree(Path.Combine(Nas, runId)));
        Assert.Equal(Tree(Path.Combine(Root, runId)), Tree(Path.Combine(Usb, runId)));
        Assert.Empty(Directory.GetDirectories(Nas).Where(d => d.Contains(".incoming-")));
        var verified = await Events(BackupAuditEvents.ReplicaVerified);
        Assert.Equal(2, verified.Count);
        var primaryHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, runId, "run.status.json"))));
        Assert.Equal(["nas", "usb"], verified.Select(v => Token(v, "destination")).Order());
        Assert.All(verified, v => { Assert.Equal(runId, Token(v, "run")); Assert.Equal(primaryHash, Token(v, "status")); Assert.Equal("false", Token(v, "reverified")); Assert.DoesNotContain(work, v); });
        Assert.Empty(await Events(BackupAuditEvents.ReplicaFailed));
        // backup-status says the same (same function): exit 8 with the same-volume warning only
        var status = await AdminTool.RunWith(Options, TwoDestinations(), "backup-status", Root);
        Assert.Equal(8, status.Exit);
        Assert.DoesNotContain("[FAILURE]", status.Out);
    }

    [PostgresFact]
    public async Task One_destination_unreachable_without_an_earlier_copy_makes_backup_run_exit_1_and_2041_names_it()
    {
        await Init();
        var gone = Path.Combine(work, "no-such-parent", "usb");
        var env = Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"), ("1__Id", "usb"), ("1__Path", gone), ("1__Kind", "local"));
        var run = await AdminTool.RunWith(Options, env, "backup-run", Root);
        Assert.True(run.Exit == 1, run.All);
        Assert.Contains("replication_incomplete", run.Err);
        Assert.Contains("[FAILURE] replica_missing destination=usb", run.Out);
        Assert.Contains("destination_unreachable", run.Out);
        Assert.False(Directory.Exists(Path.Combine(work, "no-such-parent")));
        var failed = await Events(BackupAuditEvents.ReplicaFailed);
        var one = Assert.Single(failed);
        Assert.Equal(("usb", "destination_unreachable"), (Token(one, "destination"), Token(one, "reason")));
        Assert.Single(await Events(BackupAuditEvents.ReplicaVerified));                       // the other destination verified its copy
        var status = await AdminTool.RunWith(Options, env, "backup-status", Root);
        Assert.Equal(1, status.Exit);                                                         // backup-run is never milder than backup-status
        Assert.Contains("[FAILURE] replica_missing destination=usb", status.Out);
    }

    [PostgresFact]
    public async Task A_destination_that_missed_a_run_but_holds_the_previous_one_is_lagging_and_backup_replicate_catches_up_oldest_first()
    {
        await Init();
        var first = await RunAsync();
        Directory.CreateDirectory(Nas);
        var dest = Destination("nas", Nas);
        var one = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest(first));
        Assert.True(one.Outcomes.Single().IsVerified);
        var second = await RunAsync();
        var third = await RunAsync();
        // the destination is behind: its newest verified copy is within its age, but the primary's newest run is missing there
        var lagging = ReplicaStatusReport.Evaluate([dest], Root, Trust, DateTimeOffset.UtcNow, TimeSpan.FromHours(26)).Single();
        Assert.Contains(lagging.Findings, f => f is { Code: "replica_lagging", ExitCode: 8 });
        var env = NasOnly();
        var replicate = await AdminTool.RunWith(Options, env, "backup-replicate", Root);
        Assert.True(replicate.Exit == 8, replicate.All);                                      // only the same-volume warning remains
        Assert.DoesNotContain("[FAILURE]", replicate.Out);
        var verified = (await Events(BackupAuditEvents.ReplicaVerified)).Select(d => Token(d, "run")).ToList();
        Assert.Equal([first, .. new[] { second, third }.Order(StringComparer.Ordinal)], verified);      // oldest first by run id (runs of one second order by their random suffix); the first run was not copied again
        Assert.All(new[] { first, second, third }, id => Assert.Equal(Tree(Path.Combine(Root, id)), Tree(Path.Combine(Nas, id))));
        // nothing left to copy: a second pass writes no new event
        var again = await AdminTool.RunWith(Options, env, "backup-replicate", Root);
        Assert.Equal(8, again.Exit);
        Assert.Equal(3, (await Events(BackupAuditEvents.ReplicaVerified)).Count);
        // --newest-first changes the order for a fresh destination
        Directory.CreateDirectory(Usb);
        var usb = Destination("usb", Usb, "local");
        await Service().ReplicateAsync(Root, [usb], new ReplicationRequest(NewestFirst: true));
        Assert.Equal(new[] { first, second, third }.Order(StringComparer.Ordinal).Reverse().ToList(), (await Events(BackupAuditEvents.ReplicaVerified)).Skip(3).Select(d => Token(d, "run")).ToList());
    }

    [PostgresFact]
    public async Task A_rename_that_fails_after_verification_writes_2040_then_2041_and_no_run_folder_appears()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        var result = await Service(hooks: new ReplicaCopyHooks { BeforePublish = (_, _) => throw new IOException("rename failed") }).ReplicateAsync(Root, [Destination("nas", Nas)], new ReplicationRequest(run));
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(("failed", "replica_publish_failed"), (outcome.Result, outcome.Code));
        Assert.False(Directory.Exists(Path.Combine(Nas, run)));
        Assert.Empty(Directory.GetDirectories(Nas));
        var verified = Assert.Single(await Events(BackupAuditEvents.ReplicaVerified));
        var failed = Assert.Single(await Events(BackupAuditEvents.ReplicaFailed));
        Assert.Equal(("replica_publish_failed", Token(verified, "status")), (Token(failed, "reason"), Token(failed, "status")));   // the compensating event carries the 2040's status hash
        Assert.Equal(1, ReplicationService.ReplicationCode(result.Outcomes, run, 1));
        // the destination holds nothing for this run: it counts as no verified copy
        Assert.Contains(ReplicaStatusReport.Evaluate([Destination("nas", Nas)], Root, Trust, DateTimeOffset.UtcNow, TimeSpan.FromHours(26)).Single().Findings, f => f.Code == "replica_missing");
    }

    [PostgresFact]
    public async Task A_byte_flipped_at_the_destination_a_crash_and_a_conflicting_folder_are_handled_without_publishing_a_bad_copy()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        var dest = Destination("nas", Nas);
        // flipped byte between copy and verification: verification fails, nothing is renamed into place, no 2040
        var flip = new ReplicaCopyHooks { AfterCopy = staging => { var f = Directory.EnumerateFiles(staging, "*.hbe", SearchOption.AllDirectories).First(); var b = File.ReadAllBytes(f); b[b.Length - 5] ^= 1; File.WriteAllBytes(f, b); } };
        var flipped = await Service(hooks: flip).ReplicateAsync(Root, [dest], new ReplicationRequest(run));
        Assert.Equal("replica_verification_failed", flipped.Outcomes.Single().Code);
        Assert.False(Directory.Exists(Path.Combine(Nas, run)));
        Assert.Empty(await Events(BackupAuditEvents.ReplicaVerified));
        // a crash between copy and rename: the staging folder is never a run, backup-status reports it only after 24 hours, the next replicate produces a verified copy
        await Assert.ThrowsAsync<ReplicaCrashSimulation>(() => Service(hooks: new ReplicaCopyHooks { BeforePublish = (_, _) => throw new ReplicaCrashSimulation() }).ReplicateAsync(Root, [dest], new ReplicationRequest(run)));
        var staging = Assert.Single(Directory.GetDirectories(Nas));
        Assert.Contains(".incoming-", staging);
        Assert.Empty(RetentionInventory.Scan(Nas, Trust).Runs);
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(-25));
        var stale = ReplicaStatusReport.Evaluate([dest], Root, Trust, DateTimeOffset.UtcNow, TimeSpan.FromHours(26)).Single();
        Assert.Contains(stale.Findings, f => f is { Code: "replica_incoming_stale", ExitCode: 8 });
        var again = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest(run));
        Assert.Equal("verified", again.Outcomes.Single().Result);
        Assert.Empty(Directory.GetDirectories(Nas).Where(d => d.Contains(".incoming-")));      // the stale staging folder was swept
        // a copy that differs from the primary's status under the same run id is never overwritten
        File.Delete(Path.Combine(Nas, run, "run.status.json"));
        File.Copy(Path.Combine(Root, run, "run.status.json.sig"), Path.Combine(Nas, run, "run.status.json"));   // another status under the run id
        var altered = Tree(Path.Combine(Nas, run));
        var conflict = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest(run));
        Assert.Equal("replica_conflict", conflict.Outcomes.Single().Code);
        Assert.Equal(altered, Tree(Path.Combine(Nas, run)));                                  // untouched by the engine: kept as evidence
        Assert.Contains("replica_conflict", (await Events(BackupAuditEvents.ReplicaFailed)).Select(d => Token(d, "reason")));
    }

    [PostgresFact]
    public async Task An_unencrypted_dev_test_run_is_never_copied_and_a_failed_or_unknown_run_is_refused()
    {
        await Init();
        var (_, dir) = await new BackupRunService(Options, Attachments, Encryption, TimeProvider.System).RunAsync(Root, null);   // DEV/TEST only: not encrypted, not signed
        Directory.CreateDirectory(Nas);
        var dest = Destination("nas", Nas);
        var plain = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest(Path.GetFileName(dir)));
        Assert.Equal(("failed", "replica_unencrypted_refused"), (plain.Outcomes.Single().Result, plain.Outcomes.Single().Code));
        Assert.Empty(Directory.GetFileSystemEntries(Nas));
        Assert.Equal("replica_unencrypted_refused", Token(Assert.Single(await Events(BackupAuditEvents.ReplicaFailed)), "reason"));
        // catch-up skips it silently (counted as never replicated)
        var catchUp = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest());
        Assert.Empty(catchUp.Outcomes);
        Assert.Equal(1, catchUp.NotReplicable);
        Assert.Equal("run_not_found", (await Assert.ThrowsAsync<BackupException>(() => Service().ReplicateAsync(Root, [dest], new ReplicationRequest("20200101T000000Z-00000000")))).Code);
        Assert.Equal("destination_unknown", (await Assert.ThrowsAsync<BackupException>(() => Service().ReplicateAsync(Root, [dest], new ReplicationRequest(DestinationId: "other")))).Code);
        Assert.Equal("invalid_run_id", (await Assert.ThrowsAsync<BackupException>(() => Service().ReplicateAsync(Root, [dest], new ReplicationRequest("../x")))).Code);
    }

    [PostgresFact]
    public async Task A_destination_held_by_another_replicator_is_busy_while_another_destination_is_free_and_the_locks_are_released()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas); Directory.CreateDirectory(Usb);
        await using (var held = await DestinationLock.AcquireAsync(Options, "nas", TimeSpan.FromSeconds(2), default))
        {
            var busy = await Assert.ThrowsAsync<BackupException>(() => Service(wait: TimeSpan.FromSeconds(1)).ReplicateAsync(Root, [Destination("nas", Nas)], new ReplicationRequest(run)));
            Assert.Equal("destination_busy", busy.Code);
            Assert.False(Directory.Exists(Path.Combine(Nas, run)));
            var free = await Service().ReplicateAsync(Root, [Destination("usb", Usb, "local")], new ReplicationRequest(run));
            Assert.True(free.Outcomes.Single().IsVerified);                                    // a different destination is not blocked (up to a 16-bit hash collision, which only serialises)
        }
        var after = await Service().ReplicateAsync(Root, [Destination("nas", Nas)], new ReplicationRequest(run));
        Assert.True(after.Outcomes.Single().IsVerified);
    }

    [PostgresFact]
    public async Task An_overlapping_or_invalid_destination_is_refused_before_anything_is_written_with_2043_and_exit_3()
    {
        await Init();
        // inside the primary root
        var inside = Env(("0__Id", "nas"), ("0__Path", Path.Combine(Root, "copies")), ("0__Kind", "local"));
        var run = await AdminTool.RunWith(Options, inside, "backup-run", Root);
        Assert.True(run.Exit == 3, run.All);
        Assert.False(Directory.Exists(Root) && Directory.GetDirectories(Root).Length > 0);      // no run was written
        var refused = Assert.Single(await Events(BackupAuditEvents.DestinationRefused));
        Assert.Equal(("nas", "destination_overlaps_source"), (Token(refused, "destination"), Token(refused, "reason")));
        Assert.DoesNotContain(work, refused);
        // an invalid configuration (a relative path) names no destination
        var invalid = await AdminTool.RunWith(Options, Env(("0__Id", "nas"), ("0__Path", "relative/path"), ("0__Kind", "local")), "backup-replicate", Root);
        Assert.Equal(3, invalid.Exit);
        Assert.Equal("destination_config_invalid", Token((await Events(BackupAuditEvents.DestinationRefused))[1], "reason"));
        // backup-replicate without a destination is a configuration refusal, and bad options are a usage error
        Assert.Equal(3, (await AdminTool.RunWith(Options, Env(), "backup-replicate", Root)).Exit);
        Assert.Equal(2, (await AdminTool.RunWith(Options, NasOnly(), "backup-replicate", Root, "--run", "not-a-run")).Exit);
        Assert.Equal(2, (await AdminTool.RunWith(Options, NasOnly(), "backup-replicate", Root, "--destination", "NAS")).Exit);
        Assert.Equal(3, (await AdminTool.RunWith(Options, NasOnly(), "backup-replicate", Root, "--destination", "other")).Exit);
    }

    [PostgresFact]
    public async Task A_destination_swapped_for_a_link_is_refused_with_2043_and_written_through_never_even_between_verification_and_rename()
    {
        await Init();
        var run = await RunAsync();
        var other = Path.Combine(work, "elsewhere"); Directory.CreateDirectory(other);
        Directory.CreateDirectory(Nas);
        var dest = Destination("nas", Nas);                                                     // checked while Nas was a real folder
        Directory.Delete(Nas); Directory.CreateSymbolicLink(Nas, other);                        // swapped after the check, before the copy
        var first = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest(run));
        Assert.Equal(("failed", "symbolic_link_refused"), (first.Outcomes.Single().Result, first.Outcomes.Single().Code));
        Assert.Empty(Directory.GetFileSystemEntries(other));
        var refused = Assert.Single(await Events(BackupAuditEvents.DestinationRefused));
        Assert.Equal(("nas", "symbolic_link_refused"), (Token(refused, "destination"), Token(refused, "reason")));
        // swapped between verification and the rename: 2040 is written, the publish is refused and answered by 2041, nothing appears in the real folder
        Directory.Delete(Nas); Directory.CreateDirectory(Nas);
        var real = Path.Combine(work, "nas-real");
        var hooks = new ReplicaCopyHooks { BeforePublish = (_, _) => { Directory.Move(Nas, real); Directory.CreateSymbolicLink(Nas, real); } };
        var second = await Service(hooks: hooks).ReplicateAsync(Root, [Destination("nas", Nas)], new ReplicationRequest(run));
        Assert.Equal(("failed", "replica_publish_failed"), (second.Outcomes.Single().Result, second.Outcomes.Single().Code));
        Assert.Empty(Directory.GetDirectories(real));
        Assert.Equal("replica_publish_failed", Token(Assert.Single(await Events(BackupAuditEvents.ReplicaFailed)), "reason"));
    }

    [PostgresFact]
    public async Task A_real_run_carries_the_signed_ciphertext_inventory_and_a_bit_flipped_in_the_primary_after_the_run_is_never_published()
    {
        await Init();
        var run = await RunAsync();
        var runDir = Path.Combine(Root, run);
        Assert.True(File.Exists(Path.Combine(runDir, CiphertextDigests.FileName)) && File.Exists(Path.Combine(runDir, CiphertextDigests.FileName + ".sig")));
        var list = CiphertextDigests.ReadVerified(runDir, run, SignedDocuments.ReadRunStatus(runDir, Trust).DigestListSha256, Trust);
        Assert.Equal(Directory.GetFiles(runDir, "*.hbe", SearchOption.AllDirectories).Length, list.Files.Count);   // every encrypted file, the canary and the manifests included
        Assert.True(RetentionInventory.Inspect(runDir).Clean);
        Directory.CreateDirectory(Nas);
        var dest = Destination("nas", Nas);
        var dump = Directory.GetFiles(runDir, "database.dump.hbe", SearchOption.AllDirectories).First();
        var bytes = File.ReadAllBytes(dump); bytes[bytes.Length / 2] ^= 0x01; File.WriteAllBytes(dump, bytes);   // after the run was signed, HBE1 framing intact
        var refused = await Service().ReplicateAsync(Root, [dest], new ReplicationRequest(run));
        Assert.Equal(("failed", "replica_digest_mismatch"), (refused.Outcomes.Single().Result, refused.Outcomes.Single().Code));
        Assert.Empty(Directory.GetFileSystemEntries(Nas));
        Assert.Empty(await Events(BackupAuditEvents.ReplicaVerified));
        Assert.Equal("replica_digest_mismatch", Token(Assert.Single(await Events(BackupAuditEvents.ReplicaFailed)), "reason"));
        // catch-up reports the same and copies nothing
        var catchUp = await AdminTool.RunWith(Options, NasOnly(), "backup-replicate", Root);
        Assert.True(catchUp.Exit == 1, catchUp.All);
        Assert.Contains("replica_digest_mismatch", catchUp.Out);
        Assert.Empty(Directory.GetFileSystemEntries(Nas));
    }
}
