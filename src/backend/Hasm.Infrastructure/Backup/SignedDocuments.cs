using System.Text.Json;

namespace Hasm.Infrastructure.Backup;

/// <summary>One encrypted file of a set as the signed manifest names it: purpose, set-relative name, stored size, and the HBE1 header hash and chunk-hash list (D-116).</summary>
public sealed record EncryptedFileEntry(string Purpose, string Name, long Size, string HeaderSha256, IReadOnlyList<string> ChunkSha256)
{
    [System.Text.Json.Serialization.JsonIgnore] public Hbe1Digest Digest => new(HeaderSha256, ChunkSha256);
}

/// <summary>Company-set details of a version-2 manifest (the slice 3 facts, now inside the encrypted, signed manifest).</summary>
public sealed record CompanyDetails(DatabaseLocale? DatabaseLocale, int? CompanyMigrationVersion, IReadOnlyList<string> KeyIdsRequired, int SkippedStrayFiles, int NonEnvelopeFilesWithMagic);

/// <summary>System-set details of a version-2 manifest.</summary>
public sealed record SystemDetails(DatabaseLocale? DatabaseLocale, int? SystemMigrationVersion, IReadOnlyList<string> Excluded);

/// <summary>
/// The version-2 manifest of an ENCRYPTED set (D-092, review M1 of #116). It is itself stored encrypted (<c>manifest.json.hbe</c>) and signed over its ciphertext (review L-b), and it names: kind, run id,
/// company id, creation time, format version, recovery and signing key ids, and every file with its purpose, name, size and digest.
/// </summary>
public sealed record ManifestV2(int Version, string Kind, string? RunId, Guid? CompanyId, DateTimeOffset CreatedUtc, string RecoveryKeyId, string SigningKeyId,
    IReadOnlyList<EncryptedFileEntry> Files, CompanyDetails? Company = null, SystemDetails? System = null, BackupRunReport? Run = null)
{
    public const int CurrentVersion = 2;
}

/// <summary>
/// The readable, SIGNED status of an encrypted run (<c>run.status.json</c> + <c>.sig</c>), the only document <c>backup-status</c> reads (review M1): verdict, times, counts, key ids, the excluded data,
/// warnings and the hash of the run's canary (D-117). No names, no paths.
/// </summary>
public sealed record RunStatus(int Version, string Kind, string RunId, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, long DurationMs, string Verdict,
    int CompaniesOk, int CompaniesMissingKeys, int CompaniesBusy, int CompaniesFailed, int Skipped, string RecoveryKeyId, string SigningKeyId, string CanarySha256,
    IReadOnlyList<string> Excluded, IReadOnlyList<string> Warnings, bool Encrypted, IReadOnlyList<RunStatusCompany> Companies)
{
    public const int CurrentVersion = 1;
    public const string KindValue = "run-status";
}

/// <summary>A company of a run as the signed status names it (id and outcome only), for the missed-runs warning.</summary>
public sealed record RunStatusCompany(Guid CompanyId, string Outcome);

/// <summary>Writing and reading of signed documents and encrypted manifests. Every reader verifies the signature against the operator's trust list BEFORE it parses anything (review M1, L-b).</summary>
public static class SignedDocuments
{
    public const string SignatureSuffix = ".sig";
    public const string ManifestFileName = "manifest.json.hbe";
    public const string ManifestPurpose = "manifest";
    public const string ManifestInnerName = "manifest.json";
    public const string RunStatusFileName = "run.status.json";
    /// <summary>Upper bound for a decrypted or signed document held in memory (review L-b): about one million chunk hashes.</summary>
    public const int MaxDocumentBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Writes <paramref name="content"/> owner-only (never overwrites) and its detached signature beside it.</summary>
    public static void WriteSigned(string path, byte[] content, BackupSigner signer)
    {
        BackupRunFiles.WriteOwnerOnlyAtomically(path, content);
        BackupRunFiles.WriteOwnerOnlyAtomically(path + SignatureSuffix, signer.Sign(content).ToBytes());
    }

    /// <summary>Reads a signed file and verifies it; <c>signature_missing</c> when there is no valid <c>.sig</c>, <c>document_too_large</c> above the bound.</summary>
    public static (byte[] Content, TrustedSigningKey Key) ReadVerified(string path, SigningTrust trust)
    {
        if (!File.Exists(path)) throw new BackupException("document_missing");
        if (new FileInfo(path).Length > MaxDocumentBytes) throw new BackupException("document_too_large");
        var content = File.ReadAllBytes(path);
        var sigPath = path + SignatureSuffix;
        var signature = File.Exists(sigPath) ? BackupSignature.Parse(File.ReadAllBytes(sigPath)) : null;
        return (content, trust.Verify(content, signature));
    }

    // ---- encrypted manifest ----------------------------------------------------------------------

