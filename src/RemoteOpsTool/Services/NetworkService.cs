using System.Management;
using System.Text.RegularExpressions;
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
    private readonly Dictionary<string, List<RemoteCapabilityInfo>> _capabilityCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PingResult> PingAsync(string host, CancellationToken ct = default)
    {
        if (DebugMode) _log.Debug($"Ping 开始: host={host}");
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
            {
                if (DebugMode) _log.Debug($"Ping 超时: host={host}");
                return new PingResult(false, "Ping timeout (>4s)");
            }

            var reply = await pingTask;
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                var ms = reply.RoundtripTime;
                if (DebugMode) _log.Debug($"Ping 成功: host={host} address={reply.Address} time={ms}ms");
                return new PingResult(true, $"Reply from {reply.Address}: time={ms}ms", ms);
            }
            if (DebugMode) _log.Debug($"Ping 失败: host={host} status={reply.Status}");
            return new PingResult(false, $"Ping failed: {reply.Status}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (DebugMode) _log.Debug($"Ping 异常: host={host} error={ex}");
            return new PingResult(false, ex.Message);
        }
    }

    public async Task<List<RemoteCapabilityInfo>> ProbeCapabilitiesAsync(
        string host,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var results = new List<RemoteCapabilityInfo>();

        var ping = await PingAsync(host, ct);
        results.Add(new RemoteCapabilityInfo
        {
            Name = "Ping",
            Success = ping.Success,
            Detail = ping.Success ? $"{ping.RoundtripTime}ms" : ping.Output
        });

        results.Add(await ProbeTcpPortAsync(host, 445, "SMB 445", ct));
        results.Add(await ProbeTcpPortAsync(host, 135, "RPC 135", ct));
        results.Add(await ProbeTcpPortAsync(host, 5985, "WinRM 5985", ct));

        results.Add(await ProbeAdminShareAsync(host, username, password, ct));
        results.Add(await ProbeWmiAsync(host, username, password, ct));
        results.Add(await ProbeQuerySessionAsync(host, ct));
        results.Add(await ProbePsExecAsync(host, username, password, ct));

        lock (_capabilityCache)
            _capabilityCache[host] = results;

        return results;
    }

    private static async Task<RemoteCapabilityInfo> ProbeTcpPortAsync(
        string host,
        int port,
        string name,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return new RemoteCapabilityInfo { Name = name, Success = true, Detail = "端口可连接" };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RemoteCapabilityInfo { Name = name, Success = false, Detail = "连接超时" };
        }
        catch (Exception ex)
        {
            return new RemoteCapabilityInfo { Name = name, Success = false, Detail = ex.Message };
        }
    }

    private static async Task<RemoteCapabilityInfo> ProbeAdminShareAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var share = $@"\\{host}\ADMIN$";
                var connection = NetworkShareCredentialHelper.EnsureConnection(share, username, password);
                if (!connection.Success)
                {
                    return new RemoteCapabilityInfo
                    {
                        Name = "ADMIN$ 管理共享",
                        Success = false,
                        Detail = connection.Message
                    };
                }

                return new RemoteCapabilityInfo
                {
                    Name = "ADMIN$ 管理共享",
                    Success = Directory.Exists(share),
                    Detail = Directory.Exists(share) ? "共享可访问" : "凭据会话已建立，但共享不可枚举"
                };
            }
            catch (Exception ex)
            {
                return new RemoteCapabilityInfo { Name = "ADMIN$ 管理共享", Success = false, Detail = ex.Message };
            }
        }, ct);
    }

    private static async Task<RemoteCapabilityInfo> ProbeWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Name FROM Win32_OperatingSystem"));
                var hasResult = searcher.Get().OfType<ManagementObject>().Any();
                return new RemoteCapabilityInfo
                {
                    Name = "WMI/DCOM",
                    Success = hasResult,
                    Detail = hasResult ? "root\\cimv2 可查询" : "连接成功但无返回"
                };
            }
            catch (Exception ex)
            {
                return new RemoteCapabilityInfo { Name = "WMI/DCOM", Success = false, Detail = ex.Message };
            }
        }, ct);
    }

    private static async Task<RemoteCapabilityInfo> ProbeQuerySessionAsync(
        string host,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var result = await ProcessHelper.RunAsync("query", $"user /server:{host}", timeout.Token);
            return new RemoteCapabilityInfo
            {
                Name = "会话查询",
                Success = result.Success && !string.IsNullOrWhiteSpace(result.StdOut),
                Detail = result.Success ? "query user /server 可用" : RemoteErrorClassifier.Explain(FirstNonEmpty(result.StdErr, result.StdOut, "无输出"), result.ExitCode)
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RemoteCapabilityInfo { Name = "会话查询", Success = false, Detail = "查询超时" };
        }
        catch (Exception ex)
        {
            return new RemoteCapabilityInfo { Name = "会话查询", Success = false, Detail = ex.Message };
        }
    }

    private async Task<RemoteCapabilityInfo> ProbePsExecAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _psExec.ExecuteAsync(host, username, password, "whoami", ct: timeout.Token, silent: true);
            return new RemoteCapabilityInfo
            {
                Name = "PsExec 临时执行",
                Success = result.Success,
                Detail = result.Success ? result.StdOut.Trim() : RemoteErrorClassifier.Explain(FirstNonEmpty(result.StdErr, result.StdOut, $"exit={result.ExitCode}"), result.ExitCode)
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RemoteCapabilityInfo { Name = "PsExec 临时执行", Success = false, Detail = "执行超时" };
        }
        catch (Exception ex)
        {
            return new RemoteCapabilityInfo { Name = "PsExec 临时执行", Success = false, Detail = ex.Message };
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return string.Empty;
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
            if (DebugMode) _log.Debug($"获取活动连接: host={host} method=PsExec tasklist+netstat");
            var psCmd = "powershell \"tasklist /fo csv /nh; Write-Output '---SPLITTER---'; netstat -ano\"";
            var result = await _psExec.ExecuteAsync(host, username, password, psCmd, ct: ct);
            if (!result.Success) return [];

            var parts = result.StdOut.Split("---SPLITTER---", 2, StringSplitOptions.RemoveEmptyEntries);
            var taskListOutput = parts.Length > 0 ? parts[0] : "";
            var netstatOutput = parts.Length > 1 ? parts[1] : "";

            var pidNames = ParseTaskListOutput(taskListOutput);
            var connections = ParseNetstatOutput(netstatOutput, pidNames);
            _log.Info($"活动连接查询完成: {host} connections={connections.Count}");
            return connections;
        }
        catch (Exception ex)
        {
            _log.Warn($"活动连接查询失败: {host} - {ex.Message}");
            return [];
        }
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

    private async Task<List<NetworkConnectionInfo>> GetLocalConnectionsAsync(CancellationToken ct)
    {
        try
        {
            _log.Debug($"获取本地活动连接...");
            var netstatResult = await ProcessHelper.RunAsync("netstat.exe", "-ano", ct);
            var output = netstatResult.StdOut;

            var pidNames = await GetLocalPidNamesAsync(ct);

            return ParseNetstatOutput(output, pidNames);
        }
        catch (Exception ex)
        {
            _log.Warn($"本地活动连接查询失败: {ex.Message}");
            return [];
        }
    }

    private async Task<Dictionary<int, string>> GetLocalPidNamesAsync(CancellationToken ct)
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
        catch (Exception ex)
        {
            _log.Debug($"本地进程列表查询失败: {ex.Message}");
        }
        return dict;
    }

    public async Task<List<ProcessDetailInfo>> GetProcessListAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        if (DebugMode) _log.Debug($"获取进程列表: host={host} method=PsExec tasklist /v user={username}");
        var taskListProcesses = await TryGetProcessListViaPsExecTaskListAsync(host, username, password, ct);
        if (taskListProcesses.Count > 0)
        {
            if (DebugMode) _log.Debug($"PsExec tasklist 进程列表完成: host={host} count={taskListProcesses.Count}");
            return taskListProcesses;
        }

        if (DebugMode) _log.Debug($"获取进程列表: host={host} fallback=WMI/DCOM user={username}");
        var wmiProcesses = await TryGetProcessListViaWmiAsync(host, username, password, ct);
        if (wmiProcesses.Count > 0)
        {
            if (DebugMode) _log.Debug($"WMI/DCOM 进程列表完成: host={host} count={wmiProcesses.Count}");
            return wmiProcesses;
        }

        _log.Warn($"进程列表查询无可用数据: {host}。PsExec/tasklist 与 WMI/DCOM 均未返回进程，已跳过远程 PowerShell 慢路径。");
        return [];
    }

    private async Task<List<ProcessDetailInfo>> TryGetProcessListViaPsExecTaskListAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var result = await _psExec.ExecuteAsync(
                host,
                username,
                password,
                "tasklist /v /fo csv /nh",
                ct: linkedCts.Token,
                silent: true);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                _log.Warn($"PsExec/tasklist 详细模式失败: {host} exit={result.ExitCode} detail={RemoteErrorClassifier.Explain(FirstNonEmpty(result.StdErr, result.StdOut, "无输出"), result.ExitCode)}");
                return await TryGetProcessListViaPsExecTaskListBasicAsync(host, username, password, ct);
            }

            var processes = ParseTaskListVerboseCsv(result.StdOut);
            if (processes.Count == 0)
            {
                _log.Warn($"PsExec/tasklist 详细模式返回了输出但无法解析: {host}。尝试基础模式。");
                return await TryGetProcessListViaPsExecTaskListBasicAsync(host, username, password, ct);
            }

            if (processes.Count > 0)
                _log.Info($"进程列表查询完成: {host} method=PsExec/tasklist count={processes.Count}");
            return processes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"PsExec/tasklist 详细模式查询超时: {host}");
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"PsExec/tasklist 详细模式查询异常: {host} - {ex.Message}");
            return [];
        }
    }

    private async Task<List<ProcessDetailInfo>> TryGetProcessListViaPsExecTaskListBasicAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var result = await _psExec.ExecuteAsync(
                host,
                username,
                password,
                "tasklist /fo csv /nh",
                ct: linkedCts.Token,
                silent: true);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                _log.Warn($"PsExec/tasklist 基础模式失败: {host} exit={result.ExitCode} detail={RemoteErrorClassifier.Explain(FirstNonEmpty(result.StdErr, result.StdOut, "无输出"), result.ExitCode)}");
                return [];
            }

            var processes = ParseTaskListBasicCsv(result.StdOut);
            if (processes.Count > 0)
                _log.Info($"进程列表查询完成: {host} method=PsExec/tasklist-basic count={processes.Count}");
            else
                _log.Warn($"PsExec/tasklist 基础模式返回了输出但无法解析: {host}");
            return processes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"PsExec/tasklist 基础模式查询超时: {host}");
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"PsExec/tasklist 基础模式查询异常: {host} - {ex.Message}");
            return [];
        }
    }

    private static List<ProcessDetailInfo> ParseTaskListVerboseCsv(string output)
    {
        var results = new List<ProcessDetailInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = ParseCsvLine(line);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var pid) || pid <= 0)
                continue;

            var sessionId = -1;
            if (parts.Length > 3)
                int.TryParse(parts[3], out sessionId);

            results.Add(new ProcessDetailInfo
            {
                ProcessName = parts[0],
                ProcessId = pid,
                SessionName = parts.Length > 2 ? parts[2] : "",
                SessionId = sessionId,
                MemoryMB = parts.Length > 4 ? FormatTaskListMemoryMb(parts[4]) : "",
                Status = parts.Length > 5 ? parts[5] : "",
                UserName = parts.Length > 6 && !parts[6].Equals("N/A", StringComparison.OrdinalIgnoreCase) ? parts[6] : "",
                CpuTime = parts.Length > 7 ? parts[7] : "",
                WindowTitle = parts.Length > 8 && !parts[8].Equals("N/A", StringComparison.OrdinalIgnoreCase) ? parts[8] : ""
            });
        }
        return results;
    }

    private static string FormatTaskListMemoryMb(string value)
    {
        var digits = Regex.Replace(value, "[^0-9]", "");
        if (!long.TryParse(digits, out var kb))
            return value;
        return (kb / 1024.0).ToString("F1");
    }

    private static List<ProcessDetailInfo> ParseTaskListBasicCsv(string output)
    {
        var results = new List<ProcessDetailInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = ParseCsvLine(line);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var pid) || pid <= 0)
                continue;

            var sessionId = -1;
            if (parts.Length > 3)
                int.TryParse(parts[3], out sessionId);

            results.Add(new ProcessDetailInfo
            {
                ProcessName = parts[0],
                ProcessId = pid,
                SessionName = parts.Length > 2 ? parts[2] : "",
                SessionId = sessionId,
                MemoryMB = parts.Length > 4 ? FormatTaskListMemoryMb(parts[4]) : "",
                Status = "Running"
            });
        }

        return results;
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
                SessionId = int.TryParse(parts[2], out var sessionId) ? sessionId : -1,
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
        if (DebugMode) _log.Debug($"终止进程: host={host} pid={processId} killTree={killTree} method=WMI/DCOM user={username}");
        var wmiKilled = await TryKillProcessViaWmiAsync(host, username, password, processId, killTree, ct);
        if (wmiKilled)
        {
            _log.Info($"已通过 WMI/DCOM 终止进程 PID={processId}" + (killTree ? " (含子进程)" : ""));
            return true;
        }

        var treeFlag = killTree ? " /t" : "";
        var cmd = $"taskkill /pid {processId} /f{treeFlag}";
        if (DebugMode) _log.Debug($"WMI/DCOM 终止失败，回退 PsExec: host={host} cmd={cmd}");
        var r = await _psExec.ExecuteAsync(host, username, password, cmd, ct: ct);
        if (r.Success)
            _log.Info($"已终止进程 PID={processId}" + (killTree ? " (含子进程)" : ""));
        else
            _log.Warn($"终止进程 PID={processId} 失败: {r.StdErr}");
        return r.Success;
    }

    private async Task<List<ProcessDetailInfo>> TryGetProcessListViaWmiAsync(
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
                        SessionId = (int)sessionId,
                        SessionName = sessionId.ToString(),
                        MemoryMB = Math.Round(workingSet / 1048576.0, 1).ToString("F1"),
                        Status = "Running"
                    });
                }
                _log.Debug($"WMI 进程列表查询成功: {host} count={processes.Count}");
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 进程列表查询失败: {host} - {ex.Message}");
                return [];
            }
            return processes;
        }, ct);
    }

    private async Task<bool> TryKillProcessViaWmiAsync(
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

                _log.Debug($"WMI 终止进程完成: host={host} pid={processId} killed={anyKilled}");
                return anyKilled;
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 终止进程失败: host={host} pid={processId} - {ex.Message}");
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
            _log.Debug($"获取本地用户会话...");
            var result = await ProcessHelper.RunAsync("query", "user", ct);
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                ParseSessionOutput(result.StdOut, sessions);
                _log.Info($"本地用户会话查询完成: count={sessions.Count}");
            }
            else
            {
                _log.Warn($"本地 query user 无输出");
            }
            return sessions;
        }

        if (ShouldUsePsExecForSessions(host))
        {
            _log.Info($"能力探测显示会话查询不可用，直接通过 PsExec 获取用户会话: {host}");
            var directResult = await _psExec.ExecuteAsync(host, username, password, "query user", ct: ct);
            if (!string.IsNullOrWhiteSpace(directResult.StdOut))
                ParseSessionOutput(directResult.StdOut, sessions);
            return sessions;
        }

        try
        {
            _log.Debug($"优先通过 PsExec 获取远程用户会话: host={host}");
            var psFirst = await _psExec.ExecuteAsync(host, username, password, "query user", ct: ct, silent: true);
            if (!string.IsNullOrWhiteSpace(psFirst.StdOut))
            {
                ParseSessionOutput(psFirst.StdOut, sessions);
                if (sessions.Count > 0)
                {
                    _log.Info($"PsExec 用户会话查询完成: host={host} count={sessions.Count}");
                    return sessions;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"PsExec 用户会话查询失败: {host} - {ex.Message}");
        }

        try
        {
            _log.Debug($"获取远程用户会话: host={host} method=query user /server");
            var queryResult = await ProcessHelper.RunAsync("query", $"user /server:{host}", ct);
            if (!string.IsNullOrWhiteSpace(queryResult.StdOut))
            {
                ParseSessionOutput(queryResult.StdOut, sessions);
                if (sessions.Count > 0)
                {
                    _log.Info($"远程用户会话查询完成: host={host} count={sessions.Count}");
                    return sessions;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"query user /server 失败: {host} - {ex.Message}");
        }

        _log.Info($"query user /server 无结果，回退 PsExec 获取用户会话: {host}");
        var psResult = await _psExec.ExecuteAsync(host, username, password,
            "query user", ct: ct);
        if (!string.IsNullOrWhiteSpace(psResult.StdOut))
            ParseSessionOutput(psResult.StdOut, sessions);

        _log.Info($"用户会话查询完成: host={host} count={sessions.Count}");
        return sessions;
    }

    private bool ShouldUsePsExecForSessions(string host)
    {
        lock (_capabilityCache)
        {
            if (!_capabilityCache.TryGetValue(host, out var items))
                return false;

            var querySession = items.FirstOrDefault(i => i.Name == "会话查询");
            var psExec = items.FirstOrDefault(i => i.Name == "PsExec 临时执行");
            return querySession?.Success == false && psExec?.Success == true;
        }
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

            var match = Regex.Match(trimmed,
                @"^(?<user>\S+)\s+(?:(?<session>\S+)\s+)?(?<id>\d+)\s+(?<state>\S+)\s+(?<idle>\S+)\s*(?<logon>.*)$");
            if (!match.Success) continue;

            var user = match.Groups["user"].Value;
            if (user.Contains('\\')) continue;
            if (user.StartsWith("NT ", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(match.Groups["id"].Value, out var sessionId)) continue;

            sessions.Add(new UserSessionInfo
            {
                Username = user,
                SessionName = match.Groups["session"].Value,
                SessionId = sessionId,
                State = match.Groups["state"].Value,
                IdleTime = match.Groups["idle"].Value,
                LogonTime = match.Groups["logon"].Value.Trim()
            });
        }
    }

    public async Task<bool> SignOutUserAsync(string host, string username, string password,
        int sessionId, CancellationToken ct = default)
    {
        var cmd = $"logoff {sessionId}";
        var r = await _psExec.ExecuteAsync(host, username, password, cmd, ct: ct);
        if (r.Success)
            _log.Info($"已注销会话 ID={sessionId}");
        else
            _log.Warn($"注销会话 ID={sessionId} 失败: {r.StdErr}");
        return r.Success;
    }
}
