using System.Diagnostics;
using System.Text;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class PsExecService : IPsExecService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public PsExecService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    private string PsExecPath => Path.Combine(_settings.Settings.PsToolsPath, "PsExec.exe");

    private bool DebugMode => _settings.Settings.DebugMode;

    private void DebugLog(string message)
    {
        if (DebugMode)
            _log.Debug(message);
    }

    private string BuildArguments(string targetHost, string username, string password, string command,
        bool interactiveSession, int sessionId, bool wrapCmd = true)
    {
        var sb = new StringBuilder();
        sb.Append($"\\\\{targetHost.Trim('\\', ' ')} ");
        if (!string.IsNullOrEmpty(username))
        {
            var (user, domain) = ProcessHelper.SplitUserDomain(username);
            if (string.IsNullOrEmpty(domain))
            {
                var currentDomain = Environment.UserDomainName;
                if (!string.IsNullOrEmpty(currentDomain) &&
                    !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    domain = currentDomain;
            }
            var qualifiedUser = string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
            sb.Append($"-u \"{qualifiedUser}\" ");
        }
        if (!string.IsNullOrEmpty(password))
            sb.Append($"-p \"{password}\" ");
        sb.Append("-accepteula -h -s ");
        if (interactiveSession)
            sb.Append($"-i {sessionId} -d ");
        if (wrapCmd)
        {
            var escapedCmd = command.Replace("%", "%%");
            sb.Append($"cmd /c \"{escapedCmd}\"");
        }
        else
        {
            sb.Append(command);
        }
        return sb.ToString();
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
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(psArgs, password);
        DebugLog($"远程执行: PsExec {maskedArgs}");
        if (!silent)
            _log.Info($"远程执行: PsExec {maskedArgs}");
        _log.IsExecuting = true;

        var (runAsUser, runAsDomain) = ProcessHelper.SplitUserDomain(username);
        if (string.IsNullOrEmpty(runAsDomain))
        {
            var currentDomain = Environment.UserDomainName;
            if (!string.IsNullOrEmpty(currentDomain) &&
                !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                runAsDomain = currentDomain;
        }
        var psResult = await ProcessHelper.RunAsync(PsExecPath, psArgs, runAsUser, password, runAsDomain, ct);

        _log.IsExecuting = false;
        DebugLog($"远程结果 exit={psResult.ExitCode} stdout={psResult.StdOut} stderr={psResult.StdErr}");
        if (!silent)
        {
            if (psResult.Success)
                _log.Info($"远程命令完成 (exit code: {psResult.ExitCode})");
            else
                _log.Error($"远程命令失败 (exit code: {psResult.ExitCode})\n{psResult.StdErr}");
        }
        return psResult;
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
            return;
        }

        var psArgs = BuildArguments(targetHost, username, password, command, false, 0);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(psArgs, password);
        DebugLog($"远程执行(流式): PsExec {maskedArgs}");
        if (!silent)
            _log.Info($"远程执行(流式): PsExec {maskedArgs}");

        var (runAsUser, runAsDomain) = ProcessHelper.SplitUserDomain(username);
        if (string.IsNullOrEmpty(runAsDomain))
        {
            var currentDomain = Environment.UserDomainName;
            if (!string.IsNullOrEmpty(currentDomain) &&
                !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                runAsDomain = currentDomain;
        }
        await ProcessHelper.RunWithOutputAsync(PsExecPath, psArgs, wrappedLine, runAsUser, password, runAsDomain, ct);
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

    public async Task ExecuteInteractiveRemoteAsync(string targetHost, string username, string password, string command, CancellationToken ct = default, bool wrapCmd = true)
    {
        var sessionId = await GetActiveSessionIdAsync(targetHost, username, password, ct);
        if (sessionId < 0)
        {
            _log.Warn($"目标主机 {targetHost} 当前没有活动登录会话，程序可能无法在桌面显示。");
            sessionId = 1;
        }
        var psArgs = BuildArguments(targetHost, username, password, command, true, sessionId, wrapCmd);
        var maskedArgs = CredentialMasker.MaskPasswordInCommand(psArgs, password);
        _log.Info($"远程交互执行: PsExec {maskedArgs}");
        _log.IsExecuting = true;

        var (runAsUser, runAsDomain) = ProcessHelper.SplitUserDomain(username);
        if (string.IsNullOrEmpty(runAsDomain))
        {
            var currentDomain = Environment.UserDomainName;
            if (!string.IsNullOrEmpty(currentDomain) &&
                !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                runAsDomain = currentDomain;
        }
        var result = await ProcessHelper.RunAsync(PsExecPath, psArgs, runAsUser, password, runAsDomain, ct);

        _log.IsExecuting = false;
        if (result.StdErr.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            result.StdErr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            result.StdErr.Contains("Could not start", StringComparison.OrdinalIgnoreCase))
            _log.Error($"远程交互命令失败\n{result.StdErr}");
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

        var serverArg = $"session /server:{targetHost}";
        _log.Info($"查询会话: query {serverArg}");
        var result = await ProcessHelper.RunAsync("query", serverArg, ct);
        DebugLog($"query {serverArg} exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");
        if (!result.Success)
        {
            _log.Warn($"query session 失败 (exit code: {result.ExitCode}): {result.StdErr}");
            return -1;
        }
        return SessionHelper.ParseSessionId(result.StdOut);
    }
}
