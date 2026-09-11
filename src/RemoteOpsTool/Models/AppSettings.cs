namespace RemoteOpsTool.Models;

public class AppSettings
{
    public string PsToolsPath { get; set; } = @"C:\ProgramData\RemoteAdmin\Tools";
    public string DameWarePath { get; set; } = string.Empty;
    public string DomainPublicPath { get; set; } = string.Empty;
    public string CustomCleanupDirectories { get; set; } = string.Empty;
    public bool DebugMode { get; set; }
    public bool PreferPsExec64 { get; set; } = true;
    public bool PreferWmiForRemoteCommands { get; set; } = true;
    // When PsExec is already launched with the selected credential token, do not
    // put the same password on PsExec's command line by default. This is both
    // safer and avoids credential transport being evaluated twice by PsExec.
    public bool OmitPsExecExplicitCredentialsWhenRunAs { get; set; } = true;
    public int PsExecConnectTimeoutSeconds { get; set; } = 10;
    public string PsExecRemoteWorkingDirectory { get; set; } = @"C:\Windows\System32";
    public string PsExecServiceNamePrefix { get; set; } = "RemoteOpsTool";
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public int WindowStateValue { get; set; }
}
