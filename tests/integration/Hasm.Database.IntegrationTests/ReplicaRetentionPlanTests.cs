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
/// Phase 4.3 slice 8, S8d-1 (read only) over REAL encrypted, signed runs: <c>replication_pending</c> from the destination FOLDER (light check), the tombstone exception only through an AUTHENTICATED audit
/// state, the retention plan at a destination, the deletion-readiness lines and the <c>Deletion blocked:</c> notice through the Hasm.Admin binary, and the destination token of audit 2014. Nothing can
/// delete (the code gate is closed): every tree is compared before and after. Runs in the backup collection (no parallel class).
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class ReplicaRetentionPlanTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for tests";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-rrp-" + Guid.NewGuid().ToString("N"));
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
    private static RetentionOptions Retention => new() { InstallationTimeZone = "UTC" };

    private Dictionary<string, string?> NasEnv() => new()
    {
        ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
        ["Hasm__Audit__KeyDirectory"] = AuditKeys, ["Hasm__Audit__ActiveKeyId"] = "a1", ["Hasm__Audit__CheckpointDirectory"] = Path.Combine(work, "checkpoints"),
        ["Hasm__Backup__RecoveryPublicKeyFile"] = publicKeyFile, ["Hasm__Backup__SigningKeyDirectory"] = SigningDir, ["Hasm__Backup__SigningKeyId"] = signingId,
        ["Hasm__Backup__AllowInsecureFilePermissions"] = "true", ["Hasm__Backup__Retention__InstallationTimeZone"] = "UTC",
        ["Hasm__Backup__Destinations__0__Id"] = "nas", ["Hasm__Backup__Destinations__0__Path"] = Nas, ["Hasm__Backup__Destinations__0__Kind"] = "network",
    };

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

    private static void CopyRun(string fromRoot, string toRoot, string runId)
    {
        var from = Path.Combine(fromRoot, runId);
        var to = Path.Combine(toRoot, runId);
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    private IReadOnlyList<CheckedDestination> Destinations() =>
        BackupDestinations.Check([new BackupDestination("nas", Nas, BackupDestinationKind.Network, null, null, null)], Root, [Storage, Keys, AuditKeys, SigningDir], _ => "dev:other");

    private static string TreeHash(string root) => !Directory.Exists(root) ? "" : string.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(root, p) + (File.Exists(p) ? ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "/")));

    private static string StatusHash(string root, string run) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(root, run, SignedDocuments.RunStatusFileName))));

    private static void Tombstone(string root, string fromRoot, string run)
    {
        var dir = Path.Combine(root, ".retention", "tombstones", run);
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { SignedDocuments.RunStatusFileName, SignedDocuments.RunStatusFileName + SignedDocuments.SignatureSuffix }) File.Copy(Path.Combine(fromRoot, run, n), Path.Combine(dir, n));
    }

    [PostgresFact]
    public async Task Replication_pending_is_read_from_the_destination_folder_and_the_tombstone_exception_needs_an_authenticated_audit_state()
    {
        await Init();
        var r1 = await ProvenRun();
        var r2 = await ProvenRun();
        var primaryRuns = RetentionInventory.Scan(Root, Trust).Runs;
        ReplicationPending.Result Pending(ReplicaAuditState? audit = null) => ReplicationPending.Evaluate(primaryRuns, Destinations(), Trust, audit, null, BackupSet.RealPath(Root));

        Assert.Equal([r1, r2], Pending().RunIds.Order());                                                           // the destination folder does not exist: no copy of anything
        Directory.CreateDirectory(Nas);
        CopyRun(Root, Nas, r1);
        Assert.Equal([r2], Pending().RunIds);                                                                       // only r1 has a verified copy
        CopyRun(Root, Nas, r2);
        Assert.Empty(Pending().RunIds);

        // a copy whose signed inventory is gone is not a verified copy (light check: inventory present and bound to the status)
        var digests = Path.Combine(Nas, r2, CiphertextDigests.FileName);
        var kept = File.ReadAllBytes(digests);
        File.Delete(digests);
        Assert.Equal([r2], Pending().RunIds);
        File.WriteAllBytes(digests, kept);
        Assert.Empty(Pending().RunIds);
        // a copy that the last re-verification reported corrupt no longer counts
        var corrupt = ReplicaAuditReader.Build([(1L, BackupAuditEvents.ReplicaCorrupt, $"replica corrupt operator=os:t run={r2} destination=nas check=hash", DateTimeOffset.UtcNow)]);
        Assert.Equal([r2], Pending(corrupt).RunIds);

        // the tombstone exception: r1 deleted at nas (tombstone with the primary's signed status, 2015 + 2016 for destination nas), no later copy, no folder
        var h1 = StatusHash(Root, r1);
        Directory.Delete(Path.Combine(Nas, r1), true);
        Tombstone(Nas, Root, r1);
        var pair2 = ReplicaAuditReader.Build([
            (1L, BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={r1} status={h1} reasons=none destination=nas", DateTimeOffset.UtcNow),
            (2L, BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={r1} status={h1} files=1 bytes=1 tombstone=yes resumed=no destination=nas", DateTimeOffset.UtcNow)]);
        Assert.Equal([r1], Pending(pair2).RunIds);                                                                  // a raw (informational) state authorises nothing: conservative
        var authenticated = pair2 with { Authenticated = true, VerifiedThroughSeq = 2 };
        Assert.Empty(Pending(authenticated).RunIds);                                                                // fully accounted for: not pending
        // the same pair for another destination id, or without the 2016, is not the exception
        var other = ReplicaAuditReader.Build([
            (1L, BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={r1} status={h1} reasons=none destination=nas-1", DateTimeOffset.UtcNow),
            (2L, BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={r1} status={h1} files=1 bytes=1 tombstone=yes resumed=no destination=nas-1", DateTimeOffset.UtcNow)]) with { Authenticated = true };
        Assert.Equal([r1], Pending(other).RunIds);
        var open = ReplicaAuditReader.Build([(1L, BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={r1} status={h1} reasons=none destination=nas", DateTimeOffset.UtcNow)]) with { Authenticated = true };
        Assert.Equal([r1], Pending(open).RunIds);
        // a copy published AFTER the pair (the replay case) makes the old pair void
        var replay = ReplicaAuditReader.Build([
            (1L, BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={r1} status={h1} reasons=none destination=nas", DateTimeOffset.UtcNow),
            (2L, BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={r1} status={h1} files=1 bytes=1 tombstone=yes resumed=no destination=nas", DateTimeOffset.UtcNow),
            (3L, BackupAuditEvents.ReplicaVerified, $"replica verified operator=os:t run={r1} destination=nas kind=network status={h1} files=1 bytes=1", DateTimeOffset.UtcNow)]) with { Authenticated = true };
        Assert.Equal([r1], Pending(replay).RunIds);

        // the same states through the one status function: incomplete and unexplained deletions have their own findings
        var dest = Destinations().Single();
        var state = ReplicaStatus.Inspect(dest, primaryRuns, Trust, DateTimeOffset.UtcNow, authenticated);
        Assert.Equal([r1], state.ExplainedDeletionRunIds);
        Assert.Equal(0, state.UnexplainedTombstones);
        Assert.Empty(state.IncompleteDeletionRunIds);
        var incomplete = ReplicaStatus.Inspect(dest, primaryRuns, Trust, DateTimeOffset.UtcNow, open);
        Assert.Equal([r1], incomplete.IncompleteDeletionRunIds);
        Assert.Contains("retention_deletion_incomplete", ReplicaStatus.Contribution(incomplete, r2, DateTimeOffset.UtcNow, TimeSpan.FromHours(26)).Select(f => f.Code));
        var unexplained = ReplicaStatus.Inspect(dest, primaryRuns, Trust, DateTimeOffset.UtcNow, ReplicaAuditState.Empty);
        Assert.Equal(1, unexplained.UnexplainedTombstones);                                                        // no 2015 at all
        Assert.Contains("replica_tombstone_unexplained", ReplicaStatus.Contribution(unexplained, r2, DateTimeOffset.UtcNow, TimeSpan.FromHours(26)).Select(f => f.Code));
        Assert.Equal(1, ReplicaStatus.Inspect(dest, primaryRuns, Trust, DateTimeOffset.UtcNow, null).UnexplainedTombstones);   // S8a: with no audit state every tombstone is unexplained
    }

    [PostgresFact]
    public async Task A_destination_copy_that_shares_storage_with_the_primary_keeps_the_run_replication_pending_in_the_plan_and_in_readiness()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Init();
        var r1 = await ProvenRun();
        Directory.CreateDirectory(Nas);
        CopyRun(Root, Nas, r1);
        var service = new RetentionService(Options, Retention);
        IReadOnlyList<string> PrimaryReadiness() => DeletionReadiness.Locations(Retention, Destinations(), Root, Trust, null, true).Single(l => l.Location == "primary").Reasons;

        var independent = await service.PlanLocationAsync(Root, null, Destinations(), Trust, null);                 // a real, separate copy: not pending
        Assert.Empty(independent.Pending.RunIds);
        Assert.DoesNotContain("replication_pending", independent.Plan.Runs.Single(d => d.RunId == r1).Reasons);
        Assert.DoesNotContain("replication_pending", PrimaryReadiness());

        // one file of the "copy" becomes a hard link to the primary's file: the bytes and every signature are unchanged, but it is the primary's storage (review M1 of PR 232)
        var ours = Directory.GetFiles(Path.Combine(Root, r1), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First(f => new FileInfo(f).Length > 0 && !f.EndsWith(SignedDocuments.SignatureSuffix, StringComparison.Ordinal));
        var theirs = Path.Combine(Nas, r1, Path.GetRelativePath(Path.Combine(Root, r1), ours));
        var primaryBefore = TreeHash(Root); var nasBefore = TreeHash(Nas);
        File.Delete(theirs);
        using (var ln = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { ours, theirs }, UseShellExecute = false })!)
        {
            ln.WaitForExit();
            Assert.Equal(0, ln.ExitCode);
        }
        Assert.Equal(nasBefore, TreeHash(Nas));                                                                     // byte-identical: only the identity differs
        var shared = await service.PlanLocationAsync(Root, null, Destinations(), Trust, null);
        Assert.Equal([r1], shared.Pending.RunIds);
        Assert.Equal(["nas"], shared.Pending.MissingAt[r1]);
        Assert.Contains("replication_pending", shared.Plan.Runs.Single(d => d.RunId == r1).Reasons);
        Assert.Empty(shared.Plan.Eligible);
        Assert.Contains("replication_pending", PrimaryReadiness());
        Assert.Equal((primaryBefore, nasBefore), (TreeHash(Root), TreeHash(Nas)));                                  // the check reads only
    }

    [PostgresFact]
    public async Task The_retention_plan_keeps_runs_a_destination_lacks_previews_a_destination_and_the_binary_prints_readiness_and_the_destination_token_while_nothing_changes()
    {
        await Init();
        var r1 = await ProvenRun();
        var r2 = await ProvenRun();
        Directory.CreateDirectory(Nas);
        CopyRun(Root, Nas, r1);                                                                                     // nas holds r1 only
        var service = new RetentionService(Options, Retention);
        var primaryBefore = TreeHash(Root); var nasBefore = TreeHash(Nas);

        var primary = await service.PlanLocationAsync(Root, null, Destinations(), Trust, null);
        Assert.Contains("replication_pending", primary.Plan.Runs.Single(d => d.RunId == r2).Reasons);
        Assert.DoesNotContain("replication_pending", primary.Plan.Runs.Single(d => d.RunId == r1).Reasons);
        Assert.Equal("primary", primary.Location);
        Assert.Empty(primary.Plan.Eligible);                                                                        // nothing is ever eligible while the minimums and the pending run keep everything

        var atNas = await service.PlanLocationAsync(Root, "nas", Destinations(), Trust, null);
        Assert.Equal("nas", atNas.Location);
        Assert.Equal([r1], atNas.Plan.Runs.Select(d => d.RunId));                                                   // the destination root is scanned like a primary root
        Assert.DoesNotContain(atNas.Plan.Runs, d => d.Reasons.Contains("replication_pending"));
        Assert.Equal("destination_unknown", (await Assert.ThrowsAsync<BackupException>(() => service.PlanLocationAsync(Root, "nope", Destinations(), Trust, null))).Code);

        // the binary: authenticated audit read, readiness lines, the blocked notice, audit 2014 with the destination token; nothing else is written
        var eventsBefore = await ScalarAsync<long>($"SELECT count(*) FROM {E.Table}");
        var plan = await AdminTool.RunWith(Options, NasEnv(), "backup-retention-plan", Root);
        Assert.True(plan.Exit == 0, plan.All);
        Assert.Contains($"{r2} keep:", plan.Out);
        Assert.Contains($"{r2} replication_pending at nas", plan.Out);
        Assert.Contains("Deletion readiness: primary: blocked (retention_deletion_inactive", plan.Out);
        Assert.Contains("Deletion readiness: nas: blocked (retention_deletion_inactive", plan.Out);
        Assert.Contains("Deletion blocked:", plan.Out);
        Assert.Contains("  retention_deletion_inactive location=primary", plan.Out);
        Assert.Contains("  replication_pending location=primary", plan.Out);
        Assert.DoesNotContain(Work, plan.All);                                                                      // codes and ids only, never a path
        var onNas = await AdminTool.RunWith(Options, NasEnv(), "backup-retention-plan", Root, "--destination", "nas");
        Assert.True(onNas.Exit == 0, onNas.All);
        Assert.Contains("destination nas", onNas.Out);
        Assert.DoesNotContain("replication_pending at", onNas.Out.Replace("Replication check", ""));
        var bad = await AdminTool.RunWith(Options, NasEnv(), "backup-retention-plan", Root, "--destination", "nope");
        Assert.NotEqual(0, bad.Exit);
        Assert.Equal(eventsBefore + 2, await ScalarAsync<long>($"SELECT count(*) FROM {E.Table}"));                // exactly the two 2014 events
        var details = await DetailsOf(BackupAuditEvents.RetentionEvaluated);
        Assert.Contains(details, d => d.Contains(" destination=primary", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Contains(" destination=nas", StringComparison.Ordinal));
        Assert.Equal(primaryBefore, TreeHash(Root));
        Assert.Equal(nasBefore, TreeHash(Nas));

        // backup-status prints the same readiness (informational audit read)
        var status = await AdminTool.RunWith(Options, NasEnv(), "backup-status", Root);
        Assert.Contains("Deletion readiness: primary: blocked (retention_deletion_inactive", status.Out);
        Assert.Contains("Deletion readiness: nas: blocked", status.Out);
        Assert.Equal(primaryBefore, TreeHash(Root));

        // and the apply stays closed: nothing is deleted anywhere (the audit records the destination token)
        var apply = await AdminTool.RunWith(Options, NasEnv(), "backup-retention-apply", Root);
        Assert.NotEqual(0, apply.Exit);
        Assert.Equal(primaryBefore, TreeHash(Root));
        Assert.Equal(nasBefore, TreeHash(Nas));
        Assert.Contains(await DetailsOf(BackupAuditEvents.DeletionRefused), d => d.Contains("reason=retention_deletion_inactive destination=primary", StringComparison.Ordinal));
    }

    private string Work => work;

    private async Task<List<string>> DetailsOf(short type)
    {
        await using var conn = new Npgsql.NpgsqlConnection(SystemConnection);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT {E.Detail} FROM {E.Table} WHERE {E.EventType} = @t ORDER BY {E.Seq}", conn);
        cmd.Parameters.AddWithValue("t", type);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<T> ScalarAsync<T>(string connection, string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<T> ScalarAsync<T>(string sql) => ScalarAsync<T>(SystemConnection, sql);
}
