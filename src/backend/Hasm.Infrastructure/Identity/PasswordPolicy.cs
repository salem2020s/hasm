namespace Hasm.Infrastructure.Identity;

/// <summary>
/// Password acceptance rules (length based, no composition rules). The upper bound limits hashing cost.
/// Violations are returned as short codes; the password is never echoed.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public static IReadOnlyList<string> Validate(string? password, string? normalizedLogin = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password)) { problems.Add("too_short"); return problems; }
        if (password.Length < MinLength) problems.Add("too_short");
        if (password.Length > MaxLength) problems.Add("too_long");
        if (password.Distinct().Count() < 4) problems.Add("too_repetitive");
        if (normalizedLogin is not null && password.Trim().Equals(normalizedLogin, StringComparison.OrdinalIgnoreCase))
            problems.Add("same_as_login");
        return problems;
    }
}
