namespace RemoteOpsTool.Services;

public static class CacheKeys
{
    public const string Devices = "devices";
    public const string EnvironmentVariablesPrefix = "env";
    public const string Printers = "printers";
    public const string RegistryValuesPrefix = "reg_values";
    public const string ServicePropertiesPrefix = "service_properties";
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
    /// 返回远程注册表值缓存中指定键及其所有子键的基础前缀。
    /// InvalidateByPrefix 会同时匹配基础键本身及以“基础键_”开头的子键，用于删除、重命名注册表键后精确失效整棵子树。
    /// </summary>
    public static string RegistryValuesSubtreePrefix(string registryPath)
    {
        var normalized = (registryPath ?? string.Empty).Trim().TrimEnd('\\');
        return string.IsNullOrEmpty(normalized)
            ? RegistryValuesPrefix
            : RegistryValues(normalized);
    }

    /// <summary>
    /// 服务列表按“主机 + 凭据”隔离缓存。不同凭据能看到/操作的服务集合不同，
    /// 复用同一份缓存会串味，因此用户名必须参与缓存键（只取用户名，不落密码）。
    /// </summary>
    public static string ServicesForCredential(string? username)
    {
        var normalized = NormalizeUsername(username);
        return string.IsNullOrEmpty(normalized)
            ? Services
            : $"{Services}_{DynamicSegment(normalized)}";
    }

    /// <summary>
    /// 服务属性快照同时按主机（CacheService 传入）和凭据隔离；服务名做规范化，
    /// 避免同一服务因大小写不同产生重复缓存。
    /// </summary>
    public static string ServiceProperties(string serviceName, string? username)
    {
        var normalizedUser = NormalizeUsername(username);
        var normalizedService = DynamicSegment((serviceName ?? string.Empty).Trim().ToLowerInvariant());

        return string.IsNullOrEmpty(normalizedUser)
            ? $"{ServicePropertiesPrefix}_{normalizedService}"
            : $"{ServicePropertiesPrefix}_{normalizedUser}_{normalizedService}";
    }
    public static string Software(bool deepCleanup)
        => $"{SoftwarePrefix}_{(deepCleanup ? "deep" : "normal")}";

    private static string DynamicSegment(string value)
        => value.Replace("\\", "_");

    private static string NormalizeUsername(string? username)
        => (username ?? string.Empty).Trim().TrimStart('\\').ToLowerInvariant();
}
