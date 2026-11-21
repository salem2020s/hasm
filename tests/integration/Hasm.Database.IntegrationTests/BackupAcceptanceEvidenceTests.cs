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
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Release 1 acceptance evidence for owner item 11 (D-088, with D-085): "encryption of backup sets and authenticated manifests must be completed and tested". One consolidated suite on the disposable PostgreSQL
/// server, built on the merged slice 5 engine (no new production code), that runs a REAL encrypted backup of a company, restores it into a trial database with the offline recovery key and proves it equals
/// the source; proves that every tampering or downgrade is refused BEFORE anything reaches pg_restore (wrong recovery key, a flipped ciphertext byte, a truncated set, an edited manifest, a removed signature,
/// a version-1 unencrypted set, an unsigned or edited run status); proves key rotation (an older recovery key and a retired signing key still restore what they protected; a key retired before a set was
/// signed does not); and proves the key-export round trip (every ring, rotated keys included, comes back byte for byte with the offline private key). Trial-restore path only (not the slice 7 cutover).
/// Runs in the non-parallel backup collection because restores create and drop disposable databases.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class BackupAcceptanceEvidenceTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for the acceptance evidence";
    private const string Pass2 = "the passphrase of the rotated recovery key pair";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-bae-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private string AuditKeys => Path.Combine(work, "audit-keys");
    private string SigningDir => Path.Combine(work, "signing");
    private string Root => Path.Combine(work, "runs");
    private CompanyRegistry registry = null!;
    private RecoveryKeys.GeneratedPair pair = null!;
    private string publicKeyFile = null!, signingId = null!, signingPem = null!;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };
    private AttachmentOptions Attachments => new() { StorageDirectory = Storage };
    private CompanyBackupService Companies() => new(Options, Attachments, Encryption, nameGenerator: NewTestName);
    private static RecoveryPrivateKey PrivateOf(RecoveryKeys.GeneratedPair p, string pass) => RecoveryPrivateKey.FromEncryptedPem(p.EncryptedPrivateKeyPem, pass);
    private SigningTrust Trust => new([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Active)]);
    private BackupMaterial Material(RecoveryKeys.GeneratedPair? p = null, string? signing = null, string? signingDir = null) =>
        new(RecoveryPublicKey.FromPem((p ?? pair).PublicKeyPem), BackupSigningKeys.Load(signingDir ?? SigningDir, signing ?? signingId, true));
    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);

    private Dictionary<string, string?> Env(string activeKek = "k1") => new()
    {
        ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = activeKek, ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
        ["Hasm__Audit__KeyDirectory"] = AuditKeys, ["Hasm__Audit__ActiveKeyId"] = "a1", ["Hasm__Audit__CheckpointDirectory"] = Path.Combine(work, "checkpoints"),
        ["Hasm__Backup__RecoveryPublicKeyFile"] = publicKeyFile, ["Hasm__Backup__SigningKeyDirectory"] = SigningDir,
        ["Hasm__Backup__SigningKeyId"] = signingId, ["Hasm__Backup__AllowInsecureFilePermissions"] = "true",
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
        registry = new CompanyRegistry(SystemConnection, NewTestName);
    }

    /// <summary>A Ready company with a recognisable party row and one attachment file (its plaintext is the marker no backup file may contain).</summary>
    private async Task<(CompanyRecord Company, byte[] Attachment)> NewCompany()
    {
        var c = await registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        await Exec(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName), $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
                                                                               $"VALUES ('{Guid.NewGuid()}', 'P-EVIDENCE', 'acceptance evidence party', true, 1, 1, '{Guid.NewGuid()}', now(), now())");
        var folder = Path.Combine(Storage, c.Id.ToString("N")); Directory.CreateDirectory(folder);
        var attachment = Encoding.ASCII.GetBytes("%PDF-1.4 acceptance evidence attachment " + Guid.NewGuid());
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

    /// <summary>A catalog-and-content digest of a database (tables, exact row counts and row hashes), as in the slice 3 drill.</summary>
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

    /// <summary>Trial-restores <paramref name="set"/> and requires an Ok verdict with the attachment back byte for byte and the database equal to the source; the disposable database is dropped.</summary>
    private async Task RestoresAndEquals(string set, CompanyRecord c, byte[] attachment, RestoreKeys keys)
    {
        var target = Path.Combine(work, "t-" + Guid.NewGuid().ToString("N"));
        var r = await Companies().TrialRestoreAsync(set, c.Id, target, keep: true, keys);
        try
        {
            Assert.True(r.Verdict == TrialRestoreVerdict.Ok, string.Join(", ", r.Failures.Concat(r.Warnings).Select(f => f.Code)));
            Assert.True(r.Encrypted);
            Assert.Equal(attachment, File.ReadAllBytes(Assert.Single(Directory.GetFiles(target))));
            var kept = CompanyBackupService.ReadKeptFile(r.KeptFile!)!;
            Assert.Equal(await Digest(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName)), await Digest(Options.BuildCompanyAdminConnectionString(kept.Database)));
        }
        finally { if (r.KeptFile is not null) await Companies().DropTrialRestoreAsync(r.KeptFile); }
    }

    /// <summary>Requires the trial restore of <paramref name="set"/> to be REFUSED with <paramref name="code"/> before anything reaches pg_restore: no database and no attachment target are created.</summary>
    private async Task Refused(string set, Guid companyId, RestoreKeys keys, string code)
    {
        var before = await Databases();
        var target = Path.Combine(work, "r-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(set, companyId, target, false, keys));
        Assert.Equal(code, ex.Code);
        Assert.False(Directory.Exists(target));
        Assert.True(before.SetEquals(await Databases()), $"{code}: a database was created");
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    [PostgresFact]
    public async Task A_real_encrypted_backup_holds_no_plaintext_and_restores_into_a_trial_database_with_the_recovery_key_equal_to_the_source()
    {
        await Init();
        var (c, attachment) = await NewCompany();
        // a REAL whole-installation run, unattended: only the PUBLIC recovery key and the signing key are on the server
        var (report, runDir) = await new BackupRunService(Options, Attachments, Encryption).RunAsync(Root, Material());
        Assert.Equal(("ok", true), (report.Verdict, report.Encrypted));
        var set = Path.Combine(runDir, BackupRunFiles.CompaniesFolder, c.Id.ToString("N"));
        Assert.True(CompanyBackupService.IsEncryptedSet(set));
        var markers = new[] { "PGDMP"u8.ToArray(), "P-EVIDENCE"u8.ToArray(), attachment };
        foreach (var f in Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            Assert.All(markers, m => Assert.True(bytes.AsSpan().IndexOf(m) < 0, f));                                           // no plaintext dump, row or attachment at rest
        }
        // without the offline private key nothing restores; with it the set restores into a disposable trial database equal to the source
        Assert.Equal("recovery_key_required", (await Assert.ThrowsAsync<BackupException>(() => Companies().TrialRestoreAsync(set, c.Id, Path.Combine(work, "t0"), false))).Code);
        using var priv = PrivateOf(pair, Pass);
        await RestoresAndEquals(set, c, attachment, new RestoreKeys(priv, Trust));
        // the signed run status verifies with the server's public signing key
        var entry = Assert.Single(BackupRunStatus.List(Root, Trust));
        Assert.Equal(pair.KeyId, entry.RecoveryKeyId);
    }

    [PostgresFact]
    public async Task Every_tampering_of_an_encrypted_set_is_refused_before_anything_reaches_pg_restore()
    {
        await Init();
        var (c, attachment) = await NewCompany();
        var set = Path.Combine(work, "set");
        await Companies().BackupCompanyEncryptedAsync(c.Id, set, Material());
        using var priv = PrivateOf(pair, Pass);
        var keys = new RestoreKeys(priv, Trust);
        string Copy() { var to = Path.Combine(work, "copy-" + Guid.NewGuid().ToString("N")); CopyDirectory(set, to); return to; }

        // the wrong recovery key: another pair's private key, even with its correct passphrase
        var stranger = RecoveryKeys.Generate(Pass2);
        using (var wrong = PrivateOf(stranger, Pass2)) await Refused(set, c.Id, new RestoreKeys(wrong, Trust), "recovery_key_mismatch");

        // one flipped ciphertext byte in the dump
        var flipped = Copy();
        var dump = Path.Combine(flipped, EncryptedSets.DumpName);
        var bytes = File.ReadAllBytes(dump); bytes[bytes.Length / 2] ^= 0x01; File.WriteAllBytes(dump, bytes);
        await Refused(flipped, c.Id, keys, "backup_set_not_clean");

        // a truncated set: the dump cut short, and the dump removed
        var truncated = Copy();
        var cut = File.ReadAllBytes(Path.Combine(truncated, EncryptedSets.DumpName));
        File.WriteAllBytes(Path.Combine(truncated, EncryptedSets.DumpName), cut[..(cut.Length - 17)]);
        await Refused(truncated, c.Id, keys, "backup_set_not_clean");
        var missing = Copy();
        File.Delete(Path.Combine(missing, EncryptedSets.DumpName));
        await Refused(missing, c.Id, keys, "backup_set_not_clean");

        // an edited manifest (one byte of its ciphertext) and a removed manifest signature
        var edited = Copy();
        var manifest = Path.Combine(edited, SignedDocuments.ManifestFileName);
        var m = File.ReadAllBytes(manifest); m[^5] ^= 0x01; File.WriteAllBytes(manifest, m);
        await Refused(edited, c.Id, keys, "signature_invalid");
        var unsigned = Copy();
        File.Delete(Path.Combine(unsigned, SignedDocuments.ManifestFileName + SignedDocuments.SignatureSuffix));
        await Refused(unsigned, c.Id, keys, "signature_missing");

        // a downgrade: a version-1 (plaintext, unsigned) set of the same company in place of the encrypted one
        var downgraded = Path.Combine(work, "v1");
        await Companies().BackupCompanyAsync(c.Id, downgraded);
        Assert.False(CompanyBackupService.IsEncryptedSet(downgraded));
        await Refused(downgraded, c.Id, keys, "unencrypted_set_refused");

        // the untouched set still restores: every refusal above changed nothing
        await RestoresAndEquals(set, c, attachment, keys);
    }

    [PostgresFact]
    public async Task An_unsigned_or_edited_run_status_and_a_downgraded_run_never_count_as_a_good_backup()
    {
        await Init();
        var (c, _) = await NewCompany();
        var (_, runDir) = await new BackupRunService(Options, Attachments, Encryption).RunAsync(Root, Material());
        var status = Path.Combine(runDir, SignedDocuments.RunStatusFileName);
        var signature = status + SignedDocuments.SignatureSuffix;
        var genuineStatus = File.ReadAllBytes(status); var genuineSignature = File.ReadAllBytes(signature);
        string Code() => BackupRunStatus.Evaluate(BackupRunStatus.List(Root, Trust), DateTimeOffset.UtcNow, BackupRunStatus.DefaultMaxAge).Code;
        Assert.Equal("recovery_untested", Code());                                                                         // genuine, but not production-ready until a recovery test passes (D-085)

        File.Delete(signature);
        Assert.Equal("status_unsigned", Code());                                                                           // an unsigned status counts as a FAILED run
        File.WriteAllBytes(signature, genuineSignature);
        File.WriteAllBytes(status, [.. genuineStatus, (byte)' ']);                                                    // one byte more (insignificant JSON whitespace): the signed bytes changed
        Assert.Equal("status_unsigned", Code());                                                                           // an edited status no longer verifies
        File.WriteAllBytes(status, genuineStatus);
        Assert.Equal("recovery_untested", Code());

        // a downgraded run (a version-1 plaintext set planted in place of the encrypted company set) fails the recovery test and restores nothing of that company
        var companySet = Path.Combine(runDir, BackupRunFiles.CompaniesFolder, c.Id.ToString("N"));
        Directory.Delete(companySet, true);
        await Companies().BackupCompanyAsync(c.Id, companySet);
        using var priv = PrivateOf(pair, Pass);
        var before = await Databases();
        var result = await new RecoveryTestService(Options, Attachments, Encryption, nameGenerator: NewTestName).RunAsync(runDir, new RestoreKeys(priv, Trust), null, Path.Combine(work, "rt"));
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Code == "unencrypted_set_refused");
        Assert.DoesNotContain(c.Id, result.Companies);
        Assert.True(before.SetEquals(await Databases()));
        Assert.Equal("recovery_untested", Code());                                                                         // a failed recovery test never turns the status green
    }

    [PostgresFact]
    public async Task Rotated_recovery_and_signing_keys_still_restore_what_they_protected_and_a_retired_key_signs_nothing_new()
    {
        await Init();
        var (c, attachment) = await NewCompany();
        var oldSet = Path.Combine(work, "old");
        await Companies().BackupCompanyEncryptedAsync(c.Id, oldSet, Material());

        // rotation: a NEW recovery key pair and a NEW signing key; the old signing key is retired from now on, the old recovery private key is kept offline
        var pair2 = RecoveryKeys.Generate(Pass2);
        var (signing2, signing2Pem) = BackupSigningKeys.Generate(SigningDir);
        await Task.Delay(20);                                                                                              // the retirement time lies strictly after the old set's signed creation time
        var retiredAt = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        var rotatedTrust = new SigningTrust([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Retired, retiredAt), new TrustedSigningKey(signing2, signing2Pem, SigningKeyStatus.Active)]);
        var newSet = Path.Combine(work, "new");
        await Companies().BackupCompanyEncryptedAsync(c.Id, newSet, Material(pair2, signing2));
        var lateSet = Path.Combine(work, "late");
        await Companies().BackupCompanyEncryptedAsync(c.Id, lateSet, Material(pair2, signingId));                           // signed with the OLD key after its retirement

        using var oldPriv = PrivateOf(pair, Pass);
        using var newPriv = PrivateOf(pair2, Pass2);
        await RestoresAndEquals(oldSet, c, attachment, new RestoreKeys(oldPriv, rotatedTrust));                            // old recovery key id and retired signing key: the old set still restores
        await RestoresAndEquals(newSet, c, attachment, new RestoreKeys(newPriv, rotatedTrust));                            // the new pair restores the new set
        await Refused(newSet, c.Id, new RestoreKeys(oldPriv, rotatedTrust), "recovery_key_mismatch");                      // key ids are bound: the old private key does not open the new set
        await Refused(oldSet, c.Id, new RestoreKeys(newPriv, rotatedTrust), "recovery_key_mismatch");
        await Refused(lateSet, c.Id, new RestoreKeys(newPriv, rotatedTrust), "signing_key_retired");                       // a retired key signs nothing new
    }

    [PostgresFact]
    public async Task The_key_export_round_trips_every_ring_including_rotated_keys_and_recovers_lost_key_directories()
    {
        await Init();
        // rotation of the attachment and audit key rings: k2 and a2 become active, k1 and a1 stay for what they protected
        FileKekRing.Generate(Keys, "k2");
        FileAuditKeyRing.Generate(AuditKeys, "a2");
        var env = Env(activeKek: "k2");
        env["Hasm__Audit__ActiveKeyId"] = "a2";
        var export = Path.Combine(work, "kit", "keys");
        var run = await AdminTool.RunWith(Options, env, "backup-keys", export);
        Assert.True(run.Exit == 0, run.All);
        Assert.DoesNotContain("PRIVATE KEY", run.All);

        // the export is readable only with the offline recovery private key and the trust list it carries
        using var priv = PrivateOf(pair, Pass);
        var (manifest, archive) = await KeyExport.ReadAsync(export, BackupMaterialLoader.LoadTrustList(Path.Combine(export, KeyExport.TrustListName)), priv);
        Assert.Equal(["k1", "k2"], manifest.EncryptionKeyIds.Order());
        Assert.Equal(["a1", "a2"], manifest.AuditKeyIds.Order());
        Assert.Equal(pair.KeyId, manifest.RecoveryKeyId);
        Assert.All(archive.Files, f => Assert.DoesNotContain("PRIVATE KEY", f.Content));
        using (var stranger = PrivateOf(RecoveryKeys.Generate(Pass2), Pass2))
            Assert.Equal("recovery_key_mismatch", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(export, BackupMaterialLoader.LoadTrustList(Path.Combine(export, KeyExport.TrustListName)), stranger))).Code);

        // round trip: the key directories are lost; written back from the archive they are byte for byte the originals and load as rings again
        var originals = Directory.GetFiles(Keys).Concat(Directory.GetFiles(AuditKeys)).ToDictionary(f => (Path.GetDirectoryName(f) == Keys ? KeyExport.EncryptionRing : KeyExport.AuditRing, Path.GetFileName(f)), File.ReadAllText);
        var recoveredKeys = Path.Combine(work, "recovered", "keys"); var recoveredAudit = Path.Combine(work, "recovered", "audit-keys");
        Directory.CreateDirectory(recoveredKeys); Directory.CreateDirectory(recoveredAudit);
        foreach (var f in archive.Files.Where(f => f.Ring is KeyExport.EncryptionRing or KeyExport.AuditRing))
        {
            var (dir, ext) = f.Ring == KeyExport.EncryptionRing ? (recoveredKeys, FileKekRing.Extension) : (recoveredAudit, FileAuditKeyRing.Extension);
            File.WriteAllText(Path.Combine(dir, f.KeyId + ext), f.Content);
        }
        var recovered = Directory.GetFiles(recoveredKeys).Concat(Directory.GetFiles(recoveredAudit)).ToDictionary(f => (Path.GetDirectoryName(f) == recoveredKeys ? KeyExport.EncryptionRing : KeyExport.AuditRing, Path.GetFileName(f)), File.ReadAllText);
        Assert.Equal(originals.OrderBy(kv => kv.Key.ToString()), recovered.OrderBy(kv => kv.Key.ToString()));
        Assert.Equal(1L, await new NpgsqlConnection(SystemConnection).ExecuteCount($"SELECT count(*) FROM {SystemSchema.AuditEvent.Table} WHERE {SystemSchema.AuditEvent.EventType} = {BackupAuditEvents.KeysExported}"));
    }
}

internal static class EvidenceSql
{
    public static async Task<long> ExecuteCount(this NpgsqlConnection conn, string sql)
    {
        await using (conn)
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
    }
}
