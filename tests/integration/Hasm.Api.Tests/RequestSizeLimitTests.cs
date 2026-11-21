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

    [Fact]
    public void No_limit_is_larger_than_the_documented_maximum_of_the_categories()
    {
        // the largest documented limit of a JSON command is 1 MiB; only uploads and imports (listed by their prefix) may exceed it
        var tooLarge = BodyRoutes().Where(r => Limit(r.Endpoint) is { } l && l > 1024 * 1024 && !r.Pattern.Contains("/import") && !r.Pattern.Contains("/attachments") && !LargeLimits.ContainsKey($"{r.Method} {r.Pattern}"))
            .Select(r => $"{r.Method} {r.Pattern} = {Limit(r.Endpoint)}").ToList();
        Assert.True(tooLarge.Count == 0, "limits above 1 MiB outside uploads and imports:\n" + string.Join("\n", tooLarge));
    }
}
