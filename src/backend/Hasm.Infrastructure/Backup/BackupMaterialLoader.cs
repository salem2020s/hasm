using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hasm.Infrastructure.Backup;

/// <summary>Loads the configured backup material (slice 5) and the signing trust lists.</summary>
public static class BackupMaterialLoader
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>The recovery PUBLIC key and the active signing key (only after <see cref="BackupGuard.Resolve"/> returned <see cref="BackupProtection.Encrypted"/>).</summary>
    public static BackupMaterial Load(BackupOptions options)
    {
        if (BackupGuard.Resolve(options) != BackupProtection.Encrypted) throw new BackupException("backup_encryption_not_configured");
        RecoveryPublicKey recovery;
        try { recovery = RecoveryPublicKey.FromPem(File.ReadAllText(options.RecoveryPublicKeyFile!)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BackupException("recovery_key_invalid"); }
        try { return new BackupMaterial(recovery, BackupSigningKeys.Load(options.SigningKeyDirectory!, options.SigningKeyId!, options.AllowInsecureFilePermissions)); }
        catch { recovery.Dispose(); throw; }
    }

    /// <summary>
    /// The server-side trust list for MONITORING only (<c>backup-status</c>): every signing public key in the configured signing key directory, as active. Null when no directory is configured (an
    /// encrypted run then counts as failed: its status cannot be verified). Restores use the operator's recovery-kit trust list instead (<see cref="LoadTrustList"/>).
    /// </summary>
    public static SigningTrust? ServerTrust(BackupOptions options)
    {
        if (options.SigningKeyDirectory is not { } dir || !Directory.Exists(dir)) return null;
        var entries = Directory.EnumerateFiles(dir, "sk-*" + BackupSigningKeys.PublicSuffix)
            .Select(f => (Id: Path.GetFileName(f)[..^BackupSigningKeys.PublicSuffix.Length], Pem: File.ReadAllText(f)))
            .Where(e => BackupSigningKeys.IsKeyId(e.Id))
            .Select(e => new TrustedSigningKey(e.Id, e.Pem, SigningKeyStatus.Active)).ToList();
        return new SigningTrust(entries);
    }

    /// <summary>The recovery-kit trust list (<c>signing-trust.json</c>): version 1, every signing public key with its status and retirement time (review M5, L-d).</summary>
    public sealed record TrustListFile(int Version, IReadOnlyList<TrustedSigningKey> Keys);

    public static byte[] SerializeTrustList(IReadOnlyList<TrustedSigningKey> keys) => JsonSerializer.SerializeToUtf8Bytes(new TrustListFile(1, keys), Json);

    /// <summary>A trust list of ONE signing public key (<c>--signing-public-key</c>), as active; its id is derived from the key itself. Prefer the kit's status list (<see cref="LoadTrustList"/>).</summary>
    public static SigningTrust TrustFromPublicKey(string pemPath)
    {
        string pem;
        try { pem = File.ReadAllText(pemPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BackupException("signing_trust_invalid"); }
        using var key = System.Security.Cryptography.ECDsa.Create();
        try { key.ImportFromPem(pem); }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException) { throw new BackupException("signing_trust_invalid"); }
        return new SigningTrust([new TrustedSigningKey(BackupSigningKeys.KeyIdOf(key.ExportSubjectPublicKeyInfo()), pem, SigningKeyStatus.Active)]);
    }

    public static SigningTrust LoadTrustList(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new BackupException("signing_trust_invalid");
        TrustListFile? f;
        try { f = JsonSerializer.Deserialize<TrustListFile>(File.ReadAllBytes(path), Json); }
        catch (JsonException) { throw new BackupException("signing_trust_invalid"); }
        if (f is not { Version: 1, Keys: not null } || f.Keys.Count == 0) throw new BackupException("signing_trust_invalid");
        return new SigningTrust(f.Keys);
    }
}
