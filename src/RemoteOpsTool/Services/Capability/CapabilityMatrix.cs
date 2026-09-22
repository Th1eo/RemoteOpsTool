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
    // PsExec is the unified credentialed execution channel: the launcher runs
    // under the selected credential and every invocation carries explicit -u/-p.
    // WMI/DCOM is the default for cheap commands and read-only queries; PsExec
    // remains the credentialed fallback and the explicit first choice for scripts.
    private static readonly RemoteTransportKind[][] PreferredOrder =
    [
        // Command: WMI/DCOM first to avoid starting PSEXESVC for every short
        // command. Scripts can explicitly request the streaming PsExec path.
        [RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec],
        // InteractiveLaunch: the verified PsExec desktop path first, then the
        // WMI-created one-shot task and finally direct Task Scheduler RPC.
        [RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom, RemoteTransportKind.ScheduledTask],
        // Inventory: WMI is the richest query surface, PsExec is the fallback.
        [RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec],
        // Registry uses WMI/DCOM first; PsExec can run remote registry PowerShell
        // when WMI transport is blocked by endpoint policy.
        [RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec],
        [RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec],
    ];

    // Payloads larger than the RunAs launcher's command-line budget must not be
    // started through CreateProcessWithLogonW, so they begin on WMI/DCOM and
    // keep PsExec only as the fallback.
    private static readonly RemoteTransportKind[] WmiFirstCommandOrder =
        [RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec];

    private static readonly IReadOnlyDictionary<string, RemoteTransportKind> ProbeToTransport =
        new Dictionary<string, RemoteTransportKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["WMI/DCOM"] = RemoteTransportKind.WmiDcom,
            ["PsExec 临时执行"] = RemoteTransportKind.PsExec,
            ["计划任务 RPC"] = RemoteTransportKind.ScheduledTask,
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
            RemoteTransportKind.WmiDcom,
            RemoteTransportKind.PsExec,
            RemoteTransportKind.ScheduledTask,
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
        IReadOnlySet<RemoteTransportKind> available,
        bool preferWmiForCommands = false)
    {
        var order = preferWmiForCommands && operation == RemoteOperationKind.Command
            ? WmiFirstCommandOrder
            : PreferredOrder[(int)operation];
        return order.Where(available.Contains).ToArray();
    }

    /// <summary>
    /// Interactive desktop launch uses a different PsExec shape (credentialed
    /// RunAs plus <c>-i &lt;session&gt; -d</c>) than the ordinary command probe.
    /// A failed temporary-execution probe therefore must not remove PsExec from
    /// the real launch chain. WMI/DCOM and ScheduledTask remain safe fallbacks;
    /// an actual launch transport failure is what advances the chain.
    /// </summary>
    public static IReadOnlyList<RemoteTransportKind> BuildInteractiveFallbackChain() =>
        PreferredOrder[(int)RemoteOperationKind.InteractiveLaunch];
    internal static IReadOnlyList<RemoteTransportKind> GetPreferredOrder(RemoteOperationKind operation) =>
        PreferredOrder[(int)operation];
}
