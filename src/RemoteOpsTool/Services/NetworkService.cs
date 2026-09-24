using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class NetworkService : INetworkService
{
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public NetworkService(
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

    public async Task<PingResult> PingAsync(string host, CancellationToken ct = default)
    {
        if (DebugMode) _log.Debug($"Ping 开始: host={host}");

        // A local target does not need an ICMP round-trip. ICMP is commonly
        // blocked by the local firewall, which used to make the application
        // mark the local computer as disconnected and disable every action.
        if (HostHelper.IsLocalHost(host))
        {
            ct.ThrowIfCancellationRequested();
            if (DebugMode) _log.Debug($"Ping 本机短路: host={host}");
            return new PingResult(true, "本机可用，无需 ICMP Ping", 0);
        }

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


    private Task<CommandResult> RunAdminCommandAsync(
        string host,
        string username,
        string password,
        string command,
        CancellationToken ct = default)
    {
        return HostHelper.IsLocalHost(host)
            ? _psExec.ExecuteLocalElevatedAsync(
                host, username, password, command, ct, CommandShell.Direct)
            : _execution.ExecuteOnceAsync(host, username, password, command, ct: ct);
    }

    public async Task FlushDnsAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var r = await RunAdminCommandAsync(host, username, password, "ipconfig.exe /flushdns", ct);
        if (r.Success) _log.Info($"DNS 缓存已刷新: {host}"); else _log.Error($"DNS 刷新失败: {r.StdErr}");
    }

    public async Task RefreshIpAsync(string host, string username, string password, CancellationToken ct = default)
    {
        var rel = await RunAdminCommandAsync(host, username, password, "ipconfig.exe /release", ct);
        _log.Info(rel.Success ? "IP 已释放." : $"释放失败: {rel.StdErr}");
        var ren = await RunAdminCommandAsync(host, username, password, "ipconfig.exe /renew", ct);
        if (ren.Success) _log.Info($"IP 已更新: {host}"); else _log.Error($"更新失败: {ren.StdErr}");
    }

    public async Task<List<NetworkConnectionInfo>> GetActiveConnectionsAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(host))
            return await Task.Run(() => GetLocalConnectionsAsync(ct), ct);

        try
        {
            if (DebugMode) _log.Debug($"获取活动连接: host={host} method=netstat-ano+WMI-process-map");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // netstat 与进程名映射互不依赖。进程名属于展示增强信息，
            // 不应因为 WMI/tasklist 变慢而拖垮连接列表主查询。
            var netstatTask = _execution.ExecuteOnceAsync(
                host,
                username,
                password,
                "netstat -ano",
                RemoteOperationKind.Inventory,
                silent: true,
                ct: linkedCts.Token);
            var pidNamesTask = TryGetProcessNameMapAsync(host, username, password, linkedCts.Token);

            var result = await netstatTask;
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                try { await pidNamesTask; } catch (OperationCanceledException) { }
                _log.Warn($"netstat 活动连接查询失败: {host} exit={result.ExitCode}");
                return [];
            }

            var pidNames = await pidNamesTask;
            var connections = ParseNetstatOutput(result.StdOut, pidNames);
            _log.Info($"活动连接查询完成: {host} method=netstat-ano connections={connections.Count}");
            return connections;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"活动连接查询超时: {host}");
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"活动连接查询失败: {host} - {ex.Message}");
            return [];
        }
    }

    private async Task<Dictionary<int, string>> TryGetProcessNameMapAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        // 进程名只用于展示。给它独立的短超时，避免一个慢 WMI 查询
        // 让 netstat 已返回的连接列表继续等待。
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var pidNames = await TryGetProcessNameMapViaWmiAsync(host, username, password, linkedCts.Token);
            if (pidNames.Count > 0)
                return pidNames;

            var result = await _execution.ExecuteOnceAsync(
                host,
                username,
                password,
                "tasklist /fo csv /nh",
                RemoteOperationKind.Inventory,
                silent: true,
                ct: linkedCts.Token);
            if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
                return ParseTaskListOutput(result.StdOut);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Debug($"活动连接进程名查询超时: {host}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"活动连接进程名回退查询失败: {host} - {ex.Message}");
        }

        return [];
    }

    private async Task<Dictionary<int, string>> TryGetProcessNameMapViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                var pidNames = new Dictionary<int, string>();
                ct.ThrowIfCancellationRequested();
                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT Name,ProcessId FROM Win32_Process"));
                foreach (ManagementObject process in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var pid = (int)RemoteWmiHelper.GetUInt32(process, "ProcessId");
                    var name = RemoteWmiHelper.GetString(process, "Name");
                    if (pid > 0 && !string.IsNullOrWhiteSpace(name))
                        pidNames[pid] = name;
                }
                return pidNames;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"活动连接 WMI 进程名查询失败: {host} - {ex.Message}");
            return [];
        }
    }

    private static Dictionary<int, string> ParseTaskListOutput(string output)
    {
        var dict = new Dictionary<int, string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = ParseCsvLine(line);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var pid))
                dict[pid] = parts[0];
        }
        return dict;
    }

    private async Task<List<NetworkConnectionInfo>> GetLocalConnectionsAsync(CancellationToken ct)
    {
        try
        {
            _log.Debug($"获取本地活动连接...");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var netstatResult = await ProcessHelper.RunAsync("netstat.exe", "-ano", linkedCts.Token);
            if (!netstatResult.Success || string.IsNullOrWhiteSpace(netstatResult.StdOut))
            {
                _log.Warn($"本地 netstat 查询失败: exit={netstatResult.ExitCode}");
                return [];
            }

            var pidNames = await GetLocalPidNamesAsync(linkedCts.Token);
            return ParseNetstatOutput(netstatResult.StdOut, pidNames);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("本地活动连接查询超时");
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
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
            await Task.Run(() =>
            {
                foreach (var process in Process.GetProcesses())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        dict[process.Id] = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? process.ProcessName
                            : process.ProcessName + ".exe";
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"本地进程名查询失败: {ex.Message}");
        }
        return dict;
    }

    public async Task<List<ProcessDetailInfo>> GetProcessListAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(host))
        {
            if (DebugMode) _log.Debug($"获取本机进程列表: host={host} method=tasklist");
            var localProcesses = await TryGetLocalProcessListAsync(ct);
            if (localProcesses.Count > 0)
            {
                if (DebugMode) _log.Debug($"本机 tasklist 进程列表完成: count={localProcesses.Count}");
                return localProcesses;
            }

            if (DebugMode) _log.Debug($"本机 tasklist 无数据，回退 WMI/DCOM: host={host}");
            var localWmiProcesses = await TryGetProcessListViaWmiAsync(host, username, password, ct);
            if (localWmiProcesses.Count > 0)
                return localWmiProcesses;

            _log.Warn($"本机进程列表查询无可用数据: {host}");
            return [];
        }

        if (DebugMode) _log.Debug($"获取进程列表: host={host} method=WMI/DCOM user={username}");
        var wmiProcesses = await TryGetProcessListViaWmiAsync(host, username, password, ct);
        if (wmiProcesses.Count > 0)
        {
            if (DebugMode) _log.Debug($"WMI/DCOM 进程列表完成: host={host} count={wmiProcesses.Count}");
            return wmiProcesses;
        }

        if (DebugMode) _log.Debug($"获取进程列表: host={host} fallback=tasklist command");
        var taskListProcesses = await TryGetProcessListViaPsExecTaskListAsync(host, username, password, ct);
        if (taskListProcesses.Count > 0)
        {
            if (DebugMode) _log.Debug($"命令通道 tasklist 进程列表完成: host={host} count={taskListProcesses.Count}");
            return taskListProcesses;
        }

        _log.Warn($"进程列表查询无可用数据: {host}。WMI/DCOM 与 tasklist 命令通道均未返回进程。");
        return [];
    }

    private async Task<List<ProcessDetailInfo>> TryGetLocalProcessListAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var result = await ProcessHelper.RunAsync("tasklist.exe", "/v /fo csv /nh", linkedCts.Token);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                _log.Debug($"本机 tasklist 详细模式失败: exit={result.ExitCode} detail={FirstNonEmpty(result.StdErr, result.StdOut, "无输出")}");
                result = await ProcessHelper.RunAsync("tasklist.exe", "/fo csv /nh", linkedCts.Token);
                if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
                    return [];

                return ParseTaskListBasicCsv(result.StdOut);
            }

            var processes = ParseTaskListVerboseCsv(result.StdOut);
            if (processes.Count > 0)
                _log.Info($"本机进程列表查询完成: method=tasklist count={processes.Count}");
            return processes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("本机 tasklist 进程列表查询超时");
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"本机 tasklist 进程列表查询异常: {ex.Message}");
            return [];
        }
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
            var result = await _execution.ExecuteOnceAsync(
                host,
                username,
                password,
                "tasklist /v /fo csv /nh",
                RemoteOperationKind.Inventory,
                silent: true,
                ct: linkedCts.Token);

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
            var result = await _execution.ExecuteOnceAsync(
                host,
                username,
                password,
                "tasklist /fo csv /nh",
                RemoteOperationKind.Inventory,
                silent: true,
                ct: linkedCts.Token);

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

    internal static List<NetworkConnectionInfo> ParseNetstatOutput(
        string output,
        Dictionary<int, string> pidNames)
    {
        var connections = new List<NetworkConnectionInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !int.TryParse(parts[^1], out var pid))
                continue;

            var protocol = parts[0].ToUpperInvariant();
            if (protocol is not ("TCP" or "UDP"))
                continue;

            // TCP: protocol local foreign state pid
            // UDP: protocol local *:* pid
            // 不按固定行数 Skip 表头，兼容本地化 Windows 的不同表头长度。
            var hasState = protocol == "TCP" && parts.Length >= 5;
            pidNames.TryGetValue(pid, out var processName);
            connections.Add(new NetworkConnectionInfo
            {
                Protocol = protocol,
                LocalAddress = parts[1],
                RemoteAddress = parts[2],
                State = hasState ? parts[3] : "",
                ProcessId = pid,
                ProcessName = processName ?? ""
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
        var r = await RunAdminCommandAsync(host, username, password, cmd, ct);
        if (r.Success)
            _log.Info($"已终止进程 PID={processId}" + (killTree ? " (含子进程)" : ""));
        else
            _log.Warn($"终止进程 PID={processId} 失败: {r.StdErr}");
        return r.Success;
    }

    /// <summary>
    /// 关键系统进程名单。终止这些进程会使目标主机立刻蓝屏、失去管理通道或
    /// 无法再登录，因此“重启进程”功能对其硬性拒绝。
    /// </summary>
    internal static readonly IReadOnlySet<string> CriticalProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "registry", "idle", "memcompression",
            "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "lsaiso", "svchost"
        };

    internal static string NormalizeProcessName(string? processName)
    {
        var name = (processName ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Trim();
    }

    /// <summary>
    /// 重启前的静态校验。只检查不依赖远端连接的条件，便于单元测试完整覆盖。
    /// </summary>
    internal static bool TryValidateRestartTarget(ProcessDetailInfo? process, out string reason)
    {
        reason = string.Empty;
        if (process is null)
        {
            reason = "未选择进程。";
            return false;
        }

        if (process.ProcessId <= 0)
        {
            reason = "进程 ID 无效，无法重启。";
            return false;
        }

        if (CriticalProcessNames.Contains(NormalizeProcessName(process.ProcessName)))
        {
            reason = $"{process.ProcessName} 是关键系统进程，终止后可能导致目标主机蓝屏或失去管理通道，已禁止重启。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            reason = $"无法读取 {process.ProcessName} 的可执行文件路径，不能安全重启该进程。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 生成重启使用的命令行。优先沿用进程原始命令行，仅在原始命令行以“未加引号
    /// 且含空格”的可执行文件路径开头时才重新加引号，避免被拆成 程序+参数。
    /// </summary>
    internal static string BuildRestartCommandLine(ProcessDetailInfo process)
    {
        var executablePath = (process.ExecutablePath ?? string.Empty).Trim();
        var raw = (process.CommandLine ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(raw))
            return QuoteIfNeeded(executablePath);

        if (executablePath.Length == 0 || !executablePath.Contains(' '))
            return raw;

        if (raw.StartsWith(executablePath, StringComparison.OrdinalIgnoreCase))
            return QuoteIfNeeded(executablePath) + raw[executablePath.Length..];

        return raw;
    }

    private static string QuoteIfNeeded(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return trimmed;
        if (trimmed.StartsWith('"'))
            return trimmed;
        return trimmed.Contains(' ') ? $"\"{trimmed}\"" : trimmed;
    }

    internal static string DescribeWmiCreateFailure(uint returnValue) => returnValue switch
    {
        2 => "拒绝访问（权限不足）。",
        3 => "权限不足，无法创建进程。",
        8 => "WMI 返回未知失败。",
        9 => "找不到可执行文件路径。",
        21 => "启动参数无效。",
        _ => $"WMI 创建进程失败（代码 {returnValue}）。"
    };

    public async Task<ProcessRestartResult> RestartProcessAsync(string host, string username, string password,
        ProcessDetailInfo process, CancellationToken ct = default)
    {
        if (!TryValidateRestartTarget(process, out var invalidReason))
            return ProcessRestartResult.Fail(invalidReason);

        var name = process.ProcessName;
        var pid = process.ProcessId;

        // 日志只记录进程名与 PID，绝不记录完整命令行。
        if (DebugMode)
            _log.Debug($"重启进程开始: host={host} name={name} pid={pid} session={process.SessionId}");

        if (HostHelper.IsLocalHost(host) && process.SessionId > 0 &&
            process.SessionId != Process.GetCurrentProcess().SessionId)
        {
            return ProcessRestartResult.Fail(
                $"{name} (PID {pid}) 运行在会话 {process.SessionId}，与当前会话不同，本机模式下无法在其它登录会话重建进程。");
        }

        if (await IsServiceProcessAsync(host, username, password, pid, ct))
        {
            _log.Info($"重启进程被拒绝: {name} (PID {pid}) 由 Windows 服务承载。");
            return ProcessRestartResult.Fail(
                $"{name} (PID {pid}) 由 Windows 服务承载，请改用“远程管理 → 服务管理”重启对应服务。");
        }

        var commandLine = BuildRestartCommandLine(process);

        if (!await KillProcessAsync(host, username, password, pid, killTree: false, ct))
        {
            _log.Warn($"重启进程失败: 无法终止 {name} (PID {pid})。");
            return ProcessRestartResult.Fail($"{name} (PID {pid}) 终止失败，未执行重启。");
        }

        _log.Info($"重启进程: 已终止 {name} (PID {pid})，正在重新启动。");
        var launchError = await LaunchRestartedProcessAsync(host, username, password, process, commandLine, ct);
        if (launchError is null)
        {
            _log.Info($"重启进程完成: {name}（原 PID {pid}）。");
            return ProcessRestartResult.Ok($"{name}（原 PID {pid}）已重新启动。");
        }

        _log.Error($"重启进程失败: {name} (PID {pid}) 已终止，但重新启动失败（{launchError}）。");
        return ProcessRestartResult.Fail($"{name} (PID {pid}) 已终止，但重新启动失败：{launchError}");
    }

    /// <summary>
    /// 判断目标进程是否由 Windows 服务承载。查询失败时按“非服务进程”处理，
    /// 由后续终止/启动步骤给出真实错误。
    /// </summary>
    private async Task<bool> IsServiceProcessAsync(string host, string username, string password,
        int processId, CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name,State FROM Win32_Service WHERE ProcessId={processId}"));
                foreach (ManagementObject service in searcher.Get())
                {
                    var state = RemoteWmiHelper.GetString(service, "State");
                    if (string.IsNullOrWhiteSpace(state) ||
                        state.Equals("Running", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"服务进程检测失败: host={host} pid={processId} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 重新创建进程。返回 null 表示已成功启动，否则返回可直接展示的失败原因。
    /// </summary>
    private async Task<string?> LaunchRestartedProcessAsync(string host, string username, string password,
        ProcessDetailInfo process, string commandLine, CancellationToken ct)
    {
        try
        {
            if (HostHelper.IsLocalHost(host))
                return LaunchLocalProcess(process, commandLine);

            if (process.SessionId > 0)
            {
                // 交互式进程必须回到原会话，绝不默认在当前会话启动。
                var result = await _execution.ExecuteOnceAsync(
                    host, username, password, commandLine,
                    RemoteOperationKind.InteractiveLaunch,
                    shell: CommandShell.Direct,
                    wrapCmd: false,
                    silent: true,
                    interactiveSession: true,
                    sessionId: process.SessionId,
                    ct: ct);
                if (result.Success)
                    return null;

                return FirstNonEmpty(result.StdErr, result.StdOut, $"启动通道返回退出代码 {result.ExitCode}。");
            }

            return await CreateProcessViaWmiAsync(host, username, password, process, commandLine, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"重启进程启动阶段异常: host={host} name={process.ProcessName} error={ex.Message}");
            return ex.Message;
        }
    }

    private static string? LaunchLocalProcess(ProcessDetailInfo process, string commandLine)
    {
        try
        {
            var executablePath = process.ExecutablePath.Trim();
            var arguments = commandLine.StartsWith(executablePath, StringComparison.OrdinalIgnoreCase)
                ? commandLine[executablePath.Length..].Trim()
                : string.Empty;
            var workingDirectory = Path.GetDirectoryName(executablePath);

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.SystemDirectory
                    : workingDirectory,
                UseShellExecute = false
            });

            return started is null ? "Windows 未创建进程。" : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> CreateProcessViaWmiAsync(string host, string username, string password,
        ProcessDetailInfo process, string commandLine, CancellationToken ct)
    {
        return await RemoteWmiHelper.ExecuteAsync<string?>(host, username, password, scope =>
        {
            ct.ThrowIfCancellationRequested();
            using var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
            using var parameters = processClass.GetMethodParameters("Create");
            parameters["CommandLine"] = commandLine;

            var workingDirectory = string.IsNullOrWhiteSpace(process.ExecutablePath)
                ? null
                : Path.GetDirectoryName(process.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                parameters["CurrentDirectory"] = workingDirectory;

            using var result = processClass.InvokeMethod("Create", parameters, null);
            var returnValue = RemoteWmiHelper.GetUInt32(result, "ReturnValue");
            if (returnValue != 0)
                return DescribeWmiCreateFailure(returnValue);

            var newPid = RemoteWmiHelper.GetUInt32(result, "ProcessId");
            _log.Debug($"WMI 创建进程成功: host={host} name={process.ProcessName} newPid={newPid}");
            return null;
        }, ct).ConfigureAwait(false);
    }

    private async Task<List<ProcessDetailInfo>> TryGetProcessListViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                var processes = new List<ProcessDetailInfo>();
                ct.ThrowIfCancellationRequested();
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery(
                        "SELECT Name,ProcessId,ParentProcessId,SessionId,WorkingSetSize,ExecutablePath,CommandLine FROM Win32_Process"));
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
                        ParentProcessId = (int)RemoteWmiHelper.GetUInt32(process, "ParentProcessId"),
                        SessionId = (int)sessionId,
                        SessionName = sessionId.ToString(),
                        MemoryMB = Math.Round(workingSet / 1048576.0, 1).ToString("F1"),
                        Status = "Running",
                        ExecutablePath = RemoteWmiHelper.GetString(process, "ExecutablePath"),
                        CommandLine = RemoteWmiHelper.GetString(process, "CommandLine")
                    });
                }
                _log.Debug($"WMI 进程列表查询成功: {host} count={processes.Count}");
                return processes;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 进程列表查询失败: {host} - {ex.Message}");
            return [];
        }
    }

    private async Task<bool> TryKillProcessViaWmiAsync(
        string host,
        string username,
        string password,
        int processId,
        bool killTree,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();

                var query = killTree
                    ? "SELECT ProcessId,ParentProcessId FROM Win32_Process"
                    : $"SELECT ProcessId,ParentProcessId FROM Win32_Process WHERE ProcessId={processId}";
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
                using var results = searcher.Get();

                var snapshot = new List<ProcessTreeSnapshotEntry>();
                foreach (ManagementObject process in results)
                {
                    ct.ThrowIfCancellationRequested();
                    var pid = (int)RemoteWmiHelper.GetUInt32(process, "ProcessId");
                    if (pid <= 0) continue;

                    snapshot.Add(new ProcessTreeSnapshotEntry(
                        pid,
                        (int)RemoteWmiHelper.GetUInt32(process, "ParentProcessId"),
                        process));
                }

                var processIds = BuildProcessTerminationOrder(
                    snapshot.Select(static entry => (entry.ProcessId, entry.ParentProcessId)),
                    processId,
                    ct);
                var processByPid = snapshot
                    .GroupBy(static entry => entry.ProcessId)
                    .ToDictionary(static group => group.Key, static group => group.First().Process);

                var anyKilled = false;
                foreach (var pid in processIds)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!processByPid.TryGetValue(pid, out var process))
                        continue;

                    try
                    {
                        var result = process.InvokeMethod("Terminate", null, null);
                        anyKilled |= RemoteWmiHelper.IsSuccessReturn(result);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug($"WMI 终止进程 PID={pid} 失败: host={host} - {ex.Message}");
                    }
                }

                _log.Debug($"WMI 终止进程完成: host={host} pid={processId} killed={anyKilled}");
                return anyKilled;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 终止进程失败: host={host} pid={processId} - {ex.Message}");
            return false;
        }
    }

    private sealed record ProcessTreeSnapshotEntry(
        int ProcessId,
        int ParentProcessId,
        ManagementObject Process);

    /// <summary>
    /// 生成子进程优先的终止顺序。输入可以来自同一次 Win32_Process 快照，
    /// 重复 PID 会去重，异常父子环也不会导致无限遍历。
    /// </summary>
    internal static IReadOnlyList<int> BuildProcessTerminationOrder(
        IEnumerable<(int ProcessId, int ParentProcessId)> processes,
        int rootProcessId,
        CancellationToken ct = default)
    {
        if (rootProcessId <= 0)
            return [];

        var childrenByParent = new Dictionary<int, List<int>>();
        var knownProcessIds = new HashSet<int>();
        foreach (var (processId, parentProcessId) in processes)
        {
            ct.ThrowIfCancellationRequested();
            if (processId <= 0 || !knownProcessIds.Add(processId))
                continue;

            if (!childrenByParent.TryGetValue(parentProcessId, out var children))
            {
                children = [];
                childrenByParent[parentProcessId] = children;
            }
            children.Add(processId);
        }

        var preOrder = new List<int>();
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootProcessId);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            if (!visited.Add(current))
                continue;

            preOrder.Add(current);
            if (!childrenByParent.TryGetValue(current, out var children)) continue;

            // 反向压栈以保持快照中的同级出现顺序；终止顺序最终仍会反向展开。
            for (var index = children.Count - 1; index >= 0; index--)
                stack.Push(children[index]);
        }

        preOrder.Reverse();
        return preOrder;
    }

    public async Task<List<UserSessionInfo>> GetUserSessionsWithSessionAsync(
        IRemoteExecutionSession session,
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

        _log.Debug($"使用固定能力快照查询远程用户会话: host={host}");
        var transport = await session.ExecuteAsync(
            RemoteOperationKind.Inventory,
            new RemoteCommand
            {
                TargetHost = host,
                Username = username,
                Password = password,
                Command = "query user",
                Shell = CommandShell.Direct,
                WrapCmd = false,
                Silent = true,
            },
            ct: ct);

        // query user can return exit code 1 while still producing a valid table.
        // Parse stdout first; a remote command failure is final and never retried.
        if (!string.IsNullOrWhiteSpace(transport.StdOut))
            ParseSessionOutput(transport.StdOut, sessions);

        if (sessions.Count == 0 && !transport.Success)
            _log.Warn($"用户会话查询失败: host={host} transport={transport.Transport} exit={transport.ExitCode} error={transport.StdErr}");
        else
            _log.Info($"用户会话查询完成: host={host} transport={transport.Transport} count={sessions.Count}");
        return sessions;
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
        var r = await RunAdminCommandAsync(host, username, password, cmd, ct);
        if (r.Success)
            _log.Info($"已注销会话 ID={sessionId}");
        else
            _log.Warn($"注销会话 ID={sessionId} 失败: {r.StdErr}");
        return r.Success;
    }
}
