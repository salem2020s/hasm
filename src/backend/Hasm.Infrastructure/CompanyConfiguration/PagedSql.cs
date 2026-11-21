using Npgsql;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>SQL helpers of the paged list endpoints (see <see cref="ListPaging"/>).</summary>
public static class PagedSql
{
    /// <summary>
    /// The total of matching rows when a page came back EMPTY with an offset above zero: the window count (<c>count(*) OVER ()</c>) of the page statement needs at least one row,
    /// so an offset beyond the last row asks once more for the total only. <paramref name="fromWhere"/> is the page statement's FROM and WHERE text; the paging parameters (<c>lim</c>, <c>off</c>) are not copied.
    /// </summary>
    public static async Task<long> TotalAsync(NpgsqlConnection conn, string fromWhere, NpgsqlParameterCollection pageParameters, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand($"SELECT count(*) {fromWhere}", conn);
        foreach (NpgsqlParameter p in pageParameters)
            if (p.ParameterName is not ("lim" or "off")) c.Parameters.Add(p.Clone());
        return Convert.ToInt64(await c.ExecuteScalarAsync(ct));
    }
}
