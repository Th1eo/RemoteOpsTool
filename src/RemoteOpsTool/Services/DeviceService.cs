using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class DeviceService : IDeviceService
{
    private readonly IPsExecService _psExec;
    private readonly ILogService _log;

    public DeviceService(IPsExecService psExec, ILogService log)
    {
        _psExec = psExec;
        _log = log;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        var psDevices = "powershell \"Get-PnpDevice | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Csv -NoTypeInformation\"";
        var psDrivers = "powershell \"Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Select-Object DeviceID,DriverVersion | ConvertTo-Csv -NoTypeInformation\"";

        var taskDevices = _psExec.ExecuteAsync(host, username, password, psDevices, ct: ct);
        var taskDrivers = _psExec.ExecuteAsync(host, username, password, psDrivers, ct: ct);

        await Task.WhenAll(taskDevices, taskDrivers);

        var driverMap = ParseDrivers(taskDrivers.Result);
        return ParseDevices(taskDevices.Result, driverMap);
    }

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
        var psCommand = $"powershell \"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
        var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct);
        return result.Success;
    }

    public async Task<bool> EnableDeviceAsync(string host, string username, string password, string instanceId,
        CancellationToken ct = default)
    {
        var psCommand = $"powershell \"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
        var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct);
        return result.Success;
    }

    public async Task<bool> UninstallDeviceAsync(string host, string username, string password, string instanceId,
        CancellationToken ct = default)
    {
        var psCommand = $"powershell \"Uninstall-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
        var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct);
        return result.Success;
    }
}
