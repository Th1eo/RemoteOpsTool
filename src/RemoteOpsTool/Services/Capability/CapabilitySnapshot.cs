using System.Collections.Concurrent;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>One point-in-time capability snapshot for a host/credential pair.</summary>
/// <remarks>
/// The snapshot is cached by <see cref="CapabilityService"/> and intentionally
/// carries the learned route for each operation. This lets later sessions reuse
/// a known-good transport without repeating the full probe matrix.
/// </remarks>
public sealed class CapabilitySnapshot
{
    private readonly ConcurrentDictionary<RemoteOperationKind, RemoteTransportKind> _preferredTransports = new();
    private readonly ConcurrentDictionary<RemoteTransportKind, DateTimeOffset> _transportCooldownUntil = new();

    public string Host { get; init; } = string.Empty;
    public string UsernameKey { get; init; } = string.Empty;
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

    public void RecordTransportSuccess(
        RemoteOperationKind operation,
        RemoteTransportKind transport)
    {
        _preferredTransports[operation] = transport;
        _transportCooldownUntil.TryRemove(transport, out _);
    }

    /// <summary>
    /// Prevents a just-failed channel from being selected as the first choice by
    /// a concurrent/new operation while still keeping it eligible as a fallback.
    /// </summary>
    public void RecordTransportFailure(
        RemoteTransportKind transport,
        TimeSpan? cooldown = null)
    {
        _transportCooldownUntil[transport] = DateTimeOffset.UtcNow +
            (cooldown ?? TimeSpan.FromSeconds(30));
    }

    public bool IsTransportCoolingDown(RemoteTransportKind transport) =>
        _transportCooldownUntil.TryGetValue(transport, out var until) &&
        until > DateTimeOffset.UtcNow;
}
