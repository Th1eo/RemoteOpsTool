using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class FileDiskService : IFileDiskService
{
    private static readonly ConcurrentDictionary<string, DiskInfoCacheEntry> DiskInfoCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DiskInfoCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DirectWmiQueryTimeout = TimeSpan.FromSeconds(8);
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public FileDiskService(
        IPsExecService psExec,
        IRemoteExecutionService execution,
        ISettingsService settings,
        ILogService log)
    {
        _psExec = psExec;
        _execution = execution;
        _settings = settings;
        _log = log;
    }

    public void OpenCRoot(string host, string username = "", string password = "")
    {
        _log.Debug($"打开远程 C 盘: host={host} user={username}");
        var path = HostHelper.IsLocalHost(host)
            ? @"C:\"
            : NetworkPathHelper.BuildAdminShare(host, "C");
        OpenExplorer(path, username, password);
    }

    public void OpenPublicDesktop(string host, string username = "", string password = "")
    {
        _log.Debug($"打开远程公共桌面: host={host} user={username}");
        var isLocal = HostHelper.IsLocalHost(host);
        var path = isLocal
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            : NetworkPathHelper.BuildPublicDesktop(host);
        var share = isLocal ? null : NetworkPathHelper.BuildAdminShare(host, "C");
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
        var normalizedDrive = driveLetter.Trim().TrimEnd(':');
        var path = HostHelper.IsLocalHost(host)
            ? $"{normalizedDrive}:\\"
            : NetworkPathHelper.BuildAdminShare(host, normalizedDrive);
        OpenExplorer(path, username, password);
    }

    public async Task<List<DiskInfo>> GetDiskInfoAsync(string host, string username, string password,
        CancellationToken ct = default, bool silent = false)
    {
        _log.Debug($"获取磁盘信息: host={host} method=direct-wmi-then-capability-session user={username}");
        if (TryGetCachedDiskInfo(host, username, out var cachedDisks))
        {
            _log.Debug($"磁盘信息缓存命中: host={host} count={cachedDisks.Count}");
            return cachedDisks;
        }

        // Disk inventory is read-only. Prefer the direct WMI/DCOM object model so
        // it does not need a PowerShell bootstrap, temporary registry job, or
        // remote process creation. The unified session remains the safe fallback
        // when WMI is disabled or access is denied.
        var directDisks = await TryGetDiskInfoViaWmiAsync(host, username, password, ct);
        if (directDisks.Count > 0)
        {
            CacheDiskInfo(host, username, directDisks);
            _log.Debug($"直接 WMI 磁盘信息完成: host={host} count={directDisks.Count}");
            return CloneDisks(directDisks);
        }

        if (HostHelper.IsLocalHost(host))
        {
            _log.Debug($"本地 WMI 磁盘信息完成: host={host} count={directDisks.Count}");
            return directDisks;
        }

        var command = SystemInfoService.EncodePowerShellCommand(
            "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | " +
            "Select-Object DeviceID, @{N='FreeGB';E={[math]::Round($_.FreeSpace/1GB,2)}}, @{N='SizeGB';E={[math]::Round($_.Size/1GB,2)}} | " +
            "ConvertTo-Json -Compress");

        var session = await _execution.CreateSessionAsync(host, username, password, ct);
        var transport = await session.ExecuteAsync(
            RemoteOperationKind.Inventory,
            new RemoteCommand
            {
                TargetHost = host,
                Username = username,
                Password = password,
                Command = command,
                Shell = CommandShell.Direct,
                WrapCmd = false,
                Silent = silent,
            },
            ct: ct);

        if (!transport.Success)
        {
            _log.Debug($"磁盘信息查询失败: host={host} transport={transport.Transport} exit={transport.ExitCode} error={transport.StdErr}");
            return [];
        }

        var disks = ParseDiskInfoJson(transport.StdOut, host);
        if (disks.Count > 0)
            CacheDiskInfo(host, username, disks);
        _log.Debug($"磁盘信息完成: host={host} transport={transport.Transport} count={disks.Count}");
        if (!silent && disks.Count > 0)
            _log.Info($"已通过 {transport.Transport} 获取磁盘信息: {host}");
        return CloneDisks(disks);
    }

    private static string BuildDiskInfoCacheKey(string host, string username) =>
        $"{HostHelper.NormalizeHost(host).ToLowerInvariant()}|{username.Trim().ToLowerInvariant()}";

    private static bool TryGetCachedDiskInfo(string host, string username, out List<DiskInfo> disks)
    {
        var key = BuildDiskInfoCacheKey(host, username);
        if (DiskInfoCache.TryGetValue(key, out var entry) &&
            DateTimeOffset.UtcNow - entry.CapturedAt <= DiskInfoCacheTtl)
        {
            disks = CloneDisks(entry.Disks);
            return disks.Count > 0;
        }

        DiskInfoCache.TryRemove(key, out _);
        disks = [];
        return false;
    }

    private static void CacheDiskInfo(string host, string username, IReadOnlyCollection<DiskInfo> disks)
    {
        if (disks.Count == 0)
            return;

        DiskInfoCache[BuildDiskInfoCacheKey(host, username)] = new DiskInfoCacheEntry(
            DateTimeOffset.UtcNow,
            disks.Select(CloneDisk).ToArray());
    }

    private static void InvalidateDiskInfoCache(string host, string username) =>
        DiskInfoCache.TryRemove(BuildDiskInfoCacheKey(host, username), out _);

    private static List<DiskInfo> CloneDisks(IEnumerable<DiskInfo> disks) =>
        disks.Select(CloneDisk).ToList();

    private static DiskInfo CloneDisk(DiskInfo disk) => new()
    {
        DeviceId = disk.DeviceId,
        FreeGB = disk.FreeGB,
        SizeGB = disk.SizeGB
    };

    private sealed record DiskInfoCacheEntry(DateTimeOffset CapturedAt, IReadOnlyList<DiskInfo> Disks);

    private List<DiskInfo> ParseDiskInfoJson(string output, string host)
    {
        try
        {
            var jsonStart = output.IndexOfAny(['[', '{']);
            var jsonEnd = Math.Max(output.LastIndexOf(']'), output.LastIndexOf('}'));
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return [];

            var json = output[jsonStart..(jsonEnd + 1)];
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
        catch (Exception ex)
        {
            _log.Debug($"磁盘 JSON 解析失败: {host} - {ex.Message}");
            return [];
        }
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
                var scope = RemoteWmiHelper.CreateScope(host, username, password, timeout: DirectWmiQueryTimeout);
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

        if (cleanupTargets.Length == 0)
            return new DiskCleanupResult([]);

        var reported = new Dictionary<string, DiskCleanupTargetResult>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();
        var completed = 0;
        void OnOutputLine(string line)
        {
            if (TryParseCleanupResultLine(line, out var path, out var success, out var message))
            {
                lock (sync)
                {
                    if (reported.ContainsKey(path))
                        return;

                    reported[path] = new DiskCleanupTargetResult(path, success, message);
                    completed++;
                }

                _log.Debug($"清理目标结果: host={host} path={path} success={success} message={message}");
                progress?.Report(new DiskCleanupProgress(
                    completed, cleanupTargets.Length, path, success,
                    success
                        ? $"({completed}/{cleanupTargets.Length}) 已清理并验证：{path}"
                        : $"({completed}/{cleanupTargets.Length}) 清理失败：{path}"));
                return;
            }

            if (!string.IsNullOrWhiteSpace(line))
                _log.Debug($"清理执行输出: host={host} {line}");
        }

        var script = BuildBatchCleanupScript(cleanupTargets);
        _log.Info($"Cleaning batch: host={host} targets={cleanupTargets.Length} mode=single-transport");
        CommandResult? executionResult = null;
        try
        {
            if (HostHelper.IsLocalHost(host))
            {
                executionResult = await _psExec.ExecuteWithOutputElevatedAsync(
                    host, username, password, script, OnOutputLine, ct, silent: true,
                    shell: CommandShell.PowerShell);
            }
            else
            {
                // A single PowerShell payload keeps the remote operation atomic from the
                // transport perspective and avoids starting PsExec/WMI once per path.
                // Capabilities are probed exactly once for this top-level cleanup; the
                // resulting plan is reused for the whole payload, and a non-zero remote
                // exit code is final (never retried on another channel).
                var session = await _execution.CreateSessionAsync(host, username, password, ct);
                var transport = await session.ExecuteAsync(
                    RemoteOperationKind.Command,
                    new RemoteCommand
                    {
                        TargetHost = host,
                        Username = username,
                        Password = password,
                        Command = script,
                        Shell = CommandShell.PowerShell,
                        Silent = true,
                    },
                    OnOutputLine,
                    ct);
                executionResult = transport.Result;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"批量清理执行异常: host={host} {ex.Message}");
        }

        var results = new List<DiskCleanupTargetResult>(cleanupTargets.Length);
        foreach (var target in cleanupTargets)
        {
            DiskCleanupTargetResult result;
            lock (sync)
            {
                if (reported.TryGetValue(target.Path, out var existing))
                {
                    result = existing;
                }
                else
                {
                    var transportError = executionResult is not null && !executionResult.Success
                        ? GetCleanupError(executionResult)
                        : "未收到目标验证结果，清理通道执行失败；为避免重复删除，本次未自动重试。";
                    result = new DiskCleanupTargetResult(target.Path, false, transportError);
                    completed++;
                }
            }

            results.Add(result);
            var mode = target.DeleteDirectory ? "删除目录本身" : "保留目录本身";
            if (result.Success)
                _log.Info($"清理并验证完成: host={host} path={target.Path} mode={mode}");
            else
                _log.Error($"清理失败: host={host} path={target.Path} mode={mode}\n{result.Message}");

            // A target can be reported before this final reconciliation pass. Do not
            // report it twice; missing targets are reported here with a failure state.
            bool missingReport;
            lock (sync)
            {
                missingReport = !reported.ContainsKey(target.Path);
            }

            if (missingReport)
            {
                progress?.Report(new DiskCleanupProgress(
                    completed, cleanupTargets.Length, target.Path, false,
                    $"({completed}/{cleanupTargets.Length}) 清理失败：{target.Path}"));
            }
        }

        var cleanupResult = new DiskCleanupResult(results);
        if (cleanupResult.Success)
            _log.Info($"Disk cleanup completed and verified. targets={cleanupResult.SucceededCount}");
        else
            _log.Warn($"磁盘清理完成，但以下目标失败: {string.Join(", ", results.Where(r => !r.Success).Select(r => r.Path))}");

        InvalidateDiskInfoCache(host, username);
        return cleanupResult;
    }

    private static string GetCleanupError(CommandResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        return string.IsNullOrWhiteSpace(error)
            ? $"清理命令执行失败，退出码: {result.ExitCode}"
            : error.Trim();
    }
    internal static string BuildBatchCleanupScript(IReadOnlyList<DiskCleanupTarget> targets)
    {
        var specs = string.Join(Environment.NewLine, targets.Select(target =>
        {
            var path64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(target.Path));
            var delete = target.DeleteDirectory ? "$true" : "$false";
            return $"    @{{ Path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{path64}')); DeleteDirectory = {delete} }}";
        }));

        return $$"""
$ErrorActionPreference = 'Stop'
$targets = @(
{{specs}}
)

function Write-Result {
    param([string]$Path, [bool]$Success, [string]$Message)
    $path64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Path))
    $message64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Message))
    $flag = if ($Success) { '1' } else { '0' }
    [Console]::Out.WriteLine("REMOTEOPSTOOL_RESULT|$path64|$flag|$message64")
}

function Invoke-CleanupTarget {
    param([string]$TargetPath, [bool]$DeleteDirectory)
    try {
        $hasWildcard = $TargetPath.IndexOfAny([char[]]'*?') -ge 0
        if ($hasWildcard) {
            # Escape all PowerShell wildcard syntax first, then re-enable only * and ?.
            # This makes ordinary wildcards work while keeping [ ] literal.
            $wildcardPath = [System.Management.Automation.WildcardPattern]::Escape($TargetPath)
            $wildcardPath = $wildcardPath.Replace('`*', '*').Replace('`?', '?')
            $matches = @(Get-Item -Path $wildcardPath -Force -ErrorAction SilentlyContinue)
            if ($matches.Count -eq 0) {
                Write-Result $TargetPath $true "通配符未匹配任何目标，跳过: $TargetPath"
                return $true
            }
        }
        elseif (-not (Test-Path -LiteralPath $TargetPath)) {
            Write-Result $TargetPath $true "目标不存在，跳过: $TargetPath"
            return $true
        }
        else {
            $matches = @(Get-Item -LiteralPath $TargetPath -Force)
        }

        foreach ($match in $matches) {
            if ($match.PSIsContainer -and $DeleteDirectory) {
                $rootPath = [System.IO.Path]::GetPathRoot($match.FullName)
                if ($rootPath -and $match.FullName.TrimEnd('\') -eq $rootPath.TrimEnd('\')) {
                    throw "禁止删除磁盘根目录或共享根目录: $($match.FullName)"
                }
                Remove-Item -LiteralPath $match.FullName -Force -Recurse
                if (Test-Path -LiteralPath $match.FullName) {
                    throw "目录删除后仍然存在: $($match.FullName)"
                }
            }
            elseif ($match.PSIsContainer) {
                Get-ChildItem -LiteralPath $match.FullName -Force | Remove-Item -Force -Recurse
                $remaining = @(Get-ChildItem -LiteralPath $match.FullName -Force -ErrorAction SilentlyContinue)
                if ($remaining.Count -gt 0) {
                    throw "目录清理后仍有 $($remaining.Count) 项内容存在: $($match.FullName)"
                }
            }
            else {
                Remove-Item -LiteralPath $match.FullName -Force
                if (Test-Path -LiteralPath $match.FullName) {
                    throw "文件删除后仍然存在: $($match.FullName)"
                }
            }
        }

        $action = if ($DeleteDirectory) { '删除' } else { '清空' }
        Write-Result $TargetPath $true "清理完成并验证: $TargetPath（$action $($matches.Count) 项）"
        return $true
    }
    catch {
        Write-Result $TargetPath $false ("清理失败: " + $_.Exception.Message)
        return $false
    }
}

$allOk = $true
foreach ($target in $targets) {
    if (-not (Invoke-CleanupTarget $target.Path $target.DeleteDirectory)) {
        $allOk = $false
    }
}
if ($allOk) { exit 0 } else { exit 1 }
""";
    }

    internal static bool TryParseCleanupResultLine(
        string line, out string path, out bool success, out string message)
    {
        path = string.Empty;
        success = false;
        message = string.Empty;
        const string prefix = "REMOTEOPSTOOL_RESULT|";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var parts = line.Split('|', 4, StringSplitOptions.None);
        if (parts.Length != 4 || (parts[2] != "0" && parts[2] != "1"))
            return false;
        try
        {
            path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            message = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            success = parts[2] == "1";
            return !string.IsNullOrWhiteSpace(path);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string BuildCleanupScript(string path, bool deleteDirectory)
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
