using RemoteOpsTool.Models;
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

    [Fact]
    public void BuildProcessTerminationOrder_ReturnsChildrenBeforeParents()
    {
        var order = NetworkService.BuildProcessTerminationOrder(
            new[]
            {
                (1, 0),
                (2, 1),
                (3, 1),
                (4, 2),
            },
            1);

        Assert.Equal(4, order.Count);
        Assert.Equal(1, order[^1]);
        Assert.True(order.ToList().IndexOf(2) < order.ToList().IndexOf(1));
        Assert.True(order.ToList().IndexOf(3) < order.ToList().IndexOf(1));
        Assert.True(order.ToList().IndexOf(4) < order.ToList().IndexOf(2));
    }

    [Fact]
    public void BuildProcessTerminationOrder_DeduplicatesPidsAndHandlesCycles()
    {
        var order = NetworkService.BuildProcessTerminationOrder(
            new[]
            {
                (1, 0),
                (2, 1),
                (2, 1),
                (3, 2),
                (2, 3),
            },
            1);

        Assert.Equal(new[] { 3, 2, 1 }, order);
    }

    [Fact]
    public void BuildProcessTerminationOrder_KeepsMissingRootForTaskkillFallback()
    {
        var order = NetworkService.BuildProcessTerminationOrder(
            new[] { (2, 1), (3, 1) },
            9);

        Assert.Equal(new[] { 9 }, order);
    }

    [Theory]
    [InlineData("lsass.exe")]
    [InlineData("LSASS")]
    [InlineData("wininit.exe")]
    [InlineData("services.exe")]
    [InlineData("csrss.exe")]
    [InlineData("smss.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("svchost.exe")]
    public void TryValidateRestartTarget_RejectsCriticalSystemProcesses(string processName)
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = processName,
            ProcessId = 1234,
            ExecutablePath = @"C:\Windows\System32\" + processName
        };

        Assert.False(NetworkService.TryValidateRestartTarget(process, out var reason));
        Assert.Contains("关键系统进程", reason);
    }

    [Fact]
    public void TryValidateRestartTarget_RejectsInvalidProcessId()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "notepad.exe",
            ProcessId = 0,
            ExecutablePath = @"C:\Windows\System32\notepad.exe"
        };

        Assert.False(NetworkService.TryValidateRestartTarget(process, out var reason));
        Assert.Contains("进程 ID 无效", reason);
    }

    [Fact]
    public void TryValidateRestartTarget_RejectsMissingExecutablePath()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "app.exe",
            ProcessId = 4242,
            ExecutablePath = ""
        };

        Assert.False(NetworkService.TryValidateRestartTarget(process, out var reason));
        Assert.Contains("可执行文件路径", reason);
    }

    [Fact]
    public void TryValidateRestartTarget_AcceptsOrdinaryInteractiveProcess()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "app.exe",
            ProcessId = 4242,
            SessionId = 1,
            ExecutablePath = @"C:\Apps\app.exe",
            CommandLine = @"""C:\Apps\app.exe"" --mode=prod"
        };

        Assert.True(NetworkService.TryValidateRestartTarget(process, out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void BuildRestartCommandLine_PrefersOriginalCommandLine()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "app.exe",
            ExecutablePath = @"C:\Apps\app.exe",
            CommandLine = @"""C:\Apps\app.exe"" --mode=prod"
        };

        Assert.Equal(@"""C:\Apps\app.exe"" --mode=prod", NetworkService.BuildRestartCommandLine(process));
    }

    [Fact]
    public void BuildRestartCommandLine_QuotesExecutablePathWhenCommandLineIsMissing()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "app.exe",
            ExecutablePath = @"C:\Program Files\My App\app.exe",
            CommandLine = ""
        };

        Assert.Equal(@"""C:\Program Files\My App\app.exe""", NetworkService.BuildRestartCommandLine(process));
    }

    [Fact]
    public void BuildRestartCommandLine_RequotesUnquotedPathWithSpaces()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "app.exe",
            ExecutablePath = @"C:\Program Files\My App\app.exe",
            CommandLine = @"C:\Program Files\My App\app.exe --mode=prod"
        };

        Assert.Equal(
            @"""C:\Program Files\My App\app.exe"" --mode=prod",
            NetworkService.BuildRestartCommandLine(process));
    }

    [Fact]
    public void BuildRestartCommandLine_DoesNotTouchCommandLineForPathWithoutSpaces()
    {
        var process = new ProcessDetailInfo
        {
            ProcessName = "app.exe",
            ExecutablePath = @"C:\Apps\app.exe",
            CommandLine = @"C:\Apps\app.exe --mode=prod"
        };

        Assert.Equal(@"C:\Apps\app.exe --mode=prod", NetworkService.BuildRestartCommandLine(process));
    }

    [Theory]
    [InlineData(0u, "")]
    [InlineData(2u, "拒绝访问")]
    [InlineData(9u, "找不到可执行文件路径")]
    public void DescribeWmiCreateFailure_MapsKnownReturnCodes(uint returnValue, string expectedFragment)
    {
        var message = NetworkService.DescribeWmiCreateFailure(returnValue);

        if (expectedFragment.Length == 0)
            Assert.Contains("代码", message);
        else
            Assert.Contains(expectedFragment, message);
    }
}
