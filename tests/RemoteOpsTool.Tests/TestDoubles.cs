using System.Collections.ObjectModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Tests;

internal sealed class TestSettingsService : ISettingsService
{
    public AppSettings Settings { get; } = new();
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
}

internal sealed class TestLogService : ILogService
{
    public ObservableCollection<LogEntry> Entries { get; } = [];
    public LogLevel FilterLevel { get; set; }
    public bool IsExecuting { get; set; }
    public bool FileLogEnabled { get; set; }
    public event Action<LogEntry>? EntryAppended { add { } remove { } }
    public event Action? LogCleared { add { } remove { } }
    public event Action? LogRebuilt { add { } remove { } }
    public event Action<bool>? ExecutingChanged { add { } remove { } }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Debug(string message) { }
    public void Log(LogLevel level, string message) { }
    public void Clear() { }
}

internal sealed class FakeNetworkService : INetworkService
{
    public List<RemoteCapabilityInfo> ProbeResults { get; set; } = [];
    public int ProbeCallCount { get; private set; }
    public List<string> ProbeHosts { get; } = [];
    public List<string> ProbeUsernames { get; } = [];

    public Task<PingResult> PingAsync(string host, CancellationToken ct = default) =>
        Task.FromResult(new PingResult(true, string.Empty));

    public Task FlushDnsAsync(string host, string username, string password, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RefreshIpAsync(string host, string username, string password, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<NetworkConnectionInfo>> GetActiveConnectionsAsync(
        string host, string username, string password, CancellationToken ct = default) =>
        Task.FromResult(new List<NetworkConnectionInfo>());

    public Task<List<ProcessDetailInfo>> GetProcessListAsync(
        string host, string username, string password, CancellationToken ct = default) =>
        Task.FromResult(new List<ProcessDetailInfo>());

    public Task<bool> KillProcessAsync(
        string host, string username, string password, int processId, bool killTree, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<List<UserSessionInfo>> GetUserSessionsAsync(
        string host, string username, string password, CancellationToken ct = default) =>
        Task.FromResult(new List<UserSessionInfo>());

    public Task<bool> SignOutUserAsync(
        string host, string username, string password, int sessionId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        ProbeCallCount++;
        ProbeHosts.Add(host);
        ProbeUsernames.Add(username);
        return Task.FromResult(ProbeResults.ToList());
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan amount) => UtcNow += amount;
}
