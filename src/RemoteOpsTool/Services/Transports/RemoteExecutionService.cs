using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services.Transports;

/// <summary>
/// Routes remote operations through the best known transport. Capability
/// snapshots are cached by the capability service and carry learned transport
/// preferences across sessions, while transport failures still fall back safely.
/// </summary>
public interface IRemoteExecutionService
{
    Task<IRemoteExecutionSession> CreateSessionAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default);

    Task<CommandResult> ExecuteOnceAsync(
        RemoteCommand command,
        RemoteOperationKind operation = RemoteOperationKind.Command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default);

    Task<CommandResult> ExecuteOnceAsync(
        string host,
        string username,
        string password,
        string command,
        RemoteOperationKind operation = RemoteOperationKind.Command,
        CommandShell shell = CommandShell.Cmd,
        bool wrapCmd = true,
        bool silent = false,
        bool interactiveSession = false,
        int? sessionId = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default);
}

public interface IRemoteExecutionSession
{
    CapabilitySnapshot Capability { get; }

    Task<TransportResult> ExecuteAsync(
        RemoteOperationKind operation,
        RemoteCommand command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default);
}

public sealed class RemoteExecutionService : IRemoteExecutionService
{
    private readonly ICapabilityService _capabilities;
    private readonly IRemoteCommandExecutor _executor;
    private readonly ILogService _log;

    public RemoteExecutionService(
        ICapabilityService capabilities,
        IRemoteCommandExecutor executor,
        ILogService log)
    {
        _capabilities = capabilities;
        _executor = executor;
        _log = log;
    }

    public async Task<IRemoteExecutionSession> CreateSessionAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var capability = await _capabilities.ProbeAsync(host, username, password, ct);
        return new RemoteExecutionSession(capability, _executor, _log);
    }

    public Task<CommandResult> ExecuteOnceAsync(
        RemoteCommand command,
        RemoteOperationKind operation = RemoteOperationKind.Command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default) =>
        ExecuteOnceAsync(
            command.TargetHost,
            command.Username,
            command.Password,
            command.Command,
            operation,
            command.Shell,
            command.WrapCmd,
            command.Silent,
            command.InteractiveSession,
            command.SessionId,
            onOutputLine,
            ct);

    public async Task<CommandResult> ExecuteOnceAsync(
        string host,
        string username,
        string password,
        string command,
        RemoteOperationKind operation = RemoteOperationKind.Command,
        CommandShell shell = CommandShell.Cmd,
        bool wrapCmd = true,
        bool silent = false,
        bool interactiveSession = false,
        int? sessionId = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(host, username, password, ct);
        var result = await session.ExecuteAsync(
            operation,
            new RemoteCommand
            {
                TargetHost = host,
                Username = username,
                Password = password,
                Command = command,
                Shell = shell,
                WrapCmd = wrapCmd,
                Silent = silent,
                InteractiveSession = interactiveSession,
                SessionId = sessionId,
            },
            onOutputLine,
            ct);
        return result.Result;
    }
}

internal sealed class RemoteExecutionSession : IRemoteExecutionSession
{
    /// <summary>
    /// CreateProcessWithLogonW, the launcher PsExec is started with, has a small
    /// command-line budget. Payloads above it are sent to WMI/DCOM first so the
    /// command never gets silently truncated; PsExec stays the fallback.
    /// </summary>
    private const int MaxRunAsCommandLength = PsExecService.MaxSafeRunAsCommandLength;

    private readonly IRemoteCommandExecutor _executor;
    private readonly ILogService _log;
    private readonly Dictionary<RemoteOperationKind, RemoteTransportKind> _preferredTransport = [];

    public RemoteExecutionSession(
        CapabilitySnapshot capability,
        IRemoteCommandExecutor executor,
        ILogService log)
    {
        Capability = capability;
        _executor = executor;
        _log = log;
    }

    public CapabilitySnapshot Capability { get; }

