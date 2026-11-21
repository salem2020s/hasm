using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Identity;

/// <summary>A TOTP secret as stored: AES-256-GCM ciphertext, its nonce and tag, and the id of the key that protects it.</summary>
public sealed record ProtectedTotpSecret(byte[] Cipher, byte[] Nonce, byte[] Tag, string KekId);

/// <summary>
/// Protects TOTP secrets with the existing protected key ring (Phase 4.1, D-080 (2)). The AES-256-GCM key is a purpose-bound subkey (<c>totp</c>) of the ring key, so a file-wrapping key never
/// decrypts a TOTP secret and the other way round. The authenticated data is <c>user_id | kek_id | secret_id</c> (nothing mutable): a ciphertext copied to another user, key or row fails authentication.
/// A missing or unusable key fails closed with <see cref="MfaUnavailableException"/> (the user is neither accepted nor downgraded). Plaintext is returned to the caller, who zeroes it after use.
/// </summary>
public sealed class TotpSecretProtector(KekRing ring)
{
    public const int NonceBytes = 12, TagBytes = 16;

    public ProtectedTotpSecret Protect(Guid userId, Guid secretId, ReadOnlySpan<byte> secret)
    {
        if (secret.Length == 0) throw new ArgumentException("A TOTP secret must not be empty.", nameof(secret));
        var kek = Active();
        var key = kek.DeriveSubkey("totp");
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var tag = new byte[TagBytes];
            var cipher = new byte[secret.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, secret, cipher, tag, Aad(userId, kek.Id, secretId));
            return new ProtectedTotpSecret(cipher, nonce, tag, kek.Id);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Decrypts under the key named by the record (a retired key stays readable while it is still in the ring). Throws <see cref="MfaUnavailableException"/> on a missing key or failed authentication.</summary>
    public byte[] Unprotect(Guid userId, Guid secretId, ProtectedTotpSecret protectedSecret)
    {
        if (!KekRing.IsValidKeyId(protectedSecret.KekId)) throw new MfaUnavailableException("totp_secret_invalid");
        var kek = ring.Find(protectedSecret.KekId) ?? throw new MfaUnavailableException("totp_key_unavailable");
        if (protectedSecret.Nonce.Length != NonceBytes || protectedSecret.Tag.Length != TagBytes || protectedSecret.Cipher.Length == 0) throw new MfaUnavailableException("totp_secret_invalid");
        var key = kek.DeriveSubkey("totp");
        var plain = new byte[protectedSecret.Cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(protectedSecret.Nonce, protectedSecret.Cipher, protectedSecret.Tag, plain, Aad(userId, kek.Id, secretId));
            return plain;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new MfaUnavailableException("totp_secret_authentication_failed");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Re-encrypts under the ACTIVE key (rotation). The caller stores the result and the old record is replaced atomically.</summary>
    public ProtectedTotpSecret Rewrap(Guid userId, Guid secretId, ProtectedTotpSecret current)
    {
        var plain = Unprotect(userId, secretId, current);
        try { return Protect(userId, secretId, plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private KekHandle Active()
    {
        try { return ring.GetActive(); }
        catch (EncryptionUnavailableException) { throw new MfaUnavailableException("totp_key_unavailable"); }
    }

    private static byte[] Aad(Guid userId, string kekId, Guid secretId)
    {
        var id = Encoding.ASCII.GetBytes(kekId);
        var aad = new byte[16 + 1 + id.Length + 16];
        userId.TryWriteBytes(aad.AsSpan(0, 16), bigEndian: true, out _);
        aad[16] = (byte)id.Length;
        id.CopyTo(aad, 17);
        secretId.TryWriteBytes(aad.AsSpan(17 + id.Length, 16), bigEndian: true, out _);
        return aad;
    }
}
