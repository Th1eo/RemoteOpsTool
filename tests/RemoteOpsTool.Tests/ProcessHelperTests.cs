using System.Collections.Concurrent;
using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class ProcessHelperTests
{
    [Fact]
    public async Task RunWithOutputAsync_StreamsStdOutAndStdErrToCallback()
    {
        var lines = new ConcurrentQueue<string>();
        var commandShell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        var result = await ProcessHelper.RunWithOutputAsync(
            commandShell,
            "/d /c \"echo stdout-line&echo stderr-line 1>&2\"",
            lines.Enqueue);

        Assert.True(result.Success, result.StdErr);
        Assert.Contains(lines, line => line.StartsWith("stdout-line", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("stderr-line", StringComparison.Ordinal));
    }
}