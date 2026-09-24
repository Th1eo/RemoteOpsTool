using RemoteOpsTool.Models;

namespace RemoteOpsTool.Helpers;

internal static class CredentialHealthClassifier
{
    private static readonly HashSet<string> CommandProbeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WMI/DCOM",
        "PsExec 临时执行",
        "计划任务 RPC",
    };

    public static CredentialHealthAssessment Classify(IEnumerable<RemoteCapabilityInfo>? results)
    {
        var probes = results?
            .Where(result => !string.IsNullOrWhiteSpace(result.Name))
            .ToList() ?? [];

        if (probes.Any(IsCommandProbeSuccess))
            return new CredentialHealthAssessment(CredentialHealth.Healthy, string.Empty);

        var failures = probes.Where(result => !result.Success).ToList();
        if (failures.Count == 0)
        {
            return new CredentialHealthAssessment(
                CredentialHealth.TransportUnavailable,
                "能力探测未返回可用的远程命令通道。");
        }

        if (TryFindFailure(failures, out var reauthProbe, "logon failure", "登录失败", "用户名或密码不正确", "user name or password is incorrect", "the specified network password is not correct", "指定的网络密码不正确", "账号或密码错误", "密码不正确", "1326"))
            return new CredentialHealthAssessment(CredentialHealth.NeedsReauth, FormatProbe(reauthProbe));

        if (TryFindFailure(failures, out var localLogonProbe, "unknown error (0xffffffff)", "failed to start process", "createprocesswithlogonw", "logonuser", "runas", "本地启动失败"))
            return new CredentialHealthAssessment(CredentialHealth.LocalLogonBlocked, FormatProbe(localLogonProbe));

        if (TryFindFailure(failures, out var sessionProbe, "1219", "multiple connections", "多重连接", "已用其他用户名连接", "已有到该服务器的 smb 会话"))
            return new CredentialHealthAssessment(CredentialHealth.SessionConflict, FormatProbe(sessionProbe));

        if (TryFindFailure(failures, out var authorizationProbe, "access is denied", "拒绝访问", "访问被拒绝", "1385"))
            return new CredentialHealthAssessment(CredentialHealth.AuthorizationDenied, FormatProbe(authorizationProbe));

        return new CredentialHealthAssessment(
            CredentialHealth.TransportUnavailable,
            FormatProbe(failures[0]));
    }

    private static bool IsCommandProbeSuccess(RemoteCapabilityInfo result) =>
        result.Success && CommandProbeNames.Contains(result.Name);

    private static bool TryFindFailure(
        IEnumerable<RemoteCapabilityInfo> failures,
        out RemoteCapabilityInfo probe,
        params string[] patterns)
    {
        foreach (var candidate in failures)
        {
            if (ContainsAny($"{candidate.Name} {candidate.Detail}", patterns))
            {
                probe = candidate;
                return true;
            }
        }

        probe = null!;
        return false;
    }

    private static bool ContainsAny(string text, params string[] patterns) =>
        patterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static string FormatProbe(RemoteCapabilityInfo probe)
    {
        var text = $"{probe.Name}: {probe.Detail}".Trim();
        return text.Length <= 240 ? text : text[..240] + "...";
    }
}

internal sealed record CredentialHealthAssessment(CredentialHealth Health, string Error);
