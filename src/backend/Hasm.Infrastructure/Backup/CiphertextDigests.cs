using System.Security.Cryptography;
using System.Text.Json;

namespace Hasm.Infrastructure.Backup;

/// <summary>One encrypted file of a run: its slash-separated relative name, its size and the SHA-256 of its ciphertext.</summary>
public sealed record CiphertextDigestEntry(string Name, long Size, string Sha256);

/// <summary>
/// The signed cleartext inventory of every encrypted file of a run (slice 8, S8b; D-185 as amended): written at run time after the encrypted files are final, signed with the run's signing key (never the
/// recovery key) and bound to the run by its run id and by the hash of this file being recorded in the SIGNED run status. A copy is verified against it without the recovery key.
/// </summary>
public sealed record CiphertextDigestList(int Version, string Kind, string RunId, IReadOnlyList<CiphertextDigestEntry> Files);

/// <summary>
/// Writing and reading of <c>ciphertext.digests.json</c> (+ <c>.sig</c>). Codes: <c>replica_digest_list_missing</c> (the status names no list, or the file or its signature is absent: a run written before
/// this inventory existed), <c>replica_digest_list_invalid</c> (the signature does not verify, the file is not the one the signed status names, another run's list, or malformed).
/// </summary>
public static class CiphertextDigests
{
    public const string FileName = "ciphertext.digests.json";
    public const string KindValue = "ciphertext-digests";
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The slash-separated names of every encrypted (<c>.hbe</c>) file under a run folder, ordinal order.</summary>
    internal static List<string> EncryptedNames(string runDirectory)
    {
        var names = new List<string>();
        foreach (var f in new DirectoryInfo(runDirectory).EnumerateFiles("*.hbe", SearchOption.AllDirectories))
            names.Add(Path.GetRelativePath(runDirectory, f.FullName).Replace(Path.DirectorySeparatorChar, '/'));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Hashes every encrypted file of the run, signs the inventory and returns the SHA-256 of the signed file, which the run status records.</summary>
    public static async Task<string> WriteAsync(string runDirectory, string runId, BackupSigner signer, CancellationToken ct)
    {
        var entries = new List<CiphertextDigestEntry>();
        foreach (var name in EncryptedNames(runDirectory))
        {
            var path = Path.Combine(runDirectory, name.Replace('/', Path.DirectorySeparatorChar));
            await using var s = File.OpenRead(path);
            entries.Add(new(name, s.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct))));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CiphertextDigestList(CurrentVersion, KindValue, runId, entries), Json);
        SignedDocuments.WriteSigned(Path.Combine(runDirectory, FileName), bytes, signer);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Reads the inventory of a run: it must exist with a signature that verifies against <paramref name="trust"/>, be the exact file the SIGNED status names (<paramref name="boundSha256"/>), and name
    /// this run; a list copied from another run fails the hash binding and the run id.
    /// </summary>
    public static CiphertextDigestList ReadVerified(string runDirectory, string runId, string? boundSha256, SigningTrust trust)
    {
        if (boundSha256 is null) throw new BackupException("replica_digest_list_missing");
        var path = Path.Combine(runDirectory, FileName);
        if (!File.Exists(path) || !File.Exists(path + SignedDocuments.SignatureSuffix)) throw new BackupException("replica_digest_list_missing");
        byte[] content;
        try { (content, _) = SignedDocuments.ReadVerified(path, trust); }
        catch (BackupException) { throw new BackupException("replica_digest_list_invalid"); }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), boundSha256, StringComparison.Ordinal)) throw new BackupException("replica_digest_list_invalid");
        CiphertextDigestList? list;
        try { list = JsonSerializer.Deserialize<CiphertextDigestList>(content, Json); }
        catch (JsonException) { throw new BackupException("replica_digest_list_invalid"); }
        if (list is not { Version: CurrentVersion, Kind: KindValue, Files: not null } || list.RunId != runId
            || list.Files.Any(f => f is null || string.IsNullOrEmpty(f.Name) || f.Size < 0 || f.Sha256 is not { Length: 64 } || !f.Sha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            || list.Files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != list.Files.Count)
            throw new BackupException("replica_digest_list_invalid");
        return list;
    }
}
