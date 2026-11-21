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

    // Three digests are used below, and each has its own table of allowed refusals:
    //   (1) ORIGINAL: the digest signed for the untampered file (the normal case: the signer is honest, the file was altered afterwards);
    //   (2) SELF: SelfDigest, recomputed over the tampered bytes but split at the ORIGINAL header length. It equals what a compromised signer would sign for every flip that leaves the framing alone;
    //       for a flip of a length field it is NOT what any signer would produce (the parser reads a different header), so a refusal by the digest check there is an artefact of this helper (review L1 of #183);
    //   (3) PARSER-FRAMED: Hbe1.ComputeDigestAsync over the tampered bytes, split exactly as the parser reads them. This is what a compromised signer would really sign, so under it the digest check
    //       always passes and only the format's own checks can refuse: hbe_not_authenticated is never allowed under it.

    /// <summary>Refusals that are legitimate for a byte of this part when the digest is the one signed for the ORIGINAL file: the header is parsed first (a field the parser rejects, <c>hbe_malformed</c> or <c>hbe_unsupported_version</c>), then compared with the signed digest, so a header that parses but differs is <c>hbe_not_authenticated</c>; nothing after the digest check can be reached.</summary>
    private static string[] AllowedUnderOriginalDigest(Part part) => part switch
    {
        Part.Magic or Part.KeyIdLength or Part.PurposeLength or Part.NameLength => [Malformed],                          // the magic is checked first; a changed length makes the id, purpose or name unparseable
        Part.Version => [Unsupported],                                                                                  // any other version number
        Part.KeyId or Part.Purpose or Part.Name or Part.SpkiLength or Part.ChunkSize => [Malformed, NotAuthenticated],    // still valid text or a range the parser accepts: digest mismatch; invalid: refused by the parser
        Part.Spki or Part.Salt or Part.NoncePrefix or Part.HeaderTag or Part.Chunk => [NotAuthenticated],                // content the parser does not validate: only the digest can refuse it (a chunk: its hash is compared before it is decrypted)
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>Refusals that are legitimate under <see cref="SelfDigest"/> (recomputed over the tampered bytes, split at the ORIGINAL header length), so the format's own checks must catch the byte: the order is parse, digest, key id, purpose and name, point, header MAC, chunk tags.</summary>
    private static string[] AllowedUnderSelfDigest(Part part) => part switch
    {
        Part.Magic or Part.KeyIdLength or Part.PurposeLength or Part.NameLength => [Malformed],
        Part.Version => [Unsupported],
        Part.SpkiLength => [Malformed, NotAuthenticated],                                                               // the length reframes the rest: the parse fails, or (the stray bytes read as the chunk size in range) completes over different bytes than THIS HELPER hashed; hbe_not_authenticated here is the helper's artefact, see the parser-framed table
        Part.KeyId => [Malformed, KeyMismatch],                                                                         // not a key id, or another well-formed one
        Part.Purpose or Part.Name => [Malformed, WrongFile],                                                            // not a valid purpose or name, or another valid one than the caller expects
        // SPKI (review L2 of #183). hbe_malformed: the bytes are no longer a P-256 SubjectPublicKeyInfo the platform imports (another algorithm or curve, a broken encoding, a point off the curve).
        // hbe_authentication_failed: the import still ACCEPTS the bytes. The case that is actually reached is offset 25, the unused-bits byte of the BIT STRING: a value of 1 to 7 is accepted by the
        // Linux import when Y ends in that many zero bits, and it is then the SAME point and the SAME shared secret. The file is refused all the same because the SPKI BYTES, not the point, are
        // bound three times: their hash is part of the HKDF info (other file and header keys), the header MAC is computed over the header bytes that contain them, and every chunk's associated
        // data carries the hash of that header. The first of these to be checked is the header MAC. (A flip that gave another valid point would end the same way, through another shared secret; a
        // single byte flip of X or Y practically never lands on the curve.) Pinned deterministically by The_same_ephemeral_point_re_encoded_... below.
        Part.Spki => [Malformed, AuthFailed],
        Part.ChunkSize => [Malformed, AuthFailed],                                                                      // out of range, or in range: the header MAC covers it
        Part.Salt or Part.NoncePrefix or Part.HeaderTag or Part.Chunk => [AuthFailed],                                  // header MAC, or the chunk tag (the nonce, the associated data and the key all derive from these)
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>Refusals that are legitimate under the PARSER-FRAMED digest (<see cref="Hbe1.ComputeDigestAsync"/> over the tampered bytes: what a compromised signer would sign). The digest check passes by construction, so <c>hbe_not_authenticated</c> is never allowed; the same codes apply when the parser refuses the bytes and no such digest exists at all.</summary>
    private static string[] AllowedUnderParserFramedDigest(Part part) => part switch
    {
        Part.SpkiLength => [Malformed],                                                                                 // the parser refuses the reframed header, or it completes and the SPKI is then cut short or has trailing bytes: refused at the import, before any key agreement
        _ => AllowedUnderSelfDigest(part),                                                                              // no other single-byte flip changes the framing AND still parses, so the two digests coincide (asserted in the header test)
    };

    /// <summary>One decryption attempt: the refusal code (null when the file was ACCEPTED) and whatever reached the plaintext output before the refusal.</summary>
    private static async Task<(string? Code, byte[] Output)> Attempt(byte[] file, Hbe1Digest digest)
    {
        using var output = new MemoryStream();
        try { await Hbe1.DecryptAsync(new MemoryStream(file), output, Priv, "company-dump", "database.dump", digest); return (null, output.ToArray()); }
        catch (BackupException ex) { return (ex.Code, output.ToArray()); }
    }

    /// <summary>The digest of the bytes as the parser frames them, or the parser's refusal when it cannot read them.</summary>
    private static async Task<(Hbe1Digest? Digest, string? Refusal)> ParserFramedDigest(byte[] file)
    {
        try { return (await Hbe1.ComputeDigestAsync(new MemoryStream(file)), null); }
        catch (BackupException ex) { return (null, ex.Code); }
    }

    private static bool Same(Hbe1Digest a, Hbe1Digest b) => a.HeaderSha256 == b.HeaderSha256 && a.ChunkSha256.SequenceEqual(b.ChunkSha256);

    [Fact]
    public async Task Every_altered_byte_of_the_header_with_every_flip_value_is_refused_by_the_signed_digest_and_by_the_format_itself_with_the_refusal_its_region_allows()
    {
        var plain = RandomNumberGenerator.GetBytes(Chunk + 7);                                                          // two chunks: a full one and a short last one
        var (file, digest) = await Encrypt(plain);
        var header = HeaderLength(file, plain.Length);
        var regions = HeaderRegions(file);
        Assert.Equal(header, regions[^1].Start + regions[^1].Length);                                                   // the derived layout covers exactly the header
        var findings = new List<string>();
        void Check(string digestName, string[] allowed, Region region, int pos, int flip, string? code, int released)
        {
            if (code is null) findings.Add($"ACCEPTED under {digestName}: {region.Part} at {pos} ^ 0x{flip:x2}");
            else if (!allowed.Contains(code)) findings.Add($"{digestName}: {region.Part} at {pos} ^ 0x{flip:x2}: {code}");
            if (released != 0) findings.Add($"PLAINTEXT RELEASED under {digestName} ({released} bytes): {region.Part} at {pos} ^ 0x{flip:x2}");   // review L6 of #183: a refused header releases nothing
        }
        foreach (var region in regions)
            for (var pos = region.Start; pos < region.Start + region.Length; pos++)
                for (var flip = 1; flip <= 255; flip++)
                {
                    var copy = (byte[])file.Clone();
                    copy[pos] ^= (byte)flip;
                    var original = await Attempt(copy, digest);
                    Check("signed digest of the original", AllowedUnderOriginalDigest(region.Part), region, pos, flip, original.Code, original.Output.Length);
                    var self = SelfDigest(copy, header);
                    var resigned = await Attempt(copy, self);
                    Check("digest recomputed at the original header length", AllowedUnderSelfDigest(region.Part), region, pos, flip, resigned.Code, resigned.Output.Length);
                    // review L1 of #183: the digest a compromised signer would really sign, framed as the parser reads the tampered bytes
                    var (framed, parserRefusal) = await ParserFramedDigest(copy);
                    if (framed is null) Check("no parser-framed digest exists (the parser refuses the bytes)", AllowedUnderParserFramedDigest(region.Part).Intersect([Malformed, Unsupported]).ToArray(), region, pos, flip, parserRefusal, 0);
                    else if (Same(framed, self)) Check("parser-framed digest (equal to the one above)", AllowedUnderParserFramedDigest(region.Part), region, pos, flip, resigned.Code, resigned.Output.Length);
                    else
                    {
                        if (region.Part is not (Part.SpkiLength or Part.ChunkSize)) findings.Add($"unexpected reframing: {region.Part} at {pos} ^ 0x{flip:x2} parses with another framing");   // only these two fields can change the framing and still parse
                        var reframed = await Attempt(copy, framed);
                        Check("parser-framed digest", AllowedUnderParserFramedDigest(region.Part), region, pos, flip, reframed.Code, reframed.Output.Length);
                    }
                }
        Assert.True(findings.Count == 0, $"{findings.Count} unexpected results (first 20):\n" + string.Join("\n", findings.Take(20)));
    }

    [Fact]
    public async Task Every_altered_byte_of_the_first_a_middle_and_the_last_chunk_is_refused_by_the_signed_digest_and_by_the_chunk_tag_and_only_the_authentic_chunks_before_it_are_released()
    {
        // review L5 of #183: THREE chunks at the smallest chunk size the format permits (Hbe1.MinChunkSize): a full first one, a full MIDDLE one and a short last one, so bytes of a chunk that is
        // neither first nor last are flipped too (its ciphertext, its tag and both of its boundaries).
        Assert.Equal(Hbe1.MinChunkSize, Chunk);
        var plain = RandomNumberGenerator.GetBytes(2 * Chunk + 7);
        var (file, digest) = await Encrypt(plain);
        Assert.Equal(3, digest.ChunkSha256.Count);
        var header = HeaderLength(file, plain.Length);
        Assert.Equal(header + 2 * Stored + 7 + Hbe1.TagLength, file.Length);
        var edges = new HashSet<int>();                                                                                 // first and last ciphertext byte, first and last tag byte of each of the three chunks
        for (var index = 0; index < 3; index++)
        {
            var start = header + index * Stored; var end = Math.Min(start + Stored, file.Length);
            edges.UnionWith([start, end - Hbe1.TagLength - 1, end - Hbe1.TagLength, end - 1]);
        }
        Assert.Equal(12, edges.Count);
        var originalAllowed = AllowedUnderOriginalDigest(Part.Chunk); var selfAllowed = AllowedUnderSelfDigest(Part.Chunk);   // exactly one code each: the signed chunk hash, then the AEAD tag
        Assert.Equal([NotAuthenticated], originalAllowed); Assert.Equal([AuthFailed], selfAllowed); Assert.Equal([AuthFailed], AllowedUnderParserFramedDigest(Part.Chunk));
        var findings = new List<string>(); var perChunk = new int[3];
        for (var pos = header; pos < file.Length; pos++)
        {
            var index = (pos - header) / Stored;
            // review L6 of #183, the documented streaming behaviour of Hbe1.DecryptAsync ("output written before it must be discarded by the caller"): the chunks BEFORE the altered one are authentic and
            // were already written; the altered chunk and everything after it never are. So: nothing for the first chunk, exactly chunk 0 for the middle chunk, exactly chunks 0 and 1 for the last.
            var released = plain.AsSpan(0, index * Chunk).ToArray();
            foreach (var flip in edges.Contains(pos) ? Enumerable.Range(1, 255) : [0x01, 0x80, 0xff])
            {
                perChunk[index]++;
                var copy = (byte[])file.Clone();
                copy[pos] ^= (byte)flip;
                var self = SelfDigest(copy, header);
                foreach (var (name, allowed, attempt) in new[] { ("signed digest of the original", originalAllowed, await Attempt(copy, digest)), ("digest recomputed over the tampered bytes", selfAllowed, await Attempt(copy, self)) })
                {
                    if (attempt.Code is null) findings.Add($"ACCEPTED under {name}: chunk {index} byte {pos - header - index * Stored} ^ 0x{flip:x2}");
                    else if (!allowed.Contains(attempt.Code)) findings.Add($"{name}: chunk {index} byte {pos - header - index * Stored} ^ 0x{flip:x2}: {attempt.Code}");
                    if (!attempt.Output.AsSpan().SequenceEqual(released)) findings.Add($"OUTPUT under {name}: chunk {index} byte {pos - header - index * Stored} ^ 0x{flip:x2}: {attempt.Output.Length} bytes written, expected exactly the {released.Length} authentic bytes before the chunk");
                }
            }
        }
        // a chunk flip never changes the framing, so the parser-framed digest is the recomputed one (checked on the boundaries of the middle chunk)
        foreach (var pos in new[] { header + Stored, header + 2 * Stored - 1 })
        {
            var copy = (byte[])file.Clone(); copy[pos] ^= 0x01;
            var (framed, _) = await ParserFramedDigest(copy);
            Assert.NotNull(framed); Assert.True(Same(framed, SelfDigest(copy, header)));
        }
        Assert.All(perChunk, n => Assert.True(n >= 3 * (7 + Hbe1.TagLength)));                                            // every chunk, the middle one included, had its bytes flipped
        Assert.True(perChunk[1] == 3 * (Stored - 4) + 4 * 255, $"middle chunk flips: {perChunk[1]}");
        Assert.True(findings.Count == 0, $"{findings.Count} unexpected results (first 20):\n" + string.Join("\n", findings.Take(20)));
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
        var underSelf = await Attempt(copy, SelfDigest(copy, header));
        Assert.Equal(NotAuthenticated, underSelf.Code);                                                                  // a refusal all the same: the header it parsed is not the one SelfDigest hashed (the helper splits at the ORIGINAL header length)
        Assert.Empty(underSelf.Output);
        // review L1 of #183: under the digest framed as the parser reads the bytes (what a compromised signer would sign) the digest check passes and the FORMAT refuses: 347 bytes are not one
        // SubjectPublicKeyInfo (trailing bytes after the 91 of the key), refused as malformed at the import, before any key agreement and with nothing released
        var (framed, parserRefusal) = await ParserFramedDigest(copy);
        Assert.Null(parserRefusal); Assert.NotNull(framed);
        Assert.False(Same(framed, SelfDigest(copy, header)));
        Assert.Equal(Chunk, (await Hbe1.ReadHeaderAsync(new MemoryStream(copy))).ChunkSize);                              // the reframed header parses, with the planted chunk size
        var underFramed = await Attempt(copy, framed);
        Assert.Equal(Malformed, underFramed.Code);
        Assert.Empty(underFramed.Output);
        copy[chunkSizeAt] ^= 0x01;                                                                                       // 0x01000400 is above the maximum chunk size: the parser's own refusal
        Assert.Equal(Malformed, await Refused(copy, SelfDigest(copy, header)));
        Assert.Equal((null, Malformed), await ParserFramedDigest(copy));                                                 // and no parser-framed digest exists for it
    }

    [Fact]
    public async Task The_same_ephemeral_point_re_encoded_in_the_spki_is_refused_as_an_authentication_failure_because_the_spki_bytes_are_bound_and_nothing_is_released()
    {
        // review L2 of #183. SPKI offset 25 is the unused-bits byte of the BIT STRING that holds the point (30 59 | 30 13 06 07 .. 06 08 .. | 03 42 | 00 | 04 X Y). Declaring 1 unused bit when the last
        // bit of Y is 0 is another encoding of the SAME point, which the Linux import accepts. The shared secret is then unchanged, so the point check cannot refuse the file; what refuses it is that the
        // SPKI BYTES are bound: their SHA-256 is in the HKDF info, the header MAC covers them, and each chunk's associated data carries the header hash. Measured by mutating Hbe1.cs locally: this
        // test still passes with any one or two of the three bindings removed, and fails (the file is ACCEPTED and its plaintext released) only with all three removed, so it pins that at least one
        // holds. Deterministic: a file whose ephemeral Y is even is searched for (one in two; 200 tries cannot all fail).
        var plain = RandomNumberGenerator.GetBytes(2 * Chunk + 7);
        byte[]? file = null; Region? spki = null;
        for (var attempt = 0; attempt < 200 && file is null; attempt++)
        {
            var (candidate, _) = await Encrypt(plain);
            var region = HeaderRegions(candidate).Single(r => r.Part == Part.Spki);
            if ((candidate[region.Start + region.Length - 1] & 0x01) == 0) { file = candidate; spki = region; }
        }
        Assert.NotNull(file); Assert.NotNull(spki);
        var header = HeaderLength(file, plain.Length);
        Assert.Equal(91, spki.Length);
        Assert.Equal([0x03, 0x42, 0x00, 0x04], file.AsSpan(spki.Start + 23, 4).ToArray());                                // BIT STRING, 66 bytes, 0 unused bits, uncompressed point
        var genuine = file.AsSpan(spki.Start, spki.Length).ToArray();
        using var original = ECDiffieHellman.Create(); original.ImportSubjectPublicKeyInfo(genuine, out _);
        var point = original.ExportParameters(false).Q;

        var copy = (byte[])file.Clone();
        copy[spki.Start + 25] ^= 0x01;                                                                                   // 1 unused bit declared; every other byte, the point included, is unchanged
        var reencoded = copy.AsSpan(spki.Start, spki.Length).ToArray();
        // what the platform import makes of the re-encoded key, established independently of Hbe1
        bool accepted;
        using (var probe = ECDiffieHellman.Create())
        {
            try { probe.ImportSubjectPublicKeyInfo(reencoded, out var read); accepted = read == reencoded.Length; }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException) { accepted = false; }
            if (accepted)
            {
                var q = probe.ExportParameters(false).Q;
                Assert.Equal(point.X, q.X); Assert.Equal(point.Y, q.Y);                                                   // the SAME point: the shared secret is the one of the genuine file
            }
        }
        var self = SelfDigest(copy, header);
        var (framed, _) = await ParserFramedDigest(copy);
        Assert.NotNull(framed); Assert.True(Same(framed, self));                                                         // no reframing: this IS what a compromised signer would sign
        var resigned = await Attempt(copy, framed);
        Assert.NotNull(resigned.Code);                                                                                   // never accepted, whatever the platform import does
        Assert.Equal(accepted ? AuthFailed : Malformed, resigned.Code);                                                  // accepted by the import: an authentication failure (header MAC first), although the point is genuine; not accepted: malformed
        Assert.Empty(resigned.Output);                                                                                   // and no plaintext, although the first chunk is intact and the shared secret is the genuine one
        var signed = await Attempt(copy, SelfDigest(file, header));
        Assert.Equal(NotAuthenticated, signed.Code);                                                                     // under the digest signed for the genuine file it does not get that far
        Assert.Empty(signed.Output);
        Assert.Equal(plain, await Decrypt(file, SelfDigest(file, header)));                                              // the untouched file still decrypts
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
