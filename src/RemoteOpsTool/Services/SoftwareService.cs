using System.Management;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class SoftwareService : ISoftwareService
{
    private const uint HkeyLocalMachine = 0x80000002;

    private readonly IPsExecService _psExec;
    private readonly ILogService _log;

    public SoftwareService(IPsExecService psExec, ILogService log)
    {
        _psExec = psExec;
        _log = log;
    }

    public async Task<List<SoftwareInfo>> GetInstalledSoftwareAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        _log.Debug($"获取软件清单: host={host} method=WMI StdRegProv user={username}");
        var wmiSoftware = await TryGetInstalledSoftwareViaRegistryProviderAsync(host, username, password, ct);
        if (wmiSoftware.Count > 0)
        {
            _log.Debug($"WMI StdRegProv 软件清单完成: host={host} count={wmiSoftware.Count}");
            return wmiSoftware;
        }

        _log.Warn($"WMI StdRegProv 软件清单查询不可用，回退到 PsExec: {host}");

        var software = new List<SoftwareInfo>();
        foreach (var regKey in AppConstants.SoftwareRegistryKeys)
        {
            var psCommand = $"powershell \"Get-ItemProperty '{regKey}\\*' -ErrorAction SilentlyContinue | Where-Object {{ $_.DisplayName }} | Select-Object DisplayName,UninstallString,Publisher,InstallLocation,PSChildName | ConvertTo-Csv -NoTypeInformation\"";

            var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct);
            if (!result.Success) continue;

            var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("\"DisplayName\"")) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                var childName = parts[4].Trim('"');
                software.Add(new SoftwareInfo
                {
                    DisplayName = parts[0].Trim('"'),
                    UninstallString = parts[1].Trim('"'),
                    Publisher = parts[2].Trim('"'),
                    InstallLocation = parts[3].Trim('"'),
                    RegistryKey = $"{regKey}\\{childName}"
                });
            }
        }

        return software.DistinctBy(s => s.DisplayName).ToList();
    }

    private async Task<List<SoftwareInfo>> TryGetInstalledSoftwareViaRegistryProviderAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var software = new List<SoftwareInfo>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password, @"root\default");
                scope.Connect();

                using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                foreach (var key in AppConstants.SoftwareRegistryKeys)
                {
                    var subKey = NormalizeHklmRegistryPath(key);
                    foreach (var childName in EnumSubKeys(registry, subKey))
                    {
                        ct.ThrowIfCancellationRequested();
                        var appKey = $@"{subKey}\{childName}";
                        var displayName = GetRegistryString(registry, appKey, "DisplayName");
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        software.Add(new SoftwareInfo
                        {
                            DisplayName = displayName,
                            UninstallString = GetRegistryString(registry, appKey, "UninstallString"),
                            Publisher = GetRegistryString(registry, appKey, "Publisher"),
                            InstallLocation = GetRegistryString(registry, appKey, "InstallLocation"),
                            Version = GetRegistryString(registry, appKey, "DisplayVersion"),
                            RegistryKey = $@"HKLM:\{appKey}"
                        });
                    }
                }
            }
            catch (Exception ex) { _log.Debug($"WMI StdRegProv 软件清单查询失败: {host} - {ex.Message}"); return []; }

            return software
                .GroupBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.DisplayName)
                .ToList();
        }, ct);
    }

    private static string NormalizeHklmRegistryPath(string path)
    {
        return path
            .Replace("HKLM:\\", "", StringComparison.OrdinalIgnoreCase)
            .Replace('/', '\\')
            .Trim('\\');
    }

    private static IEnumerable<string> EnumSubKeys(ManagementClass registry, string subKey)
    {
        using var inParams = registry.GetMethodParameters("EnumKey");
        inParams["hDefKey"] = HkeyLocalMachine;
        inParams["sSubKeyName"] = subKey;

        using var outParams = registry.InvokeMethod("EnumKey", inParams, null);
        if (RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") != 0)
            return [];

        return outParams["sNames"] is string[] names ? names : [];
    }

    private static string GetRegistryString(ManagementClass registry, string subKey, string valueName)
    {
        try
        {
            using var inParams = registry.GetMethodParameters("GetStringValue");
            inParams["hDefKey"] = HkeyLocalMachine;
            inParams["sSubKeyName"] = subKey;
            inParams["sValueName"] = valueName;

            using var outParams = registry.InvokeMethod("GetStringValue", inParams, null);
            if (RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") != 0)
                return string.Empty;

            return outParams["sValue"]?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<bool> UninstallSilentlyAsync(string host, string username, string password,
        string uninstallString, CancellationToken ct = default)
    {
        _log.Debug($"静默卸载软件: host={host} command={uninstallString} method=PsExec user={username}");
        var result = await _psExec.ExecuteAsync(host, username, password, uninstallString, ct: ct);
        if (result.Success) _log.Info($"静默卸载完成: {uninstallString}");
        else _log.Warn($"静默卸载失败: {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> UninstallInteractiveAsync(string host, string username, string password,
        string uninstallString, int sessionId, CancellationToken ct = default)
    {
        _log.Debug($"交互卸载软件: host={host} session={sessionId} command={uninstallString} method=PsExec user={username}");
        var result = await _psExec.ExecuteAsync(host, username, password, uninstallString,
            interactiveSession: true, sessionId: sessionId, ct: ct);
        if (result.Success) _log.Info($"交互卸载已启动: {uninstallString}");
        else _log.Warn($"交互卸载失败: {result.StdErr}");
        return result.Success;
    }
}
