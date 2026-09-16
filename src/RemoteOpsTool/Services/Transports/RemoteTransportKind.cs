namespace RemoteOpsTool.Services.Transports;

/// <summary>Agentless remote-management transport protocols used by the router.</summary>
public enum RemoteTransportKind
{
    WinRm = 0,
    WmiDcom = 1,
    ScmRpc = 2,
    PsExec = 3,
    ScheduledTask = 4,
    RemoteRegistry = 5,
    Ssh = 6,
}
