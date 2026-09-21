using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Transports;

/// <summary>Transport-neutral description of one remote command invocation.</summary>
public sealed record RemoteCommand
{
    public string TargetHost { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public CommandShell Shell { get; init; } = CommandShell.Cmd;
    public bool InteractiveSession { get; init; }
    public int? SessionId { get; init; }
    public bool WrapCmd { get; init; } = true;
    public bool Silent { get; init; }
    public bool PreferPsExec { get; init; }
}
