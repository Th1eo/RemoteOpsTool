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

    [Fact]
    public void Software_UsesNormalizedUsernameAndSeparatesCleanupModes()
    {
        var normal = CacheKeys.Software(deepCleanup: false, @"CONTOSO\Alice");
        var sameUser = CacheKeys.Software(deepCleanup: false, "contoso\\alice");
        var deep = CacheKeys.Software(deepCleanup: true, @"contoso\alice");
        var otherUser = CacheKeys.Software(deepCleanup: false, @"contoso\bob");

        Assert.Equal(normal, sameUser);
        Assert.NotEqual(normal, deep);
        Assert.NotEqual(normal, otherUser);
    }

    [Fact]
    public async Task SoftwareInvalidation_OnlyRemovesCurrentCredentialSnapshots()
    {
        var cache = new CacheService(new TestLogService(), _cacheRoot);
        const string host = "REMOTE01";
        var aliceNormal = CacheKeys.Software(false, @"CONTOSO\Alice");
        var aliceDeep = CacheKeys.Software(true, @"CONTOSO\Alice");
        var bobNormal = CacheKeys.Software(false, @"CONTOSO\Bob");

        await cache.SetAsync(host, aliceNormal, new List<string> { "alice-normal" });
        await cache.SetAsync(host, aliceDeep, new List<string> { "alice-deep" });
        await cache.SetAsync(host, bobNormal, new List<string> { "bob-normal" });

        cache.Invalidate(host, CacheKeys.Software(false, @"contoso\alice"));
        cache.Invalidate(host, CacheKeys.Software(true, @"contoso\alice"));

        Assert.Null(await cache.GetAsync<List<string>>(host, aliceNormal));
        Assert.Null(await cache.GetAsync<List<string>>(host, aliceDeep));
        Assert.NotNull(await cache.GetAsync<List<string>>(host, bobNormal));
    }
    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }
}