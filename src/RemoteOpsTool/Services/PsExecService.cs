using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RemoteOpsTool.Helpers;
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
        bool interactiveSession, int sessionId, bool wrapCmd = true)
    {
        var args = new List<string> { $"\\\\{targetHost.Trim('\\', ' ')}" };
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

        var timeout = Math.Clamp(_settings.Settings.PsExecConnectTimeoutSeconds, 3, 60);
        args.AddRange(["-accepteula", "-nobanner", "-n", timeout.ToString(), "-r", BuildServiceName(targetHost), "-h", "-s"]);

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

        if (wrapCmd)
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
        bool wrapCmd = true)
    {
        if (HostHelper.IsLocalHost(targetHost))
        {
            var (fileName, args) = BuildLocalCommand(command);
            DebugLog($"本地执行: {fileName} {args}");
            _log.IsExecuting = true;
            var result = await ProcessHelper.RunAsync(fileName, args, ct);
            _log.IsExecuting = false;
            DebugLog($"本地结果 exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");
            if (!silent && !result.Success)
                _log.Error($"本地命令失败 (exit code: {result.ExitCode})\n{result.StdErr}");
            return result;
        }

        var psArgs = BuildArguments(targetHost, username, password, command, interactiveSession, sessionId, wrapCmd);
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

    private static (string FileName, string Arguments) BuildLocalCommand(string command)
    {
        if (command.StartsWith("powershell ", StringComparison.OrdinalIgnoreCase))
            return ("powershell.exe", command["powershell ".Length..].Trim());

        var escapedCmd = command.Replace("%", "%%");
        return ("cmd.exe", $"/c \"{escapedCmd}\"");
    }

    public async Task ExecuteWithOutputAsync(
        string targetHost,
        string username,
        string password,
        string command,
        Action<string> onOutputLine,
        CancellationToken ct = default,
        bool silent = false)
    {
        Action<string> wrappedLine = line =>
        {
            if (DebugMode) _log.Debug($"| {line}");
            onOutputLine(line);
        };

        if (HostHelper.IsLocalHost(targetHost))
        {
            var (fileName, args) = BuildLocalCommand(command);
            DebugLog($"本地执行(流式): {fileName} {args}");
            if (!silent)
                _log.Info($"本地执行(流式): {fileName} {CredentialMasker.MaskPasswordInCommand(args, password)}");
            await ProcessHelper.RunWithOutputAsync(fileName, args, wrappedLine, ct);
            if (!silent)
                _log.Info($"本地执行(流式)完成: {fileName}");
            return;
        }

        var psArgs = BuildArguments(targetHost, username, password, command, false, 0);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(FormatArgumentsForLog(psArgs), password);
        DebugLog($"远程执行(流式): PsExec {maskedArgs}");
        if (!silent)
            _log.Info($"远程执行(流式): PsExec {maskedArgs}");

        await RunPsExecWithOutputAsync(psArgs, username, password, wrappedLine, ct);
        if (!silent)
            _log.Info($"远程执行(流式)完成: {targetHost}");
    }

    public void ExecuteInteractiveLocal(string command)
    {
        try
        {
            _log.Info($"启动交互程序: {command}");
            _log.IsExecuting = true;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{command}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            });
            _log.Info("交互程序已启动。");
        }
        catch (Exception ex)
        {
            _log.Error($"启动交互程序失败: {ex.Message}");
        }
        finally { _log.IsExecuting = false; }
    }

    public async Task ExecuteInteractiveRemoteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CancellationToken ct = default,
        bool wrapCmd = true,
        int? sessionId = null)
    {
        var effectiveSessionId = sessionId ?? await GetActiveSessionIdAsync(targetHost, username, password, ct);
        if (effectiveSessionId < 0)
        {
            _log.Warn($"目标主机 {targetHost} 当前没有活动登录会话，程序可能无法在桌面显示。");
            effectiveSessionId = 1;
        }
        var psArgs = BuildArguments(targetHost, username, password, command, true, effectiveSessionId, wrapCmd);
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
