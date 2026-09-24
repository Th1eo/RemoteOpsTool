namespace RemoteOpsTool.Models;

/// <summary>
/// 进程重启结果。失败时 <see cref="Message"/> 为可直接展示给用户的原因说明，
/// 不包含完整命令行等敏感内容。
/// </summary>
public record ProcessRestartResult(bool Success, string Message)
{
    public static ProcessRestartResult Ok(string message) => new(true, message);

    public static ProcessRestartResult Fail(string message) => new(false, message);
}
