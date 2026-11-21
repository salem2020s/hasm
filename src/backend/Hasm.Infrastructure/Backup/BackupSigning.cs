using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The server-held backup SIGNING key (Phase 4.3 slice 5, D-092/D-093): ECDSA on NIST P-256 in its own owner-only directory (<c>Hasm__Backup__SigningKeyDirectory</c>), separate from the attachment,
/// audit and recovery keys. Key id <c>sk-</c> + 16 hex of SHA-256(SubjectPublicKeyInfo). Files: <c>&lt;id&gt;.key.pem</c> (PKCS#8, owner-only, never exported: review N2 of #116) and
/// <c>&lt;id&gt;.pub.pem</c>. Signatures are IEEE-P1363 over the exact bytes of the signed file.
/// </summary>
public static partial class BackupSigningKeys
{
    public const string PrivateSuffix = ".key.pem", PublicSuffix = ".pub.pem";
    [GeneratedRegex("^sk-[0-9a-f]{16}$", RegexOptions.CultureInvariant)] private static partial Regex KeyIdPattern();

    public static bool IsKeyId(string? id) => id is not null && KeyIdPattern().IsMatch(id);
    public static string KeyIdOf(ReadOnlySpan<byte> spki) => "sk-" + Convert.ToHexStringLower(SHA256.HashData(spki))[..16];

    /// <summary>Generates a new signing key in <paramref name="directory"/> (owner-only; never overwrites). Returns the key id and the public key PEM (for the recovery kit).</summary>
    public static (string KeyId, string PublicKeyPem) Generate(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new BackupException("path_not_absolute");
        AuditFilePermissions.CreateOwnerOnlyDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var id = KeyIdOf(spki);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            AuditFilePermissions.CreateOwnerOnlyFile(Path.Combine(directory, id + PrivateSuffix), Encoding.ASCII.GetBytes(PemEncoding.WriteString("PRIVATE KEY", pkcs8)));
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
        var pub = PemEncoding.WriteString("PUBLIC KEY", spki);
        AuditFilePermissions.CreateOwnerOnlyFile(Path.Combine(directory, id + PublicSuffix), Encoding.ASCII.GetBytes(pub));
        return (id, pub);
    }

    /// <summary>Loads the private signing key <paramref name="keyId"/> from <paramref name="directory"/> (owner-only checks unless relaxed for tests); <c>signing_key_unavailable</c> otherwise.</summary>
    public static BackupSigner Load(string directory, string keyId, bool allowInsecurePermissions = false)
    {
        if (!IsKeyId(keyId)) throw new BackupException("signing_key_unavailable");
        var path = Path.Combine(directory, keyId + PrivateSuffix);
        try
        {
            if (!allowInsecurePermissions) AuditFilePermissions.EnsureOwnerOnly(directory, path);
            var pem = File.ReadAllText(path);
            var fields = PemEncoding.Find(pem);
            if (pem[fields.Label] is not "PRIVATE KEY") throw new BackupException("signing_key_unavailable");
            var der = Convert.FromBase64String(pem[fields.Base64Data]);
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(der, out var read);
                if (read != der.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value || KeyIdOf(key.ExportSubjectPublicKeyInfo()) != keyId)
                    throw new BackupException("signing_key_unavailable");
                return new BackupSigner(key, keyId);
            }
            catch { key.Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(der); }
        }
        catch (Exception ex) when (ex is not BackupException) { throw new BackupException("signing_key_unavailable"); }
    }
}

