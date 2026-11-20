using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Backup;

/// <summary>A refused backup-set operation. <see cref="Code"/> is a stable reason code; messages never carry paths, names or key material.</summary>
public sealed class BackupException(string code) : Exception("The backup set operation was refused.")
{
    public string Code { get; } = code;
}

/// <summary>One file of a backup set: a relative name, its size and the SHA-256 of the stored bytes (for an encrypted attachment: of the ciphertext).</summary>
public sealed record ManifestFile(string Name, long Size, string Sha256);

/// <summary>
/// The manifest of one company backup set (SRS 14.4, 16.2; Phase 3.12 coordination). It lists the database dump and every attachment file (active AND removed attachments: removed bytes are retained) with hashes,
/// and the master key ids the sensitive files need (<see cref="KeyIdsRequired"/>): the key backup, kept SEPARATELY, must contain them. It never contains key material, original attachment file names (only the generated
/// storage names) or any company name. The manifest detects corruption and loss; it is NOT a signature (authenticity and encryption of the set belong to the scheduled/encrypted backup slice).
/// </summary>
public sealed record BackupManifest(int Version, Guid CompanyId, DateTimeOffset CreatedUtc, ManifestFile Database, IReadOnlyList<ManifestFile> Attachments, IReadOnlyList<string> KeyIdsRequired, int SkippedStrayFiles);

/// <summary>The result of verifying a backup set against its manifest. Only generated names are listed.</summary>
public sealed record BackupVerification(bool ManifestValid, bool DatabaseOk, IReadOnlyList<string> Missing, IReadOnlyList<string> HashMismatch, IReadOnlyList<string> Unexpected, BackupManifest? Manifest)
{
    public bool IsClean => ManifestValid && DatabaseOk && Missing.Count == 0 && HashMismatch.Count == 0 && Unexpected.Count == 0;
}

