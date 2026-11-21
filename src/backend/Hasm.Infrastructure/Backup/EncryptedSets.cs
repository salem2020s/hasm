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
        foreach (var f in manifest.Files) expected.Add(f.Name);
        await VerifyEntriesAsync(setDirectory, manifest.Files, ct);
        foreach (var e in Directory.EnumerateFileSystemEntries(setDirectory, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(e)) continue;
            if (!expected.Contains(Path.GetRelativePath(setDirectory, e).Replace(Path.DirectorySeparatorChar, '/'))) throw new BackupException("backup_set_not_clean");
        }
    }

    /// <summary>Keyless check of the listed files only (each exists, is no link, and has its size and digest); <c>backup_set_not_clean</c> otherwise.</summary>
    public static async Task VerifyEntriesAsync(string setDirectory, IEnumerable<EncryptedFileEntry> entries, CancellationToken ct = default)
    {
        foreach (var f in entries)
        {
            var path = Path.Combine(setDirectory, f.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length != f.Size) throw new BackupException("backup_set_not_clean");
            await using var s = File.OpenRead(path);
            Hbe1Digest d;
            try { d = await Hbe1.ComputeDigestAsync(s, ct); }
            catch (BackupException) { throw new BackupException("backup_set_not_clean"); }
            if (d.HeaderSha256 != f.HeaderSha256 || !d.ChunkSha256.SequenceEqual(f.ChunkSha256)) throw new BackupException("backup_set_not_clean");
        }
    }

    /// <summary>
    /// Reads an encrypted COMPANY set for a restore: the signed manifest (verified against the operator's trust list, then decrypted), its kind, company and structure (the dump first, then only
    /// <c>attachments/&lt;storage name&gt;.hbe</c>), and every stored file against its signed digest, before anything is decrypted (H1).
    /// </summary>
    public static async Task<ManifestV2> ReadCompanySetAsync(string setDirectory, Guid expectedCompanyId, SigningTrust trust, RecoveryPrivateKey recovery, CancellationToken ct = default)
    {
        var m = await SignedDocuments.ReadManifestAsync(setDirectory, trust, recovery, CompanyKind, ct);
        if (m.Company is null || m.CompanyId is null || m.Files.Count == 0 || m.Files[0] is not { Name: DumpName, Purpose: CompanyDumpPurpose }
            || m.Files.Skip(1).Any(f => f.Purpose != AttachmentPurpose || !f.Name.StartsWith(AttachmentsFolder + "/", StringComparison.Ordinal) || !f.Name.EndsWith(".hbe", StringComparison.Ordinal)
                || !BackupSet.IsStorageName(f.Name[(AttachmentsFolder.Length + 1)..^4])))
            throw new BackupException("manifest_invalid");
        if (m.CompanyId != expectedCompanyId) throw new BackupException("company_mismatch");
        await VerifyStoredAsync(setDirectory, m, ct);
        return m;
    }

    /// <summary>
    /// Decrypts the attachments of a verified encrypted company set into <paramref name="target"/> (absent or empty) through a private (0700) staging folder, releasing only authenticated chunks
    /// (D-116); a failure removes the staging folder and leaves the target untouched. Returns the number of files.
    /// </summary>
    public static async Task<int> RestoreAttachmentsAsync(string setDirectory, ManifestV2 manifest, string target, RecoveryPrivateKey recovery, CancellationToken ct = default)
    {
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new BackupException("target_not_empty");
        if (Path.GetDirectoryName(target) is not { Length: > 0 } parent) throw new BackupException("target_invalid");
        AuditFilePermissions.CreateOwnerOnlyDirectory(parent);
        var staging = target + ".restoring-" + Guid.NewGuid().ToString("N");
        CompanyBackupService.CreatePrivateDirectory(staging, "staging_exists", "staging_not_private");
        try
        {
            var n = 0;
            foreach (var entry in manifest.Files.Where(f => f.Purpose == AttachmentPurpose))
            {
                var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(Path.Combine(staging, entry.Name[(AttachmentsFolder.Length + 1)..^4]), o))
                    await DecryptAsync(setDirectory, entry, recovery, output, ct);
                n++;
            }
            if (Directory.Exists(target)) Directory.Delete(target);   // empty by the check above
            Directory.Move(staging, target);
            return n;
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; the target was never touched */ }
            throw;
        }
    }

    /// <summary>Decrypts one listed file into <paramref name="output"/>, releasing only chunks that match the signed digest (D-116).</summary>
    public static async Task DecryptAsync(string setDirectory, EncryptedFileEntry entry, RecoveryPrivateKey recovery, Stream output, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(Path.Combine(setDirectory, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
        await Hbe1.DecryptAsync(s, output, recovery, entry.Purpose, entry.Name, entry.Digest, ct);
    }
}
