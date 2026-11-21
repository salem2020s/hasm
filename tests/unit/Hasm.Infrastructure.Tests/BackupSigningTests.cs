using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.3 slice 5 (D-092, D-093; review M1, M5 and round 2 L-d of #116): the server-held signing key, detached signatures and the recovery-kit trust list.</summary>
public sealed class BackupSigningTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "hasm-sign-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("{\"kind\":\"company\",\"runId\":\"x\"}");

    [Fact]
    public void A_generated_key_is_owner_only_signs_exact_bytes_and_verifies_only_through_the_kit()
    {
        var (id, pub) = BackupSigningKeys.Generate(dir);
        Assert.Matches("^sk-[0-9a-f]{16}$", id);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, id + BackupSigningKeys.PrivateSuffix)));
        using var signer = BackupSigningKeys.Load(dir, id, allowInsecurePermissions: true);
        var sig = signer.Sign(Content);
        Assert.Equal(id, sig.KeyId);
        var parsed = BackupSignature.Parse(sig.ToBytes());
        var trust = new SigningTrust([new TrustedSigningKey(id, pub, SigningKeyStatus.Active)]);
        Assert.Equal(id, trust.Verify(Content, parsed).KeyId);

        Assert.Equal("signature_invalid", Assert.Throws<BackupException>(() => trust.Verify([.. Content, (byte)' '], parsed)).Code);   // exact bytes
        Assert.Equal("signature_missing", Assert.Throws<BackupException>(() => trust.Verify(Content, null)).Code);
        Assert.Null(BackupSignature.Parse("not json"u8.ToArray()));
        Assert.Null(BackupSignature.Parse(Encoding.UTF8.GetBytes("{\"version\":2,\"keyId\":\"" + id + "\",\"signature\":\"AA==\"}")));
        Assert.Equal("signature_invalid", Assert.Throws<BackupException>(() => trust.Verify(Content, sig with { Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) })).Code);

        // a key the kit does not know (for example one carried inside a forged set) never verifies
        var (otherId, _) = BackupSigningKeys.Generate(Path.Combine(dir, "other"));
        using var other = BackupSigningKeys.Load(Path.Combine(dir, "other"), otherId, allowInsecurePermissions: true);
        Assert.Equal("signing_key_unknown", Assert.Throws<BackupException>(() => trust.Verify(Content, other.Sign(Content))).Code);
        Assert.Equal("signing_key_unavailable", Assert.Throws<BackupException>(() => BackupSigningKeys.Load(dir, "sk-0000000000000000", true)).Code);
    }

    [Fact]
    public void A_compromised_key_never_verifies_and_a_retired_key_only_for_content_created_before_its_retirement()
    {
        var (id, pub) = BackupSigningKeys.Generate(dir);
        using var signer = BackupSigningKeys.Load(dir, id, allowInsecurePermissions: true);
        var sig = signer.Sign(Content);
        Assert.Equal("signing_key_compromised", Assert.Throws<BackupException>(() => new SigningTrust([new TrustedSigningKey(id, pub, SigningKeyStatus.Compromised)]).Verify(Content, sig)).Code);
        var retired = new SigningTrust([new TrustedSigningKey(id, pub, SigningKeyStatus.Retired, T0)]);
        var entry = retired.Verify(Content, sig);
        SigningTrust.EnsureValidAt(entry, T0.AddSeconds(-1));                                                         // signed before the retirement: still valid
        Assert.Equal("signing_key_retired", Assert.Throws<BackupException>(() => SigningTrust.EnsureValidAt(entry, T0)).Code);
        Assert.Equal("signing_key_retired", Assert.Throws<BackupException>(() => SigningTrust.EnsureValidAt(entry, T0.AddDays(1))).Code);
        Assert.Equal("signing_trust_invalid", Assert.Throws<BackupException>(() => new SigningTrust([new TrustedSigningKey(id, pub, SigningKeyStatus.Retired)])).Code);   // retired needs a date
        Assert.Equal("signing_trust_invalid", Assert.Throws<BackupException>(() => new SigningTrust([new TrustedSigningKey("sk-0000000000000000", pub, SigningKeyStatus.Active)])).Code);   // id must match the key
    }
}
