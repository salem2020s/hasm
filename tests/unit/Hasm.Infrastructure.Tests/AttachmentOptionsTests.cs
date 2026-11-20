using Hasm.Infrastructure.Attachments;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

public class AttachmentOptionsTests
{
    private static AttachmentOptions From(params (string Key, string Value)[] values) =>
        AttachmentOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => "Hasm:Attachments:" + v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void Defaults_are_a_25_MB_file_and_four_concurrent_uploads()
    {
        var o = From();
        Assert.Equal(25L * 1024 * 1024, o.MaxFileBytes);
        Assert.Equal(4, o.MaxConcurrentUploads);
    }

    [Theory]
    [InlineData("1048576", 1048576L)]
    [InlineData("104857600", 104857600L)]                  // the hard cap itself
    [InlineData("104857601", 25L * 1024 * 1024)]           // above the hard cap: back to the default
    [InlineData("536870912", 25L * 1024 * 1024)]           // the former 512 MB ceiling is no longer accepted
    [InlineData("0", 25L * 1024 * 1024)]
    [InlineData("abc", 25L * 1024 * 1024)]
    public void The_file_size_is_capped_by_the_hard_maximum(string configured, long expected)
        => Assert.Equal(expected, From(("MaxFileBytes", configured)).MaxFileBytes);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("32", 32)]
    [InlineData("33", 4)]
    [InlineData("0", 4)]
    [InlineData("-1", 4)]
    [InlineData("x", 4)]
    public void Concurrent_uploads_are_bounded(string configured, int expected)
        => Assert.Equal(expected, From(("MaxConcurrentUploads", configured)).MaxConcurrentUploads);
}
