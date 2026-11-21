using System.Security.Cryptography;
using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 5 (D-085, D-089, D-090; review H1 of #116): the offline recovery key pair and the HBE1 backup file format. Round trips around the chunk boundaries, nonce uniqueness, and two layers of
/// detection: (1) authenticity, every header and chunk checked against the digest from the signed manifest before release (<c>hbe_not_authenticated</c>); (2) the format's own integrity, the header MAC and
/// per-chunk AEAD with index and last-chunk binding, exercised here with a digest recomputed over the tampered bytes (as if the signer itself were compromised).
/// </summary>
public sealed class Hbe1Tests
{
    private const string Pass = "correct horse battery staple twenty+";
    private const int Chunk = 1024;
    private const int Stored = Chunk + Hbe1.TagLength;
    private static readonly Lazy<(RecoveryKeys.GeneratedPair Pair, RecoveryPublicKey Public, RecoveryPrivateKey Private)> Keys = new(() =>
    {
        var pair = RecoveryKeys.Generate(Pass);
        return (pair, RecoveryPublicKey.FromPem(pair.PublicKeyPem), RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass));
    });
    private static RecoveryPublicKey Pub => Keys.Value.Public;
    private static RecoveryPrivateKey Priv => Keys.Value.Private;

    private static async Task<(byte[] File, Hbe1Digest Digest)> Encrypt(byte[] plain, string purpose = "company-dump", string name = "database.dump", int chunk = Chunk)
    {
        using var output = new MemoryStream();
        var digest = await Hbe1.EncryptAsync(new MemoryStream(plain), output, Pub, purpose, name, default, chunk);
        return (output.ToArray(), digest);
    }

    private static async Task<byte[]> Decrypt(byte[] file, Hbe1Digest digest, string purpose = "company-dump", string name = "database.dump", RecoveryPrivateKey? key = null)
    {
        using var output = new MemoryStream();
        await Hbe1.DecryptAsync(new MemoryStream(file), output, key ?? Priv, purpose, name, digest);
        return output.ToArray();
    }

    private static async Task<string> Refused(byte[] file, Hbe1Digest digest, string purpose = "company-dump", string name = "database.dump", RecoveryPrivateKey? key = null) =>
        (await Assert.ThrowsAsync<BackupException>(() => Decrypt(file, digest, purpose, name, key))).Code;

    private static int ChunkCount(int plainLength) => Math.Max(1, (plainLength + Chunk - 1) / Chunk);
    private static int HeaderLength(byte[] file, int plainLength) => file.Length - plainLength - ChunkCount(plainLength) * Hbe1.TagLength;

    /// <summary>The digest of arbitrary bytes split as header + stored chunks: what a compromised signer would sign for tampered bytes.</summary>
    private static Hbe1Digest SelfDigest(byte[] file, int headerLength)
    {
        var chunks = new List<string>();
        for (var at = headerLength; at < file.Length || chunks.Count == 0; at += Stored)
            chunks.Add(Convert.ToHexStringLower(SHA256.HashData(file.AsSpan(at, Math.Min(Stored, Math.Max(0, file.Length - at))))));
        return new Hbe1Digest(Convert.ToHexStringLower(SHA256.HashData(file.AsSpan(0, Math.Min(headerLength, file.Length)))), chunks);
    }

    // ---- recovery keys ---------------------------------------------------------------------------

    [Fact]
    public void The_recovery_pair_has_a_stable_key_id_the_private_key_needs_its_passphrase_and_only_a_p256_public_key_is_accepted()
    {
        var (pair, pub, priv) = Keys.Value;
        Assert.Matches("^rk-[0-9a-f]{16}$", pair.KeyId);
        Assert.Equal(pair.KeyId, pub.KeyId);
        Assert.Equal(pair.KeyId, priv.KeyId);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pair.PublicKeyPem);
        var pemFields = PemEncoding.Find(pair.EncryptedPrivateKeyPem);
        Assert.Equal("ENCRYPTED PRIVATE KEY", pair.EncryptedPrivateKeyPem[pemFields.Label]);                       // PKCS#8 encrypted (PBES2) only
        Assert.DoesNotContain("BEGIN PRIVATE KEY", pair.EncryptedPrivateKeyPem);                                    // never an unencrypted private key
        Assert.Equal("recovery_key_unreadable", Assert.Throws<BackupException>(() => RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, "wrong passphrase, long enough")).Code);
        Assert.Equal("recovery_key_unreadable", Assert.Throws<BackupException>(() => RecoveryPrivateKey.FromEncryptedPem(pair.PublicKeyPem, Pass)).Code);
        Assert.Equal("passphrase_too_short", Assert.Throws<BackupException>(() => RecoveryKeys.Generate("nineteen characters")).Code);   // review M3: at least 20
        Assert.Equal("recovery_key_invalid", Assert.Throws<BackupException>(() => RecoveryPublicKey.FromPem(pair.EncryptedPrivateKeyPem)).Code);
        Assert.Equal("recovery_key_invalid", Assert.Throws<BackupException>(() => RecoveryPublicKey.FromPem("not a pem")).Code);
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        Assert.Equal("recovery_key_invalid", Assert.Throws<BackupException>(() => RecoveryPublicKey.FromPem(PemEncoding.WriteString("PUBLIC KEY", p384.ExportSubjectPublicKeyInfo()))).Code);
        using var rsa = RSA.Create(2048);
        Assert.Equal("recovery_key_invalid", Assert.Throws<BackupException>(() => RecoveryPublicKey.FromPem(PemEncoding.WriteString("PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo()))).Code);
        Assert.NotEqual(pair.KeyId, RecoveryKeys.Generate(Pass).KeyId);
    }

    // ---- round trips -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData(3 * Chunk + 5)]
    public async Task Content_of_every_size_around_the_chunk_boundary_round_trips_and_has_one_signed_hash_per_chunk(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var (file, digest) = await Encrypt(plain);
        Assert.Equal(plain, await Decrypt(file, digest));
        Assert.Equal(ChunkCount(size), digest.ChunkSha256.Count);                                                     // an empty file still has one final chunk
        Assert.Equal(SelfDigest(file, HeaderLength(file, size)).HeaderSha256, digest.HeaderSha256);
        Assert.Equal(SelfDigest(file, HeaderLength(file, size)).ChunkSha256, digest.ChunkSha256);
        var header = await Hbe1.ReadHeaderAsync(new MemoryStream(file));
        Assert.Equal((1, Pub.KeyId, "company-dump", "database.dump", Chunk), (header.Version, header.KeyId, header.Purpose, header.Name, header.ChunkSize));
    }

    [Fact]
    public async Task The_default_one_mebibyte_chunks_round_trip_a_multi_chunk_file()
    {
        var plain = RandomNumberGenerator.GetBytes(Hbe1.DefaultChunkSize * 2 + 12345);
        using var output = new MemoryStream();
        const string name = "attachments/0123456789abcdef0123456789abcdef.att";
        var digest = await Hbe1.EncryptAsync(new MemoryStream(plain), output, Pub, "attachment", name);
        Assert.Equal(3, digest.ChunkSha256.Count);
        Assert.Equal(plain, await Decrypt(output.ToArray(), digest, "attachment", name));
    }

    [Fact]
    public async Task Every_encryption_is_fresh_and_nonces_never_repeat_within_a_file()
    {
        var plain = RandomNumberGenerator.GetBytes(2 * Chunk);
        var (a, da) = await Encrypt(plain); var (b, db) = await Encrypt(plain);
        Assert.NotEqual(a, b);                                                                                         // fresh ephemeral key, salt and nonce prefix
        Assert.NotEqual(da.HeaderSha256, db.HeaderSha256);
        Assert.Equal(plain, await Decrypt(a, da)); Assert.Equal(plain, await Decrypt(b, db));
        Assert.Equal("hbe_not_authenticated", await Refused(a, db));                                                  // another file's signed digest
        var prefix = RandomNumberGenerator.GetBytes(Hbe1.NoncePrefixLength);
        var nonces = new HashSet<string>();
        for (uint i = 0; i < 2000; i++) { Assert.True(nonces.Add(Convert.ToHexString(Hbe1.Nonce(prefix, i, false)))); Assert.True(nonces.Add(Convert.ToHexString(Hbe1.Nonce(prefix, i, true)))); }
        Assert.Equal(12, Hbe1.Nonce(prefix, 0, true).Length);
    }

    // ---- authenticity (signed digest) and integrity (format) --------------------------------------

    // ---- every altered byte, exhaustively --------------------------------------------------------
    //
    // The refusal that fires first depends on WHERE the byte is. A header field that frames the rest (a length) changes how the parser reads everything after it; for the SPKI length that can run into the
    // chunk bytes, whose content is random per run, so the same position gave "hbe_malformed" in almost every run and "hbe_not_authenticated" in about one run in 256 (the stray four bytes read as the chunk
    // size happened to be in range, the parse completed, and the header hash of what was parsed no longer equalled the digest the test had computed over the ORIGINAL header). Both refusals are legitimate there;
    // the test now says so per region instead of accepting one code everywhere, and it covers every header position with every flip value.

    private enum Part { Magic, Version, KeyIdLength, KeyId, PurposeLength, Purpose, NameLength, Name, SpkiLength, Spki, Salt, NoncePrefix, ChunkSize, HeaderTag, Chunk }

    private sealed record Region(Part Part, int Start, int Length);

    /// <summary>The layout of the header of an UNTAMPERED file (magic, version, then the length-prefixed fields, salt, nonce prefix, chunk size and the header tag), derived from its own length fields.</summary>
    private static List<Region> HeaderRegions(byte[] file)
    {
        var regions = new List<Region>(); var at = 0;
        void Add(Part part, int length) { regions.Add(new(part, at, length)); at += length; }
        Add(Part.Magic, 4); Add(Part.Version, 1);
        var keyIdLength = file[at]; Add(Part.KeyIdLength, 1); Add(Part.KeyId, keyIdLength);
        var purposeLength = file[at]; Add(Part.PurposeLength, 1); Add(Part.Purpose, purposeLength);
        var nameLength = (file[at] << 8) | file[at + 1]; Add(Part.NameLength, 2); Add(Part.Name, nameLength);
        var spkiLength = (file[at] << 8) | file[at + 1]; Add(Part.SpkiLength, 2); Add(Part.Spki, spkiLength);
        Add(Part.Salt, Hbe1.SaltLength); Add(Part.NoncePrefix, Hbe1.NoncePrefixLength); Add(Part.ChunkSize, 4); Add(Part.HeaderTag, Hbe1.HeaderTagLength);
        return regions;
    }

    private const string Malformed = "hbe_malformed", Unsupported = "hbe_unsupported_version", NotAuthenticated = "hbe_not_authenticated", AuthFailed = "hbe_authentication_failed", KeyMismatch = "recovery_key_mismatch", WrongFile = "hbe_wrong_file";

    /// <summary>Refusals that are legitimate for a byte of this part when the digest is the one signed for the ORIGINAL file: the header is parsed first (a field the parser rejects, <c>hbe_malformed</c> or <c>hbe_unsupported_version</c>), then compared with the signed digest, so a header that parses but differs is <c>hbe_not_authenticated</c>; nothing after the digest check can be reached.</summary>
    private static string[] AllowedUnderOriginalDigest(Part part) => part switch
    {
        Part.Magic or Part.KeyIdLength or Part.PurposeLength or Part.NameLength => [Malformed],                          // the magic is checked first; a changed length makes the id, purpose or name unparseable
        Part.Version => [Unsupported],                                                                                  // any other version number
        Part.KeyId or Part.Purpose or Part.Name or Part.SpkiLength or Part.ChunkSize => [Malformed, NotAuthenticated],    // still valid text or a range the parser accepts: digest mismatch; invalid: refused by the parser
        Part.Spki or Part.Salt or Part.NoncePrefix or Part.HeaderTag or Part.Chunk => [NotAuthenticated],                // content the parser does not validate: only the digest can refuse it
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>Refusals that are legitimate when the digest was recomputed over the tampered bytes (a compromised signer), so the format's own checks must catch the byte: the order is parse, digest, key id, purpose and name, point, header MAC, chunk tags.</summary>
    private static string[] AllowedUnderResignedDigest(Part part) => part switch
    {
        Part.Magic or Part.KeyIdLength or Part.PurposeLength or Part.NameLength => [Malformed],
        Part.Version => [Unsupported],
        Part.SpkiLength => [Malformed, NotAuthenticated],                                                               // the length reframes the rest: the parse fails, or (the stray bytes read as the chunk size in range) completes over different bytes than the digest covers
        Part.KeyId => [Malformed, KeyMismatch],                                                                         // not a key id, or another well-formed one
        Part.Purpose or Part.Name => [Malformed, WrongFile],                                                            // not a valid purpose or name, or another valid one than the caller expects
        Part.Spki => [Malformed, AuthFailed],                                                                           // not a P-256 point, or another point: another shared secret, so the header MAC fails
        Part.ChunkSize => [Malformed, AuthFailed],                                                                      // out of range, or in range: the header MAC covers it
        Part.Salt or Part.NoncePrefix or Part.HeaderTag or Part.Chunk => [AuthFailed],                                  // header MAC, or the chunk tag (the nonce, the associated data and the key all derive from these)
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    [Fact]
    public async Task Every_altered_byte_of_the_header_with_every_flip_value_is_refused_by_the_signed_digest_and_by_the_format_itself_with_the_refusal_its_region_allows()
    {
        var plain = RandomNumberGenerator.GetBytes(Chunk + 7);                                                          // two chunks: a full one and a short last one
        var (file, digest) = await Encrypt(plain);
        var header = HeaderLength(file, plain.Length);
        var regions = HeaderRegions(file);
        Assert.Equal(header, regions[^1].Start + regions[^1].Length);                                                   // the derived layout covers exactly the header
        var findings = new List<string>();
        foreach (var region in regions)
            for (var pos = region.Start; pos < region.Start + region.Length; pos++)
                for (var flip = 1; flip <= 255; flip++)
                {
                    var copy = (byte[])file.Clone();
                    copy[pos] ^= (byte)flip;
                    var original = await Refused(copy, digest);
                    if (!AllowedUnderOriginalDigest(region.Part).Contains(original)) findings.Add($"signed digest of the original: {region.Part} at {pos} ^ 0x{flip:x2}: {original}");
                    var resigned = await Refused(copy, SelfDigest(copy, header));
                    if (!AllowedUnderResignedDigest(region.Part).Contains(resigned)) findings.Add($"digest recomputed over the tampered bytes: {region.Part} at {pos} ^ 0x{flip:x2}: {resigned}");
                }
        Assert.True(findings.Count == 0, $"{findings.Count} unexpected refusals (first 20):\n" + string.Join("\n", findings.Take(20)));
    }

    [Fact]
    public async Task Every_altered_byte_of_every_chunk_is_refused_by_the_signed_digest_and_by_the_chunk_tag_and_the_edges_with_every_flip_value()
    {
        var plain = RandomNumberGenerator.GetBytes(Chunk + 7);
        var (file, digest) = await Encrypt(plain);
        var header = HeaderLength(file, plain.Length);
        var edges = new HashSet<int> { header, header + Chunk - 1, header + Chunk, header + Stored - 1, header + Stored, file.Length - 1 };   // first, last ciphertext byte and first, last tag byte of both chunks
        var findings = new List<string>();
        for (var pos = header; pos < file.Length; pos++)
            foreach (var flip in edges.Contains(pos) ? Enumerable.Range(1, 255) : [0x01, 0x80, 0xff])
            {
                var copy = (byte[])file.Clone();
                copy[pos] ^= (byte)flip;
                var original = await Refused(copy, digest);
                if (original != NotAuthenticated) findings.Add($"signed digest of the original: chunk byte {pos - header} ^ 0x{flip:x2}: {original}");   // the chunk hash is compared before the chunk is decrypted
                var resigned = await Refused(copy, SelfDigest(copy, header));
                if (resigned != AuthFailed) findings.Add($"digest recomputed over the tampered bytes: chunk byte {pos - header} ^ 0x{flip:x2}: {resigned}");   // only the AEAD tag can refuse it now
            }
        Assert.True(findings.Count == 0, $"{findings.Count} unexpected refusals (first 20):\n" + string.Join("\n", findings.Take(20)));
    }

    [Fact]
    public async Task The_spki_length_flip_that_made_the_old_test_flaky_is_hbe_not_authenticated_when_the_stray_bytes_read_as_the_chunk_size_are_in_range()
    {
        // deterministic reproduction of the failure seen once in CI ("position 53: hbe_not_authenticated"): the high byte of the SPKI length (53 in this layout) flipped makes the parser read 256 bytes too many, into the first chunk;
        // the four bytes it then takes as the chunk size are random ciphertext, almost always out of range (hbe_malformed) and in range about once in 256 runs. Here they are made in range on purpose.
        var plain = RandomNumberGenerator.GetBytes(Chunk + 7);
        var (file, _) = await Encrypt(plain);
        var header = HeaderLength(file, plain.Length);
        var spkiLength = HeaderRegions(file).Single(r => r.Part == Part.SpkiLength);
        Assert.Equal(53, spkiLength.Start);
        var copy = (byte[])file.Clone();
        copy[spkiLength.Start] ^= 0x01;                                                                                  // the SPKI length 91 becomes 347
        var chunkSizeAt = spkiLength.Start + 2 + 347 + Hbe1.SaltLength + Hbe1.NoncePrefixLength;
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)Chunk)).CopyTo(copy, chunkSizeAt);   // a valid chunk size where the parser will look
        Assert.Equal(NotAuthenticated, await Refused(copy, SelfDigest(copy, header)));                                  // a refusal all the same: the header it parsed is not the one the digest covers
        copy[chunkSizeAt] ^= 0x01;                                                                                       // 0x01000400 is above the maximum chunk size: the parser's own refusal
        Assert.Equal(Malformed, await Refused(copy, SelfDigest(copy, header)));
    }

    [Fact]
    public async Task An_off_curve_ephemeral_point_is_refused_as_malformed_even_under_a_matching_signed_digest()
    {
        // review L2 of #128: the platform import rejects a point that is not on P-256; the refusal is hbe_malformed, before any key agreement
        var plain = RandomNumberGenerator.GetBytes(100);
        var (file, _) = await Encrypt(plain);
        var at = 4 + 1;
        at += 1 + file[at];                                                                   // key id
        at += 1 + file[at];                                                                   // purpose
        at += 2 + ((file[at] << 8) | file[at + 1]);                                           // name
        var spkiLength = (file[at] << 8) | file[at + 1];
        at += 2;
        Assert.Equal(91, spkiLength);                                                          // P-256 SubjectPublicKeyInfo with an uncompressed point
        var forged = (byte[])file.Clone();
        forged[at + spkiLength - 1] ^= 0x01;                                                   // the last byte of Y: the point leaves the curve
        Assert.Equal("hbe_malformed", await Refused(forged, SelfDigest(forged, HeaderLength(forged, plain.Length))));
    }

    [Fact]
    public async Task A_first_chunk_that_fails_authentication_releases_nothing()
    {
        // review L2 of #128: even under a digest that names the altered bytes (a compromised signer), the AEAD refuses the first chunk and the output stays EMPTY
        var plain = RandomNumberGenerator.GetBytes(3 * Chunk + 5);
        var (file, _) = await Encrypt(plain);
        var header = HeaderLength(file, plain.Length);
        var forged = (byte[])file.Clone();
        forged[header + 10] ^= 0x01;
        using var output = new MemoryStream();
        var ex = await Assert.ThrowsAsync<BackupException>(() => Hbe1.DecryptAsync(new MemoryStream(forged), output, Priv, "company-dump", "database.dump", SelfDigest(forged, header)));
        Assert.Equal("hbe_authentication_failed", ex.Code);
        Assert.Equal(0, output.Length);
        // and under the GENUINE digest the altered chunk is refused before decryption, also with nothing released
        using var output2 = new MemoryStream();
        Assert.Equal("hbe_not_authenticated", (await Assert.ThrowsAsync<BackupException>(() => Hbe1.DecryptAsync(new MemoryStream(forged), output2, Priv, "company-dump", "database.dump", SelfDigest(file, header)))).Code);
        Assert.Equal(0, output2.Length);
    }

    [Fact]
    public async Task Reordered_removed_truncated_and_appended_chunks_are_refused()
    {
        var plain = RandomNumberGenerator.GetBytes(3 * Chunk);                                                         // exactly three full chunks
        var (file, digest) = await Encrypt(plain);
        var h = HeaderLength(file, plain.Length);
        byte[] Chunks(params int[] order) => [.. file.Take(h), .. order.SelectMany(i => file.Skip(h + i * Stored).Take(Stored))];
        Assert.Equal(plain, await Decrypt(Chunks(0, 1, 2), digest));
        foreach (var tampered in new[] { Chunks(1, 0, 2), Chunks(0, 2, 1), Chunks(0, 1), Chunks(0, 2), Chunks(0, 1, 2, 2), [.. file, 0x00], file[..^1], file[..(h + Stored + 5)], file[..h] })
        {
            Assert.Equal("hbe_not_authenticated", await Refused(tampered, digest));                                   // authenticity: the signed chunk list
            Assert.Equal("hbe_authentication_failed", await Refused(tampered, SelfDigest(tampered, h)));               // integrity: index, last-chunk flag and tags
        }
        Assert.Equal("hbe_malformed", await Refused(file[..(h - 1)], digest));                                         // a cut header
        Assert.Equal("hbe_malformed", await Refused([], digest));
    }

    [Fact]
    public async Task A_file_is_bound_to_its_key_purpose_and_name_and_headers_cannot_be_swapped()
    {
        var plain = RandomNumberGenerator.GetBytes(Chunk + 10);
        var (file, digest) = await Encrypt(plain);
        Assert.Equal("hbe_wrong_file", await Refused(file, digest, "attachment"));
        Assert.Equal("hbe_wrong_file", await Refused(file, digest, "company-dump", "other.dump"));
        var otherPair = RecoveryKeys.Generate(Pass);
        using var otherPrivate = RecoveryPrivateKey.FromEncryptedPem(otherPair.EncryptedPrivateKeyPem, Pass);
        Assert.Equal("recovery_key_mismatch", await Refused(file, digest, key: otherPrivate));

        // the header of one file with the body of another (same key and binding): the chunks are bound to their own header
        var (second, _) = await Encrypt(RandomNumberGenerator.GetBytes(Chunk + 10));
        var h = HeaderLength(file, plain.Length);
        byte[] swapped = [.. file.Take(h), .. second.Skip(h)];
        Assert.Equal("hbe_not_authenticated", await Refused(swapped, digest));
        Assert.Equal("hbe_authentication_failed", await Refused(swapped, SelfDigest(swapped, h)));

        // a name rewritten in the header (length kept) fails the header MAC
        var (renamed, _) = await Encrypt(plain, name: "database.dumq");
        var forged = (byte[])renamed.Clone();
        forged[System.Text.Encoding.ASCII.GetString(forged).IndexOf("database.dumq", StringComparison.Ordinal) + 12] = (byte)'p';
        Assert.Equal("hbe_authentication_failed", await Refused(forged, SelfDigest(forged, h)));
    }

    [Fact]
    public async Task Invalid_bindings_chunk_sizes_and_versions_are_refused()
    {
        Assert.Equal("hbe_binding_invalid", (await Assert.ThrowsAsync<BackupException>(() => Encrypt([1], purpose: "Company Dump"))).Code);
        Assert.Equal("hbe_binding_invalid", (await Assert.ThrowsAsync<BackupException>(() => Encrypt([1], name: "../escape"))).Code);
        Assert.Equal("hbe_binding_invalid", (await Assert.ThrowsAsync<BackupException>(() => Encrypt([1], name: "/absolute"))).Code);
        Assert.Equal("hbe_binding_invalid", (await Assert.ThrowsAsync<BackupException>(() => Encrypt([1], name: "a/./b"))).Code);
        Assert.Equal("hbe_chunk_size_invalid", (await Assert.ThrowsAsync<BackupException>(() => Encrypt([1], chunk: 512))).Code);
        Assert.Equal("hbe_chunk_size_invalid", (await Assert.ThrowsAsync<BackupException>(() => Encrypt([1], chunk: Hbe1.MaxChunkSize + 1))).Code);
        var (file, digest) = await Encrypt([1, 2, 3]);
        var v2 = (byte[])file.Clone(); v2[4] = 2;
        Assert.Equal("hbe_unsupported_version", await Refused(v2, digest));
        var notHbe = (byte[])file.Clone(); notHbe[0] = (byte)'X';
        Assert.Equal("hbe_malformed", await Refused(notHbe, digest));
        Assert.True(Hbe1.IsName("companies/0123/attachments/x.att"));
        Assert.False(Hbe1.IsName("a//b"));
    }
}
