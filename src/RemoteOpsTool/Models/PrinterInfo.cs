namespace RemoteOpsTool.Models;

public class PrinterInfo
{
    public string Name { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public bool Shared { get; set; }
    public bool Default { get; set; }
}
