namespace Hasm.Api.Tests;

/// <summary>
/// The request-size limit of a route is enforced by the REAL Kestrel server, not only declared as metadata: the real application is hosted on a loopback Kestrel port (the shared <see cref="RealKestrelHost"/>; the
/// in-memory test server does not enforce the limit), the session authentication is replaced by a handler that admits every request with a pass-through session (so the request reaches the endpoint, where the limit is
/// applied after authorization), and a raw HTTP request one byte over the limit of a newly limited route is answered 413, while a body exactly at the limit is not.
/// <c>POST /stock-documents/{id}/reject</c> had no limit metadata (the 30 MB default applied), then the 512 KiB limit of its class, and now carries the 16 KiB limit of action routes.
/// </summary>
public sealed class RequestSizeLimitKestrelTests(RequestSizeLimitKestrelTests.AdmittedServer admitted) : IClassFixture<RequestSizeLimitKestrelTests.AdmittedServer>
{
    /// <summary>One admitted real-Kestrel host for the whole class (the tests only send requests; nothing is stored).</summary>
    public sealed class AdmittedServer : IDisposable
    {
        internal RealKestrelHost Host { get; } = new(admitEveryone: true);
        public void Dispose() => Host.Dispose();
    }

    private readonly RealKestrelHost server = admitted.Host;

    private const int Limit = 16 * 1024;     // the limit of every action route (RequestLimits.Action)
    /// <summary>Action routes of five endpoint classes: the reject of the first proof and the body-binding actions that FG's review of PR 184 (L1) found at document-sized limits.</summary>
    public static TheoryData<string> ActionRoutes => new()
    {
        "/api/v1/company/stock-documents/{0}/reject",
        "/api/v1/company/stock-documents/{0}/reversal",
        "/api/v1/company/stock-adjustments/{0}/reversal",
        "/api/v1/company/journal/{0}/decision",
        "/api/v1/company/journal/{0}/reverse",
        "/api/v1/company/commercial-documents/links",
        "/api/v1/company/attachment-retention/policies/{0}/approve",
        "/api/v1/company/attachment-retention/policies/{0}/revoke",
        "/api/v1/company/attachment-retention/holds/{0}/release",
    };

    private static string Path(string route) => string.Format(route, Guid.NewGuid());

    /// <summary>A JSON body of exactly <paramref name="bytes"/> bytes.</summary>
    private static string Body(int bytes)
    {
        const string prefix = "{\"note\":\"", suffix = "\"}";
        return prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix;
    }

    [Theory, MemberData(nameof(ActionRoutes))]
    public async Task One_byte_over_the_limit_of_a_newly_limited_route_is_refused_413_by_Kestrel(string route)
    {
        Assert.Equal(413, await server.PostStatus(Path(route), Body(Limit + 1)));
    }

    [Theory, MemberData(nameof(ActionRoutes))]
    public async Task A_body_exactly_at_the_limit_is_not_refused_for_its_size(string route)
    {
        // it passes the size gate (the endpoint then answers something else: no company database exists here), which proves the limit is the exact number and not a lower hidden one
        Assert.NotEqual(413, await server.PostStatus(Path(route), Body(Limit)));
    }
}
