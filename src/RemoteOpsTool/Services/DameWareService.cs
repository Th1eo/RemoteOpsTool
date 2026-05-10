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
            var d = string.IsNullOrEmpty(domain) ? "CONTOSO" : domain;
            var args = $"-h -c -m:{targetHost} -u:{user} -p:{password} -d:{d}";

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _log.Info($"DameWare 已启动，正在连接 {targetHost}...");
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
