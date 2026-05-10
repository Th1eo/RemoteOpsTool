using System.Diagnostics;
using System.Management;
using System.Text.Json;
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

    public void OpenCRoot(string host, string username = "", string password = "")
    {
        _log.Debug($"打开远程 C 盘: host={host} user={username}");
        var path = NetworkPathHelper.BuildAdminShare(host, "C");
        OpenExplorer(path, username, password);
    }

    public void OpenPublicDesktop(string host, string username = "", string password = "")
    {
        _log.Debug($"打开远程公共桌面: host={host} user={username}");
        var path = NetworkPathHelper.BuildPublicDesktop(host);
        var share = NetworkPathHelper.BuildAdminShare(host, "C");
        OpenExplorer(path, username, password, share);
    }

    public void OpenDomainPublic()
    {
        _log.Debug($"打开域公共目录: path={_settings.Settings.DomainPublicPath}");
        var path = _settings.Settings.DomainPublicPath;
        if (string.IsNullOrEmpty(path))
        {
            _log.Warn("Domain public path is not configured.");
            return;
        }
        OpenExplorer(path);
    }

    public void OpenDrive(string host, string driveLetter, string username = "", string password = "")
    {
        _log.Debug($"打开远程盘符: host={host} drive={driveLetter} user={username}");
        var path = NetworkPathHelper.BuildAdminShare(host, driveLetter);
        OpenExplorer(path, username, password);
    }

    public async Task<List<DiskInfo>> GetDiskInfoAsync(string host, string username, string password,
        CancellationToken ct = default, bool silent = false)
    {
        _log.Debug($"获取磁盘信息: host={host} method=WMI/DCOM user={username}");
        var wmiDisks = await TryGetDiskInfoViaWmiAsync(host, username, password, ct);
        if (wmiDisks.Count > 0)
        {
            _log.Debug($"WMI/DCOM 磁盘信息完成: host={host} count={wmiDisks.Count}");
            if (!silent)
                _log.Info($"已通过 WMI/DCOM 获取磁盘信息: {host}");
            return wmiDisks;
        }

        if (!silent)
            _log.Warn($"WMI/DCOM 磁盘查询不可用，回退到 PsExec: {host}");

        var psCommand = SystemInfoService.EncodePowerShellCommand(
            "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | " +
            "Select-Object DeviceID, @{N='FreeGB';E={[math]::Round($_.FreeSpace/1GB,2)}}, @{N='SizeGB';E={[math]::Round($_.Size/1GB,2)}} | " +
            "ConvertTo-Json -Compress");
        _log.Debug($"回退 PsExec 获取磁盘信息: host={host} command={psCommand}");

        var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct, silent: silent);
        if (!result.Success) return [];

        try
        {
            var jsonStart = result.StdOut.IndexOfAny(['[', '{']);
            var jsonEnd = Math.Max(result.StdOut.LastIndexOf(']'), result.StdOut.LastIndexOf('}'));
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = result.StdOut[jsonStart..(jsonEnd + 1)];
                var elements = json.TrimStart().StartsWith("[", StringComparison.Ordinal)
                    ? JsonSerializer.Deserialize<List<JsonElement>>(json) ?? []
                    : [JsonSerializer.Deserialize<JsonElement>(json)];

                return elements.Select(item => new DiskInfo
                {
                    DeviceId = item.TryGetProperty("DeviceID", out var id) ? id.GetString() ?? "" : "",
                    FreeGB = item.TryGetProperty("FreeGB", out var free) && free.TryGetDouble(out var fg) ? fg : 0,
                    SizeGB = item.TryGetProperty("SizeGB", out var size) && size.TryGetDouble(out var sg) ? sg : 0
                }).Where(d => !string.IsNullOrWhiteSpace(d.DeviceId)).ToList();
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"PsExec 磁盘 JSON 解析失败: {host} - {ex.Message}");
        }

        return [];
    }

    private async Task<List<DiskInfo>> TryGetDiskInfoViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var disks = new List<DiskInfo>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT DeviceID,FreeSpace,Size FROM Win32_LogicalDisk WHERE DriveType=3"));
                foreach (ManagementObject disk in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var freeBytes = RemoteWmiHelper.GetUInt64(disk, "FreeSpace");
                    var sizeBytes = RemoteWmiHelper.GetUInt64(disk, "Size");
                    disks.Add(new DiskInfo
                    {
                        DeviceId = RemoteWmiHelper.GetString(disk, "DeviceID"),
                        FreeGB = Math.Round(freeBytes / 1073741824.0, 2),
                        SizeGB = Math.Round(sizeBytes / 1073741824.0, 2)
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 磁盘信息查询失败: {host} - {ex.Message}");
                return [];
            }
            return disks;
        }, ct);
    }

    public async Task CleanupDisksAsync(string host, string username, string password,
        IEnumerable<string> directories, CancellationToken ct = default)
    {
        await Parallel.ForEachAsync(directories, ct, async (dir, token) =>
        {
            _log.Debug($"清理目录: host={host} dir={dir} user={username}");
            var cleanPath = dir.Replace("C:", @"\\?\C:");
            var command = $"cmd /c \"del /f /s /q {cleanPath}\\*.* 2>nul & for /d %i in ({cleanPath}\\*) do @rmdir /s /q %i 2>nul\"";
            _log.Info($"Cleaning: {dir}");
            await _psExec.ExecuteAsync(host, username, password, command, ct: token);
        });
        _log.Info("Disk cleanup completed.");
    }

    private void OpenExplorer(string path, string username = "", string password = "", string? shareRoot = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(username) && path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var remoteName = shareRoot ?? path;
                var connection = NetworkShareCredentialHelper.EnsureConnection(remoteName, username, password);
                _log.Debug($"SMB 凭据连接: remote={remoteName} user={username} success={connection.Success} message={connection.Message}");
                if (!connection.Success)
                    _log.Warn($"无法使用所选凭据连接共享 {remoteName}: {connection.Message}");
            }

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
