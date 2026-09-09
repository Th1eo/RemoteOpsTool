using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IPsExecService
{
    Task<CommandResult> ExecuteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        bool interactiveSession = false,
        int sessionId = 1,
        CancellationToken ct = default,
        bool silent = false,
        bool wrapCmd = true,
        CommandShell shell = CommandShell.Cmd);

    Task ExecuteWithOutputAsync(
        string targetHost,
        string username,
        string password,
        string command,
        Action<string> onOutputLine,
        CancellationToken ct = default,
        bool silent = false,
        CommandShell shell = CommandShell.Cmd);

    Task<int> GetActiveSessionIdAsync(string targetHost, string username, string password, CancellationToken ct = default);
    Task<CommandResult> ExecuteLocalElevatedAsync(string targetHost, string username, string password, string command, CancellationToken ct = default, CommandShell shell = CommandShell.PowerShell);
    Task<CommandResult> ExecuteInteractiveLocalAsync(string command, string username, string password, CancellationToken ct = default, CommandShell shell = CommandShell.Cmd, int? sessionId = null);
    Task ExecuteInteractiveRemoteAsync(string targetHost, string username, string password, string command, CancellationToken ct = default, bool wrapCmd = true, int? sessionId = null, CommandShell shell = CommandShell.Cmd);
}
