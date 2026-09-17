using System.Globalization;
using System.Text.RegularExpressions;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

/// <summary>
/// Native schtasks.exe transport. Unlike the WMI/DCOM task path, this channel
/// does not require WMI/WinRM and does not install a temporary PsExec service;
/// it talks to the target's Task Scheduler RPC endpoint directly.
/// </summary>
public sealed class TaskSchedulerService : ITaskSchedulerService
{
    private const int LastTaskResultHasNotRun = 0x41303; // SCHED_S_TASK_HAS_NOT_RUN
    private static readonly TimeSpan CreateRunTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResultPollTimeout = TimeSpan.FromSeconds(20);

    private static readonly Regex LastTaskResultRegex = new(
        @"(?:(?:Last\s+Task\s+Result)|(?:上次任务结果)|(?:上次執行結果))\s*[:：]\s*([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TaskStateRegex = new(
        @"(?:(?:Status)|(?:状态)|(?:狀態))\s*[:：]\s*([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public TaskSchedulerService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    private bool DebugMode => _settings.Settings.DebugMode;

    private static string SchtasksPath =>
        Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public async Task<CommandResult> ExecuteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(targetHost))
        {
            return await ExecuteLocalAsync(command, ct);
        }

        var taskName = BuildTaskName();
        var createArgs = BuildCreateArguments(
            targetHost, username, password, taskName, command,
            interactive: false, runAsUser: null);

        if (DebugMode)
            _log.Debug($"计划任务 RPC 创建: {FormatArguments(createArgs)}");

        var create = await RunSchtasksAsync(createArgs, CreateRunTimeout, ct);
        if (!create.Success)
            return TransportFailure("计划任务 RPC 创建任务失败", create);

