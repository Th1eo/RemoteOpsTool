namespace RemoteOpsTool.Constants;

public static class AppConstants
{
    public const string AppName = "RemoteOpsTool";
    public const string CompanyFolder = "RemoteAdmin";
    public const string DefaultToolsPath = @"C:\ProgramData\RemoteAdmin\Tools";
    public const string SettingsFileName = "settings.json";
    public const string CredentialsFileName = "credentials.dat";
    public const string RouteLearningFileName = "route-learning.json";
    public const string LogFileName = "RemoteOpsTool.log";
    public const string PsExecUrl = "https://learn.microsoft.com/sysinternals/downloads/pstools";

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CompanyFolder);

    public static string SettingsFilePath =>
        Path.Combine(AppDataFolder, SettingsFileName);

    public static string CredentialsFilePath =>
        Path.Combine(AppDataFolder, CredentialsFileName);

    public static string RouteLearningFilePath =>
        Path.Combine(AppDataFolder, RouteLearningFileName);

    /// <summary>
    /// 日志写入 %LOCALAPPDATA%，避免程序目录可写或共享时被其他用户读取，
    /// 也避免 Program Files 这类受保护目录下无法落盘。
    /// </summary>
    public static string LogFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CompanyFolder,
            "logs");

    public static string LogFilePath => Path.Combine(LogFolder, LogFileName);

    public static readonly string[] CleanupDirectories =
    [
        @"C:\Windows\Temp",
        @"C:\Windows\Prefetch",
        @"C:\Windows\SoftwareDistribution\Download"
    ];

    public static readonly string[] SoftwareRegistryKeys =
    [
        @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall"
    ];
}
