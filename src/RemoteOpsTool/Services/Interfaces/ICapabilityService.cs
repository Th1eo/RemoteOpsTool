using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Interfaces;

/// <summary>
/// Provides cached capability snapshots and explicit refresh for the UI's
/// diagnostic command. Normal execution uses ProbeAsync and reuses a snapshot
/// within its TTL.
/// </summary>
public interface ICapabilityService
{
    Task<CapabilitySnapshot> ProbeAsync(string host, string username, string password, CancellationToken ct = default);

    Task<CapabilitySnapshot> RefreshAsync(string host, string username, string password, CancellationToken ct = default);

    void Invalidate(string host, string username, string password);
}