        try
        {
            var run = await RunSchtasksAsync(
                BuildRunArguments(targetHost, username, password, taskName),
                CreateRunTimeout, ct);
            if (!run.Success)
                return TransportFailure("计划任务 RPC 启动任务失败", run);

            var lastTaskResult = await WaitForLastTaskResultAsync(
                targetHost, username, password, taskName, ct);

            return lastTaskResult is null
                ? new CommandResult(0, "任务已在目标计划任务中启动；未能读取 LastTaskResult。", string.Empty)
                : lastTaskResult.Value == 0
                    ? new CommandResult(0, "任务已在目标计划任务中执行完成 (LastTaskResult=0)。", string.Empty)
                    : new CommandResult(
                        lastTaskResult.Value,
                        string.Empty,
                        $"目标计划任务执行失败 (LastTaskResult=0x{lastTaskResult.Value:X8})。");
        }
        finally
        {
            // Deletion is best-effort; a lingering disabled one-shot task is harmless.
            _ = await RunSchtasksAsync(
                BuildDeleteArguments(targetHost, username, password, taskName),
                DeleteTimeout, CancellationToken.None);
        }
    }

    public async Task<CommandResult> ExecuteInteractiveAsync(
        string targetHost,
        string username,
        string password,
        string command,
        string desktopUsername,
        CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(targetHost))
        {
            return new CommandResult(-1, string.Empty,
                "计划任务 RPC 交互通道仅用于远程目标；本机交互请使用 UAC 路径。");
        }

        var runAsUser = string.IsNullOrWhiteSpace(desktopUsername)
            ? username
            : desktopUsername;
        if (string.IsNullOrWhiteSpace(runAsUser))
        {
            return new CommandResult(-1, string.Empty,
                "计划任务 RPC 已连通，但未能确定目标主机活动桌面的用户，无法创建可见的交互任务。");
        }

        var taskName = BuildTaskName();
        var createArgs = BuildCreateArguments(
            targetHost, username, password, taskName, command,
            interactive: true, runAsUser: runAsUser);

        if (DebugMode)
            _log.Debug($"计划任务 RPC 交互创建: {FormatArguments(createArgs)}");

        var create = await RunSchtasksAsync(createArgs, CreateRunTimeout, ct);
        if (!create.Success)
            return TransportFailure("计划任务 RPC 创建交互任务失败", create);

        try
        {
            var run = await RunSchtasksAsync(
                BuildRunArguments(targetHost, username, password, taskName),
                CreateRunTimeout, ct);
            if (!run.Success)
                return TransportFailure("计划任务 RPC 启动交互任务失败", run);

            return new CommandResult(0,
                $"交互程序启动请求已通过计划任务 RPC 提交: {command}；运行身份: {runAsUser}",
                string.Empty);
        }
        finally
        {
            _ = await RunSchtasksAsync(
                BuildDeleteArguments(targetHost, username, password, taskName),
                DeleteTimeout, CancellationToken.None);
        }
    }

    private async Task<CommandResult> ExecuteLocalAsync(string command, CancellationToken ct)
    {
        var taskName = BuildTaskName();
        var createArgs = new List<string>
        {
            "/Create", "/TN", taskName, "/TR", command,
            "/SC", "ONCE", "/ST", "00:00", "/RU", "SYSTEM", "/RL", "HIGHEST", "/F"
        };
        var create = await RunSchtasksAsync(createArgs, CreateRunTimeout, ct);
        if (!create.Success)
            return TransportFailure("本机计划任务创建失败", create);

        try
        {
            var run = await RunSchtasksAsync(
                new[] { "/Run", "/TN", taskName }, CreateRunTimeout, ct);
            return run.Success
                ? new CommandResult(0, $"本机任务已提交: {command}", string.Empty)
                : TransportFailure("本机计划任务启动失败", run);
        }
        finally
        {
            _ = await RunSchtasksAsync(
                new[] { "/Delete", "/TN", taskName, "/F" }, DeleteTimeout, CancellationToken.None);
        }
    }

    private async Task<int?> WaitForLastTaskResultAsync(
        string targetHost, string username, string password, string taskName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResultPollTimeout);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                await Task.Delay(500, timeout.Token);
                var query = await RunSchtasksAsync(
                    BuildQueryArguments(targetHost, username, password, taskName),
                    QueryTimeout, timeout.Token);
                if (!query.Success)
                {
                    // The task may have finished and been cleaned up by the target.
                    return null;
                }

                var parsed = ParseLastTaskResult($"{query.StdOut}\n{query.StdErr}");
                if (parsed is int value && value != LastTaskResultHasNotRun)
                    return value;

                var state = ParseTaskState($"{query.StdOut}\n{query.StdErr}");
                if ((state is "Ready" or "Disabled") && parsed is null)
                    return null;
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<CommandResult> RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            return await ProcessHelper.RunAsync(SchtasksPath, arguments, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CommandResult(-1, string.Empty, "计划任务 RPC 操作超时。");
        }
    }

    internal static IReadOnlyList<string> BuildCreateArguments(
        string targetHost,
        string username,
        string password,
        string taskName,
        string command,
        bool interactive,
        string? runAsUser)
    {
        var args = new List<string>
        {
            "/Create",
            "/S", targetHost.Trim('\\', ' '),
            "/TN", taskName,
            "/TR", command,
            "/SC", "ONCE",
            "/ST", "00:00",
        };

        if (HasCredentials(username, password))
        {
            args.Add("/U");
            args.Add(username);
            args.Add("/P");
            args.Add(password);
        }

        if (interactive)
        {
            args.Add("/RU");
            args.Add(runAsUser ?? string.Empty);
            args.Add("/IT");
            args.Add("/RL");
            args.Add("HIGHEST");
        }
        else
        {
            args.Add("/RU");
            args.Add("SYSTEM");
            args.Add("/RL");
            args.Add("HIGHEST");
        }

        args.Add("/F");
        return args;
    }

    internal static IReadOnlyList<string> BuildRunArguments(
        string targetHost, string username, string password, string taskName)
    {
        var args = new List<string> { "/Run", "/S", targetHost.Trim('\\', ' ') };
        AddCredentials(args, username, password);
        args.Add("/TN");
        args.Add(taskName);
        return args;
    }

    internal static IReadOnlyList<string> BuildQueryArguments(
        string targetHost, string username, string password, string taskName)
    {
        var args = new List<string>
        {
            "/Query", "/S", targetHost.Trim('\\', ' '),
        };
        AddCredentials(args, username, password);
        args.Add("/TN");
        args.Add(taskName);
        args.Add("/V");
        args.Add("/FO");
        args.Add("LIST");
        return args;
    }

    internal static IReadOnlyList<string> BuildDeleteArguments(
        string targetHost, string username, string password, string taskName)
    {
        var args = new List<string> { "/Delete", "/S", targetHost.Trim('\\', ' ') };
        AddCredentials(args, username, password);
        args.Add("/TN");
        args.Add(taskName);
        args.Add("/F");
        return args;
    }

    internal static string BuildTaskName() =>
        "RemoteOpsTool_" + Guid.NewGuid().ToString("N");

    internal static string SanitizeTaskName(string taskName)
    {
        var sanitized = new string((taskName ?? string.Empty)
            .Where(char.IsLetterOrDigit).ToArray());
        if (sanitized.Length == 0)
            throw new ArgumentException("Task name must contain at least one alphanumeric character.", nameof(taskName));
        return sanitized;
    }

    internal static int? ParseLastTaskResult(string output)
    {
        var match = LastTaskResultRegex.Match(output ?? string.Empty);
        if (!match.Success)
            return null;

        var raw = match.Groups[1].Value.Trim();
        return ParseHexOrDecimal(raw);
    }

    internal static string? ParseTaskState(string output)
    {
        var match = TaskStateRegex.Match(output ?? string.Empty);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static bool IsSchtasksTransportFailure(CommandResult result)
    {
        if (result.ExitCode == -1)
            return true;
        if (result.Success)
            return false;

        var output = $"{result.StdErr}\n{result.StdOut}";
        return output.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("错误:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("RPC 服务器不可用", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("RPC server is unavailable", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("找不到网络路径", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("network path was not found", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseHexOrDecimal(string raw)
    {
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
            if (int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return hex;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;

        return null;
    }

    private static bool HasCredentials(string username, string password) =>
        !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);

    private static void AddCredentials(List<string> args, string username, string password)
    {
        if (!HasCredentials(username, password))
            return;
        args.Add("/U");
        args.Add(username);
        args.Add("/P");
        args.Add(password);
    }

    private CommandResult TransportFailure(string operation, CommandResult result) =>
        new(-1, string.Empty,
            $"{operation}: {RemoteErrorClassifier.Explain($"{result.StdErr}\n{result.StdOut}", result.ExitCode)}");

    private static string FormatArguments(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
