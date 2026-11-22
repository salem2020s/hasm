namespace Hasm.Api.Tests;

/// <summary>
/// The shared real-Kestrel harness listens on a port chosen by the operating system in BOTH of its modes, so any number of hosts (test classes in parallel, or a developer machine with port 5000 in use) can run at
/// once. The admitted mode used to listen on Kestrel's default address, 127.0.0.1:5000, and a second admitted host failed with "address already in use" (FG review L3 of PR 184).
/// </summary>
public sealed class RealKestrelHostTests
{
    private const int KestrelDefaultPort = 5000;

    [Fact]
    public async Task Hosts_of_both_modes_started_at_once_each_listen_on_their_own_ephemeral_port_and_answer_there()
    {
        // two admitted hosts and one plain host, started at the same time and alive together
        var started = await Task.WhenAll(Task.Run(() => new RealKestrelHost(admitEveryone: true)), Task.Run(() => new RealKestrelHost(admitEveryone: true)), Task.Run(() => new RealKestrelHost()));
        try
        {
            var ports = started.Select(h => h.Port).ToArray();
            Assert.Equal(ports.Length, ports.Distinct().Count());
            Assert.All(ports, p => Assert.True(p != KestrelDefaultPort && p >= 1024, $"port {p} is not an ephemeral port"));

            // each host is the one that answers on its own port, in its own mode: the admitted hosts pass authentication (the unknown route under the API is then 404), the plain host refuses the anonymous request 401
            var path = $"/api/v1/company/stock-documents/{Guid.NewGuid()}/reject";
            var statuses = await Task.WhenAll(started.Select(h => h.PostStatus(path, "{}")));
            Assert.NotEqual(401, statuses[0]);
            Assert.NotEqual(401, statuses[1]);
            Assert.Equal(401, statuses[2]);
        }
        finally
        {
            foreach (var h in started) h.Dispose();
        }
    }
}
