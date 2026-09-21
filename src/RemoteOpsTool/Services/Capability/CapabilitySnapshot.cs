using System.Collections.Concurrent;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>One point-in-time capability snapshot for a host/credential pair.</summary>
/// <remarks>
/// The snapshot is cached by <see cref="CapabilityService"/> and intentionally
/// carries operation-specific health plus the learned route for each operation.
/// This lets later sessions reuse a known-good transport without repeating the
/// full probe matrix while keeping, for example, PsExec command health separate
/// from interactive PsExec health.
/// </remarks>
public sealed class CapabilitySnapshot
{
    private const string WmiProbeName = "WMI/DCOM";
    private const string PsExecProbeName = "PsExec 临时执行";
    private const string ScheduledTaskProbeName = "计划任务 RPC";

    private readonly ConcurrentDictionary<RemoteOperationKind, RemoteTransportKind> _preferredTransports = new();
    private readonly ConcurrentDictionary<OperationCapabilityKey, OperationCapability> _operationCapabilities = new();

    public string Host { get; init; } = string.Empty;
    public string UsernameKey { get; init; } = string.Empty;
    public string CredentialFingerprint { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlySet<RemoteTransportKind> AvailableTransports { get; init; } =
        new HashSet<RemoteTransportKind>();
    public IReadOnlySet<RemoteTransportKind> UnavailableTransports { get; init; } =
        new HashSet<RemoteTransportKind>();
    public IReadOnlyList<RemoteCapabilityInfo> RawResults { get; init; } = [];

    /// <summary>Returns the learned first-choice transport for each operation.</summary>
    public IReadOnlyDictionary<RemoteOperationKind, RemoteTransportKind> PreferredTransports =>
        _preferredTransports;

    public bool TryGetPreferredTransport(
        RemoteOperationKind operation,
        out RemoteTransportKind transport) =>
        _preferredTransports.TryGetValue(operation, out transport);

    public OperationCapability GetOperationCapability(
        RemoteOperationKind operation,
        RemoteTransportKind transport)
    {
        var key = new OperationCapabilityKey(operation, transport);
        if (_operationCapabilities.TryGetValue(key, out var capability) &&
            !capability.IsExpired(DateTimeOffset.UtcNow))
        {
            return capability;
        }

        return OperationCapability.Unknown(key, DateTimeOffset.UtcNow);
    }

    public void RecordTransportSuccess(
        RemoteOperationKind operation,
        RemoteTransportKind transport,
        string detail = "")
    {
        var now = DateTimeOffset.UtcNow;
        _preferredTransports[operation] = transport;
        _operationCapabilities[new OperationCapabilityKey(operation, transport)] =
            CapabilityPolicy.CreateAvailable(operation, transport, now, detail);
    }

    /// <summary>Records a classified transport failure for one operation only.</summary>
    public void RecordTransportFailure(
        RemoteOperationKind operation,
        RemoteTransportKind transport,
        CommandResult result,
        TimeSpan? cooldown = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (_preferredTransports.TryGetValue(operation, out var preferred) && preferred == transport)
            _preferredTransports.TryRemove(operation, out _);

        var failureKind = CapabilityPolicy.ClassifyResult(result);
        _operationCapabilities[new OperationCapabilityKey(operation, transport)] =
            CapabilityPolicy.CreateFailure(
                operation,
                transport,
                failureKind,
                TransportFailureClassifier.SummarizeCommandFailure(result),
                now,
                cooldown);
    }

    /// <summary>
    /// Backward-compatible unclassified cooldown used by callers that only know
    /// that a transport attempt failed. New transport code should pass the result.
    /// </summary>
    public void RecordTransportFailure(
        RemoteOperationKind operation,
        RemoteTransportKind transport,
        TimeSpan? cooldown = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (_preferredTransports.TryGetValue(operation, out var preferred) && preferred == transport)
            _preferredTransports.TryRemove(operation, out _);

        _operationCapabilities[new OperationCapabilityKey(operation, transport)] =
            CapabilityPolicy.CreateFailure(
                operation,
                transport,
                CapabilityFailureKind.Unknown,
                "transport failure",
                now,
                cooldown);
    }

    /// <summary>
    /// Applies a transport failure to the operation-specific route ordering, but
    /// never removes the channel from a fallback chain. Only an actual transport
    /// failure can call this method; command failures must not be replayed.
    /// </summary>
    public bool IsTransportCoolingDown(
        RemoteOperationKind operation,
        RemoteTransportKind transport)
    {
        var capability = GetOperationCapability(operation, transport);
        return !capability.IsRouteReady(DateTimeOffset.UtcNow) &&
               capability.Health != CapabilityHealth.Unknown;
    }

    /// <summary>Seeds operation health from the complete probe matrix.</summary>
    public void InitializeProbeResults(IEnumerable<RemoteCapabilityInfo> probeResults)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var probe in probeResults)
        {
            if (!TryResolveProbe(probe.Name, out var transport))
                continue;

            switch (transport)
            {
                case RemoteTransportKind.WmiDcom:
                    ApplyProbe(
                        probe,
                        now,
                        transport,
                        [RemoteOperationKind.Inventory, RemoteOperationKind.RegistryRead, RemoteOperationKind.RegistryWrite]);
                    if (!probe.Success)
                        ApplyProbe(probe, now, transport, [RemoteOperationKind.Command]);
                    break;

                case RemoteTransportKind.PsExec:
                    ApplyProbe(probe, now, transport, [RemoteOperationKind.Command]);
                    break;

                case RemoteTransportKind.ScheduledTask:
                    ApplyProbe(probe, now, transport, [RemoteOperationKind.InteractiveLaunch]);
                    break;
            }
        }
    }

    /// <summary>Applies persisted route learning without storing command text or passwords.</summary>
    public void ApplyRouteLearning(IReadOnlyList<RouteLearningRecord> records)
    {
        foreach (var operation in Enum.GetValues<RemoteOperationKind>())
        {
            if (RouteLearningPolicy.TrySelectPreferred(records, operation, out var transport))
                _preferredTransports[operation] = transport;
        }
    }

    private static bool TryResolveProbe(string probeName, out RemoteTransportKind transport)
    {
        if (probeName.Equals(WmiProbeName, StringComparison.OrdinalIgnoreCase))
        {
            transport = RemoteTransportKind.WmiDcom;
            return true;
        }

        if (probeName.Equals(PsExecProbeName, StringComparison.OrdinalIgnoreCase))
        {
            transport = RemoteTransportKind.PsExec;
            return true;
        }

        if (probeName.Equals(ScheduledTaskProbeName, StringComparison.OrdinalIgnoreCase))
        {
            transport = RemoteTransportKind.ScheduledTask;
            return true;
        }

        transport = default;
        return false;
    }

    private void ApplyProbe(
        RemoteCapabilityInfo probe,
        DateTimeOffset now,
        RemoteTransportKind transport,
        IReadOnlyList<RemoteOperationKind> operations)
    {
        foreach (var operation in operations)
        {
            _operationCapabilities[new OperationCapabilityKey(operation, transport)] =
                probe.Success
                    ? CapabilityPolicy.CreateAvailable(operation, transport, now, probe.Detail)
                    : CapabilityPolicy.CreateFailure(
                        operation,
                        transport,
                        CapabilityPolicy.ClassifyProbeFailure(probe.Detail),
                        probe.Detail,
                        now);
        }
    }
}
