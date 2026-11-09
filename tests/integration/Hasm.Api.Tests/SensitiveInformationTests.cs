using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hasm.Api.Tests;

/// <summary>Database settings configured on the host must never surface in HTTP responses or headers.</summary>
public class SensitiveInformationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "Zx9-very-secret-pw";
    private const string DbName = "d00112233445566778899aabbccddeeff";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Hasm:Database:Password", Secret);
        b.UseSetting("Hasm:Database:AdminPassword", Secret);
        b.UseSetting("Hasm:Database:SystemDatabaseName", DbName);
        b.UseSetting("Hasm:Database:Host", "internal-db-host");
    }).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/api/v1/companies")]   // no registry endpoint may exist before authentication/authorization
    public async Task Responses_do_not_expose_database_configuration(string path)
    {
        var response = await Client().GetAsync(path);
        var text = await response.Content.ReadAsStringAsync() + string.Join(";", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(DbName, text);
        Assert.DoesNotContain("internal-db-host", text);
    }

    [Fact]
    public async Task Company_registry_is_not_exposed_over_http()
    {
        var client = Client();
        // Phase 2.3B: the route exists only as an authenticated, caller-scoped list; anonymous callers get nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/companies")).StatusCode);
        // State-changing calls need the CSRF request header (see AuthDefaults.RequestHeader); send it so the route itself is what is tested.
        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/companies");
        post.Headers.Add("X-Hasm-Request", "1");
        Assert.Contains((await client.SendAsync(post)).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed }); // no company-creation route
    }
}
