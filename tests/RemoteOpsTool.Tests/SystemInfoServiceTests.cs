using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class SystemInfoServiceTests
{
    [Fact]
    public void PartitionRemoteWmiQueries_UsesBoundedBatchesAndPreservesOrder()
    {
        var queries = Enumerable.Range(1, 10).ToArray();

        var batches = SystemInfoService.PartitionRemoteWmiQueries(queries, 4);

        Assert.Equal([3, 3, 2, 2], batches.Select(batch => batch.Length));
        Assert.Equal(queries, batches.SelectMany(batch => batch));
    }

    [Fact]
    public void PartitionRemoteWmiQueries_HandlesEmptyInput()
    {
        var batches = SystemInfoService.PartitionRemoteWmiQueries(Array.Empty<int>(), 4);

        Assert.Empty(batches);
    }

    [Fact]
    public void PartitionRemoteWmiQueries_RejectsNonPositiveBatchCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SystemInfoService.PartitionRemoteWmiQueries(new[] { 1 }, 0));
    }
}