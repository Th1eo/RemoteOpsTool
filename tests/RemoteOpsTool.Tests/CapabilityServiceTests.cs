using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class CapabilityServiceTests
{
    private static RemoteCapabilityInfo Probe(string name, bool success) => new()
    {
        Name = name,
        Success = success,
        Detail = success ? "ok" : "failed",
    };

    private static CapabilityService CreateService(FakeTransportProbeService probe) =>
        new(probe, new TestLogService());

    [Fact]
    public async Task ProbeAsync_CachesSnapshotWithinTtl()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(probe);

        var first = await service.ProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var second = await service.ProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");

        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Same(first, second);
        Assert.Equal("REMOTE01", first.Host);
        Assert.Equal(@"DOMAIN\admin", first.UsernameKey);
        Assert.Contains(RemoteTransportKind.WmiDcom, first.AvailableTransports);
    }

    [Fact]
    public async Task RefreshAsync_AlwaysRefreshesSnapshot()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(probe);

        _ = await service.ProbeAsync("REMOTE01", "user", "secret");
        _ = await service.RefreshAsync("REMOTE01", "user", "secret");

        Assert.Equal(2, probe.ProbeCallCount);
    }

    [Fact]
    public async Task ProbeAsync_MapsSuccessfulProbesToAvailableTransports()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResults =
            [
                Probe("WMI/DCOM", true),
                Probe("PsExec 临时执行", true),
                Probe("计划任务 RPC", false),
            ],
        };
        var service = CreateService(probe);

        var snapshot = await service.ProbeAsync("REMOTE01", "user", "secret");

        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            snapshot.AvailableTransports.OrderBy(kind => (int)kind));
        Assert.Contains(RemoteTransportKind.ScheduledTask, snapshot.UnavailableTransports);
        Assert.Equal(3, snapshot.RawResults.Count);
    }

    [Fact]
    public async Task ProbeAsync_ForwardsHostAndCredentialsToNetworkProbe()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(probe);

        _ = await service.ProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");

        Assert.Equal(new[] { "REMOTE01" }, probe.ProbeHosts);
        Assert.Equal(new[] { @"DOMAIN\admin" }, probe.ProbeUsernames);
    }

    [Fact]
    public async Task HostNormalization_ReusesTheSameSnapshot()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(probe);

        var first = await service.ProbeAsync(@"\\REMOTE01", "user", "secret");
        var second = await service.ProbeAsync("REMOTE01", "user", "secret");

        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Same(first, second);
        Assert.Equal("REMOTE01", first.Host);
        Assert.Equal("REMOTE01", second.Host);
    }

    [Fact]
    public async Task DifferentCredentials_AreReportedAsSeparateSnapshots()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(probe);

        var first = await service.ProbeAsync("REMOTE01", "userA", "secret");
        var second = await service.ProbeAsync("REMOTE01", "userB", "secret");

        Assert.Equal(2, probe.ProbeCallCount);
        Assert.Equal("userA", first.UsernameKey);
        Assert.Equal("userB", second.UsernameKey);
    }

    [Fact]
    public async Task DifferentPasswords_AreReportedAsSeparateSnapshots()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = CreateService(probe);

        var first = await service.ProbeAsync("REMOTE01", "user", "secret-A");
        var second = await service.ProbeAsync("REMOTE01", "user", "secret-B");

        Assert.Equal(2, probe.ProbeCallCount);
        Assert.NotSame(first, second);
        Assert.Equal("user", first.UsernameKey);
        Assert.Equal("user", second.UsernameKey);
    }

    [Fact]
    public void CapabilityCachePolicy_UsesDifferentTtlsForProbeClasses()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var available = new RemoteCapabilityInfo { Name = "probe", Success = true, Detail = "ok" };
        var unavailable = new RemoteCapabilityInfo { Name = "probe", Success = false, Detail = "timeout" };

        Assert.Equal(
            TimeSpan.FromMinutes(5),
            CapabilityCachePolicy.GetExpiresAt(CapabilityProbeCatalog.Wmi, available, now) - now);
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            CapabilityCachePolicy.GetExpiresAt(CapabilityProbeCatalog.PsExec, available, now) - now);
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            CapabilityCachePolicy.GetExpiresAt(CapabilityProbeCatalog.Ping, unavailable, now) - now);
    }

    [Fact]
    public async Task ProbeCache_ExpiredPsExecDoesNotInvalidateFreshWmi()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var probe = new FakeTransportProbeService
        {
            ProbeResultsFactory = (profile, _) => profile == CapabilityProbeProfile.Command
                ?
                [
                    Probe(CapabilityProbeCatalog.Wmi, true),
                    Probe(CapabilityProbeCatalog.AdminShare, true),
                    Probe(CapabilityProbeCatalog.PsExec, true),
                ]
                :
                [Probe(CapabilityProbeCatalog.Wmi, true)],
        };
        var service = new CapabilityService(probe, new TestLogService(), () => now);

        _ = await service.ProbeAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.Command);
        now = now.AddSeconds(61);

        var snapshot = await service.ProbeAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.InventoryWmiOnly);

        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Contains(RemoteTransportKind.WmiDcom, snapshot.AvailableTransports);
    }

    [Fact]
    public async Task ProbeCache_NegativeResultIsScopedToTheMissingProbe()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResultsFactory = (profile, _) => profile == CapabilityProbeProfile.InventoryWmiOnly
                ? []
                :
                [
                    Probe(CapabilityProbeCatalog.Wmi, true),
                    Probe(CapabilityProbeCatalog.AdminShare, true),
                    Probe(CapabilityProbeCatalog.PsExec, true),
                ],
        };
        var service = CreateService(probe);

        _ = await service.ProbeAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.CommandWmiFirst);
        _ = await service.ProbeAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.CommandWmiFirst);
        Assert.Equal(1, probe.ProbeCallCount);

        var snapshot = await service.ProbeAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.Command);

        Assert.Equal(2, probe.ProbeCallCount);
        Assert.Equal(CapabilityProbeProfile.Command, probe.ProbeProfiles[^1]);
        Assert.Contains(RemoteTransportKind.PsExec, snapshot.AvailableTransports);
    }

    [Fact]
    public async Task RefreshAsync_ProfileRefreshesOnlyItsRequiredProbes()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResultsFactory = (_, _) => [Probe(CapabilityProbeCatalog.Wmi, true)],
        };
        var service = CreateService(probe);

        _ = await service.ProbeAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.CommandWmiFirst);
        _ = await service.RefreshAsync(
            "REMOTE01", "user", "secret", CapabilityProbeProfile.CommandWmiFirst);

        Assert.Equal(2, probe.ProbeCallCount);
        Assert.All(
            probe.ProbeProfiles,
            profile => Assert.Equal(CapabilityProbeProfile.InventoryWmiOnly, profile));
    }
}
