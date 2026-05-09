namespace RemoteOpsTool.Models;

public class UserSessionInfo
{
    public string Username { get; set; } = "";
    public string SessionName { get; set; } = "";
    public int SessionId { get; set; }
    public string State { get; set; } = "";
    public string IdleTime { get; set; } = "";
    public string LogonTime { get; set; } = "";
}
