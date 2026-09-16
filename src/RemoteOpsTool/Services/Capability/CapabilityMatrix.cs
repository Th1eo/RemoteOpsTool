using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>
/// Pure policy object that turns probe results into an ordered fallback chain per
/// operation class. Keeping this free of I/O makes it cheap to test every
/// possible availability combination.
/// </summary>
internal static class CapabilityMatrix
{
    private static readonly RemoteTransportKind[][] PreferredOrder =
    [
        // Command: modern first, legacy PsExec last.
        [RemoteTransportKind.WinRm, RemoteTransportKind.WmiDcom, RemoteTransportKind.ScmRpc, RemoteTransportKind.PsExec, RemoteTransportKind.ScheduledTask],
        // InteractiveLaunch: PsExec -i first because it is the only mature path for a live desktop.
        [RemoteTransportKind.PsExec, RemoteTransportKind.ScheduledTask, RemoteTransportKind.WmiDcom],
        // Inventory: WMI is the richest query surface, SCM/RPC the best fallback.
        [RemoteTransportKind.WmiDcom, RemoteTransportKind.ScmRpc, RemoteTransportKind.PsExec, RemoteTransportKind.WinRm],
        // Registry.
        [RemoteTransportKind.RemoteRegistry, RemoteTransportKind.WmiDcom],
        // Registry writes use the same preference order as reads.
        [RemoteTransportKind.RemoteRegistry, RemoteTransportKind.WmiDcom],
    ];

    private static readonly IReadOnlyDictionary<string, RemoteTransportKind> ProbeToTransport =
        new Dictionary<string, RemoteTransportKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["WinRM 5985"] = RemoteTransportKind.WinRm,
            ["WMI/DCOM"] = RemoteTransportKind.WmiDcom,
            ["PsExec 临时执行"] = RemoteTransportKind.PsExec,
        };

    /// <summary>Resolve which transports are currently proven available from raw probe results.</summary>
    public static IReadOnlySet<RemoteTransportKind> ResolveAvailableTransports(
        IEnumerable<RemoteCapabilityInfo> probeResults)
    {
        var available = new HashSet<RemoteTransportKind>();
        foreach (var probe in probeResults)
        {
            if (!probe.Success)
                continue;
            if (ProbeToTransport.TryGetValue(probe.Name, out var kind))
                available.Add(kind);
        }
        return available;
    }

    /// <summary>Resolve both available and known-unavailable transports.</summary>
    public static (IReadOnlySet<RemoteTransportKind> Available, IReadOnlySet<RemoteTransportKind> Unavailable)
        ResolveTransports(IEnumerable<RemoteCapabilityInfo> probeResults)
    {
        var available = new HashSet<RemoteTransportKind>();
        var knownKinds = new HashSet<RemoteTransportKind>
        {
            RemoteTransportKind.WinRm,
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
        };

        foreach (var probe in probeResults)
        {
            if (!ProbeToTransport.TryGetValue(probe.Name, out var kind))
                continue;
            if (probe.Success)
                available.Add(kind);
            else
                knownKinds.Add(kind);
        }

        var unavailable = knownKinds.Where(kind => !available.Contains(kind)).ToHashSet();
        return (available, unavailable);
    }

    public static IReadOnlyList<RemoteTransportKind> BuildFallbackChain(
        RemoteOperationKind operation,
        IReadOnlySet<RemoteTransportKind> available)
    {
        var order = PreferredOrder[(int)operation];
        return order.Where(available.Contains).ToArray();
    }

    internal static IReadOnlyList<RemoteTransportKind> GetPreferredOrder(RemoteOperationKind operation) =>
        PreferredOrder[(int)operation];
}
