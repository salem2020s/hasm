using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Api.Tests;

/// <summary>
/// Release 1 hardening: every endpoint that can receive a request body carries an EXPLICIT request-size limit (<see cref="IRequestSizeLimitMetadata"/>), so a new route can never silently fall back to Kestrel's
/// 30 MB default. A body-accepting endpoint is one that binds a request body (<see cref="IAcceptsMetadata"/>) or that is mapped for POST, PUT or PATCH. The only exceptions are listed below with their reason.
/// </summary>
public class RequestSizeLimitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Body-accepting routes that do not carry the metadata, each justified (and each must still be a real route).</summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /api/v1/company/attachments/"] = "AttachmentEndpoints.Upload: the documented upload limit (the configured maximum file size plus the form overhead) is set per request in the handler, after the upload permission is checked and before the body is read",
    };

    /// <summary>Routes whose documented limit is above 1 MiB although they are neither uploads nor imports.</summary>
    private static readonly Dictionary<string, string> LargeLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PUT /api/v1/company/statements/mapping"] = "FinancialStatementEndpoints: the documented limit of the statement mapping (2 MiB), unchanged by this test",
    };

    private static readonly string[] BodyMethods = ["POST", "PUT", "PATCH"];

    private IReadOnlyList<(string Method, string Pattern, RouteEndpoint Endpoint)> BodyRoutes() =>
        factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                var binds = e.Metadata.GetMetadata<IAcceptsMetadata>() is not null;
                return methods.Where(m => binds || BodyMethods.Contains(m.ToUpperInvariant()))
                    .Select(m => (m.ToUpperInvariant(), "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'), e));
            })
            .OrderBy(r => r.Item1 + " " + r.Item2, StringComparer.Ordinal).ToList();

    private static long? Limit(RouteEndpoint e) => e.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

    [Fact]
    public void The_enumeration_finds_the_body_accepting_routes_and_the_exception_list_has_no_stale_entries()
    {
        var routes = BodyRoutes();
        Assert.True(routes.Count > 100, $"Only {routes.Count} body-accepting routes discovered; the enumeration is broken.");
        var keys = routes.Select(r => $"{r.Method} {r.Pattern}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(Exceptions.Keys.All(keys.Contains), "stale exception entries: " + string.Join(", ", Exceptions.Keys.Where(k => !keys.Contains(k))));
        Assert.True(Exceptions.Values.All(v => !string.IsNullOrWhiteSpace(v)), "every exception needs a reason");
    }

    [Fact]
    public void Every_body_accepting_route_carries_an_explicit_request_size_limit()
    {
        var missing = BodyRoutes().Where(r => Limit(r.Endpoint) is null && !Exceptions.ContainsKey($"{r.Method} {r.Pattern}")).Select(r => $"{r.Method} {r.Pattern}").ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} body-accepting routes fall back to the 30 MB default (add .WithMetadata(new BodyLimit()) with the limit of their category):\n" + string.Join("\n", missing));
    }

    /// <summary>The largest request body a route WITHOUT a document payload may be given: a state transition or a small command carries at most a short reason or note and a few ids (G review L1 of PR 177, FG review L1 of PR 184). Import actions keep the smaller limit of their class.</summary>
    private const long ActionLimit = 16 * 1024;
    private const long ImportActionLimit = 4 * 1024;
    /// <summary>The ceiling of a route listed in <see cref="LargerScalarBodies"/>: the master-data class limit. A body without any collection is never document-sized.</summary>
    private const long ScalarBodyCeiling = 64 * 1024;

    private const string MasterData = "master-data create with several free-text fields (codes, Arabic and English names, notes): keeps the 64 KiB limit of its class, the same as its PUT; not a state transition";

    /// <summary>
    /// The ONLY routes whose body has no collection and that may still exceed <see cref="ActionLimit"/> (never <see cref="ScalarBodyCeiling"/>), each with its reason. A new route is not here, so it is held to the action
    /// limit by default whatever its verb; adding an entry is a visible, reviewed decision. Entries must be real routes, must really bind a collection-free body and must really be above the action limit.
    /// </summary>
    private static readonly Dictionary<string, string> LargerScalarBodies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /api/v1/company/attachment-retention/holds"] = "legal-hold create (scope, reason, reference fields): keeps the 64 KiB limit of its class; not a state transition",
        ["POST /api/v1/company/inventory/groups"] = MasterData,
        ["POST /api/v1/company/inventory/units"] = MasterData,
        ["POST /api/v1/company/inventory/items/{id:guid}/barcodes"] = "sub-record create of the inventory master data: keeps the 64 KiB limit of its class",
        ["POST /api/v1/company/parties/"] = MasterData,
        ["POST /api/v1/company/parties/{id:guid}/propose"] = "shared-identity proposal: two legal names of 200 characters and a note of 500, compared AFTER Unicode composition, so a decomposed and fully escaped legitimate body can reach just over 16 KiB (900 characters x 3 code units x 6 bytes), above the action limit; keeps the 64 KiB limit of its class",
        ["POST /api/v1/company/party-roles/"] = MasterData,
        ["POST /api/v1/company/pricing/customer-groups"] = MasterData,
        ["POST /api/v1/company/pricing/discounts"] = MasterData,
        ["POST /api/v1/company/pricing/policy"] = "pricing policy version (configuration create): keeps the 64 KiB limit of its class",
        ["POST /api/v1/company/pricing/price-lists"] = MasterData,
        ["POST /api/v1/company/pricing/price-lists/{id:guid}/entries"] = "sub-record create of the pricing master data: keeps the 64 KiB limit of its class",
        ["POST /api/v1/company/pricing/quote"] = "price computation request of the pricing class: keeps the 64 KiB limit of its class",
        ["POST /api/v1/company/sales-representatives/"] = MasterData,
        ["POST /api/v1/company/taxes/"] = MasterData,
        ["POST /api/v1/company/taxes/classes"] = MasterData,
        ["POST /api/v1/company/taxes/policy"] = "tax policy version (configuration create): keeps the 64 KiB limit of its class",
        ["POST /api/v1/company/taxes/{id:guid}/rates"] = "sub-record create of the tax master data: keeps the 64 KiB limit of its class",
    };

    /// <summary>Routes that are neither uploads nor imports of content: the import upload and the attachment upload read their documented file limits.</summary>
    private static bool IsContentUpload(string method, string pattern) =>
        method == "POST" && (pattern is "/api/v1/company/imports/" or "/api/v1/company/attachments/");

    private static readonly HashSet<Type> ScalarTypes = [typeof(string), typeof(decimal), typeof(Guid), typeof(DateOnly), typeof(TimeOnly), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan)];

    /// <summary>
    /// True when a bound request type can carry NO document payload: every member, recursively, is a scalar (text, number, boolean, enum, id, date). A collection of any kind (array, list, dictionary), an open JSON
    /// value (<c>JsonElement</c>, <c>JsonNode</c>, <c>object</c>), any other framework type and a type that contains itself make it a document type. Unknown shapes are therefore NOT collection-free, and such a route
    /// is then held to the limits of the other tests only; what the rule below must not miss is a small body, and a small body can only be made of the scalars listed here.
    /// </summary>
    internal static bool IsCollectionFree(Type type) => IsCollectionFree(type, []);

    private static bool IsCollectionFree(Type type, HashSet<Type> path)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsPrimitive || t.IsEnum || ScalarTypes.Contains(t)) return true;
        if (t == typeof(object) || typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return false;
        if (t.Namespace is null || t.Namespace == "System" || t.Namespace.StartsWith("System.", StringComparison.Ordinal) || t.Namespace.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;
        if (!path.Add(t)) return false;
        var free = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).All(p => IsCollectionFree(p.PropertyType, path));
        path.Remove(t);
        return free;
    }

    /// <summary>
    /// A SMALL-BODY route, judged by what it binds and never by the words of its path (so no verb, no trailing slash and no route shape can evade it; FG review L1 and L2 of PR 184):
    /// a POST, PUT or PATCH that binds no request body at all (a state transition, a maintenance run), or a POST whose bound request type is collection-free (<see cref="IsCollectionFree(Type)"/>: a decision, a reversal,
    /// a reason, a link of two ids and a quantity). PUT and PATCH with a bound body are the updates of a record and keep the limit of their class.
    /// </summary>
    private List<(string Method, string Pattern, RouteEndpoint Endpoint)> SmallBodyRoutes() =>
        BodyRoutes().Where(r => !IsContentUpload(r.Method, r.Pattern) && r.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>() switch
        {
            null => true,
            { RequestType: { } bound } => r.Method == "POST" && IsCollectionFree(bound),
            _ => false,
        }).ToList();

    private static long SmallLimit(string pattern) => pattern.StartsWith("/api/v1/company/imports", StringComparison.OrdinalIgnoreCase) ? ImportActionLimit : ActionLimit;

    /// <summary>
    /// DEFAULT DENY: every small-body route carries at most <see cref="ActionLimit"/> (<see cref="ImportActionLimit"/> under the imports) unless it is listed, with its reason, in <see cref="LargerScalarBodies"/>, and a
    /// listed route carries at most <see cref="ScalarBodyCeiling"/>. A new action with any verb (<c>/decline</c>, <c>/void</c>, <c>/reversal</c>) and a note therefore cannot take the document-sized limit of its class.
    /// </summary>
    [Fact]
    public void Small_body_routes_carry_the_action_limit_so_a_state_transition_cannot_be_given_a_document_sized_body()
    {
        var small = SmallBodyRoutes();
        Assert.True(small.Count > 110, $"Only {small.Count} small-body routes found; the rule is not applied to the real set.");
        Assert.Contains(small, r => r.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is null);
        Assert.Contains(small, r => r.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is not null);
        var tooLarge = small.Where(r => Limit(r.Endpoint) is { } l && l > (LargerScalarBodies.ContainsKey($"{r.Method} {r.Pattern}") ? ScalarBodyCeiling : SmallLimit(r.Pattern)))
            .Select(r => $"{r.Method} {r.Pattern} = {Limit(r.Endpoint)}").ToList();
        Assert.True(tooLarge.Count == 0, $"{tooLarge.Count} routes without a document payload carry a limit above {ActionLimit} bytes ({ImportActionLimit} under the imports; {ScalarBodyCeiling} when listed in LargerScalarBodies). " +
            "Use .WithMetadata(RequestLimits.Action):\n" + string.Join("\n", tooLarge));
    }

    [Fact]
    public void The_list_of_larger_scalar_bodies_has_only_real_justified_entries_that_need_it()
    {
        var small = SmallBodyRoutes().ToDictionary(r => $"{r.Method} {r.Pattern}", r => r, StringComparer.OrdinalIgnoreCase);
        var stale = LargerScalarBodies.Keys.Where(k => !small.ContainsKey(k)).ToList();
        Assert.True(stale.Count == 0, "entries that are not small-body routes (removed, renamed, or now binding a collection):\n" + string.Join("\n", stale));
        var needless = LargerScalarBodies.Keys.Where(k => Limit(small[k].Endpoint) is not { } l || l <= SmallLimit(small[k].Pattern)).ToList();
        Assert.True(needless.Count == 0, "entries whose route is already within the action limit (remove them):\n" + string.Join("\n", needless));
        Assert.True(LargerScalarBodies.Values.All(v => !string.IsNullOrWhiteSpace(v)), "every entry needs a reason");
    }

    /// <summary>The routes FG's review named (L1 of PR 184) are small-body routes and carry exactly the action limit, and the shape test tells a scalar body from a document.</summary>
    [Theory]
    [InlineData("/api/v1/company/journal/{id:guid}/decision")]
    [InlineData("/api/v1/company/journal/{id:guid}/reverse")]
    [InlineData("/api/v1/company/stock-adjustments/{id:guid}/reversal")]
    [InlineData("/api/v1/company/stock-documents/{id:guid}/reversal")]
    [InlineData("/api/v1/company/commercial-documents/links")]
    [InlineData("/api/v1/company/attachment-retention/policies/{id:guid}/approve")]
    [InlineData("/api/v1/company/attachment-retention/policies/{id:guid}/revoke")]
    [InlineData("/api/v1/company/attachment-retention/holds/{id:guid}/release")]
    public void A_body_binding_action_with_any_verb_is_a_small_body_route_at_the_action_limit(string pattern)
    {
        var route = Assert.Single(SmallBodyRoutes(), r => r.Method == "POST" && r.Pattern == pattern);
        Assert.NotNull(route.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>());
        Assert.Equal(ActionLimit, Limit(route.Endpoint));
    }

    private sealed record ScalarBody(Guid? Id, int? Kind, decimal Quantity, string? Note, DateOnly? On, ScalarInner? Inner);
    private sealed record ScalarInner(bool Flag, string? Text);
    private sealed record ListBody(string? Note, List<int>? Lines);
    private sealed record ArrayInside(string? Note, ArrayInner? Inner);
    private sealed record ArrayInner(Guid[] Ids);
    private sealed record OpenBody(string? Note, System.Text.Json.JsonElement Extra);
    private sealed record SelfBody(string? Note, SelfBody? Next);

    [Fact]
    public void Only_a_type_made_of_scalars_is_collection_free()
    {
        Assert.True(IsCollectionFree(typeof(ScalarBody)));
        Assert.False(IsCollectionFree(typeof(ListBody)));
        Assert.False(IsCollectionFree(typeof(ArrayInside)));
        Assert.False(IsCollectionFree(typeof(OpenBody)));
        Assert.False(IsCollectionFree(typeof(SelfBody)));
        Assert.False(IsCollectionFree(typeof(Dictionary<string, string>)));
        Assert.False(IsCollectionFree(typeof(System.Text.Json.Nodes.JsonNode)));
        Assert.False(IsCollectionFree(typeof(object)));
    }

    [Fact]
    public void No_limit_is_larger_than_the_documented_maximum_of_the_categories()
    {
        // the largest documented limit of a JSON command is 1 MiB; only uploads and imports (listed by their prefix) may exceed it
        var tooLarge = BodyRoutes().Where(r => Limit(r.Endpoint) is { } l && l > 1024 * 1024 && !r.Pattern.Contains("/import") && !r.Pattern.Contains("/attachments") && !LargeLimits.ContainsKey($"{r.Method} {r.Pattern}"))
            .Select(r => $"{r.Method} {r.Pattern} = {Limit(r.Endpoint)}").ToList();
        Assert.True(tooLarge.Count == 0, "limits above 1 MiB outside uploads and imports:\n" + string.Join("\n", tooLarge));
    }
}
