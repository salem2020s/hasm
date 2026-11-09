using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// Opaque session tokens: 256 bits from the OS CSPRNG, base64url. Only the SHA-256 digest is stored, so a
/// database leak does not yield usable tokens (a fast hash is sufficient for high-entropy secrets).
/// </summary>
public static partial class SessionToken
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex WellFormed();

    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsWellFormed(string? token) => token is not null && WellFormed().IsMatch(token);

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>Digest of an arbitrary throttle key (never stored in clear).</summary>
    public static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
