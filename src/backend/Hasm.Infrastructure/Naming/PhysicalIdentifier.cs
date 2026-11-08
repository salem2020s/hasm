using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Naming;

/// <summary>
/// Rules for physical PostgreSQL identifiers (database, table, column, constraint names).
/// Physical names are opaque and carry no business meaning; this is NOT a security control.
/// </summary>
public static partial class PhysicalIdentifier
{
    /// <summary>PostgreSQL truncates identifiers longer than 63 bytes.</summary>
    public const int MaxLength = 63;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(value)) { error = "Identifier is empty."; return false; }
        if (value.Length > MaxLength) { error = $"Identifier exceeds {MaxLength} characters."; return false; }
        if (!Pattern().IsMatch(value)) { error = "Identifier must match ^[a-z][a-z0-9_]*$."; return false; }
        if (value.StartsWith("pg_", StringComparison.Ordinal)) { error = "Identifier must not use the reserved 'pg_' prefix."; return false; }
        return true;
    }

    public static bool IsValid(string? value) => IsValid(value, out _);

    public static string Validate(string? value, string parameterName)
    {
        if (!IsValid(value, out var error))
            throw new ArgumentException(error, parameterName);
        return value!;
    }

    /// <summary>
    /// Generates a new opaque database name: 'd' + 32 hex chars from a CSPRNG.
    /// Never derived from company names, tax numbers or any customer data.
    /// Generated once per company and then persisted; never regenerated.
    /// </summary>
    public static string GenerateDatabaseName() =>
        "d" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
