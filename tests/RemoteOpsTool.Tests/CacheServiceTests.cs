using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public sealed class CacheServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(),
        "RemoteOpsTool.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RegistryValuesSubtreePrefix_UsesNormalizedRegistryPath()
    {
        var prefix = CacheKeys.RegistryValuesSubtreePrefix(@"HKLM:\SOFTWARE\Vendor");

        Assert.Equal(CacheKeys.RegistryValues(@"HKLM:\SOFTWARE\Vendor"), prefix);

    }

    [Fact]
    public async Task InvalidateByPrefix_RemovesRegistryKeySubtreeButKeepsSibling()
    {
        var cache = new CacheService(new TestLogService(), _cacheRoot);
        const string host = "REMOTE01";
        const string parentPath = @"HKLM:\SOFTWARE\Vendor";
        const string childPath = @"HKLM:\SOFTWARE\Vendor\Child";
        const string siblingPath = @"HKLM:\SOFTWARE\Vendor2";

        await cache.SetAsync(host, CacheKeys.RegistryValues(parentPath), new List<string> { "parent" });
        await cache.SetAsync(host, CacheKeys.RegistryValues(childPath), new List<string> { "child" });
        await cache.SetAsync(host, CacheKeys.RegistryValues(siblingPath), new List<string> { "sibling" });

        cache.InvalidateByPrefix(host, CacheKeys.RegistryValuesSubtreePrefix(parentPath));

        Assert.Null(await cache.GetAsync<List<string>>(host, CacheKeys.RegistryValues(parentPath)));
        Assert.Null(await cache.GetAsync<List<string>>(host, CacheKeys.RegistryValues(childPath)));
        Assert.NotNull(await cache.GetAsync<List<string>>(host, CacheKeys.RegistryValues(siblingPath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }
}