namespace RemoteOpsTool.Services.Interfaces;

public interface IToolSetupService
{
    Task<bool> EnsureToolsAsync();
    bool IsPsToolsAvailable(string? customPath = null);
    string DetectPsToolsPath();
    Task<(bool Success, string Message)> ConfigurePsToolsAsync(string targetPath);
    Task<(bool Success, string Message)> UpdatePsToolsAsync(string targetPath);
    Task<(bool Success, string Message)> UninstallPsToolsAsync(string targetPath);
    void OpenDownloadPage();
    string GetStatusText(string? path = null);
}
