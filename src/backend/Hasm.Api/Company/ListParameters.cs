using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Api.Company;

/// <summary>
/// The optional <c>search</c>, <c>offset</c>, <c>limit</c> (and, for party roles, <c>kind</c> and <c>ids</c>) query parameters of the master-data list endpoints, bound as STRINGS and validated here, so a
/// non-numeric or malformed value answers the standard <c>invalid_request</c> validation body naming the offending parameter instead of the framework's bare 400. Only these list endpoints use it;
/// the error shape of every other endpoint is unchanged. An empty <c>offset</c> or <c>limit</c> counts as absent, exactly as the typed binding did.
/// </summary>
public static class ListParameters
{
    public sealed record Parsed(string? Search, int? Offset, int? Limit)
    {
        /// <summary>True when the paged form is requested at all.</summary>
        public bool Requested => ListPaging.IsRequested(Search, Offset, Limit);
    }

    /// <summary>
    /// The standard validation refusal, the same shape as every other validation error (so the client's field-error handling works): 400
    /// <c>{ "error": "invalid_request", "fields": [ { "field": name, "code": "invalid_value" } ] }</c>.
    /// </summary>
    public static IResult Invalid(string field) => Results.Json(new { error = "invalid_request", fields = new[] { new { field, code = "invalid_value" } } }, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryInt(string? raw, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;                                   // absent
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        value = v;
        return true;
    }

    /// <summary>Returns the name of the first bad parameter, or null when all are fine.</summary>
    public static string? TryParse(string? search, string? offset, string? limit, out Parsed parsed)
    {
        parsed = new Parsed(search, null, null);
        if (!ListPaging.TryCleanSearch(search, out _)) return "search";
        if (!TryInt(offset, out var o) || o is < 0) return "offset";
        if (!TryInt(limit, out var l) || l is < 1) return "limit";
        parsed = new Parsed(search, o, l);
        return null;
    }

    /// <summary>The party-role <c>kind</c>: absent, 1 (customer) or 2 (supplier); anything else is bad.</summary>
    public static string? TryParseKind(string? raw, out short? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!short.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var k) || k is not (1 or 2)) return "kind";
        kind = k;
        return null;
    }

    /// <summary>
    /// The repeated party-role <c>ids</c>: every value must be a GUID and there may be at most <see cref="ListPaging.MaxIds"/>. The framework binds an ABSENT repeated parameter as an empty
    /// array; "no ids named" is the unfiltered list, so an empty array yields null.
    /// </summary>
    public static string? TryParseIds(string[]? raw, out Guid[]? ids)
    {
        ids = null;
        if (raw is null || raw.Length == 0) return null;
        if (raw.Length > ListPaging.MaxIds) return "ids";
        var list = new Guid[raw.Length];
        for (var i = 0; i < raw.Length; i++) if (!Guid.TryParse(raw[i], out list[i])) return "ids";
        ids = list;
        return null;
    }
}
