using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class RemoteWmiConnectionPoolTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void BuildConnectionPoolKey_NormalizesHostAndUsernameButSeparatesCredentials()
    {
        var first = RemoteWmiHelper.BuildConnectionPoolKey(
            @"\\REMOTE01\", @"DOMAIN\Admin", "Secret!", @"root\cimv2", DefaultTimeout);
        var sameLogicalTarget = RemoteWmiHelper.BuildConnectionPoolKey(
            "remote01", @"domain\admin", "Secret!", @"ROOT\CIMV2", DefaultTimeout);
        var differentPassword = RemoteWmiHelper.BuildConnectionPoolKey(
            "remote01", @"domain\admin", "OtherSecret!", @"root\cimv2", DefaultTimeout);
        var differentNamespace = RemoteWmiHelper.BuildConnectionPoolKey(
            "remote01", @"domain\admin", "Secret!", @"root\default", DefaultTimeout);
        var differentTimeout = RemoteWmiHelper.BuildConnectionPoolKey(
            "remote01", @"domain\admin", "Secret!", @"root\cimv2", TimeSpan.FromSeconds(8));

        Assert.Equal(first, sameLogicalTarget);
        Assert.NotEqual(first, differentPassword);
        Assert.NotEqual(first, differentNamespace);
        Assert.NotEqual(first, differentTimeout);
        Assert.DoesNotContain("Secret!", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"domain\admin", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearConnectionPool_RemovesOnlyMatchingPool()
    {
        var key = RemoteWmiHelper.BuildConnectionPoolKey(
            "REMOTE-POOL-TEST", "user", "password", @"root\cimv2", DefaultTimeout);
        _ = RemoteWmiConnectionPoolRegistry.GetOrCreate(
            key,
            () => throw new InvalidOperationException("factory should not run"));

        Assert.True(RemoteWmiConnectionPoolRegistry.Contains(key));

        RemoteWmiHelper.ClearConnectionPool("", "", "");
        Assert.True(RemoteWmiConnectionPoolRegistry.Contains(key));

        RemoteWmiHelper.ClearConnectionPool(
            "REMOTE-POOL-TEST", "user", "password", @"root\cimv2", DefaultTimeout);
        Assert.False(RemoteWmiConnectionPoolRegistry.Contains(key));
    }
}
