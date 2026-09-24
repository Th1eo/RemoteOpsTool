using RemoteOpsTool.Constants;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class LogServiceTests
{
    [Fact]
    public void ResolveLogFilePath_UsesPerUserLocationInsteadOfProgramDirectory()
    {
        var path = LogService.ResolveLogFilePath();
        var directory = Path.GetDirectoryName(path);

        Assert.Equal(AppConstants.LogFileName, Path.GetFileName(path));
        Assert.NotNull(directory);

        var expected = Path.GetFullPath(AppConstants.LogFolder);
        var fallback = Path.GetFullPath(Path.Combine(Path.GetTempPath(), AppConstants.CompanyFolder, "logs"));
        var tempRoot = Path.GetFullPath(Path.GetTempPath());

        Assert.True(
            string.Equals(directory, expected, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directory, fallback, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directory, tempRoot, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), directory);
    }
}
