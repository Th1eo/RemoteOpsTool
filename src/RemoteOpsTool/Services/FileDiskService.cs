using System.Diagnostics;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class FileDiskService : IFileDiskService
{
    private readonly IPsExecService _psExec;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public FileDiskService(IPsExecService psExec, ISettingsService settings, ILogService log)
    {
        _psExec = psExec;
        _settings = settings;
        _log = log;
    }

    public void OpenCRoot(string host)
    {
        var path = NetworkPathHelper.BuildAdminShare(host, "C");
        OpenExplorer(path);
    }

    public void OpenPublicDesktop(string host)
    {
        var path = NetworkPathHelper.BuildPublicDesktop(host);
        OpenExplorer(path);
    }

    public void OpenDomainPublic()
    {
        var path = _settings.Settings.DomainPublicPath;
        if (string.IsNullOrEmpty(path))
        {
            _log.Warn("Domain public path is not configured.");
            return;
        }
        OpenExplorer(path);
    }

    public void OpenDrive(string host, string driveLetter)
    {
        var path = NetworkPathHelper.BuildAdminShare(host, driveLetter);
        OpenExplorer(path);
    }

    public async Task<List<DiskInfo>> GetDiskInfoAsync(string host, string username, string password,
        CancellationToken ct = default, bool silent = false)
    {
        var psCommand = "powershell \"Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, @{N='FreeGB';E={[math]::Round($_.FreeSpace/1GB,2)}}, @{N='SizeGB';E={[math]::Round($_.Size/1GB,2)}} | ConvertTo-Csv -NoTypeInformation\"";

        var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct, silent: silent);
        if (!result.Success) return [];

        var disks = new List<DiskInfo>();
        var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("DeviceID")) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;

            if (double.TryParse(parts[1].Trim('"'), out var freeGB) &&
                double.TryParse(parts[2].Trim('"'), out var sizeGB))
            {
                disks.Add(new DiskInfo
                {
                    DeviceId = parts[0].Trim('"'),
                    FreeGB = freeGB,
                    SizeGB = sizeGB
                });
            }
        }

        return disks;
    }

    public async Task CleanupDisksAsync(string host, string username, string password,
        IEnumerable<string> directories, CancellationToken ct = default)
    {
        await Parallel.ForEachAsync(directories, ct, async (dir, token) =>
        {
            var cleanPath = dir.Replace("C:", @"\\?\C:");
            var command = $"cmd /c \"del /f /s /q {cleanPath}\\*.* 2>nul & for /d %i in ({cleanPath}\\*) do @rmdir /s /q %i 2>nul\"";
            _log.Info($"Cleaning: {dir}");
            await _psExec.ExecuteAsync(host, username, password, command, ct: token);
        });
        _log.Info("Disk cleanup completed.");
    }

    private void OpenExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
            _log.Info($"Opened: {path}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to open explorer: {ex.Message}");
        }
    }
}
