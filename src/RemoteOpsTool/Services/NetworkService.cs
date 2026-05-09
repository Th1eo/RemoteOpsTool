using System.Management;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class NetworkService : INetworkService
{
    private readonly IPsExecService _psExec;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public NetworkService(IPsExecService psExec, ISettingsService settings, ILogService log)
    {
        _psExec = psExec;
        _settings = settings;
        _log = log;
    }

    private bool DebugMode => _settings.Settings.DebugMode;

    public async Task<PingResult> PingAsync(string host, CancellationToken ct = default)
    {
        try
        {
            var pingTask = Task.Run(async () =>
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                return await ping.SendPingAsync(host, 3000, [], new System.Net.NetworkInformation.PingOptions());
            });

            var winner = await Task.WhenAny(pingTask, Task.Delay(4000, ct));
            ct.ThrowIfCancellationRequested();

            if (winner != pingTask)
                return new PingResult(false, "Ping timeout (>4s)");

            var reply = await pingTask;
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                var ms = reply.RoundtripTime;
                return new PingResult(true, $"Reply from {reply.Address}: time={ms}ms", ms);
            }
            return new PingResult(false, $"Ping failed: {reply.Status}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PingResult(false, ex.Message);
        }
    }

    public async Task FlushDnsAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var r = await _psExec.ExecuteAsync(host, username, password, "ipconfig /flushdns", ct: ct);
        if (r.Success) _log.Info($"DNS 缓存已刷新: {host}"); else _log.Error($"DNS 刷新失败: {r.StdErr}");
    }

    public async Task RefreshIpAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var rel = await _psExec.ExecuteAsync(host, username, password, "ipconfig /release", ct: ct);
        _log.Info(rel.Success ? "IP 已释放." : $"释放失败: {rel.StdErr}");
        var ren = await _psExec.ExecuteAsync(host, username, password, "ipconfig /renew", ct: ct);
        if (ren.Success) _log.Info($"IP 已更新: {host}"); else _log.Error($"更新失败: {ren.StdErr}");
    }

    public async Task<List<NetworkConnectionInfo>> GetActiveConnectionsAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(host))
            return await Task.Run(() => GetLocalConnectionsAsync(ct), ct);

        try
        {
            var psCmd = "powershell \"tasklist /fo csv /nh; Write-Output '---SPLITTER---'; netstat -ano\"";
            var result = await _psExec.ExecuteAsync(host, username, password, psCmd, silent: true, ct: ct);
            if (!result.Success) return [];

            var parts = result.StdOut.Split("---SPLITTER---", 2, StringSplitOptions.RemoveEmptyEntries);
            var taskListOutput = parts.Length > 0 ? parts[0] : "";
            var netstatOutput = parts.Length > 1 ? parts[1] : "";

            var pidNames = ParseTaskListOutput(taskListOutput);
            return ParseNetstatOutput(netstatOutput, pidNames);
        }
        catch { return []; }
    }

    private static Dictionary<int, string> ParseTaskListOutput(string output)
    {
        var dict = new Dictionary<int, string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[1].Trim('"'), out var pid))
                dict[pid] = parts[0].Trim('"');
        }
        return dict;
    }

    private static async Task<List<NetworkConnectionInfo>> GetLocalConnectionsAsync(CancellationToken ct)
    {
        try
        {
            var netstatResult = await ProcessHelper.RunAsync("netstat.exe", "-ano", ct);
            var output = netstatResult.StdOut;

            var pidNames = await GetLocalPidNamesAsync(ct);

            return ParseNetstatOutput(output, pidNames);
        }
        catch { return []; }
    }

    private static async Task<Dictionary<int, string>> GetLocalPidNamesAsync(CancellationToken ct)
    {
        var dict = new Dictionary<int, string>();
        try
        {
            var result = await ProcessHelper.RunAsync("cmd.exe", "/c tasklist /fo csv /nh", ct);
            foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[1].Trim('"'), out var pid))
                    dict[pid] = parts[0].Trim('"');
            }
        }
        catch { }
        return dict;
    }

    public async Task<List<ProcessDetailInfo>> GetProcessListAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        var wmiProcesses = await TryGetProcessListViaWmiAsync(host, username, password, ct);
        if (wmiProcesses.Count > 0)
            return wmiProcesses;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var psCmd = "powershell \"Get-CimInstance Win32_Process | Select-Object Name,ProcessId,SessionId,@{N='MemMB';E={[math]::Round($_.WorkingSetSize/1MB,1)}} | ConvertTo-Csv -NoTypeInformation\"";
            var result = await _psExec.ExecuteAsync(host, username, password, psCmd, ct: linkedCts.Token, silent: true);

            if (DebugMode)
                _log.Info($"[DEBUG] GetProcessList remote exit={result.ExitCode} outlen={result.StdOut?.Length ?? 0}");

            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                _log.Warn($"获取进程列表失败 (exit={result.ExitCode}): {result.StdErr}");
                return [];
            }

            return ParseProcessCsv(result.StdOut);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("获取进程列表超时");
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"获取进程列表异常: {ex.Message}");
            return [];
        }
    }

    private static List<ProcessDetailInfo> ParseProcessCsv(string output)
    {
        var results = new List<ProcessDetailInfo>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

            var parts = ParseCsvLine(line);
            if (parts.Length < 4) continue;

            if (!int.TryParse(parts[1], out var pid) || pid <= 0) continue;

            results.Add(new ProcessDetailInfo
            {
                ProcessName = parts[0],
                ProcessId = pid,
                SessionName = parts[2],
                MemoryMB = parts.Length > 3 ? parts[3] : ""
            });
        }
        return results;
    }

    private static string[] ParseCsvLine(string line)
    {
        var parts = new List<string>();
        var inQuotes = false;
        var current = "";
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }
        parts.Add(current);
        return parts.ToArray();
    }

    private static List<NetworkConnectionInfo> ParseNetstatOutput(string output, Dictionary<int, string> pidNames)
    {
        var connections = new List<NetworkConnectionInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(4))
        {
            var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[^1], out var pid)) continue;
            pidNames.TryGetValue(pid, out var pn);
            connections.Add(new NetworkConnectionInfo
            {
                Protocol = parts[0],
                LocalAddress = parts[1],
                RemoteAddress = parts[2],
                State = parts.Length >= 5 ? parts[3] : "",
                ProcessId = pid,
                ProcessName = pn ?? ""
            });
        }
        return connections;
    }

    public async Task<bool> KillProcessAsync(string host, string username, string password,
        int processId, bool killTree, CancellationToken ct = default)
    {
        var wmiKilled = await TryKillProcessViaWmiAsync(host, username, password, processId, killTree, ct);
        if (wmiKilled)
        {
            _log.Info($"已通过 WMI/DCOM 终止进程 PID={processId}" + (killTree ? " (含子进程)" : ""));
            return true;
        }

        var treeFlag = killTree ? " /t" : "";
        var cmd = $"taskkill /pid {processId} /f{treeFlag}";
        var r = await _psExec.ExecuteAsync(host, username, password, cmd, silent: true, ct: ct);
        if (r.Success)
            _log.Info($"已终止进程 PID={processId}" + (killTree ? " (含子进程)" : ""));
        else
            _log.Warn($"终止进程 PID={processId} 失败: {r.StdErr}");
        return r.Success;
    }

    private static async Task<List<ProcessDetailInfo>> TryGetProcessListViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var processes = new List<ProcessDetailInfo>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Name,ProcessId,SessionId,WorkingSetSize FROM Win32_Process"));
                foreach (ManagementObject process in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var pid = (int)RemoteWmiHelper.GetUInt32(process, "ProcessId");
                    if (pid <= 0) continue;

                    var workingSet = RemoteWmiHelper.GetUInt64(process, "WorkingSetSize");
                    var sessionId = RemoteWmiHelper.GetUInt32(process, "SessionId");
                    processes.Add(new ProcessDetailInfo
                    {
                        ProcessName = RemoteWmiHelper.GetString(process, "Name"),
                        ProcessId = pid,
                        SessionName = $"Session {sessionId}",
                        MemoryMB = Math.Round(workingSet / 1048576.0, 1).ToString("F1"),
                        Status = "Running"
                    });
                }
            }
            catch
            {
                return [];
            }
            return processes;
        }, ct);
    }

    private static async Task<bool> TryKillProcessViaWmiAsync(
        string host,
        string username,
        string password,
        int processId,
        bool killTree,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                var processIds = killTree
                    ? BuildProcessTree(scope, processId, ct)
                    : new List<int> { processId };

                var anyKilled = false;
                foreach (var pid in processIds.AsEnumerable().Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    using var searcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery($"SELECT * FROM Win32_Process WHERE ProcessId={pid}"));
                    foreach (ManagementObject process in searcher.Get())
                    {
                        var result = process.InvokeMethod("Terminate", null, null);
                        anyKilled |= RemoteWmiHelper.IsSuccessReturn(result);
                    }
                }

                return anyKilled;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    private static List<int> BuildProcessTree(ManagementScope scope, int rootProcessId, CancellationToken ct)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT ProcessId,ParentProcessId FROM Win32_Process"));
        foreach (ManagementObject process in searcher.Get())
        {
            ct.ThrowIfCancellationRequested();
            var pid = (int)RemoteWmiHelper.GetUInt32(process, "ProcessId");
            var parentPid = (int)RemoteWmiHelper.GetUInt32(process, "ParentProcessId");
            if (!childrenByParent.TryGetValue(parentPid, out var children))
            {
                children = [];
                childrenByParent[parentPid] = children;
            }
            children.Add(pid);
        }

        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(rootProcessId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            result.Add(current);
            if (!childrenByParent.TryGetValue(current, out var children)) continue;
            foreach (var child in children)
                stack.Push(child);
        }

        return result;
    }

    public async Task<List<UserSessionInfo>> GetUserSessionsAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        var sessions = new List<UserSessionInfo>();

        if (HostHelper.IsLocalHost(host))
        {
            var result = await ProcessHelper.RunAsync("query", "user", ct);
            if (!string.IsNullOrWhiteSpace(result.StdOut))
                ParseSessionOutput(result.StdOut, sessions);
            return sessions;
        }

        try
        {
            var queryResult = await ProcessHelper.RunAsync("query", $"user /server:{host}", ct);
            if (!string.IsNullOrWhiteSpace(queryResult.StdOut))
            {
                ParseSessionOutput(queryResult.StdOut, sessions);
                if (sessions.Count > 0) return sessions;
            }
        }
        catch { }

        var psResult = await _psExec.ExecuteAsync(host, username, password,
            "query user", silent: true, ct: ct);
        if (!string.IsNullOrWhiteSpace(psResult.StdOut))
            ParseSessionOutput(psResult.StdOut, sessions);

        return sessions;
    }

    private static void ParseSessionOutput(string output, List<UserSessionInfo> sessions)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var headerSkipped = false;
        foreach (var line in lines)
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
            if (line.StartsWith(" USERNAME", StringComparison.OrdinalIgnoreCase)) continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith(">"))
                trimmed = trimmed[1..].Trim();

            var parts = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var user = parts[0];
            if (user.Contains('\\')) continue;
            if (user.StartsWith("NT ", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(parts[2], out var sessionId)) continue;

            sessions.Add(new UserSessionInfo
            {
                Username = user,
                SessionName = parts.Length > 1 ? parts[1] : "",
                SessionId = sessionId,
                State = parts.Length > 3 ? parts[3] : "",
                IdleTime = parts.Length > 4 ? parts[4] : "",
                LogonTime = parts.Length > 5 ? string.Join(" ", parts.Skip(5)) : ""
            });
        }
    }

    public async Task<bool> SignOutUserAsync(string host, string username, string password,
        int sessionId, CancellationToken ct = default)
    {
        var cmd = $"logoff {sessionId}";
        var r = await _psExec.ExecuteAsync(host, username, password, cmd, silent: true, ct: ct);
        if (r.Success)
            _log.Info($"已注销会话 ID={sessionId}");
        else
            _log.Warn($"注销会话 ID={sessionId} 失败: {r.StdErr}");
        return r.Success;
    }
}
