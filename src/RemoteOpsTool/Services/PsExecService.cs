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
        bool interactiveSession, int sessionId, bool wrapCmd = true, CommandShell shell = CommandShell.Cmd,
        bool localCredentialExecution = false)
    {
        var args = new List<string>();

        // PsExec has a special local execution path when no computer name is supplied.
        // Supplying \\<this-machine> makes it use the remote-service path even for the
        // local computer. With alternate credentials that path can fail while installing
        // the temporary service ("The handle is invalid") because of the UAC/logon-token
        // boundary. Keep the target omitted for local credential execution so PsExec uses
        // its local child-process path instead of installing a service.
        if (!localCredentialExecution)
            args.Add($"\\\\{targetHost.Trim('\\', ' ')}");

        // For local credential execution the PsExec process itself is already
        // created with the selected account by CreateProcessWithLogonW. Passing
        // the same -u/-p again makes PsExec enter its service/remote path and can
        // fail with "The handle is invalid" on the local machine.
        var includeExplicitCredentials = !localCredentialExecution &&
            !(_settings.Settings.OmitPsExecExplicitCredentialsWhenRunAs
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
        if (!localCredentialExecution)
        {
            var timeout = Math.Clamp(_settings.Settings.PsExecConnectTimeoutSeconds, 3, 60);
            args.AddRange(["-n", timeout.ToString(), "-r", BuildServiceName(targetHost)]);
        }

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
        if (HostHelper.IsLocalHost(targetHost) && (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password)))
        {
            // Do not bypass the selected credentials just because the target name
            // resolves to this computer. PsExec creates the process with the
            // credential's elevated token (-h), which avoids the UAC split-token
            // problem of Process.Start under the current desktop user.
            var localPsArgs = BuildArguments(Environment.MachineName, username, password, command, interactiveSession, sessionId, wrapCmd, shell, localCredentialExecution: true);
            var maskedLocalArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(localPsArgs), password);
            DebugLog($"本机凭据执行: PsExec {maskedLocalArgs}");
            _log.IsExecuting = true;
            var localPsResult = await RunPsExecAsync(localPsArgs, username, password, ct);
            _log.IsExecuting = false;
            if (!silent && !localPsResult.Success)
                _log.Error($"本机命令失败 (exit code: {localPsResult.ExitCode})\n{localPsResult.StdErr}");
            return localPsResult;
        }

        if (HostHelper.IsLocalHost(targetHost))
        {
            var (fileName, args) = BuildLocalCommand(command, shell);
            DebugLog($"本地执行: {fileName} {args}");
            _log.IsExecuting = true;
            var result = await ProcessHelper.RunAsync(fileName, args, ct);
            _log.IsExecuting = false;
            DebugLog($"本地结果 exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");
            if (!silent && !result.Success)
                _log.Error($"本地命令失败 (exit code: {result.ExitCode})\n{result.StdErr}");
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
        if (shell == CommandShell.PowerShell)
            return ("powershell.exe", FormatArgumentString(BuildPowerShellArguments(command).Skip(1).ToArray()));

        if (shell == CommandShell.Direct)
        {
            var direct = ProcessHelper.SplitCommandLine(command);
            if (direct.Length == 0) return ("cmd.exe", "/c exit 0");
            return (direct[0], FormatArgumentString(direct.Skip(1).ToArray()));
        }

        var escapedCmd = command.Replace("%", "%%");
        return ("cmd.exe", $"/c \"{escapedCmd}\"");
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

        if (HostHelper.IsLocalHost(targetHost) && (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password)))
        {
            var localPsArgs = BuildArguments(Environment.MachineName, username, password, command, false, 0, true, shell, localCredentialExecution: true);
            var maskedLocalArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(localPsArgs), password);
            DebugLog($"本机凭据执行(流式): PsExec {maskedLocalArgs}");
            if (!silent) _log.Info($"本机凭据执行(流式): PsExec {maskedLocalArgs}");
            await RunPsExecWithOutputAsync(localPsArgs, username, password, wrappedLine, ct);
            return;
        }

        if (HostHelper.IsLocalHost(targetHost))
        {
            var (fileName, args) = BuildLocalCommand(command, shell);
            DebugLog($"本地执行(流式): {fileName} {args}");
            if (!silent)
                _log.Info($"本地执行(流式): {fileName} {CredentialMasker.MaskPasswordInCommand(args, password)}");
            await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine, ct);
            if (!silent)
                _log.Info($"本地执行(流式)完成: {fileName}");
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

    public async Task ExecuteInteractiveLocalAsync(string command, string username, string password,
        CancellationToken ct = default, CommandShell shell = CommandShell.Cmd, int? sessionId = null)
    {
        try
        {
            var (fileName, arguments) = BuildLocalCommand(command, shell);
            _log.IsExecuting = true;

            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
            {
                // CreateProcessWithLogonW (used by ProcessStartInfo.UserName) always
                // starts with the selected user's filtered/non-elevated token. PsExec
                // then cannot install PSEXESVC and reports "The handle is invalid".
                // Start a small PowerShell bootstrap as the selected user and let that
                // user request elevation through ShellExecute's "runas" verb. This is
                // the supported UAC boundary and keeps the interactive program in the
                // current desktop session without installing a local PsExec service.
                var maskedCommand = CredentialMasker.MaskPasswordInCommand($"{fileName} {arguments}".Trim(), password);
                _log.Info($"本机交互执行(所选凭据 + UAC): {maskedCommand}");
                var result = await LaunchLocalElevatedInteractiveAsync(fileName, arguments, username, password, ct);
                if (result.Success)
                    _log.Info("本机交互程序已通过所选凭据请求管理员权限启动。");
                else
                    _log.Error($"本机交互程序启动失败\n{ExplainLocalElevationFailure(result)}");
                return;
            }

            _log.Warn("未提供凭据，将以当前用户请求 UAC 管理员权限启动交互程序。");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process == null)
                _log.Error("本机交互程序启动失败：Windows 未创建进程。");
            else
                _log.Info("本机交互程序已请求管理员权限启动。");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("本机交互程序启动已取消。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Warn("用户取消了 UAC 管理员权限确认，程序未启动。");
        }
        catch (Exception ex)
        {
            _log.Error($"启动本机交互程序失败: {ex.Message}");
        }
        finally { _log.IsExecuting = false; }
    }

    private async Task<CommandResult> LaunchLocalElevatedInteractiveAsync(
        string fileName,
        string arguments,
        string username,
        string password,
        CancellationToken ct)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Environment.SystemDirectory
        });
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $payloadJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{payload}}'))
            $payload = $payloadJson | ConvertFrom-Json
            $startInfo = New-Object System.Diagnostics.ProcessStartInfo
            $startInfo.FileName = [string]$payload.FileName
            $startInfo.Arguments = [string]$payload.Arguments
            $startInfo.WorkingDirectory = [string]$payload.WorkingDirectory
            $startInfo.UseShellExecute = $true
            $startInfo.Verb = 'runas'
            try {
                $process = [System.Diagnostics.Process]::Start($startInfo)
                if ($null -eq $process) { throw 'Windows did not create the elevated process.' }
                exit 0
            }
            catch [System.ComponentModel.Win32Exception] {
                if ($_.Exception.NativeErrorCode -eq 1223) {
                    [Console]::Error.WriteLine('UAC_CANCELLED')
                    exit 1223
                }
                throw
            }
            """;
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var bootstrapArgs = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta", "-EncodedCommand", encodedScript
        };
        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"本机 UAC 引导程序使用所选凭据启动: {runAsDomain}\\{runAsUser}");
        return await ProcessHelper.RunAsync(
            "powershell.exe", bootstrapArgs, runAsUser, password, runAsDomain, ct);
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
        var effectiveSessionId = sessionId ?? await GetActiveSessionIdAsync(targetHost, username, password, ct);
        if (effectiveSessionId < 0)
        {
            _log.Warn($"目标主机 {targetHost} 当前没有活动登录会话，程序可能无法在桌面显示。");
            effectiveSessionId = 1;
        }
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

        var fallback = await ExecuteAsync(targetHost, username, password, "query session", ct: ct, silent: true);
        if (fallback.Success)
            return SessionHelper.ParseSessionId(fallback.StdOut);

        var serverArg = $"session /server:{targetHost}";
        _log.Info($"PsExec 会话查询失败，回退 query {serverArg}");
        var result = await ProcessHelper.RunAsync("query", serverArg, ct);
        DebugLog($"query {serverArg} exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");
        if (!result.Success)
        {
            _log.Warn($"query session 失败 (exit code: {result.ExitCode}): {result.StdErr}");
            return -1;
        }
        return SessionHelper.ParseSessionId(result.StdOut);
    }

    private async Task<CommandResult> RunPsExecAsync(
        IReadOnlyList<string> psArgs,
        string username,
        string password,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return await ProcessHelper.RunAsync(PsExecPath, psArgs, ct);

        var (runAsUser, runAsDomain) = ResolveRunAsIdentity(username);
        DebugLog($"PsExec 使用所选凭据 RunAs 启动: {runAsDomain}\\{runAsUser}");
        return await ProcessHelper.RunAsync(PsExecPath, psArgs, runAsUser, password, runAsDomain, ct);
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
