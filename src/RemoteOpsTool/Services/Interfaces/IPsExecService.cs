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
        bool wrapCmd = true);

    Task ExecuteWithOutputAsync(
        string targetHost,
        string username,
        string password,
        string command,
        Action<string> onOutputLine,
        CancellationToken ct = default,
        bool silent = false);

    Task<int> GetActiveSessionIdAsync(string targetHost, string username, string password, CancellationToken ct = default);
    void ExecuteInteractiveLocal(string command);
    Task ExecuteInteractiveRemoteAsync(string targetHost, string username, string password, string command, CancellationToken ct = default, bool wrapCmd = true, int? sessionId = null);
}
