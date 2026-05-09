namespace RemoteOpsTool.Models;

public class SoftwareInfo
{
    public string DisplayName { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string RegistryKey { get; set; } = string.Empty;
}
