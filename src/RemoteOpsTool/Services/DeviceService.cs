using System.Management;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class DeviceService : IDeviceService
{
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _log;

    public DeviceService(IPsExecService psExec, IRemoteExecutionService execution, ILogService log)
    {
        _psExec = psExec;
        _execution = execution;
        _log = log;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        _log.Debug($"获取设备列表: host={host} method=WMI/DCOM user={username}");
        var wmiDevices = await TryGetDevicesViaWmiAsync(host, username, password, ct);
        if (wmiDevices.Count > 0)
        {
            _log.Debug($"WMI/DCOM 设备列表完成: host={host} count={wmiDevices.Count}");
            return wmiDevices;
        }

        _log.Warn($"WMI/DCOM 设备查询不可用，回退到 PsExec: {host}");

        var psDevices = "powershell \"Get-PnpDevice | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Csv -NoTypeInformation\"";
        var psDrivers = "powershell \"Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Select-Object DeviceID,DriverVersion | ConvertTo-Csv -NoTypeInformation\"";

        // One capability snapshot for both inventory commands. Execute
        // sequentially because a session carries shared transport preference
        // state and is intentionally not used concurrently.
        var session = await _execution.CreateSessionAsync(host, username, password, ct);
        var devicesResult = (await session.ExecuteAsync(
            RemoteOperationKind.Inventory,
            NewRemoteCommand(host, username, password, psDevices),
            ct: ct)).Result;
        var driversResult = (await session.ExecuteAsync(
            RemoteOperationKind.Inventory,
            NewRemoteCommand(host, username, password, psDrivers),
            ct: ct)).Result;

        var driverMap = ParseDrivers(driversResult);
        return ParseDevices(devicesResult, driverMap);
    }

    private async Task<List<DeviceInfo>> TryGetDevicesViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var devices = new List<DeviceInfo>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                var driverMap = QueryDriverVersions(scope, ct);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Status,PNPClass,Name,PNPDeviceID FROM Win32_PnPEntity"));
                foreach (ManagementObject device in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var instanceId = RemoteWmiHelper.GetString(device, "PNPDeviceID");
                    devices.Add(new DeviceInfo
                    {
                        Status = RemoteWmiHelper.GetString(device, "Status"),
                        Class = RemoteWmiHelper.GetString(device, "PNPClass"),
                        FriendlyName = RemoteWmiHelper.GetString(device, "Name"),
                        InstanceId = instanceId,
                        DriverVersion = ResolveDriverVersion(instanceId, driverMap)
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 设备列表查询失败: {host} - {ex.Message}");
                return [];
            }
            return devices;
        }, ct);
    }

    private static Dictionary<string, string> QueryDriverVersions(ManagementScope scope, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT DeviceID,DriverVersion FROM Win32_PnPSignedDriver"));
        foreach (ManagementObject driver in searcher.Get())
        {
            ct.ThrowIfCancellationRequested();
            var id = RemoteWmiHelper.GetString(driver, "DeviceID");
            if (string.IsNullOrWhiteSpace(id)) continue;

            map[id] = RemoteWmiHelper.GetString(driver, "DriverVersion");
        }
        return map;
    }

    private static string ResolveDriverVersion(string instanceId, Dictionary<string, string> driverMap)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return string.Empty;

        var searchId = instanceId;
        while (searchId.Length > 0)
        {
            if (driverMap.TryGetValue(searchId, out var version))
                return version;

            var lastSlash = searchId.LastIndexOf('\\');
            if (lastSlash < 0) break;
            searchId = searchId[..lastSlash];
        }

        foreach (var kv in driverMap)
        {
            if (kv.Key.StartsWith(instanceId, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return string.Empty;
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
        Shell = CommandShell.Cmd,
        WrapCmd = true,
    };

    private static Dictionary<string, string> ParseDrivers(CommandResult result)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!result.Success) return map;

        foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("\"DeviceID\"")) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            var id = parts[0].Trim('"');
            var ver = parts.Length > 1 ? parts[1].Trim('"') : "";
            if (!string.IsNullOrEmpty(id))
                map[id] = ver;
        }
        return map;
    }

    private static List<DeviceInfo> ParseDevices(CommandResult result, Dictionary<string, string> driverMap)
    {
        var devices = new List<DeviceInfo>();
        if (!result.Success) return devices;

        foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("\"Status\"")) continue;
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            var instanceId = parts[3].Trim('"');

            var driverVer = "";
            var searchId = instanceId;
            while (searchId.Length > 0)
            {
                if (driverMap.TryGetValue(searchId, out var ver))
                {
                    driverVer = ver;
                    break;
                }
                var lastSlash = searchId.LastIndexOf('\\');
                if (lastSlash < 0) break;
                searchId = searchId[..lastSlash];
            }

            if (string.IsNullOrEmpty(driverVer))
            {
                foreach (var kv in driverMap)
                {
                    if (kv.Key.StartsWith(instanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        driverVer = kv.Value;
                        break;
                    }
                }
            }

            devices.Add(new DeviceInfo
            {
                Status = parts[0].Trim('"'),
                Class = parts[1].Trim('"'),
                FriendlyName = parts[2].Trim('"'),
                InstanceId = instanceId,
                DriverVersion = driverVer
            });
        }
        return devices;
    }

    public async Task<bool> DisableDeviceAsync(string host, string username, string password, string instanceId,
        CancellationToken ct = default)
    {
        _log.Debug($"禁用设备: host={host} instanceId={instanceId} method=PsExec user={username}");
        var psCommand = $"powershell \"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteLocalElevatedAsync(
                host, username, password, psCommand, ct, CommandShell.Direct)
            : await _execution.ExecuteOnceAsync(
                host, username, password, psCommand, RemoteOperationKind.Command,
                CommandShell.Direct, ct: ct);
        if (result.Success)
            _log.Info($"设备已禁用: {instanceId}");
        else
            _log.Warn($"设备禁用失败: {instanceId} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> EnableDeviceAsync(string host, string username, string password, string instanceId,
        CancellationToken ct = default)
    {
        _log.Debug($"启用设备: host={host} instanceId={instanceId} method=PsExec user={username}");
        var psCommand = $"powershell \"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteLocalElevatedAsync(
                host, username, password, psCommand, ct, CommandShell.Direct)
            : await _execution.ExecuteOnceAsync(
                host, username, password, psCommand, RemoteOperationKind.Command,
                CommandShell.Direct, ct: ct);
        if (result.Success)
            _log.Info($"设备已启用: {instanceId}");
        else
            _log.Warn($"设备启用失败: {instanceId} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> UninstallDeviceAsync(string host, string username, string password, string instanceId,
        CancellationToken ct = default)
    {
        _log.Debug($"卸载设备: host={host} instanceId={instanceId} method=PsExec user={username}");
        var psCommand = $"powershell \"Uninstall-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteLocalElevatedAsync(
                host, username, password, psCommand, ct, CommandShell.Direct)
            : await _execution.ExecuteOnceAsync(
                host, username, password, psCommand, RemoteOperationKind.Command,
                CommandShell.Direct, ct: ct);
        if (result.Success)
            _log.Info($"设备已卸载: {instanceId}");
        else
            _log.Warn($"设备卸载失败: {instanceId} - {result.StdErr}");
        return result.Success;
    }
}
