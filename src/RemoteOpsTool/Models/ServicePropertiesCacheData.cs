namespace RemoteOpsTool.Models;

/// <summary>
/// 服务属性窗口的短时快照缓存。核心配置先展示，随后仍会做实时校验；
/// 依赖关系和失败恢复策略按 Tab 加载，避免每次打开窗口都发起完整远程探测。
/// </summary>
public sealed class ServicePropertiesCacheData
{
    public string ServiceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BinaryPath { get; set; } = string.Empty;
    public string StartParams { get; set; } = string.Empty;
    public string SelectedStartType { get; set; } = string.Empty;
    public string ServiceStatus { get; set; } = string.Empty;
    public string StatusColor { get; set; } = "#6C6A64";
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public bool CanPause { get; set; }
    public bool CanResume { get; set; }

    public bool UseLocalSystem { get; set; } = true;
    public bool UseThisAccount { get; set; }
    public string LogOnAccount { get; set; } = string.Empty;
    public bool AllowDesktopInteract { get; set; }

    public string FirstFailure { get; set; } = "不操作";
    public string SecondFailure { get; set; } = "不操作";
    public string SubsequentFailure { get; set; } = "不操作";
    public string ResetFailDays { get; set; } = "1";
    public string RestartMinutes { get; set; } = "1";

    public List<string> Dependencies { get; set; } = [];
    public List<string> DependentServices { get; set; } = [];
    public bool HasDependencyData { get; set; }
    public bool HasRecoveryData { get; set; }
    public DateTime CapturedAtUtc { get; set; }
}
