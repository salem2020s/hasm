using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Persistent storage of ENCRYPTED files on the server filesystem (company-isolated directories, owner-only access, never inside a web root, never served directly). The caller encrypts first;
/// this store only ever sees ciphertext, so no persistent plaintext exists. File names are generated identifiers (<c>&lt;32 hex&gt;.enc</c>), validated on every call, so a path can never escape its
/// company directory. Writes go to a temporary name created exclusively and are moved into place atomically (a crash leaves either the old file or the new one, never a partial one).
/// </summary>
public sealed partial class EncryptedFileStore(ImportOptions options, EncryptionOptions encryption)
{
    [GeneratedRegex("^[0-9a-f]{32}\\.enc$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private string Directory(Guid companyId)
    {
        if (options.StorageDirectory is not { } root || !Path.IsPathFullyQualified(root))
            throw new EncryptionUnavailableException("import_storage_not_configured", $"{ImportOptions.SectionName}:StorageDirectory (an absolute path) is required.");
        var dir = Path.Combine(root, companyId.ToString("N"));
        AuditFilePermissions.CreateOwnerOnlyDirectory(root);
        AuditFilePermissions.CreateOwnerOnlyDirectory(dir);
        if (!encryption.AllowInsecureFilePermissions) AuditFilePermissions.EnsureNotWritableByOthers(dir);
        return dir;
    }

    private string PathOf(Guid companyId, string name)
    {
        if (!NamePattern().IsMatch(name)) throw new ArgumentException("Invalid stored file name.", nameof(name));
        return Path.Combine(Directory(companyId), name);
    }

    /// <summary>Stores a new encrypted file; refuses to overwrite an existing one.</summary>
    public void Create(Guid companyId, string name, byte[] ciphertext)
    {
        var path = PathOf(companyId, name);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            AuditFilePermissions.CreateOwnerOnlyFile(temp, ciphertext);
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Atomically replaces an existing encrypted file (re-wrap).</summary>
    public void Replace(Guid companyId, string name, byte[] ciphertext)
    {
        var path = PathOf(companyId, name);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            AuditFilePermissions.CreateOwnerOnlyFile(temp, ciphertext);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public byte[]? Read(Guid companyId, string name)
    {
        var path = PathOf(companyId, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public bool Exists(Guid companyId, string name) => File.Exists(PathOf(companyId, name));

    /// <summary>Deletes the file; true when it existed. Idempotent.</summary>
    public bool Delete(Guid companyId, string name)
    {
        var path = PathOf(companyId, name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
