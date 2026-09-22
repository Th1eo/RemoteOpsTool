using System.Management;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class EnvVarService : IEnvVarService
{
    private const uint HkeyLocalMachine = 0x80000002;
    private const uint HkeyUsers = 0x80000003;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;

    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public EnvVarService(
        IPsExecService psExec,
        IRemoteExecutionService execution,
        ISettingsService settings,
        ILogService log)
    {
        _psExec = psExec;
        _execution = execution;
        _settings = settings;
        _log = log;
    }

    private bool DebugMode => _settings.Settings.DebugMode;

    private Task<CommandResult> ExecuteRegistryWriteAsync(
        string host,
        string username,
        string password,
        string command,
        CancellationToken ct)
    {
        return HostHelper.IsLocalHost(host)
            ? _psExec.ExecuteLocalElevatedAsync(
                host, username, password, command, ct, CommandShell.Cmd)
            : _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.RegistryWrite, ct: ct);
    }

    public async Task<List<string>> GetLoggedOnUsersAsync(string host, string username, string password, CancellationToken ct = default)
    {
        _log.Debug($"获取登录用户: host={host} method=WMI explorer owner user={username}");
        var wmiUsers = await TryGetLoggedOnUsersViaWmiAsync(host, username, password, ct);
        if (wmiUsers.Count > 0)
        {
            _log.Debug($"WMI 登录用户完成: host={host} users={string.Join(", ", wmiUsers)}");
            return wmiUsers;
        }

        var users = new List<string>();

        if (HostHelper.IsLocalHost(host))
        {
            var result = await ProcessHelper.RunAsync("query", "user", ct);
            if (DebugMode) _log.Info($"[DEBUG] query user (local) exit={result.ExitCode} stdout={result.StdOut}");
            if (!string.IsNullOrWhiteSpace(result.StdOut))
                ParseQueryUserOutput(result.StdOut, users);
            if (DebugMode) _log.Info($"[DEBUG] 解析到 {users.Count} 个本地用户: [{string.Join(", ", users)}]");
            _log.Info($"本地登录用户查询完成: count={users.Count}");
            return users;
        }

        try
        {
            var queryResult = await ProcessHelper.RunAsync("query", $"user /server:{host}", ct);
            if (DebugMode) _log.Info($"[DEBUG] query user /server exit={queryResult.ExitCode} stdout={queryResult.StdOut}");
            if (!string.IsNullOrWhiteSpace(queryResult.StdOut))
            {
                ParseQueryUserOutput(queryResult.StdOut, users);
                if (users.Count > 0) return users;
            }
        }
        catch { }

        _log.Info("query user /server failed, trying PsExec fallback for logged-on users...");

        try
        {
            var psResult = await _execution.ExecuteOnceAsync(
                host, username, password, "query user", RemoteOperationKind.Inventory, ct: ct);
            if (!string.IsNullOrWhiteSpace(psResult.StdOut))
                ParseQueryUserOutput(psResult.StdOut, users);
            _log.Info($"PsExec 登录用户查询完成: count={users.Count}");
        }
        catch { }

        return users;
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

    private static void ParseQueryUserOutput(string output, List<string> users)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var headerSkipped = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }

            if (trimmed.StartsWith(">"))
                trimmed = trimmed[1..].Trim();

            var parts = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var username = parts[0];
            if (username.Contains('\\')) continue;
            if (username.StartsWith("NT ", StringComparison.OrdinalIgnoreCase)) continue;
            if (username.Length == 0) continue;

            if (seen.Add(username))
                users.Add(username);
        }
    }

    public async Task<List<EnvVariableInfo>> GetVariablesAsync(string host, string username, string password,
        string target, CancellationToken ct = default)
    {
        _log.Debug($"获取环境变量: host={host} target={target} method=WMI StdRegProv user={username}");
        if (target == "Machine")
        {
            var machineVars = await TryGetRegistryVariablesViaWmiAsync(host, username, password,
                HkeyLocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Machine", ct);
            if (machineVars.Count > 0)
                return machineVars;

            var regCmd = "reg query \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\"";
            return await ParseRegistryVariablesInternal(host, username, password, regCmd, "Machine", ct);
        }

        // target is the username from query user
        var sid = await ResolveSidFromRegistry(host, username, password, target, ct);
        if (string.IsNullOrEmpty(sid)) return [];

        var userVars = await TryGetRegistryVariablesViaWmiAsync(host, username, password,
            HkeyUsers, $@"{sid}\Environment", target, ct);
        if (userVars.Count > 0)
            return userVars;

        var userRegCmd = $"reg query \"HKU\\{sid}\\Environment\"";
        return await ParseRegistryVariablesInternal(host, username, password, userRegCmd, target, ct);
    }

    public async Task<bool> SetVariableAsync(string host, string username, string password,
        string name, string value, string target, CancellationToken ct = default)
    {
        _log.Debug($"设置环境变量: host={host} target={target} name={name} method=WMI StdRegProv user={username}");
        if (target == "Machine")
        {
            var wmiSet = await TrySetRegistryValueViaWmiAsync(host, username, password,
                HkeyLocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", name, value, ct);
            if (wmiSet) return true;

            var cmd = $"setx \"{name}\" \"{value}\" /M";
            var r = await ExecuteRegistryWriteAsync(host, username, password, cmd, ct);
            if (r.Success)
                _log.Info($"环境变量已设置: {name} (Machine)");
            else
                _log.Warn($"环境变量设置失败: {name} - {r.StdErr}");
            return r.Success;
        }

        var sid = await ResolveSidFromRegistry(host, username, password, target, ct);
        if (string.IsNullOrEmpty(sid)) return false;

        var wmiUserSet = await TrySetRegistryValueViaWmiAsync(host, username, password,
            HkeyUsers, $@"{sid}\Environment", name, value, ct);
        if (wmiUserSet) return true;

        var regCmd = $"reg add \"HKU\\{sid}\\Environment\" /v \"{name}\" /t REG_EXPAND_SZ /d \"{value}\" /f";
        var result = await ExecuteRegistryWriteAsync(host, username, password, regCmd, ct);
        if (result.Success)
            _log.Info($"环境变量已设置: {name} (User)");
        else
            _log.Warn($"环境变量设置失败: {name} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> DeleteVariableAsync(string host, string username, string password,
        string name, string target, CancellationToken ct = default)
    {
        _log.Debug($"删除环境变量: host={host} target={target} name={name} method=WMI StdRegProv user={username}");
        if (target == "Machine")
        {
            var wmiDeleted = await TryDeleteRegistryValueViaWmiAsync(host, username, password,
                HkeyLocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", name, ct);
            if (wmiDeleted) return true;

            var cmd = $"reg delete \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\" /v \"{name}\" /f";
            var r = await ExecuteRegistryWriteAsync(host, username, password, cmd, ct);
            if (r.Success)
                _log.Info($"环境变量已删除: {name} (Machine)");
            else
                _log.Warn($"环境变量删除失败: {name} - {r.StdErr}");
            return r.Success;
        }

        var sid = await ResolveSidFromRegistry(host, username, password, target, ct);
        if (string.IsNullOrEmpty(sid)) return false;

        var wmiUserDeleted = await TryDeleteRegistryValueViaWmiAsync(host, username, password,
            HkeyUsers, $@"{sid}\Environment", name, ct);
        if (wmiUserDeleted) return true;

        var regCmd = $"reg delete \"HKU\\{sid}\\Environment\" /v \"{name}\" /f";
        var result = await ExecuteRegistryWriteAsync(host, username, password, regCmd, ct);
        if (result.Success)
            _log.Info($"环境变量已删除: {name} (User)");
        else
            _log.Warn($"环境变量删除失败: {name} - {result.StdErr}");
        return result.Success;
    }

    private async Task<string> ResolveSidFromRegistry(string host, string adminUser, string adminPwd,
        string fullUsername, CancellationToken ct)
    {
        var name = fullUsername.Contains('\\') ? fullUsername.Split('\\')[^1] : fullUsername;
        _log.Debug($"解析用户 SID: host={host} targetUser={fullUsername} method=WMI explorer owner");

        // Primary: use WMI to get the explorer.exe owner SID directly
        var wmiSid = await ResolveSidViaWmi(host, adminUser, adminPwd, fullUsername, ct);
        if (!string.IsNullOrEmpty(wmiSid))
        {
            _log.Info($"SID found via WMI for {name}: {wmiSid}");
            return wmiSid;
        }

        // Fallback: enumerate HKU keys
        _log.Info($"WMI SID lookup incomplete for {name}, falling back to HKU enumeration...");
        var regCmd = "reg query \"HKU\"";
        var result = await _execution.ExecuteOnceAsync(
            host, adminUser, adminPwd, regCmd, RemoteOperationKind.RegistryRead, ct: ct);
        if (!result.Success) { _log.Warn($"HKU query failed: {result.StdErr}"); return ""; }

        var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var candidateSids = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("HKEY_USERS\\S-1-", StringComparison.OrdinalIgnoreCase)) continue;
            var sid = trimmed["HKEY_USERS\\".Length..].Trim();
            if (sid.Length >= 30)
                candidateSids.Add(sid);
        }

        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(TimeSpan.FromSeconds(15));

        var session = await _execution.CreateSessionAsync(host, adminUser, adminPwd, ct);
        foreach (var sid in candidateSids)
        {
            ct.ThrowIfCancellationRequested();
            var checkCmd = $"reg query \"HKU\\{sid}\\Volatile Environment\" /v USERNAME 2>nul";
            var check = (await session.ExecuteAsync(
                RemoteOperationKind.RegistryRead,
                NewRemoteCommand(host, adminUser, adminPwd, checkCmd),
                ct: batchCts.Token)).Result;
            if (check.Success && check.StdOut.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"SID found via HKU for {name}: {sid}");
                return sid;
            }
        }
        _log.Warn($"SID not found for user: {name}");
        return "";
    }

    private async Task<string> ResolveSidViaWmi(string host, string adminUser, string adminPwd,
        string targetUser, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var targetName = targetUser.Contains('\\') ? targetUser.Split('\\')[^1] : targetUser;
                var scope = RemoteWmiHelper.CreateScope(host, adminUser, adminPwd);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM Win32_Process WHERE Name='explorer.exe'"));
                foreach (ManagementObject process in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var owner = process.InvokeMethod("GetOwner", null, null);
                    var user = owner?["User"]?.ToString() ?? string.Empty;
                    if (!user.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sidResult = process.InvokeMethod("GetOwnerSid", null, null);
                    var sid = sidResult?["Sid"]?.ToString() ?? string.Empty;
                    if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) && sid.Length > 20)
                        return sid;
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI SID 解析失败: {host} target={targetUser} - {ex.Message}");
            }
            return string.Empty;
        }, ct);
    }

    private async Task<List<string>> TryGetLoggedOnUsersViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var users = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM Win32_Process WHERE Name='explorer.exe'"));
                foreach (ManagementObject process in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var owner = process.InvokeMethod("GetOwner", null, null);
                    var user = owner?["User"]?.ToString() ?? string.Empty;
                    var domain = owner?["Domain"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(user)) continue;

                    users.Add(string.IsNullOrWhiteSpace(domain) ? user : $@"{domain}\{user}");
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 登录用户查询失败: {host} - {ex.Message}");
                return [];
            }
            return users.ToList();
        }, ct);
    }

    private async Task<List<EnvVariableInfo>> TryGetRegistryVariablesViaWmiAsync(
        string host,
        string username,
        string password,
        uint hive,
        string subKey,
        string target,
        CancellationToken ct)
    {
        try
        {
            var (names, types) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password, @"root\default");
                scope.Connect();

                using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                return EnumRegistryValues(registry, hive, subKey);
            }, ct);

            var rawValues = await RemoteRegistryBatchReader.ReadValuesAsync(
                host, username, password, hive, subKey, names, types, ct);

            var variables = new List<EnvVariableInfo>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                variables.Add(new EnvVariableInfo
                {
                    Name = names[i],
                    Value = rawValues[i],
                    Target = target
                });
            }

            return variables;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 注册表变量查询失败: {host} - {ex.Message}");
            return [];
        }
    }

    private static (string[] Names, uint[] Types) EnumRegistryValues(
        ManagementClass registry,
        uint hive,
        string subKey)
    {
        using var inParams = registry.GetMethodParameters("EnumValues");
        inParams["hDefKey"] = hive;
        inParams["sSubKeyName"] = subKey;

        using var outParams = registry.InvokeMethod("EnumValues", inParams, null);
        if (RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") != 0)
            return ([], []);

        var names = outParams["sNames"] as string[] ?? [];
        var types = outParams["Types"] as uint[] ?? [];
        return (names, types);
    }


    private async Task<bool> TrySetRegistryValueViaWmiAsync(
        string host,
        string username,
        string password,
        uint hive,
        string subKey,
        string name,
        string value,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password, @"root\default");
                scope.Connect();

                using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                using var inParams = registry.GetMethodParameters("SetExpandedStringValue");
                inParams["hDefKey"] = hive;
                inParams["sSubKeyName"] = subKey;
                inParams["sValueName"] = name;
                inParams["sValue"] = value;

                using var outParams = registry.InvokeMethod("SetExpandedStringValue", inParams, null);
                return RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") == 0;
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 设置注册表值失败: {host} name={name} - {ex.Message}");
                return false;
            }
        }, ct);
    }

    private async Task<bool> TryDeleteRegistryValueViaWmiAsync(
        string host,
        string username,
        string password,
        uint hive,
        string subKey,
        string name,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password, @"root\default");
                scope.Connect();

                using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                using var inParams = registry.GetMethodParameters("DeleteValue");
                inParams["hDefKey"] = hive;
                inParams["sSubKeyName"] = subKey;
                inParams["sValueName"] = name;

                using var outParams = registry.InvokeMethod("DeleteValue", inParams, null);
                return RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") == 0;
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 删除注册表值失败: {host} name={name} - {ex.Message}");
                return false;
            }
        }, ct);
    }

    private async Task<List<EnvVariableInfo>> ParseRegistryVariablesInternal(string host, string username,
        string password, string regCmd, string target, CancellationToken ct)
    {
        var result = await _execution.ExecuteOnceAsync(
            host, username, password, regCmd, RemoteOperationKind.RegistryRead, ct: ct);
        if (!result.Success)
        {
            if (result.ExitCode == 1) return [];
            _log.Warn($"reg query failed: {result.StdErr}");
            return [];
        }

        var variables = new List<EnvVariableInfo>();
        var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)) continue;

            var idx = trimmed.IndexOf("    ", StringComparison.Ordinal);
            if (idx < 0) continue;
            var name = trimmed[..idx].Trim();
            var rest = trimmed[(idx + 4)..].Trim();
            idx = rest.IndexOf("    ", StringComparison.Ordinal);
            if (idx < 0) continue;
            var value = rest[(idx + 4)..].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            variables.Add(new EnvVariableInfo { Name = name, Value = value, Target = target });
        }
        return variables;
    }
}
