using System.Collections.ObjectModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Capability;
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
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warn(string message) => Log(LogLevel.Warn, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    // 与 LogService.Debug 行为一致：以 Info 级别落到日志，并加 [DEBUG] 前缀。
    public void Debug(string message) => Log(LogLevel.Info, $"[DEBUG] {message}");
    public void Log(LogLevel level, string message) => Entries.Add(new LogEntry(System.DateTime.Now, level, message));
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

    public Task<ProcessRestartResult> RestartProcessAsync(
        string host, string username, string password, ProcessDetailInfo process, CancellationToken ct = default) =>
        Task.FromResult(ProcessRestartResult.Ok(string.Empty));

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
    public Func<CapabilityProbeProfile, int, List<RemoteCapabilityInfo>>? ProbeResultsFactory { get; set; }
    public int ProbeCallCount { get; private set; }
    public List<string> ProbeHosts { get; } = [];
    public List<string> ProbeUsernames { get; } = [];

    public List<CapabilityProbeProfile> ProbeProfiles { get; } = [];

    public Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host, string username, string password, CancellationToken ct = default) =>
        ProbeCapabilitiesAsync(host, username, password, CapabilityProbeProfile.Full, ct);

    public Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
        CancellationToken ct = default)
    {
        ProbeCallCount++;
        ProbeHosts.Add(host);
        ProbeUsernames.Add(username);
        ProbeProfiles.Add(profile);
        var results = ProbeResultsFactory?.Invoke(profile, ProbeCallCount)
            ?? ProbeResults.ToList();
        return Task.FromResult(results);
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

    public CredentialInfo? SelectedCredential { get; private set; }

    public event EventHandler? SelectedCredentialChanged;
    public event Action<string>? SaveFailed
    {
        add { }
        remove { }
    }

    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;

    public void Add(CredentialInfo credential)
    {
        if (!Credentials.Contains(credential))
            Credentials.Add(credential);
        SetSelectedCredential(credential);
    }

    public void Remove(CredentialInfo credential)
    {
        var wasSelected = ReferenceEquals(SelectedCredential, credential);
        Credentials.Remove(credential);
        if (wasSelected)
            SetSelectedCredential(Credentials.FirstOrDefault());
    }

    public void ClearAll()
    {
        foreach (var credential in Credentials)
            credential.IsSelected = false;
        Credentials.Clear();
        SetSelectedCredential(null);
    }

    public void SetSelectedCredential(CredentialInfo? credential)
    {
        if (credential is not null && !Credentials.Contains(credential))
            return;

        if (ReferenceEquals(SelectedCredential, credential))
            return;

        foreach (var item in Credentials)
            item.IsSelected = ReferenceEquals(item, credential);

        SelectedCredential = credential;
        SelectedCredentialChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? DecryptPassword(CredentialInfo credential) =>
        TryDecryptPassword(credential, out var password) ? password : null;

    public bool TryDecryptPassword(CredentialInfo credential, out string password)
    {
        password = credential.EncryptedPassword;
        return true;
    }

    public List<CredentialInfo> GetSelectedCredentials() =>
        SelectedCredential is null ? [] : [SelectedCredential];
}
