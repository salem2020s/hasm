using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Parties;

/// <summary>One page of party roles with the number of rows that match the filter (before paging).</summary>
public sealed record PartyRolePage(IReadOnlyList<PartyRoleView> Items, long Total);

/// <summary>The party-role list's names for the shared <see cref="ListPaging"/> rules (kept so existing callers and tests keep their names).</summary>
public static class PartyRoleSearch
{
    public const int DefaultLimit = ListPaging.DefaultLimit;
    public const int MaxLimit = ListPaging.MaxLimit;
    public const int MaxSearchLength = ListPaging.MaxSearchLength;
    public const int MaxIds = ListPaging.MaxIds;

    public static bool TryCleanSearch(string? search, out string? cleaned) => ListPaging.TryCleanSearch(search, out cleaned);
    /// <summary>The literal LIKE pattern; the case is folded by PostgreSQL on both sides (<see cref="ListPaging.LikeClause"/>).</summary>
    public static string LikePattern(string cleaned) => ListPaging.LikePattern(cleaned);
    public static int ClampLimit(int? limit) => ListPaging.ClampLimit(limit);
    public static int ClampOffset(int? offset) => ListPaging.ClampOffset(offset);
}
