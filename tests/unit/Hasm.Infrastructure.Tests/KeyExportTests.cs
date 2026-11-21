using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 5, <c>backup-keys</c> (D-087, D-094; review M4 and round 2 N2 of #116): the export holds the encryption and audit key rings and the signing PUBLIC keys with their status, never a
/// signing private key; it is encrypted to the recovery key and signed; key recovery verifies and decrypts it; the destination never overlaps a data or key directory or a backup root.
/// </summary>
public sealed class KeyExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-keyexp-" + Guid.NewGuid().ToString("N"));
    private const string Pass = "a recovery passphrase of twenty+";
    private readonly RecoveryPublicKey pub;
    private readonly RecoveryPrivateKey priv;
    private readonly string enc, audit, sign, signingId, signingPem;

    public KeyExportTests()
    {
        Directory.CreateDirectory(root);
        var pair = RecoveryKeys.Generate(Pass);
        pub = RecoveryPublicKey.FromPem(pair.PublicKeyPem);
        priv = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
        enc = Path.Combine(root, "enc"); audit = Path.Combine(root, "audit"); sign = Path.Combine(root, "sign");
        FileKekRing.Generate(enc, "k1"); FileKekRing.Generate(enc, "k2");
        FileAuditKeyRing.Generate(audit, "a1");
        BackupSigningKeys.Generate(sign);                                  // an older signing key
        (signingId, signingPem) = BackupSigningKeys.Generate(sign);
        File.WriteAllText(Path.Combine(enc, "notes.txt"), "not a key");
    }

    public void Dispose() { pub.Dispose(); priv.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private BackupMaterial Material() => new(pub, BackupSigningKeys.Load(sign, signingId, true));
    private SigningTrust Trust(SigningKeyStatus status = SigningKeyStatus.Active, DateTimeOffset? retired = null) => new([new TrustedSigningKey(signingId, signingPem, status, retired)]);

    [Fact]
    public async Task The_export_holds_every_ring_and_only_signing_public_keys_and_recovers_with_the_private_key()
    {
        var dest = Path.Combine(root, "export");
        var m = await KeyExport.WriteAsync(dest, enc, audit, sign, Material(), DateTimeOffset.UtcNow);
        Assert.Equal(["k1", "k2"], m.EncryptionKeyIds);
        Assert.Equal(["a1"], m.AuditKeyIds);
        Assert.Equal(2, m.SigningPublicKeyIds.Count);
        Assert.Contains(signingId, m.SigningPublicKeyIds);
        Assert.Equal(pub.KeyId, m.RecoveryKeyId);
        Assert.Equal(signingId, m.SigningKeyId);

        // nothing readable but the manifest, its signature and the public trust list; no key material in plaintext
        var names = Directory.EnumerateFiles(dest).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { KeyExport.ArchiveName, KeyExport.ManifestName, KeyExport.ManifestName + ".sig", KeyExport.TrustListName }.Order(StringComparer.Ordinal), names);
        var kekText = File.ReadAllText(Path.Combine(enc, "k1" + FileKekRing.Extension)).Trim();
        Assert.All(Directory.EnumerateFiles(dest), f => Assert.DoesNotContain(kekText, File.ReadAllText(f)));
        Assert.All(Directory.EnumerateFiles(dest), f => Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(f)));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dest));

        var (read, archive) = await KeyExport.ReadAsync(dest, BackupMaterialLoader.LoadTrustList(Path.Combine(dest, KeyExport.TrustListName)), priv);
        Assert.Equal(m.EncryptionKeyIds, read.EncryptionKeyIds);
        Assert.Equal(kekText, archive.Files.Single(f => f.Ring == KeyExport.EncryptionRing && f.KeyId == "k1").Content.Trim());
        // N2 pin: no ring for signing private keys, and no private-key PEM anywhere in the archive
        Assert.All(archive.Files, f => Assert.Contains(f.Ring, new[] { KeyExport.EncryptionRing, KeyExport.AuditRing, KeyExport.SigningPublicRing }));
        Assert.All(archive.Files, f => Assert.DoesNotContain("PRIVATE KEY", f.Content));
        Assert.Equal(2, archive.SigningTrust.Count);
    }

    [Fact]
    public async Task A_tampered_unsigned_or_foreign_export_is_refused()
    {
        var dest = Path.Combine(root, "export");
        await KeyExport.WriteAsync(dest, enc, audit, sign, Material(), DateTimeOffset.UtcNow);
        var archivePath = Path.Combine(dest, KeyExport.ArchiveName);
        var original = File.ReadAllBytes(archivePath);
        var altered = (byte[])original.Clone(); altered[^5] ^= 1;
        File.WriteAllBytes(archivePath, altered);
        Assert.Equal("backup_set_not_clean", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(dest, Trust(), priv))).Code);
        File.WriteAllBytes(archivePath, original);
        Assert.Equal("signing_key_compromised", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(dest, Trust(SigningKeyStatus.Compromised), priv))).Code);
        Assert.Equal("signing_key_retired", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(dest, Trust(SigningKeyStatus.Retired, DateTimeOffset.UtcNow.AddDays(-1)), priv))).Code);
        var manifest = Path.Combine(dest, KeyExport.ManifestName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"k2\"", "\"k3\""));
        Assert.Equal("signature_invalid", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(dest, Trust(), priv))).Code);
        File.Delete(manifest + ".sig");
        Assert.Equal("signature_missing", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(dest, Trust(), priv))).Code);

        // the wrong recovery key
        var other = RecoveryKeys.Generate(Pass);
        using var otherPriv = RecoveryPrivateKey.FromEncryptedPem(other.EncryptedPrivateKeyPem, Pass);
        var dest2 = Path.Combine(root, "export2");
        await KeyExport.WriteAsync(dest2, enc, audit, sign, Material(), DateTimeOffset.UtcNow);
        Assert.Equal("recovery_key_mismatch", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.ReadAsync(dest2, Trust(), otherPriv))).Code);
    }

    [Fact]
    public async Task The_destination_overlaps_no_key_or_data_directory_and_no_backup_root()
    {
        var storage = Path.Combine(root, "storage"); Directory.CreateDirectory(storage);
        string?[] protectedDirs = [storage, enc, audit, sign];
        foreach (var bad in new[] { Path.Combine(enc, "x"), Path.Combine(storage, "x"), Path.Combine(sign, "x"), audit, root })
            Assert.Equal("destination_overlaps_source", Assert.Throws<BackupException>(() => KeyExport.CheckDestination(bad, protectedDirs)).Code);
        var backupRoot = Path.Combine(root, "backups"); Directory.CreateDirectory(Path.Combine(backupRoot, "20261121T020000Z-0011aabb"));
        Assert.Equal("destination_in_backup_root", Assert.Throws<BackupException>(() => KeyExport.CheckDestination(Path.Combine(backupRoot, "keys"), protectedDirs)).Code);
        Assert.Equal("destination_in_backup_root", Assert.Throws<BackupException>(() => KeyExport.CheckDestination(Path.Combine(backupRoot, "20261121T020000Z-0011aabb", "keys"), protectedDirs)).Code);
        KeyExport.CheckDestination(Path.Combine(root, "kit", "keys"), protectedDirs);
        Assert.Equal("path_not_absolute", Assert.Throws<BackupException>(() => KeyExport.CheckDestination("relative/keys", protectedDirs)).Code);

        // a non-empty destination is refused and left untouched
        var used = Path.Combine(root, "used"); Directory.CreateDirectory(used); File.WriteAllText(Path.Combine(used, "a"), "a");
        Assert.Equal("destination_not_empty", (await Assert.ThrowsAsync<BackupException>(() => KeyExport.WriteAsync(used, enc, audit, sign, Material(), DateTimeOffset.UtcNow))).Code);
        Assert.True(File.Exists(Path.Combine(used, "a")));
    }

    [Fact]
    public void Backup_audit_details_name_the_operator_safely_and_tag_databases()
    {
        Assert.Matches("^os:[A-Za-z0-9._-]{1,64}$", BackupAudit.Operator());
        var tag = BackupAudit.DatabaseTag("hasm_c_0011aabb");
        Assert.Matches("^[0-9a-f]{12}$", tag);
        Assert.DoesNotContain("hasm", tag);
        Assert.Equal([2010, 2011, 2012, 2013], new[] { BackupAuditEvents.TrialRestore, BackupAuditEvents.RecoveryTest, BackupAuditEvents.KeysExported, BackupAuditEvents.SigningKeyGenerated }.Select(x => (int)x));
    }
}
