using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class HostHelperTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(".")]
    public void IsLocalHost_RecognizesLoopbackAliases(string host)
    {
        Assert.True(HostHelper.IsLocalHost(host));
    }

    [Fact]
    public void IsLocalHost_RecognizesComputerNameAndWhitespace()
    {
        Assert.True(HostHelper.IsLocalHost($"  \\{Environment.MachineName}\\  "));
    }

    [Fact]
    public void NetworkPathHelper_UsesLocalPathsForLocalComputerName()
    {
        Assert.Equal("C:\\", NetworkPathHelper.BuildAdminShare(Environment.MachineName, "c:"));
        Assert.DoesNotContain("\\\\", NetworkPathHelper.BuildPublicDesktop(Environment.MachineName));
    }

    [Fact]
    public void NetworkPathHelper_UsesUncPathsForRemoteComputer()
    {
        Assert.Equal(@"\\REMOTE01\D$", NetworkPathHelper.BuildAdminShare("REMOTE01", "d:"));
        Assert.Equal(@"\\REMOTE01\c$\Users\Public\Desktop", NetworkPathHelper.BuildPublicDesktop("REMOTE01"));
    }
}
