namespace Hasm.Api.Tests;

/// <summary>
/// The request-size limit of a route is enforced by the REAL Kestrel server, not only declared as metadata: the real application is hosted on a loopback Kestrel port (the shared <see cref="RealKestrelHost"/>; the
/// in-memory test server does not enforce the limit), the session authentication is replaced by a handler that admits every request with a pass-through session (so the request reaches the endpoint, where the limit is
/// applied after authorization), and a raw HTTP request one byte over the limit of a newly limited route is answered 413, while a body exactly at the limit is not.
/// <c>POST /stock-documents/{id}/reject</c> had no limit metadata (the 30 MB default applied), then the 512 KiB limit of its class, and now carries the 16 KiB limit of action routes.
/// </summary>
public sealed class RequestSizeLimitKestrelTests : IDisposable
{
    private readonly RealKestrelHost server = new(admitEveryone: true);

    public void Dispose() => server.Dispose();

    private const int Limit = 16 * 1024;     // the limit of every action route (RequestLimits.Action)
    private static string Reject => $"/api/v1/company/stock-documents/{Guid.NewGuid()}/reject";

    /// <summary>A JSON body of exactly <paramref name="bytes"/> bytes.</summary>
    private static string Body(int bytes)
    {
        const string prefix = "{\"note\":\"", suffix = "\"}";
        return prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix;
    }

    [Fact]
    public async Task One_byte_over_the_limit_of_a_newly_limited_route_is_refused_413_by_Kestrel()
    {
        Assert.Equal(413, await server.PostStatus(Reject, Body(Limit + 1)));
    }

    [Fact]
    public async Task A_body_exactly_at_the_limit_is_not_refused_for_its_size()
    {
        // it passes the size gate (the endpoint then answers something else: no company database exists here), which proves the limit is the exact number and not a lower hidden one
        Assert.NotEqual(413, await server.PostStatus(Reject, Body(Limit)));
    }
}
