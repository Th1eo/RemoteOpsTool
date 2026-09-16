using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>
/// Caches capability snapshots per host/credential and asks <see cref="CapabilityMatrix"/>
/// for the fallback order. Phase 1 keeps this as the decision source of truth;
/// Phase 2 will have the operation coordinator consume it.
/// </summary>
public sealed class CapabilityService : ICapabilityService
{
    private readonly INetworkService _network;
    private readonly ILogService _log;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _snapshotTtl;
    private readonly Dictionary<string, CachedSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityService(INetworkService network, ILogService log)
        : this(network, log, TimeProvider.System, TimeSpan.FromMinutes(5))
    {
    }

    internal CapabilityService(
        INetworkService network,
        ILogService log,
        TimeProvider timeProvider,
        TimeSpan snapshotTtl)
    {
        _network = network;
        _log = log;
        _timeProvider = timeProvider;
        _snapshotTtl = snapshotTtl;
    }

    public Task<CapabilitySnapshot> ProbeAsync(string host, string username, string password, CancellationToken ct = default) =>
        ProbeCoreAsync(host, username, password, ct);

    public async Task<CapabilitySnapshot> GetOrProbeAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (TryGetFreshCached(host, username, out var snapshot))
            return snapshot!;

        return await ProbeCoreAsync(host, username, password, ct);
    }

    public CapabilitySnapshot? GetCached(string host, string username) =>
        TryGetFreshCached(host, username, out var snapshot) ? snapshot : null;

    public void Invalidate(string host, string username)
    {
        var key = BuildKey(host, username);
        lock (_cache)
            _cache.Remove(key);
    }

    public async Task<IReadOnlyList<RemoteTransportKind>> GetFallbackChainAsync(
        string host,
        string username,
        string password,
        RemoteOperationKind operation,
        CancellationToken ct = default)
    {
        var snapshot = await GetOrProbeAsync(host, username, password, ct);
        return CapabilityMatrix.BuildFallbackChain(operation, snapshot.AvailableTransports);
    }

    private bool TryGetFreshCached(string host, string username, out CapabilitySnapshot? snapshot)
    {
        var key = BuildKey(host, username);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _timeProvider.GetUtcNow())
            {
                snapshot = cached.Snapshot;
                return true;
            }
        }

        snapshot = null;
        return false;
    }

    private async Task<CapabilitySnapshot> ProbeCoreAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        var rawResults = await _network.ProbeCapabilitiesAsync(host, username, password, ct);
        var (available, unavailable) = CapabilityMatrix.ResolveTransports(rawResults);
        var snapshot = new CapabilitySnapshot
        {
            Host = HostHelper.NormalizeHost(host),
            UsernameKey = BuildCredentialKey(username),
            CapturedAt = _timeProvider.GetUtcNow(),
            AvailableTransports = available,
            UnavailableTransports = unavailable,
            RawResults = rawResults,
        };

        lock (_cache)
            _cache[BuildKey(host, username)] = new CachedSnapshot(snapshot, _timeProvider.GetUtcNow().Add(_snapshotTtl));

        _log.Debug($"能力探测完成: host={snapshot.Host} user={snapshot.UsernameKey} available=[{string.Join(",", available)}]");
        return snapshot;
    }

    private static string BuildKey(string host, string username) =>
        $"{HostHelper.NormalizeHost(host)}\n{BuildCredentialKey(username)}";

    private static string BuildCredentialKey(string username) =>
        string.IsNullOrWhiteSpace(username) ? "<current-user>" : username.Trim();

    private sealed record CachedSnapshot(CapabilitySnapshot Snapshot, DateTimeOffset ExpiresAt);
}
