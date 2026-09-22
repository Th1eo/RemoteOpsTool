using RemoteOpsTool.Models;

namespace RemoteOpsTool.Helpers;

/// <summary>WMI 服务方法调用结果的语义分类。用于区分“已经达到目标状态”和真正失败。</summary>
public enum WmiServiceMethodOutcome
{
    /// <summary>调用成功。</summary>
    Succeeded,

    /// <summary>服务已经处于目标状态（启动时已在运行 / 停止时已停止），视为成功。</summary>
    AlreadyInTargetState,

    /// <summary>服务在目标主机上不存在（1060），不应回退重试。</summary>
    NotFound,

    /// <summary>凭据没有执行该操作的权限（WMI 返回 2），不应再静默回退。</summary>
    AccessDenied,

    /// <summary>操作失败，应把错误回传而不是静默回退。</summary>
    Failed,
}
public enum ServiceMutationKind
{
    /// <summary>启动类型（sc `start=` / WMI `StartMode`）。</summary>
    StartType,

    /// <summary>二进制路径与启动参数（sc `binPath=` / WMI `PathName`）。</summary>
    BinaryPath,

    /// <summary>是否允许服务与桌面交互（sc `type=` / WMI `DesktopInteract`）。</summary>
    Interactive,

    /// <summary>服务登录账户与密码（sc `obj=` / WMI `StartName` + `StartPassword`）。</summary>
    Account,

    /// <summary>服务失败恢复策略（只能走 sc `failure`）。</summary>
    FailureActions,
}

/// <summary>
/// 一条待执行的 sc.exe 命令。<see cref="Command"/> 为真实命令（可能包含密码），
/// <see cref="DisplayCommand"/> 为脱敏后的可记录文本，日志只能使用后者。
/// </summary>
public sealed record ServiceMutationCommand(
    ServiceMutationKind Kind,
    string Command,
    string DisplayCommand);

/// <summary>
/// 一次 Win32_Service.Change 调用的参数集合。<see cref="RequiresScForStartType"/> 为 true 时，
/// StartMode 必须留给 sc 通道执行（延迟启动无法用 WMI Change 表达）。
/// </summary>
public sealed record WmiServiceChangePlan(
    bool RequiresScForStartType,
    IReadOnlyDictionary<string, object?> Parameters)
{
    public bool HasParameters => Parameters.Count > 0;
}

/// <summary>
/// 服务管理相关的纯逻辑：启动类型解析、sc.exe 命令构造与脱敏、错误码识别。
/// 不涉及任何 IO，便于单元测试。
/// </summary>
public static class ServiceCommandHelper
{
    public const string StartTypeAutomatic = "自动";
    public const string StartTypeAutomaticDelayed = "自动(延迟启动)";
    public const string StartTypeManual = "手动";
    public const string StartTypeDisabled = "禁用";

    public const string FailureNone = "不操作";
    public const string FailureRestart = "重新启动服务";
    public const string FailureRunCommand = "运行一个程序";
    public const string FailureReboot = "重新启动计算机";

    /// <summary>sc.exe 中 ERROR_SERVICE_DOES_NOT_EXIST 的数值。</summary>
    public const int ServiceDoesNotExistError = 1060;

    public static string[] UiStartTypes { get; } =
        [StartTypeAutomatic, StartTypeAutomaticDelayed, StartTypeManual, StartTypeDisabled];

    public static string[] UiFailureActions { get; } =
        [FailureNone, FailureRestart, FailureRunCommand, FailureReboot];

