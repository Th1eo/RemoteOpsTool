using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface ISystemInfoService
{
    Task<string> GetSystemInfoAsync(string host, string username, string password, CancellationToken ct = default);
    Task<SystemInfoData> GetStructuredSystemInfoAsync(string host, string username, string password, CancellationToken ct = default);
}
