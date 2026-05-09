using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class EnvVarService : IEnvVarService
{
    private readonly IPsExecService _psExec;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public EnvVarService(IPsExecService psExec, ISettingsService settings, ILogService log)
    {
        _psExec = psExec;
        _settings = settings;
        _log = log;
    }

    private bool DebugMode => _settings.Settings.DebugMode;

    public async Task<List<string>> GetLoggedOnUsersAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var users = new List<string>();

        if (HostHelper.IsLocalHost(host))
        {
            var result = await ProcessHelper.RunAsync("query", "user", ct);
            if (DebugMode) _log.Info($"[DEBUG] query user (local) exit={result.ExitCode} stdout={result.StdOut}");
            if (!string.IsNullOrWhiteSpace(result.StdOut))
                ParseQueryUserOutput(result.StdOut, users);
            if (DebugMode) _log.Info($"[DEBUG] 解析到 {users.Count} 个本地用户: [{string.Join(", ", users)}]");
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
            var psResult = await _psExec.ExecuteAsync(host, username, password, "query user", silent: true, ct: ct);
            if (!string.IsNullOrWhiteSpace(psResult.StdOut))
                ParseQueryUserOutput(psResult.StdOut, users);
        }
        catch { }

        return users;
    }

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
        if (target == "Machine")
        {
            var regCmd = "reg query \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\"";
            return await ParseRegistryVariablesInternal(host, username, password, regCmd, "Machine", ct);
        }

        // target is the username from query user
        var sid = await ResolveSidFromRegistry(host, username, password, target, ct);
        if (string.IsNullOrEmpty(sid)) return [];

        var userRegCmd = $"reg query \"HKU\\{sid}\\Environment\"";
        return await ParseRegistryVariablesInternal(host, username, password, userRegCmd, target, ct);
    }

    public async Task<bool> SetVariableAsync(string host, string username, string password,
        string name, string value, string target, CancellationToken ct = default)
    {
        if (target == "Machine")
        {
            var cmd = $"setx \"{name}\" \"{value}\" /M";
            var r = await _psExec.ExecuteAsync(host, username, password, cmd, ct: ct);
            return r.Success;
        }

        var sid = await ResolveSidFromRegistry(host, username, password, target, ct);
        if (string.IsNullOrEmpty(sid)) return false;

        var regCmd = $"reg add \"HKU\\{sid}\\Environment\" /v \"{name}\" /t REG_EXPAND_SZ /d \"{value}\" /f";
        var result = await _psExec.ExecuteAsync(host, username, password, regCmd, ct: ct);
        return result.Success;
    }

    public async Task<bool> DeleteVariableAsync(string host, string username, string password,
        string name, string target, CancellationToken ct = default)
    {
        if (target == "Machine")
        {
            var cmd = $"reg delete \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\" /v \"{name}\" /f";
            var r = await _psExec.ExecuteAsync(host, username, password, cmd, ct: ct);
            return r.Success;
        }

        var sid = await ResolveSidFromRegistry(host, username, password, target, ct);
        if (string.IsNullOrEmpty(sid)) return false;

        var regCmd = $"reg delete \"HKU\\{sid}\\Environment\" /v \"{name}\" /f";
        var result = await _psExec.ExecuteAsync(host, username, password, regCmd, ct: ct);
        return result.Success;
    }

    private async Task<string> ResolveSidFromRegistry(string host, string adminUser, string adminPwd,
        string fullUsername, CancellationToken ct)
    {
        var name = fullUsername.Contains('\\') ? fullUsername.Split('\\')[^1] : fullUsername;

        // Primary: use WMI to get the explorer.exe owner SID directly
        var wmiSid = await ResolveSidViaWmi(host, adminUser, adminPwd, ct);
        if (!string.IsNullOrEmpty(wmiSid))
        {
            // Verify this SID belongs to our target user
            var checkCmd = $"reg query \"HKU\\{wmiSid}\\Volatile Environment\" /v USERNAME 2>nul";
            var check = await _psExec.ExecuteAsync(host, adminUser, adminPwd, checkCmd, ct: ct);
            if (check.Success && check.StdOut.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"SID found via WMI for {name}: {wmiSid}");
                return wmiSid;
            }
        }

        // Fallback: enumerate HKU keys
        _log.Info($"WMI SID lookup incomplete for {name}, falling back to HKU enumeration...");
        var regCmd = "reg query \"HKU\"";
        var result = await _psExec.ExecuteAsync(host, adminUser, adminPwd, regCmd, ct: ct);
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

        var checkTasks = candidateSids.Select(async sid =>
        {
            var checkCmd = $"reg query \"HKU\\{sid}\\Volatile Environment\" /v USERNAME 2>nul";
            var check = await _psExec.ExecuteAsync(host, adminUser, adminPwd, checkCmd, ct: batchCts.Token);
            if (!check.Success) return (sid, false);
            return (sid, check.StdOut.Contains(name, StringComparison.OrdinalIgnoreCase));
        });

        var results = await Task.WhenAll(checkTasks);
        foreach (var (sid, matched) in results)
        {
            if (matched)
            {
                _log.Info($"SID found via HKU for {name}: {sid}");
                return sid;
            }
        }
        _log.Warn($"SID not found for user: {name}");
        return "";
    }

    private async Task<string> ResolveSidViaWmi(string host, string adminUser, string adminPwd, CancellationToken ct)
    {
        try
        {
            var psCmd = "powershell -NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'Name=''explorer.exe''' | Select-Object -First 1).GetOwnerSid().Sid\"";
            var result = await _psExec.ExecuteAsync(host, adminUser, adminPwd, psCmd, ct: ct);
            if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                var sid = result.StdOut.Trim();
                if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) && sid.Length > 20)
                    return sid;
            }
        }
        catch { }
        return "";
    }

    private async Task<List<EnvVariableInfo>> ParseRegistryVariablesInternal(string host, string username,
        string password, string regCmd, string target, CancellationToken ct)
    {
        var result = await _psExec.ExecuteAsync(host, username, password, regCmd, ct: ct);
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
