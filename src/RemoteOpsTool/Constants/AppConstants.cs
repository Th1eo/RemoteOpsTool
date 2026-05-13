namespace RemoteOpsTool.Constants;

public static class AppConstants
{
    public const string AppName = "RemoteOpsTool";
    public const string CompanyFolder = "RemoteAdmin";
    public const string DefaultToolsPath = @"C:\ProgramData\RemoteAdmin\Tools";
    public const string SettingsFileName = "settings.json";
    public const string CredentialsFileName = "credentials.dat";
    public const string PsExecUrl = "https://learn.microsoft.com/sysinternals/downloads/pstools";

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CompanyFolder);

    public static string SettingsFilePath =>
        Path.Combine(AppDataFolder, SettingsFileName);

    public static string CredentialsFilePath =>
        Path.Combine(AppDataFolder, CredentialsFileName);

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
