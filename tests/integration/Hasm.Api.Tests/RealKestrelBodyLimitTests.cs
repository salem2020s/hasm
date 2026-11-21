namespace Hasm.Api.Tests;

/// <summary>
/// The request-size limit of an endpoint on a REAL Kestrel host (loopback, raw sockets: no test server, no stand-ins), with the NUL guard in the pipeline. The login endpoint accepts at most 8 KiB (8192 bytes). A body of one byte over is
/// refused with 413, declared or chunked; a body of exactly the limit reaches the handler (no database is configured, so the handler answers 503, which is NOT 413); and the NUL guard still answers 400 for a NUL inside a body that fits
/// and never changes the 413 for a body that does not. This is what the unit-level guard tests cannot show: the framework's real order and the real refusal.
/// </summary>
public sealed class RealKestrelBodyLimitTests : IDisposable
{
    private const int Limit = 8 * 1024;
    private readonly RealKestrelHost server = new();                                                           // the shared real-Kestrel harness (loopback, raw sockets)

    public void Dispose() => server.Dispose();

    /// <summary>A JSON login body of exactly <paramref name="bytes"/> bytes (the padding is a long login name).</summary>
    private static string Body(int bytes, string filler = "a")
    {
        const string prefix = "{\"login\":\"", suffix = "\",\"password\":\"x\"}";
        return prefix + new string(filler[0], bytes - prefix.Length - suffix.Length) + suffix;
    }

    private Task<int> Status(string body, bool chunked) => server.PostStatus("/api/v1/auth/login", body, chunked);

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
