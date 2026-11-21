using System.Security.Cryptography;
using System.Text.Json;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The signed, READABLE manifest of a key export (D-087, D-094; review M4 and round 2 N2 of #116): the key ids it holds and the digest of the encrypted archive. Key ids are not secret; the keys are only
/// inside <c>keys.hbe</c>, encrypted to the offline recovery key.
/// </summary>
public sealed record KeyExportManifest(int Version, string Kind, DateTimeOffset CreatedUtc, string RecoveryKeyId, string SigningKeyId, IReadOnlyList<string> EncryptionKeyIds,
    IReadOnlyList<string> AuditKeyIds, IReadOnlyList<string> SigningPublicKeyIds, EncryptedFileEntry Archive)
{
    public const int CurrentVersion = 1;
    public const string KindValue = "key-export";
}

/// <summary>One key file inside the encrypted archive. <see cref="Ring"/> is <c>encryption</c>, <c>audit</c> or <c>signing-public</c>; there is no ring for signing PRIVATE keys (N2).</summary>
public sealed record KeyArchiveFile(string Ring, string KeyId, string Content);

/// <summary>The decrypted archive: the key files and the signing trust list of the server (every signing public key, review M5).</summary>
public sealed record KeyArchive(int Version, IReadOnlyList<KeyArchiveFile> Files, IReadOnlyList<TrustedSigningKey> SigningTrust);

/// <summary>
/// <c>backup-keys</c> (D-087, D-094): every key directory exported as ONE encrypted, signed archive. It holds the encryption master keys (attachments, import files, TOTP secrets under 4.1), the audit key
/// ring, and the backup signing PUBLIC keys with their status; never a signing PRIVATE key (review round 2, N2: a holder of the recovery key could otherwise sign forged sets). Output folder:
/// <c>keys.hbe</c>, <c>key-export.json</c> + <c>.sig</c> (signed over its exact bytes) and a readable copy of the trust list, <c>signing-trust.json</c>, for the recovery kit.
/// </summary>
public static class KeyExport
{
    public const string ArchiveName = "keys.hbe";
    public const string ArchivePurpose = "key-export";
    public const string ManifestName = "key-export.json";
    public const string TrustListName = "signing-trust.json";
    public const string EncryptionRing = "encryption", AuditRing = "audit", SigningPublicRing = "signing-public";
    /// <summary>Bound for the decrypted archive held in memory (key files are tiny; thousands of keys fit).</summary>
    public const int MaxArchiveBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Writes the export into <paramref name="destination"/> (absent or empty; created owner-only). The caller has checked that the destination overlaps no data or key directory
    /// (<see cref="CheckDestination"/>). Returns the manifest (already re-read and verified).
    /// </summary>
    public static async Task<KeyExportManifest> WriteAsync(string destination, string encryptionKeyDirectory, string? auditKeyDirectory, string signingKeyDirectory, BackupMaterial material,
        DateTimeOffset createdUtc, CancellationToken ct = default)
    {
        var files = new List<KeyArchiveFile>();
        files.AddRange(ReadRing(encryptionKeyDirectory, FileKekRing.Extension, KekRing.IsValidKeyId, EncryptionRing));
        if (auditKeyDirectory is not null) files.AddRange(ReadRing(auditKeyDirectory, FileAuditKeyRing.Extension, AuditKeyRing.IsValidKeyId, AuditRing));
        files.AddRange(ReadRing(signingKeyDirectory, BackupSigningKeys.PublicSuffix, BackupSigningKeys.IsKeyId, SigningPublicRing));       // public keys ONLY (N2)
        if (!files.Any(f => f.Ring == SigningPublicRing && f.KeyId == material.Signer.KeyId)) throw new BackupException("signing_public_key_missing");
        // the server knows no statuses: every key it holds is listed as active; the operator marks retired or compromised keys in the kit (D-093)
        var trust = files.Where(f => f.Ring == SigningPublicRing).Select(f => new TrustedSigningKey(f.KeyId, f.Content, SigningKeyStatus.Active)).ToList();

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new BackupException("destination_not_empty");
        if (!Directory.Exists(destination)) CompanyBackupService.CreatePrivateDirectory(destination, "destination_not_empty", "destination_not_private");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new KeyArchive(1, files, trust), Json);
        try
        {
            EncryptedFileEntry archive;
            using (var source = new MemoryStream(plain, writable: false)) archive = await EncryptedSets.EncryptToFileAsync(source, destination, ArchiveName, ArchivePurpose, material.Recovery, ct);
            var manifest = new KeyExportManifest(KeyExportManifest.CurrentVersion, KeyExportManifest.KindValue, createdUtc, material.Recovery.KeyId, material.Signer.KeyId,
                Ids(files, EncryptionRing), Ids(files, AuditRing), Ids(files, SigningPublicRing), archive);
            SignedDocuments.WriteSigned(Path.Combine(destination, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json), material.Signer);
            BackupRunFiles.WriteOwnerOnlyAtomically(Path.Combine(destination, TrustListName), BackupMaterialLoader.SerializeTrustList(trust));
            var self = new SigningTrust([new TrustedSigningKey(material.Signer.KeyId, trust.Single(t => t.KeyId == material.Signer.KeyId).PublicKeyPem, SigningKeyStatus.Active)]);
            var reread = ReadManifest(destination, self);
            await VerifyArchiveStoredAsync(destination, reread, ct);
            return reread;
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The destination must be absolute and overlap none of the given directories (backup root, attachment root, encryption, audit and signing key directories: review M4).</summary>
    public static string CheckDestination(string destination, IEnumerable<string?> protectedDirectories)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new BackupException("path_not_absolute");
        var resolved = BackupSet.RealPath(destination);
        foreach (var p in protectedDirectories)
            if (p is not null && (BackupSet.IsInside(resolved, BackupSet.RealPath(p)) || BackupSet.IsInside(BackupSet.RealPath(p), resolved))) throw new BackupException("destination_overlaps_source");
        // the backup root is not configured (it is an argument of backup-run): a destination that is, or lies inside, a run folder or a folder holding run folders is refused (D-087: never beside the sets)
        for (var d = new DirectoryInfo(resolved); d is not null; d = d.Parent)
        {
            if (BackupRunFiles.IsRunId(d.Name)) throw new BackupException("destination_in_backup_root");
            if (d.Exists && d.EnumerateDirectories().Any(c => BackupRunFiles.IsRunId(c.Name))) throw new BackupException("destination_in_backup_root");
        }
        return resolved;
    }

    /// <summary>Reads and verifies the signed export manifest against the operator's trust list (before anything else is read).</summary>
    public static KeyExportManifest ReadManifest(string exportDirectory, SigningTrust trust)
    {
        var (content, key) = SignedDocuments.ReadVerified(Path.Combine(exportDirectory, ManifestName), trust);
        KeyExportManifest? m;
        try { m = JsonSerializer.Deserialize<KeyExportManifest>(content, Json); }
        catch (JsonException) { throw new BackupException("manifest_invalid"); }
        if (m is null || m.Version != KeyExportManifest.CurrentVersion || m.Kind != KeyExportManifest.KindValue || m.SigningKeyId != key.KeyId || !RecoveryKeys.IsKeyId(m.RecoveryKeyId)
            || m.EncryptionKeyIds is null || m.AuditKeyIds is null || m.SigningPublicKeyIds is null || m.Archive is null
            || m.Archive.Name != ArchiveName || m.Archive.Purpose != ArchivePurpose || m.Archive.ChunkSha256 is not { Count: > 0 }
            || m.EncryptionKeyIds.Any(k => !KekRing.IsValidKeyId(k)) || m.AuditKeyIds.Any(k => !AuditKeyRing.IsValidKeyId(k)) || m.SigningPublicKeyIds.Any(k => !BackupSigningKeys.IsKeyId(k)))
            throw new BackupException("manifest_invalid");
        SigningTrust.EnsureValidAt(key, m.CreatedUtc);
        return m;
    }

    /// <summary>
    /// Key recovery (part of the recovery test, D-094): verifies the signed manifest, the stored archive against its digest, decrypts it in memory (bounded, authenticated chunks only) and checks that the
    /// archive holds exactly the key ids the manifest lists and no signing private key. The caller must dispose nothing; key material lives in the returned archive only.
    /// </summary>
    public static async Task<(KeyExportManifest Manifest, KeyArchive Archive)> ReadAsync(string exportDirectory, SigningTrust trust, RecoveryPrivateKey recovery, CancellationToken ct = default)
    {
        var m = ReadManifest(exportDirectory, trust);
        if (m.RecoveryKeyId != recovery.KeyId) throw new BackupException("recovery_key_mismatch");
        await VerifyArchiveStoredAsync(exportDirectory, m, ct);
        using var plain = new BoundedMemoryStream(MaxArchiveBytes);
        await EncryptedSets.DecryptAsync(exportDirectory, m.Archive, recovery, plain, ct);
        var buffer = plain.ToArray();
        KeyArchive? a;
        try { a = JsonSerializer.Deserialize<KeyArchive>(buffer, Json); }
        catch (JsonException) { throw new BackupException("key_export_invalid"); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
        if (a is not { Version: 1, Files: not null, SigningTrust: not null } || a.Files.Any(f => f is null || f.Content is null)
            || !Ids(a.Files, EncryptionRing).SequenceEqual(m.EncryptionKeyIds) || !Ids(a.Files, AuditRing).SequenceEqual(m.AuditKeyIds) || !Ids(a.Files, SigningPublicRing).SequenceEqual(m.SigningPublicKeyIds)
            || a.Files.Any(f => f.Ring is not (EncryptionRing or AuditRing or SigningPublicRing)) || a.Files.Any(f => f.Content.Contains("PRIVATE KEY", StringComparison.Ordinal)))
            throw new BackupException("key_export_invalid");
        return (m, a);
    }

    private static async Task VerifyArchiveStoredAsync(string exportDirectory, KeyExportManifest m, CancellationToken ct)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { ArchiveName, ManifestName, ManifestName + SignedDocuments.SignatureSuffix, TrustListName };
        foreach (var e in Directory.EnumerateFileSystemEntries(exportDirectory))
            if (!expected.Contains(Path.GetFileName(e)) || Directory.Exists(e)) throw new BackupException("backup_set_not_clean");
        var path = Path.Combine(exportDirectory, ArchiveName);
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length != m.Archive.Size) throw new BackupException("backup_set_not_clean");
        await using var s = File.OpenRead(path);
        Hbe1Digest d;
        try { d = await Hbe1.ComputeDigestAsync(s, ct); }
        catch (BackupException) { throw new BackupException("backup_set_not_clean"); }
        if (d.HeaderSha256 != m.Archive.HeaderSha256 || !d.ChunkSha256.SequenceEqual(m.Archive.ChunkSha256)) throw new BackupException("backup_set_not_clean");
    }

    /// <summary>Review L4 of #128: a signing public key file is parsed at WRITE time: exactly one PEM block labelled PUBLIC KEY, a P-256 key whose derived id is the file's id.</summary>
    private static void RequireSigningPublicKey(string id, string pem)
    {
        try
        {
            var fields = System.Security.Cryptography.PemEncoding.Find(pem);
            if (pem[fields.Label] is not "PUBLIC KEY" || pem[fields.Location.End..].Trim().Length != 0 || pem[..fields.Location.Start].Trim().Length != 0) throw new BackupException("key_file_invalid");
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pem[fields.Base64Data]), out _);
            if (key.KeySize != 256 || BackupSigningKeys.KeyIdOf(key.ExportSubjectPublicKeyInfo()) != id) throw new BackupException("key_file_invalid");
        }
        catch (Exception ex) when (ex is not BackupException) { throw new BackupException("key_file_invalid"); }
    }

    private static List<string> Ids(IEnumerable<KeyArchiveFile> files, string ring) => [.. files.Where(f => f.Ring == ring).Select(f => f.KeyId).Order(StringComparer.Ordinal)];

    private static IEnumerable<KeyArchiveFile> ReadRing(string directory, string suffix, Func<string, bool> isKeyId, string ring)
    {
        if (!Directory.Exists(directory)) throw new BackupException("key_directory_missing");
        if (new DirectoryInfo(Path.TrimEndingDirectorySeparator(directory)).LinkTarget is not null) throw new BackupException("symbolic_link_refused");
        var result = new List<KeyArchiveFile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + suffix).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(path)[..^suffix.Length];
            if (!isKeyId(id)) continue;
            if (new FileInfo(path).LinkTarget is not null) throw new BackupException("symbolic_link_refused");
            if (new FileInfo(path).Length > 64 * 1024) throw new BackupException("key_file_invalid");
            var content = File.ReadAllText(path);
            if (ring == SigningPublicRing) RequireSigningPublicKey(id, content);
            result.Add(new KeyArchiveFile(ring, id, content));
        }
        return result;
    }
}
