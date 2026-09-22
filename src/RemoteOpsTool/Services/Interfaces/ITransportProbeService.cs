using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;

namespace RemoteOpsTool.Services.Interfaces;

/// <summary>
/// Builds a fresh, stateless capability report for one host/credential pair.
/// The probe is intentionally independent of the capability-aware router so the
/// router can depend on probe results without a dependency cycle. Results are
/// cached by <see cref="ICapabilityService"/>, not by the probe.
/// </summary>
public interface ITransportProbeService
{
    /// <summary>Runs the full compatibility probe.</summary>
    Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default);

    /// <summary>Runs only the probes required by the supplied operation profile.</summary>
    Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
        CancellationToken ct = default);
}
