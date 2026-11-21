using System.Security.Cryptography;
using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.3 slice 5 (D-089; review M3/L4 of #116): the recovery kit written by <c>backup-recovery-keygen</c>.</summary>
public sealed class RecoveryKitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-kit-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private const string P1 = "first custodian passphrase 1";
    private const string P2 = "second custodian passphrase 2";

    [Fact]
    public void The_kit_holds_the_public_key_two_encrypted_copies_and_their_checksums_owner_only_and_is_never_overwritten()
    {
        var pair = RecoveryKeys.Generate([P1, P2]);
        var files = RecoveryKit.Write(root, pair);
        Assert.Equal(new[] { $"{pair.KeyId}.pub.pem", $"copy-1/{pair.KeyId}.key.pem", $"copy-2/{pair.KeyId}.key.pem", $"{pair.KeyId}.sha256" }, files);
        if (!OperatingSystem.IsWindows())
            foreach (var f in files) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, f)));
        var copy1 = File.ReadAllText(Path.Combine(root, "copy-1", pair.KeyId + ".key.pem"));
        var copy2 = File.ReadAllText(Path.Combine(root, "copy-2", pair.KeyId + ".key.pem"));
        using (var k1 = RecoveryPrivateKey.FromEncryptedPem(copy1, P1)) Assert.Equal(pair.KeyId, k1.KeyId);
        using (var k2 = RecoveryPrivateKey.FromEncryptedPem(copy2, P2)) Assert.Equal(pair.KeyId, k2.KeyId);           // separate custodians: each copy opens only with its own passphrase
        Assert.Equal("recovery_key_unreadable", Assert.Throws<BackupException>(() => RecoveryPrivateKey.FromEncryptedPem(copy1, P2)).Code);
        foreach (var line in File.ReadAllLines(Path.Combine(root, pair.KeyId + ".sha256")))
        {
            var (hash, name) = (line[..64], line[66..]);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))));
        }
        Assert.Equal("destination_not_empty", Assert.Throws<BackupException>(() => RecoveryKit.Write(root, pair)).Code);
        Assert.Equal("path_not_absolute", Assert.Throws<BackupException>(() => RecoveryKit.Write("relative", pair)).Code);
        Assert.Equal("passphrase_too_short", Assert.Throws<BackupException>(() => RecoveryKeys.Generate([P1, "short"])).Code);
    }
}
