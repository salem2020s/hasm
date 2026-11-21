using System.Net;
using System.Text;
using System.Text.Json;
using Hasm.Api.Auth;
using Hasm.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Hasm.Api.Tests;

/// <summary>
/// Shared input rule (no NUL in any text query value or JSON string value; Kestrel itself refuses a NUL in the path) and the defence-in-depth mapping of PostgreSQL 22021. All of it runs WITHOUT a database: the guard answers before routing, so these requests
/// never reach authentication or a database call (proof: an anonymous caller gets the validation answer, not 401, and no database is configured).
/// </summary>
public class InputHardeningTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<(HttpStatusCode Status, string Error, (string Field, string Code)[] Fields)> Read(HttpResponseMessage r)
    {
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        var fields = json.TryGetProperty("fields", out var f) ? f.EnumerateArray().Select(x => (x.GetProperty("field").GetString()!, x.GetProperty("code").GetString()!)).ToArray() : [];
        return (r.StatusCode, json.GetProperty("error").GetString()!, fields);
    }

    [Theory]
    [InlineData("/api/v1/company/commercial-documents/?docType=a%00b", "docType")]
    [InlineData("/api/v1/company/inventory/items?search=%00", "search")]
    [InlineData("/api/v1/company/inventory/barcode-lookup?barcode=a%00b", "barcode")]
    [InlineData("/api/v1/company/exchange-rates/history?currency=%00", "currency")]
    [InlineData("/api/v1/company/journal/?q=a%00b", "q")]
    [InlineData("/api/v1/company/imports/?adapter=%00", "adapter")]
    [InlineData("/api/v1/company/parties/?search=a%00b", "search")]
    public async Task A_nul_in_a_query_value_is_a_mapped_400_with_a_field_error_before_any_authentication_or_database_call(string url, string field)
    {
        using var response = await Client().GetAsync(url);
        var (status, error, fields) = await Read(response);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), (status, error));
        Assert.Equal([(field, "invalid_value")], fields);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_nul_in_a_query_key_is_a_mapped_400()
    {
        using var inKey = await Client().GetAsync("/api/v1/company/parties/?a%00b=1");
        Assert.Equal([("query", "invalid_value")], (await Read(inKey)).Fields);
    }

    [Theory]
    [InlineData("{\"code\":\"a\\u0000b\",\"displayName\":\"x\"}", "code")]
    [InlineData("{\"code\":\"ok\",\"displayName\":\"\\u0000\"}", "displayName")]
    [InlineData("{\"identity\":{\"legalNameAr\":\"x\\u0000\"}}", "legalNameAr")]
    [InlineData("{\"lines\":[{\"description\":\"\\u0000\"}]}", "description")]
    [InlineData("{\"names\":[\"a\",\"b\\u0000\"]}", "names")]
    [InlineData("\"\\u0000\"", "body")]
    public async Task A_nul_in_any_json_string_value_is_a_mapped_400_naming_the_field(string json, string field)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/company/parties/") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Hasm-Request", "1");
        using var response = await Client().SendAsync(request);
        var (status, error, fields) = await Read(response);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), (status, error));
        Assert.Equal([(field, "invalid_value")], fields);
    }

    [Fact]
    public async Task Ordinary_input_is_untouched_and_a_literal_backslash_u0000_text_is_not_a_nul()
    {
        // a request without NUL goes on to the normal pipeline (no session: 401), also when the text merely LOOKS like an escape ("\\u0000" is a backslash followed by u0000)
        foreach (var json in new[] { "{\"code\":\"a\",\"displayName\":\"\\\\u0000\"}", "{\"code\":\"\u202E\u0301\"}", "{}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/company/parties/") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Hasm-Request", "1");
            using var response = await Client().SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var q = await Client().GetAsync("/api/v1/company/parties/?search=%25_%5C%27%22");
        Assert.Equal(HttpStatusCode.Unauthorized, q.StatusCode);
    }

    [Fact]
    public async Task The_guard_leaves_the_body_readable_for_the_endpoint_and_does_not_buffer_what_it_must_not()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseHasmNulCharacterGuard();
        app.Run(async ctx => { using var reader = new StreamReader(ctx.Request.Body); ctx.Response.StatusCode = 200; await ctx.Response.WriteAsync(await reader.ReadToEndAsync()); });
        var pipeline = app.Build();

        async Task<(int Status, string Body)> Run(string contentType, byte[] body, bool setLength = true)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.ContentType = contentType;
            ctx.Request.Body = new MemoryStream(body);
            if (setLength) ctx.Request.ContentLength = body.Length;
            ctx.Response.Body = new MemoryStream();
            await pipeline(ctx);
            ctx.Response.Body.Position = 0;
            return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
        }

        var ok = Encoding.UTF8.GetBytes("{\"a\":\"ثابت\u202E\"}");
        Assert.Equal((200, "{\"a\":\"ثابت\u202E\"}"), await Run("application/json", ok));                          // scanned, rewound, the endpoint reads exactly the same bytes
        Assert.Equal(400, (await Run("application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"a\":\"\\u0000\"}"))).Status);
        Assert.Equal(400, (await Run("application/vnd.api+json", Encoding.UTF8.GetBytes("{\"a\":\"\\u0000\"}"))).Status);
        Assert.Equal(200, (await Run("application/octet-stream", Encoding.UTF8.GetBytes("{\"a\":\"\\u0000\"}"))).Status);   // an upload is not JSON: not scanned (the database mapping is the backstop)
        Assert.Equal(200, (await Run("application/json", Encoding.UTF8.GetBytes("{\"a\":\"\\u0000\"}"), setLength: false)).Status);   // unknown length (chunked): not buffered
        var big = Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', InputHardening.MaxScannedBody) + "\\u0000\"}");
        Assert.Equal(200, (await Run("application/json", big)).Status);                                            // above the scan limit: passed on, never buffered whole
        Assert.Equal(200, (await Run("application/json", Encoding.UTF8.GetBytes("{\"a\":"))).Status);              // malformed JSON is the binder's to reject
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"a\":\"b\"}", null)]
    [InlineData("{\"a\":\"b\\u0000\"}", "a")]
    [InlineData("{\"o\":{\"p\":\"q\\u0000\"},\"r\":\"s\"}", "p")]
    [InlineData("{\"o\":{\"p\":\"q\"},\"r\":\"s\\u0000\"}", "r")]                                                  // the name comes back to the outer property after a nested object
    [InlineData("[\"\\u0000\"]", "body")]
    [InlineData("{\"a\\u0000b\":1}", "body")]                                                                    // a NUL in a property NAME
    [InlineData("{\"a\":\"\\\\u0000\"}", null)]                                                                  // an escaped backslash followed by text
    [InlineData("{\"a\":\"\\u0001\"}", null)]                                                                    // other control characters are not this rule
    [InlineData("{\"a\":", null)]
    public void NulFieldIn_finds_the_property_that_holds_a_nul(string json, string? expected) =>
        Assert.Equal(expected, InputHardening.NulFieldIn(Encoding.UTF8.GetBytes(json)));

    // ------------------------------------------------------------------------------------------------------------------------- the 22021 mapping

    private static PostgresException Pg(string sqlState) => new("m", "ERROR", "ERROR", sqlState);

    [Fact]
    public void Only_a_22021_failure_is_a_caller_data_problem_every_other_database_failure_stays_an_outage()
    {
        Assert.True(HasmExceptionMapping.IsInvalidCharacter(Pg("22021")));
        Assert.True(HasmExceptionMapping.IsInvalidCharacter(new InvalidOperationException("wrapped", Pg("22021"))));
        Assert.True(HasmExceptionMapping.IsInvalidCharacter(new AggregateException(new Exception("a"), new InvalidOperationException("b", Pg("22021")))));
        foreach (var state in new[] { "08006", "08001", "53300", "57P01", "57014", "40001", "40P01", "23505", "23503", "22P05", "42501", "XX000" })
            Assert.False(HasmExceptionMapping.IsInvalidCharacter(Pg(state)), state);
        Assert.False(HasmExceptionMapping.IsInvalidCharacter(new NpgsqlException("connection refused", new System.Net.Sockets.SocketException())));
        Assert.False(HasmExceptionMapping.IsInvalidCharacter(new TimeoutException()));
        Assert.False(HasmExceptionMapping.IsInvalidCharacter(null));
    }

    [Fact]
    public async Task The_exception_handler_answers_400_for_22021_and_keeps_503_for_other_database_failures_500_for_the_rest()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseHasmExceptionHandling();
        app.MapGet("/bad-char", () => { throw Pg("22021"); });
        app.MapGet("/wrapped-bad-char", () => { throw new InvalidOperationException("w", Pg("22021")); });
        app.MapGet("/outage", () => { throw Pg("08006"); });
        app.MapGet("/connect", () => { throw new NpgsqlException("down"); });
        app.MapGet("/duplicate", () => { throw Pg("23505"); });
        app.MapGet("/other", () => { throw new InvalidOperationException("boom"); });
        await app.StartAsync();
        var client = app.GetTestClient();

        async Task<(int, string)> Get(string path) { using var r = await client.GetAsync(path); return ((int)r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!); }
        Assert.Equal((400, "invalid_request"), await Get("/bad-char"));
        Assert.Equal((400, "invalid_request"), await Get("/wrapped-bad-char"));
        Assert.Equal((503, "service_unavailable"), await Get("/outage"));                                           // a genuine outage is never hidden as a client error
        Assert.Equal((503, "service_unavailable"), await Get("/connect"));
        Assert.Equal((503, "service_unavailable"), await Get("/duplicate"));                                        // unchanged mapping for every other database error (the constraint matrix owns 23505)
        Assert.Equal((500, "internal_error"), await Get("/other"));
    }
}
