using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class ToolSetupService : IToolSetupService
{
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string PsToolsDownloadUrl = "https://download.sysinternals.com/files/PSTools.zip";

    public ToolSetupService(ILogService log, ISettingsService settings)
    {
        _log = log;
        _settings = settings;
    }

    public async Task<bool> EnsureToolsAsync()
    {
        var path = _settings.Settings.PsToolsPath;
        if (string.IsNullOrEmpty(path))
            path = AppConstants.DefaultToolsPath;

        if (IsPsToolsAvailable(path))
        {
            _log.Info($"PsExec 就绪: {path}");
            return true;
        }

        _log.Warn($"PsExec 未找到: {path}");
        return false;
    }

    public bool IsPsToolsAvailable(string? customPath = null)
    {
        var path = customPath ?? _settings.Settings.PsToolsPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        var candidates = new[]
        {
            Path.Combine(path, "PsExec64.exe"),
            Path.Combine(path, "PsExec.exe")
        };

        return candidates.Any(file => File.Exists(file) && IsMicrosoftSigned(file));
    }

    public string DetectPsToolsPath()
    {
        var defaultPath = AppConstants.DefaultToolsPath;
        if (IsPsToolsAvailable(defaultPath))
            return defaultPath;
        return string.Empty;
    }

    public string GetStatusText(string? path = null)
    {
        return IsPsToolsAvailable(path)
            ? "PsExec 状态正常 √"
            : "PsExec 未找到或签名异常 ×";
    }

    public void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://learn.microsoft.com/sysinternals/downloads/pstools",
                UseShellExecute = true
            });
            _log.Info("已打开微软 PsTools 下载页面。");
        }
        catch (Exception ex)
        {
            _log.Error($"打开下载页面失败: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> ConfigurePsToolsAsync(string targetPath)
    {
        try
        {
            if (string.IsNullOrEmpty(targetPath))
            {
                targetPath = AppConstants.DefaultToolsPath;
                _settings.Settings.PsToolsPath = targetPath;
                await _settings.SaveAsync();
            }

            _log.Info($"正在下载 PsTools 到 {targetPath}...");

            if (Directory.Exists(targetPath))
                Directory.Delete(targetPath, true);
            Directory.CreateDirectory(targetPath);

            var zipPath = Path.Combine(Path.GetTempPath(), "PSTools.zip");
            var response = await _http.GetAsync(PsToolsDownloadUrl);
            response.EnsureSuccessStatusCode();

            await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
            await response.Content.CopyToAsync(fs);
            fs.Close();

            ZipFile.ExtractToDirectory(zipPath, targetPath, true);
            File.Delete(zipPath);

            var ok = IsPsToolsAvailable(targetPath);
            _log.Info(ok ? "PsExec 配置完成，签名校验通过" : "PsExec 提取不完整或签名校验失败");
            return (ok, ok ? "PsExec 配置完成" : "提取不完整或签名校验失败");
        }
        catch (Exception ex)
        {
            _log.Error($"配置 PsExec 失败: {ex.Message}");
            return (false, $"配置失败: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> UpdatePsToolsAsync(string targetPath)
    {
        var uninstallResult = await UninstallPsToolsAsync(targetPath);
        if (!uninstallResult.Success)
            return uninstallResult;

        _log.Info("正在下载最新版 PsExec...");
        return await ConfigurePsToolsAsync(targetPath);
    }

    public Task<(bool Success, string Message)> UninstallPsToolsAsync(string targetPath)
    {
        try
        {
            if (string.IsNullOrEmpty(targetPath))
                targetPath = _settings.Settings.PsToolsPath;

            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, true);
                _log.Info($"已卸载 PsExec: {targetPath}");
                return Task.FromResult((true, "卸载完成"));
            }

            return Task.FromResult((true, "PsExec 目录不存在，无需卸载"));
        }
        catch (Exception ex)
        {
            _log.Error($"卸载 PsExec 失败: {ex.Message}");
            return Task.FromResult((false, $"卸载失败: {ex.Message}"));
        }
    }

    private static bool IsMicrosoftSigned(string filePath)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(filePath);
            return cert.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