    /// <summary>
    /// 解析 `sc qc` 输出的 START_TYPE 值，例如：
    /// `2 AUTO_START` / `2 AUTO_START (DELAYED)` / `3 DEMAND_START` / `4 DISABLED`。
    /// 延迟启动必须优先判断，早期实现按前导数字 5 判断，实际永远匹配不到。
    /// </summary>
    public static string ParseScStartType(string? scValue)
    {
        if (string.IsNullOrWhiteSpace(scValue))
            return StartTypeManual;

        var value = scValue.Trim();
        if (value.Contains("DELAYED", StringComparison.OrdinalIgnoreCase))
            return StartTypeAutomaticDelayed;

        var token = value
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? value;

        return token switch
        {
            "2" => StartTypeAutomatic,
            "3" => StartTypeManual,
            "4" => StartTypeDisabled,
            "5" => StartTypeAutomaticDelayed,
            _ => value.ToUpperInvariant() switch
            {
                var v when v.Contains("AUTO_START") => StartTypeAutomatic,
                var v when v.Contains("DEMAND_START") => StartTypeManual,
                var v when v.Contains("DISABLED") => StartTypeDisabled,
                _ => StartTypeManual,
            },
        };
    }

    /// <summary>由 WMI 的 StartMode + DelayedAutoStart 得到界面启动类型。</summary>
    public static string StartTypeFromWmi(string? startMode, bool delayedAutoStart)
        => startMode?.Trim().ToUpperInvariant() switch
        {
            "AUTO" => delayedAutoStart ? StartTypeAutomaticDelayed : StartTypeAutomatic,
            "MANUAL" => StartTypeManual,
            "DISABLED" => StartTypeDisabled,
            _ => StartTypeManual,
        };

    /// <summary>界面启动类型 → sc.exe `start=` 取值。</summary>
    public static string? ToScStartOption(string? uiStartType) => uiStartType switch
    {
        StartTypeAutomatic => "auto",
        StartTypeAutomaticDelayed => "delayed-auto",
        StartTypeManual => "demand",
        StartTypeDisabled => "disabled",
        _ => null,
    };

    /// <summary>失败操作中文名 → sc.exe `actions=` 取值。</summary>
    public static int ToFailureNumber(string? uiAction) => uiAction switch
    {
        FailureRestart => 1,
        FailureRunCommand => 2,
        FailureReboot => 3,
        _ => 0,
    };

    /// <summary>
    /// 识别“服务不存在”（1060）。sc.exe 退出码本身可能是 1060，也可能只体现在输出文本里，
    /// 因此两种来源都要检查，并兼容中英文提示。
    /// </summary>
    public static bool IsServiceNotInstalled(int exitCode, string? output)
    {
        if (exitCode == ServiceDoesNotExistError)
            return true;

        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains(ServiceDoesNotExistError.ToString(), StringComparison.Ordinal)
            || output.Contains("ERROR_SERVICE_DOES_NOT_EXIST", StringComparison.OrdinalIgnoreCase)
            || output.Contains("does not exist as an installed service", StringComparison.OrdinalIgnoreCase)
            || output.Contains("并未以已安装的服务存在", StringComparison.Ordinal)
            || output.Contains("指定的服务不存在", StringComparison.Ordinal);
    }

    /// <summary>服务不存在时的用户可读提示，替代难懂的 exit=1060。</summary>
    public static string ServiceNotInstalledMessage(string serviceName)
        => $"服务 “{serviceName}” 在目标主机上不存在（错误 1060）。" +
           "该服务可能已被卸载、重命名，或列表缓存已过期，请刷新服务列表后重试。";

    /// <summary>把参数包成 sc.exe 期望的双引号形式。</summary>
    public static string QuoteScValue(string? value) => $"\"{value ?? string.Empty}\"";

    /// <summary>
    /// 校验登录页输入。返回 null 表示通过，否则返回面向用户的错误提示。
    /// <paramref name="passwordRequired"/> 为 false 时（账户未变更）允许留空密码，
    /// 表示沿用目标主机上已有的服务密码，避免仅改启动类型也要重输密码。
    /// </summary>
    public static string? ValidateLogOnInput(bool useLocalSystem, string? logOnAccount, string? password,
        string? confirmPassword, bool passwordRequired = true)
    {
        if (useLocalSystem)
            return null;

        if (string.IsNullOrWhiteSpace(logOnAccount))
            return "请填写服务登录账户。";

        if (logOnAccount.Contains('"', StringComparison.Ordinal))
            return "服务登录账户不能包含双引号。";

        if (!passwordRequired && string.IsNullOrEmpty(password) && string.IsNullOrEmpty(confirmPassword))
            return null;

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            return "两次输入的密码不一致。";

        if (string.IsNullOrEmpty(password))
            return "切换服务登录账户时必须填写密码。";

        return null;
    }

