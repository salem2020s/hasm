using System.Net;
using System.Net.Sockets;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// Turns the validated client address into the string used as the throttling identity. The address must already be the one
/// the server trusts (the connection peer, or the forwarded address accepted from a configured trusted proxy); this class
/// never reads headers. IPv4-mapped IPv6 addresses fold to IPv4, and IPv6 addresses are reduced to their /64 prefix because a
/// single IPv6 subscriber normally controls a whole /64, so per-address counting would let one client rotate freely.
/// </summary>
public static class SourceAddress
{
    public const string Unknown = "unknown";

    public static string Normalize(IPAddress? address)
    {
        if (address is null) return Unknown;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _)) return Unknown;
        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }
}
