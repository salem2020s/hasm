using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Backup;

/// <summary>A refused backup-set operation. <see cref="Code"/> is a stable reason code; messages never carry paths, names or key material.</summary>
public sealed class BackupException(string code, string? cause = null) : Exception("The backup set operation was refused.")
{
    public string Code { get; } = code;
    /// <summary>A secondary reason code when the primary one hides an earlier failure (for example a failed cleanup after a failed restore); a code only, never tool output.</summary>
    public string? Cause { get; } = cause;
}

/// <summary>One file of a backup set: a relative name, its size and the SHA-256 of the stored bytes (for an encrypted attachment: of the ciphertext).</summary>
public sealed record ManifestFile(string Name, long Size, string Sha256);

/// <summary>
/// The manifest of one company backup set (SRS 14.4, 16.2; Phase 3.12 coordination). It lists the database dump and every attachment file (active AND removed attachments: removed bytes are retained) with hashes,
/// and the master key ids the sensitive files need (<see cref="KeyIdsRequired"/>): the key backup, kept SEPARATELY, must contain them. It never contains key material, original attachment file names (only the generated
/// storage names) or any company name. <see cref="KeyIdsRequired"/> covers the attachment envelopes of the set ONLY: encrypted import-file originals live outside the attachment folder and need their own key coverage (recorded limitation, see docs/PHASE-4.3.md). The manifest detects corruption and loss; it is NOT a signature (authenticity and encryption of the set belong to the scheduled/encrypted backup slice).
/// </summary>
/// <remarks><see cref="DatabaseLocale"/> is the encoding and locale of the source database captured at dump time (decision D-053); a manifest written before it existed reads with null (the restore then needs the live source).
/// <see cref="CompanyMigrationVersion"/> is the highest company migration applied to the source at backup time (decision D-062); null in an older manifest (checked after the restore instead).</remarks>
public sealed record BackupManifest(int Version, Guid CompanyId, DateTimeOffset CreatedUtc, ManifestFile Database, IReadOnlyList<ManifestFile> Attachments, IReadOnlyList<string> KeyIdsRequired, int SkippedStrayFiles, int NonEnvelopeFilesWithMagic = 0, DatabaseLocale? DatabaseLocale = null, int? CompanyMigrationVersion = null);

/// <summary>
/// The encoding, libc collation/ctype and locale provider (<c>c</c> = libc, <c>i</c> = ICU, <c>b</c> = builtin; null on PostgreSQL 14, which has only libc) of a company database, captured when it is dumped, so a restore
/// reproduces them without the source database (disaster recovery). Values are data: they are validated again before they are placed in a statement (<see cref="PgBackupTool"/>).
/// </summary>
public sealed record DatabaseLocale(string Encoding, string? Collate, string? Ctype, string? Provider)
{
    /// <summary>Structural check of a stored record (non-empty encoding, bounded lengths, a one-letter provider); the character rules are applied by the restore.</summary>
    public bool IsWellFormed => !string.IsNullOrEmpty(Encoding) && Encoding.Length <= 64 && (Collate?.Length ?? 0) <= 256 && (Ctype?.Length ?? 0) <= 256 && (Provider is null || Provider.Length == 1);
}

/// <summary>The result of verifying a backup set against its manifest. Only generated names are listed.</summary>
public sealed record BackupVerification(bool ManifestValid, bool DatabaseOk, IReadOnlyList<string> Missing, IReadOnlyList<string> HashMismatch, IReadOnlyList<string> Unexpected, BackupManifest? Manifest)
{
    public bool IsClean => ManifestValid && DatabaseOk && Missing.Count == 0 && HashMismatch.Count == 0 && Unexpected.Count == 0;
    /// <summary>Files that start with the envelope magic without being a valid envelope (counted at backup time).</summary>
    public int NonEnvelopeFilesWithMagic => Manifest?.NonEnvelopeFilesWithMagic ?? 0;
}

