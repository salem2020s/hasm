using System.Net;
using System.Text.Json;
using Hasm.Api.Auth;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Api.Tests;

/// <summary>Phase 3.1A-1 HTTP surface: no-store for the company area, a safe error for unavailable company databases, and no new endpoints. No database needed.</summary>
public class CompanyRuntimeApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string PhysicalName = "d00112233445566778899aabbccddeeff";
    private const string Secret = "Zx9-very-secret-pw";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Hasm:Database:Password", Secret);
        b.UseSetting("Hasm:Database:SystemDatabaseName", PhysicalName);
        b.UseSetting("Hasm:Database:Host", "internal-db-host");
    }).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    [Theory]
    [InlineData("/api/v1/company")]
    [InlineData("/api/v1/company/profile")]
    [InlineData("/api/v1/company/anything/else")]
    [InlineData("/api/v1/companies")]
    [InlineData("/api/v1/access/members")]
    public async Task The_company_area_is_never_cached(string path)
    {
        var response = await Client().GetAsync(path);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Other_endpoints_keep_their_cache_behaviour()
    {
        var health = await Client().GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Null(health.Headers.CacheControl);
    }

    [Theory]
    [InlineData("GET", "/api/v1/company")]
    [InlineData("GET", "/api/v1/company/runtime")]
    [InlineData("GET", "/api/v1/company/audit")]
    [InlineData("POST", "/api/v1/company")]
    [InlineData("PUT", "/api/v1/company/profile")]
    [InlineData("DELETE", "/api/v1/company/anything")]
    public async Task No_company_business_endpoint_exists_yet_and_none_is_anonymous(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Hasm-Request", "1");
        var response = await Client().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.Unauthorized });
        Assert.DoesNotContain(PhysicalName, text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("internal-db-host", text);
    }

    [Fact]
    public async Task State_changing_company_calls_still_need_the_request_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/company/anything");
        var response = await Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("missing_request_header", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void The_company_runtime_is_registered_but_fails_closed_until_configured()
    {
        // Nothing is built at startup (the API starts without a database); first use refuses to run without configuration.
        using var services = factory.Services.CreateScope();
        Assert.Throws<DatabaseConfigurationException>(() => services.ServiceProvider.GetRequiredService<CompanyDatabaseResolver>());
        Assert.Throws<DatabaseConfigurationException>(() => services.ServiceProvider.GetRequiredService<CompanyAuditedTransactions>());
    }

    private static async Task<HttpClient> HostWithFailure(Exception failure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseHasmExceptionHandling();
        app.UseHasmResponseHardening();
        app.MapGet("/api/v1/company/boom", () => { throw failure; });
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Theory]
    [InlineData(CompanyDatabaseException.NotFound)]
    [InlineData(CompanyDatabaseException.NotReady)]
    [InlineData(CompanyDatabaseException.Unavailable)]
    [InlineData(CompanyDatabaseException.RuntimeNotProvisioned)]
    [InlineData(CompanyDatabaseException.SchemaUnmigrated)]
    [InlineData(CompanyDatabaseException.SchemaIncompatible)]
    [InlineData(CompanyDatabaseException.RuntimeOverprivileged)]
    public async Task An_unavailable_company_database_is_one_generic_503_that_reveals_nothing(string code)
    {
        var client = await HostWithFailure(new CompanyDatabaseException(code));
        var response = await client.GetAsync("/api/v1/company/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("company_unavailable", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(code, body);                         // internal reasons stay in the (code-only) server log
        Assert.DoesNotContain("migrat", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privilege", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_database_failures_still_reveal_no_connection_details()
    {
        var client = await HostWithFailure(new InvalidOperationException($"Host=internal-db-host;Database={PhysicalName};Password={Secret}"));
        var response = await client.GetAsync("/api/v1/company/boom");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(PhysicalName, body);
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain("internal-db-host", body);
    }
}
