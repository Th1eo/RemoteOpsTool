using System.Management;
using System.Text.RegularExpressions;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class SoftwareService : ISoftwareService
{
    private const uint HkeyClassesRoot = 0x80000000;
    private const uint HkeyCurrentUser = 0x80000001;
    private const uint HkeyLocalMachine = 0x80000002;
    private const string SoftwareCsvBeginMarker = "__REMOTEOPS_SOFTWARE_CSV_BEGIN__";
    private const string SoftwareCsvEndMarker = "__REMOTEOPS_SOFTWARE_CSV_END__";
    private static readonly Regex ProductCodeRegex = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.Compiled);

    private static readonly string[] KnownSilentSwitches =
    [
        "/quiet", "/qn", "/qb", "/passive", "/s", "/silent", "/verysilent",
        "--quiet", "--silent", "--unattended", "-quiet", "-silent", "-s"
    ];

    private static readonly string[] DeepCleanupRegistryKeys =
    [
        @"HKCR:\Installer\Products"
    ];

    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _log;

    public SoftwareService(IPsExecService psExec, IRemoteExecutionService execution, ILogService log)
    {
        _psExec = psExec;
        _execution = execution;
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

        // One capability snapshot for the whole inventory. The session pins the
        // transport for every registry key so a mid-listing capability change
        // cannot switch channels half way through the enumeration.
        var session = await _execution.CreateSessionAsync(host, username, password, ct);
        foreach (var regKey in AppConstants.SoftwareRegistryKeys)
        {
            var psCommand = BuildSoftwareRegistryPsCommand(regKey);
            var result = (await session.ExecuteAsync(
                RemoteOperationKind.Inventory,
                NewRemoteCommand(host, username, password, psCommand),
                ct: ct)).Result;
            if (!result.Success) continue;

            var lines = ExtractSoftwareCsvLines(result.StdOut);
            foreach (var line in lines)
            {
                var parts = ParseCsvLine(line);
                if (parts.Length < 6) continue;
                if (parts[0].Equals("DisplayName", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(parts[0])) continue;

                var childName = parts[5];
                software.Add(new SoftwareInfo
                {
                    DisplayName = parts[0],
                    UninstallString = parts[1],
                    QuietUninstallString = parts[2],
                    Publisher = parts[3],
                    InstallLocation = parts[4],
                    RegistryKey = $@"{regKey.TrimEnd('\\')}\{childName}"
                });
            }
        }

        return DeduplicateSoftware(software);
    }

    public async Task<bool> DeleteRegistryKeyAsync(string host, string username, string password,
        string registryKey, CancellationToken ct = default)
    {
        if (!TryParseRegistryPath(registryKey, out var registryPath))
            return false;

        _log.Debug($"删除软件注册表键: host={host} key={registryKey} method=WMI StdRegProv user={username}");
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password, @"root\default");
                scope.Connect();

                using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                using var inParams = registry.GetMethodParameters("DeleteKey");
                inParams["hDefKey"] = registryPath.Hive;
                inParams["sSubKeyName"] = registryPath.SubKey;

                using var outParams = registry.InvokeMethod("DeleteKey", inParams, null);
                var returnValue = RemoteWmiHelper.GetUInt32(outParams, "ReturnValue");
                // StdRegProv returns 2 when the key is already absent. Cleanup is idempotent,
                // so an absent key is a successful end state rather than an operation failure.
                return returnValue is 0 or 2;
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI StdRegProv 软件注册表键删除失败，准备回退到 PsExec: {host} key={registryKey} - {ex.Message}");
                return false;
            }
        }, ct);
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
        WrapCmd = false,
    };
    private static string BuildSoftwareRegistryPsCommand(string regKey)
    {
        var scanPath = $@"{regKey.TrimEnd('\\')}\*";
        var script = "$ErrorActionPreference='SilentlyContinue'; " +
            $"Write-Output '{SoftwareCsvBeginMarker}'; " +
            $"Get-ItemProperty -Path {PowerShellLiteral(scanPath)} -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.DisplayName } | " +
            "Select-Object DisplayName,UninstallString,QuietUninstallString,Publisher,InstallLocation,PSChildName | " +
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
                            QuietUninstallString = GetRegistryString(registry, registryPath.Hive, appKey, "QuietUninstallString"),
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
                            QuietUninstallString = GetRegistryString(registry, registryPath.Hive, installPropertiesKey, "QuietUninstallString"),
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
        SoftwareInfo software, CancellationToken ct = default)
    {
        var plan = BuildSilentUninstallCommand(software);
        if (string.IsNullOrWhiteSpace(plan.Command))
        {
            _log.Warn($"静默卸载跳过: {software.DisplayName} 没有可用卸载命令");
            return false;
        }

        _log.Info($"静默卸载参数: {software.DisplayName} - {plan.Description}");
        _log.Debug($"静默卸载软件: host={host} command={plan.Command} method=PsExec user={username}");
        // Uninstall strings are executable paths plus arguments. Passing the normal
        // quoted executable form as direct PsExec arguments avoids an extra `cmd /c`
        // layer, which can turn a path such as `"C:\\Program Files\\7-Zip\\Uninstall.exe"`
        // into a doubly-quoted command. Strings containing % keep the shell layer so
        // target-side environment variables can still expand.
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteLocalElevatedAsync(
                host, username, password, plan.Command, ct,
                RequiresCommandShell(plan.Command) ? CommandShell.Cmd : CommandShell.Direct)
            : await _execution.ExecuteOnceAsync(
                host, username, password, plan.Command, RemoteOperationKind.Command,
                RequiresCommandShell(plan.Command) ? CommandShell.Cmd : CommandShell.Direct,
                wrapCmd: RequiresCommandShell(plan.Command), ct: ct);
        var success = IsSuccessfulUninstallResult(result);
        if (success)
            _log.Info($"静默卸载完成: {software.DisplayName} (exit code: {result.ExitCode})");
        else
            _log.Warn($"静默卸载失败 (exit code: {result.ExitCode}): {FormatCommandFailure(result)}");
        return success;
    }

    private static SilentUninstallPlan BuildSilentUninstallCommand(SoftwareInfo software)
    {
        if (!string.IsNullOrWhiteSpace(software.QuietUninstallString))
            return new SilentUninstallPlan(software.QuietUninstallString.Trim(), "使用注册表 QuietUninstallString");

        var uninstallString = software.UninstallString.Trim();
        if (string.IsNullOrWhiteSpace(uninstallString))
            return SilentUninstallPlan.Empty;

        if (TryBuildMsiSilentCommand(uninstallString, software.RegistryKey, out var msiCommand))
            return new SilentUninstallPlan(msiCommand, "识别为 MSI，使用 /x /qn /norestart");

        if (HasKnownSilentSwitch(uninstallString))
            return new SilentUninstallPlan(uninstallString, "原卸载命令已包含静默参数");

        var args = ProcessHelper.SplitCommandLine(uninstallString);
        if (args.Length == 0)
            return new SilentUninstallPlan(uninstallString, "无法解析命令，按原命令执行");

        var exeName = Path.GetFileName(args[0]).ToLowerInvariant();
        var lowerCommand = uninstallString.ToLowerInvariant();

        if (exeName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return new SilentUninstallPlan(
                $"msiexec.exe /x {QuoteCommandArgument(args[0])} /qn /norestart",
                "识别为 MSI 文件，使用 msiexec /x /qn /norestart");

        if (exeName.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            return new SilentUninstallPlan(
                AppendArguments(uninstallString, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"),
                "识别为 Inno Setup 卸载器，追加 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART");

        if (exeName is "uninstall.exe" or "uninst.exe" or "uninstaller.exe")
            return new SilentUninstallPlan(
                AppendArguments(uninstallString, "/S"),
                "识别为常见 EXE 卸载器，追加 /S");

        if (exeName is "setup.exe" or "setup64.exe" &&
            (lowerCommand.Contains("removeonly", StringComparison.OrdinalIgnoreCase) ||
             lowerCommand.Contains("uninstall", StringComparison.OrdinalIgnoreCase)))
            return new SilentUninstallPlan(
                AppendArguments(uninstallString, "/s /v\"/qn /norestart\""),
                "识别为 setup 卸载模式，追加 /s /v\"/qn /norestart\"");

        if (exeName.Equals("update.exe", StringComparison.OrdinalIgnoreCase) &&
            lowerCommand.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            return new SilentUninstallPlan(
                AppendArguments(uninstallString, "--silent"),
                "识别为 Update.exe 卸载模式，追加 --silent");

        return new SilentUninstallPlan(
            AppendArguments(uninstallString, "/quiet /norestart"),
            "未知 EXE 卸载器，尝试通用 /quiet /norestart");
    }

    private static bool TryBuildMsiSilentCommand(string uninstallString, string registryKey, out string command)
    {
        command = string.Empty;
        if (!uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase) &&
            !registryKey.Contains("Installer\\Products", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = ProductCodeRegex.Match(uninstallString);
        if (!match.Success)
            match = ProductCodeRegex.Match(registryKey);

        if (!match.Success)
            return false;

        command = $"msiexec.exe /x {match.Value.ToUpperInvariant()} /qn /norestart";
        return true;
    }

    private static bool HasKnownSilentSwitch(string command)
    {
        var args = ProcessHelper.SplitCommandLine(command);
        return args.Skip(1).Any(arg =>
        {
            var normalized = arg.Trim().Trim('"');
            return KnownSilentSwitches.Any(s => normalized.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
                   normalized.StartsWith("/qn", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string AppendArguments(string command, string arguments)
    {
        var trimmed = command.Trim();
        return trimmed.EndsWith(arguments, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed} {arguments}";
    }

    private static string QuoteCommandArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";

        var needsQuotes = value.Any(char.IsWhiteSpace) || value.Contains('"');
        var escaped = value.Replace("\"", "\\\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private readonly record struct SilentUninstallPlan(string Command, string Description)
    {
        public static SilentUninstallPlan Empty => new(string.Empty, string.Empty);
    }

    public async Task<bool> UninstallInteractiveAsync(string host, string username, string password,
        string uninstallString, int? sessionId = null, CancellationToken ct = default)
    {
        _log.Debug($"交互卸载软件: host={host} session={sessionId?.ToString() ?? "(auto)"} command={uninstallString} method=PsExec user={username}");
        // Launch the registry command directly. Wrapping an already-quoted uninstall
        // path in `cmd /c` introduces a second layer of quotes (visible in the report
        // as `cmd /c "\\\"C:\\Program Files...\\\""`) and is a common reason for
        // an uninstaller process to start with no visible window.
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteInteractiveLocalAsync(
                uninstallString, username, password, ct,
                RequiresCommandShell(uninstallString) ? CommandShell.Cmd : CommandShell.Direct,
                sessionId)
            : await _execution.ExecuteOnceAsync(
                host, username, password, uninstallString, RemoteOperationKind.InteractiveLaunch,
                RequiresCommandShell(uninstallString) ? CommandShell.Cmd : CommandShell.Direct,
                wrapCmd: RequiresCommandShell(uninstallString),
                interactiveSession: true, sessionId: sessionId, ct: ct);
        var success = IsSuccessfulUninstallResult(result);
        if (success) _log.Info($"交互卸载已启动: {uninstallString}");
        else _log.Warn($"交互卸载失败 (exit code: {result.ExitCode}): {FormatCommandFailure(result)}");
        return success;
    }

    private static bool RequiresCommandShell(string command)
    {
        // Direct PsExec arguments are safer for the normal quoted executable form.
        // Keep the cmd layer only when the uninstall string needs target-side
        // environment-variable expansion (for example `%ProgramFiles%\Foo\uninstall.exe`).
        return command.Contains('%');
    }

    private static bool IsSuccessfulUninstallResult(CommandResult result)
    {
        // MSI uses 3010 (reboot required) and 1641 (reboot initiated) for successful
        // uninstall operations. PsExec itself still propagates those child codes.
        return result.Success || result.ExitCode is 1641 or 3010;
    }

    private static string FormatCommandFailure(CommandResult result)
    {
        return string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
    }
}