/// <summary>What a restore wrote: the attachment files and how many of them only look like envelopes (they start with the envelope magic but are ordinary content: no key is needed for them).</summary>
public sealed record RestoreResult(int Files, int NonEnvelopeFilesWithMagic);

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
    /// <summary>True for a generated attachment storage name (<c>&lt;32 hex&gt;.att</c>).</summary>
    internal static bool IsStorageName(string? name) => name is not null && StorageName.IsMatch(name);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly byte[] EnvelopeMagic = [(byte)'H', (byte)'F', (byte)'E', (byte)'1'];

    /// <summary>
    /// Creates the set in <paramref name="destination"/> (must not exist or be empty and must not overlap the source). Stray files (temporary uploads) are skipped and counted. Symbolic links are refused.
    /// <paramref name="databaseLocale"/> is what <see cref="PgBackupTool.DumpAsync"/> captured and <paramref name="companyMigrationVersion"/> the company migration level; both are stored in the manifest.
    /// </summary>
    public static BackupManifest Create(Guid companyId, string databaseDumpFile, string attachmentCompanyDirectory, string destination, DateTimeOffset createdUtc, DatabaseLocale? databaseLocale = null, int? companyMigrationVersion = null)
    {
        if (companyId == Guid.Empty) throw new BackupException("invalid_company");
        if (databaseLocale is { IsWellFormed: false }) throw new BackupException("invalid_database_locale");
        if (companyMigrationVersion is <= 0) throw new BackupException("invalid_migration_version");
        if (!File.Exists(databaseDumpFile)) throw new BackupException("database_dump_missing");
        if (!Path.IsPathFullyQualified(destination) || !Path.IsPathFullyQualified(attachmentCompanyDirectory)) throw new BackupException("path_not_absolute");
        var source = RealPath(attachmentCompanyDirectory);
        var dest = RealPath(destination);
        if (Directory.Exists(attachmentCompanyDirectory) && new DirectoryInfo(Path.GetFullPath(attachmentCompanyDirectory).TrimEnd(Path.DirectorySeparatorChar)).LinkTarget is not null) throw new BackupException("symbolic_link_refused");
        if (IsInside(dest, source) || IsInside(source, dest)) throw new BackupException("destination_overlaps_source");
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any()) throw new BackupException("destination_not_empty");
        if (new FileInfo(databaseDumpFile).LinkTarget is not null) throw new BackupException("symbolic_link_refused");

        var destExisted = Directory.Exists(dest);
        try { return CreateCore(companyId, databaseDumpFile, source, dest, createdUtc, databaseLocale, companyMigrationVersion); }
        catch
        {
            // never leave a partial set that could be mistaken for a backup
            try { if (destExisted) { foreach (var e in Directory.EnumerateFileSystemEntries(dest)) { if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e); } } else Directory.Delete(dest, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }

    private static BackupManifest CreateCore(Guid companyId, string databaseDumpFile, string source, string dest, DateTimeOffset createdUtc, DatabaseLocale? databaseLocale, int? companyMigrationVersion)
    {
        AuditFilePermissions.CreateOwnerOnlyDirectory(dest);
        var attachDir = Path.Combine(dest, AttachmentFolderName);
        AuditFilePermissions.CreateOwnerOnlyDirectory(attachDir);

        var dumpCopy = Path.Combine(dest, DatabaseFileName);
        File.Copy(databaseDumpFile, dumpCopy, false);
        var database = Describe(dumpCopy, DatabaseFileName);

        var files = new List<ManifestFile>();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var stray = 0; var lookalikes = 0;
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
                // Only a structurally valid envelope names a key. A plain file that merely starts with the magic (a CSV "HFE1,...") or carries an invalid key id is
                // ordinary content: it is counted, never a reason to refuse the backup. (A well-formed envelope with a made-up key id still lists that id: conservative, it can only add a missing-key report.)
                var kind = ClassifyEnvelope(copy, out var keyId);
                if (kind == Envelope.Valid) keys.Add(keyId!);
                else if (kind == Envelope.MagicOnly) lookalikes++;
            }
        }

        var manifest = new BackupManifest(CurrentVersion, companyId, createdUtc, database, files, [.. keys], stray, lookalikes, databaseLocale, companyMigrationVersion);
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
    public static RestoreResult RestoreAttachments(string setDirectory, string targetCompanyDirectory, Guid expectedCompanyId) =>
        RestoreAttachments(setDirectory, targetCompanyDirectory, expectedCompanyId, null);

    /// <summary>As the public overload; <paramref name="afterVerify"/> (tests only) runs after the set was verified and before any file is copied, to prove the staged re-hash.</summary>
    internal static RestoreResult RestoreAttachments(string setDirectory, string targetCompanyDirectory, Guid expectedCompanyId, Action<string>? afterVerify)
    {
        if (!Path.IsPathFullyQualified(targetCompanyDirectory) || !Path.IsPathFullyQualified(setDirectory)) throw new BackupException("path_not_absolute");
        if (expectedCompanyId == Guid.Empty) throw new BackupException("invalid_company");
        var set = RealPath(setDirectory);
        var target = RealPath(targetCompanyDirectory);
        if (target.Length == 0 || Path.GetDirectoryName(target) is not { Length: > 0 } parent) throw new BackupException("target_invalid");
        if (IsInside(target, set) || IsInside(set, target)) throw new BackupException("target_overlaps_set");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new BackupException("target_not_empty");
        var v = Verify(set);
        if (!v.IsClean || v.Manifest is null) throw new BackupException("backup_set_not_clean");
        if (v.Manifest.CompanyId != expectedCompanyId) throw new BackupException("company_mismatch");   // a set of another company is never restored under this one (Scenario 21.8)

        AuditFilePermissions.CreateOwnerOnlyDirectory(parent);
        var staging = target + ".restoring-" + Guid.NewGuid().ToString("N");
        AuditFilePermissions.CreateOwnerOnlyDirectory(staging);
        try
        {
            afterVerify?.Invoke(set);
            foreach (var f in v.Manifest.Attachments)
            {
                var copy = Path.Combine(staging, f.Name);
                File.Copy(Path.Combine(set, AttachmentFolderName, f.Name), copy, false);
                if (!Matches(copy, f)) throw new BackupException("staged_file_changed");   // the source changed after verification (TOCTOU)
            }
            if (Directory.Exists(target)) Directory.Delete(target);   // empty by the check above
            Directory.Move(staging, target);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; the target was never touched */ }
            throw;
        }
        return new RestoreResult(v.Manifest.Attachments.Count, v.Manifest.NonEnvelopeFilesWithMagic);
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
        if (m.DatabaseLocale is { IsWellFormed: false }) return null;
        if (m.CompanyMigrationVersion is <= 0) return null;
        return m;
    }

    private enum Envelope { None, MagicOnly, Valid }
    private const int NonceLength = 12, DekLength = 32, TagLength = 16;

    /// <summary>
    /// Reads at most the envelope header (never the whole file) and applies the structural rules of format v1 (see <see cref="EnvelopeEncryptor"/>): magic, key id (1..32 valid characters), fixed header fields and a ciphertext length
    /// that makes header + ciphertext + tag equal the file size exactly. The mirror of the private parser is locked by a test against a real envelope.
    /// </summary>
    private static Envelope ClassifyEnvelope(string path, out string? keyId)
    {
        keyId = null;
        using var fs = File.OpenRead(path);
        var size = fs.Length;
        var head = new byte[5 + 32 + NonceLength + DekLength + TagLength + NonceLength + 8];
        var n = 0;
        while (n < head.Length) { var r = fs.Read(head, n, head.Length - n); if (r == 0) break; n += r; }
        if (n < 4 || !head.AsSpan(0, 4).SequenceEqual(EnvelopeMagic)) return Envelope.None;
        if (n < 5) return Envelope.MagicOnly;
        int idLen = head[4];
        var fixedLen = NonceLength + DekLength + TagLength + NonceLength + 8;
        if (idLen is 0 or > 32 || n < 5 + idLen + fixedLen) return Envelope.MagicOnly;
        var id = System.Text.Encoding.ASCII.GetString(head, 5, idLen);
        if (!KekRing.IsValidKeyId(id)) return Envelope.MagicOnly;
        var lenOffset = 5 + idLen + NonceLength + DekLength + TagLength + NonceLength;
        var len = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(head.AsSpan(lenOffset, 8));
        var headerEnd = lenOffset + 8;
        if (len < 0 || len > size - headerEnd - TagLength || headerEnd + len + TagLength != size) return Envelope.MagicOnly;
        keyId = id;
        return Envelope.Valid;
    }

    /// <summary>The full path with EVERY existing component resolved through symbolic links (so an alias anywhere in the path cannot hide an overlap).</summary>
    internal static string RealPath(string path)
    {
        var cur = RealPathOnce(path);
        for (var i = 0; i < 40; i++)
        {
            if (cur.Length == 0) return cur;       // the file system root
            var next = RealPathOnce(cur);          // a resolved target may itself pass through further links: repeat until stable
            if (string.Equals(next, cur, StringComparison.Ordinal)) return cur;
            cur = next;
        }
        throw new BackupException("path_too_deep");
    }

    private static string RealPathOnce(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var cur = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur + Path.DirectorySeparatorChar + part;
            FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            try
            {
                if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } resolved)
                    cur = resolved.FullName.TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BackupException("path_unresolvable"); }   // for example a cyclic symbolic link or an unreadable component
        }
        return cur;
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

    internal static bool IsInside(string path, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
