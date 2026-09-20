using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Interfaces;

public interface INetworkService
{
    Task<PingResult> PingAsync(string host, CancellationToken ct = default);
    Task FlushDnsAsync(string host, string username, string password, CancellationToken ct = default);
    Task RefreshIpAsync(string host, string username, string password, CancellationToken ct = default);
    Task<List<NetworkConnectionInfo>> GetActiveConnectionsAsync(string host, string username, string password, CancellationToken ct = default);
    Task<List<ProcessDetailInfo>> GetProcessListAsync(string host, string username, string password, CancellationToken ct = default);
    Task<bool> KillProcessAsync(string host, string username, string password, int processId, bool killTree, CancellationToken ct = default);
    Task<List<UserSessionInfo>> GetUserSessionsWithSessionAsync(
        IRemoteExecutionSession session,
        string host,
        string username,
        string password,
        CancellationToken ct = default);
    Task<bool> SignOutUserAsync(string host, string username, string password, int sessionId, CancellationToken ct = default);
}

public record PingResult(bool Success, string Output, long RoundtripTime = 0);
