using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Backup;

/// <summary>The parsed, not yet authenticated header of an <see cref="Hbe1"/> file (enough to choose the recovery key).</summary>
public sealed record Hbe1Header(int Version, string KeyId, string Purpose, string Name, int ChunkSize);

/// <summary>
/// The AUTHENTICITY anchor of one HBE1 file (review H1 of #116): the SHA-256 of its header and of every chunk as stored (ciphertext and tag), in order. It is recorded in the SIGNED manifest; decryption
/// releases a chunk only after its stored bytes match the corresponding hash, so no unauthenticated plaintext ever reaches pg_restore or attachment staging. (ECIES to a public key and the header MAC
/// prove integrity against accidents, not who wrote the file.)
/// </summary>
public sealed record Hbe1Digest(string HeaderSha256, IReadOnlyList<string> ChunkSha256);

/// <summary>
/// The backup file encryption format <c>HBE1</c> (Phase 4.3 slice 5, owner decision D-085, technical choice D-090). Each file is encrypted on its own to the offline recovery PUBLIC key (ECIES):
/// <list type="bullet">
/// <item>an ephemeral P-256 key agrees a secret with the recovery key; HKDF-SHA256 (32-byte random salt; info = format, key id, purpose, set-relative file name and a hash of the ephemeral key) derives
/// a 256-bit file key and a header key;</item>
/// <item>the header (magic, version, key id, purpose, name, ephemeral key, salt, nonce prefix, chunk size) is authenticated by HMAC-SHA256 with the header key BEFORE any chunk is processed;</item>
/// <item>the content is AES-256-GCM in chunks (default 1 MiB). Nonce = 7-byte random prefix | 4-byte big-endian chunk index | 1-byte last-chunk flag; AAD = SHA-256 of the authenticated header | chunk index.
/// The last chunk is the one followed by end of file (0 to chunk-size bytes; an empty file is one empty last chunk).</item>
/// </list>
/// Detected: header tampering, a wrong key, a file presented under another name or purpose, reordered, removed, truncated or appended chunks. Memory stays bounded (one chunk at a time). Decrypted output is
/// written only after each chunk's tag verified; a caller must still treat the whole output as invalid when decryption throws (a later chunk can fail) and discard it.
/// </summary>
public static partial class Hbe1
{
    public const int DefaultChunkSize = 1024 * 1024;
    public const int MinChunkSize = 1024;
    public const int MaxChunkSize = 16 * 1024 * 1024;
    public const int SaltLength = 32;
    public const int NoncePrefixLength = 7;
    public const int TagLength = 16;
    public const int HeaderTagLength = 32;
    private const byte Version = 1;
    private static readonly byte[] Magic = "HBE1"u8.ToArray();
    private const int MaxNameBytes = 512;
    private const int MaxSpkiBytes = 512;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)] private static partial Regex PurposePattern();
    // set-relative names: segments of letters, digits, '.', '_', '-' joined by '/', never "." or ".." segments
    [GeneratedRegex("^(?!.*(^|/)\\.{1,2}(/|$))[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();

    public static bool IsPurpose(string? p) => p is not null && PurposePattern().IsMatch(p);
    public static bool IsName(string? n) => n is not null && Encoding.UTF8.GetByteCount(n) <= MaxNameBytes && NamePattern().IsMatch(n);

    /// <summary>Encrypts <paramref name="plaintext"/> (read to its end) into <paramref name="output"/> for <paramref name="key"/>, bound to <paramref name="purpose"/> and <paramref name="name"/>.</summary>
    public static async Task<Hbe1Digest> EncryptAsync(Stream plaintext, Stream output, RecoveryPublicKey key, string purpose, string name, CancellationToken ct = default, int chunkSize = DefaultChunkSize)
    {
        if (!IsPurpose(purpose) || !IsName(name)) throw new BackupException("hbe_binding_invalid");
        if (chunkSize is < MinChunkSize or > MaxChunkSize) throw new BackupException("hbe_chunk_size_invalid");
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralSpki = ephemeral.ExportSubjectPublicKeyInfo();
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var prefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        var shared = ephemeral.DeriveRawSecretAgreement(key.Key.PublicKey);
        var (fileKey, headerKey) = DeriveKeys(shared, salt, key.KeyId, purpose, name, ephemeralSpki);
        try
        {
            var unsigned = HeaderBytes(key.KeyId, purpose, name, ephemeralSpki, salt, prefix, chunkSize);
            var header = Concat(unsigned, HMACSHA256.HashData(headerKey, unsigned));
            await output.WriteAsync(header, ct);
            var headerHash = SHA256.HashData(header);
            var chunkHashes = new List<string>();
            using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            using var aes = new AesGcm(fileKey, TagLength);
            var current = new byte[chunkSize]; var next = new byte[chunkSize];
            var currentLength = await ReadFullAsync(plaintext, current, ct);
            var cipher = new byte[chunkSize]; var tag = new byte[TagLength];
            for (uint index = 0; ; index++)
            {
                if (index == uint.MaxValue) throw new BackupException("hbe_too_large");
                // look ahead: the current chunk is the last one exactly when nothing follows it
                var nextLength = currentLength == chunkSize ? await ReadFullAsync(plaintext, next, ct) : 0;
                var last = nextLength == 0;
                aes.Encrypt(Nonce(prefix, index, last), current.AsSpan(0, currentLength), cipher.AsSpan(0, currentLength), tag, Aad(headerHash, index));
                await output.WriteAsync(cipher.AsMemory(0, currentLength), ct);
                await output.WriteAsync(tag, ct);
                chunkHash.AppendData(cipher, 0, currentLength); chunkHash.AppendData(tag);
                chunkHashes.Add(Convert.ToHexStringLower(chunkHash.GetHashAndReset()));
                if (last) break;
                (current, next) = (next, current);
                currentLength = nextLength;
            }
            CryptographicOperations.ZeroMemory(current); CryptographicOperations.ZeroMemory(next);
            return new Hbe1Digest(Convert.ToHexStringLower(headerHash), chunkHashes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(fileKey); CryptographicOperations.ZeroMemory(headerKey);
        }
    }

    /// <summary>Reads and structurally validates the header (no key needed; NOT authenticated: use it only to select the recovery key).</summary>
    public static async Task<Hbe1Header> ReadHeaderAsync(Stream input, CancellationToken ct = default) => (await ParseHeaderAsync(input, ct)).Header;

    /// <summary>
    /// The digest of a stored HBE1 file as it is (header hash and the hash of every stored chunk), computed WITHOUT a key. Used (1) to compare a stored file with its entry in the signed manifest before
    /// a restore, and (2) for a file that is itself authenticated by a signature over its exact bytes (an encrypted manifest, review L-b), whose digest is then derived from those bytes.
    /// </summary>
    public static async Task<Hbe1Digest> ComputeDigestAsync(Stream input, CancellationToken ct = default)
    {
        var (h, unsigned, tag, _, _, _) = await ParseHeaderAsync(input, ct);
        var headerHash = Convert.ToHexStringLower(SHA256.HashData(Concat(unsigned, tag)));
        var chunks = new List<string>();
        var buffer = new byte[h.ChunkSize + TagLength];
        while (true)
        {
            var n = await ReadFullAsync(input, buffer, ct);
            if (n == 0 && chunks.Count > 0) break;
            chunks.Add(Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, n))));
            if (n < buffer.Length) break;
        }
        return new Hbe1Digest(headerHash, chunks);
    }

    /// <summary>
    /// Decrypts <paramref name="input"/> into <paramref name="plaintext"/>. The header and every chunk are first checked against <paramref name="expected"/> from the SIGNED manifest
    /// (<c>hbe_not_authenticated</c>); a chunk is decrypted and released only after that check and its AEAD tag. Refused before any output: an unknown version or malformed header (<c>hbe_malformed</c>), another recovery key
    /// (<c>recovery_key_mismatch</c>), another purpose or name (<c>hbe_wrong_file</c>), a header that fails authentication (<c>hbe_authentication_failed</c>). A chunk that fails authentication, including
    /// truncation, reordering and appended data, is <c>hbe_authentication_failed</c>; output written before it must be discarded by the caller.
    /// </summary>
    public static async Task DecryptAsync(Stream input, Stream plaintext, RecoveryPrivateKey key, string expectedPurpose, string expectedName, Hbe1Digest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var (h, unsigned, headerTag, ephemeralSpki, salt, prefix) = await ParseHeaderAsync(input, ct);
        // H1: the header must be the one the signed manifest names, before the key is even used
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(Concat(unsigned, headerTag))), expected.HeaderSha256, StringComparison.Ordinal)) throw new BackupException("hbe_not_authenticated");
        if (h.KeyId != key.KeyId) throw new BackupException("recovery_key_mismatch");
        if (h.Purpose != expectedPurpose || h.Name != expectedName) throw new BackupException("hbe_wrong_file");
        using var ephemeral = ECDiffieHellman.Create();
        try
        {
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralSpki, out var read);
            if (read != ephemeralSpki.Length || !RecoveryPublicKey.IsP256(ephemeral)) throw new BackupException("hbe_malformed");
        }
        catch (Exception ex) when (ex is not BackupException) { throw new BackupException("hbe_malformed"); }   // another curve or algorithm (PlatformNotSupported), an off-curve point (rejected by the platform import)
        byte[] shared;
        try { shared = key.Key.DeriveRawSecretAgreement(ephemeral.PublicKey); }
        catch (Exception ex) when (ex is not BackupException) { throw new BackupException("hbe_malformed"); }
        var (fileKey, headerKey) = DeriveKeys(shared, salt, h.KeyId, h.Purpose, h.Name, ephemeralSpki);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(headerKey, unsigned), headerTag)) throw new BackupException("hbe_authentication_failed");
            var headerHash = SHA256.HashData(Concat(unsigned, headerTag));
            using var aes = new AesGcm(fileKey, TagLength);
            var size = h.ChunkSize + TagLength;
            var current = new byte[size]; var next = new byte[size];
            var currentLength = await ReadFullAsync(input, current, ct);
            var plain = new byte[h.ChunkSize];
            for (uint index = 0; ; index++)
            {
                if (index == uint.MaxValue) throw new BackupException("hbe_too_large");
                // H1: the stored chunk must match the signed list BEFORE it is decrypted or released
                if (index >= expected.ChunkSha256.Count || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(current.AsSpan(0, currentLength))), expected.ChunkSha256[(int)index], StringComparison.Ordinal))
                    throw new BackupException("hbe_not_authenticated");
                if (currentLength < TagLength) throw new BackupException("hbe_authentication_failed");          // a missing or cut chunk
                var nextLength = currentLength == size ? await ReadFullAsync(input, next, ct) : 0;
                var last = nextLength == 0;
                if (last != (index + 1 == expected.ChunkSha256.Count)) throw new BackupException("hbe_not_authenticated");   // fewer (truncation) or more (extension) chunks than signed, checked before the tag
                var cipherLength = currentLength - TagLength;
                try { aes.Decrypt(Nonce(prefix, index, last), current.AsSpan(0, cipherLength), current.AsSpan(cipherLength, TagLength), plain.AsSpan(0, cipherLength), Aad(headerHash, index)); }
                catch (CryptographicException) { CryptographicOperations.ZeroMemory(plain); throw new BackupException("hbe_authentication_failed"); }
                await plaintext.WriteAsync(plain.AsMemory(0, cipherLength), ct);
                if (last) break;
                (current, next) = (next, current);
                currentLength = nextLength;
            }
            CryptographicOperations.ZeroMemory(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(fileKey); CryptographicOperations.ZeroMemory(headerKey);
        }
    }

    // ---- internals -----------------------------------------------------------------------------

    internal static byte[] Nonce(ReadOnlySpan<byte> prefix, uint index, bool last)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength, 4), index);
        nonce[11] = last ? (byte)1 : (byte)0;
        return nonce;
    }

    private static byte[] Aad(ReadOnlySpan<byte> headerHash, uint index)
    {
        var aad = new byte[headerHash.Length + 4];
        headerHash.CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(headerHash.Length), index);
        return aad;
    }

    private static (byte[] FileKey, byte[] HeaderKey) DeriveKeys(byte[] shared, byte[] salt, string keyId, string purpose, string name, byte[] ephemeralSpki)
    {
        var info = Encoding.UTF8.GetBytes($"hasm-backup/HBE1/v{Version}|{keyId}|{purpose}|{name}|{Convert.ToHexStringLower(SHA256.HashData(ephemeralSpki))}");
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 64, salt, info);
        try { return (okm[..32], okm[32..]); }
        finally { CryptographicOperations.ZeroMemory(okm); }
    }

    private static byte[] HeaderBytes(string keyId, string purpose, string name, byte[] spki, byte[] salt, byte[] prefix, int chunkSize)
    {
        var id = Encoding.ASCII.GetBytes(keyId); var p = Encoding.ASCII.GetBytes(purpose); var n = Encoding.UTF8.GetBytes(name);
        using var ms = new MemoryStream();
        ms.Write(Magic); ms.WriteByte(Version);
        ms.WriteByte((byte)id.Length); ms.Write(id);
        ms.WriteByte((byte)p.Length); ms.Write(p);
        Span<byte> u16 = stackalloc byte[2]; Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)n.Length); ms.Write(u16); ms.Write(n);
        BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)spki.Length); ms.Write(u16); ms.Write(spki);
        ms.Write(salt); ms.Write(prefix);
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)chunkSize); ms.Write(u32);
        return ms.ToArray();
    }

    private static async Task<(Hbe1Header Header, byte[] Unsigned, byte[] HeaderTag, byte[] Spki, byte[] Salt, byte[] Prefix)> ParseHeaderAsync(Stream input, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        async Task<byte[]> Take(int n)
        {
            var b = new byte[n];
            if (await ReadFullAsync(input, b, ct) != n) throw new BackupException("hbe_malformed");
            ms.Write(b);
            return b;
        }
        if (!(await Take(4)).AsSpan().SequenceEqual(Magic)) throw new BackupException("hbe_malformed");
        var version = (await Take(1))[0];
        if (version != Version) throw new BackupException("hbe_unsupported_version");
        var keyId = Encoding.ASCII.GetString(await Take((await Take(1))[0]));
        var purpose = Encoding.ASCII.GetString(await Take((await Take(1))[0]));
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(await Take(2));
        if (nameLength > MaxNameBytes) throw new BackupException("hbe_malformed");
        string name;
        try { name = new UTF8Encoding(false, true).GetString(await Take(nameLength)); }
        catch (DecoderFallbackException) { throw new BackupException("hbe_malformed"); }
        var spkiLength = BinaryPrimitives.ReadUInt16BigEndian(await Take(2));
        if (spkiLength is 0 or > MaxSpkiBytes) throw new BackupException("hbe_malformed");
        var spki = await Take(spkiLength);
        var salt = await Take(SaltLength);
        var prefix = await Take(NoncePrefixLength);
        var chunkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(await Take(4));
        if (!RecoveryKeys.IsKeyId(keyId) || !IsPurpose(purpose) || !IsName(name) || chunkSize is < MinChunkSize or > MaxChunkSize) throw new BackupException("hbe_malformed");
        var unsigned = ms.ToArray();
        var tag = new byte[HeaderTagLength];
        if (await ReadFullAsync(input, tag, ct) != HeaderTagLength) throw new BackupException("hbe_malformed");
        return (new Hbe1Header(version, keyId, purpose, name, chunkSize), unsigned, tag, spki, salt, prefix);
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var n = 0;
        while (n < buffer.Length)
        {
            var r = await s.ReadAsync(buffer.AsMemory(n), ct);
            if (r == 0) break;
            n += r;
        }
        return n;
    }

    private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }
}
