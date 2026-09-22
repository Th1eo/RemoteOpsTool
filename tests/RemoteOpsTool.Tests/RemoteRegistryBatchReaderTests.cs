using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class RemoteRegistryBatchReaderTests
{
    [Theory]
    [InlineData(2u, "GetExpandedStringValue")]
    [InlineData(3u, "GetBinaryValue")]
    [InlineData(4u, "GetDWORDValue")]
    [InlineData(7u, "GetMultiStringValue")]
    [InlineData(11u, "GetQWORDValue")]
    [InlineData(1u, "GetStringValue")]
    [InlineData(99u, "GetStringValue")]
    public void GetValueMethod_MapsRegistryTypes(uint type, string expected)
    {
        Assert.Equal(expected, RemoteRegistryBatchReader.GetValueMethod(type));
    }

    [Fact]
    public async Task ReadValuesAsync_EmptyNamesReturnsEmptyArray()
    {
        var values = await RemoteRegistryBatchReader.ReadValuesAsync(
            "REMOTE01", "user", "secret", 0, "Environment", [], []);

        Assert.Empty(values);
    }

    [Fact]
    public async Task ReadValuesAsync_HonorsPreCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteRegistryBatchReader.ReadValuesAsync(
                "REMOTE01", "user", "secret", 0, "Environment",
                ["Path"], [1u], cts.Token));
    }
    [Fact]
    public async Task ReadValuesAsync_EmptyRequestBatchReturnsEmptyArray()
    {
        var values = await RemoteRegistryBatchReader.ReadValuesAsync(
            "REMOTE01", "user", "secret", [], CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public async Task ReadValuesAsync_RequestBatchHonorsPreCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var requests = new[]
        {
            new RemoteRegistryBatchReader.RegistryValueRequest(0, "Environment", "Path", 1u)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteRegistryBatchReader.ReadValuesAsync(
                "REMOTE01", "user", "secret", requests, cts.Token));
    }}
