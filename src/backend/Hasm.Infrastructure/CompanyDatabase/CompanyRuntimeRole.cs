using System.Security.Cryptography;
using System.Text;

namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>
/// The PostgreSQL login role the API uses for ONE company database. Each company has its own role, so a credential that works for
/// company A is rejected by company B's database by PostgreSQL itself (CONNECT is granted on exactly one database and revoked from PUBLIC).
/// The role name is derived from the opaque physical database name through a one-way hash: deterministic (no registry column is
/// needed), not reversible to the database name, and carries no business meaning. Roles are cluster-wide objects; the
/// <c>hasm_rt_</c> prefix lets an operator recognise (and never confuse) them.
/// </summary>
public static class CompanyRuntimeRole
{
    public const string Prefix = "hasm_rt_";

    public static string NameFor(string physicalDatabaseName)
    {
        Naming.PhysicalIdentifier.Validate(physicalDatabaseName, nameof(physicalDatabaseName));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("hasm-company-runtime-role/" + physicalDatabaseName));
        return Prefix + Convert.ToHexString(digest)[..32].ToLowerInvariant();
    }

    public static bool IsRuntimeRoleName(string? name) =>
        name is not null && name.Length == Prefix.Length + 32 && name.StartsWith(Prefix, StringComparison.Ordinal)
        && name.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>A new random password: 256 bits from a CSPRNG, URL-safe characters only. Never derived from anything.</summary>
    public static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// SCRAM-SHA-256 verifier (RFC 5802 / 7677, as stored by PostgreSQL) for <paramref name="password"/>. Passing the verifier instead of
    /// the plaintext to CREATE/ALTER ROLE keeps the password out of the server statement log and out of pg_stat_activity.
    /// Composition of .NET primitives only (PBKDF2, HMAC, SHA-256); no custom cryptography.
    /// </summary>
    public static string ScramVerifier(string password, byte[]? salt = null, int iterations = 4096)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
