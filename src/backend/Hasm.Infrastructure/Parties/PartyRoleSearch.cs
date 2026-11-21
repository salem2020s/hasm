namespace Hasm.Infrastructure.Parties;

/// <summary>One page of party roles with the number of rows that match the filter (before paging).</summary>
public sealed record PartyRolePage(IReadOnlyList<PartyRoleView> Items, long Total);

/// <summary>
/// The optional search and paging of the party-role list (backward compatible: a list without these parameters is unchanged). Pure helpers, unit tested:
/// the search text is trimmed and bounded, matched case-insensitively against the party code and name as a literal substring (the LIKE wildcards are escaped),
/// and the page size is clamped.
/// </summary>
public static class PartyRoleSearch
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxSearchLength = 100;

    /// <summary>Trimmed search text, or null when empty. Longer than <see cref="MaxSearchLength"/> is invalid (the caller answers 400).</summary>
    public static bool TryCleanSearch(string? search, out string? cleaned)
    {
        cleaned = null;
        var t = search?.Trim();
        if (string.IsNullOrEmpty(t)) return true;
        if (t.Length > MaxSearchLength || t.Any(char.IsControl)) return false;
        cleaned = t;
        return true;
    }

    /// <summary>The LIKE pattern of a literal, case-insensitive substring match (use with <c>upper(column) LIKE pattern</c>).</summary>
    public static string LikePattern(string cleaned) => "%" + cleaned.ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
    public static int ClampOffset(int? offset) => Math.Max(0, offset ?? 0);
}
