using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class CapabilityMatrixTests
{
    private static RemoteCapabilityInfo Probe(string name, bool success) => new()
    {
        Name = name,
        Success = success,
        Detail = success ? "ok" : "failed",
    };

    [Fact]
    public void ResolveAvailableTransports_ReturnsOnlySuccessfulKnownProbes()
    {
        var results = new[]
        {
            Probe("WinRM 5985", true),
            Probe("WMI/DCOM", false),
            Probe("PsExec 临时执行", true),
            Probe("unknown probe", true),
        };

        var available = CapabilityMatrix.ResolveAvailableTransports(results);

        Assert.Equal(
            new[] { RemoteTransportKind.WinRm, RemoteTransportKind.PsExec },
            available.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void ResolveTransports_AllAvailable_LeavesUnavailableEmpty()
    {
        var results = new[]
        {
            Probe("WinRM 5985", true),
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true),
        };

        var (available, unavailable) = CapabilityMatrix.ResolveTransports(results);

        Assert.Equal(
            new[] { RemoteTransportKind.WinRm, RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            available.OrderBy(kind => (int)kind));
        Assert.Empty(unavailable);
    }

    [Fact]
    public void ResolveTransports_MixedResults_SplitsAvailableAndUnavailable()
    {
        var results = new[]
        {
            Probe("WinRM 5985", true),
            Probe("WMI/DCOM", false),
            Probe("PsExec 临时执行", false),
        };

        var (available, unavailable) = CapabilityMatrix.ResolveTransports(results);

        Assert.Equal(new[] { RemoteTransportKind.WinRm }, available);
        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            unavailable.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void ResolveTransports_NoneAvailable_MarksAllProbeableKindsUnavailable()
    {
        var results = new[]
        {
            Probe("WinRM 5985", false),
            Probe("WMI/DCOM", false),
            Probe("PsExec 临时执行", false),
        };

        var (available, unavailable) = CapabilityMatrix.ResolveTransports(results);

        Assert.Empty(available);
        Assert.Equal(
            new[] { RemoteTransportKind.WinRm, RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            unavailable.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void ResolveTransports_IgnoresUnknownProbeNames()
    {
        var (available, unavailable) = CapabilityMatrix.ResolveTransports(new[]
        {
            Probe("SSH 22", false),
        });

        Assert.Empty(available);
        Assert.DoesNotContain(RemoteTransportKind.Ssh, unavailable);
    }

    [Theory]
    [InlineData(RemoteOperationKind.Command, RemoteTransportKind.WinRm)]
    [InlineData(RemoteOperationKind.InteractiveLaunch, RemoteTransportKind.PsExec)]
    [InlineData(RemoteOperationKind.Inventory, RemoteTransportKind.WmiDcom)]
    [InlineData(RemoteOperationKind.RegistryRead, RemoteTransportKind.RemoteRegistry)]
    [InlineData(RemoteOperationKind.RegistryWrite, RemoteTransportKind.RemoteRegistry)]
    public void GetPreferredOrder_StartsWithOperationSpecificFirstChoice(
        RemoteOperationKind operation, RemoteTransportKind expectedFirst)
    {
        Assert.Equal(expectedFirst, CapabilityMatrix.GetPreferredOrder(operation)[0]);
    }

    [Fact]
    public void BuildFallbackChain_Command_PrefersWinRmThenWmiThenPsExec()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WinRm,
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.Command, available);

        Assert.Equal(
            new[] { RemoteTransportKind.WinRm, RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            chain);
    }

    [Fact]
    public void BuildFallbackChain_Command_ExcludesUnavailableAndUnprobeableChannels()
    {
        var available = new HashSet<RemoteTransportKind> { RemoteTransportKind.WmiDcom };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.Command, available);

        Assert.Equal(new[] { RemoteTransportKind.WmiDcom }, chain);
    }

    [Fact]
    public void BuildFallbackChain_InteractiveLaunch_PrefersPsExecThenWmi()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.PsExec,
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.WinRm,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.InteractiveLaunch, available);

        Assert.Equal(new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom }, chain);
    }

    [Fact]
    public void BuildFallbackChain_Inventory_PrefersWmiThenPsExecThenWinRm()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WinRm,
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.Inventory, available);

        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec, RemoteTransportKind.WinRm },
            chain);
    }

    [Fact]
    public void BuildFallbackChain_Registry_PrefersRemoteRegistryThenWmi()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.RemoteRegistry,
        };

        var readChain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.RegistryRead, available);
        var writeChain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.RegistryWrite, available);

        Assert.Equal(
            new[] { RemoteTransportKind.RemoteRegistry, RemoteTransportKind.WmiDcom },
            readChain);
        Assert.Equal(readChain, writeChain);
    }

    [Fact]
    public void BuildFallbackChain_Registry_FallsBackToWmiOnly()
    {
        var available = new HashSet<RemoteTransportKind> { RemoteTransportKind.WmiDcom };

        Assert.Equal(new[] { RemoteTransportKind.WmiDcom },
            CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.RegistryWrite, available));
    }

    [Fact]
    public void BuildFallbackChain_NoAvailableTransports_ReturnsEmptyChain()
    {
        Assert.Empty(CapabilityMatrix.BuildFallbackChain(
            RemoteOperationKind.Command, new HashSet<RemoteTransportKind>()));
    }
}
