using System.Management;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services.Transports;

/// <summary>
/// Stateless capability probe. It deliberately talks to single, independent
/// channels (ICMP, TCP, SMB, WMI and one-shot runners) instead of the
/// capability-aware router, so probing can never recurse into itself and can
/// never depend on the transports it is trying to classify.
/// This probe does not cache results; CapabilityService owns the host/credential snapshot cache.
/// </summary>
public sealed class TransportProbeService : ITransportProbeService
{
    private readonly IRemoteCommandExecutor _executor;

    public TransportProbeService(IRemoteCommandExecutor executor)
    {
        _executor = executor;
    }

    private static async Task<PingResult> PingAsync(string host, CancellationToken ct)
    {
        // A local target does not need an ICMP round-trip. ICMP is commonly
        // blocked by the local firewall, which used to make the application
        // mark the local computer as disconnected and disable every action.
        if (HostHelper.IsLocalHost(host))
        {
            ct.ThrowIfCancellationRequested();
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
                return new PingResult(false, "Ping timeout (>4s)");

            var reply = await pingTask;
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                return new PingResult(true, $"Reply from {reply.Address}: time={reply.RoundtripTime}ms", reply.RoundtripTime);

            return new PingResult(false, $"Ping failed: {reply.Status}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
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

        if (HostHelper.IsLocalHost(host))
        {
            // These ports describe remote management transports. A local
            // target already uses local APIs/processes and must not be marked
            // unavailable just because SMB/RPC/WinRM is disabled locally.
            foreach (var name in new[] { "SMB 445", "RPC 135", "WinRM 5985" })
            {
                results.Add(new RemoteCapabilityInfo
                {
                    Name = name,
                    Success = true,
                    Detail = "本机目标无需远程端口；已使用本地执行路径"
                });
            }
        }
        else
        {
            results.Add(await ProbeTcpPortAsync(host, 445, "SMB 445", ct));
            results.Add(await ProbeTcpPortAsync(host, 135, "RPC 135", ct));
            results.Add(await ProbeTcpPortAsync(host, 5985, "WinRM 5985", ct));
        }

        results.Add(await ProbeAdminShareAsync(host, username, password, ct));
        results.Add(await ProbeWmiAsync(host, username, password, ct));
        results.Add(await ProbeQuerySessionAsync(host, username, password, ct));
        results.Add(await ProbePsExecAsync(host, username, password, ct));
        results.Add(await ProbeSchtasksAsync(host, username, password, ct));

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
        if (HostHelper.IsLocalHost(host))
        {
            ct.ThrowIfCancellationRequested();
            var localSystemDirectory = Environment.SystemDirectory;
            var accessible = Directory.Exists(localSystemDirectory);
            return new RemoteCapabilityInfo
            {
                Name = "ADMIN$ 管理共享",
                Success = accessible,
                Detail = accessible
                    ? "本机系统目录可访问（本机无需 ADMIN$ SMB）"
                    : $"本机系统目录不可访问: {localSystemDirectory}"
            };
        }

        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var share = $@"\\{host.Trim().Trim('\\')}\ADMIN$";
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

    private async Task<RemoteCapabilityInfo> ProbeQuerySessionAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var isLocal = HostHelper.IsLocalHost(host);
            var result = await RunQueryUserAsync(host, username, password, timeout.Token);
            var hasOutput = !string.IsNullOrWhiteSpace(result.StdOut);
            return new RemoteCapabilityInfo
            {
                Name = "会话查询",
                Success = hasOutput,
                Detail = hasOutput
                    ? (isLocal ? "本机 query user 可用" : "query user /server 可用")
                    : RemoteErrorClassifier.Explain(FirstNonEmpty(result.StdErr, result.StdOut, "无输出"), result.ExitCode)
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

    private static async Task<CommandResult> RunQueryUserAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        var isLocal = HostHelper.IsLocalHost(host);
        var arguments = isLocal ? "user" : $"user /server:{host.Trim().Trim('\\')}";
        if (isLocal || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return await ProcessHelper.RunAsync("query", arguments, ct);

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        return await ProcessHelper.RunAsync(
            "query", arguments, runAsUser, password, runAsDomain, ct);
    }

    private static (string User, string Domain) ResolveRunAsIdentity(string username)
    {
        var (runAsUser, runAsDomain) = ProcessHelper.SplitUserDomain(username);
        if (string.IsNullOrEmpty(runAsDomain))
        {
            var currentDomain = Environment.UserDomainName;
            if (!string.IsNullOrEmpty(currentDomain) &&
                !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                runAsDomain = currentDomain;
        }

        return (runAsUser, runAsDomain);
    }

    private static async Task<RemoteCapabilityInfo> ProbeSchtasksAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var schtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            var args = new List<string>
            {
                "/Query", "/S", host.Trim('\\', ' '), "/V", "/FO", "LIST",
            };
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                args.Add("/U");
                args.Add(username);
                args.Add("/P");
                args.Add(password);
            }

            var result = await ProcessHelper.RunAsync(schtasksPath, args, timeout.Token);
            return new RemoteCapabilityInfo
            {
                Name = "计划任务 RPC",
                Success = result.Success,
                Detail = result.Success
                    ? "schtasks /Query 可访问"
                    : RemoteErrorClassifier.Explain(FirstNonEmpty(result.StdErr, result.StdOut, $"exit={result.ExitCode}"), result.ExitCode)
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RemoteCapabilityInfo { Name = "计划任务 RPC", Success = false, Detail = "执行超时" };
        }
        catch (Exception ex)
        {
            return new RemoteCapabilityInfo { Name = "计划任务 RPC", Success = false, Detail = ex.Message };
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
            var result = await _executor.ExecutePsExecOnlyAsync(new RemoteCommand
            {
                TargetHost = host,
                Username = username,
                Password = password,
                Command = "whoami",
                Shell = CommandShell.Direct,
                Silent = true,
            }, ct: timeout.Token);
            return new RemoteCapabilityInfo
            {
                Name = "PsExec 临时执行",
                Success = !result.IsTransportFailure && result.Success,
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
}