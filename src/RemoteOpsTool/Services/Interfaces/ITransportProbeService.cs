using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

/// <summary>
/// Builds a fresh, stateless capability report for one host/credential pair.
/// The probe is intentionally independent of the capability-aware router so the
/// router can depend on probe results without a dependency cycle, and results
/// must never be cached by any implementation or caller.
/// </summary>
public interface ITransportProbeService
{
    Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default);
}