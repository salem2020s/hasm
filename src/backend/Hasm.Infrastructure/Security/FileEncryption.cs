using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Audit;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Security;

/// <summary>Raised when encryption cannot be performed safely (missing/invalid key, tampered or foreign data). The message never contains key material, paths or content.</summary>
public sealed class EncryptionUnavailableException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Encryption settings (section <c>Hasm:Encryption</c>, environment <c>Hasm__Encryption__*</c>). No defaults for key locations; nothing is hardcoded. Parsing never throws: a deployment without
/// valid configuration still starts, and every operation that needs encryption fails CLOSED (no plaintext fallback, no generated replacement key).
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Hasm:Encryption";

    /// <summary>Directory holding the master-key files (<c>&lt;keyId&gt;.ekey</c>), readable only by the service account and kept OUTSIDE the repository, the database and the encrypted-file storage.</summary>
    public string? KeyDirectory { get; init; }
    /// <summary>Identifier of the master key that wraps the data keys of NEW files. Older keys stay in the directory so older files remain readable.</summary>
    public string? ActiveKeyId { get; init; }
    /// <summary>Operator-supplied relaxation of the key-file permission checks for disposable test environments only.</summary>
    public bool AllowInsecureFilePermissions { get; init; }
    /// <summary>Key ids (comma separated in configuration) whose identity tokens, session tags and identity tags are STILL accepted during a controlled key rotation (owner decision OD-6): list the retired key only for the configured maximum session lifetime, then remove it. Values are always written under the active key.</summary>
    public IReadOnlyList<string> IdentityPreviousKeyIds { get; init; } = [];

    public static EncryptionOptions FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        var insecure = false;
        var text = Get("AllowInsecureFilePermissions");
        if (text is not null && !bool.TryParse(text, out insecure)) insecure = false;
        return new EncryptionOptions { KeyDirectory = Get("KeyDirectory"), ActiveKeyId = Get("ActiveKeyId"), AllowInsecureFilePermissions = insecure, IdentityPreviousKeyIds = (Get("IdentityPreviousKeyIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(KekRing.IsValidKeyId).Distinct().ToList() };
    }
}

/// <summary>One master key (key-encryption key). The wrapping key is derived with HKDF-SHA-256 from the master secret, domain-separated from every other use of the same file format.</summary>
public sealed class KekHandle
{
    public const int MinimumKeyBytes = 32;
    private readonly byte[] wrapKey;

    public string Id { get; }

    public KekHandle(string id, byte[] master)
    {
        if (!KekRing.IsValidKeyId(id)) throw new EncryptionUnavailableException("encryption_key_invalid", "Invalid encryption key identifier.");
        if (master.Length < MinimumKeyBytes) throw new EncryptionUnavailableException("encryption_key_invalid", "An encryption key must be at least 32 bytes.");
        Id = id;
        wrapKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("hasm-file-kek/v1/" + id));
    }

    internal byte[] Wrap(ReadOnlySpan<byte> dek, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> tag)
    {
        var cipher = new byte[dek.Length];
        using var aes = new AesGcm(wrapKey, 16);
        aes.Encrypt(nonce, dek, cipher, tag, aad);
        return cipher;
    }

    internal byte[] Unwrap(ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> aad)
    {
        var dek = new byte[wrapped.Length];
        using var aes = new AesGcm(wrapKey, 16);
        try { aes.Decrypt(nonce, wrapped, tag, dek, aad); }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(dek); throw new EncryptionUnavailableException("encryption_authentication_failed", "The encrypted content failed authentication (wrong key, tampering or corruption)."); }
        return dek;
    }

    /// <summary>A purpose-bound subkey (HKDF-SHA-256 over the wrapping key, label <c>hasm-purpose/v1/&lt;purpose&gt;/&lt;keyId&gt;</c>), so other protected data (TOTP secrets) never uses the file-wrapping key itself.</summary>
    internal byte[] DeriveSubkey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, wrapKey, 32, info: Encoding.ASCII.GetBytes($"hasm-purpose/v1/{purpose}/{Id}"));

    public override string ToString() => $"EncryptionKey({Id})"; // never prints material
}