/// <summary>The outcome of comparing the key ids a backup set needs with the key ids present in a restored key directory.</summary>
public sealed record KeyCoverage(IReadOnlyList<string> Required, IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// Coordinated backup of ONE company: its database dump + its attachment folder (+ the list of keys needed, never the keys). The set is a directory: <c>database.dump</c>, <c>attachments/&lt;32 hex&gt;.att</c>, <c>manifest.json</c>
/// (written last, atomically). Restore verifies the whole set first and writes only to an empty target directory chosen by the caller, so no other company folder and nothing else is touched. The database dump itself is produced and
/// restored by the database tooling (separate slice); this class coordinates, hashes and proves the set.
/// </summary>
public static class BackupSet
{
    public const int CurrentVersion = 1;
    public const string DatabaseFileName = "database.dump";
    public const string ManifestFileName = "manifest.json";
    public const string AttachmentFolderName = "attachments";

    private static readonly Regex StorageName = new("^[0-9a-f]{32}\\.att$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly byte[] EnvelopeMagic = [(byte)'H', (byte)'F', (byte)'E', (byte)'1'];

    /// <summary>Creates the set in <paramref name="destination"/> (must not exist or be empty and must not overlap the source). Stray files (temporary uploads) are skipped and counted. Symbolic links are refused.</summary>
    public static BackupManifest Create(Guid companyId, string databaseDumpFile, string attachmentCompanyDirectory, string destination, DateTimeOffset createdUtc)
    {
        if (companyId == Guid.Empty) throw new BackupException("invalid_company");
        if (!File.Exists(databaseDumpFile)) throw new BackupException("database_dump_missing");
        if (!Path.IsPathFullyQualified(destination) || !Path.IsPathFullyQualified(attachmentCompanyDirectory)) throw new BackupException("path_not_absolute");
        var source = Path.GetFullPath(attachmentCompanyDirectory);
        var dest = Path.GetFullPath(destination);
        if (IsInside(dest, source) || IsInside(source, dest)) throw new BackupException("destination_overlaps_source");
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any()) throw new BackupException("destination_not_empty");
        if (new FileInfo(databaseDumpFile).LinkTarget is not null) throw new BackupException("symbolic_link_refused");

        AuditFilePermissions.CreateOwnerOnlyDirectory(dest);
        var attachDir = Path.Combine(dest, AttachmentFolderName);
        AuditFilePermissions.CreateOwnerOnlyDirectory(attachDir);

        var dumpCopy = Path.Combine(dest, DatabaseFileName);
        File.Copy(databaseDumpFile, dumpCopy, false);
        var database = Describe(dumpCopy, DatabaseFileName);

        var files = new List<ManifestFile>();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var stray = 0;
        if (Directory.Exists(source))
        {
            foreach (var path in Directory.EnumerateFiles(source).OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (!StorageName.IsMatch(name)) { stray++; continue; }
                if (new FileInfo(path).LinkTarget is not null) throw new BackupException("symbolic_link_refused");
                var copy = Path.Combine(attachDir, name);
                File.Copy(path, copy, false);
                files.Add(Describe(copy, name));
                var bytes = File.ReadAllBytes(copy);
                if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(EnvelopeMagic))
                {
                    try { keys.Add(EnvelopeEncryptor.KeyIdOf(bytes)); }
                    catch (EncryptionUnavailableException) { throw new BackupException("encrypted_file_unreadable"); }
                }
            }
        }

        var manifest = new BackupManifest(CurrentVersion, companyId, createdUtc, database, files, [.. keys], stray);
        var temp = Path.Combine(dest, ManifestFileName + ".tmp");
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        File.Move(temp, Path.Combine(dest, ManifestFileName));
        return manifest;
    }

    /// <summary>Verifies every file of the set against its manifest: missing, altered and unexpected files are reported by generated name only.</summary>
    public static BackupVerification Verify(string setDirectory)
    {
        var manifest = ReadManifest(setDirectory);
        if (manifest is null) return new BackupVerification(false, false, [], [], [], null);

        var missing = new List<string>(); var mismatch = new List<string>(); var unexpected = new List<string>();
        var dbPath = Path.Combine(setDirectory, DatabaseFileName);
        var dbExists = File.Exists(dbPath);
        var databaseOk = dbExists && Matches(dbPath, manifest.Database);
        if (!dbExists) missing.Add(DatabaseFileName);
        else if (!databaseOk) mismatch.Add(DatabaseFileName);

        var attachDir = Path.Combine(setDirectory, AttachmentFolderName);
        foreach (var f in manifest.Attachments)
        {
            var p = Path.Combine(attachDir, f.Name);
            if (!File.Exists(p)) missing.Add(f.Name);
            else if (!Matches(p, f)) mismatch.Add(f.Name);
        }
        var known = manifest.Attachments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(attachDir))
            foreach (var p in Directory.EnumerateFileSystemEntries(attachDir))
                if (!known.Contains(Path.GetFileName(p))) unexpected.Add("attachments/unlisted");
        foreach (var p in Directory.EnumerateFileSystemEntries(setDirectory))
        {
            var n = Path.GetFileName(p);
            if (n != DatabaseFileName && n != ManifestFileName && n != AttachmentFolderName) unexpected.Add("set/unlisted");
        }
        return new BackupVerification(true, databaseOk, missing, mismatch, unexpected, manifest);
    }

    /// <summary>
    /// Restores the attachment folder of the set into <paramref name="targetCompanyDirectory"/>, which must not exist or be empty (never merges, never overwrites). The set is verified first; a set that is not clean is refused
    /// and nothing is written. Files are staged in a sibling directory and moved into place, so a failure leaves the target untouched. Returns the number of files restored.
    /// </summary>
    public static int RestoreAttachments(string setDirectory, string targetCompanyDirectory)
    {
        if (!Path.IsPathFullyQualified(targetCompanyDirectory) || !Path.IsPathFullyQualified(setDirectory)) throw new BackupException("path_not_absolute");
        var set = Path.GetFullPath(setDirectory);
        var target = Path.GetFullPath(targetCompanyDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (IsInside(target, set) || IsInside(set, target)) throw new BackupException("target_overlaps_set");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new BackupException("target_not_empty");
        var v = Verify(set);
        if (!v.IsClean || v.Manifest is null) throw new BackupException("backup_set_not_clean");

        var parent = Path.GetDirectoryName(target) ?? throw new BackupException("target_invalid");
        AuditFilePermissions.CreateOwnerOnlyDirectory(parent);
        var staging = target + ".restoring-" + Guid.NewGuid().ToString("N");
        AuditFilePermissions.CreateOwnerOnlyDirectory(staging);
        try
        {
            foreach (var f in v.Manifest.Attachments) File.Copy(Path.Combine(set, AttachmentFolderName, f.Name), Path.Combine(staging, f.Name), false);
            if (Directory.Exists(target)) Directory.Delete(target);   // empty by the check above
            Directory.Move(staging, target);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (IOException) { /* best effort; the target was never touched */ }
            throw;
        }
        return v.Manifest.Attachments.Count;
    }

    /// <summary>Which of the key ids the set needs are absent from <paramref name="availableKeyIds"/> (for example the ids found in the restored key directory). Retaining every key a retained attachment needs is decision K.</summary>
    public static KeyCoverage CheckKeys(BackupManifest manifest, IEnumerable<string> availableKeyIds)
    {
        var have = availableKeyIds.ToHashSet(StringComparer.Ordinal);
        return new KeyCoverage(manifest.KeyIdsRequired, [.. manifest.KeyIdsRequired.Where(k => !have.Contains(k))]);
    }

    /// <summary>The key ids present as files in a key directory (names only; no file is opened, no material is read).</summary>
    public static IReadOnlyList<string> ListKeyIds(string keyDirectory)
    {
        if (!Directory.Exists(keyDirectory)) return [];
        return [.. Directory.EnumerateFiles(keyDirectory, "*" + FileKekRing.Extension)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(k => KekRing.IsValidKeyId(k))
            .OrderBy(k => k, StringComparer.Ordinal)];
    }

    /// <summary>Reads and structurally validates the manifest; null when missing, malformed, unsupported or carrying a name that could escape the set.</summary>
    public static BackupManifest? ReadManifest(string setDirectory)
    {
        var path = Path.Combine(setDirectory, ManifestFileName);
        if (!File.Exists(path)) return null;
        BackupManifest? m;
        try { m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(path), Json); }
        catch (JsonException) { return null; }
        if (m is null || m.Version != CurrentVersion || m.CompanyId == Guid.Empty || m.Database is null || m.Attachments is null || m.KeyIdsRequired is null) return null;
        if (m.Database.Name != DatabaseFileName || !Sha256Hex.IsMatch(m.Database.Sha256 ?? "") || m.Database.Size < 0) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in m.Attachments)
            if (a is null || a.Name is null || !StorageName.IsMatch(a.Name) || !Sha256Hex.IsMatch(a.Sha256 ?? "") || a.Size < 0 || !names.Add(a.Name)) return null;
        if (m.KeyIdsRequired.Any(k => k is null || !KekRing.IsValidKeyId(k))) return null;
        return m;
    }

    private static ManifestFile Describe(string path, string name)
    {
        using var fs = File.OpenRead(path);
        var size = fs.Length;
        return new ManifestFile(name, size, Convert.ToHexStringLower(SHA256.HashData(fs)));
    }

    private static bool Matches(string path, ManifestFile expected)
    {
        var actual = Describe(path, expected.Name);
        return actual.Size == expected.Size && string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal);
    }

    private static bool IsInside(string path, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
