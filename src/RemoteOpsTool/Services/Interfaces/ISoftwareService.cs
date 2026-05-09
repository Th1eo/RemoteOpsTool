using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface ISoftwareService
{
    Task<List<SoftwareInfo>> GetInstalledSoftwareAsync(string host, string username, string password, CancellationToken ct = default);
    Task<bool> UninstallSilentlyAsync(string host, string username, string password, string uninstallString, CancellationToken ct = default);
    Task<bool> UninstallInteractiveAsync(string host, string username, string password, string uninstallString, int sessionId, CancellationToken ct = default);
}
