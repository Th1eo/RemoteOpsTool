namespace RemoteOpsTool.Models;

public class ProcessDetailInfo
{
    public string ProcessName { get; set; } = "";
    public int ProcessId { get; set; }
    public int SessionId { get; set; } = -1;
    public string SessionName { get; set; } = "";
    public string MemoryMB { get; set; } = "";
    public string CpuTime { get; set; } = "";
    public string Status { get; set; } = "";
    public string UserName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
}
