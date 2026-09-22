using System.Diagnostics;
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

    Task<IRemoteExecutionSession> CreateSessionAsync(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
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
    private readonly IRouteLearningStore _routeLearning;

    public RemoteExecutionService(
        ICapabilityService capabilities,
        IRemoteCommandExecutor executor,
        ILogService log)
        : this(capabilities, executor, log, NullRouteLearningStore.Instance)
    {
    }

    public RemoteExecutionService(
        ICapabilityService capabilities,
        IRemoteCommandExecutor executor,
        ILogService log,
        IRouteLearningStore routeLearning)
    {
        _capabilities = capabilities;
        _executor = executor;
        _log = log;
        _routeLearning = routeLearning;
    }

    public Task<IRemoteExecutionSession> CreateSessionAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default) =>
        CreateSessionAsync(host, username, password, CapabilityProbeProfile.Full, ct);

    public async Task<IRemoteExecutionSession> CreateSessionAsync(
        string host,
        string username,
        string password,
        CapabilityProbeProfile profile,
        CancellationToken ct = default)
    {
        var capability = await _capabilities.ProbeAsync(host, username, password, profile, ct);
        capability.ApplyRouteLearning(
            _routeLearning.GetRecords(capability.Host, capability.CredentialFingerprint));
        return new RemoteExecutionSession(
            capability, _executor, _log, _routeLearning, _capabilities,
            host, username, password);
    }
    public async Task<CommandResult> ExecuteOnceAsync(
        RemoteCommand command,
        RemoteOperationKind operation = RemoteOperationKind.Command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(
            command.TargetHost,
            command.Username,
            command.Password,
            ResolveProbeProfile(operation, command),
            ct);
        var result = await session.ExecuteAsync(operation, command, onOutputLine, ct);
        return result.Result;
    }

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
        var remoteCommand = new RemoteCommand
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
        };
        var session = await CreateSessionAsync(
            host,
            username,
            password,
            ResolveProbeProfile(operation, remoteCommand),
            ct);
        var result = await session.ExecuteAsync(operation, remoteCommand, onOutputLine, ct);
        return result.Result;
    }

    private static CapabilityProbeProfile ResolveProbeProfile(
        RemoteOperationKind operation,
        RemoteCommand command)
    {
        if (operation == RemoteOperationKind.InteractiveLaunch)
            return CapabilityProbeProfile.InteractiveLaunch;

        if (operation == RemoteOperationKind.RegistryRead ||
            operation == RemoteOperationKind.RegistryWrite)
            return CapabilityProbeProfile.RegistryWmiOnly;

        if (operation == RemoteOperationKind.Inventory)
            return CapabilityProbeProfile.InventoryWmiOnly;

        return command.PreferPsExec
            ? CapabilityProbeProfile.Command
            : CapabilityProbeProfile.CommandWmiFirst;
    }
}

internal sealed class RemoteExecutionSession : IRemoteExecutionSession
{
    private const int MaxRunAsCommandLength = PsExecService.MaxSafeRunAsCommandLength;

    private readonly IRemoteCommandExecutor _executor;
    private readonly ILogService _log;
    private readonly IRouteLearningStore _routeLearning;
    private readonly ICapabilityService _capabilities;
    private readonly string _host;
    private readonly string _username;
    private readonly string _password;
    private readonly HashSet<CapabilityProbeProfile> _upgradedProfiles = [];
    private readonly SemaphoreSlim _upgradeGate = new(1, 1);
    private readonly Dictionary<RemoteOperationKind, RemoteTransportKind> _preferredTransport = [];

    public RemoteExecutionSession(
        CapabilitySnapshot capability,
        IRemoteCommandExecutor executor,
        ILogService log,
        IRouteLearningStore routeLearning,
        ICapabilityService capabilities,
        string host,
        string username,
        string password)
    {
        Capability = capability;
        _executor = executor;
        _log = log;
        _routeLearning = routeLearning;
        _capabilities = capabilities;
        _host = host;
        _username = username;
        _password = password;
    }

    public CapabilitySnapshot Capability { get; private set; }

