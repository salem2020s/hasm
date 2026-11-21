using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Hasm.Api.Auth;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hasm.Api.Tests;

/// <summary>
/// ONE real-Kestrel harness for the tests that need the framework's real request handling (the in-memory test server enforces neither the request-size limit nor the real order of the pipeline): the real application on a
/// free loopback port (<c>UseKestrel(0)</c>) and a raw-socket POST, declared or chunked, that returns the status code of the answer. Optionally the session authentication is replaced by a handler that admits every
/// request with a session that satisfies the default MFA policy (pass-through state), so a request reaches an endpoint behind authorization, where the endpoint's size limit is applied.
/// </summary>
internal sealed class RealKestrelHost : IDisposable
{
    private sealed class AdmitEveryone(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            // the default policy asks for the session the real handler stores (MFA state None: pass-through, D-161)
            Context.Items[AuthDefaults.SessionItemKey] = new AuthenticatedSession(Guid.NewGuid(), userId, "probe", "probe", false, SessionClient.Web, now.AddHours(1), now.AddHours(8));
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private readonly WebApplicationFactory<Program> factory;

    public string Host { get; }
    public int Port { get; }

    public RealKestrelHost(bool admitEveryone = false)
    {
        factory = admitEveryone
            ? new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.Configure<AuthenticationOptions>(o => o.Schemes.Single(s => s.Name == AuthDefaults.Scheme).HandlerType = typeof(AdmitEveryone))))
            : new WebApplicationFactory<Program>();
        factory.UseKestrel(0);                                                                                  // a real Kestrel server on a free loopback port
        factory.StartServer();
        using var probe = factory.CreateClient();
        Host = probe.BaseAddress!.Host;
        Port = probe.BaseAddress.Port;
    }

    public void Dispose() => factory.Dispose();

    /// <summary>Sends one raw HTTP/1.1 POST (JSON, with the anti-CSRF header) and returns the status code of the answer. A chunked body is sent in 1 KiB chunks.</summary>
    public async Task<int> PostStatus(string path, string body, bool chunked = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Host, Port);
        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"POST {path} HTTP/1.1\r\n").Append($"Host: {Host}:{Port}\r\n").Append("Content-Type: application/json\r\n").Append($"{AuthDefaults.RequestHeader}: 1\r\nConnection: close\r\n");
        head.Append(chunked ? "Transfer-Encoding: chunked\r\n\r\n" : $"Content-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        try
        {
            if (chunked)
            {
                for (var i = 0; i < payload.Length; i += 1024)
                {
                    var n = Math.Min(1024, payload.Length - i);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"));
                    await stream.WriteAsync(payload.AsMemory(i, n));
                    await stream.WriteAsync("\r\n"u8.ToArray());
                }
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            else await stream.WriteAsync(payload);
        }
        catch (IOException) { /* the server may answer and close before the whole body is sent: its answer is still on the socket */ }
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();
        return int.Parse(statusLine!.Split(' ')[1]);
    }
}
