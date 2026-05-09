using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IEnvVarService
{
    Task<List<string>> GetLoggedOnUsersAsync(string host, string username, string password, CancellationToken ct = default);
    Task<List<EnvVariableInfo>> GetVariablesAsync(string host, string username, string password, string target, CancellationToken ct = default);
    Task<bool> SetVariableAsync(string host, string username, string password, string name, string value, string target, CancellationToken ct = default);
    Task<bool> DeleteVariableAsync(string host, string username, string password, string name, string target, CancellationToken ct = default);
}
