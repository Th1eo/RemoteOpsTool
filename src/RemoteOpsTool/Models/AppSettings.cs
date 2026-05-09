namespace RemoteOpsTool.Models;

public class AppSettings
{
    public string PsToolsPath { get; set; } = @"C:\ProgramData\RemoteAdmin\Tools";
    public string DameWarePath { get; set; } = string.Empty;
    public string DomainPublicPath { get; set; } = string.Empty;
    public string CustomCleanupDirectories { get; set; } = string.Empty;
    public bool DebugMode { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public int WindowStateValue { get; set; }
}
