using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Backup encryption and signing settings (section <c>Hasm:Backup</c>, environment <c>Hasm__Backup__*</c>; Phase 4.3 slice 5, D-085/D-088/D-095):
/// the recovery PUBLIC key file, the signing key directory and its active key id, and the DEV/TEST-only acknowledgement for unencrypted sets.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Hasm:Backup";
    /// <summary>The only value that permits an unencrypted set (DEV/TEST only, D-088). Any other non-empty value is a configuration error.</summary>
    public const string UnencryptedAcknowledgement = "yes-this-is-not-production";

    public string? RecoveryPublicKeyFile { get; init; }
    public string? SigningKeyDirectory { get; init; }
    public string? SigningKeyId { get; init; }
    public string? UnencryptedDevTestOnly { get; init; }
    /// <summary>Relaxed key-file permission checks for disposable test environments only (as for the other key rings).</summary>
    public bool AllowInsecureFilePermissions { get; init; }

    public static BackupOptions FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        return new BackupOptions
        {
            RecoveryPublicKeyFile = Get("RecoveryPublicKeyFile"), SigningKeyDirectory = Get("SigningKeyDirectory"), SigningKeyId = Get("SigningKeyId"),
            UnencryptedDevTestOnly = Get("UnencryptedDevTestOnly"),
            AllowInsecureFilePermissions = bool.TryParse(Get("AllowInsecureFilePermissions"), out var insecure) && insecure,
        };
    }
}

/// <summary>How backups are produced under the current settings (the DEV/TEST guard, D-095).</summary>
public enum BackupProtection { Encrypted, UnencryptedDevTestOnly }

public static class BackupGuard
{
    /// <summary>
    /// Decides the protection mode; never a silent fallback:
    /// <list type="bullet">
    /// <item>an acknowledgement value other than the exact constant is <c>unencrypted_ack_invalid</c>;</item>
    /// <item>the acknowledgement together with a configured recovery key is <c>unencrypted_ack_with_recovery_key</c> (review L2 of #116: an installation with encryption set up cannot be downgraded by one variable);</item>
    /// <item>the exact acknowledgement alone gives <see cref="BackupProtection.UnencryptedDevTestOnly"/> (marked, always DEGRADED);</item>
    /// <item>otherwise the recovery public key file, the signing key directory and a signing key id must all be configured (<c>backup_encryption_not_configured</c>).</item>
    /// </list>
    /// </summary>
    public static BackupProtection Resolve(BackupOptions options)
    {
        if (options.UnencryptedDevTestOnly is { } ack)
        {
            if (!string.Equals(ack, BackupOptions.UnencryptedAcknowledgement, StringComparison.Ordinal)) throw new BackupException("unencrypted_ack_invalid");
            if (options.RecoveryPublicKeyFile is not null) throw new BackupException("unencrypted_ack_with_recovery_key");
            return BackupProtection.UnencryptedDevTestOnly;
        }
        if (options.RecoveryPublicKeyFile is null || !Path.IsPathFullyQualified(options.RecoveryPublicKeyFile)
            || options.SigningKeyDirectory is null || !Path.IsPathFullyQualified(options.SigningKeyDirectory) || !BackupSigningKeys.IsKeyId(options.SigningKeyId))
            throw new BackupException("backup_encryption_not_configured");
        return BackupProtection.Encrypted;
    }

    /// <summary>
    /// Whether a READER (trial restore) may accept a version-1, unencrypted set: only under the reader's own exact acknowledgement (review M1 of #116: never because of a field inside the set). An invalid
    /// acknowledgement, or one together with a recovery key, is a configuration error as for <see cref="Resolve"/>.
    /// </summary>
    public static bool UnencryptedReadAllowed(BackupOptions options) => options.UnencryptedDevTestOnly is not null && Resolve(options) == BackupProtection.UnencryptedDevTestOnly;

    /// <summary>The status line every readiness or health output shows while unencrypted sets are permitted (review L2 of #116).</summary>
    public const string DegradedStatus = "DEGRADED: unencrypted backups (DEV/TEST only)";
}
