using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class NetworkServiceTests
{
    [Fact]
    public void ParseNetstatOutput_ParsesTcpAndUdpWithoutHeaderOffset()
    {
        const string output = """
        Active Connections

          Proto  Local Address          Foreign Address        State           PID
          TCP    10.0.0.5:50210         10.0.0.8:443           ESTABLISHED     4120
          UDP    0.0.0.0:5353           *:*                                    8844
        """;
        var names = new Dictionary<int, string>
        {
            [4120] = "chrome.exe",
            [8844] = "svchost.exe"
        };

        var rows = NetworkService.ParseNetstatOutput(output, names);

        Assert.Equal(2, rows.Count);
        Assert.Equal("TCP", rows[0].Protocol);
        Assert.Equal("10.0.0.5:50210", rows[0].LocalAddress);
        Assert.Equal("10.0.0.8:443", rows[0].RemoteAddress);
        Assert.Equal("ESTABLISHED", rows[0].State);
        Assert.Equal("chrome.exe", rows[0].ProcessName);
        Assert.Equal("UDP", rows[1].Protocol);
        Assert.Equal("*:*", rows[1].RemoteAddress);
        Assert.Empty(rows[1].State);
        Assert.Equal(8844, rows[1].ProcessId);
    }

    [Fact]
    public void ParseNetstatOutput_IgnoresLocalizedHeadersAndKeepsProtocolRows()
    {
        const string output = """
        活动连接

          协议  本地地址          外部地址        状态            PID
          TCP    [::1]:135          [::]:0          LISTENING       1052
          UDP    [::]:3702          *:*                              2216
        """;

        var rows = NetworkService.ParseNetstatOutput(output, new Dictionary<int, string>());

        Assert.Equal(2, rows.Count);
        Assert.Equal("TCP", rows[0].Protocol);
        Assert.Equal("[::1]:135", rows[0].LocalAddress);
        Assert.Equal("UDP", rows[1].Protocol);
        Assert.Equal(2216, rows[1].ProcessId);
    }

    [Fact]
    public void ParseNetstatOutput_IgnoresMalformedRows()
    {
        const string output = """
          TCP    10.0.0.5:1           10.0.0.8:2             ESTABLISHED     not-a-pid
          not-a-protocol 1 2 3 4
        """;

        var rows = NetworkService.ParseNetstatOutput(output, new Dictionary<int, string>());

        Assert.Empty(rows);
    }
}