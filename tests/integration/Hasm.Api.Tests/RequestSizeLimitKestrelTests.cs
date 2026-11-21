using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Hasm.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hasm.Api.Tests;

/// <summary>
/// The request-size limit of a route is enforced by the REAL Kestrel server, not only declared as metadata: the real application is hosted on a loopback Kestrel port (the in-memory test server does not enforce
/// the limit), the session authentication is replaced by a handler that admits every request (so the request reaches the endpoint, where the limit is applied), and a raw HTTP request one byte over the limit of a
/// newly limited route is answered 413, while a body exactly at the limit is not. <c>POST /stock-documents/{id}/reject</c> had no limit metadata (the 30 MB default applied) and now carries the 512 KiB limit of its class.
/// </summary>
public sealed class RequestSizeLimitKestrelTests : IDisposable
{
    private sealed class AdmitEveryone(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private readonly WebApplicationFactory<Program> factory;

    public RequestSizeLimitKestrelTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Configure<AuthenticationOptions>(o => o.Schemes.Single(s => s.Name == AuthDefaults.Scheme).HandlerType = typeof(AdmitEveryone))));
        factory.UseKestrel(0);
        factory.StartServer();
    }

    public void Dispose() => factory.Dispose();

    /// <summary>Sends one raw HTTP/1.1 request with a JSON body of exactly <paramref name="bodyBytes"/> bytes and returns the status code of the answer.</summary>
    private async Task<int> StatusOfPost(string path, int bodyBytes)
    {
        var address = new Uri(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);
        await using var stream = tcp.GetStream();
        // {"note":"xxx...x"} padded to exactly bodyBytes
        var prefix = "{\"note\":\""; var suffix = "\"}";
        var body = prefix + new string('x', bodyBytes - prefix.Length - suffix.Length) + suffix;
        var head = $"POST {path} HTTP/1.1\r\nHost: localhost\r\n{AuthDefaults.RequestHeader}: 1\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(head + body);
        await stream.WriteAsync(bytes);
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);
        var line = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];          // HTTP/1.1 413 Payload Too Large
        return int.Parse(line.Split(' ')[1]);
    }

    private const int Limit = 512 * 1024;     // the limit of StockDocumentEndpoints, now also on the action routes
    private static string Reject => $"/api/v1/company/stock-documents/{Guid.NewGuid()}/reject";

    [Fact]
    public async Task One_byte_over_the_limit_of_a_newly_limited_route_is_refused_413_by_Kestrel()
    {
        Assert.Equal(413, await StatusOfPost(Reject, Limit + 1));
    }

    [Fact]
    public async Task A_body_exactly_at_the_limit_is_not_refused_for_its_size()
    {
        // it passes the size gate (the endpoint then answers something else: no company database exists here), which proves the limit is the exact number and not a lower hidden one
        Assert.NotEqual(413, await StatusOfPost(Reject, Limit));
    }
}
