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

    public static string Software(bool deepCleanup)
        => $"{SoftwarePrefix}_{(deepCleanup ? "deep" : "normal")}";

    private static string DynamicSegment(string value)
        => value.Replace("\\", "_");
}
