namespace RemoteOpsTool.Services.Transports;

/// <summary>
/// A single command-execution transport. Providers implement only the transports
/// they actually support; the router composes a fallback chain from them.
/// </summary>
public interface ICommandTransport
{
    RemoteTransportKind Transport { get; }

    Task<TransportResult> ExecuteAsync(RemoteCommand command, CancellationToken ct = default);

    Task<TransportResult> ExecuteWithOutputAsync(
        RemoteCommand command,
        Action<string> onOutputLine,
        CancellationToken ct = default);
}