/// <summary>Versioned master keys, looked up by identifier so files written under an earlier key stay readable during and after a rotation.</summary>
public abstract class KekRing
{
    private static readonly Regex KeyIdPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool IsValidKeyId(string id) => KeyIdPattern.IsMatch(id);

    /// <summary>The key for new files. Throws <see cref="EncryptionUnavailableException"/> when missing or unusable (fail closed).</summary>
    public abstract KekHandle GetActive();

    /// <summary>A specific (possibly retired) key, or null when it is not available to this process.</summary>
    public abstract KekHandle? Find(string keyId);
}

/// <summary>Fixed keys held in memory; for tests and composition only (disposable random keys).</summary>
public sealed class InMemoryKekRing : KekRing
{
    private readonly ConcurrentDictionary<string, KekHandle> keys = new();
    private volatile string? active;
    public volatile bool Unavailable;

    public InMemoryKekRing(string activeId = "k1", byte[]? master = null)
    {
        Add(activeId, master ?? RandomNumberGenerator.GetBytes(32));
        active = activeId;
    }

    public void Add(string id, byte[] master) => keys[id] = new KekHandle(id, master);
    public void Activate(string id) => active = keys.ContainsKey(id) ? id : throw new KeyNotFoundException();
    public bool Remove(string id) => keys.TryRemove(id, out _);

    public override KekHandle GetActive() =>
        !Unavailable && active is { } id && keys.TryGetValue(id, out var k) ? k : throw new EncryptionUnavailableException("encryption_key_unavailable", "The active encryption key is not available.");

    public override KekHandle? Find(string keyId) => Unavailable ? null : keys.GetValueOrDefault(keyId);
}

/// <summary>
/// Key files <c>&lt;keyId&gt;.ekey</c> (one line, base64, at least 32 random bytes) in a directory only the service account can read (the same owner-only rules as the audit keys: Linux modes
/// 0700/0600, Windows ACLs without broad read access). Keys are loaded on first use and cached for the process lifetime. A missing or unusable key fails closed; a replacement is NEVER generated
/// implicitly (use <see cref="Generate"/> through the administrative procedure). Key material never appears in logs, exceptions or responses.
/// </summary>
public sealed class FileKekRing(EncryptionOptions options) : KekRing
{
    private readonly ConcurrentDictionary<string, KekHandle> cache = new();
    public const string Extension = ".ekey";

