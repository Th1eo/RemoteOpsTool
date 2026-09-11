using System.Diagnostics;
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

    private IReadOnlyList<string> BuildArguments(string targetHost, string username, string password, string command,
        bool interactiveSession, int sessionId, bool wrapCmd = true, CommandShell shell = CommandShell.Cmd)
    {
        var args = new List<string>
        {
            $"\\\\{targetHost.Trim('\\', ' ')}"
        };

        var includeExplicitCredentials = !(_settings.Settings.OmitPsExecExplicitCredentialsWhenRunAs
            && !string.IsNullOrWhiteSpace(username)
            && !string.IsNullOrEmpty(password));

        if (!string.IsNullOrEmpty(username) && includeExplicitCredentials)
        {
            args.Add("-u");
            args.Add(QualifyUserName(username));
        }
        if (!string.IsNullOrEmpty(password) && includeExplicitCredentials)
        {
            args.Add("-p");
            args.Add(password);
        }

        args.AddRange(["-accepteula", "-nobanner", "-h"]);
        var timeout = Math.Clamp(_settings.Settings.PsExecConnectTimeoutSeconds, 3, 60);
        args.AddRange(["-n", timeout.ToString(), "-r", BuildServiceName(targetHost)]);

        // -s forces the child into LocalSystem and discards the selected user context.
        // Only use it when no credential was supplied; with credentials, -h asks PsExec
        // for the elevated administrator token while preserving that user identity.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
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
            if (IsElevatedManagementCommand(fileName))
            {
                // MMC snap-ins must be started as interactive processes. Running
                // mmc.exe through the redirected-output path is unelevated and can
                // fail with ERROR_ELEVATION_REQUIRED.
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

        var psResult = await RunPsExecAsync(psArgs, username, password, ct);

        // PsExec's -d mode does not return the child process exit code. Depending on
        // the PsExec build, it may return the newly-created remote PID instead (for
        // example 23332) even though the process was started successfully. Treat the
        // documented launch confirmation as success, otherwise interactive callers
        // report a false failure and refresh the software list immediately.
        if (interactiveSession)
            psResult = NormalizeDetachedLaunchResult(psResult);

        _log.IsExecuting = false;
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

    private static (string FileName, string Arguments) BuildLocalCommand(string command, CommandShell shell)
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

    private static bool IsElevatedManagementCommand(string fileName) =>
        fileName.Equals("mmc.exe", StringComparison.OrdinalIgnoreCase);

    private static (string FileName, string Arguments)? TryBuildManagementCommand(
        IReadOnlyList<string> commandArgs)
    {
        if (commandArgs.Count == 0)
            return null;

        var entryPoint = commandArgs[0];
        if (entryPoint.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
        {
            var cplArgs = new[] { "shell32.dll,Control_RunDLL" }.Concat(commandArgs).ToArray();
            return ("rundll32.exe", FormatArgumentString(cplArgs));
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

            if (IsElevatedManagementCommand(fileName))
            {
                // MMC snap-ins are GUI programs and need the interactive/UAC path,
                // not a redirected-output process started with a filtered token.
                var result = await ExecuteInteractiveLocalAsync(command, username, password, ct, shell);
                if (!result.Success)
                    wrappedLine(ExplainLocalElevationFailure(result));
                return;
            }

            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
                DebugLog($"本机流式执行使用所选凭据: {runAsDomain}\\{runAsUser}");
                await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine,
                    runAsUser, password, runAsDomain, ct);
            }
            else
            {
                await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine, ct);
            }

            if (!silent)
                _log.Info($"本机执行(流式)完成: {fileName}");
            return;
        }

        var psArgs = BuildArguments(targetHost, username, password, command, false, 0, true, shell);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
        DebugLog($"远程执行(流式): PsExec {maskedArgs}");
        if (!silent)
            _log.Info($"远程执行(流式): PsExec {maskedArgs}");

        await RunPsExecWithOutputAsync(psArgs, username, password, wrappedLine, ct);
        if (!silent)
            _log.Info($"远程执行(流式)完成: {targetHost}");
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
        DebugLog($"远程交互程序将发送到会话 ID {effectiveSessionId}: host={targetHost}");
        var psArgs = BuildArguments(targetHost, username, password, command, true, effectiveSessionId, wrapCmd, shell);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
        _log.Info($"远程交互执行: PsExec {maskedArgs}");
        _log.IsExecuting = true;

        var result = await RunPsExecAsync(psArgs, username, password, ct);

        _log.IsExecuting = false;
        if (result.StdErr.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            result.StdErr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            result.StdErr.Contains("Could not start", StringComparison.OrdinalIgnoreCase))
            _log.Error($"远程交互命令失败\n{RemoteErrorClassifier.Explain(result.StdErr, result.ExitCode)}\n{result.StdErr}");
        else
            _log.Info("远程交互程序已启动。");
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

    private async Task<CommandResult> RunPsExecAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
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

    private async Task RunPsExecWithOutputAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            await ProcessHelper.RunWithOutputAsync(PsExecPath, psArgs, onOutputLine, ct);
            return;
        }

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"PsExec 流式使用所选凭据 RunAs 启动: {runAsDomain}\\{runAsUser}");
        await ProcessHelper.RunWithOutputAsync(PsExecPath, psArgs, onOutputLine, runAsUser, password, runAsDomain, ct);
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
