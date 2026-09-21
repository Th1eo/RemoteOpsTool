using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services.Capability;

public sealed class CapabilityService : ICapabilityService
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(5);
    private readonly ITransportProbeService _probe;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityService(ITransportProbeService probe, ILogService log)
    {
        _probe = probe;
        _log = log;
    }

    public async Task<CapabilitySnapshot> ProbeAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var key = BuildCacheKey(host, username, password);
        var entry = _cache.GetOrAdd(key, static _ => new CacheEntry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Snapshot is { } cached && DateTimeOffset.UtcNow - cached.CapturedAt < SnapshotTtl)
            {
                _log.Debug($"能力探测缓存命中: host={cached.Host} user={cached.UsernameKey}");
                return cached;
            }

            var snapshot = await ProbeCoreAsync(host, username, password, ct);
            entry.Snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<CapabilitySnapshot> RefreshAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var key = BuildCacheKey(host, username, password);
        var entry = _cache.GetOrAdd(key, static _ => new CacheEntry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            var snapshot = await ProbeCoreAsync(host, username, password, ct);
            entry.Snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public void Invalidate(string host, string username, string password) =>
        _cache.TryRemove(BuildCacheKey(host, username, password), out _);

    private async Task<CapabilitySnapshot> ProbeCoreAsync(string host, string username, string password, CancellationToken ct)
    {
        var rawResults = await _probe.ProbeCapabilitiesAsync(host, username, password, ct);
        var (available, unavailable) = CapabilityMatrix.ResolveTransports(rawResults);
        var snapshot = new CapabilitySnapshot
        {
            Host = HostHelper.NormalizeHost(host),
            UsernameKey = BuildCredentialKey(username),
            CredentialFingerprint = ComputeCredentialFingerprint(username, password),
            CapturedAt = DateTimeOffset.UtcNow,
            AvailableTransports = available,
            UnavailableTransports = unavailable,
            RawResults = rawResults,
        };
        snapshot.InitializeProbeResults(rawResults);
        _log.Debug($"能力探测完成: host={snapshot.Host} user={snapshot.UsernameKey} available=[{string.Join(",", available)}]");
        return snapshot;
    }

    private static string BuildCacheKey(string host, string username, string password) =>
        $"{HostHelper.NormalizeHost(host)}|{BuildCredentialKey(username)}|{ComputeCredentialFingerprint(username, password)}";

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

    private sealed class CacheEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CapabilitySnapshot? Snapshot { get; set; }
    }
}
