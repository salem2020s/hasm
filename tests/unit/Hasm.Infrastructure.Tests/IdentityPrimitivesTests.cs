using Hasm.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

public class LoginNameTests
{
    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("  Alice  ", "alice")]
    [InlineData("ALICE.Smith", "alice.smith")]
    [InlineData("a_b-c@example.com", "a_b-c@example.com")]
    [InlineData("ＡＬＩＣＥ", "alice")]              // NFKC folds full-width forms to ASCII, so they collide with "alice"
    [InlineData("user123", "user123")]
    public void Normalizes_to_a_canonical_lowercase_form(string input, string expected)
    {
        Assert.True(LoginName.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]                               // too short
    [InlineData(".alice")]                           // must start alphanumeric
    [InlineData("-alice")]
    [InlineData("ali ce")]                           // whitespace inside
    [InlineData("al\nice")]
    [InlineData("علي_أحمد")]                          // non-ASCII letters are rejected (look-alike protection)
    [InlineData("аlice")]                            // Cyrillic "а" homoglyph
    [InlineData("alice'; DROP TABLE x;--")]
    [InlineData("a/b/c")]
    public void Rejects_invalid_identifiers(string? input)
    {
        Assert.False(LoginName.TryNormalize(input, out var normalized));
        Assert.Equal("", normalized);
    }

    [Fact]
    public void Enforces_length_bounds()
    {
        Assert.True(LoginName.TryNormalize(new string('a', 64), out _));
        Assert.False(LoginName.TryNormalize(new string('a', 65), out _));
        Assert.False(LoginName.TryNormalize(new string('a', LoginName.MaxInputLength + 1), out _));
    }

    [Fact]
    public void Lone_surrogates_do_not_throw()
        => Assert.False(LoginName.TryNormalize("ali\ud800ce", out _));
}

public class PasswordPolicyTests
{
    [Fact]
    public void Accepts_a_reasonable_passphrase() => Assert.Empty(PasswordPolicy.Validate("correct horse battery", "alice"));

    [Theory]
    [InlineData(null, "too_short")]
    [InlineData("", "too_short")]
    [InlineData("short1", "too_short")]
    [InlineData("aaaaaaaaaaaaaaaa", "too_repetitive")]
    [InlineData("alice.smith@example.com", "same_as_login")]
    public void Reports_violations_as_codes(string? password, string code)
        => Assert.Contains(code, PasswordPolicy.Validate(password, "alice.smith@example.com"));

    [Fact]
    public void Rejects_overlong_passwords() => Assert.Contains("too_long", PasswordPolicy.Validate(new string('x', 1) + string.Concat(Enumerable.Repeat("ab12", 40))));

    [Fact]
    public void Violation_codes_never_echo_the_password()
        => Assert.All(PasswordPolicy.Validate("hunter2hunter2", "hunter2hunter2"), c => Assert.DoesNotContain("hunter2", c));
}

public class PasswordServiceTests
{
    private const string Password = "Correct-Horse-Battery-9";

    [Fact]
    public void Hash_does_not_contain_the_password_and_uses_the_framework_format()
    {
        var hash = new PasswordService(1000).Hash(Password);
        Assert.DoesNotContain(Password, hash);
        Assert.StartsWith("AQAAAA", hash);               // ASP.NET Core Identity v3 marker (format version 0x01)
        Assert.True(hash.Length >= 60);                  // satisfies the database CHECK constraint
    }

    [Fact]
    public void Each_hash_uses_a_fresh_random_salt()
    {
        var service = new PasswordService(1000);
        Assert.NotEqual(service.Hash(Password), service.Hash(Password));
    }

