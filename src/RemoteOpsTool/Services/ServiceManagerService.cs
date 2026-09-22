using System.Management;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class ServiceManagerService : IServiceManagerService
{
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _log;

    public ServiceManagerService(IPsExecService psExec, IRemoteExecutionService execution, ILogService log)
    {
        _psExec = psExec;
        _execution = execution;
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
        var session = await _execution.CreateSessionAsync(host, username, password, ct);
        var result = (await session.ExecuteAsync(
            RemoteOperationKind.Inventory,
            NewRemoteCommand(host, username, password, psCmd),
            ct: ct)).Result;
        if (!result.Success)
        {
            _log.Warn($"PsExec 服务列表查询失败: {host} - {result.StdErr}");
            return [];
        }

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
            _log.Info($"PsExec PowerShell 服务查询无结果，回退 sc query: {host}");
            var scResult = (await session.ExecuteAsync(
                RemoteOperationKind.Inventory,
                NewRemoteCommand(host, username, password, "sc query state= all"),
                ct: ct)).Result;
            if (scResult.Success)
                services = ParseScQueryOutput(scResult.StdOut);
        }

        _log.Info($"服务列表查询完成: host={host} count={services.Count}");
        return services;
    }

    public async Task<string> GetServiceConfigAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"获取服务配置: host={host} service={serviceName} method=WMI/DCOM user={username}");
        var wmiConfig = await TryGetServiceConfigViaWmiAsync(host, username, password, serviceName, ct);
        if (!string.IsNullOrWhiteSpace(wmiConfig))
            return wmiConfig;

        var result = await _execution.ExecuteOnceAsync(
            host, username, password, $"sc qc \"{serviceName}\"",
            RemoteOperationKind.Inventory, ct: ct);
        if (result.Success)
            _log.Info($"服务配置查询完成: {serviceName}");
        else
            _log.Warn($"服务配置查询失败: {serviceName} - {result.StdErr}");
        return result.Success ? result.StdOut : result.StdErr;
    }

    public async Task<bool> StartServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"启动服务: host={host} service={serviceName} method=WMI/DCOM user={username}");
        if (!await TryStartServiceCoreAsync(host, username, password, serviceName, ct))
            return false;

        _log.Info($"已启动服务: {serviceName}");
        return true;
    }

    public async Task<bool> StopServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"停止服务: host={host} service={serviceName} method=WMI/DCOM user={username}");
        if (!await TryStopServiceCoreAsync(host, username, password, serviceName, ct))
            return false;

        _log.Info($"已停止服务: {serviceName}");
        return true;
    }

    public async Task<bool> RestartServiceAsync(string host, string username, string password, string serviceName,
        CancellationToken ct = default)
    {
        _log.Debug($"重启服务: host={host} service={serviceName} method=WMI/DCOM user={username}");
        // stop/start 分别只执行一次。WMI 失败才允许回退 sc；一旦命令已提交，
        // 后续只轮询状态，超时也不会重放命令。
        if (!await TryStopServiceCoreAsync(host, username, password, serviceName, ct))
            return false;

        if (!await TryStartServiceCoreAsync(host, username, password, serviceName, ct))
            return false;

        _log.Info($"已重启服务: {serviceName}");
        return true;
    }

    private const int ServiceStatePollIntervalMs = 300;
    private const int ServiceStatePollTimeoutMs = 20000;

    private async Task<bool> TryStartServiceCoreAsync(
        string host,
        string username,
        string password,
        string serviceName,
        CancellationToken ct)
    {
        var wmi = await TryInvokeServiceMethodViaWmiAsync(
            host, username, password, serviceName, "StartService", ct);
        switch (wmi.Outcome)
        {
            case WmiServiceMethodOutcome.Succeeded:
            case WmiServiceMethodOutcome.AlreadyInTargetState:
                return await WaitForServiceStateAsync(
                    host, username, password, serviceName, ServiceRuntimeState.Running, "启动", ct);
            case WmiServiceMethodOutcome.NotFound:
                _log.Error(ServiceCommandHelper.ServiceNotInstalledMessage(serviceName));
                return false;
            case WmiServiceMethodOutcome.AccessDenied:
                _log.Error($"启动服务被拒绝: {serviceName} - 凭据对目标服务的启动权限不足（WMI 返回 2/拒绝访问）。");
                return false;
        }

        _log.Debug($"WMI/DCOM 启动未生效(return={wmi.ReturnCode})，回退 sc start: {serviceName}");
        var result = await ExecuteServiceChangeAsync(
            host, username, password, $"sc start \"{serviceName}\"", ct);
        if (!result.Success)
        {
            LogServiceChangeFailure("启动", serviceName, result);
            return false;
        }

        return await WaitForServiceStateAsync(
            host, username, password, serviceName, ServiceRuntimeState.Running, "启动", ct);
    }

    private async Task<bool> TryStopServiceCoreAsync(
        string host,
        string username,
        string password,
        string serviceName,
        CancellationToken ct)
    {
        var wmi = await TryInvokeServiceMethodViaWmiAsync(
            host, username, password, serviceName, "StopService", ct);
        switch (wmi.Outcome)
        {
            case WmiServiceMethodOutcome.Succeeded:
            case WmiServiceMethodOutcome.AlreadyInTargetState:
                return await WaitForServiceStateAsync(
                    host, username, password, serviceName, ServiceRuntimeState.Stopped, "停止", ct);
            case WmiServiceMethodOutcome.NotFound:
                _log.Error(ServiceCommandHelper.ServiceNotInstalledMessage(serviceName));
                return false;
            case WmiServiceMethodOutcome.AccessDenied:
                _log.Error($"停止服务被拒绝: {serviceName} - 凭据对目标服务的停止权限不足（WMI 返回 2/拒绝访问）。");
                return false;
        }

        _log.Debug($"WMI/DCOM 停止未生效(return={wmi.ReturnCode})，回退 sc stop: {serviceName}");
        var result = await ExecuteServiceChangeAsync(
            host, username, password, $"sc stop \"{serviceName}\"", ct);
        if (!result.Success)
        {
            LogServiceChangeFailure("停止", serviceName, result);
            return false;
        }

        return await WaitForServiceStateAsync(
            host, username, password, serviceName, ServiceRuntimeState.Stopped, "停止", ct);
    }

    private async Task<bool> WaitForServiceStateAsync(
        string host,
        string username,
        string password,
        string serviceName,
        ServiceRuntimeState target,
        string actionText,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ServiceStatePollTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await TryReadServiceStateAsync(host, username, password, serviceName, ct);
            if (state == target)
                return true;

            await Task.Delay(ServiceStatePollIntervalMs, ct);
        }

        _log.Warn(
            $"服务{actionText}命令已提交，但目标状态未在 {ServiceStatePollTimeoutMs / 1000} 秒内达到；" +
            $"已停止轮询且未重放命令: {serviceName}");
        return false;
    }

    private async Task<ServiceRuntimeState> TryReadServiceStateAsync(
        string host,
        string username,
        string password,
        string serviceName,
        CancellationToken ct)
    {
        var wmiState = await TryReadServiceStateViaWmiAsync(host, username, password, serviceName, ct);
        if (wmiState != ServiceRuntimeState.Unknown)
            return wmiState;

        try
        {
            var result = HostHelper.IsLocalHost(host)
                ? await ProcessHelper.RunAsync("sc.exe", $"query \"{serviceName}\"", ct)
                : await _execution.ExecuteOnceAsync(
                    host,
                    username,
                    password,
                    $"sc query \"{serviceName}\"",
                    RemoteOperationKind.Inventory,
                    ct: ct);
            return result.Success
                ? ServiceCommandHelper.ParseScQueryState(result.StdOut)
                : ServiceRuntimeState.Unknown;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ServiceRuntimeState.Unknown;
        }
    }

    private async Task<ServiceRuntimeState> TryReadServiceStateViaWmiAsync(
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
                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery($"SELECT State FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                return service == null
                    ? ServiceRuntimeState.Unknown
                    : ServiceCommandHelper.ParseState(RemoteWmiHelper.GetString(service, "State"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return ServiceRuntimeState.Unknown;
            }
        }, ct);
    }

    private void LogServiceChangeFailure(string actionText, string serviceName, CommandResult result)
    {
        var error = (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr).Trim();
        if (ServiceCommandHelper.IsServiceNotInstalled(result.ExitCode, result.StdOut + result.StdErr))
            _log.Error(ServiceCommandHelper.ServiceNotInstalledMessage(serviceName));
        else
            _log.Warn($"{actionText}服务失败: {serviceName} - {error}");
    }

    private Task<CommandResult> ExecuteServiceChangeAsync(
        string host,
        string username,
        string password,
        string command,
        CancellationToken ct)
    {
        return HostHelper.IsLocalHost(host)
            ? _psExec.ExecuteLocalElevatedAsync(
                host, username, password, command, ct, CommandShell.Direct)
            : _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.Command, ct: ct);
    }

    private static RemoteCommand NewRemoteCommand(
        string host,
        string username,
        string password,
        string command) => new()
    {
        TargetHost = host,
        Username = username,
        Password = password,
        Command = command,
        Shell = CommandShell.Direct,
        WrapCmd = false,
    };

    private async Task<List<ServiceInfo>> TryGetServicesViaWmiAsync(
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
                    new ObjectQuery("SELECT Name,DisplayName,State,StartMode,DelayedAutoStart FROM Win32_Service"));
                foreach (ManagementObject svc in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    services.Add(new ServiceInfo
                    {
                        ServiceName = RemoteWmiHelper.GetString(svc, "Name"),
                        DisplayName = RemoteWmiHelper.GetString(svc, "DisplayName"),
                        Status = RemoteWmiHelper.GetString(svc, "State"),
                        StartType = ServiceCommandHelper.StartTypeFromWmi(RemoteWmiHelper.GetString(svc, "StartMode"), RemoteWmiHelper.GetBool(svc, "DelayedAutoStart"))
                    });
                }
                _log.Debug($"WMI 服务列表查询成功: {host} count={services.Count}");
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 服务列表查询失败: {host} - {ex.Message}");
                return [];
            }
            return services;
        }, ct);
    }

    private async Task<string> TryGetServiceConfigViaWmiAsync(
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
                    new ObjectQuery($"SELECT Name,DisplayName,State,StartMode,DelayedAutoStart,PathName,StartName,Description,DesktopInteract FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null) return string.Empty;

                _log.Debug($"WMI 服务配置查询成功: {serviceName}");
                return string.Join(Environment.NewLine,
                    $"SERVICE_NAME: {RemoteWmiHelper.GetString(service, "Name")}",
                    $"DISPLAY_NAME: {RemoteWmiHelper.GetString(service, "DisplayName")}",
                    $"STATE: {RemoteWmiHelper.GetString(service, "State")}",
                    $"START_TYPE: {RemoteWmiHelper.GetString(service, "StartMode")}{(RemoteWmiHelper.GetBool(service, "DelayedAutoStart") ? " (DELAYED)" : string.Empty)}",
                    $"PATH_NAME: {RemoteWmiHelper.GetString(service, "PathName")}",
                    $"START_NAME: {RemoteWmiHelper.GetString(service, "StartName")}",
                    $"DESCRIPTION: {RemoteWmiHelper.GetString(service, "Description")}");
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 服务配置查询失败: {serviceName} - {ex.Message}");
                return string.Empty;
            }
        }, ct);
    }

    /// <summary>WMI 服务方法调用结果：Outcome 表达语义，ReturnCode 保留原始 WMI 返回值（0/10/11/5/1060 等）。</summary>
    private sealed record WmiServiceMethodResult(WmiServiceMethodOutcome Outcome, uint ReturnCode);

    private async Task<WmiServiceMethodResult> TryInvokeServiceMethodViaWmiAsync(
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
                    new ObjectQuery($"SELECT Name,DisplayName,State,StartMode,DelayedAutoStart,PathName,StartName,Description,DesktopInteract FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null)
                {
                    _log.Debug($"WMI {methodName}: 未找到服务 {serviceName}（1060）。");
                    return new WmiServiceMethodResult(WmiServiceMethodOutcome.NotFound, 1060);
                }

                using var result = service.InvokeMethod(methodName, null, new System.Management.InvokeMethodOptions());
                var rawCode = RemoteWmiHelper.GetUInt32(result, "ReturnValue");
                var outcome = ServiceCommandHelper.MapWmiMethodReturnCode(methodName, rawCode);
                _log.Debug($"WMI {methodName}完成: {serviceName} return={rawCode} outcome={outcome}");
                return new WmiServiceMethodResult(outcome, rawCode);
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI {methodName}调用异常: {serviceName} - {ex.Message}");
                return new WmiServiceMethodResult(WmiServiceMethodOutcome.Failed, 0);
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
