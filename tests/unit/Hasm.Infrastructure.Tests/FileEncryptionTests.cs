using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using System.Text;
using Hasm.Infrastructure.Security;

namespace Hasm.Infrastructure.Tests;

/// <summary>Envelope encryption, key rings, tampering, wrong keys, rotation and fail-closed behaviour (disposable random keys only).</summary>
public class FileEncryptionTests
{
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("account,name\n0101,الصندوق\n0102,Bank\n");
    private const string Ctx = "hasm-import:company:batch";

    private static (EnvelopeEncryptor Enc, InMemoryKekRing Ring) New() { var ring = new InMemoryKekRing("k1"); return (new EnvelopeEncryptor(ring), ring); }

    [Fact]
    public void Content_round_trips_and_the_envelope_never_contains_the_plaintext_or_a_reused_key_or_nonce()
    {
        var (enc, _) = New();
        var a = enc.Encrypt(Plain, Ctx); var b = enc.Encrypt(Plain, Ctx);
        Assert.Equal(Plain, enc.Decrypt(a, Ctx));
        Assert.NotEqual(a, b);                                                                  // fresh data key and nonces every time
        Assert.True(a.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Bank")) < 0);                    // ciphertext only
        Assert.Equal("k1", EnvelopeEncryptor.KeyIdOf(a));
        // wrapped data key (32 + tag) and data nonce differ between two encryptions of the same file
        Assert.NotEqual(a.AsSpan(5 + 2, 12 + 32 + 16 + 12).ToArray(), b.AsSpan(5 + 2, 12 + 32 + 16 + 12).ToArray());
        Assert.Empty(enc.Decrypt(enc.Encrypt([], Ctx), Ctx));
        var big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        Assert.Equal(big, enc.Decrypt(enc.Encrypt(big, Ctx), Ctx));
    }

    [Fact]
    public void Every_kind_of_tampering_corruption_and_context_change_fails_authentication_and_returns_no_plaintext()
    {
        var (enc, _) = New();
        var good = enc.Encrypt(Plain, Ctx);
        // flip one byte in every region: header, key id, wrap nonce, wrapped key, wrap tag, data nonce, length, ciphertext, data tag
        for (var i = 0; i < good.Length; i++)
        {
            var bad = (byte[])good.Clone(); bad[i] ^= 0x01;
            var ex = Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt(bad, Ctx));
            Assert.Contains(ex.Code, new[] { "encryption_authentication_failed", "encryption_format_invalid", "encryption_key_unavailable" });
        }
        Assert.Equal("encryption_authentication_failed", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt(good, "hasm-import:company:OTHER-batch")).Code);   // moved to another context
        Assert.Equal("encryption_format_invalid", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt(good[..^1], Ctx)).Code);                                // truncated
        Assert.Equal("encryption_format_invalid", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt([.. good, 0], Ctx)).Code);                              // extended
        Assert.Equal("encryption_format_invalid", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt(Encoding.UTF8.GetBytes("not encrypted at all"), Ctx)).Code);
        Assert.Equal("encryption_format_invalid", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt([], Ctx)).Code);
    }

    [Fact]
    public void A_wrong_or_missing_key_never_decrypts_and_an_unavailable_key_store_fails_closed_for_encryption_too()
    {
        var (enc, ring) = New();
        var good = enc.Encrypt(Plain, Ctx);
        var other = new EnvelopeEncryptor(new InMemoryKekRing("k1"));                           // same id, different secret
        Assert.Equal("encryption_authentication_failed", Assert.Throws<EncryptionUnavailableException>(() => other.Decrypt(good, Ctx)).Code);
        var stranger = new EnvelopeEncryptor(new InMemoryKekRing("zzz"));                       // key id unknown to this ring
        Assert.Equal("encryption_key_unavailable", Assert.Throws<EncryptionUnavailableException>(() => stranger.Decrypt(good, Ctx)).Code);
        ring.Unavailable = true;
        Assert.Equal("encryption_key_unavailable", Assert.Throws<EncryptionUnavailableException>(() => enc.Encrypt(Plain, Ctx)).Code);
        Assert.Equal("encryption_key_unavailable", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt(good, Ctx)).Code);
        Assert.Throws<EncryptionUnavailableException>(() => new KekHandle("k9", new byte[16]));  // too short
        Assert.Throws<EncryptionUnavailableException>(() => new KekHandle("BAD ID", new byte[32]));
    }

    [Fact]
    public void Rotation_keeps_old_files_readable_and_a_rewrap_moves_only_the_data_key_to_the_active_key()
    {
        var ring = new InMemoryKekRing("k1"); var enc = new EnvelopeEncryptor(ring);
        var old = enc.Encrypt(Plain, Ctx);
        ring.Add("k2", RandomNumberGenerator.GetBytes(32)); ring.Activate("k2");
        Assert.Equal(Plain, enc.Decrypt(old, Ctx));                                             // still readable during rotation
        Assert.Equal("k2", EnvelopeEncryptor.KeyIdOf(enc.Encrypt(Plain, Ctx)));
        var wrapped = enc.Rewrap(old, Ctx);
        Assert.Equal("k2", EnvelopeEncryptor.KeyIdOf(wrapped));
        Assert.Equal(old[^(12 + 8 + Plain.Length + 16)..], wrapped[^(12 + 8 + Plain.Length + 16)..]);   // nonce, length, ciphertext and tag are untouched
        ring.Remove("k1");                                                                      // the retired key may now be removed: the rewrapped file no longer needs it
        Assert.Equal(Plain, enc.Decrypt(wrapped, Ctx));
        Assert.Equal("encryption_key_unavailable", Assert.Throws<EncryptionUnavailableException>(() => enc.Decrypt(old, Ctx)).Code);
        Assert.Equal("encryption_authentication_failed", Assert.Throws<EncryptionUnavailableException>(() => enc.Rewrap(wrapped, "other-context")).Code);
    }

    [Fact]
    public void The_file_key_ring_loads_versioned_keys_fails_closed_never_generates_one_implicitly_and_refuses_bad_key_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hasm-kek-" + Guid.NewGuid().ToString("N"));
        try
        {
            var unconfigured = new FileKekRing(new EncryptionOptions());
            Assert.Equal("encryption_not_configured", Assert.Throws<EncryptionUnavailableException>(() => unconfigured.GetActive()).Code);
            var missingDir = new FileKekRing(new EncryptionOptions { KeyDirectory = dir, ActiveKeyId = "k1" });
            Assert.Equal("encryption_key_unavailable", Assert.Throws<EncryptionUnavailableException>(() => missingDir.GetActive()).Code);
            Assert.False(Directory.Exists(dir));                                                // nothing was created as a side effect
            FileKekRing.Generate(dir, "k1"); FileKekRing.Generate(dir, "k2");
            Assert.ThrowsAny<IOException>(() => FileKekRing.Generate(dir, "k1"));              // never overwritten
            var options = new EncryptionOptions { KeyDirectory = dir, ActiveKeyId = "k1" };
            var enc1 = new EnvelopeEncryptor(new FileKekRing(options));
            var file = enc1.Encrypt(Plain, Ctx);
            Assert.Equal("k1", EnvelopeEncryptor.KeyIdOf(file));
            var enc2 = new EnvelopeEncryptor(new FileKekRing(new EncryptionOptions { KeyDirectory = dir, ActiveKeyId = "k2" }));
            Assert.Equal(Plain, enc2.Decrypt(file, Ctx));                                       // a fresh process with a newer active key reads the older file
            Assert.Equal("k2", EnvelopeEncryptor.KeyIdOf(enc2.Rewrap(file, Ctx)));
            // an active key that does not exist, a malformed key file and a short key all fail closed (no key is generated, no material is echoed)
            Assert.Equal("encryption_key_unavailable", Assert.Throws<EncryptionUnavailableException>(() => new FileKekRing(new EncryptionOptions { KeyDirectory = dir, ActiveKeyId = "nope" }).GetActive()).Code);
            File.WriteAllText(Path.Combine(dir, "bad" + FileKekRing.Extension), "!!!not base64!!!");
            File.WriteAllText(Path.Combine(dir, "short" + FileKekRing.Extension), Convert.ToBase64String(new byte[16]));
            foreach (var id in new[] { "bad", "short" })
            {
                var ex = Assert.Throws<EncryptionUnavailableException>(() => new FileKekRing(new EncryptionOptions { KeyDirectory = dir, ActiveKeyId = id, AllowInsecureFilePermissions = true }).GetActive());
                Assert.DoesNotContain(dir, ex.Message);
            }
            Assert.Null(new FileKekRing(options).Find("../k1"));                               // key ids cannot traverse
            Assert.False(File.Exists(Path.Combine(dir, "nope" + FileKekRing.Extension)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Key_options_parse_without_throwing_and_expose_no_default_key_location()
    {
        var empty = EncryptionOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Null(empty.KeyDirectory); Assert.Null(empty.ActiveKeyId); Assert.False(empty.AllowInsecureFilePermissions);
        var names = new[] { "Hasm__Encryption__KeyDirectory", "Hasm__Encryption__ActiveKeyId", "Hasm__Encryption__AllowInsecureFilePermissions" };
        try
        {
            Environment.SetEnvironmentVariable(names[0], " /x "); Environment.SetEnvironmentVariable(names[1], "k1"); Environment.SetEnvironmentVariable(names[2], "nonsense");
            var configured = EncryptionOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddEnvironmentVariables().Build());
            Assert.Equal(("/x", "k1", false), (configured.KeyDirectory, configured.ActiveKeyId, configured.AllowInsecureFilePermissions));   // an unparsable switch means the strict default
        }
        finally { foreach (var n in names) Environment.SetEnvironmentVariable(n, null); }
    }
}