    public async Task<TransportResult> ExecuteAsync(
        RemoteOperationKind operation,
        RemoteCommand command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var preferWmiForCommands = RequiresWmiTransport(operation, command);
        var baseChain = CapabilityMatrix.BuildFallbackChain(
            operation, Capability.AvailableTransports, preferWmiForCommands);
        if (baseChain.Count == 0)
        {
            var message = $"目标主机 {Capability.Host} 未探测到可用于 {operation} 的远程执行通道。";
            _log.Error(message);
            return TransportResult.TransportFailure(
                RemoteTransportKind.WmiDcom,
                new CommandResult(-1, string.Empty, message));
        }

        // A learned PsExec route must not override the RunAs command-line safety
        // rule for oversized payloads. Cooldown ordering still applies below, so
        // PsExec remains eligible if WMI is unavailable or cooling down.
        RemoteTransportKind? preferred = null;
        if (!preferWmiForCommands)
        {
            preferred = _preferredTransport.TryGetValue(operation, out var sessionPreferred)
                ? sessionPreferred
                : Capability.TryGetPreferredTransport(operation, out var cachedPreferred)
                    ? cachedPreferred
                    : null;
        }
        var chain = OrderTransportChain(baseChain, preferred);
        var failures = new List<(RemoteTransportKind Transport, CommandResult Result)>();

        for (var index = 0; index < chain.Count; index++)
        {
            var transport = chain[index];
            _log.Debug($"远程执行计划: host={Capability.Host} operation={operation} attempt={index + 1}/{chain.Count} transport={transport}");
            var result = await ExecuteTransportAsync(transport, operation, command, onOutputLine, ct);
            if (!result.IsTransportFailure)
            {
                _preferredTransport[operation] = transport;
                Capability.RecordTransportSuccess(operation, transport);
                return result;
            }

            // A channel that just failed must not stay pinned as this session's
            // first choice; the snapshot cooldown keeps it as a late fallback.
            _preferredTransport.Remove(operation);
            Capability.RecordTransportFailure(transport);
            failures.Add((transport, result.Result));
            _log.Warn($"远程传输通道 {transport} 不可用: host={Capability.Host} operation={operation} error={TransportFailureClassifier.SummarizeCommandFailure(result.Result)}");
        }

        var failureMessage = $"{operation}失败：没有可用的远程传输通道。" +
            Environment.NewLine +
            string.Join(Environment.NewLine,
                failures.Select(f => $"{f.Transport}: {TransportFailureClassifier.SummarizeCommandFailure(f.Result)}"));
        return TransportResult.TransportFailure(
            failures[^1].Transport,
            new CommandResult(-1, string.Empty, failureMessage));
    }

    /// <summary>
    /// Oversized credentialed payloads must not be started through the RunAs
    /// launcher, so they begin on WMI/DCOM and keep PsExec as the fallback.
    /// </summary>
    private static bool RequiresWmiTransport(RemoteOperationKind operation, RemoteCommand command) =>
        operation == RemoteOperationKind.Command &&
        !command.InteractiveSession &&
        !string.IsNullOrWhiteSpace(command.Username) &&
        !string.IsNullOrEmpty(command.Password) &&
        !string.IsNullOrEmpty(command.Command) &&
        command.Command.Length > MaxRunAsCommandLength;

    /// <summary>
    /// Puts the learned first-choice transport first and pushes any channel still
    /// inside its failure cooldown to the end. A channel that just failed is
    /// therefore never retried as the first choice, but stays available as a
    /// fallback. When every channel is cooling down the original order is kept so
    /// a deliberate retry is still possible.
    /// </summary>
    private IReadOnlyList<RemoteTransportKind> OrderTransportChain(
        IReadOnlyList<RemoteTransportKind> chain,
        RemoteTransportKind? preferred)
    {
        var ordered = OrderPreferredTransport(chain, preferred);
        if (ordered.Count <= 1)
            return ordered;

        var ready = ordered
            .Where(transport => !Capability.IsTransportCoolingDown(transport))
            .ToArray();
        if (ready.Length == 0)
            return ordered;

        return
        [
            .. ready,
            .. ordered.Where(transport => Capability.IsTransportCoolingDown(transport)),
        ];
    }

    private static IReadOnlyList<RemoteTransportKind> OrderPreferredTransport(
        IReadOnlyList<RemoteTransportKind> chain,
        RemoteTransportKind? preferred)
    {
        if (preferred is null || !chain.Contains(preferred.Value))
            return chain;

        return [preferred.Value, .. chain.Where(transport => transport != preferred.Value)];
    }

    private Task<TransportResult> ExecuteTransportAsync(
        RemoteTransportKind transport,
        RemoteOperationKind operation,
        RemoteCommand command,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        if (operation == RemoteOperationKind.InteractiveLaunch)
        {
            return transport switch
            {
                RemoteTransportKind.PsExec => _executor.ExecuteInteractivePsExecOnlyAsync(command, ct),
                RemoteTransportKind.WmiDcom => _executor.ExecuteInteractiveWmiOnlyAsync(command, ct),
                RemoteTransportKind.ScheduledTask => _executor.ExecuteInteractiveScheduledTaskOnlyAsync(command, ct),
                _ => Task.FromResult(TransportResult.TransportFailure(
                    transport, new CommandResult(-1, string.Empty, $"不支持的交互执行通道: {transport}"))),
            };
        }

        return transport switch
        {
            RemoteTransportKind.PsExec => _executor.ExecutePsExecOnlyAsync(command, onOutputLine, ct),
            RemoteTransportKind.WmiDcom => ExecuteWmiWithReplayAsync(command, onOutputLine, ct),
            _ => Task.FromResult(TransportResult.TransportFailure(
                transport, new CommandResult(-1, string.Empty, $"不支持的命令执行通道: {transport}"))),
        };
    }

    private async Task<TransportResult> ExecuteWmiWithReplayAsync(
        RemoteCommand command,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        var result = await _executor.ExecuteWmiOnlyAsync(command, ct);
        if (onOutputLine is not null)
        {
            ReplayOutput(result.Result.StdOut, onOutputLine);
            ReplayOutput(result.Result.StdErr, onOutputLine);
        }
        return result;
    }

    private static void ReplayOutput(string output, Action<string> onOutputLine)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        foreach (var line in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                onOutputLine(line);
        }
    }
}