using System.Management;
using RemoteOpsTool.Helpers;
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
        _log.Debug($"获取服务列表: host={host} method=WMI/DCOM user={username}");
        var wmiServices = await TryGetServicesViaWmiAsync(host, username, password, ct);
        if (wmiServices.Count > 0)
        {
            _log.Debug($"WMI/DCOM 服务列表完成: host={host} count={wmiServices.Count}");
            return wmiServices;
        }

        _log.Warn($"WMI/DCOM 服务查询不可用，回退到 PsExec: {host}");

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
        _log.Debug($"获取服务配置: host={host} service={serviceName} method=WMI/DCOM user={username}");
        var wmiConfig = await TryGetServiceConfigViaWmiAsync(host, username, password, serviceName, ct);
        if (!string.IsNullOrWhiteSpace(wmiConfig))
            return wmiConfig;

        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc qc \"{serviceName}\"", silent: true, ct: ct);
        return result.Success ? result.StdOut : result.StdErr;
    }

    public async Task<bool> StartServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"启动服务: host={host} service={serviceName} method=WMI/DCOM user={username}");
        var wmiStarted = await TryInvokeServiceMethodViaWmiAsync(host, username, password, serviceName, "StartService", ct);
        if (wmiStarted)
        {
            _log.Info($"已通过 WMI/DCOM 启动服务: {serviceName}");
            return true;
        }

        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc start \"{serviceName}\"", silent: true, ct: ct);
        if (result.Success) _log.Info($"已启动服务: {serviceName}");
        else _log.Warn($"启动服务失败: {serviceName} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> StopServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"停止服务: host={host} service={serviceName} method=WMI/DCOM user={username}");
        var wmiStopped = await TryInvokeServiceMethodViaWmiAsync(host, username, password, serviceName, "StopService", ct);
        if (wmiStopped)
        {
            _log.Info($"已通过 WMI/DCOM 停止服务: {serviceName}");
            return true;
        }

        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc stop \"{serviceName}\"", silent: true, ct: ct);
        if (result.Success) _log.Info($"已停止服务: {serviceName}");
        else _log.Warn($"停止服务失败: {serviceName} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> RestartServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"重启服务: host={host} service={serviceName} method=WMI/DCOM user={username}");
        var wmiStopped = await TryInvokeServiceMethodViaWmiAsync(host, username, password, serviceName, "StopService", ct);
        if (wmiStopped)
        {
            await Task.Delay(1500, ct);
            var wmiStarted = await TryInvokeServiceMethodViaWmiAsync(host, username, password, serviceName, "StartService", ct);
            if (wmiStarted)
            {
                _log.Info($"已通过 WMI/DCOM 重启服务: {serviceName}");
                return true;
            }
        }

        await _psExec.ExecuteAsync(host, username, password,
            $"sc stop \"{serviceName}\"", silent: true, ct: ct);
        await Task.Delay(1500, ct);
        var result = await _psExec.ExecuteAsync(host, username, password,
            $"sc start \"{serviceName}\"", silent: true, ct: ct);
        if (result.Success) _log.Info($"已重启服务: {serviceName}");
        else _log.Warn($"重启服务失败: {serviceName} - {result.StdErr}");
        return result.Success;
    }

    private static async Task<List<ServiceInfo>> TryGetServicesViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var services = new List<ServiceInfo>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Name,DisplayName,State,StartMode FROM Win32_Service"));
                foreach (ManagementObject svc in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    services.Add(new ServiceInfo
                    {
                        ServiceName = RemoteWmiHelper.GetString(svc, "Name"),
                        DisplayName = RemoteWmiHelper.GetString(svc, "DisplayName"),
                        Status = RemoteWmiHelper.GetString(svc, "State"),
                        StartType = RemoteWmiHelper.GetString(svc, "StartMode")
                    });
                }
            }
            catch
            {
                return [];
            }
            return services;
        }, ct);
    }

    private static async Task<string> TryGetServiceConfigViaWmiAsync(
        string host,
        string username,
        string password,
        string serviceName,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                var escapedName = RemoteWmiHelper.EscapeWqlString(serviceName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null) return string.Empty;

                return string.Join(Environment.NewLine,
                    $"SERVICE_NAME: {RemoteWmiHelper.GetString(service, "Name")}",
                    $"DISPLAY_NAME: {RemoteWmiHelper.GetString(service, "DisplayName")}",
                    $"STATE: {RemoteWmiHelper.GetString(service, "State")}",
                    $"START_TYPE: {RemoteWmiHelper.GetString(service, "StartMode")}",
                    $"PATH_NAME: {RemoteWmiHelper.GetString(service, "PathName")}",
                    $"START_NAME: {RemoteWmiHelper.GetString(service, "StartName")}",
                    $"DESCRIPTION: {RemoteWmiHelper.GetString(service, "Description")}");
            }
            catch
            {
                return string.Empty;
            }
        }, ct);
    }

    private static async Task<bool> TryInvokeServiceMethodViaWmiAsync(
        string host,
        string username,
        string password,
        string serviceName,
        string methodName,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                var escapedName = RemoteWmiHelper.EscapeWqlString(serviceName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null) return false;

                var result = service.InvokeMethod(methodName, null, null);
                return RemoteWmiHelper.IsSuccessReturn(result);
            }
            catch
            {
                return false;
            }
        }, ct);
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
