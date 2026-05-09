using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IDeviceService
{
    Task<List<DeviceInfo>> GetDevicesAsync(string host, string username, string password, CancellationToken ct = default);
    Task<bool> DisableDeviceAsync(string host, string username, string password, string instanceId, CancellationToken ct = default);
    Task<bool> EnableDeviceAsync(string host, string username, string password, string instanceId, CancellationToken ct = default);
    Task<bool> UninstallDeviceAsync(string host, string username, string password, string instanceId, CancellationToken ct = default);
}
