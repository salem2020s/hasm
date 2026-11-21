using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hasm.Api.Tests;

/// <summary>
/// The request-size limit of an endpoint on a REAL Kestrel host (loopback, raw sockets: no test server, no stand-ins), with the NUL guard in the pipeline. The login endpoint accepts at most 8 KiB (8192 bytes). A body of one byte over is
/// refused with 413, declared or chunked; a body of exactly the limit reaches the handler (no database is configured, so the handler answers 503, which is NOT 413); and the NUL guard still answers 400 for a NUL inside a body that fits
/// and never changes the 413 for a body that does not. This is what the unit-level guard tests cannot show: the framework's real order and the real refusal.
/// </summary>
public sealed class RealKestrelBodyLimitTests : IDisposable
{
    private const int Limit = 8 * 1024;
    private readonly WebApplicationFactory<Program> factory;
    private readonly string host;
    private readonly int port;

    public RealKestrelBodyLimitTests()
    {
        factory = new WebApplicationFactory<Program>();
        factory.UseKestrel(0);                                                                                  // a real Kestrel server on a free loopback port
        factory.StartServer();
        using var probe = factory.CreateClient();
        host = probe.BaseAddress!.Host;
        port = probe.BaseAddress.Port;
    }

    public void Dispose() => factory.Dispose();

    /// <summary>A JSON login body of exactly <paramref name="bytes"/> bytes (the padding is a long login name).</summary>
    private static string Body(int bytes, string filler = "a")
    {
        const string prefix = "{\"login\":\"", suffix = "\",\"password\":\"x\"}";
        return prefix + new string(filler[0], bytes - prefix.Length - suffix.Length) + suffix;
    }

    private async Task<int> Status(string body, bool chunked)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder("POST /api/v1/auth/login HTTP/1.1\r\n").Append($"Host: {host}:{port}\r\n").Append("Content-Type: application/json\r\n").Append("X-Hasm-Request: 1\r\nConnection: close\r\n");
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

    // Kestrel counts the chunk framing (size lines and CRLFs) of a chunked body toward the limit, so a chunked body is "at the limit" a little earlier than its payload (pre-existing behavior, recorded as a follow-up in
    // the review record of PR 175); the chunked cases therefore use a payload with room for the framing.
    private const int ChunkedFits = Limit - 600;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_byte_over_the_endpoints_limit_is_413_and_a_body_that_fits_reaches_the_handler(bool chunked)
    {
        Assert.Equal(413, await Status(Body(Limit + 1), chunked));
        Assert.Equal(413, await Status(Body(Limit * 3), chunked));
        var fits = await Status(Body(chunked ? ChunkedFits : Limit), chunked);                                  // declared: exactly at the limit
        Assert.NotEqual(413, fits);                                                                              // it got past the size limit to the handler (no database here: not a success either)
        Assert.InRange(fits, 400, 599);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_nul_guard_answers_400_inside_the_limit_and_never_changes_the_413_outside_it(bool chunked)
    {
        Assert.Equal(400, await Status(Body(chunked ? ChunkedFits : Limit, "\u0000"), chunked));               // a NUL inside a body that fits: the guard's validation answer
        Assert.Equal(413, await Status(Body(Limit + 1, "\u0000"), chunked));                                    // the same NUL one byte over: still the size refusal
    }
}
