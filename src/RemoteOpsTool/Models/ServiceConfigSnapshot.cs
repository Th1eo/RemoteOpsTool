namespace RemoteOpsTool.Models;

/// <summary>
/// 服务属性快照。加载时记录一次“原始值”，应用修改时再记录一次“目标值”，
/// 两者比较后只提交真正发生变化的配置项，避免无谓的远程命令与副作用。
/// </summary>
public sealed class ServiceConfigSnapshot
{
    public string ServiceName { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;
    public string BinaryPath { get; init; } = string.Empty;
    public string StartParams { get; init; } = string.Empty;
    public bool UseLocalSystem { get; init; } = true;
    public string LogOnAccount { get; init; } = string.Empty;
    public bool AllowDesktopInteract { get; init; }
    public string FirstFailure { get; init; } = "不操作";
    public string SecondFailure { get; init; } = "不操作";
    public string SubsequentFailure { get; init; } = "不操作";
    public string ResetFailDays { get; init; } = "1";
    public string RestartMinutes { get; init; } = "1";
}
