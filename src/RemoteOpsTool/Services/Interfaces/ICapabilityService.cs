using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Interfaces;

/// <summary>
/// Probes a remote host's available management transports, caches the result, and
/// exposes ordered fallback chains per operation class.
/// </summary>
public interface ICapabilityService
{
    Task<CapabilitySnapshot> ProbeAsync(string host, string username, string password, CancellationToken ct = default);

    Task<CapabilitySnapshot> GetOrProbeAsync(string host, string username, string password, CancellationToken ct = default);

    CapabilitySnapshot? GetCached(string host, string username);

    void Invalidate(string host, string username);

    Task<IReadOnlyList<RemoteTransportKind>> GetFallbackChainAsync(
        string host,
        string username,
        string password,
        RemoteOperationKind operation,
        CancellationToken ct = default);
}
