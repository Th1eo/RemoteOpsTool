using System.Net;
using System.Net.Sockets;
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
        Assert.True(HostHelper.IsLocalHost(Dns.GetHostName()));
    }

    [Fact]
    public void IsLocalHost_RecognizesLocalFqdnAndTrailingDotWhenAvailable()
    {
        try
        {
            var fqdn = Dns.GetHostEntry(Dns.GetHostName()).HostName;
            if (string.IsNullOrWhiteSpace(fqdn))
                return;

            Assert.True(HostHelper.IsLocalHost(fqdn));
            Assert.True(HostHelper.IsLocalHost(fqdn.TrimEnd('.') + "."));
        }
        catch (SocketException)
        {
            // Some build agents do not have a resolvable local DNS entry.
        }
    }

    [Fact]
    public void IsLocalHost_RecognizesLocalInterfaceAddressesWhenAvailable()
    {
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                Assert.True(HostHelper.IsLocalHost(address.ToString()));
        }
        catch (SocketException)
        {
            // Some build agents do not expose local interface addresses through DNS.
        }
    }

    [Fact]
    public void IsLocalHost_DoesNotClassifyRemoteNameAsLocal()
    {
        Assert.False(HostHelper.IsLocalHost("remoteopstool-definitely-remote.invalid"));
    }

    [Fact]
    public void NormalizeHost_CollapsesAllLocalAliasesToMachineName()
    {
        Assert.Equal(Environment.MachineName, HostHelper.NormalizeHost("localhost"));
        Assert.Equal(Environment.MachineName, HostHelper.NormalizeHost("127.0.0.1"));
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
