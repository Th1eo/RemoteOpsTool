using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class ServiceManagerService : IServiceManagerService
{
    private readonly IPsExecService _psExec;
    private readonly ILogService _log;

    public ServiceManagerService(IPsExecService psExec, ILogService log)
    {
        _psExec = psExec;
        _log = log;
    }

    public async Task<List<ServiceInfo>> GetServicesAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        var psScript = @"
Get-Service | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Json -Compress
";
        var psCmd = SystemInfoService.EncodePowerShellCommand(psScript);
        var result = await _psExec.ExecuteAsync(host, username, password, psCmd, silent: true, ct: ct);
        if (!result.Success) return [];

        var services = new List<ServiceInfo>();
        try
        {
            var jsonStart = result.StdOut.IndexOf('[');
            var jsonEnd = result.StdOut.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = result.StdOut[jsonStart..(jsonEnd + 1)];
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(json);
                if (parsed != null)
                {
                    foreach (var svc in parsed)
                    {
                        services.Add(new ServiceInfo
                        {
                            ServiceName = GetJsonProp(svc, "Name"),
                            DisplayName = GetJsonProp(svc, "DisplayName"),
                            Status = GetJsonProp(svc, "Status"),
                            StartType = GetJsonProp(svc, "StartType")
                        });
                    }
                }
            }
        }
        catch { }

        if (services.Count == 0)
        {
            var scResult = await _psExec.ExecuteAsync(host, username, password,
                "sc query state= all", silent: true, ct: ct);
            if (scResult.Success)
                services = ParseScQueryOutput(scResult.StdOut);
        }

        return services;
    }

    public async Task<string> GetServiceConfigAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc qc \"{serviceName}\"", silent: true, ct: ct);
        return result.Success ? result.StdOut : result.StdErr;
    }

    public async Task<bool> StartServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc start \"{serviceName}\"", silent: true, ct: ct);
        if (result.Success) _log.Info($"已启动服务: {serviceName}");
        else _log.Warn($"启动服务失败: {serviceName} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> StopServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc stop \"{serviceName}\"", silent: true, ct: ct);
        if (result.Success) _log.Info($"已停止服务: {serviceName}");
        else _log.Warn($"停止服务失败: {serviceName} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> RestartServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        await _psExec.ExecuteAsync(host, username, password,
            $"sc stop \"{serviceName}\"", silent: true, ct: ct);
        await Task.Delay(1500, ct);
        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc start \"{serviceName}\"", silent: true, ct: ct);
        if (result.Success) _log.Info($"已重启服务: {serviceName}");
        else _log.Warn($"重启服务失败: {serviceName} - {result.StdErr}");
        return result.Success;
    }

    private static List<ServiceInfo> ParseScQueryOutput(string output)
    {
        var services = new List<ServiceInfo>();
        string? currentName = null, currentDisplay = null, currentStatus = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
                currentName = trimmed["SERVICE_NAME:".Length..].Trim();
            else if (trimmed.StartsWith("DISPLAY_NAME:", StringComparison.OrdinalIgnoreCase))
                currentDisplay = trimmed["DISPLAY_NAME:".Length..].Trim();
            else if (trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
            {
                var parts = trimmed.Split([':'], 2);
                var stateParts = parts.Length > 1 ? parts[1].Trim().Split(' ') : [];
                if (stateParts.Length >= 1) currentStatus = stateParts[0];
            }

            if (currentName != null && currentDisplay != null && currentStatus != null)
            {
                services.Add(new ServiceInfo
                {
                    ServiceName = currentName,
                    DisplayName = currentDisplay,
                    Status = currentStatus,
                    StartType = ""
                });
                currentName = currentDisplay = currentStatus = null;
            }
        }
        return services;
    }

    private static string GetJsonProp(System.Text.Json.JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind != System.Text.Json.JsonValueKind.Null)
            return prop.ToString();
        return "";
    }
}
