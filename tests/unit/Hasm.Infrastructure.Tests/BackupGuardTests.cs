using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.3 slice 5 (D-088, D-095; review L2 of #116): the DEV/TEST-only guard for unencrypted backup sets, and the pin that no repository configuration enables it.</summary>
public sealed class BackupGuardTests
{
    private static readonly string Abs = Path.Combine(Path.GetTempPath(), "hasm-guard");

    [Fact]
    public void Encryption_is_the_default_and_an_unencrypted_set_needs_the_exact_acknowledgement_without_a_recovery_key()
    {
        Assert.Equal("backup_encryption_not_configured", Assert.Throws<BackupException>(() => BackupGuard.Resolve(new BackupOptions())).Code);
        Assert.Equal("backup_encryption_not_configured", Assert.Throws<BackupException>(() => BackupGuard.Resolve(new BackupOptions { RecoveryPublicKeyFile = Path.Combine(Abs, "rk.pub.pem") })).Code);
        Assert.Equal("backup_encryption_not_configured", Assert.Throws<BackupException>(() => BackupGuard.Resolve(new BackupOptions { RecoveryPublicKeyFile = "relative.pem", SigningKeyDirectory = Abs, SigningKeyId = "sk-0123456789abcdef" })).Code);
        Assert.Equal(BackupProtection.Encrypted, BackupGuard.Resolve(new BackupOptions { RecoveryPublicKeyFile = Path.Combine(Abs, "rk.pub.pem"), SigningKeyDirectory = Abs, SigningKeyId = "sk-0123456789abcdef" }));

        Assert.Equal(BackupProtection.UnencryptedDevTestOnly, BackupGuard.Resolve(new BackupOptions { UnencryptedDevTestOnly = BackupOptions.UnencryptedAcknowledgement }));
        foreach (var wrong in new[] { "true", "yes", "YES-THIS-IS-NOT-PRODUCTION", "yes-this-is-not-production " })
            Assert.Equal("unencrypted_ack_invalid", Assert.Throws<BackupException>(() => BackupGuard.Resolve(new BackupOptions { UnencryptedDevTestOnly = wrong })).Code);
        Assert.Equal("unencrypted_ack_with_recovery_key", Assert.Throws<BackupException>(() => BackupGuard.Resolve(new BackupOptions
        {
            UnencryptedDevTestOnly = BackupOptions.UnencryptedAcknowledgement, RecoveryPublicKeyFile = Path.Combine(Abs, "rk.pub.pem"), SigningKeyDirectory = Abs, SigningKeyId = "sk-0123456789abcdef",
        })).Code);
        Assert.StartsWith("DEGRADED", BackupGuard.DegradedStatus);
    }

    [Fact]
    public void No_shipped_configuration_in_the_repository_sets_the_unencrypted_acknowledgement()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
        Assert.NotNull(root);
        var shipped = new[] { "*.json", "*.env", "*.yml", "*.yaml", "*.toml", "*.ps1", "*.sh", "*.service", "*.xml", "*.config", "*.wxs", "*.nsi" };
        var offenders = shipped.SelectMany(p => Directory.EnumerateFiles(root!.FullName, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"))   // agent worktrees inside a checkout are other branches
            .Where(f => File.ReadAllText(f).Contains("UnencryptedDevTestOnly", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root!.FullName, f)).ToList();
        Assert.True(offenders.Count == 0, "shipped configuration must never permit unencrypted backups: " + string.Join(", ", offenders));
    }
}
