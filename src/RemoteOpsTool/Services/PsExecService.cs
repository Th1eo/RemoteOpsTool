using System.Diagnostics;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class PsExecService : IPsExecService, IRemoteCommandExecutor
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly ITaskSchedulerService? _taskScheduler;
    private static int _serviceCounter;
    internal const int MaxSafeRunAsCommandLength = 700;
    private const uint HkeyLocalMachine = 0x80000002;
    private const string WmiJobRoot = @"SOFTWARE\RemoteOpsTool\WmiJobs";
    // This marker is emitted only for failures in the WMI/DCOM transport
    // itself. It prevents a remote command's legitimate output (for example a
    // diagnostic mentioning Win32_Process.Create or StdRegProv) from being
    // mistaken for a reason to execute the command a second time via PsExec.
    private const int WmiMaxCommandLineLength = 30000;
    private static readonly TimeSpan WmiCommandTimeout = TimeSpan.FromMinutes(15);

    public PsExecService(
        ISettingsService settings,
        ILogService log,
        ITaskSchedulerService? taskScheduler = null)
    {
        _settings = settings;
        _log = log;
        _taskScheduler = taskScheduler;
    }

    private string PsExecPath
    {
        get
        {
            var path = _settings.Settings.PsToolsPath;
            var psExec64 = Path.Combine(path, "PsExec64.exe");
            if (_settings.Settings.PreferPsExec64 && File.Exists(psExec64))
                return psExec64;

            return Path.Combine(path, "PsExec.exe");
        }
    }

    private bool DebugMode => _settings.Settings.DebugMode;

    private void DebugLog(string message)
    {
        if (DebugMode)
            _log.Debug(message);
    }

    internal IReadOnlyList<string> BuildArguments(string targetHost, string username, string password, string command,
        bool interactiveSession, int sessionId, bool wrapCmd = true, CommandShell shell = CommandShell.Cmd,
        bool useDefaultServiceName = false)
    {
        var args = new List<string>
        {
            $"\\\\{targetHost.Trim('\\', ' ')}"
        };

        var hasCredentials = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
        // PsExec must be started under the selected credential and must also
        // receive -u/-p for the remote service connection. The remote target
        // may otherwise authenticate the temporary service with the launcher
        // token instead of the selected administrative credential.
        var includeExplicitCredentials = hasCredentials;

        if (includeExplicitCredentials)
        {
            args.Add("-u");
            args.Add(QualifyUserName(username));
            args.Add("-p");
            args.Add(password);
        }

        args.AddRange(["-accepteula", "-nobanner", "-h"]);
        var timeout = Math.Clamp(_settings.Settings.PsExecConnectTimeoutSeconds, 3, 60);
        if (interactiveSession)
        {
            // Interactive desktop launches use PsExec's default PSEXESVC together with
            // explicit -u/-p, -h, -i and -d. A custom -r name and -s are deliberately
            // excluded: -s creates a LocalSystem token that is not attached to the
            // desktop user's window station, which surfaces as an unusable black window.
            args.AddRange(["-n", timeout.ToString()]);
        }
        else
        {
            args.AddRange(["-n", timeout.ToString()]);
            if (!useDefaultServiceName)
                args.AddRange(["-r", BuildServiceName(targetHost)]);
        }

        // -s forces the child into LocalSystem and discards the selected user
        // context. Never use it for interactive GUI launches: a LocalSystem token
        // is not attached to the target desktop user's window station, so console
        // apps surface as an empty black window and MMC snap-ins fail to initialize.
        if (!hasCredentials && !interactiveSession)
            args.Add("-s");

        if (!string.IsNullOrWhiteSpace(_settings.Settings.PsExecRemoteWorkingDirectory))
        {
            args.Add("-w");
            args.Add(_settings.Settings.PsExecRemoteWorkingDirectory.Trim());
        }

        if (interactiveSession)
        {
            args.Add("-i");
            args.Add(sessionId.ToString());
            args.Add("-d");
        }

        if (shell == CommandShell.PowerShell)
        {
            args.AddRange(BuildPowerShellArguments(command));
        }
        else if (shell == CommandShell.Cmd && wrapCmd)
        {
            // ProcessStartInfo.ArgumentList already handles argument quoting. Do not
            // double percent signs here: in `cmd /c`, that would prevent remote
            // environment variables such as `%ProgramFiles%` from expanding.
            args.AddRange(["cmd", "/c", command]);
        }
        else
        {
            args.AddRange(ProcessHelper.SplitCommandLine(command));
        }
        return args;
    }

    /// <summary>
    /// Resolves the command shape used by an interactive desktop launch.
    /// Interactive mode launches a program; it must not inherit a shell wrapper
    /// merely because the UI currently has PowerShell selected. This is the
    /// service-side guard that prevents `regedit.exe`, `notepad.exe`, MMC and
    /// Control Panel targets from becoming an empty PowerShell host window.
    /// </summary>
    internal static (CommandShell Shell, bool WrapCmd) ResolveInteractiveLaunchShape(
        string command,
        CommandShell requestedShell,
        bool requestedWrapCmd)
    {
        var parts = ProcessHelper.SplitCommandLine(command);
        if (parts.Length == 0)
            return (CommandShell.Direct, false);

        if (TryBuildManagementCommand(parts) is not null)
            return (CommandShell.Direct, false);

        // A command that already names cmd/powershell/pwsh is a program launch,
        // not a script that should be wrapped in another shell.
        if (IsDirectShellEntry(parts[0]))
            return (CommandShell.Direct, false);

        // PowerShell is only selected as a host when the input is recognizably a
        // PowerShell command. Ordinary executable entry points stay direct.
        if (requestedShell == CommandShell.PowerShell && LooksLikePowerShellScript(parts))
            return (CommandShell.PowerShell, true);

        // requestedWrapCmd is deliberately not trusted for interactive launches.
        // Older callers passed true together with the default PowerShell shell,
        // which produced the verified black-window failure mode. Keep the
        // parameter for source compatibility while making the shape deterministic.
        return (CommandShell.Direct, false);
    }

    internal static bool IsDirectShellEntry(string command)
    {
        var parts = ProcessHelper.SplitCommandLine(command);
        if (parts.Length == 0)
            return false;

        var name = Path.GetFileName(parts[0].Trim().Trim('"'));
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }
    private static bool LooksLikePowerShellScript(IReadOnlyList<string> parts)
    {
        var first = parts[0].Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(first))
            return false;

        // A path or a token with a file extension is an executable/program entry,
        // even when PowerShell is currently selected in the UI.
        if (first.Contains('\\') || first.Contains('/') || Path.HasExtension(first))
            return false;

        if (Regex.IsMatch(first, @"^[A-Za-z][A-Za-z0-9]*-[A-Za-z][A-Za-z0-9]*$"))
            return true;

        var command = string.Join(' ', parts);
        return command.Contains('$') ||
               command.Contains("::") ||
               command.Contains("$(") ||
               command.Contains("@(") ||
               command.Contains('|') ||
               command.Contains(';') ||
               command.Contains('`') ||
               command.Contains('{') ||
               command.Contains('}') ||
               command.StartsWith("&", StringComparison.Ordinal) ||
               command.StartsWith(". ", StringComparison.Ordinal);
    }

    public async Task<TransportResult> ExecutePsExecOnlyAsync(
        RemoteCommand command,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(command.TargetHost))
        {
            return TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "PsExec raw-only 通道仅支持远程目标。"));
        }

        var psArgs = BuildArguments(
            command.TargetHost,
            command.Username,
            command.Password,
            command.Command,
            interactiveSession: false,
            sessionId: command.SessionId ?? 0,
            wrapCmd: command.WrapCmd,
            shell: command.Shell,
            useDefaultServiceName: true);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), command.Password);
        if (command.Silent)
            DebugLog($"远程执行通道: PsExec(raw-only) {maskedArgs}");
        else
            _log.Info($"远程执行通道: PsExec(raw-only) {maskedArgs}");

        var result = onOutputLine is null
            ? await RunPsExecOnceAsync(psArgs, command.Username, command.Password, ct, environment: null)
            : await RunPsExecWithOutputOnceAsync(psArgs, command.Username, command.Password, onOutputLine, ct);
        result = NormalizeDetachedLaunchResult(result);
        return CreatePsExecTransportResult(result);
    }

    public async Task<TransportResult> ExecuteWmiOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(command.TargetHost))
        {
            return TransportResult.TransportFailure(
                RemoteTransportKind.WmiDcom,
                CreateWmiTransportFailure("WMI raw-only 通道仅支持远程目标。"));
        }

        if (command.Silent)
            DebugLog($"远程执行通道: WMI/DCOM(raw-only) host={HostHelper.NormalizeHost(command.TargetHost)} shell={command.Shell}");
        else
            _log.Info($"远程执行通道: WMI/DCOM(raw-only) host={HostHelper.NormalizeHost(command.TargetHost)} shell={command.Shell}");
        var result = await ExecuteViaWmiAsync(
            command.TargetHost,
            command.Username,
            command.Password,
            command.Command,
            command.Shell,
            ct);
        return CreateWmiTransportResult(result);
    }

    public async Task<TransportResult> ExecuteInteractivePsExecOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(command.TargetHost))
        {
            return TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "交互 PsExec raw-only 通道仅支持远程目标。"));
        }

        var prepared = await PrepareInteractiveLaunchAsync(command, ct);
        if (!prepared.IsValid)
        {
            return TransportResult.CommandFailure(RemoteTransportKind.PsExec, prepared.Error!);
        }

        var psArgs = BuildArguments(
            command.TargetHost,
            command.Username,
            command.Password,
            prepared.Command,
            interactiveSession: true,
            sessionId: prepared.SessionId,
            wrapCmd: prepared.WrapCmd,
            shell: prepared.Shell);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), command.Password);
        _log.Info($"远程交互执行通道: PsExec(raw-only) {maskedArgs}");

        // One execution shape only. Desktop launches always use the verified
        // RunAs/PSEXESVC layout and never enter the generic recovery chain.
        var result = await RunPsExecInteractiveAsync(
            psArgs, command.Username, command.Password, ct);
        result = NormalizeDetachedLaunchResult(result);
        return CreatePsExecTransportResult(result);
    }

    public async Task<TransportResult> ExecuteInteractiveWmiOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        var prepared = await PrepareInteractiveLaunchAsync(command, ct);
        if (!prepared.IsValid)
            return TransportResult.CommandFailure(RemoteTransportKind.WmiDcom, prepared.Error!);

        _log.Info($"远程交互执行通道: WMI/DCOM 一次性任务(raw-only) host={HostHelper.NormalizeHost(command.TargetHost)} session={prepared.SessionId}");
        var result = await ExecuteInteractiveViaWmiTaskAsync(
            command.TargetHost,
            command.Username,
            command.Password,
            prepared.Command,
            prepared.Shell,
            ct,
            prepared.SessionId,
            prepared.DesktopUsername);
        return CreateWmiTransportResult(result);
    }

    public async Task<TransportResult> ExecuteInteractiveScheduledTaskOnlyAsync(
        RemoteCommand command,
        CancellationToken ct = default)
    {
        if (_taskScheduler is null)
        {
            return TransportResult.TransportFailure(
                RemoteTransportKind.ScheduledTask,
                new CommandResult(-1, string.Empty, "计划任务 RPC 通道未配置。"));
        }

        var prepared = await PrepareInteractiveLaunchAsync(command, ct);
        if (!prepared.IsValid)
            return TransportResult.CommandFailure(RemoteTransportKind.ScheduledTask, prepared.Error!);

        _log.Info($"远程交互执行通道: 计划任务 RPC(raw-only) host={HostHelper.NormalizeHost(command.TargetHost)} session={prepared.SessionId}");
        var result = await ExecuteInteractiveViaSchtasksAsync(
            command.TargetHost,
            command.Username,
            command.Password,
            prepared.Command,
            prepared.Shell,
            ct,
            prepared.SessionId,
            prepared.DesktopUsername);
        return IsSchtasksTransportFailure(result)
            ? TransportResult.TransportFailure(RemoteTransportKind.ScheduledTask, result)
            : result.Success
                ? TransportResult.Ok(RemoteTransportKind.ScheduledTask, result)
                : TransportResult.CommandFailure(RemoteTransportKind.ScheduledTask, result);
    }

    private async Task<PreparedInteractiveLaunch> PrepareInteractiveLaunchAsync(
        RemoteCommand command,
        CancellationToken ct)
    {
        var activeSession = await GetActiveSessionAsync(
            command.TargetHost,
            command.Username,
            command.Password,
            command.SessionId,
            ct);
        var sessionId = command.SessionId ?? activeSession.SessionId;
        if (sessionId <= 0)
        {
            return PreparedInteractiveLaunch.Invalid(new CommandResult(
                -1, string.Empty, "未检测到目标主机活动桌面会话。"));
        }

        var desktopUsername = activeSession.SessionId == sessionId
            ? activeSession.Username
            : string.Empty;
        var managementCommand = TryBuildManagementCommand(ProcessHelper.SplitCommandLine(command.Command));
        var effectiveCommand = managementCommand is { } launch
            ? $"{launch.FileName} {launch.Arguments}".Trim()
            : command.Command;
        var shape = ResolveInteractiveLaunchShape(
            effectiveCommand, command.Shell, command.WrapCmd);

        if (managementCommand is not null)
            _log.Info($"远程管理入口已规范化: {command.Command} -> {effectiveCommand}");

        return PreparedInteractiveLaunch.Valid(
            sessionId, desktopUsername, effectiveCommand, shape.Shell, shape.WrapCmd);
    }

    private static TransportResult CreatePsExecTransportResult(CommandResult result) =>
        IsPsExecTransportFailure(result)
            ? TransportResult.TransportFailure(RemoteTransportKind.PsExec, result)
            : result.Success
                ? TransportResult.Ok(RemoteTransportKind.PsExec, result)
                : TransportResult.CommandFailure(RemoteTransportKind.PsExec, result);

    private static TransportResult CreateWmiTransportResult(CommandResult result) =>
        IsWmiTransportFailure(result)
            ? TransportResult.TransportFailure(RemoteTransportKind.WmiDcom, result)
            : result.Success
                ? TransportResult.Ok(RemoteTransportKind.WmiDcom, result)
                : TransportResult.CommandFailure(RemoteTransportKind.WmiDcom, result);

    private sealed record PreparedInteractiveLaunch(
        bool IsValid,
        int SessionId,
        string DesktopUsername,
        string Command,
        CommandShell Shell,
        bool WrapCmd,
        CommandResult? Error)
    {
        public static PreparedInteractiveLaunch Valid(
            int sessionId, string desktopUsername, string command,
            CommandShell shell, bool wrapCmd) =>
            new(true, sessionId, desktopUsername, command, shell, wrapCmd, null);

        public static PreparedInteractiveLaunch Invalid(CommandResult error) =>
            new(false, 0, string.Empty, string.Empty, CommandShell.Direct, false, error);
    }

    public async Task<CommandResult> ExecuteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        bool interactiveSession = false,
        int? sessionId = null,
        CancellationToken ct = default,
        bool silent = false,
        bool wrapCmd = true,
        CommandShell shell = CommandShell.Cmd)
    {
        if (HostHelper.IsLocalHost(targetHost))
        {
            var (fileName, args) = BuildLocalCommand(command, shell);
            if (IsInteractiveManagementCommand(fileName))
            {
                // Control Panel (.cpl) and MMC (.msc) entry points are GUI programs. They
                // must use the interactive/UAC path instead of a redirected-output
                // process started with an alternate, filtered token.
                return await ExecuteInteractiveLocalAsync(command, username, password, ct, shell, sessionId);
            }
            var hasCredentials = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
            var maskedCommand = CredentialMasker.MaskPasswordInCommand($"{fileName} {args}".Trim(), password);
            CommandResult result;

            _log.IsExecuting = true;
            try
            {
                if (hasCredentials)
                {
                    var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
                    DebugLog($"本机凭据直接执行: {maskedCommand} user={runAsDomain}\\{runAsUser}");
                    if (!silent)
                        _log.Info($"本机执行: {maskedCommand}");
                    result = await ProcessHelper.RunAsync(fileName, args,
                        runAsUser, password, runAsDomain, ct);
                    if (!result.Success && IsPermissionFailure(result))
                    {
                        _log.Warn("本机所选凭据令牌权限不足，切换到当前桌面 UAC；不会使用 PsExec 自连接。");
                        result = await LaunchLocalElevatedAndWaitAsync(fileName, args, ct);
                    }
                }
                else
                {
                    DebugLog($"本地执行: {maskedCommand}");
                    result = await ProcessHelper.RunAsync(fileName, args, ct);
                }
            }
            finally
            {
                _log.IsExecuting = false;
            }

            DebugLog($"本机结果 exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");
            if (!silent && !result.Success)
                _log.Error($"本机命令失败 (exit code: {result.ExitCode})\n{result.StdErr}");
            return result;
        }

        var effectiveSessionId = sessionId ?? 0;
        string? desktopUsername = null;
        if (interactiveSession)
        {
            var activeSession = await GetActiveSessionAsync(
                targetHost, username, password, sessionId, ct);
            if (!activeSession.IsValid)
            {
                const string message = "目标主机未检测到可用的活动桌面会话，已取消交互执行。请刷新会话列表后重试。";
                if (!silent)
                    _log.Error(message);
                return new CommandResult(-1, string.Empty, message);
            }

            effectiveSessionId = activeSession.SessionId;
            desktopUsername = activeSession.Username;
            DebugLog($"交互执行使用活动会话: host={targetHost} session={effectiveSessionId} user={desktopUsername}");
        }

        var preferWmi = ShouldPreferWmiTransport(
            username, password, command, interactiveSession);
        var usedPsExec = false;
        var haveFinalResult = false;
        CommandResult? preferredWmiFailure = null;
        CommandResult psResult = new(-1, string.Empty, "远程命令未执行。");
        _log.IsExecuting = true;
        try
        {
            // Credentialed long commands (especially encoded PowerShell cleanup
            // scripts) are safer through WMI/DCOM: the RunAs launcher PsExec must
            // use (CreateProcessWithLogonW) has a small command-line budget, so an
            // oversized payload could otherwise be truncated before it starts.
            if (preferWmi)
            {
                _log.Info($"远程命令较长，优先使用 WMI/DCOM 安全传输: host={targetHost} commandLength={command.Length}");
                psResult = await ExecuteViaWmiAsync(targetHost, username, password, command, shell, ct);
                haveFinalResult = !IsWmiTransportFailure(psResult);
                if (!haveFinalResult)
                {
                    preferredWmiFailure = psResult;
                    _log.Warn($"目标主机 {targetHost} 的 WMI/DCOM 传输不可用，回退 PsExec 兜底执行；不会因远程命令自身非零退出而重复执行。错误: {SummarizeCommandFailure(psResult)}");
                }
            }

            if (!haveFinalResult)
            {
                var psArgs = BuildArguments(targetHost, username, password, command,
                    interactiveSession, effectiveSessionId, wrapCmd, shell);
                var maskedArgs = CredentialMasker.MaskPasswordInCommand(
                    FormatArgumentsForLog(psArgs), password);
                usedPsExec = true;
                DebugLog($"远程执行通道: PsExec {maskedArgs}");
                if (!silent)
                    _log.Info($"远程执行通道: PsExec {maskedArgs}");

                psResult = await RunPsExecForModeAsync(psArgs, username, password, interactiveSession, ct);
                if (IsPsExecTransportFailure(psResult))
                {
                    usedPsExec = false;
                    _log.Warn($"目标主机 {targetHost} 本次 PsExec 传输失败，自动切换 " +
                        (interactiveSession ? "WMI/DCOM 交互任务" : "WMI/DCOM") + " 执行。");
                    // If WMI was already attempted as the preferred channel,
                    // return that transport error rather than executing a
                    // potentially destructive command a second time.
                    if (preferWmi)
                    {
                        psResult = CombineTransportFailures(
                            "远程命令执行", "WMI/DCOM", preferredWmiFailure, "PsExec", psResult);
                    }
                    else
                    {
                        var fallbackResult = interactiveSession
                            ? await ExecuteInteractiveViaWmiTaskAsync(
                                targetHost, username, password, command, shell, ct,
                                effectiveSessionId, desktopUsername)
                            : await ExecuteViaWmiAsync(targetHost, username, password, command, shell, ct);
                        if (IsWmiTransportFailure(fallbackResult))
                        {
                            var psExecAndWmiFailure = CombineTransportFailures(
                                "远程命令执行", "PsExec", psResult, "WMI/DCOM", fallbackResult);
                            if (interactiveSession && _taskScheduler is not null)
                            {
                                var schtasksResult = await ExecuteInteractiveViaSchtasksAsync(
                                    targetHost, username, password, command, shell, ct,
                                    effectiveSessionId, desktopUsername);
                                if (IsSchtasksTransportFailure(schtasksResult))
                                {
                                    psResult = CombineTransportFailures(
                                        "远程命令执行", "PsExec/WMI", psExecAndWmiFailure,
                                        "计划任务 RPC", schtasksResult);
                                }
                                else
                                {
                                    psResult = schtasksResult;
                                }
                            }
                            else
                            {
                                psResult = psExecAndWmiFailure;
                            }
                        }
                        else
                        {
                            psResult = fallbackResult;
                        }
                        DebugLog($"WMI/DCOM 回退结果: host={targetHost} interactive={interactiveSession} " +
                            $"exit={psResult.ExitCode} stdout={psResult.StdOut} stderr={psResult.StdErr}");
                    }
                }
                haveFinalResult = true;
            }
        }
        finally
        {
            _log.IsExecuting = false;
        }

        // PsExec's -d mode does not return the child process exit code. Depending on
        // the PsExec build, it may return the newly-created remote PID instead (for
        // example 23332) even though the process was started successfully. Treat the
        // documented launch confirmation as success, otherwise interactive callers
        // report a false failure and refresh the software list immediately. WMI's
        // interactive task already returns its own request status and must not be
        // interpreted as a detached PsExec result.
        if (interactiveSession && usedPsExec)
            psResult = NormalizeDetachedLaunchResult(psResult);

        DebugLog($"远程结果 exit={psResult.ExitCode} stdout={psResult.StdOut} stderr={psResult.StdErr}");
        if (!silent)
        {
            if (psResult.Success)
                _log.Info($"远程命令完成 (exit code: {psResult.ExitCode})");
            else
                _log.Error($"远程命令失败 (exit code: {psResult.ExitCode})\n{RemoteErrorClassifier.Explain(psResult.StdErr, psResult.ExitCode)}\n{psResult.StdErr}");
        }
        return psResult;
    }

    private static CommandResult NormalizeDetachedLaunchResult(CommandResult result) =>
        TransportFailureClassifier.NormalizeDetachedLaunchResult(result);

    internal static (string FileName, string Arguments) BuildLocalCommand(string command, CommandShell shell)
    {
        var direct = ProcessHelper.SplitCommandLine(command);
        if (direct.Length == 0)
            return ("cmd.exe", "/d /c exit 0");

        // Resolve shell management entry points before selecting the shell. This
        // keeps appwiz.cpl and compmgmt.msc consistent in CMD, Direct, and
        // PowerShell modes (PowerShell does not perform CMD association lookup).
        var managementCommand = TryBuildManagementCommand(direct);
        if (managementCommand.HasValue)
            return managementCommand.Value;

        if (shell == CommandShell.PowerShell)
            return ("powershell.exe", FormatArgumentString(BuildPowerShellArguments(command).Skip(1).ToArray()));

        if (shell == CommandShell.Direct)
            return (direct[0], FormatArgumentString(direct.Skip(1).ToArray()));

        // Pass the complete command as cmd.exe's single /c argument. Building the
        // Windows command line with the normal argv quoting rules is important
        // here: manually surrounding the command with quotes breaks commands that
        // already begin with a quoted executable path or contain embedded quotes.
        // Percent signs remain intact so cmd.exe can still expand variables such as
        // %ProgramFiles% on the machine where the command runs.
        return ("cmd.exe", FormatArgumentString(["/d", "/s", "/c", command]));
    }

    private static bool IsInteractiveManagementCommand(string fileName) =>
        // All Control Panel and MMC entry points create GUI windows. Never send them
        // through the redirected-output path, otherwise the window can be created in
        // the wrong token/session and report a misleading path/permission error.
        fileName.Equals("control.exe", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("mmc.exe", StringComparison.OrdinalIgnoreCase);

    internal static (string FileName, string Arguments)? TryBuildManagementCommand(
        IReadOnlyList<string> commandArgs)
    {
        if (commandArgs.Count == 0)
            return null;

        var entryPoint = commandArgs[0];
        if (Path.GetFileName(entryPoint).Equals("appwiz.cpl", StringComparison.OrdinalIgnoreCase))
        {
            // appwiz.cpl is a legacy entry point for the Programs and Features
            // Control Panel item. Launch the canonical Control Panel name instead
            // of routing it through rundll32; the latter can make the elevated
            // process resolve the shell namespace as an inaccessible path.
            return ("control.exe", "/name Microsoft.ProgramsAndFeatures");
        }

        if (entryPoint.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
        {
            // Use control.exe for CPL files so Windows resolves the item through
            // its normal Control Panel host rather than rundll32.
            return ("control.exe", FormatArgumentString(commandArgs));
        }

        if (entryPoint.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
            return ("mmc.exe", FormatArgumentString(commandArgs));

        return null;
    }

    private static IReadOnlyList<string> BuildPowerShellArguments(string script)
    {
        var bytes = Encoding.Unicode.GetBytes(script);
        return ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(bytes)];
    }

    private static string FormatArgumentString(IReadOnlyList<string> args) =>
        ProcessHelper.CombineArgumentsForWindows(args);

    public async Task<CommandResult> ExecuteWithOutputAsync(
        string targetHost,
        string username,
        string password,
        string command,
        Action<string> onOutputLine,
        CancellationToken ct = default,
        bool silent = false,
        CommandShell shell = CommandShell.Cmd)
    {
        Action<string> wrappedLine = line =>
        {
            if (DebugMode) _log.Debug($"| {line}");
            onOutputLine(line);
        };

        if (HostHelper.IsLocalHost(targetHost))
        {
            _log.IsExecuting = true;
            try
            {
                var (fileName, args) = BuildLocalCommand(command, shell);
            var maskedCommand = CredentialMasker.MaskPasswordInCommand($"{fileName} {args}".Trim(), password);
            DebugLog($"本机执行(流式): {maskedCommand}");
            if (!silent)
                _log.Info($"本机执行(流式): {maskedCommand}");

            if (IsInteractiveManagementCommand(fileName))
            {
                // Control Panel (.cpl) and MMC (.msc) entry points are GUI programs and
                // need the interactive/UAC path, not redirected output under a
                // filtered alternate-credential token.
                var result = await ExecuteInteractiveLocalAsync(command, username, password, ct, shell);
                if (!result.Success)
                    wrappedLine(ExplainLocalElevationFailure(result));
                return result;
            }

            CommandResult localResult;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
                DebugLog($"本机流式执行使用所选凭据: {runAsDomain}\\{runAsUser}");
                localResult = await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine,
                    runAsUser, password, runAsDomain, ct);
                if (!localResult.Success && IsPermissionFailure(localResult))
                {
                    _log.Warn("本机所选凭据令牌权限不足，流式执行切换到当前桌面 UAC；不会使用 PsExec 自连接。");
                    localResult = await LaunchLocalElevatedAndWaitAsync(fileName, args, ct);
                    ReplayCompletedOutput(localResult, wrappedLine);
                }
            }
            else
            {
                localResult = await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine, ct);
            }

            if (!silent)
            {
                if (localResult.Success)
                    _log.Info($"本机执行(流式)完成: {fileName}");
                else
                    _log.Error($"本机命令失败 (exit code: {localResult.ExitCode})\n{localResult.StdErr}");
            }
            return localResult;
            }
            finally
            {
                _log.IsExecuting = false;
            }
        }

        // GUI management entry points cannot produce useful redirected output on a
        // remote PsExec service (Session 0). Route them to the active desktop session
        // even when the terminal is in its normal, non-interactive mode.
        if (TryBuildManagementCommand(ProcessHelper.SplitCommandLine(command)) != null)
        {
            _log.Info("检测到远程图形管理入口，自动切换到目标主机活动会话交互执行。");
            return await ExecuteInteractiveRemoteAsync(
                targetHost, username, password, command, ct, sessionId: null, shell: shell);
        }

        _log.IsExecuting = true;
        try
        {
        var preferWmi = ShouldPreferWmiTransport(
            username, password, command, interactiveSession: false);
        var haveFinalResult = false;
        CommandResult? preferredWmiFailure = null;
        CommandResult remoteResult = new(-1, string.Empty, "远程命令未执行。");

        if (preferWmi)
        {
            _log.Info($"远程流式命令较长，优先使用 WMI/DCOM 安全传输: host={targetHost} commandLength={command.Length}；输出将在命令结束后返回。");
            remoteResult = await ExecuteViaWmiAsync(targetHost, username, password, command, shell, ct);
            haveFinalResult = !IsWmiTransportFailure(remoteResult);
            if (haveFinalResult)
                ReplayCompletedOutput(remoteResult, wrappedLine);
            else
            {
                preferredWmiFailure = remoteResult;
                _log.Warn($"目标主机 {targetHost} 的 WMI/DCOM 传输不可用，回退 PsExec；不会因远程命令自身非零退出而重复执行。错误: {SummarizeCommandFailure(remoteResult)}");
            }
        }

        if (!haveFinalResult)
        {
            var psArgs = BuildArguments(targetHost, username, password, command, false, 0, true, shell);
            var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
            DebugLog($"远程执行(流式)通道: PsExec {maskedArgs}");
            if (!silent)
                _log.Info($"远程执行(流式)通道: PsExec {maskedArgs}");

            remoteResult = await RunPsExecWithOutputAsync(psArgs, username, password, wrappedLine, ct);
            if (IsPsExecTransportFailure(remoteResult))
            {
                _log.Warn($"目标主机 {targetHost} 本次 PsExec 传输失败，自动切换 WMI/DCOM 命令执行；输出将在命令结束后返回。");
                if (preferWmi)
                {
                    remoteResult = CombineTransportFailures(
                        "远程流式命令执行", "WMI/DCOM", preferredWmiFailure, "PsExec", remoteResult);
                }
                else
                {
                    var fallbackResult = await ExecuteViaWmiAsync(
                        targetHost, username, password, command, shell, ct);
                    remoteResult = IsWmiTransportFailure(fallbackResult)
                        ? CombineTransportFailures(
                            "远程流式命令执行", "PsExec", remoteResult, "WMI/DCOM", fallbackResult)
                        : fallbackResult;
                    // Replay the actual result. When both transports fail, the
                    // combined diagnostics preserve the original PsExec error.
                    ReplayCompletedOutput(remoteResult, wrappedLine);
                }
            }
            haveFinalResult = true;
        }
        if (!silent)
        {
            if (remoteResult.Success)
                _log.Info($"远程执行(流式)完成: {targetHost}");
            else
                _log.Error($"远程命令失败 (exit code: {remoteResult.ExitCode})\n" +
                    $"{RemoteErrorClassifier.Explain(remoteResult.StdErr, remoteResult.ExitCode)}\n{remoteResult.StdErr}");
        }
          return remoteResult;
        }
        finally
        {
            _log.IsExecuting = false;
        }
    }

    public async Task<CommandResult> ExecuteWithOutputElevatedAsync(
        string targetHost,
        string username,
        string password,
        string command,
        Action<string> onOutputLine,
        CancellationToken ct = default,
        bool silent = false,
        CommandShell shell = CommandShell.PowerShell)
    {
        if (!HostHelper.IsLocalHost(targetHost))
            return new CommandResult(1, string.Empty, "ExecuteWithOutputElevatedAsync 仅支持本机目标。");

        var (fileName, arguments) = BuildLocalCommand(command, shell);
        Action<string> wrappedLine = line =>
        {
            if (DebugMode) _log.Debug($"| {line}");
            onOutputLine(line);
        };
        var maskedCommand = CredentialMasker.MaskPasswordInCommand($"{fileName} {arguments}".Trim(), password);
        _log.IsExecuting = true;
        try
        {
            if (IsInteractiveManagementCommand(fileName))
            {
                var guiResult = await ExecuteInteractiveLocalAsync(command, username, password, ct, shell);
                if (!guiResult.Success)
                    wrappedLine(ExplainLocalElevationFailure(guiResult));
                return guiResult;
            }

            CommandResult result;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
                DebugLog($"本机流式管理员执行使用所选凭据: file={fileName} argsLength={arguments.Length} user={runAsDomain}\\{runAsUser}");
                result = await ExecuteLocalWithCredentialsWithOutputAsync(
                    fileName, arguments, username, password, wrappedLine, ct);
                if (!result.Success && IsPermissionFailure(result))
                {
                    _log.Warn("本机所选凭据权限不足，流式输出切换到当前桌面 UAC；不会使用 PsExec 自连接。");
                    result = await LaunchLocalElevatedAndWaitAsync(fileName, arguments, ct);
                    ReplayCompletedOutput(result, wrappedLine);
                }
            }
            else
            {
                _log.Info($"本机管理员执行(流式): {maskedCommand}");
                result = await LaunchLocalElevatedAndWaitAsync(fileName, arguments, ct);
                ReplayCompletedOutput(result, wrappedLine);
            }

            if (!silent && !result.Success)
                _log.Error($"本机管理员命令失败 (exit code: {result.ExitCode})\n{ExplainLocalElevationFailure(result)}");
            return result;
        }
        finally
        {
            _log.IsExecuting = false;
        }
    }

    private async Task<CommandResult> ExecuteLocalWithCredentialsWithOutputAsync(
        string fileName,
        string arguments,
        string username,
        string password,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        var bootstrapArgs = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "&([scriptblock]::Create($env:REMOTEOPSTOOL_LOCAL_BOOTSTRAP))"
        };
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REMOTEOPSTOOL_LOCAL_BOOTSTRAP"] = BuildLocalCommandBootstrapScript(),
            ["REMOTEOPSTOOL_LOCAL_FILE"] = fileName,
            ["REMOTEOPSTOOL_LOCAL_ARGS"] = arguments,
            ["REMOTEOPSTOOL_LOCAL_DIR"] = Environment.SystemDirectory
        };
        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return await ProcessHelper.RunWithOutputAsync(
            powerShellPath, bootstrapArgs, onOutputLine, runAsUser, password,
            runAsDomain, environment, ct);
    }

    public Task<CommandResult> ExecuteInteractiveLocalAsync(string command, string username, string password,
        CancellationToken ct = default, CommandShell shell = CommandShell.Cmd, int? sessionId = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var (effectiveShell, _) = ResolveInteractiveLaunchShape(command, shell, true);
            var (fileName, arguments) = BuildLocalCommand(command, effectiveShell);
            var maskedCommand = CredentialMasker.MaskPasswordInCommand(
                $"{fileName} {arguments}".Trim(), password);
            _log.IsExecuting = true;

            // UAC elevation is bound to the current interactive desktop. Windows does
            // not support safely injecting a stored password into the secure UAC
            // prompt. Starting an alternate-credential bootstrap first creates a
            // filtered token and can fail with "Access is denied". Always request UAC
            // from the current desktop; when required, Windows lets the operator enter
            // administrator credentials in the secure prompt.
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                _log.Info($"本机交互执行: {maskedCommand}");
                _log.Info("本机交互管理员程序将通过当前桌面请求 UAC；已保存凭据不会自动注入 UAC 安全提示。");
            }
            else
            {
                _log.Warn("未提供凭据，将以当前用户请求 UAC 管理员权限启动交互程序。");
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process == null)
            {
                _log.Error("本机交互程序启动失败：Windows 未创建进程。");
                return Task.FromResult(new CommandResult(-1, string.Empty, "Windows 未创建交互进程。"));
            }

            _log.Info("本机交互程序已请求管理员权限启动。");
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }
        catch (OperationCanceledException)
        {
            _log.Warn("本机交互程序启动已取消。");
            return Task.FromResult(new CommandResult(-1, string.Empty, "本机交互程序启动已取消。"));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Warn("用户取消了 UAC 管理员权限确认，程序未启动。");
            return Task.FromResult(new CommandResult(1223, string.Empty, "UAC_CANCELLED"));
        }
        catch (Exception ex)
        {
            _log.Error($"启动本机交互程序失败: {ex.Message}");
            return Task.FromResult(new CommandResult(-1, string.Empty, ex.Message));
        }
        finally { _log.IsExecuting = false; }
    }

    public async Task<CommandResult> ExecuteLocalElevatedAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CancellationToken ct = default,
        CommandShell shell = CommandShell.PowerShell)
    {
        if (!HostHelper.IsLocalHost(targetHost))
        {
            return new CommandResult(1, string.Empty, "ExecuteLocalElevatedAsync 仅支持本机目标。");
        }

        var (fileName, arguments) = BuildLocalCommand(command, shell);
        var maskedCommand = CredentialMasker.MaskPasswordInCommand($"{fileName} {arguments}".Trim(), password);
        _log.IsExecuting = true;
        try
        {
            var identity = string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)
                ? "当前用户"
                : username;
            _log.Info($"本机管理员执行: {maskedCommand} user={identity}");
            CommandResult result;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                // First use the selected account directly. This is the normal path
                // for writable locations such as C:\Temp and avoids the failing
                // CreateProcessWithLogonW + ShellExecute(runas) combination.
                result = await ExecuteLocalWithCredentialsAsync(fileName, arguments, username, password, ct);
                if (!result.Success && IsPermissionFailure(result))
                {
                    // A process started with alternate credentials receives a filtered
                    // token. Do not fall back to local PsExec: it may still try to
                    // install PSEXESVC and fail with "The handle is invalid". Request
                    // elevation from the current interactive desktop instead.
                    _log.Warn("本机所选凭据执行权限不足，正在通过当前桌面请求 UAC 管理员权限。");
                    result = await LaunchLocalElevatedAndWaitAsync(fileName, arguments, ct);
                }
            }
            else
            {
                _log.Info("本机当前用户执行请求 UAC 管理员权限。");
                result = await LaunchLocalElevatedAndWaitAsync(fileName, arguments, ct);
            }

            if (result.Success)
                DebugLog($"本机管理员执行完成: exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");
            else
                _log.Error($"本机管理员执行失败 (exit code: {result.ExitCode})\n{ExplainLocalElevationFailure(result)}");
            return result;
        }
        finally
        {
            _log.IsExecuting = false;
        }
    }

    private async Task<CommandResult> ExecuteLocalWithCredentialsAsync(
        string fileName,
        string arguments,
        string username,
        string password,
        CancellationToken ct)
    {
        // CreateProcessWithLogonW has a short command-line limit. Keep the
        // alternate-credential command short and transfer the real target
        // arguments through the environment.
        var bootstrapArgs = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "&([scriptblock]::Create($env:REMOTEOPSTOOL_LOCAL_BOOTSTRAP))"
        };
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REMOTEOPSTOOL_LOCAL_BOOTSTRAP"] = BuildLocalCommandBootstrapScript(),
            ["REMOTEOPSTOOL_LOCAL_FILE"] = fileName,
            ["REMOTEOPSTOOL_LOCAL_ARGS"] = arguments,
            ["REMOTEOPSTOOL_LOCAL_DIR"] = Environment.SystemDirectory
        };
        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"本机所选凭据直接执行: file={fileName} argsLength={arguments.Length} user={runAsDomain}\\{runAsUser}");
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return await ProcessHelper.RunAsyncWithEnvironment(
            powerShellPath, bootstrapArgs, runAsUser, password, runAsDomain, environment, ct);
    }

    private static string BuildLocalCommandBootstrapScript() =>
        "$ErrorActionPreference='Stop';" +
        "$p=New-Object System.Diagnostics.ProcessStartInfo;" +
        "$p.FileName=$env:REMOTEOPSTOOL_LOCAL_FILE;" +
        "$p.Arguments=$env:REMOTEOPSTOOL_LOCAL_ARGS;" +
        "$p.WorkingDirectory=$env:REMOTEOPSTOOL_LOCAL_DIR;" +
        "$p.UseShellExecute=$false;$p.CreateNoWindow=$true;" +
        "$p.RedirectStandardOutput=$true;$p.RedirectStandardError=$true;" +
        "$x=[System.Diagnostics.Process]::Start($p);" +
        "$o=$x.StandardOutput.ReadToEndAsync();$e=$x.StandardError.ReadToEndAsync();" +
        "$x.WaitForExit();[Console]::Out.Write($o.Result);[Console]::Error.Write($e.Result);exit $x.ExitCode";

    private static bool IsPermissionFailure(CommandResult result)
    {
        var output = $"{result.StdOut}\n{result.StdErr}";
        return result.ExitCode is 5 or 740 ||
               output.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("requires elevation", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("需要提升", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CommandResult> LaunchLocalElevatedAndWaitAsync(
        string fileName,
        string arguments,
        CancellationToken ct)
    {
        // CreateProcessWithLogonW has a short command-line limit. Keep the
        // alternate-credential bootstrap tiny and pass the actual command and
        // bootstrap source through the environment instead. The bootstrap asks
        // UAC to run a second PowerShell process; that elevated process starts
        // the real command directly and captures its output. Do not use
        // `cmd.exe /c ... > ...` here: nested CMD quoting changes commands that
        // contain quotes, metacharacters, or paths ending in a backslash.
        var bootstrapScript = BuildLocalElevationBootstrapScript();
        var childBootstrap = BuildLocalElevatedChildScript();
        var childBootstrapBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(childBootstrap));
        var bootstrapInvoker = "$b=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($env:REMOTEOPSTOOL_ELEVATE_BOOTSTRAP));&([scriptblock]::Create($b))";
        var bootstrapArgs = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta", "-Command", bootstrapInvoker
        };
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REMOTEOPSTOOL_ELEVATE_BOOTSTRAP"] = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrapScript)),
            ["REMOTEOPSTOOL_ELEVATE_CHILD_BOOTSTRAP"] = childBootstrapBase64,
            ["REMOTEOPSTOOL_ELEVATE_FILE"] = fileName,
            ["REMOTEOPSTOOL_ELEVATE_ARGS"] = arguments,
            ["REMOTEOPSTOOL_ELEVATE_DIR"] = Environment.SystemDirectory
        };
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        DebugLog("本机 UAC 引导程序使用当前交互桌面启动。目标参数通过环境变量传递，不经过 cmd.exe 二次解析。");
        return await ProcessHelper.RunAsyncWithEnvironment(
            powerShellPath, bootstrapArgs, string.Empty, string.Empty, null, environment, ct);
    }

    internal static string BuildLocalElevationBootstrapScript() =>
        """
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('RemoteOpsTool-Elevated-' + [Guid]::NewGuid().ToString('N'))
$stdoutPath = Join-Path $root 'stdout.log'
$stderrPath = Join-Path $root 'stderr.log'
$exitCode = 1
$capturedStdOut = ''
$capturedStdErr = ''
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $env:REMOTEOPSTOOL_ELEVATE_STDOUT = $stdoutPath
    $env:REMOTEOPSTOOL_ELEVATE_STDERR = $stderrPath

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $psi.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + $env:REMOTEOPSTOOL_ELEVATE_CHILD_BOOTSTRAP
    $psi.WorkingDirectory = [Environment]::SystemDirectory
    $psi.UseShellExecute = $true
    $psi.Verb = 'runas'
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden

    try {
        $child = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $child) { throw 'Windows 未创建提升进程。' }
        $child.WaitForExit()
        $exitCode = $child.ExitCode
    }
    catch {
        $caught = $_.Exception
        while ($null -ne $caught.InnerException) { $caught = $caught.InnerException }
        if (($caught -is [System.ComponentModel.Win32Exception]) -and $caught.NativeErrorCode -eq 1223) {
            $capturedStdErr = 'UAC_CANCELLED'
            $exitCode = 1223
        }
        else {
            throw
        }
    }

    if (Test-Path -LiteralPath $stdoutPath) {
        $capturedStdOut = [IO.File]::ReadAllText($stdoutPath)
    }
    if (Test-Path -LiteralPath $stderrPath) {
        $capturedStdErr = [IO.File]::ReadAllText($stderrPath)
    }
}
catch {
    $capturedStdErr = $_.Exception.Message
    if ($exitCode -eq 0) { $exitCode = 1 }
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Force -Recurse -ErrorAction SilentlyContinue
    }
}
if (-not [string]::IsNullOrEmpty($capturedStdOut)) { [Console]::Out.Write($capturedStdOut) }
if (-not [string]::IsNullOrEmpty($capturedStdErr)) { [Console]::Error.Write($capturedStdErr) }
exit $exitCode
""";

    internal static string BuildLocalElevatedChildScript() =>
        """
$ErrorActionPreference = 'Stop'
$exitCode = 1
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $env:REMOTEOPSTOOL_ELEVATE_FILE
    $startInfo.Arguments = $env:REMOTEOPSTOOL_ELEVATE_ARGS
    $startInfo.WorkingDirectory = $env:REMOTEOPSTOOL_ELEVATE_DIR
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Windows 未创建提升命令进程。' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($env:REMOTEOPSTOOL_ELEVATE_STDOUT, $stdout, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($env:REMOTEOPSTOOL_ELEVATE_STDERR, $stderr, [Text.Encoding]::UTF8)
    $exitCode = $process.ExitCode
}
catch {
    [IO.File]::WriteAllText($env:REMOTEOPSTOOL_ELEVATE_STDERR, $_.Exception.ToString(), [Text.Encoding]::UTF8)
}
exit $exitCode
""";

    private static string ExplainLocalElevationFailure(CommandResult result)
    {
        var output = $"{result.StdErr}\n{result.StdOut}".Trim();
        if (result.ExitCode == 1223 || output.Contains("UAC_CANCELLED", StringComparison.OrdinalIgnoreCase))
            return "用户取消了 UAC 管理员权限确认，程序未启动。";

        if (output.Contains("user name or password is incorrect", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("用户名或密码不正确", StringComparison.OrdinalIgnoreCase) ||
            result.ExitCode == 1326)
            return "所选凭据的用户名或密码不正确。";

        return string.IsNullOrWhiteSpace(output)
            ? $"Windows 返回错误代码 {result.ExitCode}。"
            : output;
    }
    public async Task<CommandResult> ExecuteInteractiveRemoteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CancellationToken ct = default,
        bool wrapCmd = true,
        int? sessionId = null,
        CommandShell shell = CommandShell.Cmd)
    {
        // Keep this public remote entry point safe even if a caller forgets to
        // branch on the host first. A local computer name must never reach
        // PsExec's remote-service path (which can fail with PSEXESVC handle
        // errors on the same machine).
        if (HostHelper.IsLocalHost(targetHost))
        {
            _log.Debug($"交互执行检测到本机目标，切换本地路径: host={targetHost}");
            return await ExecuteInteractiveLocalAsync(command, username, password, ct, shell, sessionId);
        }

        var activeSession = await GetActiveSessionAsync(targetHost, username, password, sessionId, ct);
        var effectiveSessionId = sessionId ?? activeSession.SessionId;
        if (effectiveSessionId <= 0)
        {
            _log.Error($"目标主机 {targetHost} 未检测到活动登录会话，已取消启动交互程序；不会再使用可能无效的会话 ID 1。");
            return new CommandResult(-1, string.Empty, "未检测到目标主机活动桌面会话。");
        }

        var desktopUsername = activeSession.SessionId == effectiveSessionId
            ? activeSession.Username
            : string.Empty;
        var managementCommand = TryBuildManagementCommand(ProcessHelper.SplitCommandLine(command));
        var effectiveCommand = managementCommand is { } launch
            ? $"{launch.FileName} {launch.Arguments}".Trim()
            : command;
        var (effectiveShell, effectiveWrapCmd) =
            ResolveInteractiveLaunchShape(effectiveCommand, shell, wrapCmd);

        DebugLog($"远程交互程序将发送到会话 ID {effectiveSessionId}: host={targetHost}");
        if (managementCommand is not null)
            _log.Info($"远程管理入口已规范化: {command} -> {effectiveCommand}");
        _log.IsExecuting = true;
        var psExecTransportFailed = false;
        CommandResult result;
        try
        {
            var psArgs = BuildArguments(targetHost, username, password, effectiveCommand, true, effectiveSessionId, effectiveWrapCmd, effectiveShell);
            var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
            _log.Info($"远程交互执行通道: PsExec {maskedArgs}");
            var psExecResult = await RunPsExecInteractiveAsync(psArgs, username, password, ct);
            if (!IsPsExecTransportFailure(psExecResult))
            {
                result = psExecResult;
            }
            else
            {
                psExecTransportFailed = true;
                _log.Warn($"目标主机 {targetHost} 本次 PsExec 传输失败，正在尝试通过 WMI/DCOM 创建一次性高权限交互任务。不会缓存本次失败。");
                var wmiResult = await ExecuteInteractiveViaWmiTaskAsync(
                    targetHost, username, password, effectiveCommand, effectiveShell, ct,
                    effectiveSessionId, desktopUsername);
                if (!IsWmiTransportFailure(wmiResult))
                {
                    // WMI returned a real result. Do not report the PsExec
                    // transport error as the command result or execute it a
                    // third time.
                    psExecTransportFailed = false;
                    result = wmiResult;
                }
                else
                {
                    var psExecAndWmiFailure = CombineTransportFailures(
                        "远程交互命令执行", "PsExec", psExecResult, "WMI/DCOM", wmiResult);
                    if (_taskScheduler is not null)
                    {
                        var schtasksResult = await ExecuteInteractiveViaSchtasksAsync(
                            targetHost, username, password, effectiveCommand, effectiveShell, ct,
                            effectiveSessionId, desktopUsername);
                        if (IsSchtasksTransportFailure(schtasksResult))
                        {
                            result = CombineTransportFailures(
                                "远程交互命令执行", "PsExec/WMI", psExecAndWmiFailure,
                                "计划任务 RPC", schtasksResult);
                        }
                        else
                        {
                            psExecTransportFailed = false;
                            result = schtasksResult;
                        }
                    }
                    else
                    {
                        result = psExecAndWmiFailure;
                    }
                }
            }
        }
        finally
        {
            _log.IsExecuting = false;
        }
        result = NormalizeDetachedLaunchResult(result);

        if (!result.Success)
        {
            _log.Error($"远程交互命令失败\n{RemoteErrorClassifier.Explain(result.StdErr, result.ExitCode)}\n{result.StdErr}");
            if (psExecTransportFailed)
                _log.Warn("PsExec、WMI/DCOM 与计划任务 RPC 交互通道均未成功。请检查 ADMIN$/SCM/RPC、WMI、任务计划程序，以及运维账号是否已登录目标桌面会话。");
        }
        else
        {
            _log.Info("远程交互程序已启动。");
        }

        return result;
    }

    public async Task<int> GetActiveSessionIdAsync(string targetHost, string username, string password,
        CancellationToken ct = default)
    {
        var session = await GetActiveSessionAsync(targetHost, username, password, null, ct);
        return session.SessionId;
    }

    private async Task<ActiveSessionInfo> GetActiveSessionAsync(
        string targetHost,
        string username,
        string password,
        int? preferredSessionId,
        CancellationToken ct)
    {
        if (HostHelper.IsLocalHost(targetHost))
        {
            var explorerSessionIds = new List<int>();
            try
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                {
                    using (process)
                    {
                        try { explorerSessionIds.Add(process.SessionId); }
                        catch { /* The process may exit while the session is read. */ }
                    }
                }
            }
            catch { /* Session enumeration is best effort. */ }

            var sessionId = SelectLocalSessionId(
                explorerSessionIds,
                Process.GetCurrentProcess().SessionId,
                preferredSessionId);
            return sessionId > 0
                ? new ActiveSessionInfo(sessionId, Environment.UserName)
                : ActiveSessionInfo.None;
        }

        // Query Terminal Services RPC directly first. This is much cheaper than
        // installing a PsExec service only to discover the active desktop session.
        var serverArg = $"session /server:{HostHelper.NormalizeHost(targetHost)}";
        var queryPath = Path.Combine(Environment.SystemDirectory, "query.exe");
        CommandResult directResult;
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
        {
            var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
            DebugLog($"活动会话查询使用所选凭据: {runAsDomain}\\{runAsUser} query {serverArg}");
            directResult = await ProcessHelper.RunAsync(
                queryPath, serverArg, runAsUser, password, runAsDomain, ct);
        }
        else
        {
            directResult = await ProcessHelper.RunAsync(queryPath, serverArg, ct);
        }

        DebugLog($"query {serverArg} exit={directResult.ExitCode} stdout={directResult.StdOut} stderr={directResult.StdErr}");
        var directSession = SessionHelper.ParseActiveSession(directResult.StdOut, preferredSessionId);
        if (directSession.IsValid)
        {
            DebugLog($"直接会话输出解析成功: host={targetHost} sessionId={directSession.SessionId} " +
                $"desktopUser={directSession.Username} exit={directResult.ExitCode}");
            return await EnrichRemoteSessionUserAsync(
                targetHost, username, password, directSession, ct);
        }

        // Interactive preparation must never re-enter the generic execution
        // pipeline. Doing so could probe/fall back again and, in the old RunAs
        // recovery path, create an unusable black desktop window. If Terminal
        // Services RPC cannot identify a desktop session, the caller will try
        // the next transport instead of executing an unrelated recovery query.
        _log.Warn($"直接 query {serverArg} 未返回活动会话；交互准备停止，不做隐藏的远程命令回退。" +
            $"directExit={directResult.ExitCode}, error={directResult.StdErr}");
        return ActiveSessionInfo.None;
    }

    /// <summary>
    /// Selects a real interactive local session. Session 1 is not a safe default:
    /// Windows may have no session 1, while the console user may be in session 2+
    /// or the current process may be running in a service session (0).
    /// </summary>
    internal static int SelectLocalSessionId(
        IEnumerable<int> explorerSessionIds,
        int currentSessionId,
        int? preferredSessionId)
    {
        var sessions = explorerSessionIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (preferredSessionId is > 0)
            return sessions.Contains(preferredSessionId.Value) ? preferredSessionId.Value : 0;

        if (currentSessionId > 0)
            return currentSessionId;

        return sessions.FirstOrDefault();
    }

    private async Task<ActiveSessionInfo> EnrichRemoteSessionUserAsync(
        string targetHost,
        string username,
        string password,
        ActiveSessionInfo session,
        CancellationToken ct)
    {
        if (!session.IsValid || IsQualifiedUserName(session.Username))
            return session;

        var owner = await TryResolveExplorerOwnerAsync(targetHost, username, password, session.SessionId, ct);
        if (!string.IsNullOrWhiteSpace(owner))
        {
            DebugLog($"WMI 解析活动桌面用户成功: host={targetHost} sessionId={session.SessionId} desktopUser={owner}");
            return session with { Username = owner };
        }

        DebugLog($"WMI 未能解析活动桌面用户，保留 query session 用户名: host={targetHost} sessionId={session.SessionId} desktopUser={session.Username}");
        return session;
    }

    private async Task<string> TryResolveExplorerOwnerAsync(
        string targetHost,
        string username,
        string password,
        int sessionId,
        CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(targetHost, username, password);
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT Name, SessionId FROM Win32_Process WHERE Name = 'explorer.exe'"));
                using var results = searcher.Get();
                foreach (ManagementObject process in results)
                {
                    using (process)
                    {
                        var processSessionId = RemoteWmiHelper.GetUInt32(process, "SessionId");
                        if (processSessionId != sessionId)
                            continue;

                        using var owner = process.InvokeMethod("GetOwner", null, null);
                        if (owner == null || RemoteWmiHelper.GetUInt32(owner, "ReturnValue") != 0)
                            continue;

                        var domain = RemoteWmiHelper.GetString(owner, "Domain");
                        var user = RemoteWmiHelper.GetString(owner, "User");
                        if (!string.IsNullOrWhiteSpace(user))
                            return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
                    }
                }

                return string.Empty;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog($"WMI 解析 explorer.exe 所有者失败: host={targetHost} sessionId={sessionId} error={ex.Message}");
            return string.Empty;
        }
    }

    private static bool IsQualifiedUserName(string? username) =>
        !string.IsNullOrWhiteSpace(username) &&
        (username.Contains('\\') || username.Contains('@'));

    private async Task<CommandResult> ExecuteInteractiveViaWmiTaskAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CommandShell shell,
        CancellationToken ct,
        int? preferredSessionId = null,
        string? desktopUsername = null)
    {
        if (string.IsNullOrWhiteSpace(desktopUsername))
        {
            var activeSession = await GetActiveSessionAsync(
                targetHost, username, password, preferredSessionId, ct);
            desktopUsername = activeSession.Username;
        }

        var taskUser = ResolveInteractiveTaskUser(desktopUsername, username);
        if (string.IsNullOrWhiteSpace(taskUser))
        {
            return new CommandResult(-1, string.Empty,
                "WMI/DCOM 已连接，但未能确定目标主机活动桌面的用户，无法创建可见的交互任务。");
        }

        var (fileName, arguments) = BuildLocalCommand(command, shell);
        var taskScript = BuildInteractiveTaskScript(fileName, arguments, taskUser, sessionId: preferredSessionId);
        var result = await ExecuteViaWmiAsync(
            targetHost, username, password, taskScript, CommandShell.PowerShell, ct);
        if (result.Success)
            _log.Info("PsExec 不可用，已通过 WMI/DCOM 与一次性计划任务请求在目标用户桌面启动管理员程序。");
        return result;
    }

    private async Task<CommandResult> ExecuteInteractiveViaSchtasksAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CommandShell shell,
        CancellationToken ct,
        int? preferredSessionId = null,
        string? desktopUsername = null)
    {
        if (_taskScheduler is null)
        {
            return new CommandResult(-1, string.Empty,
                "计划任务 RPC 通道未配置。");
        }

        if (string.IsNullOrWhiteSpace(desktopUsername))
        {
            var activeSession = await GetActiveSessionAsync(
                targetHost, username, password, preferredSessionId, ct);
            desktopUsername = activeSession.Username;
        }

        var taskUser = ResolveSchtasksInteractiveRunAsUser(desktopUsername, username, targetHost);
        if (string.IsNullOrWhiteSpace(taskUser))
        {
            return new CommandResult(-1, string.Empty,
                "计划任务 RPC 已连通，但未能确定目标主机活动桌面的用户，无法创建可见的交互任务。");
        }

        var (fileName, arguments) = BuildLocalCommand(command, shell);
        var quotedFileName = ProcessHelper.QuoteArgumentForWindows(fileName);
        var runCommand = string.IsNullOrWhiteSpace(arguments)
            ? quotedFileName
            : $"{quotedFileName} {arguments}";
        var result = await _taskScheduler.ExecuteInteractiveAsync(
            targetHost, username, password, runCommand, taskUser, ct);
        if (result.Success)
        {
            _log.Info("PsExec 与 WMI/DCOM 均不可用，已通过计划任务 RPC 在目标用户桌面请求启动程序。");
        }
        return result;
    }


    internal static string ResolveInteractiveTaskUser(string? desktopUsername, string connectionUsername)
    {
        if (string.IsNullOrWhiteSpace(desktopUsername))
            return string.IsNullOrWhiteSpace(connectionUsername) ? string.Empty : QualifyUserName(connectionUsername);

        var desktopUser = desktopUsername.Trim();
        if (desktopUser.Contains('\\') || desktopUser.Contains('@'))
            return desktopUser;

        var (connectionUser, _) = ProcessHelper.SplitUserDomain(connectionUsername);
        // `query session` normally returns only the logon name (for example
        // "Alice" while the connection credential is "CONTOSO\alice").
        // Do not prepend the connection credential's domain to an unrelated
        // logon name: Task Scheduler would then look up a non-existent account
        // and RunEx can fall back to a filtered token, surfacing console apps
        // as an empty black window. Only preserve the connection credential's
        // qualification when the logon name actually matches that credential.
        if (desktopUser.Equals(connectionUser, StringComparison.OrdinalIgnoreCase))
            return QualifyUserName(connectionUsername);

        return desktopUser;
    }

    internal static string ResolveSchtasksInteractiveRunAsUser(
        string? desktopUsername, string connectionUsername, string targetHost)
    {
        if (string.IsNullOrWhiteSpace(desktopUsername))
            return string.IsNullOrWhiteSpace(connectionUsername) ? string.Empty : QualifyUserName(connectionUsername);

        var desktopUser = desktopUsername.Trim();
        if (desktopUser.Contains('\\') || desktopUser.Contains('@'))
            return desktopUser;

        var (connectionUser, _) = ProcessHelper.SplitUserDomain(connectionUsername);
        if (desktopUser.Equals(connectionUser, StringComparison.OrdinalIgnoreCase))
            return QualifyUserName(connectionUsername);

        // schtasks.exe /S /RU can only run interactively for the /RU account
        // itself; unlike PsExec -i it cannot inject a process into another
        // user's desktop session. A bare logon name that differs from the
        // connection credential is ambiguous (it may be local or a different
        // domain form such as "Alice" vs "alice"), so prefer the known
        // connection credential. Without a credential, qualify with the target
        // computer name; when the target is an IP address no safe qualifier
        // exists and the bare name is returned as-is.
        if (!string.IsNullOrWhiteSpace(connectionUsername))
            return QualifyUserName(connectionUsername);

        var computerName = GetShortComputerName(targetHost);
        return string.IsNullOrWhiteSpace(computerName) ? desktopUser : $"{computerName}\\{desktopUser}";
    }

    private static string GetShortComputerName(string targetHost)
    {
        var normalized = HostHelper.NormalizeHost(targetHost);
        if (string.IsNullOrWhiteSpace(normalized) ||
            IPAddress.TryParse(normalized, out _))
            return string.Empty;

        var firstDot = normalized.IndexOf('.');
        return firstDot > 0 ? normalized[..firstDot] : normalized;
    }

    internal static string BuildInteractiveTaskScript(
        string fileName, string arguments, string username, string? taskId = null, int? sessionId = null)
    {
        var safeTaskId = new string((taskId ?? Guid.NewGuid().ToString("N"))
            .Where(char.IsLetterOrDigit).ToArray());
        if (safeTaskId.Length == 0)
            throw new ArgumentException("Interactive task ID cannot be empty.", nameof(taskId));

        var fileBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(fileName));
        var argumentsBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(arguments));
        var usernameBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(username));
        var sessionIdLiteral = sessionId is > 0
            ? sessionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";
        // When RunEx selects the session user (sessionId > 0) the task
        // principal must not be pinned to a possibly non-existent qualified
        // account; doing so can make Task Scheduler launch the child under a
        // filtered token and surface console apps as an empty black window.
        // For session 0 (Run) keep the principal so the task stays registered
        // with an explicit interactive-token identity.
        var principalUserIdLine = sessionId is > 0
            ? string.Empty
            : "    $definition.Principal.UserId = $userName\n";
        return $$"""
$ErrorActionPreference = 'Stop'
$taskName = 'RemoteOpsTool_Interactive_{{safeTaskId}}'
$fileName = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{fileBase64}}'))
$arguments = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{argumentsBase64}}'))
$userName = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{usernameBase64}}'))
$sessionId = {{sessionIdLiteral}}
$service = $null
$root = $null
try {
    $resolved = (Get-Command -Name $fileName -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $root = $service.GetFolder('\')
    $definition = $service.NewTask(0)
    $definition.RegistrationInfo.Description = 'RemoteOpsTool temporary interactive launch'
    $definition.Settings.Enabled = $true
    $definition.Settings.AllowDemandStart = $true
    $definition.Settings.StartWhenAvailable = $true
    $definition.Settings.ExecutionTimeLimit = 'PT8H'
    $definition.Settings.Hidden = $false
{{principalUserIdLine}}    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 1
    $action = $definition.Actions.Create(0)
    $action.Path = $resolved
    $action.Arguments = $arguments
    $registered = $root.RegisterTaskDefinition($taskName, $definition, 6, $null, $null, 3, $null)
    if ($sessionId -gt 0) {
        $instance = $registered.RunEx($null, 4, $sessionId, $null)
    }
    else {
        $instance = $registered.Run($null)
    }
    if ($null -eq $instance) { throw '任务计划程序未创建运行实例。' }

    # RunEx returns as soon as Task Scheduler accepts the request. Keep the
    # task registered long enough to avoid racing the scheduler on slower hosts,
    # but do not wait the full timeout when the invocation is already queued,
    # running, or has completed quickly (common for Control Panel/MMS snap-ins).
    $requestedAt = Get-Date
    $deadline = $requestedAt.AddSeconds(10)
    $state = 0
    $instanceState = 0
    $lastRunTime = [DateTime]::MinValue
    do {
        Start-Sleep -Milliseconds 200
        try { $instanceState = [int]$instance.State } catch { $instanceState = 0 }
        try { $state = [int]$registered.State } catch { $state = 0 }
        try { $lastRunTime = [DateTime]$registered.LastRunTime } catch { $lastRunTime = [DateTime]::MinValue }
        if ($instanceState -eq 4 -or $instanceState -eq 2) { break } # Running / Queued
        if ($instanceState -eq 3 -and $lastRunTime -ge $requestedAt.AddSeconds(-2)) { break } # Completed quickly
    } while ((Get-Date) -lt $deadline)
    if ($state -eq 1 -or $instanceState -eq 1) { throw '任务计划程序已禁用交互任务。' }
    $runIdentity = if ($sessionId -gt 0) { '已登录会话用户（由 RunEx 自动选择）' } else { $userName }
    Write-Output ('交互程序启动请求已提交: ' + $resolved + '；运行身份: ' + $runIdentity + '；会话 ID: ' + $sessionId + '；任务状态: ' + $state + '；实例状态: ' + $instanceState)
}
finally {
    if ($null -ne $root) {
        try { $root.DeleteTask($taskName, 0) } catch { }
    }
}
""";
    }

    private async Task<CommandResult> ExecuteViaWmiAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CommandShell shell,
        CancellationToken ct)
    {
        return await Task.Run(() => ExecuteViaWmi(targetHost, username, password, command, shell, ct), ct);
    }

    private CommandResult ExecuteViaWmi(
        string targetHost,
        string username,
        string password,
        string command,
        CommandShell shell,
        CancellationToken ct)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var jobSubKey = $"{WmiJobRoot}\\{jobId}";
        ManagementScope? processScope = null;
        ManagementClass? registry = null;
        uint bootstrapProcessId = 0;
        var jobCreated = false;

        try
        {
            ct.ThrowIfCancellationRequested();
            processScope = RemoteWmiHelper.CreateScope(targetHost, username, password);
            var registryScope = RemoteWmiHelper.CreateScope(targetHost, username, password, @"root\default");
            processScope.Connect();
            registryScope.Connect();

            registry = new ManagementClass(registryScope, new ManagementPath("StdRegProv"), null);
            var createKeyCode = CreateRemoteRegistryKey(registry, jobSubKey);
            if (createKeyCode != 0)
                return CreateWmiTransportFailure(
                    $"WMI/DCOM 无法创建命令结果通道（StdRegProv 返回 {createKeyCode}）。",
                    (int)createKeyCode);

            jobCreated = true;
            WriteRemoteRegistryString(registry, jobSubKey, "State", "Pending");
            WriteRemoteRegistryString(registry, jobSubKey, "Shell",
                shell == CommandShell.PowerShell ? "PowerShell" : "Cmd");
            WriteRemoteRegistryString(registry, jobSubKey, "Command",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

            using var processClass = new ManagementClass(processScope, new ManagementPath("Win32_Process"), null);
            using var createParameters = processClass.GetMethodParameters("Create");
            var bootstrap = BuildWmiBootstrapScript(jobId);
            var encodedBootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
            var bootstrapCommand =
                $"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedBootstrap}";
            if (bootstrapCommand.Length > WmiMaxCommandLineLength)
                return CreateWmiTransportFailure(
                    $"WMI/DCOM 引导命令过长（{bootstrapCommand.Length} 字符），已取消执行。");

            createParameters["CommandLine"] = bootstrapCommand;
            createParameters["CurrentDirectory"] = @"C:\Windows\System32";

            DebugLog($"WMI/DCOM 创建远程命令进程: host={targetHost} shell={shell} job={jobId}");
            using var createResult = processClass.InvokeMethod("Create", createParameters, null);
            var createCode = RemoteWmiHelper.GetUInt32(createResult, "ReturnValue");
            bootstrapProcessId = RemoteWmiHelper.GetUInt32(createResult, "ProcessId");
            if (createCode != 0)
                return CreateWmiTransportFailure(ExplainWmiCreateFailure(createCode), (int)createCode);

            var deadline = DateTime.UtcNow + WmiCommandTimeout;
            var startedAt = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var state = ReadRemoteRegistryString(registry, jobSubKey, "State");
                if (state.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    var exitCode = ReadRemoteRegistryDword(registry, jobSubKey, "ExitCode") ?? -1;
                    var stdOut = DecodeWmiResult(ReadRemoteRegistryString(registry, jobSubKey, "StdOut"));
                    var stdErr = DecodeWmiResult(ReadRemoteRegistryString(registry, jobSubKey, "StdErr"));
                    if (state.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var detail = string.IsNullOrWhiteSpace(stdErr)
                            ? "WMI/DCOM 命令引导脚本失败，目标主机未返回错误详情。"
                            : $"WMI/DCOM 命令引导脚本失败: {stdErr}";
                        return CreateWmiTransportFailure(detail, exitCode);
                    }

                    return new CommandResult(exitCode, stdOut, stdErr);
                }

                if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(2) &&
                    !IsRemoteProcessRunning(processScope, bootstrapProcessId))
                {
                    return CreateWmiTransportFailure(
                        $"WMI/DCOM 命令引导进程已提前退出（状态: {state}）。" +
                        "目标机可能禁止远程进程写入 HKLM，或 PowerShell 被应用控制策略拦截。");
                }

                if (ct.WaitHandle.WaitOne(250))
                    ct.ThrowIfCancellationRequested();
            }

            TryTerminateWmiJob(processScope, registry, jobSubKey, bootstrapProcessId);
            return CreateWmiTransportFailure(
                $"WMI/DCOM 命令执行超过 {WmiCommandTimeout.TotalMinutes:0} 分钟，已停止等待。");
        }
        catch (OperationCanceledException)
        {
            if (processScope != null && registry != null)
                TryTerminateWmiJob(processScope, registry, jobSubKey, bootstrapProcessId);
            throw;
        }
        catch (Exception ex)
        {
            if (processScope != null && registry != null)
                TryTerminateWmiJob(processScope, registry, jobSubKey, bootstrapProcessId);
            return CreateWmiTransportFailure($"WMI/DCOM 命令执行失败: {ex.Message}");
        }
        finally
        {
            if (jobCreated && registry != null)
                DeleteRemoteRegistryKey(registry, jobSubKey);
            registry?.Dispose();
        }
    }

    internal static string BuildWmiBootstrapScript(string jobId)
    {
        var safeJobId = new string(jobId.Where(char.IsLetterOrDigit).ToArray());
        if (safeJobId.Length == 0)
            throw new ArgumentException("WMI job ID cannot be empty.", nameof(jobId));

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$jobKey = 'HKLM:\\{WmiJobRoot}\\{safeJobId}'");
        script.AppendLine("$scriptPath = $null");
        script.AppendLine("function Convert-ToResultBase64([string] $value) {");
        script.AppendLine("    if ($null -eq $value) { $value = '' }");
        script.AppendLine("    if ($value.Length -gt 120000) { $value = $value.Substring(0, 120000) + [Environment]::NewLine + '[输出已截断]' }");
        script.AppendLine("    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))");
        script.AppendLine("}");
        script.AppendLine("try {");
        script.AppendLine("    $job = Get-ItemProperty -LiteralPath $jobKey");
        script.AppendLine("    $command = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String([string]$job.Command))");
        script.AppendLine("    $scriptKind = [string]$job.Shell");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name State -Value 'Running' -PropertyType String -Force | Out-Null");
        script.AppendLine("    $extension = if ($scriptKind -eq 'PowerShell') { '.ps1' } else { '.cmd' }");
        script.AppendLine("    $scriptPath = Join-Path $env:windir ('Temp\\RemoteOpsTool_' + [Guid]::NewGuid().ToString('N') + $extension)");
        script.AppendLine("    if ($scriptKind -eq 'PowerShell') {");
        script.AppendLine("        [IO.File]::WriteAllText($scriptPath, $command, [Text.Encoding]::Unicode)");
        script.AppendLine("        $fileName = Join-Path $env:windir 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'");
        script.AppendLine("        $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"' + $scriptPath + '\"'");
        script.AppendLine("    } else {");
        script.AppendLine("        [IO.File]::WriteAllText($scriptPath, $command, [Text.Encoding]::Default)");
        script.AppendLine("        $fileName = $env:ComSpec");
        script.AppendLine("        $arguments = '/d /s /c \"' + $scriptPath + '\"'");
        script.AppendLine("    }");
        script.AppendLine("    $startInfo = New-Object Diagnostics.ProcessStartInfo");
        script.AppendLine("    $startInfo.FileName = $fileName");
        script.AppendLine("    $startInfo.Arguments = $arguments");
        script.AppendLine("    $startInfo.WorkingDirectory = Join-Path $env:windir 'System32'");
        script.AppendLine("    $startInfo.UseShellExecute = $false");
        script.AppendLine("    $startInfo.CreateNoWindow = $true");
        script.AppendLine("    $startInfo.RedirectStandardOutput = $true");
        script.AppendLine("    $startInfo.RedirectStandardError = $true");
        script.AppendLine("    $startInfo.RedirectStandardInput = $true");
        script.AppendLine("    $process = New-Object Diagnostics.Process");
        script.AppendLine("    $process.StartInfo = $startInfo");
        script.AppendLine("    if (-not $process.Start()) { throw 'Windows 未创建命令进程' }");
        script.AppendLine("    try { $process.StandardInput.Close() } catch { }");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name ChildProcessId -Value ([uint32]$process.Id) -PropertyType DWord -Force | Out-Null");
        script.AppendLine("    $stdoutTask = $process.StandardOutput.ReadToEndAsync()");
        script.AppendLine("    $stderrTask = $process.StandardError.ReadToEndAsync()");
        script.AppendLine("    $process.WaitForExit()");
        script.AppendLine("    $stdout = if ($stdoutTask.Wait(5000)) { $stdoutTask.GetAwaiter().GetResult() } else { '[标准输出未在子进程退出后及时关闭]' }");
        script.AppendLine("    $stderr = if ($stderrTask.Wait(5000)) { $stderrTask.GetAwaiter().GetResult() } else { '[标准错误未在子进程退出后及时关闭]' }");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name ExitCode -Value $process.ExitCode -PropertyType DWord -Force | Out-Null");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name StdOut -Value (Convert-ToResultBase64 $stdout) -PropertyType String -Force | Out-Null");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name StdErr -Value (Convert-ToResultBase64 $stderr) -PropertyType String -Force | Out-Null");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name State -Value 'Completed' -PropertyType String -Force | Out-Null");
        script.AppendLine("} catch {");
        script.AppendLine("    try {");
        script.AppendLine("        New-Item -Path $jobKey -Force | Out-Null");
        script.AppendLine("        New-ItemProperty -Path $jobKey -Name ExitCode -Value ([uint32]::MaxValue) -PropertyType DWord -Force | Out-Null");
        script.AppendLine("        New-ItemProperty -Path $jobKey -Name StdOut -Value '' -PropertyType String -Force | Out-Null");
        script.AppendLine("        New-ItemProperty -Path $jobKey -Name StdErr -Value (Convert-ToResultBase64 $_.Exception.Message) -PropertyType String -Force | Out-Null");
        script.AppendLine("        New-ItemProperty -Path $jobKey -Name State -Value 'Failed' -PropertyType String -Force | Out-Null");
        script.AppendLine("    } catch { }");
        script.AppendLine("} finally {");
        script.AppendLine("    if ($scriptPath) { Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue }");
        script.AppendLine("}");
        return script.ToString();
    }

    private static uint CreateRemoteRegistryKey(ManagementClass registry, string subKey)
    {
        using var parameters = registry.GetMethodParameters("CreateKey");
        parameters["hDefKey"] = HkeyLocalMachine;
        parameters["sSubKeyName"] = subKey;
        using var result = registry.InvokeMethod("CreateKey", parameters, null);
        return RemoteWmiHelper.GetUInt32(result, "ReturnValue");
    }

    private static void WriteRemoteRegistryString(
        ManagementClass registry, string subKey, string valueName, string value)
    {
        using var parameters = registry.GetMethodParameters("SetStringValue");
        parameters["hDefKey"] = HkeyLocalMachine;
        parameters["sSubKeyName"] = subKey;
        parameters["sValueName"] = valueName;
        parameters["sValue"] = value;
        using var result = registry.InvokeMethod("SetStringValue", parameters, null);
        var returnCode = RemoteWmiHelper.GetUInt32(result, "ReturnValue");
        if (returnCode != 0)
            throw new InvalidOperationException(
                $"StdRegProv 写入 {valueName} 失败，返回代码 {returnCode}。");
    }

    private static string ReadRemoteRegistryString(ManagementClass registry, string subKey, string valueName)
    {
        using var parameters = registry.GetMethodParameters("GetStringValue");
        parameters["hDefKey"] = HkeyLocalMachine;
        parameters["sSubKeyName"] = subKey;
        parameters["sValueName"] = valueName;
        using var result = registry.InvokeMethod("GetStringValue", parameters, null);
        return RemoteWmiHelper.GetUInt32(result, "ReturnValue") == 0
            ? RemoteWmiHelper.GetString(result, "sValue")
            : string.Empty;
    }

    private static int? ReadRemoteRegistryDword(ManagementClass registry, string subKey, string valueName)
    {
        using var parameters = registry.GetMethodParameters("GetDWORDValue");
        parameters["hDefKey"] = HkeyLocalMachine;
        parameters["sSubKeyName"] = subKey;
        parameters["sValueName"] = valueName;
        using var result = registry.InvokeMethod("GetDWORDValue", parameters, null);
        if (RemoteWmiHelper.GetUInt32(result, "ReturnValue") != 0)
            return null;
        return unchecked((int)RemoteWmiHelper.GetUInt32(result, "uValue"));
    }

    private static void DeleteRemoteRegistryKey(ManagementClass registry, string subKey)
    {
        try
        {
            using var parameters = registry.GetMethodParameters("DeleteKey");
            parameters["hDefKey"] = HkeyLocalMachine;
            parameters["sSubKeyName"] = subKey;
            using var _ = registry.InvokeMethod("DeleteKey", parameters, null);
        }
        catch { }
    }

    private static CommandResult CreateWmiTransportFailure(string message, int exitCode = -1) =>
        TransportFailureClassifier.CreateWmiTransportFailure(message, exitCode);

    private static string DecodeWmiResult(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return value; }
    }

    private static void ReplayCompletedOutput(CommandResult result, Action<string> onOutputLine)
    {
        foreach (var line in SplitOutputLines(result.StdOut))
            onOutputLine(line);
        foreach (var line in SplitOutputLines(result.StdErr))
            onOutputLine(line);
    }

    private static IEnumerable<string> SplitOutputLines(string output) =>
        output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    private static string ExplainWmiCreateFailure(uint returnCode) => returnCode switch
    {
        2 => "WMI Win32_Process.Create 被拒绝访问：所选账号没有远程创建进程权限。",
        3 => "WMI Win32_Process.Create 权限不足。",
        9 => "WMI Win32_Process.Create 找不到远程命令路径。",
        21 => "WMI Win32_Process.Create 参数无效。",
        _ => $"WMI Win32_Process.Create 失败，返回代码 {returnCode}。"
    };

    private static bool IsRemoteProcessRunning(ManagementScope scope, uint processId)
    {
        if (processId == 0)
            return false;
        try
        {
            using var process = new ManagementObject(
                scope, new ManagementPath($"Win32_Process.Handle='{processId}'"), null);
            process.Get();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryTerminateWmiJob(
        ManagementScope scope, ManagementClass registry, string jobSubKey, uint bootstrapProcessId)
    {
        var childProcessId = ReadRemoteRegistryDword(registry, jobSubKey, "ChildProcessId");
        if (childProcessId is > 0)
            TryTerminateRemoteProcess(scope, unchecked((uint)childProcessId.Value));
        TryTerminateRemoteProcess(scope, bootstrapProcessId);
    }

    private static void TryTerminateRemoteProcess(ManagementScope scope, uint processId)
    {
        if (processId == 0)
            return;
        try
        {
            using var process = new ManagementObject(scope, new ManagementPath($"Win32_Process.Handle='{processId}'"), null);
            process.InvokeMethod("Terminate", null);
        }
        catch { }
    }

    private async Task<CommandResult> RunPsExecForModeAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        bool interactiveSession,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        return interactiveSession
            ? await RunPsExecInteractiveAsync(psArgs, username, password, ct)
            : await RunPsExecAsync(psArgs, username, password, ct, environment);
    }

    private async Task<CommandResult> RunPsExecInteractiveAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct)
    {
        // Desktop GUI launch is intentionally a single, fixed attempt. PsExec must
        // authenticate with the explicit -u/-p credentials and launch via RunAs
        // under the selected credential, while keeping the explicit credentials.
        // Never add -s: that mode can report success while creating an unusable
        // LocalSystem black window. Transport failures fall back at the session layer.
        DebugLog("PsExec 交互通道单次直启：显式凭据、默认 PSEXESVC、无 -s，并由所选凭据 RunAs 启动");
        return await RunPsExecOnceAsync(
            psArgs,
            username,
            password,
            ct,
            environment: null);
    }

    private async Task<CommandResult> RunPsExecAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        return await RunPsExecWithRecoveryAsync(
            psArgs, username, password, ct, interactiveSession: false,
            executeOnce: args => RunPsExecOnceAsync(args, username, password, ct, environment));
    }

    private async Task<CommandResult> RunPsExecWithOutputAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        return await RunPsExecWithRecoveryAsync(
            psArgs, username, password, ct, interactiveSession: false,
            executeOnce: args => RunPsExecWithOutputOnceAsync(args, username, password, onOutputLine, ct));
    }

    private async Task<CommandResult> RunPsExecWithRecoveryAsync(
        IReadOnlyList<string> initialArguments,
        string username,
        string password,
        CancellationToken ct,
        bool interactiveSession,
        Func<IReadOnlyList<string>, Task<CommandResult>> executeOnce)
    {
        var attempts = BuildPsExecExecutionAttempts(
            initialArguments, username, password, interactiveSession);
        CommandResult? lastResult = null;

        for (var index = 0; index < attempts.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = attempts[index];
            if (index > 0)
            {
                var maskedArgs = CredentialMasker.MaskPasswordInCommand(
                    FormatArgumentsForLog(attempt.Arguments), password);
                _log.Warn($"PsExec 临时服务启动被拒绝，自动重试：{attempt.Description}");
                DebugLog($"PsExec 重试参数: {maskedArgs}");
            }

            lastResult = await executeOnce(attempt.Arguments);
            if (!IsPsExecServiceStartDenied(lastResult))
                return lastResult;
        }

        return lastResult ?? new CommandResult(-1, string.Empty, "PsExec 未执行。");
    }

    internal static IReadOnlyList<PsExecRecoveryAttempt> BuildPsExecExecutionAttempts(
        IReadOnlyList<string> initialArguments,
        string username,
        string password,
        bool interactiveSession)
    {
        if (interactiveSession)
        {
            // Interactive GUI launch has exactly one valid execution shape.
            // Never generate a stripped-credential RunAs retry in this mode.
            return
            [
                new PsExecRecoveryAttempt(
                    initialArguments.ToArray(),
                    "交互桌面历史验证布局直启（禁止去凭据 RunAs 恢复）")
            ];
        }

        return BuildPsExecRecoveryAttempts(initialArguments, username, password);
    }

    internal static IReadOnlyList<PsExecRecoveryAttempt> BuildPsExecRecoveryAttempts(
        IReadOnlyList<string> initialArguments,
        string username,
        string password)
    {
        // Every credentialed attempt keeps both -u/-p and the selected-user
        // RunAs launcher. Only the service-name layout is allowed to change.
        // Dropping -u/-p would violate the remote authentication contract and
        // can make PsExec start successfully without having target permissions.
        var attempts = new List<PsExecRecoveryAttempt>
        {
            new(initialArguments.ToArray(), "原始参数（RunAs + 显式目标凭据）")
        };

        AddRecoveryAttempt(
            attempts,
            RemovePsExecOption(initialArguments, "-r", hasValue: true),
            "使用所选凭据与默认 PSEXESVC 服务名");

        return attempts;
    }

    private static void AddRecoveryAttempt(
        ICollection<PsExecRecoveryAttempt> attempts,
        IReadOnlyList<string> arguments,
        string description)
    {
        if (attempts.Any(existing => existing.Arguments.SequenceEqual(arguments, StringComparer.OrdinalIgnoreCase)))
            return;
        attempts.Add(new PsExecRecoveryAttempt(arguments, description));
    }

    private static IReadOnlyList<string> RemovePsExecOption(
        IReadOnlyList<string> arguments, string option, bool hasValue)
    {
        var result = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(arguments[index]);
                continue;
            }

            if (hasValue && index + 1 < arguments.Count)
                index++;
        }
        return result;
    }

    internal static bool ShouldPreferWmiTransport(
        string username,
        string password,
        string command,
        bool interactiveSession = false)
    {
        // WMI stores the command payload in a temporary, credential-protected
        // registry job. Keep this optimization for very long payloads, while
        // every actual PsExec attempt still uses RunAs plus explicit -u/-p.
        return !interactiveSession &&
            !string.IsNullOrWhiteSpace(username) &&
            !string.IsNullOrEmpty(password) &&
            !string.IsNullOrEmpty(command) &&
            command.Length > MaxSafeRunAsCommandLength;
    }

    internal static bool IsWmiTransportFailure(CommandResult result) =>
        TransportFailureClassifier.IsWmiTransportFailure(result);

    private static string SummarizeCommandFailure(CommandResult result) =>
        TransportFailureClassifier.SummarizeCommandFailure(result);

    internal static CommandResult CombineTransportFailures(
        string operation,
        string firstChannel,
        CommandResult? first,
        string secondChannel,
        CommandResult second) =>
        TransportFailureClassifier.CombineTransportFailures(
            operation, firstChannel, first, secondChannel, second);

    internal static bool IsPsExecTransportFailure(CommandResult result) =>
        TransportFailureClassifier.IsPsExecTransportFailure(result);

    internal static bool IsSchtasksTransportFailure(CommandResult result) =>
        TaskSchedulerService.IsSchtasksTransportFailure(result);

    internal static bool IsPsExecServiceStartDenied(CommandResult result) =>
        TransportFailureClassifier.IsPsExecServiceStartDenied(result);

    private async Task<CommandResult> RunPsExecOnceAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment,
        bool forceSelectedUserRunAs = false)
    {
        var hasCredentials = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
        var hasExplicitCredentials = HasExplicitPsExecCredentials(psArgs);
        if (!ShouldRunPsExecAsSelectedUser(
                hasCredentials, hasExplicitCredentials, forceSelectedUserRunAs))
        {
            return environment == null
                ? await ProcessHelper.RunAsync(PsExecPath, psArgs, ct)
                : await ProcessHelper.RunAsyncWithEnvironment(PsExecPath, psArgs,
                    string.Empty, string.Empty, null, environment, ct);
        }

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"PsExec 使用所选凭据 RunAs 启动: {runAsDomain}\\{runAsUser}");
        return environment == null
            ? await ProcessHelper.RunAsync(PsExecPath, psArgs, runAsUser, password, runAsDomain, ct)
            : await ProcessHelper.RunAsyncWithEnvironment(PsExecPath, psArgs,
                runAsUser, password, runAsDomain, environment, ct);
    }

    internal static bool ShouldRunPsExecAsSelectedUser(
        bool hasCredentials,
        bool hasExplicitPsExecCredentials,
        bool forceSelectedUserRunAs)
    {
        // Explicit -u/-p and CreateProcessWithLogonW are both required. The
        // explicit arguments authenticate PsExec's remote service connection;
        // RunAs supplies the selected local launcher token and environment.
        // Keep the parameters for source/test compatibility, but never allow
        // explicit credentials to disable RunAs.
        return hasCredentials;
    }

    private async Task<CommandResult> RunPsExecWithOutputOnceAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return await ProcessHelper.RunWithOutputAsync(PsExecPath, psArgs, onOutputLine, ct);

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"PsExec 流式使用所选凭据 RunAs 启动，并传入显式目标凭据: {runAsDomain}\\{runAsUser}");
        return await ProcessHelper.RunWithOutputAsync(
            PsExecPath, psArgs, onOutputLine, runAsUser, password, runAsDomain, ct);
    }

    private static bool HasExplicitPsExecCredentials(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument.Equals("-u", StringComparison.OrdinalIgnoreCase));

    internal sealed record PsExecRecoveryAttempt(IReadOnlyList<string> Arguments, string Description);

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

    private static string QualifyUserName(string username)
    {
        var (user, domain) = ProcessHelper.SplitUserDomain(username);
        if (string.IsNullOrEmpty(domain))
        {
            var currentDomain = Environment.UserDomainName;
            if (!string.IsNullOrEmpty(currentDomain) &&
                !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                domain = currentDomain;
        }

        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private string BuildServiceName(string targetHost)
    {
        var configuredPrefix = string.IsNullOrWhiteSpace(_settings.Settings.PsExecServiceNamePrefix)
            ? "RemoteOpsTool"
            : _settings.Settings.PsExecServiceNamePrefix.Trim();
        var prefix = new string(configuredPrefix.Where(char.IsLetterOrDigit).Take(24).ToArray());
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = "RemoteOpsTool";

        var host = new string(targetHost.Where(char.IsLetterOrDigit).Take(16).ToArray());
        if (string.IsNullOrWhiteSpace(host))
            host = "Host";

        var counter = Interlocked.Increment(ref _serviceCounter) % 10000;
        return $"{prefix}_{host}_{Environment.ProcessId}_{counter:D4}";
    }

    private static string FormatArgumentsForLog(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(QuoteForLog));
    }

    private static string QuoteForLog(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        if (!arg.Any(char.IsWhiteSpace) && !arg.Contains('"'))
            return arg;

        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
