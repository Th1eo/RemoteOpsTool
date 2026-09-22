using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class CleanupPathNormalizationTests
{
    [Theory]
    [InlineData("\"C:\\Temp\\file name.log\"", @"C:\Temp\file name.log")]
    [InlineData("  \"\\\\server\\share\\folder\\*.tmp\"  ", @"\\server\share\folder\*.tmp")]
    [InlineData("\"C:\\Temp\\Old Folder\\\"", @"C:\Temp\Old Folder\")]
    [InlineData(@"C:\Temp\*.log", @"C:\Temp\*.log")]
    [InlineData("\"\"", "")]
    public void NormalizeCleanupPath_HandlesWindowsCopyAsPath(string input, string expected)
    {
        Assert.Equal(expected, FileDiskService.NormalizeCleanupPath(input));
    }
}