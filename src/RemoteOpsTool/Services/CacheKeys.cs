namespace RemoteOpsTool.Services;

public static class CacheKeys
{
    public const string Devices = "devices";
    public const string EnvironmentVariablesPrefix = "env";
    public const string Printers = "printers";
    public const string RegistryValuesPrefix = "reg_values";
    public const string Services = "services";
    public const string SoftwarePrefix = "software";
    public const string SystemInfo = "systeminfo";

    public static string EnvironmentVariables(string target)
        => target == "Machine"
            ? $"{EnvironmentVariablesPrefix}_machine"
            : $"{EnvironmentVariablesPrefix}_{DynamicSegment(target)}";

    public static string RegistryValues(string registryPath)
        => $"{RegistryValuesPrefix}_{DynamicSegment(registryPath)}";

    /// <summary>
    /// 服务列表按“主机 + 凭据”隔离缓存。不同凭据能看到/操作的服务集合不同，
    /// 复用同一份缓存会串味，因此用户名必须参与缓存键（只取用户名，不落密码）。
    /// </summary>
    public static string ServicesForCredential(string? username)
    {
        var normalized = (username ?? string.Empty).Trim().TrimStart('\\').ToLowerInvariant();
        return string.IsNullOrEmpty(normalized)
            ? Services
            : $"{Services}_{DynamicSegment(normalized)}";
    }
    public static string Software(bool deepCleanup)
        => $"{SoftwarePrefix}_{(deepCleanup ? "deep" : "normal")}";

    private static string DynamicSegment(string value)
        => value.Replace("\\", "_");
}
