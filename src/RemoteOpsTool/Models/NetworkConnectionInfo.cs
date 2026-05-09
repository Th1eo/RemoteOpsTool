namespace RemoteOpsTool.Models;

public class NetworkConnectionInfo
{
    public string Protocol { get; set; } = string.Empty;
    public string LocalAddress { get; set; } = string.Empty;
    public string RemoteAddress { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
}
