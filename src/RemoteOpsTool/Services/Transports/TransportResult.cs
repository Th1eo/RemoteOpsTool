namespace RemoteOpsTool.Services.Transports;

/// <summary>Result of one transport attempt, tagged with the channel that produced it.</summary>
public sealed record TransportResult(
    RemoteTransportKind Transport,
    CommandResult Result,
    bool IsTransportFailure)
{
    public bool Success => Result.Success;
    public int ExitCode => Result.ExitCode;
    public string StdOut => Result.StdOut;
    public string StdErr => Result.StdErr;

    public static TransportResult Ok(RemoteTransportKind transport, CommandResult result) =>
        new(transport, result, false);

    public static TransportResult TransportFailure(RemoteTransportKind transport, CommandResult result) =>
        new(transport, result, true);

    public static TransportResult CommandFailure(RemoteTransportKind transport, CommandResult result) =>
        new(transport, result, false);
}
