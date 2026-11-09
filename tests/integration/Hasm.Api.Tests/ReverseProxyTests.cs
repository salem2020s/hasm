using System.Net;
using Hasm.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Hasm.Api.Tests;

/// <summary>
/// Forwarded client-address handling. A real pipeline built with the production extension methods answers with the client
/// identity the throttling code would use; the connection peer is set per request through the X-Test-Peer header (the
/// test transport has no sockets). No database is involved.
/// </summary>
public class ReverseProxyTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build();   // later entries override earlier ones

    private static string P(string name) => $"{ReverseProxyOptions.SectionName}:{name}";

    private static async Task<HttpClient> Host(IConfiguration configuration)
    {
        var options = ReverseProxyOptions.FromConfiguration(configuration);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var peer)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            return next();
        });
        app.UseHasmReverseProxy(options);
        app.MapGet("/whoami", (HttpContext c) => Results.Json(new { source = c.ClientSource(), scheme = c.Request.Scheme }));
        await app.StartAsync();
        return app.GetTestClient();
    }

    private sealed record Seen(string Source, string Scheme);

    private static async Task<Seen> Ask(HttpClient client, string peer, string? forwardedFor = null, string? proto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null) request.Headers.Add("X-Forwarded-Proto", proto);
        using var response = await client.SendAsync(request);
        var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return new Seen(json.GetProperty("source").GetString()!, json.GetProperty("scheme").GetString()!);
    }

    // ---- direct connections (no reverse proxy) ----

    [Fact]
    public async Task Direct_connections_use_the_peer_address_and_ignore_forwarded_headers()
    {
        var client = await Host(Config());
        var seen = await Ask(client, "203.0.113.50", forwardedFor: "198.51.100.1", proto: "https");
        Assert.Equal("203.0.113.50", seen.Source);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_while_the_proxy_feature_is_disabled_even_when_proxies_are_listed()
    {
        var client = await Host(Config((P("Enabled"), "false"), (P("KnownProxies:0"), "10.1.1.1")));
        Assert.Equal("10.1.1.1", (await Ask(client, "10.1.1.1", "198.51.100.1")).Source);
    }

    // ---- trusted proxies ----

    [Fact]
    public async Task A_configured_proxy_may_state_the_client_address()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "10.1.1.1")));
        var seen = await Ask(client, "10.1.1.1", "198.51.100.77", "https");
        Assert.Equal("198.51.100.77", seen.Source);
        Assert.Equal("https", seen.Scheme);
    }

    [Fact]
    public async Task A_proxy_inside_a_configured_network_is_trusted()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownNetworks:0"), "10.1.0.0/16")));
        Assert.Equal("198.51.100.77", (await Ask(client, "10.1.200.9", "198.51.100.77")).Source);
        Assert.Equal("10.2.0.1", (await Ask(client, "10.2.0.1", "198.51.100.77")).Source);   // outside the network: not trusted
    }

    [Fact]
    public async Task IPv6_proxy_addresses_and_clients_are_supported_and_clients_share_their_slash_64()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "fd00::10")));
        var a = await Ask(client, "fd00::10", "2001:db8:5:6::1");
        var b = await Ask(client, "fd00::10", "2001:db8:5:6:ffff::2");
        Assert.Equal("2001:db8:5:6::/64", a.Source);
        Assert.Equal(a.Source, b.Source);
    }

    // ---- spoofing ----

    [Fact]
    public async Task An_untrusted_client_cannot_spoof_its_address_or_scheme()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "10.1.1.1")));
        var seen = await Ask(client, "203.0.113.50", "198.51.100.1", "https");
        Assert.Equal("203.0.113.50", seen.Source);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task A_client_cannot_impersonate_the_proxy_by_claiming_its_address_in_a_header()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "10.1.1.1")));
        Assert.Equal("203.0.113.50", (await Ask(client, "203.0.113.50", "10.1.1.1")).Source);
    }

    [Fact]
    public async Task Addresses_injected_by_the_client_to_the_left_of_the_proxys_entry_are_ignored()
    {
        // The client sent "X-Forwarded-For: 9.9.9.9"; the proxy appended the address it really saw.
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "10.1.1.1")));
        Assert.Equal("198.51.100.77", (await Ask(client, "10.1.1.1", "9.9.9.9, 198.51.100.77")).Source);
    }

    [Fact]
    public async Task A_trusted_proxy_chain_is_walked_only_through_trusted_hops_and_never_past_the_limit()
    {
        var client = await Host(Config(
            (P("Enabled"), "true"), (P("ForwardLimit"), "2"), (P("KnownProxies:0"), "10.1.1.1"), (P("KnownProxies:1"), "10.1.1.2")));
        // client -> proxy 10.1.1.2 -> proxy 10.1.1.1 -> API; the attacker prepended 9.9.9.9
        Assert.Equal("198.51.100.77", (await Ask(client, "10.1.1.1", "9.9.9.9, 198.51.100.77, 10.1.1.2")).Source);
        // an untrusted hop in the chain stops the walk at that hop
        Assert.Equal("203.0.113.8", (await Ask(client, "10.1.1.1", "198.51.100.77, 203.0.113.8")).Source);
    }

    [Fact]
    public async Task The_default_limit_of_one_trusts_only_the_nearest_proxys_statement()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "10.1.1.1"), (P("KnownProxies:1"), "10.1.1.2")));
        Assert.Equal("10.1.1.2", (await Ask(client, "10.1.1.1", "198.51.100.77, 10.1.1.2")).Source);
    }

    [Fact]
    public async Task Protocol_forwarding_can_be_switched_off_while_addresses_are_still_forwarded()
    {
        var client = await Host(Config((P("Enabled"), "true"), (P("TrustProtocol"), "false"), (P("KnownProxies:0"), "10.1.1.1")));
        var seen = await Ask(client, "10.1.1.1", "198.51.100.77", "https");
        Assert.Equal("198.51.100.77", seen.Source);
        Assert.Equal("http", seen.Scheme);
    }

    // ---- configuration validation ----

    [Fact]
    public void Enabling_the_feature_without_any_trusted_proxy_is_refused()
    {
        var ex = Assert.Throws<ReverseProxyConfigurationException>(() => ReverseProxyOptions.FromConfiguration(Config((P("Enabled"), "true"))));
        Assert.Contains("KnownProxies", ex.Message);
    }

    [Theory]
    [InlineData("KnownProxies:0", "not-an-ip")]
    [InlineData("KnownProxies:0", "0.0.0.0")]
    [InlineData("KnownProxies:0", "::")]
    [InlineData("KnownNetworks:0", "10.0.0.0")]               // not CIDR
    [InlineData("KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("KnownNetworks:0", "::/0")]
    [InlineData("KnownNetworks:0", "4.0.0.0/4")]
    [InlineData("KnownNetworks:0", "2000::/3")]
    [InlineData("ForwardLimit", "-3")]
    [InlineData("ForwardLimit", "11")]
    [InlineData("ForwardLimit", "many")]
    [InlineData("Enabled", "maybe")]
    [InlineData("TrustProtocol", "perhaps")]
    public void Unsafe_or_malformed_settings_stop_startup_without_echoing_the_value(string key, string value)
    {
        var ex = Assert.Throws<ReverseProxyConfigurationException>(() => ReverseProxyOptions.FromConfiguration(
            Config((P("Enabled"), "true"), (P("KnownProxies:9"), "10.1.1.1"), (P(key), value))));
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void The_framework_switch_that_trusts_every_sender_is_refused()
    {
        var ex = Assert.Throws<ReverseProxyConfigurationException>(() =>
            ReverseProxyOptions.FromConfiguration(Config((ReverseProxyOptions.UnsafeSwitchKey, "true"))));
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", ex.Message);
        Assert.Contains(ReverseProxyOptions.SectionName, ex.Message);
    }

    [Fact]
    public void The_framework_default_loopback_trust_is_removed_from_the_effective_options()
    {
        var o = ReverseProxyOptions.FromConfiguration(Config((P("Enabled"), "true"), (P("KnownProxies:0"), "10.1.1.1"))).ToForwardedHeadersOptions();
        Assert.Equal([IPAddress.Parse("10.1.1.1")], o.KnownProxies);
        Assert.Empty(o.KnownIPNetworks);
    }

    [Fact]
    public async Task The_application_refuses_to_start_with_the_unsafe_framework_switch()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting(ReverseProxyOptions.UnsafeSwitchKey, "true"));
        await Assert.ThrowsAsync<ReverseProxyConfigurationException>(async () => { using var c = factory.CreateClient(); await c.GetAsync("/api/v1/health"); });
    }

    [Fact]
    public async Task The_application_starts_with_a_valid_proxy_configuration_and_serves_health()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting(P("Enabled"), "true");
            b.UseSetting(P("KnownProxies:0"), "127.0.0.1");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);
    }
}
