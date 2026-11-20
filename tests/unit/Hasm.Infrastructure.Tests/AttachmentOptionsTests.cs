using Hasm.Infrastructure.Attachments;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

public class AttachmentOptionsTests
{
    private const long Default = 25L * 1024 * 1024, Cap = 100L * 1024 * 1024;

    private static AttachmentOptions From(params (string Key, string Value)[] values) =>
        AttachmentOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => "Hasm:Attachments:" + v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void Defaults_are_a_25_MB_file_four_concurrent_uploads_two_per_user_and_a_two_minute_deadline()
    {
        var o = From();
        Assert.Equal(Default, o.MaxFileBytes);
        Assert.Equal((4, 2, 120), (o.MaxConcurrentUploads, o.MaxConcurrentUploadsPerUser, o.UploadTimeoutSeconds));
    }

    [Theory]
    [InlineData("1048576", 1048576L)]
    [InlineData("104857600", Cap)]                          // the hard cap itself
    [InlineData("104857601", Cap)]                          // above the hard cap: clamped to the cap
    [InlineData("536870912", Cap)]                          // the former 512 MB ceiling is clamped
    [InlineData("0", Default)]
    [InlineData("-5", Default)]
    [InlineData("abc", Default)]
    public void The_file_size_is_clamped_to_the_hard_maximum(string configured, long expected)
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

    [Theory]
    [InlineData("1", "8", 1)]
    [InlineData("8", "3", 3)]                                // never above the total
    [InlineData("0", "8", 2)]
    [InlineData("x", "1", 1)]
    public void The_per_user_limit_never_exceeds_the_total(string perUser, string total, int expected)
        => Assert.Equal(expected, From(("MaxConcurrentUploadsPerUser", perUser), ("MaxConcurrentUploads", total)).MaxConcurrentUploadsPerUser);

    [Theory]
    [InlineData("10", 10)]
    [InlineData("900", 900)]
    [InlineData("9", 120)]
    [InlineData("901", 120)]
    [InlineData("x", 120)]
    public void The_upload_deadline_is_bounded(string configured, int expected)
        => Assert.Equal(expected, From(("UploadTimeoutSeconds", configured)).UploadTimeoutSeconds);
}

public class UploadGateTests
{
    [Fact]
    public void The_total_limit_refuses_without_waiting_and_a_released_slot_can_be_taken_again()
    {
        var gate = new UploadGate(2, 2);
        var a = gate.TryEnter(Guid.NewGuid());
        var b = gate.TryEnter(Guid.NewGuid());
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Null(gate.TryEnter(Guid.NewGuid()));
        Assert.Equal(2, gate.InUse);
        a!.Dispose();
        Assert.Equal(1, gate.InUse);
        Assert.NotNull(gate.TryEnter(Guid.NewGuid()));
    }

    [Fact]
    public void One_user_cannot_take_every_slot()
    {
        var gate = new UploadGate(4, 1);
        var user = Guid.NewGuid();
        var first = gate.TryEnter(user);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter(user));                    // the same user is refused although slots are free
        Assert.NotNull(gate.TryEnter(Guid.NewGuid()));       // another user is served
        first!.Dispose();
        Assert.NotNull(gate.TryEnter(user));
    }

    [Fact]
    public void Disposing_a_slot_twice_releases_it_once()
    {
        var gate = new UploadGate(2, 2);
        var slot = gate.TryEnter(Guid.NewGuid())!;
        slot.Dispose(); slot.Dispose();
        Assert.Equal(0, gate.InUse);
        var x = gate.TryEnter(Guid.NewGuid()); var y = gate.TryEnter(Guid.NewGuid());
        Assert.NotNull(x); Assert.NotNull(y);
        Assert.Null(gate.TryEnter(Guid.NewGuid()));          // a double release would have made room for a third
    }

    [Fact]
    public async Task Concurrent_callers_never_exceed_the_limits()
    {
        var gate = new UploadGate(3, 3);
        var user = Guid.NewGuid();
        var slots = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => gate.TryEnter(user))));
        Assert.Equal(3, slots.Count(s => s is not null));
        Assert.Equal(3, gate.InUse);
    }
}
