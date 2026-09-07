using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class SessionHelperTests
{
    [Fact]
    public void ParseSessionId_ReturnsActiveConsoleIdFromCapturedOutput()
    {
        const string output = """
             SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE
            > services                                            0  Disc\
            > console                   opsuser1                 2  Active\
            > opsuser2                  8  Disc\
            > rdp-tcp                                         65536  Listen\
            """;

        Assert.Equal(2, SessionHelper.ParseSessionId(output));
    }

    [Fact]
    public void ParseSessionId_ReturnsActiveRdpIdEvenWithoutConsole()
    {
        const string output = "rdp-tcp#5  user.name  7  Active\\";

        Assert.Equal(7, SessionHelper.ParseSessionId(output));
    }

    [Fact]
    public void ParseSessionId_DoesNotSelectDisconnectedConsole()
    {
        const string output = "console  user.name  2  Disc\\";

        Assert.Equal(-1, SessionHelper.ParseSessionId(output));
    }
}
