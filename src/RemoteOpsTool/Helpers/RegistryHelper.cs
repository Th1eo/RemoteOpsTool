namespace RemoteOpsTool.Helpers;

public static class RegistryHelper
{
    public static string[] GetSubKeyFullPaths(string path)
    {
        var (hive, subPath) = ParseHiveAndPath(path);
        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Default);
        using var sub = string.IsNullOrEmpty(subPath) ? key : key.OpenSubKey(subPath, false);
        if (sub == null) return [];

        var names = sub.GetSubKeyNames();
        var basePath = path.EndsWith('\\') ? path : path + "\\";
        return names.Select(n => basePath + n).ToArray();
    }

    public static List<RemoteOpsTool.ViewModels.Dialogs.RegValueDisplay> GetValues(string path)
    {
        var result = new List<RemoteOpsTool.ViewModels.Dialogs.RegValueDisplay>();
        var (hive, subPath) = ParseHiveAndPath(path);
        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Default);
        using var sub = string.IsNullOrEmpty(subPath) ? key : key.OpenSubKey(subPath, false);
        if (sub == null) return result;

        foreach (var name in sub.GetValueNames())
        {
            var kind = sub.GetValueKind(name);
            var raw = sub.GetValue(name);
            var valStr = FormatValue(raw, kind);
            result.Add(new RemoteOpsTool.ViewModels.Dialogs.RegValueDisplay { Name = name, Type = FormatKind(kind), Value = valStr });
        }
        return result;
    }

    public static (Microsoft.Win32.RegistryHive hive, string subPath) ParseHiveAndPath(string path)
    {
        var trimmed = path.Trim().TrimStart('\\');
        var sepIdx = trimmed.IndexOf('\\');
        var hiveName = sepIdx > 0 ? trimmed[..sepIdx] : trimmed;
        var subPath = sepIdx > 0 ? trimmed[(sepIdx + 1)..] : "";

        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Microsoft.Win32.RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Microsoft.Win32.RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => Microsoft.Win32.RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => Microsoft.Win32.RegistryHive.CurrentConfig,
            _ => throw new ArgumentException($"Unknown registry hive: {hiveName}")
        };

        return (hive, subPath);
    }

    private static string FormatKind(Microsoft.Win32.RegistryValueKind kind) => kind switch
    {
        Microsoft.Win32.RegistryValueKind.String => "REG_SZ",
        Microsoft.Win32.RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        Microsoft.Win32.RegistryValueKind.Binary => "REG_BINARY",
        Microsoft.Win32.RegistryValueKind.DWord => "REG_DWORD",
        Microsoft.Win32.RegistryValueKind.MultiString => "REG_MULTI_SZ",
        Microsoft.Win32.RegistryValueKind.QWord => "REG_QWORD",
        _ => kind.ToString()
    };

    private static string FormatValue(object? raw, Microsoft.Win32.RegistryValueKind kind)
    {
        if (raw == null) return "(value not set)";
        return kind switch
        {
            Microsoft.Win32.RegistryValueKind.MultiString => string.Join(" ", (string[])raw),
            Microsoft.Win32.RegistryValueKind.Binary => BitConverter.ToString((byte[])raw).Replace('-', ','),
            Microsoft.Win32.RegistryValueKind.DWord => $"0x{((int)raw):X8} ({raw})",
            Microsoft.Win32.RegistryValueKind.QWord => $"0x{((long)raw):X16} ({raw})",
            _ => raw.ToString() ?? ""
        };
    }
}
