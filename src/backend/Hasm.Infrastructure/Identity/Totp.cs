using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// RFC 6238 time-based one-time passwords (HMAC-SHA-1, 30 s step, 6 digits: the parameters every authenticator app supports) and RFC 4648 Base32.
/// Pure functions with no storage or policy: enrolment, secret protection, enforcement and recovery are separate, later slices of Phase 4.1.
/// Verification reports the matched time step so the caller can persist it and refuse replays of an already used step.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20;
    public const int MaxWindow = 2;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepFor(DateTimeOffset utc) => utc.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code of one time step, zero-padded to <paramref name="digits"/> (1..9).</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        if (secret.Length == 0) throw new ArgumentException("A TOTP secret must not be empty.", nameof(secret));
        if (digits is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(digits));
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, mac);
        var offset = mac[19] & 0x0F;
        var binary = BinaryPrimitives.ReadUInt32BigEndian(mac.Slice(offset, 4)) & 0x7FFFFFFFu;
        uint modulus = 1;
        for (var i = 0; i < digits; i++) modulus *= 10;
        return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// Checks a submitted code against the steps within <paramref name="window"/> of the current one. Returns the matched step, or null when
    /// the code is malformed, wrong, or belongs to a step not newer than <paramref name="lastAcceptedStep"/> (replay). Every candidate step is compared in
    /// constant time and all are always evaluated, so timing does not reveal which step, if any, matched.
    /// <para><b>Caller requirement (replay protection):</b> this function is pure and cannot prevent a replay by itself. The caller MUST pass the last accepted step it stored for the user and MUST persist the
    /// returned step with an atomic compare-and-set (for example <c>UPDATE ... SET last_step = @matched WHERE user = @u AND (last_step IS NULL OR last_step &lt; @matched)</c> and require one affected row) in
    /// the same step as accepting the sign-in. A plain read-then-write lets two concurrent requests with the same code both succeed. Failures must also be throttled by the caller (see the login throttling).</para>
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset utc, long? lastAcceptedStep = null, int window = 1)
    {
        if (window is < 0 or > MaxWindow) throw new ArgumentOutOfRangeException(nameof(window));
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var submitted = Encoding.ASCII.GetBytes(code);
        var current = StepFor(utc);
        long? match = null;
        for (var step = current - window; step <= current + window; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Compute(secret, step));
            var equal = CryptographicOperations.FixedTimeEquals(submitted, expected);
            if (equal && (lastAcceptedStep is null || step > lastAcceptedStep) && (match is null || step > match)) match = step;
        }
        return match;
    }

    /// <summary>Unpadded upper-case Base32, the form authenticator apps display and accept.</summary>
    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Decodes Base32, ignoring case, spaces, hyphens and trailing '=' padding. Returns false for any other character or an impossible length.</summary>
    public static bool TryFromBase32(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(text)) return false;
        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        var padding = 0;
        var symbols = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-') continue;
            if (raw == '=') { padding++; continue; }
            if (padding > 0 || raw > 127) return false;                       // data after padding, or any non-ASCII character (no culture folding: U+0131 must not become 'I')
            var index = Alphabet.IndexOf(raw is >= 'a' and <= 'z' ? (char)(raw - 32) : raw);
            if (index < 0) return false;
            symbols++;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        // A valid encoding leaves fewer than 5 spare bits, all zero.
        if (bytes.Count == 0 || bits >= 5 || buffer != 0) return false;
        // Padding is optional, but when present it must complete a block of 8 symbols with a valid count (1, 3, 4 or 6).
        if (padding > 0 && (padding is not (1 or 3 or 4 or 6) || (symbols + padding) % 8 != 0)) return false;
        data = bytes.ToArray();
        return true;
    }

    /// <summary>The otpauth provisioning URI (de-facto Key URI format) for QR codes. Issuer and account are percent-encoded.</summary>
    public static string ProvisioningUri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Contains(':')) throw new ArgumentException("The issuer must be non-empty and must not contain ':'.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("The account must not be empty.", nameof(account));
        var i = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{i}:{Uri.EscapeDataString(account)}?secret={ToBase32(secret)}&issuer={i}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
