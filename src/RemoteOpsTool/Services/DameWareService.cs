using System.Diagnostics;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class DameWareService : IDameWareService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    private static readonly string[] CommonPaths =
    [
        @"C:\Program Files\SolarWinds\Dameware Mini Remote Control x64\DWRCC.exe",
        @"C:\Program Files (x86)\SolarWinds\Dameware Mini Remote Control x64\DWRCC.exe",
        @"C:\Program Files\DameWare\DWRCC.exe",
        @"C:\Program Files (x86)\DameWare\DWRCC.exe",
        @"C:\Program Files\DameWare Remote Support\DWRCC.exe"
    ];

    public DameWareService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    public Task ConnectAsync(string targetHost, string username, string password)
    {
        var path = ResolveDameWarePath();
        if (string.IsNullOrEmpty(path))
        {
            _log.Error("DameWare (DWRCC.exe) 未找到。请在高级设置中配置路径。");
            return Task.CompletedTask;
        }

        try
        {
            var (user, domain) = ProcessHelper.SplitUserDomain(username);
            if (string.IsNullOrWhiteSpace(user))
            {
                _log.Error("启动 DameWare 失败: 未提供有效用户名。");
                return Task.CompletedTask;
            }

            // DameWare's CLI requires -p on versions that support credentialed
            // startup. ArgumentList prevents quoting bugs, but the third-party
            // process still receives the password as a command-line argument;
            // never log the complete argument list or expose it in diagnostics.
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = Environment.UserDomainName;
                if (string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    domain = string.Empty;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
            };
            foreach (var argument in new[] { "-h", "-c", $"-m:{targetHost}", $"-u:{user}", $"-p:{password}" })
                startInfo.ArgumentList.Add(argument);
            if (!string.IsNullOrWhiteSpace(domain))
                startInfo.ArgumentList.Add($"-d:{domain}");

            Process.Start(startInfo);
            var identity = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            _log.Info($"DameWare 已启动，正在连接 {targetHost}（用户 {identity}）。");
        }
        catch (Exception ex)
        {
            _log.Error($"启动 DameWare 失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private string ResolveDameWarePath()
    {
        var configured = _settings.Settings.DameWarePath;
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        foreach (var p in CommonPaths)
        {
            if (File.Exists(p))
            {
                _log.Info($"自动发现 DameWare: {p}");
                return p;
            }
        }

        return string.Empty;
    }
}
