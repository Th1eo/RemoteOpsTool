namespace RemoteOpsTool.Models;

public class DiskInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public double FreeGB { get; set; }
    public double SizeGB { get; set; }
    public double UsedGB => SizeGB - FreeGB;
    public double UsagePercent => SizeGB > 0 ? Math.Round(UsedGB / SizeGB * 100, 1) : 0;
    public string Display => $"{DeviceId}  {FreeGB:F1} GB / {SizeGB:F1} GB  ({UsagePercent}%)";
}