    public async Task<TransportResult> ExecuteAsync(
        RemoteOperationKind operation,
        RemoteCommand command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var preferWmiForCommands = RequiresWmiTransport(operation, command);

        // Uploaded scripts explicitly request streaming PsExec. Probe that
        // capability only for this command shape instead of making every
        // ordinary read/command session pay the PsExec startup cost.
        if (command.PreferPsExec &&
            operation == RemoteOperationKind.Command &&
            !preferWmiForCommands &&
            !Capability.AvailableTransports.Contains(RemoteTransportKind.PsExec))
        {
            await TryUpgradeCapabilityAsync(operation, ct);
        }

        var queue = BuildOperationChain(operation, command, preferWmiForCommands).ToList();
        if (queue.Count == 0)
        {
            await TryUpgradeCapabilityAsync(operation, ct);
            queue = BuildOperationChain(operation, command, preferWmiForCommands).ToList();
        }

        if (queue.Count == 0)
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
        var preferPsExec = command.PreferPsExec &&
            operation == RemoteOperationKind.Command &&
            !preferWmiForCommands &&
            Capability.AvailableTransports.Contains(RemoteTransportKind.PsExec);

        RemoteTransportKind? preferred = null;
        if (operation != RemoteOperationKind.InteractiveLaunch && !preferWmiForCommands)
        {
            if (preferPsExec)
            {
                preferred = RemoteTransportKind.PsExec;
            }
            else
            {
                RemoteTransportKind? learnedPreferred = _preferredTransport.TryGetValue(operation, out var sessionPreferred)
                    ? sessionPreferred
                    : Capability.TryGetPreferredTransport(operation, out var cachedPreferred)
                        ? cachedPreferred
                        : null;

                // Ordinary commands default to WMI/DCOM. Historical PsExec
                // successes must not silently restore the slower PSEXESVC path;
                // explicit script execution can still opt into PsExec above.
                if (operation == RemoteOperationKind.Command &&
                    learnedPreferred == RemoteTransportKind.PsExec &&
                    !command.PreferPsExec)
                {
                    learnedPreferred = null;
                }

                preferred = learnedPreferred;
            }
        }

        queue = OrderTransportChain(queue, preferred, operation).ToList();
        var queued = new HashSet<RemoteTransportKind>(queue);
        var attempted = new HashSet<RemoteTransportKind>();
        var failures = new List<(RemoteTransportKind Transport, CommandResult Result)>();
        var nextIndex = 0;

        while (nextIndex < queue.Count)
        {
            var transport = queue[nextIndex++];
            if (!attempted.Add(transport))
                continue;

            _log.Debug(
                $"远程执行计划: host={Capability.Host} operation={operation} attempt={nextIndex}/{queue.Count} transport={transport}");
            var startedAt = Stopwatch.GetTimestamp();

            // Every transport gets its own tracker so identical output emitted by
            // a failed channel and a later fallback channel is not suppressed.
            var outputTracker = new RemoteOutputLineTracker();
            Action<string>? trackedOutputLine = onOutputLine is null
                ? null
                : line =>
                {
                    outputTracker.Record(line);
                    onOutputLine(line);
                };

            var result = await ExecuteTransportAsync(transport, operation, command, trackedOutputLine, ct);
            if (onOutputLine is not null)
            {
                // PsExec usually streams its output, while WMI/DCOM returns the
                // complete stdout/stderr only after the remote process exits.
                ReplayUnseenOutput(result.Result.StdOut, outputTracker, onOutputLine);
                ReplayUnseenOutput(result.Result.StdErr, outputTracker, onOutputLine);
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (!result.IsTransportFailure)
            {
                // A non-zero exit code means the remote command actually started.
                // It is a command failure, not a transport failure, and must
                // never be replayed through another channel.
                _preferredTransport[operation] = transport;
                Capability.RecordTransportSuccess(operation, transport);
                _routeLearning.Record(new CapabilityOutcome(
                    Capability.Host,
                    Capability.CredentialFingerprint,
                    operation,
                    transport,
                    TransportSucceeded: true,
                    Math.Max(0, (long)elapsed.TotalMilliseconds),
                    CapabilityFailureKind.None,
                    DateTimeOffset.UtcNow));
                return result;
            }

            var failureKind = CapabilityPolicy.ClassifyResult(result.Result);
            _preferredTransport.Remove(operation);
            Capability.RecordTransportFailure(operation, transport, result.Result);
            _routeLearning.Record(new CapabilityOutcome(
                Capability.Host,
                Capability.CredentialFingerprint,
                operation,
                transport,
                TransportSucceeded: false,
                Math.Max(0, (long)elapsed.TotalMilliseconds),
                failureKind,
                DateTimeOffset.UtcNow));
            failures.Add((transport, result.Result));
            _log.Warn(
                $"远程传输通道 {transport} 不可用: host={Capability.Host} operation={operation} error={TransportFailureClassifier.SummarizeCommandFailure(result.Result)}");

            // Only a real transport failure reaches this point. Upgrade the
            // minimum probe profile now, then append any newly discovered
            // fallback channels without replaying a failed/started command.
            if (await TryUpgradeCapabilityAsync(operation, ct))
            {
                foreach (var candidate in BuildOperationChain(operation, command, preferWmiForCommands))
                {
                    if (attempted.Contains(candidate) || !queued.Add(candidate))
                        continue;
                    queue.Add(candidate);
                }
            }
        }

        var failureMessage = $"{operation}失败：没有可用的远程传输通道。" +
            Environment.NewLine +
            string.Join(Environment.NewLine,
                failures.Select(f => $"{f.Transport}: {TransportFailureClassifier.SummarizeCommandFailure(f.Result)}"));
        return TransportResult.TransportFailure(
            failures[^1].Transport,
            new CommandResult(-1, string.Empty, failureMessage));
    }

    private IReadOnlyList<RemoteTransportKind> BuildOperationChain(
        RemoteOperationKind operation,
        RemoteCommand command,
        bool preferWmiForCommands)
    {
        return operation == RemoteOperationKind.InteractiveLaunch
            ? CapabilityMatrix.BuildInteractiveFallbackChain()
            : CapabilityMatrix.BuildFallbackChain(
                operation,
                Capability.AvailableTransports,
                preferWmiForCommands);
    }

    private async Task<bool> TryUpgradeCapabilityAsync(
        RemoteOperationKind operation,
        CancellationToken ct)
    {
        var targetProfile = operation switch
        {
            RemoteOperationKind.Command or
            RemoteOperationKind.Inventory or
            RemoteOperationKind.RegistryRead or
            RemoteOperationKind.RegistryWrite => CapabilityProbeProfile.Command,
            _ => (CapabilityProbeProfile?)null,
        };

        if (targetProfile is null ||
            Capability.Profile == CapabilityProbeProfile.Full ||
            Capability.Profile == targetProfile)
        {
            return false;
        }

        await _upgradeGate.WaitAsync(ct);
        try
        {
            if (_upgradedProfiles.Contains(targetProfile.Value))
                return false;

            _upgradedProfiles.Add(targetProfile.Value);
            var upgraded = await _capabilities.ProbeAsync(
                _host,
                _username,
                _password,
                targetProfile.Value,
                ct);
            upgraded.ApplyRouteLearning(
                _routeLearning.GetRecords(upgraded.Host, upgraded.CredentialFingerprint));
            Capability = upgraded;
            return true;
        }
        finally
        {
            _upgradeGate.Release();
        }
    }
    private static bool RequiresWmiTransport(RemoteOperationKind operation, RemoteCommand command) =>
        operation == RemoteOperationKind.Command &&
        !command.InteractiveSession &&
        !string.IsNullOrWhiteSpace(command.Username) &&
        !string.IsNullOrEmpty(command.Password) &&
        !string.IsNullOrEmpty(command.Command) &&
        command.Command.Length > MaxRunAsCommandLength;

    /// <summary>
    /// Puts the learned first-choice transport first and pushes any channel still
    /// inside its operation-specific failure cooldown to the end. A channel that
    /// just failed is never the first choice, but remains a fallback.
    /// </summary>
    private IReadOnlyList<RemoteTransportKind> OrderTransportChain(
        IReadOnlyList<RemoteTransportKind> chain,
        RemoteTransportKind? preferred,
        RemoteOperationKind operation)
    {
        var ordered = OrderPreferredTransport(chain, preferred);
        if (ordered.Count <= 1)
            return ordered;

        var ready = ordered
            .Where(transport => !Capability.IsTransportCoolingDown(operation, transport))
            .ToArray();
        if (ready.Length == 0)
            return ordered;

        return
        [
            .. ready,
            .. ordered.Where(transport => Capability.IsTransportCoolingDown(operation, transport)),
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
            RemoteTransportKind.WmiDcom => _executor.ExecuteWmiOnlyAsync(command, ct),
            _ => Task.FromResult(TransportResult.TransportFailure(
                transport, new CommandResult(-1, string.Empty, $"不支持的命令执行通道: {transport}"))),
        };
    }

    private static void ReplayUnseenOutput(
        string output,
        RemoteOutputLineTracker outputTracker,
        Action<string> onOutputLine)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        foreach (var line in output
                     .Replace("\r\n", "\n")
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line) && !outputTracker.TryConsume(line))
                onOutputLine(line);
        }
    }
}

internal sealed class RemoteOutputLineTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _unreplayedCounts = new(StringComparer.Ordinal);

    public void Record(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_sync)
        {
            _unreplayedCounts.TryGetValue(line, out var count);
            _unreplayedCounts[line] = count + 1;
        }
    }

    public bool TryConsume(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        lock (_sync)
        {
            if (!_unreplayedCounts.TryGetValue(line, out var count))
                return false;

            if (count <= 1)
                _unreplayedCounts.Remove(line);
            else
                _unreplayedCounts[line] = count - 1;
            return true;
        }
    }
}





