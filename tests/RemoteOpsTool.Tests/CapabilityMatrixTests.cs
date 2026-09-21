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
            Probe("WMI/DCOM", false),
            Probe("PsExec 临时执行", true),
            Probe("计划任务 RPC", true),
            Probe("unknown probe", true),
        };

        var available = CapabilityMatrix.ResolveAvailableTransports(results);

        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.ScheduledTask },
            available.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void ResolveTransports_AllAvailable_LeavesUnavailableEmpty()
    {
        var results = new[]
        {
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true),
            Probe("计划任务 RPC", true),
        };

        var (available, unavailable) = CapabilityMatrix.ResolveTransports(results);

        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec, RemoteTransportKind.ScheduledTask },
            available.OrderBy(kind => (int)kind));
        Assert.Empty(unavailable);
    }

    [Fact]
    public void ResolveTransports_MixedResults_SplitsAvailableAndUnavailable()
    {
        var results = new[]
        {
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", false),
            Probe("计划任务 RPC", false),
        };

        var (available, unavailable) = CapabilityMatrix.ResolveTransports(results);

        Assert.Equal(new[] { RemoteTransportKind.WmiDcom }, available);
        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.ScheduledTask },
            unavailable.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void ResolveTransports_NoneAvailable_MarksAllProbeableKindsUnavailable()
    {
        var results = new[]
        {
            Probe("WMI/DCOM", false),
            Probe("PsExec 临时执行", false),
            Probe("计划任务 RPC", false),
        };

        var (available, unavailable) = CapabilityMatrix.ResolveTransports(results);

        Assert.Empty(available);
        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec, RemoteTransportKind.ScheduledTask },
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
    [InlineData(RemoteOperationKind.Command, RemoteTransportKind.PsExec)]
    [InlineData(RemoteOperationKind.InteractiveLaunch, RemoteTransportKind.PsExec)]
    [InlineData(RemoteOperationKind.Inventory, RemoteTransportKind.WmiDcom)]
    [InlineData(RemoteOperationKind.RegistryRead, RemoteTransportKind.WmiDcom)]
    [InlineData(RemoteOperationKind.RegistryWrite, RemoteTransportKind.WmiDcom)]
    public void GetPreferredOrder_StartsWithOperationSpecificFirstChoice(
        RemoteOperationKind operation, RemoteTransportKind expectedFirst)
    {
        Assert.Equal(expectedFirst, CapabilityMatrix.GetPreferredOrder(operation)[0]);
    }

    [Fact]
    public void PreferredOrder_OnlyUsesImplementedTransports()
    {
        var implemented = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
            RemoteTransportKind.ScheduledTask,
        };

        foreach (var operation in Enum.GetValues<RemoteOperationKind>())
        {
            foreach (var transport in CapabilityMatrix.GetPreferredOrder(operation))
                Assert.Contains(transport, implemented);
        }
    }

    [Fact]
    public void BuildFallbackChain_Command_PrefersPsExecThenWmi()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.Command, available);

        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom },
            chain);
    }

    [Fact]
    public void BuildFallbackChain_Command_LongPayloadPrefersWmi()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(
            RemoteOperationKind.Command,
            available,
            preferWmiForCommands: true);

        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
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
    public void BuildFallbackChain_InteractiveLaunch_FullAvailabilityIsPsExecThenWmiThenScheduledTask()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.PsExec,
            RemoteTransportKind.ScheduledTask,
            RemoteTransportKind.WmiDcom,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.InteractiveLaunch, available);

        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom, RemoteTransportKind.ScheduledTask },
            chain);
    }

    [Fact]
    public void BuildFallbackChain_Inventory_PrefersWmiThenPsExec()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
        };

        var chain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.Inventory, available);

        Assert.Equal(
            new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            chain);
    }

    [Fact]
    public void BuildFallbackChain_Registry_PrefersWmiThenPsExec()
    {
        var available = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
            RemoteTransportKind.RemoteRegistry,
        };

        var readChain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.RegistryRead, available);
        var writeChain = CapabilityMatrix.BuildFallbackChain(RemoteOperationKind.RegistryWrite, available);

        Assert.Equal(new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec }, readChain);
        Assert.Equal(new[] { RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec }, writeChain);
        Assert.DoesNotContain(RemoteTransportKind.RemoteRegistry, readChain);
        Assert.DoesNotContain(RemoteTransportKind.RemoteRegistry, writeChain);
    }

    [Fact]
    public void BuildInteractiveFallbackChain_IsNotFilteredByProbeAvailability()
    {
        Assert.Equal(
            new[]
            {
                RemoteTransportKind.PsExec,
                RemoteTransportKind.WmiDcom,
                RemoteTransportKind.ScheduledTask,
            },
            CapabilityMatrix.BuildInteractiveFallbackChain());
    }
    [Fact]
    public void BuildFallbackChain_NoAvailableTransports_ReturnsEmptyChain()
    {
        Assert.Empty(CapabilityMatrix.BuildFallbackChain(
            RemoteOperationKind.Command, new HashSet<RemoteTransportKind>()));
    }
}
