using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// Login identifier rules. Normalization: trim, Unicode NFKC, invariant lower-casing. The result must match
/// <c>[a-z0-9][a-z0-9._@-]{2,63}</c> (ASCII only, so look-alike Unicode identifiers cannot be registered).
/// Uniqueness is enforced on the normalized form. The same rule is a database CHECK constraint.
/// </summary>
public static partial class LoginName
{
    public const int MaxInputLength = 256;

    [GeneratedRegex("^[a-z0-9][a-z0-9._@-]{2,63}$")]
    private static partial Regex Valid();

    /// <summary>Normalizes and validates; on failure <paramref name="normalized"/> is empty.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxInputLength) return false;
        string candidate;
        try { candidate = input.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
        catch (ArgumentException) { return false; } // malformed surrogate sequences
        if (!Valid().IsMatch(candidate)) return false;
        normalized = candidate;
        return true;
    }
}
