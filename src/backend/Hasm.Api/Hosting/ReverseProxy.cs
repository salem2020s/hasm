using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;
using Hasm.Infrastructure.Identity;

namespace Hasm.Api.Hosting;

public sealed class ReverseProxyConfigurationException(string message) : Exception(message);

/// <summary>
/// Trusted reverse proxy settings, section "Hasm:ReverseProxy" (environment variables Hasm__ReverseProxy__*).
/// Forwarded client-address headers are honoured ONLY when the immediate peer is one of the configured proxies or networks;
/// from anyone else they are ignored. Disabled by default (direct connections). Values are never echoed in error messages.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "Hasm:ReverseProxy";
    /// <summary>The legacy ASP.NET Core switch (ASPNETCORE_FORWARDEDHEADERS_ENABLED) trusts every sender; it is refused.</summary>
    public const string UnsafeSwitchKey = "ForwardedHeaders_Enabled";

    public bool Enabled { get; init; }
    public IReadOnlyList<IPAddress> KnownProxies { get; init; } = [];
    public IReadOnlyList<IPNetwork> KnownNetworks { get; init; } = [];
    /// <summary>Number of trusted proxies in the chain (1 = a single proxy in front of the API). Entries beyond it are ignored.</summary>
    public int ForwardLimit { get; init; } = 1;
    /// <summary>Also accept X-Forwarded-Proto from trusted proxies (TLS terminated at the proxy).</summary>
    public bool TrustProtocol { get; init; } = true;

    public static ReverseProxyOptions FromConfiguration(IConfiguration configuration)
    {
        if (bool.TryParse(configuration[UnsafeSwitchKey], out var unsafeSwitch) && unsafeSwitch)
            throw new ReverseProxyConfigurationException(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED trusts forwarded headers from any sender and is not allowed. " +
                $"Remove it and configure {SectionName}:Enabled with {SectionName}:KnownProxies / {SectionName}:KnownNetworks.");

        var s = configuration.GetSection(SectionName);
        var enabledText = s[nameof(Enabled)];
        var enabled = false;
        if (!string.IsNullOrWhiteSpace(enabledText) && !bool.TryParse(enabledText, out enabled))
            throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: {SectionName}:{nameof(Enabled)} must be true or false.");

        var proxies = new List<IPAddress>();
        foreach (var text in s.GetSection(nameof(KnownProxies)).Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(text?.Trim(), out var ip))
                throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: an entry of {SectionName}:{nameof(KnownProxies)} is not an IP address.");
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: {SectionName}:{nameof(KnownProxies)} must list concrete proxy addresses.");
            proxies.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
        }

        var networks = new List<IPNetwork>();
        foreach (var text in s.GetSection(nameof(KnownNetworks)).Get<string[]>() ?? [])
        {
            if (!IPNetwork.TryParse(text?.Trim(), out var net))
                throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: an entry of {SectionName}:{nameof(KnownNetworks)} is not a CIDR network (e.g. 192.0.2.0/24).");
            var minimum = net.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 8 : 7;
            if (net.PrefixLength < minimum)
                throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: {SectionName}:{nameof(KnownNetworks)} entries must be internal ranges (at least /8 for IPv4, /7 for IPv6), never the Internet.");
            networks.Add(net);
        }

        var limit = 1;
        var limitText = s[nameof(ForwardLimit)];
        if (!string.IsNullOrWhiteSpace(limitText) && (!int.TryParse(limitText, out limit) || limit is < 1 or > 10))
            throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: {SectionName}:{nameof(ForwardLimit)} must be an integer between 1 and 10.");

        var trustProtocol = true;
        var protoText = s[nameof(TrustProtocol)];
        if (!string.IsNullOrWhiteSpace(protoText) && !bool.TryParse(protoText, out trustProtocol))
            throw new ReverseProxyConfigurationException($"Invalid reverse proxy configuration: {SectionName}:{nameof(TrustProtocol)} must be true or false.");

        if (enabled && proxies.Count == 0 && networks.Count == 0)
            throw new ReverseProxyConfigurationException(
                $"Invalid reverse proxy configuration: {SectionName}:{nameof(Enabled)} requires at least one {nameof(KnownProxies)} or {nameof(KnownNetworks)} entry (forwarded headers are never trusted from arbitrary senders).");

        return new ReverseProxyOptions { Enabled = enabled, KnownProxies = proxies, KnownNetworks = networks, ForwardLimit = limit, TrustProtocol = trustProtocol };
    }

    /// <summary>Builds the framework options: only the configured proxies/networks are trusted (the framework's implicit loopback trust is removed).</summary>
    public ForwardedHeadersOptions ToForwardedHeadersOptions()
    {
        var o = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | (TrustProtocol ? ForwardedHeaders.XForwardedProto : ForwardedHeaders.None),
            ForwardLimit = ForwardLimit,
            RequireHeaderSymmetry = false,
        };
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var p in KnownProxies) o.KnownProxies.Add(p);
        foreach (var n in KnownNetworks) o.KnownIPNetworks.Add(n);
        return o;
    }
}

public static class ReverseProxySetup
{
    /// <summary>Must run first in the pipeline so that HTTPS redirection, throttling and logging see the validated client. No-op when disabled.</summary>
    public static IApplicationBuilder UseHasmReverseProxy(this IApplicationBuilder app, ReverseProxyOptions options) =>
        options.Enabled ? app.UseForwardedHeaders(options.ToForwardedHeadersOptions()) : app;

    /// <summary>The validated client identity used for throttling: the connection peer, or the address accepted from a trusted proxy.</summary>
    public static string ClientSource(this HttpContext http) => SourceAddress.Normalize(http.Connection.RemoteIpAddress);
}
