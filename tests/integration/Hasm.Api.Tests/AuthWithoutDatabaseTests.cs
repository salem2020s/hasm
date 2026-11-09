using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hasm.Api.Tests;

/// <summary>
/// Authentication behaviour that must hold with no database configured at all: anonymous access is refused before
/// any database access, and failures never leak configuration. (End-to-end login tests run against PostgreSQL in
/// Hasm.Database.IntegrationTests.)
/// </summary>
public class AuthWithoutDatabaseTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "Zx9-very-secret-pw";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Hasm:Database:Password", Secret);
        b.UseSetting("Hasm:Database:Host", "internal-db-host");
    }).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static HttpRequestMessage Post(string path, object? body = null, bool header = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        return request;
    }

    [Fact]
    public async Task Anonymous_status_works_without_touching_the_database()
    {
        var response = await Client().GetAsync("/api/v1/auth/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"authenticated\":false", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/auth/change-password")]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/admin/users/6f1d3c0a-0000-4000-8000-000000000001/state")]
    public async Task Protected_endpoints_return_401_for_anonymous_callers(string path)
    {
        var response = await Client().SendAsync(Post(path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Fake_bearer_and_cookie_values_never_authenticate()
    {
        var client = Client();
        using var bearer = Post("/api/v1/admin/users");
        bearer.Headers.Add("Authorization", "Bearer not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bearer)).StatusCode);

        using var cookie = Post("/api/v1/admin/users");
        cookie.Headers.Add("Cookie", "__Host-hasm_session=forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(cookie)).StatusCode);
    }

    [Fact]
    public async Task Login_without_database_configuration_fails_closed_without_leaking_anything()
    {
        var response = await Client().SendAsync(Post("/api/v1/auth/login", new { login = "alice", password = "Whatever-Password-1" }));
        var text = await response.Content.ReadAsStringAsync() + string.Join(";", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("service_unavailable", text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("internal-db-host", text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("Hasm:Database", text);
    }

    [Theory]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/admin/users")]
    public async Task State_changing_requests_need_the_custom_header(string path)
    {
        var response = await Client().SendAsync(Post(path, header: false));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/users")]
    public async Task No_self_registration_route_exists(string path)
    {
        var response = await Client().SendAsync(Post(path, new { login = "x", password = "y" }));
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Unhandled_errors_return_a_generic_body()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = new StringContent("{broken", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Hasm-Request", "1");
        var response = await Client().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode is >= 400 and < 500);
        Assert.DoesNotContain("   at ", text);
        Assert.DoesNotContain("JsonException", text);
    }
}
