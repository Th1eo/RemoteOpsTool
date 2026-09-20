using System.Collections.ObjectModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

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


    public Task<List<UserSessionInfo>> GetUserSessionsWithSessionAsync(
        IRemoteExecutionSession session,
        string host, string username, string password, CancellationToken ct = default) =>
        Task.FromResult(new List<UserSessionInfo>());

    public Task<bool> SignOutUserAsync(
        string host, string username, string password, int sessionId, CancellationToken ct = default) =>
        Task.FromResult(true);
}

internal sealed class FakeTransportProbeService : ITransportProbeService
{
    public List<RemoteCapabilityInfo> ProbeResults { get; set; } = [];
    public int ProbeCallCount { get; private set; }
    public List<string> ProbeHosts { get; } = [];
    public List<string> ProbeUsernames { get; } = [];

    public Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        ProbeCallCount++;
        ProbeHosts.Add(host);
        ProbeUsernames.Add(username);
        return Task.FromResult(ProbeResults.ToList());
    }
}

internal sealed class FakeRemoteCommandExecutor : IRemoteCommandExecutor
{
    public List<RemoteTransportKind> CallOrder { get; } = [];
    public List<RemoteCommand> PsExecCommands { get; } = [];
    public List<RemoteCommand> WmiCommands { get; } = [];
    public List<RemoteCommand> InteractivePsExecCommands { get; } = [];
    public List<RemoteCommand> InteractiveWmiCommands { get; } = [];
    public List<RemoteCommand> InteractiveScheduledTaskCommands { get; } = [];

    public Action<string>? PsExecOutputLine { get; set; }

    public Func<RemoteCommand, CancellationToken, Task<TransportResult>>? PsExecHandler { get; set; }
    public Func<RemoteCommand, CancellationToken, Task<TransportResult>>? WmiHandler { get; set; }
    public Func<RemoteCommand, CancellationToken, Task<TransportResult>>? InteractivePsExecHandler { get; set; }
    public Func<RemoteCommand, CancellationToken, Task<TransportResult>>? InteractiveWmiHandler { get; set; }
    public Func<RemoteCommand, CancellationToken, Task<TransportResult>>? InteractiveScheduledTaskHandler { get; set; }

    public Task<TransportResult> ExecutePsExecOnlyAsync(
        RemoteCommand command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        CallOrder.Add(RemoteTransportKind.PsExec);
        PsExecCommands.Add(command);
        PsExecOutputLine = onOutputLine;
        return PsExecHandler?.Invoke(command, ct)
            ?? Task.FromResult(TransportResult.Ok(
                RemoteTransportKind.PsExec,
                new CommandResult(0, string.Empty, string.Empty)));
    }

    public Task<TransportResult> ExecuteWmiOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        CallOrder.Add(RemoteTransportKind.WmiDcom);
        WmiCommands.Add(command);
        return WmiHandler?.Invoke(command, ct)
            ?? Task.FromResult(TransportResult.Ok(
                RemoteTransportKind.WmiDcom,
                new CommandResult(0, string.Empty, string.Empty)));
    }

    public Task<TransportResult> ExecuteInteractivePsExecOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        CallOrder.Add(RemoteTransportKind.PsExec);
        InteractivePsExecCommands.Add(command);
        return InteractivePsExecHandler?.Invoke(command, ct)
            ?? Task.FromResult(TransportResult.Ok(
                RemoteTransportKind.PsExec,
                new CommandResult(0, string.Empty, string.Empty)));
    }

    public Task<TransportResult> ExecuteInteractiveWmiOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        CallOrder.Add(RemoteTransportKind.WmiDcom);
        InteractiveWmiCommands.Add(command);
        return InteractiveWmiHandler?.Invoke(command, ct)
            ?? Task.FromResult(TransportResult.Ok(
                RemoteTransportKind.WmiDcom,
                new CommandResult(0, string.Empty, string.Empty)));
    }

    public Task<TransportResult> ExecuteInteractiveScheduledTaskOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        CallOrder.Add(RemoteTransportKind.ScheduledTask);
        InteractiveScheduledTaskCommands.Add(command);
        return InteractiveScheduledTaskHandler?.Invoke(command, ct)
            ?? Task.FromResult(TransportResult.Ok(
                RemoteTransportKind.ScheduledTask,
                new CommandResult(0, string.Empty, string.Empty)));
    }
}



internal sealed class TestCredentialService : ICredentialService
{
    public ObservableCollection<CredentialInfo> Credentials { get; } = [];

    public CredentialInfo? SelectedCredential { get; set; }

    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
    public void Add(CredentialInfo credential) => Credentials.Add(credential);
    public void Remove(CredentialInfo credential) => Credentials.Remove(credential);
    public void ClearAll()
    {
        Credentials.Clear();
        SelectedCredential = null;
    }

    public string? DecryptPassword(CredentialInfo credential) => credential.EncryptedPassword;

    public List<CredentialInfo> GetSelectedCredentials() =>
        SelectedCredential is null ? [] : [SelectedCredential];
}