using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>One point-in-time capability probe result for a host/credential pair.</summary>
public sealed class CapabilitySnapshot
{
    public string Host { get; init; } = string.Empty;
    public string UsernameKey { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlySet<RemoteTransportKind> AvailableTransports { get; init; } =
        new HashSet<RemoteTransportKind>();
    public IReadOnlySet<RemoteTransportKind> UnavailableTransports { get; init; } =
        new HashSet<RemoteTransportKind>();
    public IReadOnlyList<RemoteCapabilityInfo> RawResults { get; init; } = [];
}