    [Fact]
    public void Verifies_the_right_password_and_rejects_others()
    {
        var service = new PasswordService(1000);
        var hash = service.Hash(Password);
        Assert.Equal(PasswordVerification.Success, service.Verify(hash, Password));
        Assert.Equal(PasswordVerification.Failed, service.Verify(hash, Password + "x"));
        Assert.Equal(PasswordVerification.Failed, service.Verify(hash, ""));
        Assert.Equal(PasswordVerification.Failed, service.Verify(hash, Password.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("plaintext-password-stored-by-mistake")]
    public void Malformed_or_plaintext_stored_values_never_verify(string stored)
        => Assert.Equal(PasswordVerification.Failed, new PasswordService(1000).Verify(stored, stored));

    [Fact]
    public void Hashes_with_a_lower_iteration_count_request_a_rehash()
    {
        var old = new PasswordService(1000).Hash(Password);
        Assert.Equal(PasswordVerification.SuccessRehashNeeded, new PasswordService(2000).Verify(old, Password));
        Assert.Equal(PasswordVerification.Success, new PasswordService(2000).Verify(new PasswordService(2000).Hash(Password), Password));
    }

    [Fact]
    public void Default_cost_meets_the_documented_minimum() => Assert.True(PasswordService.DefaultIterations >= 210_000);

    [Fact]
    public void Dummy_verification_does_not_throw() => new PasswordService(1000).BurnVerification(Password);
}

public class SessionTokenTests
{
    [Fact]
    public void Tokens_are_unique_256_bit_base64url()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => SessionToken.Generate()).ToList();
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(SessionToken.IsWellFormed(t), t));
        Assert.All(tokens, t => Assert.Equal(43, t.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("a b c d e f g h i j k l m n o p q r s t u v w x y z")]
    [InlineData("'; DROP TABLE x; --aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Malformed_tokens_are_rejected_before_any_lookup(string? token) => Assert.False(SessionToken.IsWellFormed(token));

    [Fact]
    public void Stored_digest_is_a_32_byte_one_way_hash()
    {
        var token = SessionToken.Generate();
        var hash = SessionToken.Hash(token);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, SessionToken.Hash(token));
        Assert.NotEqual(hash, SessionToken.Hash(SessionToken.Generate()));
        Assert.DoesNotContain(token, Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase);
    }
}

public class AuthOptionsTests
{
    private static IConfiguration Config(params (string, string)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => $"Hasm:Auth:{v.Item1}", v => (string?)v.Item2)).Build();

    [Fact]
    public void Defaults_are_valid_and_conservative()
    {
        var o = AuthOptions.FromConfiguration(Config());
        Assert.Equal(30, o.SessionIdleMinutes);
        Assert.Equal(12, o.SessionAbsoluteHours);
        Assert.Equal(5, o.MaxFailuresPerLoginSource);
        Assert.Equal(100, o.MaxFailuresPerLoginGlobal);
        Assert.True(o.Pbkdf2Iterations >= 210_000);
    }

    [Fact]
    public void Weak_password_hashing_cost_is_refused()
    {
        var ex = Assert.Throws<AuthConfigurationException>(() => AuthOptions.FromConfiguration(Config(("Pbkdf2Iterations", "1000"))));
        Assert.Contains("Pbkdf2Iterations", ex.Message);
    }

    [Fact]
    public void Non_numeric_values_name_the_setting_without_echoing_the_value()
    {
        var ex = Assert.Throws<AuthConfigurationException>(() => AuthOptions.FromConfiguration(Config(("BlockMinutes", "s3cret-value"))));
        Assert.Contains("BlockMinutes", ex.Message);
        Assert.DoesNotContain("s3cret-value", ex.Message);
    }

    [Theory]
    [InlineData("SessionIdleMinutes", "0")]
    [InlineData("SessionAbsoluteHours", "100000")]
    [InlineData("MaxFailuresPerLoginSource", "1")]
    [InlineData("MaxFailuresPerLoginGlobal", "9")]
    [InlineData("MaxFailuresPerLoginGlobal", "19")]   // below 4x the per-pair limit
    public void Out_of_range_values_are_refused(string key, string value)
        => Assert.Throws<AuthConfigurationException>(() => AuthOptions.FromConfiguration(Config((key, value))));
}
