namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// The shared, optional search and paging of the master-data list endpoints (party roles, units, item groups, customer groups). Backward compatible by design: a list without
/// any of these parameters is unchanged. Pure helpers (unit tested): the search text is trimmed and bounded and matched as a LITERAL substring (the LIKE wildcards are escaped);
/// both sides of the comparison are folded by PostgreSQL itself (<c>upper(column) LIKE upper(@like)</c>), so the database's own case folding decides, never .NET's;
/// the page size is clamped; ordering is by the caller's stable key (the code). All values reach SQL as parameters.
/// </summary>
public static class ListPaging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxSearchLength = 100;
    /// <summary>The most ids one lookup may name (the ids travel in the query string, which has a request-line limit).</summary>
    public const int MaxIds = 100;

    /// <summary>True when the paged/searched form is requested at all (any of the parameters present).</summary>
    public static bool IsRequested(string? search, int? offset, int? limit) => search is not null || offset is not null || limit is not null;

    /// <summary>Trimmed search text, or null when empty. Longer than <see cref="MaxSearchLength"/> or with control characters is invalid (the caller answers 400).</summary>
    public static bool TryCleanSearch(string? search, out string? cleaned)
    {
        cleaned = null;
        var t = search?.Trim();
        if (string.IsNullOrEmpty(t)) return true;
        if (t.Length > MaxSearchLength || t.Any(char.IsControl)) return false;
        cleaned = t;
        return true;
    }

    /// <summary>The LIKE pattern of a literal substring: the wildcards of the text are escaped (backslash is the escape character), the case is NOT changed (use <see cref="LikeClause"/>).</summary>
    public static string LikePattern(string cleaned) => "%" + cleaned.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>
    /// The SQL predicate matching the literal search text in any of the given columns, case-insensitively through PostgreSQL's own <c>upper()</c> on both sides, for example
    /// <c>(upper(p.code) LIKE upper(@like) ESCAPE '\' OR upper(p.name) LIKE upper(@like) ESCAPE '\')</c>. A null column is treated as no match. The caller adds the parameter
    /// <paramref name="parameter"/> with <see cref="LikePattern"/>.
    /// </summary>
    public static string LikeClause(string parameter, params string[] columns) =>
        "(" + string.Join(" OR ", columns.Select(c => $"upper({c}) LIKE upper(@{parameter}) ESCAPE '\\'")) + ")";

    public static int ClampLimit(int? limit, int fallback = DefaultLimit) => Math.Clamp(limit ?? fallback, 1, MaxLimit);
    public static int ClampOffset(int? offset) => Math.Max(0, offset ?? 0);
}
