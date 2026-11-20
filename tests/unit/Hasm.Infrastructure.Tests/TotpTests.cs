using System.Text;
using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1 secret "12345678901234567890" (8-digit reference values; the 6-digit code is their last six digits).
    private static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_the_RFC_6238_vectors(long unixSeconds, string expected8)
    {
        var step = Totp.StepFor(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected8, Totp.Compute(Rfc, step, 8));
        Assert.Equal(expected8[2..], Totp.Compute(Rfc, step));
    }

    [Fact]
    public void Verify_accepts_the_current_and_adjacent_steps_only()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.StepFor(now);
        Assert.Equal(step, Totp.Verify(Rfc, Totp.Compute(Rfc, step), now));
        Assert.Equal(step - 1, Totp.Verify(Rfc, Totp.Compute(Rfc, step - 1), now));
        Assert.Equal(step + 1, Totp.Verify(Rfc, Totp.Compute(Rfc, step + 1), now));
        Assert.Null(Totp.Verify(Rfc, Totp.Compute(Rfc, step - 2), now));
        Assert.Null(Totp.Verify(Rfc, Totp.Compute(Rfc, step + 2), now));
        Assert.Equal(step - 2, Totp.Verify(Rfc, Totp.Compute(Rfc, step - 2), now, window: 2));
    }

    [Fact]
    public void Verify_refuses_a_replayed_step_but_accepts_a_newer_one()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.StepFor(now);
        var accepted = Totp.Verify(Rfc, Totp.Compute(Rfc, step), now);
        Assert.Equal(step, accepted);
        Assert.Null(Totp.Verify(Rfc, Totp.Compute(Rfc, step), now, lastAcceptedStep: accepted));
        Assert.Null(Totp.Verify(Rfc, Totp.Compute(Rfc, step - 1), now, lastAcceptedStep: accepted));
        Assert.Equal(step + 1, Totp.Verify(Rfc, Totp.Compute(Rfc, step + 1), now, lastAcceptedStep: accepted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    [InlineData("١٢٣٤٥٦")] // Arabic-Indic digits are not codes
    public void Verify_rejects_malformed_codes(string? code)
        => Assert.Null(Totp.Verify(Rfc, code, DateTimeOffset.FromUnixTimeSeconds(59)));

    [Fact]
    public void Verify_rejects_a_wrong_code_and_a_wrong_secret()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var good = Totp.Compute(Rfc, Totp.StepFor(now));
        var wrong = good == "000000" ? "000001" : "000000";
        Assert.Null(Totp.Verify(Rfc, wrong, now));
        Assert.Null(Totp.Verify(Totp.GenerateSecret(), good, now));
    }

    [Fact]
    public void Verify_and_compute_validate_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Verify(Rfc, "123456", DateTimeOffset.UtcNow, window: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Verify(Rfc, "123456", DateTimeOffset.UtcNow, window: -1));
        Assert.Throws<ArgumentException>(() => Totp.Compute([], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Compute(Rfc, 1, 10));
    }

    [Theory]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_the_RFC_4648_vectors(string plain, string encoded)
    {
        Assert.Equal(encoded, Totp.ToBase32(Encoding.ASCII.GetBytes(plain)));
        Assert.True(Totp.TryFromBase32(encoded, out var data));
        Assert.Equal(plain, Encoding.ASCII.GetString(data));
    }

    [Fact]
    public void Base32_decoding_tolerates_case_spacing_and_padding_and_round_trips_secrets()
    {
        Assert.True(Totp.TryFromBase32("mzxw 6ytb-oi======", out var data));
        Assert.Equal("foobar", Encoding.ASCII.GetString(data));
        var secret = Totp.GenerateSecret();
        Assert.Equal(Totp.SecretBytes, secret.Length);
        Assert.True(Totp.TryFromBase32(Totp.ToBase32(secret), out var back));
        Assert.Equal(secret, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MZXW6YTB1")]   // '1' is not in the alphabet
    [InlineData("MZXW=6YTB")]   // data after padding
    [InlineData("M")]           // impossible length
    [InlineData("MZ")]          // non-zero trailing bits
    public void Base32_decoding_rejects_invalid_input(string? text)
        => Assert.False(Totp.TryFromBase32(text, out _));

    [Fact]
    public void Provisioning_uri_carries_the_standard_parameters_and_escapes_the_labels()
    {
        var uri = Totp.ProvisioningUri("HASM ERP", "ali@example.com", Rfc);
        Assert.Equal("otpauth://totp/HASM%20ERP:ali%40example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=HASM%20ERP&algorithm=SHA1&digits=6&period=30", uri);
        Assert.Throws<ArgumentException>(() => Totp.ProvisioningUri("a:b", "x", Rfc));
        Assert.Throws<ArgumentException>(() => Totp.ProvisioningUri("HASM", " ", Rfc));
    }
}
