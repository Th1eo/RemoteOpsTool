using System.Text.Json;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class CacheService : ICacheService
{
    private readonly string _cacheRoot;
    private readonly ILogService _log;

    private static readonly Dictionary<string, TimeSpan> TtlMap = new()
    {
        ["services"] = TimeSpan.FromSeconds(30),
        ["devices"] = TimeSpan.FromMinutes(5),
        ["printers"] = TimeSpan.FromMinutes(5),
        ["sessions"] = TimeSpan.FromMinutes(2),
        ["disk_info"] = TimeSpan.FromSeconds(10),
        ["systeminfo"] = TimeSpan.FromMinutes(15),
    };

    public CacheService(ILogService log)
    {
        _log = log;
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteOpsTool",
            "Cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<T?> GetAsync<T>(string host, string dataKey) where T : class
    {
        var (valid, data) = await TryGetAsync<T>(host, dataKey);
        return valid ? data : null;
    }

    private async Task<(bool Valid, T? Data)> TryGetAsync<T>(string host, string dataKey) where T : class
    {
        var filePath = GetCacheFilePath(host, dataKey);
        if (!File.Exists(filePath))
            return (false, null);

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<T>(json);
            var valid = !IsExpired(filePath, dataKey);
            return (valid, data);
        }
        catch (Exception ex)
        {
            _log.Debug($"缓存读取失败 [{host}/{dataKey}]: {ex.Message}");
            return (false, null);
        }
    }

    public async Task PopulateFromCacheAsync<T>(string host, string dataKey, Action<T> onData) where T : class
    {
        var (valid, data) = await TryGetAsync<T>(host, dataKey);
        if (data != null)
            onData(data);
    }

    public async Task SaveAndPopulateAsync<T>(string host, string dataKey, T data, Action<T> onData) where T : class
    {
        await SetAsync(host, dataKey, data);
        onData(data);
    }

    public async Task SetAsync<T>(string host, string dataKey, T data) where T : class
    {
        var filePath = GetCacheFilePath(host, dataKey);
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            _log.Debug($"缓存写入失败 [{host}/{dataKey}]: {ex.Message}");
        }
    }

    public void Invalidate(string host, string? dataKey = null)
    {
        if (dataKey == null)
        {
            var hostDir = GetHostCacheDirectory(host);
            if (Directory.Exists(hostDir))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(hostDir))
                        File.Delete(file);
                }
                catch { }
            }
        }
        else
        {
            var filePath = GetCacheFilePath(host, dataKey);
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch { }
        }
    }

    public async Task<bool> HasValidCacheAsync(string host, string dataKey)
    {
        var filePath = GetCacheFilePath(host, dataKey);
        if (!File.Exists(filePath))
            return false;

        return !IsExpired(filePath, dataKey);
    }

    public string? GetCacheAge(string host, string dataKey)
    {
        var filePath = GetCacheFilePath(host, dataKey);
        if (!File.Exists(filePath))
            return null;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath);
        if (age.TotalSeconds < 10)
            return "刚刚";
        if (age.TotalSeconds < 60)
            return $"{(int)age.TotalSeconds} 秒前";
        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes} 分钟前";
        return $"{(int)age.TotalHours} 小时前";
    }

    public string GetCacheFilePath(string host, string dataKey)
    {
        var safeHost = SanitizeFileName(host);
        var safeKey = SanitizeFileName(dataKey);
        return Path.Combine(_cacheRoot, safeHost, $"{safeKey}.json");
    }

    private string GetHostCacheDirectory(string host)
    {
        var safeHost = SanitizeFileName(host);
        return Path.Combine(_cacheRoot, safeHost);
    }

    private bool IsExpired(string filePath, string dataKey)
    {
        var ttl = GetTtl(dataKey);
        if (ttl == null)
            return false;

        var lastWrite = File.GetLastWriteTimeUtc(filePath);
        return DateTime.UtcNow - lastWrite > ttl.Value;
    }

    private static TimeSpan? GetTtl(string dataKey)
    {
        foreach (var (pattern, ttl) in TtlMap)
        {
            if (dataKey == pattern || dataKey.StartsWith(pattern + "_", StringComparison.Ordinal))
                return ttl;
        }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "_default" : sanitized;
    }
}
