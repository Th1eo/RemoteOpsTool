namespace RemoteOpsTool.Services.Transports;

/// <summary>
/// Exposes strictly single-channel command execution. Implementations must not
/// probe capabilities, retry a failed command, or fall back to another channel;
/// <see cref="RemoteExecutionService"/> owns that policy.
/// </summary>
public interface IRemoteCommandExecutor
{
    Task<TransportResult> ExecutePsExecOnlyAsync(
        RemoteCommand command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default);

    Task<TransportResult> ExecuteWmiOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default);

    Task<TransportResult> ExecuteInteractivePsExecOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default);

    Task<TransportResult> ExecuteInteractiveWmiOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default);

    Task<TransportResult> ExecuteInteractiveScheduledTaskOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default);
}
