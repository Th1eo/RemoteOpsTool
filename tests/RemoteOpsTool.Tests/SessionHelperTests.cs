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
    public void ParseActiveSession_ReturnsDesktopUsernameForTaskFallback()
    {
        const string output = "console  desktop.user  2  Active\\";

        var session = SessionHelper.ParseActiveSession(output);

        Assert.Equal(2, session.SessionId);
        Assert.Equal("desktop.user", session.Username);
    }

    [Fact]
    public void ParseActiveSession_PrefersRequestedActiveSession()
    {
        const string output = """
            console  console.user  2  Active\
            rdp-tcp#5  rdp.user  7  Active\
            """;

        var session = SessionHelper.ParseActiveSession(output, 7);

        Assert.Equal(7, session.SessionId);
        Assert.Equal("rdp.user", session.Username);
    }

    [Fact]
    public void ParseActiveSession_QueryUserFormat_ReturnsUsernameBeforeSessionName()
    {
        const string output = "desktop.user  console  2  Active  none  9/11/2026 8:00 AM";

        var session = SessionHelper.ParseActiveSession(output);

        Assert.Equal(2, session.SessionId);
        Assert.Equal("desktop.user", session.Username);
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