    /// <summary>WMI 服务方法（StartService/StopService 等）返回值的结构化分类。</summary>
    public static WmiServiceMethodOutcome MapWmiMethodReturnCode(string methodName, uint returnCode)
        => returnCode switch
        {
            0 => WmiServiceMethodOutcome.Succeeded,
            // Win32_Service 通用返回码 2 才是拒绝访问。
            2 => WmiServiceMethodOutcome.AccessDenied,
            // 服务已经在运行：启动本身幂等，视为成功，不要再用 sc 重复启动。
            10 when methodName.Equals("StartService", StringComparison.OrdinalIgnoreCase)
                => WmiServiceMethodOutcome.AlreadyInTargetState,
            // StopService 返回 5 表示服务当前未运行，停止目标已经达成。
            5 when methodName.Equals("StopService", StringComparison.OrdinalIgnoreCase)
                => WmiServiceMethodOutcome.AlreadyInTargetState,
            1060 => WmiServiceMethodOutcome.NotFound,
            _ => WmiServiceMethodOutcome.Failed,
        };

    /// <summary>
    /// 判断密码能否安全地放进 `cmd /c sc.exe ...` 命令行。
    /// 双引号会破坏参数边界；百分号会被 cmd.exe 当作环境变量展开。
    /// 两者都无法在命令行层转义，必须改用其它通道传入。
    /// </summary>
    public static bool IsPasswordCommandLineSafe(string? password, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrEmpty(password))
            return true;

        if (password.Contains('"'))
        {
            reason = "服务账户密码包含双引号 (\")，无法通过 sc.exe 命令行安全传递。";
            return false;
        }

        if (password.Contains('%'))
        {
            reason = "服务账户密码包含百分号 (%)，cmd.exe 会把它当作环境变量展开。";
            return false;
        }

