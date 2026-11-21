using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// The offline backup recovery key pair (Phase 4.3 slice 5, owner decision D-085, technical choice D-089): ECDH on NIST P-256. The server only ever holds the PUBLIC key; the private key is generated offline,
/// stored as passphrase-encrypted PKCS#8 (PBES2: AES-256-CBC with PBKDF2-HMAC-SHA256) in two offline copies, and brought to a machine only for a restore or a recovery test.
/// The key id is <c>rk-</c> followed by the first 16 hex characters of SHA-256 over the SubjectPublicKeyInfo.
/// </summary>
public static partial class RecoveryKeys
{
    public const int PbkdfIterations = 600_000;
    public const int MinimumPassphraseLength = 20;   // review M3 of #116: at least 20 characters (or six diceware words, which are longer)
    [GeneratedRegex("^rk-[0-9a-f]{16}$", RegexOptions.CultureInvariant)] private static partial Regex KeyIdPattern();

    public static bool IsKeyId(string? id) => id is not null && KeyIdPattern().IsMatch(id);

    /// <summary>The key id of a SubjectPublicKeyInfo.</summary>
    public static string KeyIdOf(ReadOnlySpan<byte> subjectPublicKeyInfo) => "rk-" + Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo))[..16];

    /// <summary>A newly generated pair: the public key (PEM) and the passphrase-encrypted private key, once per passphrase (PEM; one per offline copy). The private key exists in memory only while this runs.</summary>
    public sealed record GeneratedPair(string KeyId, string PublicKeyPem, IReadOnlyList<string> EncryptedPrivateKeyPems)
    {
        public string EncryptedPrivateKeyPem => EncryptedPrivateKeyPems[0];
    }

    public static GeneratedPair Generate(string passphrase) => Generate([passphrase]);

    /// <summary>
    /// Generates the pair and encrypts the private key once per passphrase (review M3 of #116: one passphrase for both copies, or a separate one per copy held by separate custodians). Each passphrase must be
    /// at least <see cref="MinimumPassphraseLength"/> characters. PBES2 has no MAC of its own: keep the SHA-256 of every copy in the recovery kit (review L4).
    /// </summary>
    public static GeneratedPair Generate(IReadOnlyList<string> passphrases)
    {
        if (passphrases.Count is < 1 or > 2 || passphrases.Any(p => p is null || p.Length < MinimumPassphraseLength)) throw new BackupException("passphrase_too_short");
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var pems = new List<string>();
        foreach (var passphrase in passphrases)
        {
            var encrypted = key.ExportEncryptedPkcs8PrivateKey(passphrase.AsSpan(), new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, PbkdfIterations));
            try { pems.Add(PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", encrypted)); }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        return new GeneratedPair(KeyIdOf(spki), PemEncoding.WriteString("PUBLIC KEY", spki), pems);
    }
}

/// <summary>A recovery PUBLIC key (the only recovery material on the server).</summary>
public sealed class RecoveryPublicKey : IDisposable
{
    internal ECDiffieHellman Key { get; }
    public string KeyId { get; }
    internal byte[] SubjectPublicKeyInfo { get; }

    private RecoveryPublicKey(ECDiffieHellman key, byte[] spki) { Key = key; SubjectPublicKeyInfo = spki; KeyId = RecoveryKeys.KeyIdOf(spki); }

    /// <summary>Loads a PEM <c>PUBLIC KEY</c>; anything else (another curve, another algorithm, a private key, trailing garbage) is <c>recovery_key_invalid</c>.</summary>
    public static RecoveryPublicKey FromPem(string pem)
    {
        ECDiffieHellman? key = null;
        try
        {
            var fields = PemEncoding.Find(pem);
            if (pem[fields.Label] is not "PUBLIC KEY") throw new BackupException("recovery_key_invalid");
            var der = Convert.FromBase64String(pem[fields.Base64Data]);
            key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(der, out var read);
            if (read != der.Length || !IsP256(key)) throw new BackupException("recovery_key_invalid");
            return new RecoveryPublicKey(key, key.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            key?.Dispose();
            throw new BackupException("recovery_key_invalid");
        }
        catch { key?.Dispose(); throw; }
    }

    internal static bool IsP256(ECDiffieHellman key) => key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;

    public void Dispose() => Key.Dispose();
}

/// <summary>A recovery PRIVATE key, decrypted in memory for a restore or a recovery test only; dispose it as soon as possible.</summary>
public sealed class RecoveryPrivateKey : IDisposable
{
    internal ECDiffieHellman Key { get; }
    public string KeyId { get; }

    private RecoveryPrivateKey(ECDiffieHellman key) { Key = key; KeyId = RecoveryKeys.KeyIdOf(key.ExportSubjectPublicKeyInfo()); }

    /// <summary>Decrypts a PEM <c>ENCRYPTED PRIVATE KEY</c>. A wrong passphrase or any malformed input is <c>recovery_key_unreadable</c> (never more detail).</summary>
    public static RecoveryPrivateKey FromEncryptedPem(string pem, ReadOnlySpan<char> passphrase)
    {
        ECDiffieHellman? key = null;
        byte[]? der = null;
        try
        {
            var fields = PemEncoding.Find(pem);
            if (pem[fields.Label] is not "ENCRYPTED PRIVATE KEY") throw new BackupException("recovery_key_unreadable");
            der = Convert.FromBase64String(pem[fields.Base64Data]);
            key = ECDiffieHellman.Create();
            key.ImportEncryptedPkcs8PrivateKey(passphrase, der, out var read);
            if (read != der.Length || !RecoveryPublicKey.IsP256(key)) throw new BackupException("recovery_key_unreadable");
            return new RecoveryPrivateKey(key);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            key?.Dispose();
            throw new BackupException("recovery_key_unreadable");
        }
        catch { key?.Dispose(); throw; }
        finally { if (der is not null) CryptographicOperations.ZeroMemory(der); }
    }

    public void Dispose() => Key.Dispose();
}
