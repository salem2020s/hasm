using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using ST = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Settings;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 8, S8c (D-185, D-186, D-188, D-189) on the disposable PostgreSQL server: <c>backup-replica-verify</c> over REAL encrypted copies, audit 2040 (reverified) and 2042 read back, the corrupt copy
/// in <c>backup-status</c> (which now reads the audit stream when destinations are configured), and a restore from a copy. Every folder lies on one volume, so a healthy destination reads exit 8
/// (<c>destination_same_volume</c>) in <c>backup-status</c>. Runs in the backup collection.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class BackupReplicaVerifyTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for tests";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-brv-" + Guid.NewGuid().ToString("N"));
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
    private readonly StrictlyOrderedClock clock = new();                                                // runs created in one test are strictly ordered by creation (no same-second ordering by random suffix)
    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);
    private AuditTrail Trail() => AuditTrail.FromOptions(new AuditOptions { KeyDirectory = AuditKeys, ActiveKeyId = "a1", CheckpointDirectory = Path.Combine(work, "checkpoints"), AllowInsecureFilePermissions = true }, TimeProvider.System);
    private ReplicationService Service() => new(Options, Trust, new SystemReplicaEvents(SystemConnection, Trail())) { LockWait = TimeSpan.FromSeconds(5) };

    private Dictionary<string, string?> NasOnly() => Env(("0__Id", "nas"), ("0__Path", Nas), ("0__Kind", "network"));

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
        company = (await registry.GetAsync(company.Id))!;
        // the company has a time zone that resolves: without one retention classifies it unknown and the default re-verification selection holds all its copies (review U3 of PR 214)
        await using var db = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
        await db.OpenAsync();
        await using var zone = new NpgsqlCommand($"INSERT INTO {ST.Table} ({ST.TimeZone}, {ST.UpdatedAtUtc}) VALUES ('UTC', now()) ON CONFLICT ({ST.Singleton}) DO UPDATE SET {ST.TimeZone} = EXCLUDED.{ST.TimeZone}, {ST.Revision} = {ST.Table}.{ST.Revision} + 1, {ST.UpdatedAtUtc} = now()", db);
        await zone.ExecuteNonQueryAsync();
    }

    private async Task<string> RunAsync()
    {
        var (report, dir) = await new BackupRunService(Options, Attachments, Encryption, clock).RunAsync(Root, Material());
        Assert.Equal("ok", report.Verdict);
        return Path.GetFileName(dir);
    }

    private CheckedDestination Destination() => new(new BackupDestination("nas", Nas, BackupDestinationKind.Network, null, null, null), Nas, Directory.Exists(Nas), false);

    private async Task Replicate(string run) => Assert.True((await Service().ReplicateAsync(Root, [Destination()], new ReplicationRequest(run))).Outcomes.Single().IsVerified);

    private async Task<List<string>> Events(short type)
    {
        await using var conn = new NpgsqlConnection(SystemConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {E.Detail} FROM {E.Table} WHERE {E.StreamKey} = @s AND {E.EventType} = @t ORDER BY {E.Seq}", conn);
        cmd.Parameters.AddWithValue("s", AuditStream.SystemKey);
        cmd.Parameters.AddWithValue("t", type);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private static string Token(string detail, string key) => detail.Split(' ').Single(t => t.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];

    private static void FlipBit(string path) { var b = File.ReadAllBytes(path); b[b.Length / 2] ^= 0x01; File.WriteAllBytes(path, b); }

    private static string Dump(string runDir) => Directory.GetFiles(runDir, "database.dump.hbe", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).First();

    [PostgresFact]
    public async Task Verified_copies_write_2040_reverified_and_backup_status_shows_the_age_of_the_last_re_verification()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        var before = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);
        Assert.Contains("last re-verification: never", before.Out);
        var verify = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify");
        Assert.True(verify.Exit == 0, verify.All);
        Assert.Contains($"Re-verification {run} at destination nas: VERIFIED", verify.Out);
        var verified = await Events(BackupAuditEvents.ReplicaVerified);
        Assert.Equal(["false", "true"], verified.Select(v => Token(v, "reverified")).ToList());      // the copy, then its re-verification
        Assert.Empty(await Events(BackupAuditEvents.ReplicaCorrupt));
        var status = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);
        Assert.Equal(8, status.Exit);                                                                   // the same-volume warning only
        Assert.Matches(new Regex(@"last re-verification: \d+\.\d hour\(s\) ago"), status.Out);
        Assert.DoesNotContain("[FAILURE]", status.Out);
        // usage: an unknown destination is a configuration refusal, a bad option is a usage error
        Assert.Equal(3, (await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify", "--destination", "other")).Exit);
        Assert.Equal(2, (await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify", "--bogus")).Exit);
        Assert.Equal(3, (await AdminTool.RunWith(Options, Env(), "backup-replica-verify")).Exit);       // no destination configured
    }

    [PostgresFact]
    public async Task A_bit_flipped_in_a_copy_is_reported_once_with_2042_left_in_place_fails_the_status_and_is_cleared_by_a_fresh_copy()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        var dump = Dump(Path.Combine(Nas, run));
        FlipBit(dump);
        var flipped = File.ReadAllBytes(dump);

        var verify = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify");
        Assert.True(verify.Exit == 1, verify.All);
        Assert.Contains("CORRUPT (hash)", verify.Out);
        Assert.Contains("reported (audit 2042)", verify.Out);
        var corrupt = Assert.Single(await Events(BackupAuditEvents.ReplicaCorrupt));
        Assert.Equal((run, "nas", "hash"), (Token(corrupt, "run"), Token(corrupt, "destination"), Token(corrupt, "check")));
        Assert.DoesNotContain(work, corrupt);
        Assert.Equal(flipped, File.ReadAllBytes(dump));                                                   // never repaired, moved or deleted
        var again = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify");
        Assert.Equal(1, again.Exit);
        Assert.Contains("already reported earlier", again.Out);
        Assert.Single(await Events(BackupAuditEvents.ReplicaCorrupt));                                    // not reported again every night

        var status = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);
        Assert.Equal(1, status.Exit);
        Assert.Contains("[FAILURE] replica_corrupt destination=nas", status.Out);
        Assert.Contains("[FAILURE] replica_missing destination=nas", status.Out);                        // a corrupt copy does not count as a verified one

        // the operator moves the corrupt folder away (it stays evidence); the next backup-replicate writes a fresh copy, whose 2040 clears the report
        Directory.Move(Path.Combine(Nas, run), Path.Combine(work, "evidence-" + run));
        var moved = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);
        Assert.DoesNotContain("replica_corrupt", moved.Out);
        Assert.Contains("replica_missing", moved.Out);
        var replicate = await AdminTool.RunWith(Options, NasOnly(), "backup-replicate", Root);
        Assert.True(replicate.Exit == 8, replicate.All);
        var fresh = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);
        Assert.Equal(8, fresh.Exit);
        Assert.DoesNotContain("[FAILURE]", fresh.Out);
        Assert.True(File.Exists(Path.Combine(work, "evidence-" + run, "run.status.json")));
        Assert.Equal(0, (await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify")).Exit);     // the fresh copy verifies
    }

    [PostgresFact]
    public async Task The_default_selection_checks_the_newest_copy_and_all_checks_every_folder()
    {
        await Init();
        var one = await RunAsync();
        var two = await RunAsync();
        Assert.True(string.CompareOrdinal(one, two) < 0);                                                // the fixture's runs are strictly ordered by creation
        var (older, newest) = (one, two);
        Directory.CreateDirectory(Nas);
        await Replicate(older); await Replicate(newest);
        FlipBit(Dump(Path.Combine(Nas, older)));                                                          // damage only the OLDER copy
        var normal = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify");
        Assert.True(normal.Exit == 0, normal.All);                                                        // the one company's newest copy is the newer run
        Assert.Contains($"Re-verification {newest} at destination nas: VERIFIED", normal.Out);
        Assert.DoesNotContain(older, normal.Out);
        Assert.Empty(await Events(BackupAuditEvents.ReplicaCorrupt));
        var all = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify", "--all");
        Assert.Equal(1, all.Exit);
        Assert.Contains($"Re-verification {older} at destination nas: CORRUPT (hash)", all.Out);
        Assert.Equal(older, Token(Assert.Single(await Events(BackupAuditEvents.ReplicaCorrupt)), "run"));
    }

    [PostgresFact]
    public async Task Backup_status_with_destinations_needs_the_audit_stream_and_without_destinations_reads_no_database()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        var unreachable = NasOnly(); unreachable["Hasm__Database__Port"] = "1";
        var withDestination = await AdminTool.RunWith(Options, unreachable, "backup-status", Root);
        Assert.Equal(1, withDestination.Exit);
        Assert.Contains("[FAILURE] audit_unreadable", withDestination.Out);
        // no destination configured: no database is read, so the same unreachable database changes nothing
        var noDestination = Env(); noDestination["Hasm__Database__Port"] = "1";
        var plain = await AdminTool.RunWith(Options, noDestination, "backup-status", Root);
        Assert.DoesNotContain("audit_unreadable", plain.Out);
        Assert.Contains("Replicas: none", plain.Out);
        // a status on the destination folder itself reads none either
        var onDestination = await AdminTool.RunWith(Options, unreachable, "backup-status", Nas);
        Assert.DoesNotContain("audit_unreadable", onDestination.Out);
    }

    [PostgresFact]
    public async Task A_company_restores_from_a_destination_copy_to_the_same_catalog_and_content_as_from_the_primary()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        using var priv = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
        var keys = new RestoreKeys(priv, Trust);
        var service = new CompanyBackupService(Options, Attachments, Encryption);
        var sourceDigest = await DigestOf(Options.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
        var digests = new List<string>();
        foreach (var root in new[] { Root, Nas })
        {
            var set = Path.Combine(root, run, "companies", company.Id.ToString("N"));
            var r = await service.TrialRestoreAsync(set, company.Id, Path.Combine(work, "t-" + Guid.NewGuid().ToString("N")), keep: true, keys);
            try
            {
                Assert.True(r.Verdict == TrialRestoreVerdict.Ok, string.Join(", ", r.Failures.Concat(r.Warnings).Select(f => f.Code)));
                digests.Add(await DigestOf(Options.BuildCompanyAdminConnectionString(CompanyBackupService.ReadKeptFile(r.KeptFile!)!.Database)));
            }
            finally { if (r.KeptFile is not null) await service.DropTrialRestoreAsync(r.KeptFile); }
        }
        Assert.Equal(sourceDigest, digests[0]);                                                          // the primary restores to the source
        Assert.Equal(digests[0], digests[1]);                                                            // and so does the copy, without the primary
        // a damaged copy is refused by the restore itself before anything is decrypted
        FlipBit(Path.Combine(Nas, run, "companies", company.Id.ToString("N"), "database.dump.hbe"));
        var refused = await Assert.ThrowsAnyAsync<BackupException>(() => service.TrialRestoreAsync(Path.Combine(Nas, run, "companies", company.Id.ToString("N")), company.Id, Path.Combine(work, "t2"), keep: false, keys));
        Assert.False(string.IsNullOrEmpty(refused.Code));
    }

    [PostgresFact]
    public async Task A_destination_run_folder_that_is_a_link_to_the_primarys_run_is_corrupt_not_verified()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        Directory.Move(Path.Combine(Nas, run), Path.Combine(work, "real-copy"));
        Directory.CreateSymbolicLink(Path.Combine(Nas, run), Path.Combine(Root, run));              // review M1 of #214: a "copy" that is the primary itself
        var before = (await Events(BackupAuditEvents.ReplicaVerified)).Count;
        var verify = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify");
        Assert.True(verify.Exit == 1, verify.All);
        Assert.Contains("CORRUPT (link)", verify.Out);
        Assert.Equal("link", Token(Assert.Single(await Events(BackupAuditEvents.ReplicaCorrupt)), "check"));
        Assert.Equal(before, (await Events(BackupAuditEvents.ReplicaVerified)).Count);                // no 2040 for it
        var status = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);
        Assert.Equal(1, status.Exit);                                                                  // the status agrees with the verification
    }

    [PostgresFact]
    public async Task Without_a_trusted_signing_key_the_verification_is_a_configuration_error_and_reports_no_copy_corrupt()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        var noKey = NasOnly(); noKey["Hasm__Backup__SigningKeyDirectory"] = Path.Combine(work, "no-such-signing-dir");
        var verify = await AdminTool.RunWith(Options, noKey, "backup-replica-verify", "--all");
        Assert.Equal(3, verify.Exit);                                                                   // review L1 of #214
        Assert.Empty(await Events(BackupAuditEvents.ReplicaCorrupt));
        Assert.Single(await Events(BackupAuditEvents.ReplicaVerified));                                 // only the copy itself: nothing was re-verified or reported
        Assert.Equal(3, (await AdminTool.RunWith(Options, noKey, "backup-replicate", Root)).Exit);
    }

    [PostgresFact]
    public async Task A_copy_whose_file_is_a_hard_link_to_the_primarys_is_not_independent_in_the_status_and_corrupt_in_the_verification()
    {
        await Init();
        var run = await RunAsync();
        Directory.CreateDirectory(Nas);
        await Replicate(run);
        var ours = Dump(Path.Combine(Nas, run)); var theirs = Dump(Path.Combine(Root, run));
        File.Delete(ours);
        using (var ln = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { theirs, ours }, UseShellExecute = false })!) { ln.WaitForExit(); Assert.Equal(0, ln.ExitCode); }
        var status = await AdminTool.RunWith(Options, NasOnly(), "backup-status", Root);                  // review U1 of PR 214: the primary is known here
        Assert.Equal(1, status.Exit);
        Assert.Contains("[FAILURE] replica_not_independent destination=nas", status.Out);
        var verify = await AdminTool.RunWith(Options, NasOnly(), "backup-replica-verify");               // no primary here: the link count finds it
        Assert.True(verify.Exit == 1, verify.All);
        Assert.Contains("CORRUPT (link)", verify.Out);
        var replicate = await AdminTool.RunWith(Options, NasOnly(), "backup-replicate", Root);
        Assert.Contains("replica_not_independent", replicate.Out + replicate.Err);                         // and a copy over it is refused too
        Assert.True(replicate.Exit != 0);
    }

    private static async Task<string> DigestOf(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1", conn))
        await using (var r = await list.ExecuteReaderAsync()) while (await r.ReadAsync()) tables.Add(r.GetString(0));
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            var q = new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var cmd = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {q} x", conn);
            sb.Append(table).Append('=').Append((string)(await cmd.ExecuteScalarAsync())!).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
