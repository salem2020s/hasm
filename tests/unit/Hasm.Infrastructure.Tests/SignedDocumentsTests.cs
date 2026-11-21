using System.Security.Cryptography;
using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.3 slice 5 (D-092, D-116; review M1, L-b, L-d of #116): version-2 manifests encrypted and signed over their ciphertext, and the signed run status.</summary>
public sealed class SignedDocumentsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-sdoc-" + Guid.NewGuid().ToString("N"));
    private const string Pass = "a recovery passphrase of twenty+";
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);

    private readonly RecoveryKeys.GeneratedPair pair = RecoveryKeys.Generate(Pass);
    private readonly RecoveryPublicKey pub;
    private readonly RecoveryPrivateKey priv;
    private readonly BackupSigner signer;
    private readonly SigningTrust trust;
    private readonly string signingPub;

    public SignedDocumentsTests()
    {
        Directory.CreateDirectory(root);
        pub = RecoveryPublicKey.FromPem(pair.PublicKeyPem);
        priv = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
        var (id, p) = BackupSigningKeys.Generate(Path.Combine(root, "sign"));
        signingPub = p;
        signer = BackupSigningKeys.Load(Path.Combine(root, "sign"), id, true);
        trust = new SigningTrust([new TrustedSigningKey(id, p, SigningKeyStatus.Active)]);
    }

    public void Dispose() { signer.Dispose(); pub.Dispose(); priv.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private ManifestV2 Manifest(string kind = "company") => new(ManifestV2.CurrentVersion, kind, "20261121T020000Z-0011aabb", Guid.NewGuid(), T0, pub.KeyId, signer.KeyId,
        [new EncryptedFileEntry("company-dump", "database.dump.hbe", 10, new string('a', 64), [new string('b', 64)])],
        new CompanyDetails(new DatabaseLocale("UTF8", "C", "C", "c"), 31, ["k1"], 0, 0));

    [Fact]
    public async Task An_encrypted_manifest_round_trips_only_with_a_valid_signature_over_its_ciphertext()
    {
        var dir = Path.Combine(root, "set"); Directory.CreateDirectory(dir);
        var m = Manifest();
        await SignedDocuments.WriteManifestAsync(dir, m, pub, signer);
        Assert.False(File.Exists(Path.Combine(dir, "manifest.json")));                                                 // never in plaintext
        var read = await SignedDocuments.ReadManifestAsync(dir, trust, priv, "company");
        Assert.Equal(m.CompanyId, read.CompanyId);
        Assert.Equal((m.Files[0].Purpose, m.Files[0].Name, m.Files[0].Size, m.Files[0].HeaderSha256), (read.Files[0].Purpose, read.Files[0].Name, read.Files[0].Size, read.Files[0].HeaderSha256));
        Assert.Equal(m.Files[0].ChunkSha256, read.Files[0].ChunkSha256);
        Assert.Equal(31, read.Company!.CompanyMigrationVersion);

        Assert.Equal("manifest_invalid", (await Assert.ThrowsAsync<BackupException>(() => SignedDocuments.ReadManifestAsync(dir, trust, priv, "system"))).Code);
        var path = Path.Combine(dir, SignedDocuments.ManifestFileName);
        var bytes = File.ReadAllBytes(path); bytes[^5] ^= 1;
        File.Delete(path); File.WriteAllBytes(path, bytes);
        Assert.Equal("signature_invalid", (await Assert.ThrowsAsync<BackupException>(() => SignedDocuments.ReadManifestAsync(dir, trust, priv, "company"))).Code);
        File.Delete(path + SignedDocuments.SignatureSuffix);
        Assert.Equal("signature_missing", (await Assert.ThrowsAsync<BackupException>(() => SignedDocuments.ReadManifestAsync(dir, trust, priv, "company"))).Code);
    }

    [Fact]
    public async Task A_manifest_from_a_key_the_kit_does_not_trust_or_signed_after_its_retirement_is_refused()
    {
        var dir = Path.Combine(root, "set"); Directory.CreateDirectory(dir);
        await SignedDocuments.WriteManifestAsync(dir, Manifest(), pub, signer);
        var (otherId, otherPub) = BackupSigningKeys.Generate(Path.Combine(root, "other"));
        Assert.Equal("signing_key_unknown", (await Assert.ThrowsAsync<BackupException>(() => SignedDocuments.ReadManifestAsync(dir, new SigningTrust([new TrustedSigningKey(otherId, otherPub, SigningKeyStatus.Active)]), priv, "company"))).Code);
        var retiredBefore = new SigningTrust([new TrustedSigningKey(signer.KeyId, signingPub, SigningKeyStatus.Retired, T0)]);
        Assert.Equal("signing_key_retired", (await Assert.ThrowsAsync<BackupException>(() => SignedDocuments.ReadManifestAsync(dir, retiredBefore, priv, "company"))).Code);
        var retiredAfter = new SigningTrust([new TrustedSigningKey(signer.KeyId, signingPub, SigningKeyStatus.Retired, T0.AddDays(1))]);
        Assert.NotNull(await SignedDocuments.ReadManifestAsync(dir, retiredAfter, priv, "company"));
        var other = RecoveryKeys.Generate(Pass);
        using var otherPriv = RecoveryPrivateKey.FromEncryptedPem(other.EncryptedPrivateKeyPem, Pass);
        Assert.Equal("recovery_key_mismatch", (await Assert.ThrowsAsync<BackupException>(() => SignedDocuments.ReadManifestAsync(dir, trust, otherPriv, "company"))).Code);
    }

    [Fact]
    public void The_run_status_is_read_only_with_a_valid_signature_and_an_unsigned_status_counts_as_failed()
    {
        var runId = "20261121T020000Z-0011aabb";
        var dir = Path.Combine(root, runId); Directory.CreateDirectory(dir);
        var status = new RunStatus(1, RunStatus.KindValue, runId, T0, T0.AddMinutes(3), 180000, "ok", 2, 0, 0, 0, 1, pub.KeyId, signer.KeyId,
            Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))), ["session", "login_throttle"], [], true, [new RunStatusCompany(Guid.NewGuid(), "ok")]);
        SignedDocuments.WriteRunStatus(dir, status, signer);
        Assert.Equal("ok", SignedDocuments.ReadRunStatus(dir, trust).Verdict);
        var path = Path.Combine(dir, SignedDocuments.RunStatusFileName);
        var text = File.ReadAllText(path).Replace("\"verdict\": \"ok\"", "\"verdict\": \"partial\"");
        File.Delete(path); File.WriteAllText(path, text);
        Assert.Equal("status_unsigned", Assert.Throws<BackupException>(() => SignedDocuments.ReadRunStatus(dir, trust)).Code);   // edited: the signature no longer verifies
        File.Delete(path + SignedDocuments.SignatureSuffix);
        Assert.Equal("status_unsigned", Assert.Throws<BackupException>(() => SignedDocuments.ReadRunStatus(dir, trust)).Code);
    }

    [Fact]
    public async Task The_keyless_digest_of_a_stored_file_equals_the_digest_produced_while_encrypting()
    {
        foreach (var size in new[] { 0, 1, Hbe1.DefaultChunkSize, Hbe1.DefaultChunkSize + 1 })
        {
            using var output = new MemoryStream();
            var expected = await Hbe1.EncryptAsync(new MemoryStream(RandomNumberGenerator.GetBytes(size)), output, pub, "attachment", "x.att");
            var computed = await Hbe1.ComputeDigestAsync(new MemoryStream(output.ToArray()));
            Assert.Equal(expected.HeaderSha256, computed.HeaderSha256);
            Assert.Equal(expected.ChunkSha256, computed.ChunkSha256);
        }
    }
}
