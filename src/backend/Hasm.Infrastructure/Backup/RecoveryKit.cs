using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Writes a generated recovery pair as a recovery kit (D-089):
/// <list type="bullet">
/// <item><c>&lt;out&gt;/&lt;key id&gt;.pub.pem</c>: the only file that goes to the server;</item>
/// <item><c>&lt;out&gt;/copy-1/&lt;key id&gt;.key.pem</c> and <c>&lt;out&gt;/copy-2/&lt;key id&gt;.key.pem</c>: the encrypted private key, one per offline medium (separate passphrases when requested);</item>
/// <item><c>&lt;out&gt;/&lt;key id&gt;.sha256</c>: the SHA-256 of every file (PBES2 has no MAC of its own; review L4 of #116).</item>
/// </list>
/// Everything is created owner-only and never overwritten; the output directory must be absolute and absent or empty.
/// </summary>
public static class RecoveryKit
{
    public static IReadOnlyList<string> Write(string outDirectory, RecoveryKeys.GeneratedPair pair)
    {
        if (!Path.IsPathFullyQualified(outDirectory)) throw new BackupException("path_not_absolute");
        if (Directory.Exists(outDirectory) && Directory.EnumerateFileSystemEntries(outDirectory).Any()) throw new BackupException("destination_not_empty");
        AuditFilePermissions.CreateOwnerOnlyDirectory(outDirectory);
        var written = new List<string>();
        void Put(string relative, string content)
        {
            var path = Path.Combine(outDirectory, relative);
            if (Path.GetDirectoryName(path) is { } dir) AuditFilePermissions.CreateOwnerOnlyDirectory(dir);
            AuditFilePermissions.CreateOwnerOnlyFile(path, Encoding.ASCII.GetBytes(content));
            written.Add(relative.Replace('\\', '/'));
        }
        Put(pair.KeyId + ".pub.pem", pair.PublicKeyPem);
        Put(Path.Combine("copy-1", pair.KeyId + ".key.pem"), pair.EncryptedPrivateKeyPems[0]);
        Put(Path.Combine("copy-2", pair.KeyId + ".key.pem"), pair.EncryptedPrivateKeyPems[^1]);
        var sums = string.Concat(written.Select(r => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(outDirectory, r))))}  {r}\n"));
        Put(pair.KeyId + ".sha256", sums);
        return written;
    }
}
