using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 5 on the disposable PostgreSQL server (D-089..D-095, D-116..D-118): encrypted, signed backups with no plaintext at rest, trial restore with the offline recovery key, refusal of forged
/// sets before anything reaches pg_restore, <c>backup-keys</c> and <c>backup-signing-keygen</c>, the recovery test end to end with the canary, the system audit events, and encryption by default. The
/// Hasm.Admin restores name their disposable databases opaquely (production generator) and drop them, so the class runs in the NON-PARALLEL backup collection.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class EncryptedBackupTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for tests";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-ebt-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private string AuditKeys => Path.Combine(work, "audit-keys");
    private string SigningDir => Path.Combine(work, "signing");
    private string Kit => Path.Combine(work, "kit");
    private string Root => Path.Combine(work, "runs");
    private CompanyRegistry registry = null!;
    private RecoveryKeys.GeneratedPair pair = null!;
    private string privateKeyFile = null!, publicKeyFile = null!, signingId = null!, signingPem = null!;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };
    private AttachmentOptions Attachments => new() { StorageDirectory = Storage };
    private CompanyBackupService Companies() => new(Options, Attachments, Encryption, nameGenerator: NewTestName);
    private RecoveryPublicKey Public() => RecoveryPublicKey.FromPem(pair.PublicKeyPem);
    private RecoveryPrivateKey Private() => RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
    private SigningTrust Trust => new([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Active)]);
    private BackupMaterial Material() => new(Public(), BackupSigningKeys.Load(SigningDir, signingId, true));
    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);

    private Dictionary<string, string?> Env(bool encrypted = true) => new()
    {
        ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
        ["Hasm__Audit__KeyDirectory"] = AuditKeys, ["Hasm__Audit__ActiveKeyId"] = "a1", ["Hasm__Audit__CheckpointDirectory"] = Path.Combine(work, "checkpoints"),
        ["Hasm__Backup__RecoveryPublicKeyFile"] = encrypted ? publicKeyFile : null, ["Hasm__Backup__SigningKeyDirectory"] = encrypted ? SigningDir : null,
        ["Hasm__Backup__SigningKeyId"] = encrypted ? signingId : null, ["Hasm__Backup__AllowInsecureFilePermissions"] = "true",
    };

    private async Task Init()
    {
        Directory.CreateDirectory(work); Directory.CreateDirectory(Storage);
        FileKekRing.Generate(Keys, "k1");
        FileAuditKeyRing.Generate(AuditKeys, "a1");
        pair = RecoveryKeys.Generate(Pass);
        RecoveryKit.Write(Path.Combine(work, "recovery"), pair);
        publicKeyFile = Path.Combine(work, "recovery", pair.KeyId + ".pub.pem");
        privateKeyFile = Path.Combine(work, "recovery", "copy-1", pair.KeyId + ".key.pem");
        (signingId, signingPem) = BackupSigningKeys.Generate(SigningDir);
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        registry = new CompanyRegistry(SystemConnection, NewTestName);
    }

    /// <summary>A Ready company with one local party, one attachment file with a row-less storage name (an unreferenced extra, harmless) and its plaintext marker.</summary>
    private async Task<(CompanyRecord Company, byte[] Attachment)> NewCompany()
    {
        var c = await registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        await Exec(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName), $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
                                                                               $"VALUES ('{Guid.NewGuid()}', 'P-PLAIN-MARKER', 'plaintext party marker', true, 1, 1, '{Guid.NewGuid()}', now(), now())");
        var folder = Path.Combine(Storage, c.Id.ToString("N")); Directory.CreateDirectory(folder);
        var attachment = Encoding.ASCII.GetBytes("%PDF-1.4 plaintext attachment marker " + Guid.NewGuid());
        File.WriteAllBytes(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".att"), attachment);
        return ((await registry.GetAsync(c.Id))!, attachment);
    }

    private static async Task Exec(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<HashSet<string>> Databases()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT datname FROM pg_database", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    private async Task<long> AuditEvents(short type) => await Scalar<long>(SystemConnection, $"SELECT count(*) FROM {E.Table} WHERE {E.EventType} = {type}");

    private static async Task<string> Digest(string connection)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1", conn))
        await using (var r = await list.ExecuteReaderAsync()) while (await r.ReadAsync()) tables.Add(r.GetString(0));
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {new NpgsqlCommandBuilder().QuoteIdentifier(table)} x", conn);
            sb.Append(table).Append('=').Append((string)(await cmd.ExecuteScalarAsync())!).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [PostgresFact]
    public async Task The_key_inventory_of_an_encrypted_set_includes_the_keys_of_encrypted_cash_numbers()
    {
        await Init();
        var (c, _) = await NewCompany();
        await Exec(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName), CashTestSql.BankRow("NB-1", "1101", "k1"));
        await Exec(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName), CashTestSql.BankRow("NB-9", "1102", "k9"));
        var backup = await Companies().BackupCompanyEncryptedAsync(c.Id, Path.Combine(work, "set-cash"), Material());
        Assert.Equal(new[] { "k1", "k9" }, backup.Manifest.Company!.KeyIdsRequired);
        Assert.Equal(new[] { "k9" }, backup.MissingKeyIds);
    }

    [PostgresFact]
    public async Task An_encrypted_run_holds_no_plaintext_and_its_signed_status_needs_a_recovery_test()
    {
        await Init();
        var (c, attachment) = await NewCompany();
        var material = Material();
        var (report, runDir) = await new BackupRunService(Options, Attachments, Encryption).RunAsync(Root, material);
        Assert.Equal("ok", report.Verdict);
        Assert.True(report.Encrypted);

        // D-091: only ciphertext, signatures, the signed readable status and the signed ciphertext inventory (names, sizes and SHA-256 of the ciphertext, D-185 as amended); no dump, manifest, report or marker in plaintext
        var files = Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(runDir, f).Replace('\\', '/')).ToList();
        Assert.All(files, f => Assert.True(f.EndsWith(".hbe") || f.EndsWith(".sig") || f == SignedDocuments.RunStatusFileName || f == CiphertextDigests.FileName, f));
        Assert.Contains("system/database.dump.hbe", files);
        Assert.Contains($"companies/{c.Id:N}/database.dump.hbe", files);
        Assert.Single(files, f => f.StartsWith($"companies/{c.Id:N}/attachments/"));
        Assert.DoesNotContain(BackupRunFiles.ReportFileName, files);
        Assert.DoesNotContain(BackupRunFiles.UnencryptedMarker, files);
        var markers = new[] { "PGDMP"u8.ToArray(), "P-PLAIN-MARKER"u8.ToArray(), attachment };
        foreach (var f in Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            Assert.All(markers, m => Assert.False(Contains(bytes, m), f));
        }
        var status = File.ReadAllText(Path.Combine(runDir, SignedDocuments.RunStatusFileName));
        Assert.DoesNotContain(c.PhysicalDatabaseName, status);

        // monitoring verifies the signed status with the server's public keys; the key is not production-ready until a recovery test passes (D-085)
        var entry = Assert.Single(BackupRunStatus.List(Root, Trust));
        Assert.Equal(pair.KeyId, entry.RecoveryKeyId);
        var s = BackupRunStatus.Evaluate([entry], DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge);
        Assert.Equal((8, "recovery_untested"), (s.ExitCode, s.Code));
        var unsigned = Assert.Single(BackupRunStatus.List(Root, null));
        Assert.Equal("status_unsigned", BackupRunStatus.Evaluate([unsigned], DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge).Code);
    }

    [PostgresFact]
    public async Task An_encrypted_set_trial_restores_with_the_private_key_and_equals_the_source()
    {
        await Init();
        var (c, attachment) = await NewCompany();
        var set = Path.Combine(work, "set");
        var backup = await Companies().BackupCompanyEncryptedAsync(c.Id, set, Material());
        Assert.Equal(pair.KeyId, backup.Manifest.RecoveryKeyId);
        Assert.Equal(signingId, backup.Manifest.SigningKeyId);
        Assert.Equal("recovery_key_required", (await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(set, c.Id, Path.Combine(work, "t0"), false))).Code);

        using var priv = Private();
        var target = Path.Combine(work, "t1");
        var r = await Companies().TrialRestoreAsync(set, c.Id, target, keep: true, new RestoreKeys(priv, Trust));
        try
        {
            Assert.True(r.Verdict == TrialRestoreVerdict.Ok, string.Join(", ", r.Failures.Concat(r.Warnings).Select(f => f.Code)));
            Assert.True(r.Encrypted);
            Assert.Equal(1, r.AttachmentFiles);
            Assert.Equal(1, r.UnreferencedFiles);
            Assert.Matches("^[0-9a-f]{12}$", r.DatabaseTag!);
            Assert.Equal(attachment, File.ReadAllBytes(Assert.Single(Directory.GetFiles(target))));
            var kept = CompanyBackupService.ReadKeptFile(r.KeptFile!)!;
            Assert.Equal(BackupAudit.DatabaseTag(kept.Database), r.DatabaseTag);
            Assert.Equal(await Digest(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName)), await Digest(Options.BuildCompanyAdminConnectionString(kept.Database)));
        }
        finally { await Companies().DropTrialRestoreAsync(r.KeptFile!); }
    }

    [PostgresFact]
    public async Task A_forged_unsigned_or_swapped_set_is_refused_before_anything_reaches_pg_restore()
    {
        await Init();
        var (a, _) = await NewCompany();
        var (b, _) = await NewCompany();
        var setA = Path.Combine(work, "a"); var setB = Path.Combine(work, "b");
        await Companies().BackupCompanyEncryptedAsync(a.Id, setA, Material());
        await Companies().BackupCompanyEncryptedAsync(b.Id, setB, Material());
        using var priv = Private();
        var keys = new RestoreKeys(priv, Trust);
        var before = await Databases();
        async Task<string> Refused(string set)
        {
            var target = Path.Combine(work, "t-" + Guid.NewGuid().ToString("N"));
            var code = (await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(set, a.Id, target, false, keys))).Code;
            Assert.False(Directory.Exists(target));
            Assert.True(before.SetEquals(await Databases()));                                                              // nothing was created: refused before pg_restore
            return code;
        }

        // H1: B's dump is a VALID HBE1 file for the same recovery key, purpose and name, but not the file A's signed manifest names
        var dumpA = Path.Combine(setA, EncryptedSets.DumpName);
        var original = File.ReadAllBytes(dumpA);
        File.Delete(dumpA); File.Copy(Path.Combine(setB, EncryptedSets.DumpName), dumpA);
        Assert.Equal("backup_set_not_clean", await Refused(setA));
        File.Delete(dumpA); File.WriteAllBytes(dumpA, original);

        // a manifest signed by a key the operator's kit does not trust (an attacker holding only the PUBLIC recovery key can produce valid HBE1 files, never this signature)
        var manifestSig = Path.Combine(setA, SignedDocuments.ManifestFileName + SignedDocuments.SignatureSuffix);
        var genuineSig = File.ReadAllBytes(manifestSig);
        var (otherId, _) = BackupSigningKeys.Generate(Path.Combine(work, "attacker"));
        using (var attacker = BackupSigningKeys.Load(Path.Combine(work, "attacker"), otherId, true))
        {
            File.Delete(manifestSig);
            File.WriteAllBytes(manifestSig, attacker.Sign(File.ReadAllBytes(Path.Combine(setA, SignedDocuments.ManifestFileName))).ToBytes());
        }
        Assert.Equal("signing_key_unknown", await Refused(setA));
        File.Delete(manifestSig);
        Assert.Equal("signature_missing", await Refused(setA));
        File.WriteAllBytes(manifestSig, genuineSig);

        // B's set under A's id, and a compromised signing key in the kit
        Assert.Equal("company_mismatch", (await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(setB, a.Id, Path.Combine(work, "tb"), false, keys))).Code);
        var compromised = new RestoreKeys(priv, new SigningTrust([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Compromised)]));
        Assert.Equal("signing_key_compromised", (await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(setA, a.Id, Path.Combine(work, "tc"), false, compromised))).Code);
        Assert.True(before.SetEquals(await Databases()));

        // review H1 of #128: an unencrypted, unsigned version-1 set of A planted in an ENCRYPTED run is never restored by the recovery test, and the test does not pass
        var (_, runDir) = await new BackupRunService(Options, Attachments, Encryption).RunAsync(Root, Material());
        var recovery = new RecoveryTestService(Options, Attachments, Encryption, nameGenerator: NewTestName);
        var aSet = Path.Combine(runDir, BackupRunFiles.CompaniesFolder, a.Id.ToString("N"));
        var genuine = Path.Combine(work, "genuine-a");
        Directory.Move(aSet, genuine);
        await Companies().BackupCompanyAsync(a.Id, aSet);                                                              // version 1: plaintext dump, no signature
        Assert.False(CompanyBackupService.IsEncryptedSet(aSet));
        before = await Databases();
        var planted = await recovery.RunAsync(runDir, keys, null, Path.Combine(work, "rt1"));
        Assert.False(planted.Passed);
        Assert.Contains(planted.Failures, f => f.Code == "unencrypted_set_refused");
        Assert.DoesNotContain(a.Id, planted.Companies);                                                               // A was never restored
        Assert.True(before.SetEquals(await Databases()));
        Assert.Equal("unencrypted_set_refused", (await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(aSet, a.Id, Path.Combine(work, "td"), false, keys))).Code);   // the library itself refuses

        // review L1 of #128: a validly signed set of ANOTHER run is refused (run_mismatch), and company folders must match the signed status
        Directory.Delete(aSet, true);
        var (_, otherRun) = await new BackupRunService(Options, Attachments, Encryption).RunAsync(Root, Material());
        Directory.Move(Path.Combine(otherRun, BackupRunFiles.CompaniesFolder, a.Id.ToString("N")), aSet);
        var older = await recovery.RunAsync(runDir, keys, null, Path.Combine(work, "rt2"));
        Assert.False(older.Passed);
        Assert.Contains(older.Failures, f => f.Code == "run_mismatch");
        Directory.Delete(aSet, true);
        Directory.Move(genuine, aSet);
        Directory.CreateDirectory(Path.Combine(runDir, BackupRunFiles.CompaniesFolder, Guid.NewGuid().ToString("N")));
        var extra = await recovery.RunAsync(runDir, keys, null, Path.Combine(work, "rt3"));
        Assert.False(extra.Passed);
        Assert.Contains(extra.Failures, f => f.Code == "company_sets_mismatch");
        Assert.True(before.SetEquals(await Databases()));
    }

    [PostgresFact]
    public async Task Backup_keys_and_signing_keygen_are_audited_export_public_signing_keys_only_and_recover()
    {
        await Init();
        var env = Env();
        var newSigning = Path.Combine(work, "signing2");
        var keygen = await AdminTool.RunWith(Options, new Dictionary<string, string?>(env) { ["Hasm__Backup__SigningKeyDirectory"] = newSigning }, "backup-signing-keygen");
        Assert.True(keygen.Exit == 0, keygen.All);
        var generated = Assert.Single(Directory.GetFiles(newSigning, "sk-*.key.pem"));
        Assert.Contains(Path.GetFileName(generated)[..^".key.pem".Length], keygen.Out);
        Assert.DoesNotContain("PRIVATE KEY", keygen.All);
        Assert.Equal(1, await AuditEvents(BackupAuditEvents.SigningKeyGenerated));

        var export = Path.Combine(Kit, "keys");
        var run = await AdminTool.RunWith(Options, env, "backup-keys", export);
        Assert.True(run.Exit == 0, run.All);
        Assert.Equal(1, await AuditEvents(BackupAuditEvents.KeysExported));
        using var priv = Private();
        var (m, archive) = await KeyExport.ReadAsync(export, BackupMaterialLoader.LoadTrustList(Path.Combine(export, KeyExport.TrustListName)), priv);
        Assert.Equal(["k1"], m.EncryptionKeyIds);
        Assert.Equal(["a1"], m.AuditKeyIds);
        Assert.Equal([signingId], m.SigningPublicKeyIds);
        Assert.All(archive.Files, f => Assert.DoesNotContain("PRIVATE KEY", f.Content));                                // review round 2 N2
        Assert.Equal(File.ReadAllText(Path.Combine(Keys, "k1" + FileKekRing.Extension)), archive.Files.Single(f => f.Ring == KeyExport.EncryptionRing).Content);

        // never inside a data or key directory; never without the recovery key, even under the DEV/TEST acknowledgement
        var overlap = await AdminTool.RunWith(Options, env, "backup-keys", Path.Combine(Keys, "export"));
        Assert.Equal(1, overlap.Exit);
        Assert.Contains("destination_overlaps_source", overlap.Err);
        var noKey = await AdminTool.RunWith(Options, new Dictionary<string, string?>(Env(encrypted: false)) { ["Hasm__Backup__UnencryptedDevTestOnly"] = BackupOptions.UnencryptedAcknowledgement },
            "backup-keys", Path.Combine(Kit, "keys2"));
        Assert.Equal(3, noKey.Exit);
        Assert.Contains("backup_encryption_not_configured", noKey.Err);
        Assert.Equal(1, await AuditEvents(BackupAuditEvents.KeysExported));
    }

    [PostgresFact]
    public async Task The_recovery_test_restores_the_whole_run_writes_the_preimage_proof_turns_status_green_and_is_audited()
    {
        await Init();
        var (c, _) = await NewCompany();
        var env = Env();
        var backup = await AdminTool.RunWith(Options, env, "backup-run", Root);
        Assert.True(backup.Exit == 0, backup.All);
        var runDir = Assert.Single(Directory.GetDirectories(Root));
        var status = await AdminTool.RunWith(Options, env, "backup-status", Root);
        Assert.Equal(8, status.Exit);
        Assert.Contains("recovery_untested", status.Out);
        Assert.True((await AdminTool.RunWith(Options, env, "backup-keys", Kit)).Exit == 0);

        var before = await Databases();
        string[] args = ["backup-recovery-test", runDir, "--recovery-key", privateKeyFile, "--signing-trust", Path.Combine(Kit, KeyExport.TrustListName), "--keys-export", Kit, "--passphrase-stdin"];
        var wrong = await AdminTool.RunWithInput(Options, env, "not the passphrase at all, sorry\n", args);
        Assert.Equal(1, wrong.Exit);
        Assert.Contains("recovery_key_unreadable", wrong.Err);
        Assert.False(File.Exists(Path.Combine(runDir, RecoveryTestProof.FileName)));

        var test = await AdminTool.RunWithInput(Options, env, Pass + "\n", args);
        Assert.True(test.Exit == 0, test.All);
        Assert.Contains("PASSED", test.Out);
        Assert.Contains("key export checked", test.Out);
        Assert.True(before.SetEquals(await Databases()));                                                                     // every disposable database was dropped
        Assert.DoesNotContain(c.PhysicalDatabaseName, test.All);
        Assert.DoesNotContain(Pass, test.All);
        var proof = File.ReadAllText(Path.Combine(runDir, RecoveryTestProof.FileName));
        Assert.Contains("canaryPreimage", proof);
        Assert.Equal(2, await AuditEvents(BackupAuditEvents.RecoveryTest));                                          // review L3 of #128: the refused attempt is audited too
        Assert.Equal(1L, await Scalar<long>(SystemConnection, $"SELECT count(*) FROM {E.Table} WHERE {E.EventType} = {BackupAuditEvents.RecoveryTest} AND {E.Detail} LIKE '%outcome=refused code=recovery_key_unreadable%'"));
        Assert.Equal(0L, await Scalar<long>(SystemConnection, $"SELECT count(*) FROM {E.Table} WHERE {E.EventType} = {BackupAuditEvents.RecoveryTest} AND {E.Detail} LIKE '%{c.PhysicalDatabaseName}%'"));

        var green = await AdminTool.RunWith(Options, env, "backup-status", Root);
        Assert.True(green.Exit == 0, green.All);
        Assert.Contains("Backup status: ok", green.Out);
    }

    [PostgresFact]
    public async Task Encryption_is_required_by_default_and_an_unencrypted_set_is_dev_test_only()
    {
        await Init();
        var (c, _) = await NewCompany();
        var plain = Env(encrypted: false);
        foreach (var args in new[] { new[] { "backup-run", Root }, new[] { "backup-company", c.Id.ToString(), Path.Combine(work, "s0") } })
        {
            var refused = await AdminTool.RunWith(Options, plain, args);
            Assert.Equal(3, refused.Exit);
            Assert.Contains("backup_encryption_not_configured", refused.Err);
        }
        Assert.False(Directory.Exists(Root));
        var contradictory = await AdminTool.RunWith(Options, new Dictionary<string, string?>(Env()) { ["Hasm__Backup__UnencryptedDevTestOnly"] = BackupOptions.UnencryptedAcknowledgement },
            "backup-company", c.Id.ToString(), Path.Combine(work, "s1"));
        Assert.Equal(3, contradictory.Exit);
        Assert.Contains("unencrypted_ack_with_recovery_key", contradictory.Err);

        // the DEV/TEST path: explicit, marked, DEGRADED; and a reader without the acknowledgement refuses the unencrypted set
        var ack = new Dictionary<string, string?>(plain) { ["Hasm__Backup__UnencryptedDevTestOnly"] = BackupOptions.UnencryptedAcknowledgement };
        var set = Path.Combine(work, "s2");
        var dev = await AdminTool.RunWith(Options, ack, "backup-company", c.Id.ToString(), set);
        Assert.True(dev.Exit == 0, dev.All);
        Assert.Contains("DEGRADED", dev.Err);
        Assert.True(File.Exists(Path.Combine(set, BackupRunFiles.UnencryptedMarker)));
        var reader = await AdminTool.RunWith(Options, Env(), "trial-restore-company", set, c.Id.ToString(), Path.Combine(work, "t2"));
        Assert.Equal(1, reader.Exit);
        Assert.Contains("unencrypted_set_refused", reader.Err);
        // review L3 of #128: the refused trial restore is audited (outcome refused and its code), nothing restored
        Assert.Equal(1, await AuditEvents(BackupAuditEvents.TrialRestore));
        Assert.Equal(1L, await Scalar<long>(SystemConnection, $"SELECT count(*) FROM {E.Table} WHERE {E.EventType} = {BackupAuditEvents.TrialRestore} AND {E.Detail} LIKE '%outcome=refused code=unencrypted_set_refused%'"));
    }
}
