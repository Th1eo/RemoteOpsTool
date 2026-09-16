using System.Text.RegularExpressions;

namespace RemoteOpsTool.Services.Transports;

/// <summary>
/// Centralizes "is this a transport failure" classification so every remote
/// provider uses the same rules instead of duplicating them inside PsExecService.
/// A transport failure means the command never started and another channel may be
/// tried; a command failure (non-zero exit after the command actually ran) must
/// never be retried because that would execute a side-effecting command twice.
/// </summary>
internal static class TransportFailureClassifier
{
    public const string WmiTransportFailureMarker = "[RemoteOpsTool.WmiTransportFailure]";

    private static readonly Regex DetachedLaunchOutputRegex = new(
        @"\bstarted\b.*\bprocess\s+id\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static bool IsPsExecLauncherFailure(CommandResult result) =>
        // ProcessHelper uses -1 only when PsExec itself could not be created.
        result.ExitCode == -1;

    public static bool IsPsExecTransportFailure(CommandResult result) =>
        IsPsExecServiceStartDenied(result) || IsPsExecLauncherFailure(result);

    public static bool IsPsExecServiceStartDenied(CommandResult result)
    {
        var output = $"{result.StdOut}\n{result.StdErr}";
        var hasExplicitFailureVerb = output.Contains("Could not", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Couldn't", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("无法", StringComparison.OrdinalIgnoreCase);
        var hasNamedRemoteOpsService = Regex.IsMatch(
            output, @"RemoteOpsTool_[A-Za-z0-9_.-]+\s+(?:service|服务)",
            RegexOptions.IgnoreCase);
        var hasPsExecServiceMarker = output.Contains("PSEXESVC", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("PsExec service", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("PsExec 服务", StringComparison.OrdinalIgnoreCase) ||
            hasNamedRemoteOpsService;
        var hasServiceOperation = output.Contains("install", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("start", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("communication", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("handle", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("安装", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("启动", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("通信", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("句柄", StringComparison.OrdinalIgnoreCase);
        var mentionsAccessDenied = output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("访问被拒绝", StringComparison.OrdinalIgnoreCase);
        var mentionsInvalidHandle = output.Contains("The handle is invalid", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("句柄无效", StringComparison.OrdinalIgnoreCase);

        // Only classify an access-denied message as a transport failure when the
        // same output identifies PsExec's service/SCM channel. A command's own
        // "Access is denied" must remain a normal command failure.
        return hasPsExecServiceMarker && hasServiceOperation &&
            (mentionsAccessDenied || mentionsInvalidHandle || hasExplicitFailureVerb);
    }

    public static bool IsWmiTransportFailure(CommandResult result)
    {
        if (result.Success)
            return false;

        var output = $"{result.StdOut}\n{result.StdErr}";
        if (output.Contains(WmiTransportFailureMarker, StringComparison.OrdinalIgnoreCase))
            return true;

        // Compatibility for results produced by older builds. Do not classify
        // a bare "Win32_Process.Create"/"StdRegProv" occurrence as transport
        // failure because the remote command may print those words itself.
        if (output.Contains("WMI/DCOM 无法", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("WMI/DCOM 命令执行失败", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("WMI/DCOM 命令引导进程", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("WMI/DCOM 命令执行超过", StringComparison.OrdinalIgnoreCase))
            return true;

        var legacyCreateFailure = result.ExitCode is 2 or 3 or 9 or 21 &&
            result.StdErr.TrimStart().StartsWith(
                "WMI Win32_Process.Create ", StringComparison.OrdinalIgnoreCase);
        if (legacyCreateFailure)
            return true;

        return result.ExitCode == -1 &&
            (output.Contains("WMI 命令执行失败", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("WMI/DCOM 无法连接", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("WMI/DCOM 连接失败", StringComparison.OrdinalIgnoreCase));
    }

    public static CommandResult CreateWmiTransportFailure(string message, int exitCode = -1) =>
        new(exitCode, string.Empty, $"{WmiTransportFailureMarker} {message}");

    public static CommandResult NormalizeDetachedLaunchResult(CommandResult result)
    {
        if (result.Success)
            return result;

        var output = $"{result.StdOut}\n{result.StdErr}";
        if (!DetachedLaunchOutputRegex.IsMatch(output))
            return result;

        return result with { ExitCode = 0 };
    }

    public static string SummarizePsExecFailure(CommandResult result)
    {
        var output = $"{result.StdErr}\n{result.StdOut}".Trim();
        if (output.Length > 180)
            output = output[..180] + "...";
        return string.IsNullOrWhiteSpace(output) ? $"exit={result.ExitCode}" : output.Replace('\r', ' ').Replace('\n', ' ');
    }

    public static string SummarizeCommandFailure(CommandResult result)
    {
        var output = $"{result.StdErr}\n{result.StdOut}".Trim();
        if (output.Length > 240)
            output = output[..240] + "...";
        return string.IsNullOrWhiteSpace(output)
            ? $"exit={result.ExitCode}"
            : output.Replace('\r', ' ').Replace('\n', ' ');
    }

    public static CommandResult CombineTransportFailures(
        string operation,
        string firstChannel,
        CommandResult? first,
        string secondChannel,
        CommandResult second)
    {
        var firstDetail = first is null
            ? "未返回错误详情"
            : SummarizeCommandFailure(first);
        var secondDetail = SummarizeCommandFailure(second);
        var message = $"{operation}失败：两个远程传输通道均不可用。" +
            $"{Environment.NewLine}{firstChannel}: {firstDetail}" +
            $"{Environment.NewLine}{secondChannel}: {secondDetail}";
        return new CommandResult(-1, string.Empty, message);
    }
}
