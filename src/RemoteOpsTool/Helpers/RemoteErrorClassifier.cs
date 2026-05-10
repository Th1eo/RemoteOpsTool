using System.ComponentModel;

namespace RemoteOpsTool.Helpers;

public static class RemoteErrorClassifier
{
    public static string Explain(string? message, int? exitCode = null)
    {
        var text = message ?? string.Empty;

        if (ContainsAny(text, "Access is denied", "拒绝访问", "访问被拒绝"))
            return "访问被拒绝：账号可能不是目标机本地管理员，或目标机的 ADMIN$/SCM/RPC 策略拒绝该令牌。";

        if (ContainsAny(text, "Logon failure", "登录失败", "用户名或密码不正确", "password is incorrect"))
            return "登录失败：用户名、域名或密码不正确。";

        if (ContainsAny(text, "network path was not found", "找不到网络路径", "The network name cannot be found", "网络名不存在"))
            return "网络路径不可达：目标机离线、DNS/主机名解析失败，或 SMB/文件共享被防火墙阻断。";

        if (ContainsAny(text, "The RPC server is unavailable", "RPC 服务器不可用", "RPC server is unavailable"))
            return "RPC 不可用：目标机 RPC/DCOM/SCM 端口被阻断，或相关服务不可达。";

        if (ContainsAny(text, "Multiple connections", "1219", "多重连接", "已用其他用户名连接"))
            return "SMB 凭据冲突：当前 Windows 会话已用其他账号连接过该目标共享，请先断开连接后重试。";

        if (ContainsAny(text, "The directory name is invalid", "目录名称无效"))
            return "工作目录无效：RunAs 启动进程需要使用可访问的工作目录，建议使用 System32。";

        if (exitCode is 5)
            return "错误 5：访问被拒绝。";
        if (exitCode is 53)
            return "错误 53：找不到网络路径。";
        if (exitCode is 67)
            return "错误 67：网络名不存在。";
        if (exitCode is 86 or 1326)
            return "账号或密码错误。";
        if (exitCode is 1219)
            return "错误 1219：已有不同凭据的 SMB 连接。";
        if (exitCode is 1385)
            return "错误 1385：该账号未被授予目标机所需的登录权限。";

        if (exitCode.HasValue)
        {
            try { return new Win32Exception(exitCode.Value).Message; }
            catch { }
        }

        return string.IsNullOrWhiteSpace(text) ? "未知错误。" : text.Trim();
    }

    private static bool ContainsAny(string text, params string[] patterns)
    {
        return patterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
