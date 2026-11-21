using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 5 (D-090, D-091, D-092, D-116): an encrypted company set without a database (the dump is a stream from a fake producer): no plaintext at rest, a signed encrypted manifest, keyless
/// verification of every stored file, authenticated decryption, and refusal of altered, missing or unlisted files.
/// </summary>
public sealed class EncryptedSetsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-encset-" + Guid.NewGuid().ToString("N"));
    private const string Pass = "a recovery passphrase of twenty+";
    private readonly RecoveryPublicKey pub;
    private readonly RecoveryPrivateKey priv;
    private readonly BackupSigner signer;
    private readonly SigningTrust trust;

    public EncryptedSetsTests()
    {
        Directory.CreateDirectory(root);
        var pair = RecoveryKeys.Generate(Pass);
        pub = RecoveryPublicKey.FromPem(pair.PublicKeyPem);
        priv = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
        var (id, p) = BackupSigningKeys.Generate(Path.Combine(root, "sign"));
        signer = BackupSigningKeys.Load(Path.Combine(root, "sign"), id, true);
        trust = new SigningTrust([new TrustedSigningKey(id, p, SigningKeyStatus.Active)]);
    }

    public void Dispose() { signer.Dispose(); pub.Dispose(); priv.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private static string Name() => Guid.NewGuid().ToString("N") + ".att";

    private async Task<(string Set, byte[] Dump, Dictionary<string, byte[]> Attachments, ManifestV2 Manifest)> CreateAsync()
    {
        var company = Guid.NewGuid();
        var att = Path.Combine(root, "att"); Directory.CreateDirectory(att);
        var files = new Dictionary<string, byte[]> { [Name()] = Encoding.ASCII.GetBytes("%PDF-1.4 plain"), [Name()] = new EnvelopeEncryptor(new InMemoryKekRing("k1")).Encrypt("secret"u8.ToArray(), "ctx") };
        foreach (var (n, b) in files) File.WriteAllBytes(Path.Combine(att, n), b);
        File.WriteAllText(Path.Combine(att, "upload.tmp"), "stray");
        var dump = RandomNumberGenerator.GetBytes(Hbe1.DefaultChunkSize + 777);
        var set = Path.Combine(root, "set");
        var locale = new DatabaseLocale("UTF8", "C", "C", "c");
        var m = await EncryptedSets.CreateCompanyAsync(company, "20261121T020000Z-0011aabb", async consume => { await consume(new MemoryStream(dump), default); return locale; }, att, set,
            DateTimeOffset.UtcNow, 31, new BackupMaterial(pub, signer), default);
        return (set, dump, files, m);
    }

    [Fact]
    public async Task A_company_set_holds_only_ciphertext_and_decrypts_through_the_signed_manifest()
    {
        var (set, dump, attachments, m) = await CreateAsync();
        var stored = Directory.EnumerateFiles(set, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(set, f).Replace('\\', '/')).OrderBy(x => x).ToList();
        Assert.All(stored, f => Assert.True(f.EndsWith(".hbe") || f.EndsWith(".hbe.sig"), f));                       // D-091: no plaintext at rest
        Assert.Equal(1 + attachments.Count + 2, stored.Count);
        Assert.Equal(1, m.Company!.SkippedStrayFiles);
        Assert.Equal(new[] { "k1" }, m.Company.KeyIdsRequired);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(set));

        var read = await SignedDocuments.ReadManifestAsync(set, trust, priv, EncryptedSets.CompanyKind);
        await EncryptedSets.VerifyStoredAsync(set, read);
        using (var plain = new MemoryStream()) { await EncryptedSets.DecryptAsync(set, read.Files[0], priv, plain); Assert.Equal(dump, plain.ToArray()); }
        foreach (var entry in read.Files.Skip(1))
        {
            using var plain = new MemoryStream();
            await EncryptedSets.DecryptAsync(set, entry, priv, plain);
            Assert.Equal(attachments[entry.Name["attachments/".Length..^".hbe".Length]], plain.ToArray());
        }
    }

    [Fact]
    public async Task Altered_missing_and_unlisted_files_are_refused_before_any_decryption()
    {
        var (set, _, _, m) = await CreateAsync();
        var dumpPath = Path.Combine(set, EncryptedSets.DumpName);
        var original = File.ReadAllBytes(dumpPath);
        var altered = (byte[])original.Clone(); altered[^3] ^= 1;
        File.WriteAllBytes(dumpPath, altered);
        Assert.Equal("backup_set_not_clean", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.VerifyStoredAsync(set, m))).Code);
        using (var sink = new MemoryStream())
            Assert.Equal("hbe_not_authenticated", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.DecryptAsync(set, m.Files[0], priv, sink))).Code);
        File.WriteAllBytes(dumpPath, original);
        await EncryptedSets.VerifyStoredAsync(set, m);
        File.WriteAllText(Path.Combine(set, "extra.txt"), "x");
        Assert.Equal("backup_set_not_clean", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.VerifyStoredAsync(set, m))).Code);
        File.Delete(Path.Combine(set, "extra.txt"));
        File.Delete(Path.Combine(set, m.Files[1].Name));
        Assert.Equal("backup_set_not_clean", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.VerifyStoredAsync(set, m))).Code);
    }

    [Fact]
    public async Task A_restore_reads_the_set_through_the_trust_list_and_decrypts_attachments_into_a_private_target_only_when_authentic()
    {
        var (set, _, attachments, m) = await CreateAsync();
        Assert.Equal("company_mismatch", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.ReadCompanySetAsync(set, Guid.NewGuid(), trust, priv))).Code);
        var read = await EncryptedSets.ReadCompanySetAsync(set, m.CompanyId!.Value, trust, priv);
        var target = Path.Combine(root, "restored", "att");
        Assert.Equal(attachments.Count, await EncryptedSets.RestoreAttachmentsAsync(set, read, target, priv));
        foreach (var (name, bytes) in attachments) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, name)));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(target));
        Assert.Equal("target_not_empty", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.RestoreAttachmentsAsync(set, read, target, priv))).Code);

        // an attachment altered after verification fails authentication while decrypting; the target is never created and no staging folder stays behind
        var victim = Path.Combine(set, read.Files[1].Name);
        var bytes2 = File.ReadAllBytes(victim); bytes2[^2] ^= 1; File.WriteAllBytes(victim, bytes2);
        var target2 = Path.Combine(root, "restored", "att2");
        Assert.Equal("hbe_not_authenticated", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.RestoreAttachmentsAsync(set, read, target2, priv))).Code);
        Assert.False(Directory.Exists(target2));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "restored"), "att2.restoring-*"));
        Assert.Equal("backup_set_not_clean", (await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.ReadCompanySetAsync(set, m.CompanyId!.Value, trust, priv))).Code);
    }

    [Fact]
    public async Task A_failing_dump_leaves_no_set_behind()
    {
        var att = Path.Combine(root, "att2"); Directory.CreateDirectory(att);
        var set = Path.Combine(root, "set2");
        await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.CreateCompanyAsync(Guid.NewGuid(), null, _ => throw new BackupException("dump_failed"), att, set, DateTimeOffset.UtcNow, 1,
            new BackupMaterial(pub, signer), default));
        Assert.False(Directory.Exists(set));
        // an ICU source is refused after the dump: the partial set is removed
        await Assert.ThrowsAsync<BackupException>(() => EncryptedSets.CreateCompanyAsync(Guid.NewGuid(), null, async consume => { await consume(new MemoryStream([1, 2, 3]), default); return new DatabaseLocale("UTF8", "und", "und", "i"); },
            att, set, DateTimeOffset.UtcNow, 1, new BackupMaterial(pub, signer), default));
        Assert.False(Directory.Exists(set));
    }
}
