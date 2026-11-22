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
/// Phase 4.3 slice 8 on the disposable PostgreSQL server, S8a (D-183, D-184, D-186): configured destinations over REAL encrypted, signed runs. No copy engine exists yet, so a copy is made here
/// byte for byte (exactly what D-185 will publish). Covered: the status contribution of a destination (missing, verified, same volume, lagging, corrupt, unexplained tombstone), the overall
/// status through the Hasm.Admin binary, a status call on a destination folder, refusals of an invalid or overlapping configuration (exit 3, nothing created), and a key export or trial restore into a
/// destination refused. Read-only throughout: no audit event is written by the status. Runs in the backup collection (no parallel class).
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class BackupReplicationTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for tests";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-brp-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private string AuditKeys => Path.Combine(work, "audit-keys");
    private string SigningDir => Path.Combine(work, "signing");
    private string Root => Path.Combine(work, "runs");
    private string Nas => Path.Combine(work, "nas");
    private RecoveryKeys.GeneratedPair pair = null!;
    private string publicKeyFile = null!, signingId = null!, signingPem = null!;
    private CompanyRecord company = null!;

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

    private Dictionary<string, string?> NasEnv() => Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"));

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
        company = await registry.RegisterPendingAsync();
        await new CompanyProvisioner(Options, registry).ProvisionAsync(company.Id);
    }

    /// <summary>One encrypted run now (the status of the Hasm.Admin binary uses the real clock), with a passed recovery test, so the primary's own root status is ok.</summary>
    private async Task<string> ProvenRun()
    {
        var (report, dir) = await new BackupRunService(Options, Attachments, Encryption, TimeProvider.System).RunAsync(Root, Material());
        Assert.Equal("ok", report.Verdict);
        using var priv = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
        var t = await new RecoveryTestService(Options, Attachments, Encryption, nameGenerator: NewTestName).RunAsync(dir, new RestoreKeys(priv, Trust), null, Path.Combine(work, "rt-" + Path.GetFileName(dir)));
        Assert.True(t.Passed, string.Join(", ", t.Failures.Select(f => f.Code)));
        RecoveryTestService.WriteProof(dir, t, DateTimeOffset.UtcNow);
        return Path.GetFileName(dir);
    }

    /// <summary>A byte-for-byte copy of a run folder (what D-185 will publish after its verification).</summary>
    private static void CopyRun(string fromRoot, string toRoot, string runId)
    {
        var from = Path.Combine(fromRoot, runId);
        var to = Path.Combine(toRoot, runId);
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    /// <summary>The fake volume of the destination: its own folder, or (while it does not exist yet) the nearest existing parent the real probe falls back to.</summary>
    private bool OnDestinationVolume(string p)
    {
        if (p.StartsWith(Nas, StringComparison.Ordinal)) return true;
        if (Directory.Exists(Nas)) return false;
        var nearest = Path.GetDirectoryName(Nas);
        while (nearest is not null && !Directory.Exists(nearest)) nearest = Path.GetDirectoryName(nearest);
        return string.Equals(p, nearest, StringComparison.Ordinal);
    }

    private IReadOnlyList<string> Findings(string? otherVolume = "dev:other")
    {
        var configured = new[] { new BackupDestination("nas", Nas, BackupDestinationKind.Network, null, null, null) };
        var check = Assert.Single(BackupDestinations.Check(configured, Root, [Storage, Keys, AuditKeys, SigningDir],
            p => OnDestinationVolume(p) ? otherVolume : "dev:primary"));
        var primaryRuns = RetentionInventory.Scan(Root, Trust).Runs;
        var newest = primaryRuns.Where(ReplicaStatus.IsReplicable).Max(r => r.RunId);
        var state = ReplicaStatus.Inspect(check, primaryRuns, Trust, DateTimeOffset.UtcNow);
        return [.. ReplicaStatus.Contribution(state, newest, DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge).Select(f => $"{f.Code}:{f.ExitCode}")];
    }

    private async Task<long> AuditCount() => await ScalarAsync<long>(SystemConnection, $"SELECT count(*) FROM {E.Table}");

    private static async Task<T> ScalarAsync<T>(string connection, string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task A_destination_contributes_to_the_overall_status_from_its_verified_copies_by_the_one_status_function()
    {
        await Init();
        var run = await ProvenRun();
        var eventsBefore = await AuditCount();

        // no destination configured: today's behaviour, and the root status is ok
        var none = await AdminTool.RunWith(Options, Env(), "backup-status", Root);
        Assert.True(none.Exit == 0, none.All);
        Assert.Contains("Replicas: none", none.Out);

        // configured but the folder does not exist yet: no verified copy, exit 1, and nothing is created by the status
        var missing = await AdminTool.RunWith(Options, NasEnv(), "backup-status", Root);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("[FAILURE] replica_missing destination=nas", missing.Out);
        Assert.Contains("Overall backup status", missing.Out);
        Assert.False(Directory.Exists(Nas));
        Assert.Equal(["replica_missing:1"], Findings());

        // a byte-identical copy of the run: verified; on another volume it contributes nothing, so the overall status is ok
        Directory.CreateDirectory(Nas);
        CopyRun(Root, Nas, run);
        Assert.Empty(Findings());
        // the same copy on the primary's volume (as every temp folder of this test is): the same-volume warning, exit 8 (D-184, MD-Q2 / D-230), never 0
        Assert.Equal(["destination_same_volume:8"], Findings(otherVolume: "dev:primary"));
        Assert.Equal(["destination_same_volume:8"], Findings(otherVolume: null));            // an unknown volume cannot be shown to be independent
        var sameVolume = await AdminTool.RunWith(Options, NasEnv(), "backup-status", Root);
        Assert.Equal(8, sameVolume.Exit);
        Assert.Contains($"newest verified copy {run}", sameVolume.Out);
        Assert.Contains("[WARNING] destination_same_volume destination=nas", sameVolume.Out);
        Assert.DoesNotContain("replica_missing", sameVolume.Out);

        // a status call ON the destination folder: that folder's root status only (the copy holds its recovery-test proof too)
        var onCopy = await AdminTool.RunWith(Options, NasEnv(), "backup-status", Nas);
        Assert.True(onCopy.Exit == 0, onCopy.All);
        Assert.Contains("ROOT status only", onCopy.Out);
        Assert.DoesNotContain("Overall backup status", onCopy.Out);

        // a newer run at the primary that is not yet at the destination: lagging (8) while the older copy is fresh
        var newer = await ProvenRun();
        Assert.Equal(["replica_lagging:8"], Findings());
        CopyRun(Root, Nas, newer);
        Assert.Empty(Findings());

        // a copy whose signed status was altered no longer verifies: corrupt, exit 1, also through the binary
        var status = Path.Combine(Nas, run, SignedDocuments.RunStatusFileName);
        var original = File.ReadAllBytes(status);
        File.WriteAllText(status, File.ReadAllText(status).Replace("\"ok\"", "\"partial\""));
        Assert.Equal(["replica_corrupt:1"], Findings());
        var corrupt = await AdminTool.RunWith(Options, NasEnv(), "backup-status", Root);
        Assert.Equal(1, corrupt.Exit);
        Assert.Contains("[FAILURE] replica_corrupt destination=nas", corrupt.Out);
        File.WriteAllBytes(status, original);
        Assert.Empty(Findings());

        // a tombstone at a destination: no deletion at a destination exists in this slice, so nothing explains it (exit 1)
        Directory.CreateDirectory(Path.Combine(Nas, ".retention", "tombstones", run));
        Assert.Equal(["replica_tombstone_unexplained:1"], Findings());
        Directory.Delete(Path.Combine(Nas, ".retention"), true);

        // a stale staging folder (a copy that never finished) is never a copy and warns
        var staging = Path.Combine(Nas, newer + ".incoming-0123456789abcdef");
        Directory.CreateDirectory(staging);
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(-25));
        Assert.Equal(["replica_incoming_stale:8"], Findings());

        // read-only: the status wrote no audit event
        Assert.Equal(eventsBefore, await AuditCount());
    }

    [PostgresFact]
    public async Task An_invalid_or_unsafe_destination_configuration_is_refused_before_anything_and_never_skipped()
    {
        await Init();
        await ProvenRun();
        async Task Refused(string code, Dictionary<string, string?> env)
        {
            var r = await AdminTool.RunWith(Options, env, "backup-status", Root);
            Assert.True(r.Exit == 3, r.All);
            Assert.Contains($"refused ({code}", r.Err);
            Assert.DoesNotContain("Backup status:", r.Out);                                   // refused before any result is computed
        }
        await Refused("destination_config_invalid", Env(("0__Id", "NAS"), ("0__Path", Nas), ("0__Kind", "network")));
        await Refused("destination_config_invalid", Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"), ("2__Id", "usb"), ("2__Path", Nas + "-usb"), ("2__Kind", "local")));
        await Refused("retention_below_minimum", Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"), ("0__DailyKeep", "6")));
        await Refused("destination_overlaps_source", Env(("0__Id", "nas"), ("0__Path", Path.Combine(Root, "copies")), ("0__Kind", "local")));
        await Refused("destination_overlaps_source", Env(("0__Id", "nas"), ("0__Path", Path.Combine(Keys, "copies")), ("0__Kind", "local")));
        await Refused("destination_duplicate", Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"), ("1__Id", "usb"), ("1__Path", Nas + "/"), ("1__Kind", "local")));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Nas);
            var link = Path.Combine(work, "nas-link"); Directory.CreateSymbolicLink(link, Nas);
            await Refused("symbolic_link_refused", Env(("0__Id", "nas"), ("0__Path", link), ("0__Kind", "network")));
        }
        Assert.False(Directory.Exists(Path.Combine(Root, "copies")));
        Assert.False(Directory.Exists(Path.Combine(Keys, "copies")));
    }

    [PostgresFact]
    public async Task A_key_export_or_a_trial_restore_into_a_destination_is_refused_before_anything_is_written()
    {
        await Init();
        var run = await ProvenRun();
        Directory.CreateDirectory(Nas);
        var eventsBefore = await AuditCount();

        // D-087 / D-184: the recovery material never travels with the data copies
        var keys = await AdminTool.RunWith(Options, NasEnv(), "backup-keys", Path.Combine(Nas, "kit"));
        Assert.Equal(1, keys.Exit);
        Assert.Contains("destination_in_backup_root", keys.Err);
        Assert.False(Directory.Exists(Path.Combine(Nas, "kit")));
        var around = await AdminTool.RunWith(Options, Env(("0__Id", "nas"), ("0__Path", Path.Combine(work, "kit-parent", "nas")), ("0__Kind", "network")), "backup-keys", Path.Combine(work, "kit-parent"));
        Assert.Equal(1, around.Exit);
        Assert.Contains("destination_in_backup_root", around.Err);
        Assert.Equal(eventsBefore, await AuditCount());                                       // no export event: nothing was exported
        // an invalid destination configuration is a configuration error there too
        Assert.Equal(3, (await AdminTool.RunWith(Options, Env(("0__Id", "nas")), "backup-keys", Path.Combine(work, "kit"))).Exit);
        Assert.False(Directory.Exists(Path.Combine(work, "kit")));
        // beside the destination the export works
        var ok = await AdminTool.RunWith(Options, NasEnv(), "backup-keys", Path.Combine(work, "kit"));
        Assert.True(ok.Exit == 0, ok.All);

        // a trial restore never stages its attachments inside a destination (refused before any key is loaded or anything restored)
        var set = Path.Combine(Root, run, "companies", company.Id.ToString("N"));
        var trial = await AdminTool.RunWith(Options, NasEnv(), "trial-restore-company", set, company.Id.ToString(), Path.Combine(Nas, "staging"));
        Assert.Equal(1, trial.Exit);
        Assert.Contains("destination_in_backup_root", trial.Err);
        Assert.False(Directory.Exists(Path.Combine(Nas, "staging")));
        Assert.False(Directory.Exists(Path.Combine(work, "kit-parent")));                     // nothing was created around the destination either
    }
}
