using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class DeviceServiceTests
{
    [Fact]
    public void DriverVersionIndex_ResolvesExactAndAncestorMatches()
    {
        var index = new DriverVersionIndex(new Dictionary<string, string>
        {
            [@"PCI\VEN_8086&DEV_1234\SUBSYS_A"] = "10.0.1",
            [@"PCI\VEN_8086&DEV_9999\SUBSYS_B"] = "10.0.2"
        });

        Assert.Equal("10.0.1", index.Resolve(@"PCI\VEN_8086&DEV_1234\SUBSYS_A"));
        Assert.Equal("10.0.1", index.Resolve(@"PCI\VEN_8086&DEV_1234"));
    }

    [Fact]
    public void DriverVersionIndex_ResolvesDriverDeviceIdPrefixWithoutFullScan()
    {
        var index = new DriverVersionIndex(new Dictionary<string, string>
        {
            [@"USB\VID_1234&PID_5678\REV_0002"] = "5.6.7"
        });

        Assert.Equal("5.6.7", index.Resolve(@"USB\VID_1234&PID_5678"));
    }

    [Fact]
    public void DriverVersionIndex_IsCaseInsensitiveAndHandlesMissingValues()
    {
        var index = new DriverVersionIndex(new Dictionary<string, string>
        {
            [@"ROOT\DEVICE\0000"] = "1.0.0.0"
        });

        Assert.Equal("1.0.0.0", index.Resolve(@"root\device\0000"));
        Assert.Equal(string.Empty, index.Resolve("UNKNOWN"));
        Assert.Equal(string.Empty, index.Resolve(null));
    }
}
