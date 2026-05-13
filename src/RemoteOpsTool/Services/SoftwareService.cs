using System.Management;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class SoftwareService : ISoftwareService
{
    private const uint HkeyClassesRoot = 0x80000000;
    private const uint HkeyCurrentUser = 0x80000001;
    private const uint HkeyLocalMachine = 0x80000002;
    private const string SoftwareCsvBeginMarker = "__REMOTEOPS_SOFTWARE_CSV_BEGIN__";
    private const string SoftwareCsvEndMarker = "__REMOTEOPS_SOFTWARE_CSV_END__";
    private static readonly string[] DeepCleanupRegistryKeys =
    [
        @"HKCR:\Installer\Products"
    ];

    private readonly IPsExecService _psExec;
    private readonly ILogService _log;

    public SoftwareService(IPsExecService psExec, ILogService log)
    {
        _psExec = psExec;
        _log = log;
    }

    public async Task<List<SoftwareInfo>> GetInstalledSoftwareAsync(string host, string username, string password,
        bool deepCleanup = false,
        CancellationToken ct = default)
    {
        _log.Debug($"获取软件清单: host={host} method=WMI StdRegProv user={username}");
        var software = await TryGetInstalledSoftwareViaRegistryProviderAsync(host, username, password, ct);
        if (software.Count > 0)
        {
            _log.Debug($"WMI StdRegProv 软件清单完成: host={host} count={software.Count}");
        }
        else
        {
            _log.Warn($"WMI StdRegProv 软件清单查询不可用，回退到 PsExec: {host}");
            software = await GetInstalledSoftwareViaPsExecAsync(host, username, password, ct);
        }

        if (deepCleanup)
        {
            _log.Debug($"深度清理扫描: host={host} method=WMI StdRegProv user={username}");
            var deepSoftware = await TryGetDeepCleanupSoftwareViaRegistryProviderAsync(host, username, password, ct);
            _log.Info($"深度清理扫描完成: host={host} count={deepSoftware.Count}");
            return software
                .Concat(deepSoftware)
                .Where(s => !string.IsNullOrWhiteSpace(s.DisplayName))
                .OrderBy(s => s.DisplayName)
                .ThenBy(s => s.RegistryKey)
                .ToList();
        }

        return software
            .Where(s => !string.IsNullOrWhiteSpace(s.DisplayName))
            .OrderBy(s => s.DisplayName)
            .ThenBy(s => s.RegistryKey)
            .ToList();
    }

    private async Task<List<SoftwareInfo>> GetInstalledSoftwareViaPsExecAsync(string host, string username, string password,
        CancellationToken ct)
    {
        var software = new List<SoftwareInfo>();
        foreach (var regKey in AppConstants.SoftwareRegistryKeys)
        {
            var psCommand = BuildSoftwareRegistryPsCommand(regKey);
            var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct, wrapCmd: false);
            if (!result.Success) continue;

            var lines = ExtractSoftwareCsvLines(result.StdOut);
            foreach (var line in lines)
            {
                var parts = ParseCsvLine(line);
                if (parts.Length < 5) continue;
                if (parts[0].Equals("DisplayName", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(parts[0])) continue;

                var childName = parts[4];
                software.Add(new SoftwareInfo
                {
                    DisplayName = parts[0],
                    UninstallString = parts[1],
                    Publisher = parts[2],
                    InstallLocation = parts[3],
                    RegistryKey = $@"{regKey.TrimEnd('\\')}\{childName}"
                });
            }
        }

        return DeduplicateSoftware(software);
    }

    private static string BuildSoftwareRegistryPsCommand(string regKey)
    {
        var scanPath = $@"{regKey.TrimEnd('\\')}\*";
        var script = "$ErrorActionPreference='SilentlyContinue'; " +
            $"Write-Output '{SoftwareCsvBeginMarker}'; " +
            $"Get-ItemProperty -Path {PowerShellLiteral(scanPath)} -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.DisplayName } | " +
            "Select-Object DisplayName,UninstallString,Publisher,InstallLocation,PSChildName | " +
            "ConvertTo-Csv -NoTypeInformation; " +
            $"Write-Output '{SoftwareCsvEndMarker}'";
        return $"powershell -NoProfile -Command \"{script}\"";
    }

    private static IEnumerable<string> ExtractSoftwareCsvLines(string stdout)
    {
        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var captured = new List<string>();
        var insideCsv = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Equals(SoftwareCsvBeginMarker, StringComparison.Ordinal))
            {
                insideCsv = true;
                continue;
            }

            if (line.Equals(SoftwareCsvEndMarker, StringComparison.Ordinal))
                break;

            if (insideCsv)
                captured.Add(line);
        }

        if (captured.Count > 0)
            return captured;

        return lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("\"", StringComparison.Ordinal));
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static string PowerShellLiteral(string value)
    {
        return $"'{value.Replace("'", "''")}'";
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
                    if (!TryParseRegistryPath(key, out var registryPath))
                        continue;

                    foreach (var childName in EnumSubKeys(registry, registryPath.Hive, registryPath.SubKey))
                    {
                        ct.ThrowIfCancellationRequested();
                        var appKey = $@"{registryPath.SubKey}\{childName}";
                        var displayName = GetRegistryString(registry, registryPath.Hive, appKey, "DisplayName");
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        software.Add(new SoftwareInfo
                        {
                            DisplayName = displayName,
                            UninstallString = GetRegistryString(registry, registryPath.Hive, appKey, "UninstallString"),
                            Publisher = GetRegistryString(registry, registryPath.Hive, appKey, "Publisher"),
                            InstallLocation = GetRegistryString(registry, registryPath.Hive, appKey, "InstallLocation"),
                            Version = GetRegistryString(registry, registryPath.Hive, appKey, "DisplayVersion"),
                            RegistryKey = registryPath.BuildDisplayPath(childName)
                        });
                    }
                }
            }
            catch (Exception ex) { _log.Debug($"WMI StdRegProv 软件清单查询失败: {host} - {ex.Message}"); return []; }

            return software
                .OrderBy(s => s.DisplayName)
                .ThenBy(s => s.RegistryKey)
                .ToList();
        }, ct);
    }

    private static bool TryParseRegistryPath(string path, out RegistryPath registryPath)
    {
        registryPath = default;
        var normalized = path.Replace('/', '\\').Trim().TrimEnd('\\');
        var wildcardSuffix = @"\*";
        if (normalized.EndsWith(wildcardSuffix, StringComparison.Ordinal))
            normalized = normalized[..^wildcardSuffix.Length];

        var hive = normalized.Split(['\\'], 2, StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd(':');
        var subKey = normalized.Contains('\\') ? normalized[(normalized.IndexOf('\\') + 1)..].Trim('\\') : string.Empty;

        var hiveInfo = hive.ToUpperInvariant() switch
        {
            "HKCR" or "HKEY_CLASSES_ROOT" => (Key: HkeyClassesRoot, Display: "HKCR"),
            "HKCU" or "HKEY_CURRENT_USER" => (Key: HkeyCurrentUser, Display: "HKCU"),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (Key: HkeyLocalMachine, Display: "HKLM"),
            _ => (Key: 0u, Display: string.Empty)
        };

        if (hiveInfo.Key == 0 || string.IsNullOrWhiteSpace(subKey))
            return false;

        registryPath = new RegistryPath(hiveInfo.Key, hiveInfo.Display, subKey);
        return true;
    }

    private static IEnumerable<string> EnumSubKeys(ManagementClass registry, uint hive, string subKey)
    {
        using var inParams = registry.GetMethodParameters("EnumKey");
        inParams["hDefKey"] = hive;
        inParams["sSubKeyName"] = subKey;

        using var outParams = registry.InvokeMethod("EnumKey", inParams, null);
        if (RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") != 0)
            return [];

        return outParams["sNames"] is string[] names ? names : [];
    }

    private static string GetRegistryString(ManagementClass registry, uint hive, string subKey, string valueName)
    {
        try
        {
            using var inParams = registry.GetMethodParameters("GetStringValue");
            inParams["hDefKey"] = hive;
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

    private async Task<List<SoftwareInfo>> TryGetDeepCleanupSoftwareViaRegistryProviderAsync(
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
                foreach (var key in DeepCleanupRegistryKeys)
                {
                    if (!TryParseRegistryPath(key, out var registryPath))
                        continue;

                    foreach (var childName in EnumSubKeys(registry, registryPath.Hive, registryPath.SubKey))
                    {
                        ct.ThrowIfCancellationRequested();
                        var productKey = $@"{registryPath.SubKey}\{childName}";
                        var installPropertiesKey = $@"{productKey}\InstallProperties";

                        var displayName = FirstNonEmpty(
                            GetRegistryString(registry, registryPath.Hive, installPropertiesKey, "DisplayName"),
                            GetRegistryString(registry, registryPath.Hive, productKey, "ProductName"),
                            GetRegistryString(registry, registryPath.Hive, productKey, "DisplayName"),
                            childName);

                        software.Add(new SoftwareInfo
                        {
                            DisplayName = displayName,
                            UninstallString = GetRegistryString(registry, registryPath.Hive, installPropertiesKey, "UninstallString"),
                            Publisher = FirstNonEmpty(
                                GetRegistryString(registry, registryPath.Hive, installPropertiesKey, "Publisher"),
                                GetRegistryString(registry, registryPath.Hive, productKey, "Publisher")),
                            InstallLocation = GetRegistryString(registry, registryPath.Hive, installPropertiesKey, "InstallLocation"),
                            Version = GetRegistryString(registry, registryPath.Hive, installPropertiesKey, "DisplayVersion"),
                            RegistryKey = registryPath.BuildDisplayPath(childName)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI StdRegProv 深度清理扫描失败: {host} - {ex.Message}");
                return [];
            }

            return software
                .Where(s => !string.IsNullOrWhiteSpace(s.DisplayName))
                .OrderBy(s => s.DisplayName)
                .ThenBy(s => s.RegistryKey)
                .ToList();
        }, ct);
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static void MergeSoftware(List<SoftwareInfo> target, IEnumerable<SoftwareInfo> additions)
    {
        foreach (var item in additions)
        {
            if (target.Any(existing => IsSameSoftwareEntry(existing, item)))
                continue;

            target.Add(item);
        }
    }

    private static List<SoftwareInfo> DeduplicateSoftware(IEnumerable<SoftwareInfo> software)
    {
        var result = new List<SoftwareInfo>();
        MergeSoftware(result, software.Where(s => !string.IsNullOrWhiteSpace(s.DisplayName)));
        return result
            .OrderBy(s => s.DisplayName)
            .ToList();
    }

    private static bool IsSameSoftwareEntry(SoftwareInfo left, SoftwareInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.RegistryKey) &&
            !string.IsNullOrWhiteSpace(right.RegistryKey) &&
            left.RegistryKey.Equals(right.RegistryKey, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(left.DisplayName) &&
               !string.IsNullOrWhiteSpace(right.DisplayName) &&
               left.DisplayName.Equals(right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct RegistryPath(uint Hive, string DisplayHive, string SubKey)
    {
        public string BuildDisplayPath(string childName) => $@"{DisplayHive}:\{SubKey}\{childName}";
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
