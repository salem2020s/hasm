using Hasm.Infrastructure.Audit;

namespace Hasm.Infrastructure.Backup;

/// <summary>The public material an unattended encrypted backup needs: the recovery PUBLIC key and the server signing key (never the recovery private key).</summary>
public sealed record BackupMaterial(RecoveryPublicKey Recovery, BackupSigner Signer);

/// <summary>
/// Encrypted, signed backup sets (Phase 4.3 slice 5, D-089..D-095, D-116). A company set is a private folder holding <c>database.dump.hbe</c>, <c>attachments/&lt;storage name&gt;.hbe</c> and the
/// encrypted, signed version-2 manifest. No plaintext is ever written: <c>pg_dump</c> is piped into the encryptor, attachments are encrypted file by file. Every file is bound (HBE1 purpose and name)
/// to its set-relative name, and the signed manifest carries each file's digest, so a restore releases only authenticated chunks.
/// </summary>
public static class EncryptedSets
{
    public const string DumpName = "database.dump.hbe";
    public const string CompanyDumpPurpose = "company-dump";
    public const string SystemDumpPurpose = "system-dump";
    public const string AttachmentPurpose = "attachment";
    public const string AttachmentsFolder = "attachments";
    public const string CompanyKind = "company";
    public const string SystemKind = "system";

    /// <summary>Encrypts <paramref name="source"/> into a NEW owner-only file and returns its manifest entry (name = the set-relative path, also bound inside the HBE1 header).</summary>
    public static async Task<EncryptedFileEntry> EncryptToFileAsync(Stream source, string setDirectory, string name, string purpose, RecoveryPublicKey recovery, CancellationToken ct)
    {
        var path = Path.Combine(setDirectory, name.Replace('/', Path.DirectorySeparatorChar));
        if (Path.GetDirectoryName(path) is { } dir) AuditFilePermissions.CreateOwnerOnlyDirectory(dir);
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Hbe1Digest digest;
        await using (var output = new FileStream(path, o))
        {
            digest = await Hbe1.EncryptAsync(source, output, recovery, purpose, name, ct);
            await output.FlushAsync(ct);
        }
        return new EncryptedFileEntry(purpose, name, new FileInfo(path).Length, digest.HeaderSha256, digest.ChunkSha256);
    }

    /// <summary>
    /// Creates an encrypted company set in the private folder <paramref name="destination"/> (absent or empty). <paramref name="dump"/> runs pg_dump with the given stdout consumer and returns the
    /// captured locale; attachments are taken from <paramref name="attachmentDirectory"/> (stray files skipped and counted, symbolic links refused).
    /// </summary>
    internal static async Task<ManifestV2> CreateCompanyAsync(Guid companyId, string? runId, Func<Func<Stream, CancellationToken, Task>, Task<DatabaseLocale>> dump, string attachmentDirectory,
        string destination, DateTimeOffset createdUtc, int migrationLevel, BackupMaterial material, CancellationToken ct)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new BackupException("destination_not_empty");
        if (!Directory.Exists(destination)) CompanyBackupService.CreatePrivateDirectory(destination, "destination_not_empty", "destination_not_private");
        try
        {
            EncryptedFileEntry? dumpEntry = null;
            var locale = await dump(async (stdout, c) => dumpEntry = await EncryptToFileAsync(stdout, destination, DumpName, CompanyDumpPurpose, material.Recovery, c));
            PgBackupTool.EnsureRestorable(locale);
            var files = new List<EncryptedFileEntry> { dumpEntry ?? throw new BackupException("dump_failed") };
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            int stray = 0, lookalikes = 0;
            if (Directory.Exists(attachmentDirectory))
            {
                if (new DirectoryInfo(Path.TrimEndingDirectorySeparator(attachmentDirectory)).LinkTarget is not null) throw new BackupException("symbolic_link_refused");
                foreach (var path in Directory.EnumerateFiles(attachmentDirectory).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var storage = Path.GetFileName(path);
                    if (!BackupSet.IsStorageName(storage)) { stray++; continue; }
                    if (new FileInfo(path).LinkTarget is not null) throw new BackupException("symbolic_link_refused");
                    var kind = BackupSet.ClassifyEnvelope(path, out var keyId);
                    if (kind == BackupSet.Envelope.Valid) keys.Add(keyId!);
                    else if (kind == BackupSet.Envelope.MagicOnly) lookalikes++;
                    await using var source = File.OpenRead(path);
                    files.Add(await EncryptToFileAsync(source, destination, $"{AttachmentsFolder}/{storage}.hbe", AttachmentPurpose, material.Recovery, ct));
                }
            }
            var manifest = new ManifestV2(ManifestV2.CurrentVersion, CompanyKind, runId, companyId, createdUtc, material.Recovery.KeyId, material.Signer.KeyId, files,
                new CompanyDetails(locale, migrationLevel, [.. keys], stray, lookalikes));
            await SignedDocuments.WriteManifestAsync(destination, manifest, material.Recovery, material.Signer, ct);
            await VerifyStoredAsync(destination, manifest, ct);
            return manifest;
        }
        catch
        {
            // never leave a partial set that could be mistaken for a backup
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Keyless check of a set against its (already verified) manifest: every listed file exists with its size and digest, and nothing else is in the set besides the manifest and its signature
    /// (<c>backup_set_not_clean</c>). Run before any decryption (H1: the stored bytes are what the signed manifest names).
    /// </summary>
    public static async Task VerifyStoredAsync(string setDirectory, ManifestV2 manifest, CancellationToken ct = default)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { SignedDocuments.ManifestFileName, SignedDocuments.ManifestFileName + SignedDocuments.SignatureSuffix };
        foreach (var f in manifest.Files)
        {
            expected.Add(f.Name);
            var path = Path.Combine(setDirectory, f.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length != f.Size) throw new BackupException("backup_set_not_clean");
            await using var s = File.OpenRead(path);
            Hbe1Digest d;
            try { d = await Hbe1.ComputeDigestAsync(s, ct); }
            catch (BackupException) { throw new BackupException("backup_set_not_clean"); }
            if (d.HeaderSha256 != f.HeaderSha256 || !d.ChunkSha256.SequenceEqual(f.ChunkSha256)) throw new BackupException("backup_set_not_clean");
        }
        foreach (var e in Directory.EnumerateFileSystemEntries(setDirectory, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(e)) continue;
            if (!expected.Contains(Path.GetRelativePath(setDirectory, e).Replace(Path.DirectorySeparatorChar, '/'))) throw new BackupException("backup_set_not_clean");
        }
    }

    /// <summary>Decrypts one listed file into <paramref name="output"/>, releasing only chunks that match the signed digest (D-116).</summary>
    public static async Task DecryptAsync(string setDirectory, EncryptedFileEntry entry, RecoveryPrivateKey recovery, Stream output, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(Path.Combine(setDirectory, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
        await Hbe1.DecryptAsync(s, output, recovery, entry.Purpose, entry.Name, entry.Digest, ct);
    }
}
