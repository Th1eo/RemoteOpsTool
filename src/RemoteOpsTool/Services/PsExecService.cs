using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class PsExecService : IPsExecService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private static int _serviceCounter;
    private const int MaxSafeRunAsCommandLength = 700;
    private const uint HkeyLocalMachine = 0x80000002;
    private const string WmiJobRoot = @"SOFTWARE\RemoteOpsTool\WmiJobs";
    private const int WmiMaxCommandLineLength = 30000;
    private static readonly TimeSpan WmiCommandTimeout = TimeSpan.FromMinutes(15);
    private static readonly Regex DetachedLaunchOutputRegex = new(
        @"\bstarted\b.*\bprocess\s+id\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public PsExecService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
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
        bool interactiveSession, int sessionId, bool wrapCmd = true, CommandShell shell = CommandShell.Cmd)
    {
        var args = new List<string>
        {
            $"\\\\{targetHost.Trim('\\', ' ')}"
        };

        var hasCredentials = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
        // CreateProcessWithLogonW has a small command-line limit. For long encoded
        // PowerShell payloads (notably disk cleanup), let PsExec consume -u/-p itself
        // and launch PsExec under the current process instead of RunAs.
        var requiresExplicitCredentials = command.Length > MaxSafeRunAsCommandLength;
        var includeExplicitCredentials = hasCredentials &&
            (!_settings.Settings.OmitPsExecExplicitCredentialsWhenRunAs || requiresExplicitCredentials);

        if (includeExplicitCredentials)
        {
            args.Add("-u");
            args.Add(QualifyUserName(username));
            args.Add("-p");
            args.Add(password);
        }

        args.AddRange(["-accepteula", "-nobanner", "-h"]);
        var timeout = Math.Clamp(_settings.Settings.PsExecConnectTimeoutSeconds, 3, 60);
        args.AddRange(["-n", timeout.ToString(), "-r", BuildServiceName(targetHost)]);

        // -s forces the child into LocalSystem and discards the selected user context.
        // Only use it when no credential was supplied; with credentials, -h asks PsExec
        // for the elevated administrator token while preserving that user identity.
        if (!hasCredentials)
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

    public async Task<CommandResult> ExecuteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        bool interactiveSession = false,
        int sessionId = 1,
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

        var psArgs = BuildArguments(targetHost, username, password, command, interactiveSession, sessionId, wrapCmd, shell);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
        DebugLog($"远程执行: PsExec {maskedArgs}");
        if (!silent)
            _log.Info($"远程执行: PsExec {maskedArgs}");
        _log.IsExecuting = true;
        CommandResult psResult;
        try
        {
            psResult = await RunPsExecAsync(psArgs, username, password, ct);
            if (!interactiveSession && IsPsExecServiceStartDenied(psResult))
            {
                _log.Warn($"目标主机 {targetHost} 拒绝启动 PsExec 临时服务，自动切换 WMI/DCOM 命令执行。");
                psResult = await ExecuteViaWmiAsync(targetHost, username, password, command, shell, ct);
                DebugLog($"WMI/DCOM 命令结果: host={targetHost} exit={psResult.ExitCode} stdout={psResult.StdOut} stderr={psResult.StdErr}");
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
        // report a false failure and refresh the software list immediately.
        if (interactiveSession)
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

    private static CommandResult NormalizeDetachedLaunchResult(CommandResult result)
    {
        if (result.Success)
            return result;

        var output = $"{result.StdOut}\n{result.StdErr}";
        if (!DetachedLaunchOutputRegex.IsMatch(output))
            return result;

        return result with { ExitCode = 0 };
    }

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

        // Keep percent signs intact. CMD expands variables such as
        // %ProgramFiles%; doubling them here changes the command semantics.
        return ("cmd.exe", $"/d /s /c \"{command}\"");
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
        string.Join(" ", args.Select(arg => arg.Any(char.IsWhiteSpace) ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg));

    public async Task ExecuteWithOutputAsync(
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
                return;
            }

            CommandResult localResult;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
                DebugLog($"本机流式执行使用所选凭据: {runAsDomain}\\{runAsUser}");
                localResult = await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine,
                    runAsUser, password, runAsDomain, ct);
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
            return;
        }

        // GUI management entry points cannot produce useful redirected output on a
        // remote PsExec service (Session 0). Route them to the active desktop session
        // even when the terminal is in its normal, non-interactive mode.
        if (TryBuildManagementCommand(ProcessHelper.SplitCommandLine(command)) != null)
        {
            _log.Info("检测到远程图形管理入口，自动切换到目标主机活动会话交互执行。");
            await ExecuteInteractiveRemoteAsync(targetHost, username, password, command, ct, sessionId: null, shell: shell);
            return;
        }

        var psArgs = BuildArguments(targetHost, username, password, command, false, 0, true, shell);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
        DebugLog($"远程执行(流式): PsExec {maskedArgs}");
        if (!silent)
            _log.Info($"远程执行(流式): PsExec {maskedArgs}");

        var remoteResult = await RunPsExecWithOutputAsync(psArgs, username, password, wrappedLine, ct);
        if (IsPsExecServiceStartDenied(remoteResult))
        {
            _log.Warn($"目标主机 {targetHost} 拒绝启动 PsExec 临时服务，自动切换 WMI/DCOM 命令执行；输出将在命令结束后返回。");
            remoteResult = await ExecuteViaWmiAsync(targetHost, username, password, command, shell, ct);
            foreach (var line in SplitOutputLines(remoteResult.StdOut))
                wrappedLine(line);
            foreach (var line in SplitOutputLines(remoteResult.StdErr))
                wrappedLine(line);
        }
        if (!silent)
        {
            if (remoteResult.Success)
                _log.Info($"远程执行(流式)完成: {targetHost}");
            else
                _log.Error($"远程命令失败 (exit code: {remoteResult.ExitCode})\n" +
                    $"{RemoteErrorClassifier.Explain(remoteResult.StdErr, remoteResult.ExitCode)}\n{remoteResult.StdErr}");
        }
    }

    public Task<CommandResult> ExecuteInteractiveLocalAsync(string command, string username, string password,
        CancellationToken ct = default, CommandShell shell = CommandShell.Cmd, int? sessionId = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var (fileName, arguments) = BuildLocalCommand(command, shell);
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
        // bootstrap source through the environment instead. The bootstrap then
        // asks UAC to run the command elevated, redirects the elevated process'
        // output to temporary files, waits for its real exit code, and relays it.
        const string bootstrapScript = """
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('RemoteOpsTool-Elevated-' + [Guid]::NewGuid().ToString('N'))
$stdoutPath = Join-Path $root 'stdout.log'
$stderrPath = Join-Path $root 'stderr.log'
$exitCode = 1
$capturedStdOut = ''
$capturedStdErr = ''
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $childCommand = '"' + $env:REMOTEOPSTOOL_ELEVATE_FILE + '"'
    if (-not [string]::IsNullOrWhiteSpace($env:REMOTEOPSTOOL_ELEVATE_ARGS)) {
        $childCommand += ' ' + $env:REMOTEOPSTOOL_ELEVATE_ARGS
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec
    $psi.Arguments = '/d /s /c "' + $childCommand + ' > "' + $stdoutPath + '" 2> "' + $stderrPath + '""'
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
        var bootstrapInvoker = "$b=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($env:REMOTEOPSTOOL_ELEVATE_BOOTSTRAP));&([scriptblock]::Create($b))";
        var bootstrapArgs = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta", "-Command", bootstrapInvoker
        };
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REMOTEOPSTOOL_ELEVATE_BOOTSTRAP"] = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrapScript)),
            ["REMOTEOPSTOOL_ELEVATE_FILE"] = fileName,
            ["REMOTEOPSTOOL_ELEVATE_ARGS"] = arguments
        };
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        DebugLog("本机 UAC 引导程序使用当前交互桌面启动。");
        return await ProcessHelper.RunAsyncWithEnvironment(
            powerShellPath, bootstrapArgs, string.Empty, string.Empty, null, environment, ct);
    }

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
    public async Task ExecuteInteractiveRemoteAsync(
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
            await ExecuteInteractiveLocalAsync(command, username, password, ct, shell, sessionId);
            return;
        }

        var effectiveSessionId = sessionId ?? await GetActiveSessionIdAsync(targetHost, username, password, ct);
        if (effectiveSessionId < 0)
        {
            _log.Error($"目标主机 {targetHost} 未检测到活动登录会话，已取消启动交互程序；不会再使用可能无效的会话 ID 1。");
            return;
        }
        var managementCommand = TryBuildManagementCommand(ProcessHelper.SplitCommandLine(command));
        var effectiveCommand = managementCommand is { } launch
            ? $"{launch.FileName} {launch.Arguments}".Trim()
            : command;
        var effectiveShell = managementCommand is not null ? CommandShell.Direct : shell;

        DebugLog($"远程交互程序将发送到会话 ID {effectiveSessionId}: host={targetHost}");
        if (managementCommand is not null)
            _log.Info($"远程管理入口已规范化: {command} -> {effectiveCommand}");
        var psArgs = BuildArguments(targetHost, username, password, effectiveCommand, true, effectiveSessionId, wrapCmd, effectiveShell);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
        _log.Info($"远程交互执行: PsExec {maskedArgs}");
        _log.IsExecuting = true;
        CommandResult result;
        try
        {
            result = await RunPsExecAsync(psArgs, username, password, ct);
            if (IsPsExecServiceStartDenied(result))
            {
                _log.Warn($"目标主机 {targetHost} 拒绝 PsExec 临时服务，正在尝试通过 WMI/DCOM 创建一次性高权限交互任务。");
                result = await ExecuteInteractiveViaWmiTaskAsync(
                    targetHost, username, password, effectiveCommand, effectiveShell, ct);
            }
        }
        finally
        {
            _log.IsExecuting = false;
        }

        if (!result.Success)
        {
            _log.Error($"远程交互命令失败\n{RemoteErrorClassifier.Explain(result.StdErr, result.ExitCode)}\n{result.StdErr}");
            if (IsPsExecServiceStartDenied(result))
                _log.Warn("目标主机同时拒绝 PsExec 和 WMI/DCOM 交互任务。请检查 ADMIN$/SCM/RPC、WMI 与任务计划程序远程管理策略。");
        }
        else
        {
            _log.Info("远程交互程序已启动。");
        }
    }

    public async Task<int> GetActiveSessionIdAsync(string targetHost, string username, string password,
        CancellationToken ct = default)
    {
        if (HostHelper.IsLocalHost(targetHost))
        {
            try
            {
                var proc = Process.GetProcessesByName("explorer").FirstOrDefault();
                return proc?.SessionId ?? 1;
            }
            catch { return 1; }
        }

        var psExecResult = await ExecuteAsync(targetHost, username, password, "query session", ct: ct, silent: true);
        var psExecSessionId = SessionHelper.ParseSessionId(psExecResult.StdOut);
        if (psExecSessionId >= 0)
        {
            // query.exe can return exit code 1 even when it writes a complete session
            // table. The table is authoritative for interactive routing; do not throw
            // away a valid Active session merely because the process exit code is nonzero.
            DebugLog($"PsExec 会话输出解析成功: host={targetHost} sessionId={psExecSessionId} " +
                $"exit={psExecResult.ExitCode}");
            return psExecSessionId;
        }

        var serverArg = $"session /server:{targetHost}";
        _log.Info($"PsExec 会话输出中未找到活动会话，回退 query {serverArg}");
        var directResult = await ProcessHelper.RunAsync("query", serverArg, ct);
        DebugLog($"query {serverArg} exit={directResult.ExitCode} stdout={directResult.StdOut} stderr={directResult.StdErr}");
        var directSessionId = SessionHelper.ParseSessionId(directResult.StdOut);
        if (directSessionId >= 0)
        {
            DebugLog($"直接会话输出解析成功: host={targetHost} sessionId={directSessionId} " +
                $"exit={directResult.ExitCode}");
            return directSessionId;
        }

        _log.Warn($"query session 未返回可用的活动会话 (exit code: {directResult.ExitCode}): " +
            $"{directResult.StdErr}");
        return -1;
    }

    private async Task<CommandResult> ExecuteInteractiveViaWmiTaskAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CommandShell shell,
        CancellationToken ct)
    {
        var (fileName, arguments) = BuildLocalCommand(command, shell);
        var taskScript = BuildInteractiveTaskScript(fileName, arguments, QualifyUserName(username));
        var result = await ExecuteViaWmiAsync(
            targetHost, username, password, taskScript, CommandShell.PowerShell, ct);
        if (result.Success)
            _log.Info("PsExec 不可用，已通过 WMI/DCOM 与一次性计划任务请求在目标用户桌面启动管理员程序。");
        return result;
    }

    internal static string BuildInteractiveTaskScript(
        string fileName, string arguments, string username, string? taskId = null)
    {
        var safeTaskId = new string((taskId ?? Guid.NewGuid().ToString("N"))
            .Where(char.IsLetterOrDigit).ToArray());
        if (safeTaskId.Length == 0)
            throw new ArgumentException("Interactive task ID cannot be empty.", nameof(taskId));

        var fileBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(fileName));
        var argumentsBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(arguments));
        var usernameBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(username));
        return $$"""
$ErrorActionPreference = 'Stop'
$taskName = 'RemoteOpsTool_Interactive_{{safeTaskId}}'
$fileName = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{fileBase64}}'))
$arguments = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{argumentsBase64}}'))
$userName = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{usernameBase64}}'))
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
    $definition.Principal.UserId = $userName
    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 1
    $action = $definition.Actions.Create(0)
    $action.Path = $resolved
    $action.Arguments = $arguments
    $registered = $root.RegisterTaskDefinition($taskName, $definition, 6, $null, $null, 3, $null)
    $instance = $registered.Run($null)
    Start-Sleep -Milliseconds 750
    if ($null -eq $instance) { throw '任务计划程序未创建运行实例。' }
    Write-Output ('交互程序启动请求已提交: ' + $resolved + '；运行身份: ' + $userName)
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
                return new CommandResult((int)createKeyCode, string.Empty,
                    $"WMI/DCOM 无法创建命令结果通道（StdRegProv 返回 {createKeyCode}）。");

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
                return new CommandResult(-1, string.Empty,
                    $"WMI/DCOM 引导命令过长（{bootstrapCommand.Length} 字符），已取消执行。");

            createParameters["CommandLine"] = bootstrapCommand;
            createParameters["CurrentDirectory"] = @"C:\Windows\System32";

            DebugLog($"WMI/DCOM 创建远程命令进程: host={targetHost} shell={shell} job={jobId}");
            using var createResult = processClass.InvokeMethod("Create", createParameters, null);
            var createCode = RemoteWmiHelper.GetUInt32(createResult, "ReturnValue");
            bootstrapProcessId = RemoteWmiHelper.GetUInt32(createResult, "ProcessId");
            if (createCode != 0)
                return new CommandResult((int)createCode, string.Empty, ExplainWmiCreateFailure(createCode));

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
                    return new CommandResult(exitCode, stdOut, stdErr);
                }

                if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(2) &&
                    !IsRemoteProcessRunning(processScope, bootstrapProcessId))
                {
                    return new CommandResult(-1, string.Empty,
                        $"WMI/DCOM 命令引导进程已提前退出（状态: {state}）。" +
                        "目标机可能禁止远程进程写入 HKLM，或 PowerShell 被应用控制策略拦截。");
                }

                if (ct.WaitHandle.WaitOne(250))
                    ct.ThrowIfCancellationRequested();
            }

            TryTerminateWmiJob(processScope, registry, jobSubKey, bootstrapProcessId);
            return new CommandResult(-1, string.Empty,
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
            return new CommandResult(-1, string.Empty, $"WMI/DCOM 命令执行失败: {ex.Message}");
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
        script.AppendLine("    $process = New-Object Diagnostics.Process");
        script.AppendLine("    $process.StartInfo = $startInfo");
        script.AppendLine("    if (-not $process.Start()) { throw 'Windows 未创建命令进程' }");
        script.AppendLine("    New-ItemProperty -Path $jobKey -Name ChildProcessId -Value ([uint32]$process.Id) -PropertyType DWord -Force | Out-Null");
        script.AppendLine("    $stdoutTask = $process.StandardOutput.ReadToEndAsync()");
        script.AppendLine("    $stderrTask = $process.StandardError.ReadToEndAsync()");
        script.AppendLine("    $process.WaitForExit()");
        script.AppendLine("    $stdout = $stdoutTask.GetAwaiter().GetResult()");
        script.AppendLine("    $stderr = $stderrTask.GetAwaiter().GetResult()");
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

    private static string DecodeWmiResult(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return value; }
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

    private async Task<CommandResult> RunPsExecAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        return await RunPsExecWithRecoveryAsync(
            psArgs, username, password, ct,
            args => RunPsExecOnceAsync(args, username, password, ct, environment));
    }

    private async Task<CommandResult> RunPsExecWithOutputAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        return await RunPsExecWithRecoveryAsync(
            psArgs, username, password, ct,
            args => RunPsExecWithOutputOnceAsync(args, username, password, onOutputLine, ct));
    }

    private async Task<CommandResult> RunPsExecWithRecoveryAsync(
        IReadOnlyList<string> initialArguments,
        string username,
        string password,
        CancellationToken ct,
        Func<IReadOnlyList<string>, Task<CommandResult>> executeOnce)
    {
        var attempts = BuildPsExecRecoveryAttempts(initialArguments, username, password);
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

    internal static IReadOnlyList<PsExecRecoveryAttempt> BuildPsExecRecoveryAttempts(
        IReadOnlyList<string> initialArguments,
        string username,
        string password)
    {
        var attempts = new List<PsExecRecoveryAttempt>
        {
            new(initialArguments.ToArray(), "原始参数")
        };

        var hasCredentials = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
        if (hasCredentials && HasExplicitPsExecCredentials(initialArguments) &&
            FormatArgumentsForLog(initialArguments).Length <= MaxSafeRunAsCommandLength)
        {
            var runAsArguments = RemovePsExecOption(
                RemovePsExecOption(initialArguments, "-u", hasValue: true), "-p", hasValue: true);
            AddRecoveryAttempt(attempts, runAsArguments, "以所选凭据 RunAs 启动 PsExec");
            AddRecoveryAttempt(attempts, RemovePsExecOption(runAsArguments, "-r", hasValue: true),
                "以所选凭据 RunAs 并使用默认 PSEXESVC 服务名");
        }
        else
        {
            AddRecoveryAttempt(attempts, RemovePsExecOption(initialArguments, "-r", hasValue: true),
                "使用 PsExec 默认 PSEXESVC 服务名");
        }

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

    internal static bool IsPsExecServiceStartDenied(CommandResult result)
    {
        var output = $"{result.StdOut}\n{result.StdErr}";
        var mentionsServiceFailure = output.Contains("Could not start", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Couldn't install PSEXESVC", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Could not install PSEXESVC", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Error establishing communication with PsExec service", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("无法启动", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("无法安装 PSEXESVC", StringComparison.OrdinalIgnoreCase);
        var mentionsAccessDenied = output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("访问被拒绝", StringComparison.OrdinalIgnoreCase);
        return mentionsServiceFailure && mentionsAccessDenied;
    }

    private async Task<CommandResult> RunPsExecOnceAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || HasExplicitPsExecCredentials(psArgs))
            return environment == null
                ? await ProcessHelper.RunAsync(PsExecPath, psArgs, ct)
                : await ProcessHelper.RunAsyncWithEnvironment(PsExecPath, psArgs,
                    string.Empty, string.Empty, null, environment, ct);

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"PsExec 使用所选凭据 RunAs 启动: {runAsDomain}\\{runAsUser}");
        return environment == null
            ? await ProcessHelper.RunAsync(PsExecPath, psArgs, runAsUser, password, runAsDomain, ct)
            : await ProcessHelper.RunAsyncWithEnvironment(PsExecPath, psArgs,
                runAsUser, password, runAsDomain, environment, ct);
    }

    private async Task<CommandResult> RunPsExecWithOutputOnceAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || HasExplicitPsExecCredentials(psArgs))
            return await ProcessHelper.RunWithOutputAsync(PsExecPath, psArgs, onOutputLine, ct);

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"PsExec 流式使用所选凭据 RunAs 启动: {runAsDomain}\\{runAsUser}");
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
