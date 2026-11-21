using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 4.1 S1 (D-080 (2)): TOTP secrets under the protected key ring, bound to user, key and row.</summary>
public class TotpSecretProtectorTests
{
    private static readonly Guid User = Guid.NewGuid(), Secret = Guid.NewGuid();

    private static (TotpSecretProtector P, InMemoryKekRing Ring) Make() { var ring = new InMemoryKekRing("k1"); return (new TotpSecretProtector(ring), ring); }

    [Fact]
    public void Round_trips_and_the_ciphertext_is_not_the_secret()
    {
        var (p, _) = Make();
        var secret = Totp.GenerateSecret();
        var stored = p.Protect(User, Secret, secret);
        Assert.NotEqual(secret, stored.Cipher);
        Assert.Equal("k1", stored.KekId);
        Assert.Equal(secret, p.Unprotect(User, Secret, stored));
    }

    [Fact]
    public void Every_protection_uses_a_fresh_nonce()
    {
        var (p, _) = Make();
        var secret = Totp.GenerateSecret();
        var a = p.Protect(User, Secret, secret);
        var b = p.Protect(User, Secret, secret);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Cipher, b.Cipher);
    }

    [Fact]
    public void A_ciphertext_moved_to_another_user_or_row_or_key_label_fails_authentication()
    {
        var (p, _) = Make();
        var stored = p.Protect(User, Secret, Totp.GenerateSecret());
        Assert.Equal("totp_secret_authentication_failed", Assert.Throws<MfaUnavailableException>(() => p.Unprotect(Guid.NewGuid(), Secret, stored)).Code);
        Assert.Equal("totp_secret_authentication_failed", Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Guid.NewGuid(), stored)).Code);
    }

    [Fact]
    public void Tampered_cipher_nonce_or_tag_fail_and_malformed_records_are_refused()
    {
        var (p, _) = Make();
        var s = p.Protect(User, Secret, Totp.GenerateSecret());
        byte[] Flip(byte[] b) { var c = (byte[])b.Clone(); c[0] ^= 1; return c; }
        Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s with { Cipher = Flip(s.Cipher) }));
        Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s with { Nonce = Flip(s.Nonce) }));
        Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s with { Tag = Flip(s.Tag) }));
        Assert.Equal("totp_secret_invalid", Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s with { Nonce = [1, 2] })).Code);
        Assert.Equal("totp_secret_invalid", Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s with { KekId = "Bad Id!" })).Code);
    }

    [Fact]
    public void A_missing_key_fails_closed_for_both_directions()
    {
        var (p, ring) = Make();
        var s = p.Protect(User, Secret, Totp.GenerateSecret());
        ring.Unavailable = true;
        Assert.Equal("totp_key_unavailable", Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s)).Code);
        Assert.Equal("totp_key_unavailable", Assert.Throws<MfaUnavailableException>(() => p.Protect(User, Secret, Totp.GenerateSecret())).Code);
        ring.Unavailable = false;
        ring.Remove("k1");
        Assert.Equal("totp_key_unavailable", Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, s)).Code);   // a retired and removed key
    }

    [Fact]
    public void Rewrap_moves_the_secret_to_the_active_key_and_the_old_key_can_then_be_retired()
    {
        var (p, ring) = Make();
        var secret = Totp.GenerateSecret();
        var old = p.Protect(User, Secret, secret);
        ring.Add("k2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");
        Assert.Equal(secret, p.Unprotect(User, Secret, old));                                              // the retired key still reads existing secrets
        var moved = p.Rewrap(User, Secret, old);
        Assert.Equal("k2", moved.KekId);
        ring.Remove("k1");
        Assert.Equal(secret, p.Unprotect(User, Secret, moved));
        Assert.Throws<MfaUnavailableException>(() => p.Unprotect(User, Secret, old));
    }

    [Fact]
    public void A_totp_secret_is_never_readable_with_the_file_wrapping_key_label()
    {
        // the subkey is purpose-bound: protecting under the "totp" purpose differs from any other purpose of the same key
        var ring = new InMemoryKekRing("k1");
        var k = ring.GetActive();
        var a = k.GetType().GetMethod("DeriveSubkey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotEqual((byte[])a.Invoke(k, ["totp"])!, (byte[])a.Invoke(k, ["other"])!);
    }

    [Fact]
    public void An_empty_secret_is_refused()
    {
        var (p, _) = Make();
        Assert.Throws<ArgumentException>(() => p.Protect(User, Secret, ReadOnlySpan<byte>.Empty));
    }
}