    /// <summary>Serializes, encrypts (HBE1, purpose <c>manifest</c>) and signs the ciphertext of <paramref name="manifest"/> into <paramref name="directory"/>.</summary>
    public static async Task WriteManifestAsync(string directory, ManifestV2 manifest, RecoveryPublicKey recovery, BackupSigner signer, CancellationToken ct = default)
    {
        using var cipher = new MemoryStream();
        await Hbe1.EncryptAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)), cipher, recovery, ManifestPurpose, ManifestInnerName, ct);
        WriteSigned(Path.Combine(directory, ManifestFileName), cipher.ToArray(), signer);
    }

    /// <summary>
    /// Verifies the signature over the manifest's CIPHERTEXT, then decrypts it in memory (size-bounded) with the digest of those authenticated bytes, parses it, and checks its structure, its key ids
    /// and the retirement rule of its signing key against its signed creation time (review L-d).
    /// </summary>
    public static async Task<ManifestV2> ReadManifestAsync(string directory, SigningTrust trust, RecoveryPrivateKey recovery, string expectedKind, CancellationToken ct = default)
    {
        var (cipher, key) = ReadVerified(Path.Combine(directory, ManifestFileName), trust);
        var digest = await Hbe1.ComputeDigestAsync(new MemoryStream(cipher), ct);       // the bytes are authenticated by the signature
        using var plain = new BoundedMemoryStream(MaxDocumentBytes);
        await Hbe1.DecryptAsync(new MemoryStream(cipher), plain, recovery, ManifestPurpose, ManifestInnerName, digest, ct);
        ManifestV2? m;
        try { m = JsonSerializer.Deserialize<ManifestV2>(plain.ToArray(), Json); }
        catch (JsonException) { throw new BackupException("manifest_invalid"); }
        if (m is null || m.Version != ManifestV2.CurrentVersion || m.Kind != expectedKind || m.Files is null || m.RecoveryKeyId != recovery.KeyId || m.SigningKeyId != key.KeyId
            || m.Files.Any(f => f is null || !Hbe1.IsPurpose(f.Purpose) || !Hbe1.IsName(f.Name) || f.Size < 0 || f.ChunkSha256 is null || f.ChunkSha256.Count == 0)
            || m.Files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != m.Files.Count)
            throw new BackupException("manifest_invalid");
        SigningTrust.EnsureValidAt(key, m.CreatedUtc);
        return m;
    }

    // ---- run status ------------------------------------------------------------------------------

    public static void WriteRunStatus(string runDirectory, RunStatus status, BackupSigner signer) =>
        WriteSigned(Path.Combine(runDirectory, RunStatusFileName), JsonSerializer.SerializeToUtf8Bytes(status, Json), signer);

    /// <summary>The verified status of a run; <c>status_unsigned</c> when the signature is missing or does not verify (backup-status then treats the run as FAILED: review M1).</summary>
    public static RunStatus ReadRunStatus(string runDirectory, SigningTrust trust)
    {
        byte[] content; TrustedSigningKey key;
        try { (content, key) = ReadVerified(Path.Combine(runDirectory, RunStatusFileName), trust); }
        catch (BackupException ex) when (ex.Code is "signature_missing" or "signature_invalid" or "signing_key_unknown" or "signing_key_compromised") { throw new BackupException("status_unsigned", ex.Code); }
        RunStatus? s;
        try { s = JsonSerializer.Deserialize<RunStatus>(content, Json); }
        catch (JsonException) { throw new BackupException("status_invalid"); }
        if (s is null || s.Version != RunStatus.CurrentVersion || s.Kind != RunStatus.KindValue || s.RunId != Path.GetFileName(Path.TrimEndingDirectorySeparator(runDirectory))
            || !BackupRunFiles.Verdicts.Contains(s.Verdict) || s.SigningKeyId != key.KeyId || !RecoveryKeys.IsKeyId(s.RecoveryKeyId) || s.CanarySha256 is not { Length: 64 }
            || s.Excluded is null || s.Warnings is null || !s.Encrypted || s.Companies is null
            || s.Companies.Any(c => c is null || c.CompanyId == Guid.Empty || !BackupRunFiles.CompanyOutcomes.Contains(c.Outcome)) || s.Warnings.Any(w => !BackupRunFiles.IsCode(w)) || s.Excluded.Any(e => !BackupRunFiles.IsCode(e)))
            throw new BackupException("status_invalid");
        SigningTrust.EnsureValidAt(key, s.StartedUtc);
        return s;
    }
}

/// <summary>A memory stream that refuses to grow beyond a bound (a decrypted document is never allowed to exhaust memory).</summary>
internal sealed class BoundedMemoryStream(int limit) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
    private void Check(int count) { if (Length + count > limit) throw new BackupException("document_too_large"); }
}