    public override KekHandle GetActive()
    {
        if (options.KeyDirectory is null || options.ActiveKeyId is null || !IsValidKeyId(options.ActiveKeyId))
            throw new EncryptionUnavailableException("encryption_not_configured", $"{EncryptionOptions.SectionName}:KeyDirectory and :ActiveKeyId are required.");
        return Find(options.ActiveKeyId) ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "The active encryption key is missing or unusable.");
    }

    public override KekHandle? Find(string keyId)
    {
        if (!IsValidKeyId(keyId) || options.KeyDirectory is null) return null;
        if (cache.TryGetValue(keyId, out var cached)) return cached;
        var path = Path.Combine(options.KeyDirectory, keyId + Extension);
        try
        {
            if (!File.Exists(path)) return null;
            if (!options.AllowInsecureFilePermissions) AuditFilePermissions.EnsureOwnerOnly(options.KeyDirectory, path);
            var bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            var handle = new KekHandle(keyId, bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return cache.GetOrAdd(keyId, handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or AuditUnavailableException or EncryptionUnavailableException)
        {
            return null; // fail closed without leaking paths or contents
        }
    }

    /// <summary>The ids of the key files in <paramref name="directory"/> (valid ids only; nothing is read or decoded). An absent directory has none.</summary>
    public static IReadOnlyList<string> ListKeyIds(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*" + Extension).Select(f => Path.GetFileNameWithoutExtension(f)!).Where(IsValidKeyId).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Creates a new random 256-bit key file with owner-only permissions. Refuses to overwrite an existing key. Returns nothing but succeeds or throws.</summary>
    public static void Generate(string directory, string keyId)
    {
        if (!IsValidKeyId(keyId)) throw new ArgumentException("Invalid key identifier (lowercase letters, digits, hyphen; max 32).", nameof(keyId));
        AuditFilePermissions.CreateOwnerOnlyDirectory(directory);
        var path = Path.Combine(directory, keyId + Extension);
        var secret = RandomNumberGenerator.GetBytes(32);
        try { AuditFilePermissions.CreateOwnerOnlyFile(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(secret) + "\n")); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}

/// <summary>
/// Authenticated envelope encryption of whole files (the largest import file is limited, so one AES-256-GCM operation per file). Format v1:
/// <c>"HFE1" | keyIdLen(1) | keyId | wrapNonce(12) | wrappedDek(32) | wrapTag(16) | dataNonce(12) | cipherLen(8, big endian) | ciphertext | dataTag(16)</c>.
/// A fresh random 256-bit data key and fresh random nonces are generated for EVERY encryption, so no (key, nonce) pair is ever reused. The data key is wrapped with the master key using AES-256-GCM
/// (associated data: format, key id and context); the content is authenticated with the context (for example company and batch), so an encrypted file cannot be moved to another context or altered.
/// Decryption verifies the authentication tag BEFORE any plaintext is returned. Re-wrapping replaces only the wrapped data key (the ciphertext is untouched).
/// </summary>
public sealed class EnvelopeEncryptor(KekRing ring)
{
    /// <summary>The identifier of the master key new files are wrapped with (throws when the active key is unavailable: fail closed).</summary>
    public string ActiveKeyId => ring.GetActive().Id;

    private static readonly byte[] Magic = "HFE1"u8.ToArray();
    private const int NonceLength = 12, TagLength = 16, DekLength = 32;

    private static byte[] WrapAad(string keyId, string context) => [.. Magic, .. Encoding.ASCII.GetBytes(keyId), (byte)'|', .. Encoding.UTF8.GetBytes(context)];
    private static byte[] DataAad(string context) => [.. Magic, .. "data|"u8.ToArray(), .. Encoding.UTF8.GetBytes(context)];

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string context)
    {
        var kek = ring.GetActive();
        var dek = RandomNumberGenerator.GetBytes(DekLength);
        try
        {
            var wrapNonce = RandomNumberGenerator.GetBytes(NonceLength);
            var dataNonce = RandomNumberGenerator.GetBytes(NonceLength);
            var wrapTag = new byte[TagLength];
            var wrapped = kek.Wrap(dek, wrapNonce, WrapAad(kek.Id, context), wrapTag);
            var cipher = new byte[plaintext.Length];
            var dataTag = new byte[TagLength];
            using (var aes = new AesGcm(dek, TagLength)) aes.Encrypt(dataNonce, plaintext, cipher, dataTag, DataAad(context));
            var id = Encoding.ASCII.GetBytes(kek.Id);
            var output = new byte[4 + 1 + id.Length + NonceLength + DekLength + TagLength + NonceLength + 8 + cipher.Length + TagLength];
            var o = 0;
            void Put(ReadOnlySpan<byte> b) { b.CopyTo(output.AsSpan(o)); o += b.Length; }
            Put(Magic); output[o++] = (byte)id.Length; Put(id); Put(wrapNonce); Put(wrapped); Put(wrapTag); Put(dataNonce);
            BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(o), cipher.Length); o += 8;
            Put(cipher); Put(dataTag);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private readonly record struct Parsed(string KeyId, int WrapNonce, int Wrapped, int WrapTag, int DataNonce, int Cipher, int CipherLength, int DataTag);

    private static Parsed Parse(ReadOnlySpan<byte> e)
    {
        EncryptionUnavailableException Bad() => new("encryption_format_invalid", "The encrypted content is not valid.");
        if (e.Length < 5 || !e[..4].SequenceEqual(Magic)) throw Bad();
        int idLen = e[4], o = 5;
        if (idLen is 0 or > 32 || e.Length < o + idLen + NonceLength + DekLength + TagLength + NonceLength + 8 + TagLength) throw Bad();
        var keyId = Encoding.ASCII.GetString(e.Slice(o, idLen)); o += idLen;
        if (!KekRing.IsValidKeyId(keyId)) throw Bad();
        var wrapNonce = o; o += NonceLength;
        var wrapped = o; o += DekLength;
        var wrapTag = o; o += TagLength;
        var dataNonce = o; o += NonceLength;
        var len = BinaryPrimitives.ReadInt64BigEndian(e.Slice(o, 8)); o += 8;
        if (len < 0 || len > e.Length - o - TagLength || o + len + TagLength != e.Length) throw Bad();
        return new Parsed(keyId, wrapNonce, wrapped, wrapTag, dataNonce, o, (int)len, o + (int)len);
    }

    /// <summary>The identifier of the master key that wraps the data key of this encrypted content.</summary>
    public static string KeyIdOf(ReadOnlySpan<byte> envelope) => Parse(envelope).KeyId;

    public byte[] Decrypt(byte[] envelope, string context)
    {
        var p = Parse(envelope);
        var kek = ring.Find(p.KeyId) ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "The key that protects this content is not available.");
        var dek = kek.Unwrap(envelope.AsSpan(p.Wrapped, DekLength), envelope.AsSpan(p.WrapNonce, NonceLength), envelope.AsSpan(p.WrapTag, TagLength), WrapAad(p.KeyId, context));
        try
        {
            var plain = new byte[p.CipherLength];
            using var aes = new AesGcm(dek, TagLength);
            try { aes.Decrypt(envelope.AsSpan(p.DataNonce, NonceLength), envelope.AsSpan(p.Cipher, p.CipherLength), envelope.AsSpan(p.DataTag, TagLength), plain, DataAad(context)); }
            catch (CryptographicException) { CryptographicOperations.ZeroMemory(plain); throw new EncryptionUnavailableException("encryption_authentication_failed", "The encrypted content failed authentication (wrong key, tampering or corruption)."); }
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    /// <summary>Re-wraps the data key under the CURRENT active master key (the old key must still be available). The ciphertext and its authentication tag are unchanged.</summary>
    public byte[] Rewrap(byte[] envelope, string context)
    {
        var p = Parse(envelope);
        var oldKey = ring.Find(p.KeyId) ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "The key that protects this content is not available.");
        var newKey = ring.GetActive();
        var dek = oldKey.Unwrap(envelope.AsSpan(p.Wrapped, DekLength), envelope.AsSpan(p.WrapNonce, NonceLength), envelope.AsSpan(p.WrapTag, TagLength), WrapAad(p.KeyId, context));
        try
        {
            var wrapNonce = RandomNumberGenerator.GetBytes(NonceLength);
            var wrapTag = new byte[TagLength];
            var wrapped = newKey.Wrap(dek, wrapNonce, WrapAad(newKey.Id, context), wrapTag);
            var id = Encoding.ASCII.GetBytes(newKey.Id);
            var body = envelope.AsSpan(p.DataNonce, NonceLength + 8 + p.CipherLength + TagLength);
            var output = new byte[4 + 1 + id.Length + NonceLength + DekLength + TagLength + body.Length];
            var o = 0;
            void Put(ReadOnlySpan<byte> b) { b.CopyTo(output.AsSpan(o)); o += b.Length; }
            Put(Magic); output[o++] = (byte)id.Length; Put(id); Put(wrapNonce); Put(wrapped); Put(wrapTag); Put(body);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }
}