        return true;
    }

    /// <summary>把二进制路径与启动参数还原为 sc.exe binPath= 需要的形式。</summary>
    public static string ComposePathName(string? binaryPath, string? startParams)
    {
        var path = (binaryPath ?? string.Empty).Trim();
        if (path.Length == 0)
            return string.Empty;

        if (path.Contains(' ') && !(path.StartsWith('"') && path.EndsWith('"')))
            path = $"\"{path}\"";

        var parameters = (startParams ?? string.Empty).Trim();
        return parameters.Length == 0 ? path : $"{path} {parameters}";
    }

    /// <summary>
    /// 比较“原始快照/目标快照”，生成真正需要执行的 sc.exe 命令。
    /// 未发生变化的配置项一律不生成命令，避免重复下发和覆盖用户未触碰的设置。
    /// </summary>
    public static IReadOnlyList<ServiceMutationCommand> BuildConfigCommands(
        ServiceConfigSnapshot current,
        ServiceConfigSnapshot desired,
        string? newPassword)
    {
        var commands = new List<ServiceMutationCommand>();
        var service = QuoteScValue(desired.ServiceName);

        if (StartTypeChanged(current, desired) && ToScStartOption(desired.StartType) is { Length: > 0 } startOption)
        {
            var text = $"config {service} start= {startOption}";
            commands.Add(new ServiceMutationCommand(ServiceMutationKind.StartType, text, text));
        }

        // 只有在用户真正编辑过二进制路径/参数时才改写 binPath，避免把仅用于展示的
        // 拆解结果（例如未加引号但含空格的路径）回写覆盖原始配置。
        if (PathEdited(current, desired) && !string.IsNullOrWhiteSpace(desired.BinaryPath))
        {
            var text = $"config {service} binPath= {QuoteScValue(ComposePathName(desired.BinaryPath, desired.StartParams))}";
            commands.Add(new ServiceMutationCommand(ServiceMutationKind.BinaryPath, text, text));
        }

        if (IsAccountChanged(current, desired))
        {
            var targetAccount = desired.UseLocalSystem ? "LocalSystem" : desired.LogOnAccount;
            var parts = new List<string> { $"obj= {QuoteScValue(targetAccount)}" };
            if (!desired.UseLocalSystem)
                parts.Add($"password= {QuoteScValue(newPassword)}");

            var command = $"config {service} {string.Join(' ', parts)}";
            var display = desired.UseLocalSystem
                ? command
                : $"config {service} obj= {QuoteScValue(targetAccount)} password= \"********\"";
            commands.Add(new ServiceMutationCommand(ServiceMutationKind.Account, command, display));
        }
        else if (!string.IsNullOrEmpty(newPassword) && !desired.UseLocalSystem)
        {
            // 账户没变但用户输入了新密码：只重置密码，不动其它配置。
            var command = $"config {service} obj= {QuoteScValue(desired.LogOnAccount)} password= {QuoteScValue(newPassword)}";
            var display = $"config {service} obj= {QuoteScValue(desired.LogOnAccount)} password= \"********\"";
            commands.Add(new ServiceMutationCommand(ServiceMutationKind.Account, command, display));
        }

        // `type= interact` 会改变服务类型，只有用户确实切换了“允许服务与桌面交互”时才下发；
        // 早期实现写成 `type= interact type= own`，后一个 type= 会覆盖前一个，导致交互始终未生效。
        if (current.AllowDesktopInteract != desired.AllowDesktopInteract)
        {
            var typeArgs = desired.AllowDesktopInteract ? "type= interact" : "type= own";
            var text = $"config {service} {typeArgs}";
            commands.Add(new ServiceMutationCommand(ServiceMutationKind.Interactive, text, text));
        }
        if (FailureActionsChanged(current, desired))
        {
            var resetSeconds = ParsePositiveInt(desired.ResetFailDays, 1) * 86400;
            var rebootMilliseconds = ParsePositiveInt(desired.RestartMinutes, 1) * 60000;
            var text =
                $"failure {service} actions= {ToFailureNumber(desired.FirstFailure)}/" +
                $"{ToFailureNumber(desired.SecondFailure)}/{ToFailureNumber(desired.SubsequentFailure)} " +
                $"reset= {resetSeconds} reboot= {rebootMilliseconds}";
            commands.Add(new ServiceMutationCommand(ServiceMutationKind.FailureActions, text, text));
        }

        return commands;
    }

    // ---- 变更判定：sc 命令构造与 WMI Change 计划共用同一套谓词，避免两条通道判断不一致 ----

    /// <summary>启动类型是否发生变化。</summary>
    public static bool StartTypeChanged(ServiceConfigSnapshot current, ServiceConfigSnapshot desired)
        => !string.Equals(current.StartType, desired.StartType, StringComparison.Ordinal);

    /// <summary>二进制路径或启动参数是否被用户改动。</summary>
    public static bool PathEdited(ServiceConfigSnapshot current, ServiceConfigSnapshot desired)
        => !string.Equals(current.BinaryPath?.Trim(), desired.BinaryPath?.Trim(), StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(current.StartParams?.Trim(), desired.StartParams?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>失败恢复策略是否发生变化。</summary>
    public static bool FailureActionsChanged(ServiceConfigSnapshot current, ServiceConfigSnapshot desired)
        => !string.Equals(current.FirstFailure, desired.FirstFailure, StringComparison.Ordinal) ||
           !string.Equals(current.SecondFailure, desired.SecondFailure, StringComparison.Ordinal) ||
           !string.Equals(current.SubsequentFailure, desired.SubsequentFailure, StringComparison.Ordinal) ||
           !string.Equals(current.ResetFailDays, desired.ResetFailDays, StringComparison.Ordinal) ||
           !string.Equals(current.RestartMinutes, desired.RestartMinutes, StringComparison.Ordinal);
    /// <summary>服务登录账户是否发生变化。</summary>
    public static bool IsAccountChanged(ServiceConfigSnapshot current, ServiceConfigSnapshot desired)
        => current.UseLocalSystem != desired.UseLocalSystem ||
           !string.Equals(NormalizeAccount(current), NormalizeAccount(desired), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// “自动(延迟启动)”无法用 Win32_Service.Change 表达：Change 的 StartMode 只有 Automatic，
    /// 而延迟标志是独立的注册表值，必须由 sc `start= delayed-auto/auto` 落地。
    /// 因此只要变更涉及延迟启动，就必须保留 sc 通道。
    /// </summary>
    public static bool RequiresScForStartType(ServiceConfigSnapshot current, ServiceConfigSnapshot desired)
        => StartTypeChanged(current, desired) &&
           (string.Equals(desired.StartType, StartTypeAutomaticDelayed, StringComparison.Ordinal) ||
            string.Equals(current.StartType, StartTypeAutomaticDelayed, StringComparison.Ordinal));

    /// <summary>界面启动类型 → Win32_Service.Change 的 StartMode 取值。</summary>
    public static string? ToWmiStartMode(string? uiStartType) => uiStartType switch
    {
        StartTypeAutomatic => "Automatic",
        StartTypeManual => "Manual",
        StartTypeDisabled => "Disabled",
        _ => null,
    };

    /// <summary>
    /// 由“原始快照 → 目标快照”得到一次 Win32_Service.Change 的参数集合。
    /// <see cref="WmiServiceChangePlan.RequiresScForStartType"/> 为 true 时，
    /// StartMode 必须留给 sc 通道，其余参数仍可在同一次 WMI Change 中下发。
    /// </summary>
    public static WmiServiceChangePlan BuildWmiChangePlan(
        ServiceConfigSnapshot current,
        ServiceConfigSnapshot desired,
        string? newPassword)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var requiresScForStartType = RequiresScForStartType(current, desired);

        if (!requiresScForStartType &&
            StartTypeChanged(current, desired) &&
            ToWmiStartMode(desired.StartType) is { Length: > 0 } startMode)
        {
            parameters["StartMode"] = startMode;
        }

        if (PathEdited(current, desired) && !string.IsNullOrWhiteSpace(desired.BinaryPath))
            parameters["PathName"] = ComposePathName(desired.BinaryPath, desired.StartParams);

        if (current.AllowDesktopInteract != desired.AllowDesktopInteract)
            parameters["DesktopInteract"] = desired.AllowDesktopInteract;

        var passwordSupplied = !string.IsNullOrEmpty(newPassword);

        if (IsAccountChanged(current, desired))
        {
            parameters["StartName"] = desired.UseLocalSystem ? "LocalSystem" : desired.LogOnAccount.Trim();
            // LocalSystem 不需要密码；其它账户必须带上新密码，否则服务会拒绝启动。
            parameters["StartPassword"] = desired.UseLocalSystem ? string.Empty : (newPassword ?? string.Empty);
        }
        else if (passwordSupplied && !desired.UseLocalSystem)
        {
            // 账户没变但用户输入了新密码：只重置密码，其它配置保持不变。
            parameters["StartName"] = desired.LogOnAccount.Trim();
            parameters["StartPassword"] = newPassword;
        }

        return new WmiServiceChangePlan(requiresScForStartType, parameters);
    }
    private static int ParsePositiveInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static string NormalizeAccount(ServiceConfigSnapshot snapshot)
        => snapshot.UseLocalSystem ? "LocalSystem" : snapshot.LogOnAccount.Trim();
}
