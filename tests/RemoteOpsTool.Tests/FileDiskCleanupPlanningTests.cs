using RemoteOpsTool.Models;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class FileDiskCleanupPlanningTests
{
    [Fact]
    public void BuildBatchCleanupScript_ContainsAllTargetsAndDeleteModes()
    {
        var script = FileDiskService.BuildBatchCleanupScript([
            new DiskCleanupTarget(@"C:\Temp\*.log", false),
            new DiskCleanupTarget(@"C:\Temp\cache", true)
        ]);

        Assert.Contains("REMOTEOPSTOOL_RESULT|", script);
        Assert.Contains("WildcardPattern]::Escape", script);
        Assert.Contains("Replace('`*', '*').Replace('`?', '?')", script);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"C:\Temp\*.log")), script);
        Assert.Contains("DeleteDirectory = $false", script);
        Assert.Contains("DeleteDirectory = $true", script);
        Assert.Contains("禁止删除磁盘根目录或共享根目录", script);
    }

    [Fact]
    public void TryParseCleanupResultLine_RoundTripsUnicodePipeAndSpaces()
    {
        var path = @"C:\临时目录\a|b *.log";
        var message = "清理完成：已删除 2 项 | 已验证";
        var path64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path));
        var message64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message));

        var parsed = FileDiskService.TryParseCleanupResultLine(
            $"REMOTEOPSTOOL_RESULT|{path64}|1|{message64}",
            out var parsedPath, out var success, out var parsedMessage);

        Assert.True(parsed);
        Assert.Equal(path, parsedPath);
        Assert.True(success);
        Assert.Equal(message, parsedMessage);
    }

    [Fact]
    public void TryParseCleanupResultLine_RejectsMalformedProtocol()
    {
        Assert.False(FileDiskService.TryParseCleanupResultLine(
            "REMOTEOPSTOOL_RESULT|not-base64|1|bad", out _, out _, out _));
        Assert.False(FileDiskService.TryParseCleanupResultLine(
            "ordinary output", out _, out _, out _));
    }
}
