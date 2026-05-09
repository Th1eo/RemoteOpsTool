using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IServiceManagerService
{
    Task<List<ServiceInfo>> GetServicesAsync(string host, string username, string password, CancellationToken ct = default);
    Task<string> GetServiceConfigAsync(string host, string username, string password, string serviceName, CancellationToken ct = default);
    Task<bool> StartServiceAsync(string host, string username, string password, string serviceName, CancellationToken ct = default);
    Task<bool> StopServiceAsync(string host, string username, string password, string serviceName, CancellationToken ct = default);
    Task<bool> RestartServiceAsync(string host, string username, string password, string serviceName, CancellationToken ct = default);
}
