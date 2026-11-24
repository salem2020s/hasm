namespace Hasm.Api.Tests;

/// <summary>
/// Review L4 (iv) of PR 247: the request-size limits of the cash voucher routes are ENFORCED by the real Kestrel server, not only declared (the in-memory test server answers 400 and does not show it). The real
/// application is hosted on a loopback Kestrel port with every request admitted (<see cref="RealKestrelHost"/>), so a request reaches the endpoint, where the limit applies: create and update carry the 512 KiB
/// limit of a document body, submit and cancel the 16 KiB limit of action routes (<c>RequestLimits.Action</c>). One byte over the limit is answered 413; a body exactly at the limit is not refused for its size.
/// </summary>
public sealed class CashVoucherRequestSizeKestrelTests(CashVoucherRequestSizeKestrelTests.AdmittedServer admitted) : IClassFixture<CashVoucherRequestSizeKestrelTests.AdmittedServer>
{
    public sealed class AdmittedServer : IDisposable
    {
        internal RealKestrelHost Host { get; } = new(admitEveryone: true);
        public void Dispose() => Host.Dispose();
    }

    private readonly RealKestrelHost server = admitted.Host;
    private const string Root = "/api/v1/company/cash-vouchers";
    private const int Document = 512 * 1024, Action = 16 * 1024;

    /// <summary>Every body-accepting voucher route with its method and its limit (there is no other one: the route list is pinned by <c>CashVoucherUnitTests</c>).</summary>
    public static TheoryData<string, string, int> Routes => new()
    {
        { "POST", Root, Document },
        { "PUT", Root + "/{0}", Document },
        { "POST", Root + "/{0}/submit", Action },
        { "POST", Root + "/{0}/cancel", Action },
    };

    private static string Path(string route) => string.Format(route, Guid.NewGuid());

    /// <summary>A JSON body of exactly <paramref name="bytes"/> bytes.</summary>
    private static string Body(int bytes)
    {
        const string prefix = "{\"note\":\"", suffix = "\"}";
        return prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix;
    }

    [Theory, MemberData(nameof(Routes))]
    public async Task One_byte_over_the_limit_of_a_voucher_route_is_refused_413_by_Kestrel(string method, string route, int limit)
    {
        Assert.Equal(413, await server.PostStatus(Path(route), Body(limit + 1), method: method));
        Assert.Equal(413, await server.PostStatus(Path(route), Body(limit + 1), chunked: true, method: method));   // also without a declared length
    }

    [Theory, MemberData(nameof(Routes))]
    public async Task A_body_exactly_at_the_limit_of_a_voucher_route_is_not_refused_for_its_size(string method, string route, int limit)
    {
        // it passes the size gate (the endpoint then answers something else: no company database exists here), which proves the limit is the exact number and not a lower hidden one
        Assert.NotEqual(413, await server.PostStatus(Path(route), Body(limit), method: method));
    }
}
