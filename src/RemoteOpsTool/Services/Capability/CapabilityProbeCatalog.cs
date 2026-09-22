namespace RemoteOpsTool.Services.Capability;

/// <summary>
/// Defines the minimum probe set for each operation profile and how a partial
/// cache miss is served without repeating unrelated probes.
/// </summary>
internal static class CapabilityProbeCatalog
{
    public const string Ping = "Ping";
    public const string Smb = "SMB 445";
    public const string Rpc = "RPC 135";
    public const string WinRm = "WinRM 5985";
    public const string AdminShare = "ADMIN$ 管理共享";
    public const string Wmi = "WMI/DCOM";
    public const string Session = "会话查询";
    public const string PsExec = "PsExec 临时执行";
    public const string ScheduledTask = "计划任务 RPC";

    private static readonly string[] NetworkProbes = [Ping, Smb, Rpc, WinRm];

    private static readonly string[] FullProbes =
    [
        Ping, Smb, Rpc, WinRm, AdminShare, Wmi, Session, PsExec, ScheduledTask,
    ];

    private static readonly string[] CommandProbes = [AdminShare, Wmi, PsExec];

    public static IReadOnlyList<string> GetRequiredProbes(CapabilityProbeProfile profile) =>
        profile switch
        {
            CapabilityProbeProfile.Full => FullProbes,
            CapabilityProbeProfile.Command => CommandProbes,
            CapabilityProbeProfile.SessionQuery => [Wmi, Session],
            CapabilityProbeProfile.InventoryWmiOnly or
            CapabilityProbeProfile.RegistryWmiOnly or
            CapabilityProbeProfile.CommandWmiFirst => [Wmi],
            _ => [],
        };

    /// <summary>
    /// Selects the smallest built-in probe profile that can produce every missing
    /// result. A missing PsExec result has to use Command because PsExec and its
    /// ADMIN$-share prerequisite are probed together there.
    /// </summary>
    public static CapabilityProbeProfile SelectFetchProfile(
        CapabilityProbeProfile requestedProfile,
        IReadOnlyCollection<string> missingProbes)
    {
        if (missingProbes.Count == 0)
            return requestedProfile;

        if (missingProbes.Any(NetworkProbes.Contains) ||
            missingProbes.Contains(ScheduledTask) ||
            (missingProbes.Contains(Session) && missingProbes.Contains(PsExec)))
        {
            return CapabilityProbeProfile.Full;
        }

        if (missingProbes.Contains(Session))
            return CapabilityProbeProfile.SessionQuery;

        if (missingProbes.Contains(PsExec) || missingProbes.Contains(AdminShare))
            return CapabilityProbeProfile.Command;

        return CapabilityProbeProfile.InventoryWmiOnly;
    }
}
