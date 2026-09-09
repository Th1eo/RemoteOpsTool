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

    public async Task<DiskCleanupResult> CleanupDisksAsync(string host, string username, string password,
        IEnumerable<DiskCleanupTarget> targets, IProgress<DiskCleanupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var cleanupTargets = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .Select(target => new DiskCleanupTarget(target.Path.Trim(), target.DeleteDirectory))
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DiskCleanupTarget(group.Key, group.Any(target => target.DeleteDirectory)))
            .ToArray();
        var results = new List<DiskCleanupTargetResult>(cleanupTargets.Length);

        for (var index = 0; index < cleanupTargets.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var target = cleanupTargets[index];
            var path = target.Path;
            var mode = target.DeleteDirectory ? "删除目录本身" : "保留目录本身";
            _log.Debug($"清理目标: host={host} path={path} mode={mode} user={username}");
            var script = BuildCleanupScript(path, target.DeleteDirectory);
            var command = SystemInfoService.EncodePowerShellCommand(script);
            _log.Info($"Cleaning: {path} ({mode})");

            var result = HostHelper.IsLocalHost(host)
                ? await _psExec.ExecuteLocalElevatedAsync(
                    host, username, password, script, ct, CommandShell.PowerShell)
                : await _psExec.ExecuteAsync(host, username, password, command, ct: ct,
                    silent: true);
            var message = result.Success
                ? GetCleanupMessage(result, path)
                : GetCleanupError(result);
            results.Add(new DiskCleanupTargetResult(path, result.Success, message));

            if (result.Success)
                _log.Info($"清理并验证完成: {path} ({mode})");
            else
                _log.Error($"清理失败: host={host} path={path} mode={mode} exit={result.ExitCode}\n{message}");

            progress?.Report(new DiskCleanupProgress(
                index + 1, cleanupTargets.Length, path, result.Success,
                result.Success
                    ? $"({index + 1}/{cleanupTargets.Length}) 已清理并验证：{path}"
                    : $"({index + 1}/{cleanupTargets.Length}) 清理失败：{path}"));
        }

        var cleanupResult = new DiskCleanupResult(results);
        if (cleanupResult.Success)
            _log.Info($"Disk cleanup completed and verified. targets={cleanupResult.SucceededCount}");
        else
            _log.Warn($"磁盘清理完成，但以下目标失败: {string.Join(", ", results.Where(r => !r.Success).Select(r => r.Path))}");

        return cleanupResult;
    }

    private static string GetCleanupMessage(CommandResult result, string path)
    {
        var output = result.StdOut.Trim();
        return string.IsNullOrWhiteSpace(output) ? $"清理并验证完成: {path}" : output;
    }

    private static string GetCleanupError(CommandResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        return string.IsNullOrWhiteSpace(error) ? "清理命令执行失败，未返回详细信息。" : error.Trim();
    }

    private static string BuildCleanupScript(string path, bool deleteDirectory)
    {
        var literalPath = path.Replace("'", "''", StringComparison.Ordinal);
        var deleteDirectoryValue = deleteDirectory ? "$true" : "$false";
        return $"$ErrorActionPreference = 'Stop'\r\n" +
               $"$targetPath = '{literalPath}'\r\n" +
               $"$deleteDirectory = {deleteDirectoryValue}\r\n" +
               "try {\r\n" +
               "    $hasWildcard = $targetPath.IndexOfAny([char[]]'*?') -ge 0\r\n" +
               "    if ($hasWildcard) {\r\n" +
               "        # Support ordinary * and ? wildcards while treating brackets literally.\r\n" +
               "        $wildcardPath = [System.Management.Automation.WildcardPattern]::Escape($targetPath)\r\n" +
               "        $wildcardPath = $wildcardPath.Replace('`*', '*').Replace('`?', '?')\r\n" +
               "        $matches = @(Get-Item -Path $wildcardPath -Force -ErrorAction SilentlyContinue)\r\n" +
               "        if ($matches.Count -eq 0) {\r\n" +
               "            Write-Output \"通配符未匹配任何目标，跳过: $targetPath\"\r\n" +
               "            exit 0\r\n" +
               "        }\r\n" +
               "\r\n" +
               "        foreach ($match in $matches) {\r\n" +
               "            if ($match.PSIsContainer -and $deleteDirectory) {\r\n" +
               "                $rootPath = [System.IO.Path]::GetPathRoot($match.FullName)\r\n" +
               "                if ($rootPath -and $match.FullName.TrimEnd('\\') -eq $rootPath.TrimEnd('\\')) {\r\n" +
               "                    throw '禁止删除磁盘根目录或共享根目录'\r\n" +
               "                }\r\n" +
               "            }\r\n" +
               "            if ($match.PSIsContainer -and -not $deleteDirectory) {\r\n" +
               "                Get-ChildItem -LiteralPath $match.FullName -Force | Remove-Item -Force -Recurse\r\n" +
               "                $remaining = @(Get-ChildItem -LiteralPath $match.FullName -Force -ErrorAction SilentlyContinue)\r\n" +
               "                if ($remaining.Count -gt 0) { throw \"目录清理后仍有 $($remaining.Count) 项内容存在: $($match.FullName)\" }\r\n" +
               "            }\r\n" +
               "            else {\r\n" +
               "                Remove-Item -LiteralPath $match.FullName -Force -Recurse\r\n" +
               "                if (Test-Path -LiteralPath $match.FullName) { throw \"删除后目标仍然存在: $($match.FullName)\" }\r\n" +
               "            }\r\n" +
               "        }\r\n" +
               "        $action = if ($deleteDirectory) { '删除' } else { '清空' }\r\n" +
               "        Write-Output \"清理完成并验证: $targetPath（$action $($matches.Count) 项）\"\r\n" +
               "        exit 0\r\n" +
               "    }\r\n" +
               "\r\n" +
               "    if (-not (Test-Path -LiteralPath $targetPath)) {\r\n" +
               "        Write-Output \"目标不存在，跳过: $targetPath\"\r\n" +
               "        exit 0\r\n" +
               "    }\r\n" +
               "\r\n" +
               "    $target = Get-Item -LiteralPath $targetPath -Force\r\n" +
               "    if ($target.PSIsContainer) {\r\n" +
               "        if ($deleteDirectory) {\r\n" +
               "            $rootPath = [System.IO.Path]::GetPathRoot($target.FullName)\r\n" +
               "            if ($rootPath -and $target.FullName.TrimEnd('\\') -eq $rootPath.TrimEnd('\\')) {\r\n" +
               "                throw '禁止删除磁盘根目录或共享根目录'\r\n" +
               "            }\r\n" +
               "            Remove-Item -LiteralPath $target.FullName -Force -Recurse\r\n" +
               "            if (Test-Path -LiteralPath $targetPath) { throw '目录删除后仍然存在' }\r\n" +
               "        }\r\n" +
               "        else {\r\n" +
               "            Get-ChildItem -LiteralPath $targetPath -Force | Remove-Item -Force -Recurse\r\n" +
               "            $remaining = @(Get-ChildItem -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue)\r\n" +
               "            if ($remaining.Count -gt 0) { throw \"目录清理后仍有 $($remaining.Count) 项内容存在\" }\r\n" +
               "        }\r\n" +
               "    }\r\n" +
               "    else {\r\n" +
               "        Remove-Item -LiteralPath $targetPath -Force\r\n" +
               "        if (Test-Path -LiteralPath $targetPath) { throw '文件删除后仍然存在' }\r\n" +
               "    }\r\n" +
               "    $action = if ($target.PSIsContainer -and -not $deleteDirectory) { '目录内容已清空' } else { '目标已删除' }\r\n" +
               "    Write-Output \"清理完成并验证: $targetPath（$action）\"\r\n" +
               "    exit 0\r\n" +
               "} catch {\r\n" +
               "    Write-Error (\"清理失败: \" + $_.Exception.Message)\r\n" +
               "    exit 1\r\n" +
               "}\r\n";
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