/// <summary>A loaded signing key; signs exact bytes and produces the detached <c>.sig</c> document.</summary>
public sealed class BackupSigner(ECDsa key, string keyId) : IDisposable
{
    public string KeyId { get; } = keyId;
    public BackupSignature Sign(ReadOnlySpan<byte> content) =>
        new(1, KeyId, Convert.ToBase64String(key.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    public void Dispose() => key.Dispose();
}

/// <summary>The detached signature document (<c>*.sig</c>): version, signing key id and the base64 IEEE-P1363 signature over the exact bytes of the signed file.</summary>
public sealed record BackupSignature(int Version, string KeyId, string Signature)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Parses a <c>.sig</c> document; null when malformed (callers refuse a missing or malformed signature: review M1 of #116).</summary>
    public static BackupSignature? Parse(byte[] bytes)
    {
        if (bytes.Length > 4096) return null;
        try
        {
            var s = JsonSerializer.Deserialize<BackupSignature>(bytes, Json);
            return s is { Version: 1 } && BackupSigningKeys.IsKeyId(s.KeyId) && s.Signature is { Length: > 0 and < 512 } ? s : null;
        }
        catch (JsonException) { return null; }
    }
}

public enum SigningKeyStatus { Active, Retired, Compromised }

/// <summary>One trusted signing public key of the recovery kit, with its status and, for a retired key, its retirement time (review M5 and round 2 L-d of #116).</summary>
public sealed record TrustedSigningKey(string KeyId, string PublicKeyPem, SigningKeyStatus Status, DateTimeOffset? RetiredUtc = null);

/// <summary>
/// The verification trust anchor: the signing PUBLIC keys the operator keeps in the recovery kit (never a key carried inside a set). A <c>compromised</c> key never verifies; a <c>retired</c> key verifies
/// only content whose signed <c>createdUtc</c> is before its retirement time (held here, never taken from the set).
/// </summary>
public sealed class SigningTrust
{
    private readonly Dictionary<string, (TrustedSigningKey Entry, ECDsa Key)> keys = new(StringComparer.Ordinal);

    public SigningTrust(IEnumerable<TrustedSigningKey> entries)
    {
        foreach (var e in entries)
        {
            if (!BackupSigningKeys.IsKeyId(e.KeyId) || keys.ContainsKey(e.KeyId)) throw new BackupException("signing_trust_invalid");
            if (e.Status == SigningKeyStatus.Retired && e.RetiredUtc is null) throw new BackupException("signing_trust_invalid");
            var key = ECDsa.Create();
            try
            {
                var fields = PemEncoding.Find(e.PublicKeyPem);
                if (e.PublicKeyPem[fields.Label] is not "PUBLIC KEY") throw new BackupException("signing_trust_invalid");
                var der = Convert.FromBase64String(e.PublicKeyPem[fields.Base64Data]);
                key.ImportSubjectPublicKeyInfo(der, out var read);
                if (read != der.Length || BackupSigningKeys.KeyIdOf(key.ExportSubjectPublicKeyInfo()) != e.KeyId) throw new BackupException("signing_trust_invalid");
            }
            catch (Exception ex) when (ex is not BackupException) { key.Dispose(); throw new BackupException("signing_trust_invalid"); }
            keys[e.KeyId] = (e, key);
        }
    }

    /// <summary>
    /// Verifies <paramref name="signature"/> over the exact <paramref name="content"/> and returns the trusted key entry. Refusals: <c>signature_missing</c>, <c>signing_key_unknown</c>,
    /// <c>signing_key_compromised</c>, <c>signature_invalid</c>. The retirement rule needs the content's own signed creation time, which for an encrypted manifest is known only after decryption:
    /// call <see cref="EnsureValidAt"/> with it before using the parsed content.
    /// </summary>
    public TrustedSigningKey Verify(ReadOnlySpan<byte> content, BackupSignature? signature)
    {
        if (signature is null) throw new BackupException("signature_missing");
        if (!keys.TryGetValue(signature.KeyId, out var k)) throw new BackupException("signing_key_unknown");
        if (k.Entry.Status == SigningKeyStatus.Compromised) throw new BackupException("signing_key_compromised");
        byte[] sig;
        try { sig = Convert.FromBase64String(signature.Signature); }
        catch (FormatException) { throw new BackupException("signature_invalid"); }
        if (!k.Key.VerifyData(content, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new BackupException("signature_invalid");
        return k.Entry;
    }

    /// <summary>A retired key verifies only content whose signed <c>createdUtc</c> is BEFORE its retirement time held in the kit (<c>signing_key_retired</c> otherwise).</summary>
    public static void EnsureValidAt(TrustedSigningKey key, DateTimeOffset signedCreatedUtc)
    {
        if (key.Status == SigningKeyStatus.Compromised) throw new BackupException("signing_key_compromised");
        if (key.Status == SigningKeyStatus.Retired && signedCreatedUtc >= key.RetiredUtc) throw new BackupException("signing_key_retired");
    }
}
