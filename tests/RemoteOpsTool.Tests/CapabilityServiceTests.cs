using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class CapabilityServiceTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static RemoteCapabilityInfo Probe(string name, bool success) => new()
    {
        Name = name,
        Success = success,
        Detail = success ? "ok" : "failed",
    };

    private static CapabilityService CreateService(
        FakeNetworkService network,
        FakeTimeProvider time,
        TimeSpan? ttl = null) =>
        new(network, new TestLogService(), time, ttl ?? Ttl);

    [Fact]
    public async Task GetOrProbeAsync_ProbesOnceAndReusesCachedSnapshot()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(network, new FakeTimeProvider());

        var first = await service.GetOrProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var second = await service.GetOrProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");

        Assert.Equal(1, network.ProbeCallCount);
        Assert.Same(first, second);
        Assert.Equal("REMOTE01", first.Host);
        Assert.Equal(@"DOMAIN\admin", first.UsernameKey);
        Assert.Contains(RemoteTransportKind.WmiDcom, first.AvailableTransports);
    }

    [Fact]
    public async Task GetOrProbeAsync_ProbesAgainAfterTtlExpiry()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var time = new FakeTimeProvider();
        var service = CreateService(network, time);

        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");
        time.Advance(Ttl.Add(TimeSpan.FromSeconds(1)));

        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");

        Assert.Equal(2, network.ProbeCallCount);
    }

    [Fact]
    public async Task GetOrProbeAsync_DoesNotReProbeJustBeforeTtlExpiry()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var time = new FakeTimeProvider();
        var service = CreateService(network, time);

        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");
        time.Advance(Ttl.Subtract(TimeSpan.FromTicks(1)));

        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");

        Assert.Equal(1, network.ProbeCallCount);
    }

    [Fact]
    public async Task Invalidate_ForcesNextGetOrProbeToReProbe()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(network, new FakeTimeProvider());

        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");
        service.Invalidate("REMOTE01", "user");
        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");

        Assert.Equal(2, network.ProbeCallCount);
    }

    [Fact]
    public async Task GetCached_ReturnsNullBeforeProbeAndSnapshotAfterProbe()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WinRM 5985", true)] };
        var service = CreateService(network, new FakeTimeProvider());

        Assert.Null(service.GetCached("REMOTE01", "user"));

        _ = await service.GetOrProbeAsync("REMOTE01", "user", "secret");

        var cached = service.GetCached("REMOTE01", "user");
        Assert.NotNull(cached);
        Assert.Contains(RemoteTransportKind.WinRm, cached.AvailableTransports);
    }

    [Fact]
    public async Task GetFallbackChainAsync_ReturnsOrderedChainAndUsesCache()
    {
        var network = new FakeNetworkService
        {
            ProbeResults =
            [
                Probe("WinRM 5985", true),
                Probe("WMI/DCOM", true),
                Probe("PsExec 临时执行", false),
            ],
        };
        var service = CreateService(network, new FakeTimeProvider());

        var chain = await service.GetFallbackChainAsync(
            "REMOTE01", "user", "secret", RemoteOperationKind.Command);
        var secondChain = await service.GetFallbackChainAsync(
            "REMOTE01", "user", "secret", RemoteOperationKind.Command);

        Assert.Equal(
            new[] { RemoteTransportKind.WinRm, RemoteTransportKind.WmiDcom },
            chain);
        Assert.Equal(chain, secondChain);
        Assert.Equal(1, network.ProbeCallCount);
    }

    [Fact]
    public async Task HostNormalization_ReusesCacheForLocalHostAliases()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(network, new FakeTimeProvider());

        _ = await service.GetOrProbeAsync("localhost", "user", "secret");
        _ = await service.GetOrProbeAsync(Environment.MachineName, "user", "secret");

        Assert.Equal(1, network.ProbeCallCount);
        Assert.Equal(Environment.MachineName, service.GetCached("localhost", "user")!.Host);
    }

    [Fact]
    public async Task DifferentCredentialKeys_DoNotShareSnapshot()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(network, new FakeTimeProvider());

        _ = await service.GetOrProbeAsync("REMOTE01", "userA", "secret");
        _ = await service.GetOrProbeAsync("REMOTE01", "userB", "secret");

        Assert.Equal(2, network.ProbeCallCount);
    }

    [Fact]
    public async Task ProbeAsync_AlwaysRefreshesSnapshot()
    {
        var network = new FakeNetworkService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(network, new FakeTimeProvider());

        _ = await service.ProbeAsync("REMOTE01", "user", "secret");
        _ = await service.ProbeAsync("REMOTE01", "user", "secret");

        Assert.Equal(2, network.ProbeCallCount);
    }
}
