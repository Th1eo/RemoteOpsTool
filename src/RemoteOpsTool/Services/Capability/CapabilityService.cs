using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services.Capability;

/// <summary>
/// Caches capability probe results independently per host, credential and probe
/// item. A short-lived PsExec result can expire without forcing a fresh WMI or
/// network probe, and a failed transport only enters its own cooldown.
/// </summary>
public sealed class CapabilityService : ICapabilityService
{
    private readonly ITransportProbeService _probe;
    private readonly ILogService _log;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, CachedProbeResult> _probeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedSnapshot> _snapshotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityService(ITransportProbeService probe, ILogService log)
        : this(probe, log, static () => DateTimeOffset.UtcNow)
    {
    }

    internal CapabilityService(
        ITransportProbeService probe,
        ILogService log,
        Func<DateTimeOffset> utcNow)
    {
        _probe = probe;
        _log = log;
        _utcNow = utcNow;
    }

    public Task<CapabilitySnapshot> ProbeAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default) =>
        ProbeAsync(host, username, password, CapabilityProbeProfile.Full, ct);

    public async Task<CapabilitySnapshot> ProbeAsync(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
        CancellationToken ct = default)
    {
        var prefix = BuildCachePrefix(host, username, password);
        var snapshotKey = BuildSnapshotKey(prefix, profile);
        var now = _utcNow();
        if (_snapshotCache.TryGetValue(snapshotKey, out var cached) && cached.ExpiresAt > now)
        {
            _log.Debug(
                $"能力探测缓存命中: host={cached.Snapshot.Host} user={cached.Snapshot.UsernameKey} profile={profile}");
            return cached.Snapshot;
        }

        var required = CapabilityProbeCatalog.GetRequiredProbes(profile);
        if (required.Count > 0)
        {
            await RefreshProbesAsync(
                prefix,
                host,
                username,
                password,
                profile,
                required,
                force: false,
                ct);
        }

        return BuildAndCacheSnapshot(host, username, password, profile, required);
    }

    public Task<CapabilitySnapshot> RefreshAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default) =>
        RefreshAsync(host, username, password, CapabilityProbeProfile.Full, ct);

    public async Task<CapabilitySnapshot> RefreshAsync(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
        CancellationToken ct = default)
    {
        var prefix = BuildCachePrefix(host, username, password);
        var required = CapabilityProbeCatalog.GetRequiredProbes(profile);
        if (required.Count > 0)
        {
            await RefreshProbesAsync(
                prefix,
                host,
                username,
                password,
                profile,
                required,
                force: true,
                ct);
        }

        return BuildAndCacheSnapshot(host, username, password, profile, required);
    }

    public void Invalidate(string host, string username, string password)
    {
        var prefix = BuildCachePrefix(host, username, password);
        foreach (var key in _probeCache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _probeCache.TryRemove(key, out _);
        }

        foreach (var key in _snapshotCache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _snapshotCache.TryRemove(key, out _);
        }
    }

    private async Task RefreshProbesAsync(
        string prefix,
        string host,
        string username,
        string password,
        CapabilityProbeProfile requestedProfile,
        IReadOnlyList<string> required,
        bool force,
        CancellationToken ct)
    {
        var gate = _refreshGates.GetOrAdd(prefix, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = _utcNow();
            var missing = required
                .Where(name => force || !IsFresh(prefix, name, now))
                .ToArray();
            if (missing.Length == 0)
                return;

            if (force)
            {
                foreach (var name in required)
                    _probeCache.TryRemove(BuildProbeKey(prefix, name), out _);
            }

            var fetchProfile = CapabilityProbeCatalog.SelectFetchProfile(requestedProfile, missing);
            var results = await _probe.ProbeCapabilitiesAsync(host, username, password, fetchProfile, ct);
            var observedAt = _utcNow();
            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.Name))
                    continue;

                _probeCache[BuildProbeKey(prefix, result.Name)] =
                    CachedProbeResult.FromResult(result, observedAt);
                returned.Add(result.Name);
            }

            // Remember probes that did not report a result. This prevents every
            // subsequent operation from replaying an expensive missing probe.
            foreach (var name in required.Where(name => !returned.Contains(name)))
            {
                _probeCache[BuildProbeKey(prefix, name)] =
                    CachedProbeResult.Missing(name, observedAt);
            }

            _log.Debug(
                $"能力探测刷新: host={HostHelper.NormalizeHost(host)} profile={fetchProfile} missing={missing.Length} returned={results.Count}");
        }
        finally
        {
            gate.Release();
        }
    }

    private CapabilitySnapshot BuildAndCacheSnapshot(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
        IReadOnlyList<string> required)
    {
        var prefix = BuildCachePrefix(host, username, password);
        var now = _utcNow();
        var rawResults = GetAllFreshResults(prefix, required, now);
        var (available, unavailable) = CapabilityMatrix.ResolveTransports(rawResults);
        var snapshot = new CapabilitySnapshot
        {
            Host = HostHelper.NormalizeHost(host),
            UsernameKey = BuildCredentialKey(username),
            CredentialFingerprint = ComputeCredentialFingerprint(username, password),
            CapturedAt = now,
            Profile = profile,
            AvailableTransports = available,
            UnavailableTransports = unavailable,
            RawResults = rawResults,
        };
        snapshot.InitializeProbeResults(rawResults);

        var expiresAt = GetSnapshotExpiry(prefix, required, now);
        _snapshotCache[BuildSnapshotKey(prefix, profile)] = new CachedSnapshot(snapshot, expiresAt);
        _log.Debug(
            $"能力快照构建: host={snapshot.Host} user={snapshot.UsernameKey} profile={profile} available=[{string.Join(",", available)}]");
        return snapshot;
    }

    private IReadOnlyList<RemoteCapabilityInfo> GetAllFreshResults(
        string prefix,
        IReadOnlyList<string> required,
        DateTimeOffset now)
    {
        var results = new Dictionary<string, RemoteCapabilityInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _probeCache)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !pair.Value.WasReturned ||
                !pair.Value.IsFresh(now))
            {
                continue;
            }

            results[pair.Value.Name] = pair.Value.Result!;
        }

        // Keep the raw result order stable for callers that display diagnostics.
        var ordered = new List<RemoteCapabilityInfo>();
        foreach (var name in required)
        {
            if (results.Remove(name, out var result))
                ordered.Add(result);
        }

        ordered.AddRange(results
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value));
        return ordered;
    }

    private DateTimeOffset GetSnapshotExpiry(
        string prefix,
        IReadOnlyList<string> required,
        DateTimeOffset now)
    {
        if (required.Count == 0)
            return now + CapabilityCachePolicy.GetMissingTtl();

        var min = DateTimeOffset.MaxValue;
        foreach (var name in required)
        {
            if (_probeCache.TryGetValue(BuildProbeKey(prefix, name), out var cached))
                min = cached.ExpiresAt < min ? cached.ExpiresAt : min;
            else
                min = now + CapabilityCachePolicy.GetMissingTtl() < min
                    ? now + CapabilityCachePolicy.GetMissingTtl()
                    : min;
        }

        return min;
    }

    private bool IsFresh(string prefix, string probeName, DateTimeOffset now) =>
        _probeCache.TryGetValue(BuildProbeKey(prefix, probeName), out var cached) &&
        cached.IsFresh(now);

    private static string BuildCachePrefix(string host, string username, string password) =>
        $"{HostHelper.NormalizeHost(host)}|{BuildCredentialKey(username)}|{ComputeCredentialFingerprint(username, password)}|";

    private static string BuildProbeKey(string prefix, string probeName) => prefix + probeName;

    private static string BuildSnapshotKey(string prefix, CapabilityProbeProfile profile) =>
        $"{prefix}snapshot|{profile}";

    private static string BuildCredentialKey(string username) =>
        string.IsNullOrWhiteSpace(username) ? "<current-user>" : username.Trim();

    private static string ComputeCredentialFingerprint(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
            return "<empty>";

        // The route-learning key must distinguish two accounts that happen to
        // use the same password on the same host.
        var material = $"{BuildCredentialKey(username)}\n{password}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    private sealed record CachedProbeResult(
        string Name,
        RemoteCapabilityInfo? Result,
        DateTimeOffset ExpiresAt,
        bool WasReturned)
    {
        public bool IsFresh(DateTimeOffset now) => ExpiresAt > now;

        public static CachedProbeResult FromResult(RemoteCapabilityInfo result, DateTimeOffset observedAt) =>
            new(
                result.Name,
                result,
                CapabilityCachePolicy.GetExpiresAt(result.Name, result, observedAt),
                WasReturned: true);

        public static CachedProbeResult Missing(string name, DateTimeOffset observedAt) =>
            new(
                name,
                null,
                observedAt + CapabilityCachePolicy.GetMissingTtl(),
                WasReturned: false);
    }

    private sealed record CachedSnapshot(
        CapabilitySnapshot Snapshot,
        DateTimeOffset ExpiresAt);
}
