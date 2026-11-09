using System.Net;
using Hasm.Infrastructure.Identity;

namespace Hasm.Infrastructure.Tests;

public class SourceAddressTests
{
    [Fact]
    public void Missing_address_is_the_shared_unknown_bucket() => Assert.Equal(SourceAddress.Unknown, SourceAddress.Normalize(null));

    [Fact]
    public void IPv4_is_used_as_is() => Assert.Equal("203.0.113.9", SourceAddress.Normalize(IPAddress.Parse("203.0.113.9")));

    [Fact]
    public void IPv4_mapped_IPv6_folds_to_the_IPv4_address() =>
        Assert.Equal("203.0.113.9", SourceAddress.Normalize(IPAddress.Parse("::ffff:203.0.113.9")));

    [Fact]
    public void IPv6_addresses_are_reduced_to_their_slash_64_so_one_subscriber_cannot_rotate_addresses()
    {
        var a = SourceAddress.Normalize(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var b = SourceAddress.Normalize(IPAddress.Parse("2001:db8:1:2::1"));
        Assert.Equal("2001:db8:1:2::/64", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_slash_64_networks_stay_separate() =>
        Assert.NotEqual(
            SourceAddress.Normalize(IPAddress.Parse("2001:db8:1:2::1")),
            SourceAddress.Normalize(IPAddress.Parse("2001:db8:1:3::1")));
}
