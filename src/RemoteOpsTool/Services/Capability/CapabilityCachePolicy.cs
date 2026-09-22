using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Capability;

/// <summary>
/// Probe-level cache and cooldown policy. Network reachability and PsExec are
/// intentionally short-lived, while structured WMI health is reused longer.
/// </summary>
internal static class CapabilityCachePolicy
{
    private static readonly TimeSpan NetworkSuccessTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkFailureTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionSuccessTtl = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SessionFailureTtl = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PsExecSuccessTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ManagementSuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScheduledTaskSuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MissingProbeTtl = TimeSpan.FromSeconds(30);

    public static DateTimeOffset GetExpiresAt(
        string probeName,
        RemoteCapabilityInfo probe,
        DateTimeOffset checkedAt)
    {
        var ttl = GetTtl(probeName, probe);
        return checkedAt + ttl;
    }

    public static TimeSpan GetTtl(string probeName, RemoteCapabilityInfo probe)
    {
        if (probeName is CapabilityProbeCatalog.Ping or
            CapabilityProbeCatalog.Smb or
            CapabilityProbeCatalog.Rpc or
            CapabilityProbeCatalog.WinRm)
        {
            return probe.Success ? NetworkSuccessTtl : NetworkFailureTtl;
        }

        if (probeName.Equals(CapabilityProbeCatalog.Session, StringComparison.OrdinalIgnoreCase))
            return probe.Success ? SessionSuccessTtl : SessionFailureTtl;

        if (probeName.Equals(CapabilityProbeCatalog.AdminShare, StringComparison.OrdinalIgnoreCase))
            return probe.Success ? NetworkSuccessTtl : CapabilityPolicy.TransientFailureTtl;

        if (!probe.Success)
        {
            var failureKind = CapabilityPolicy.ClassifyProbeFailure(probe.Detail);
            return CapabilityPolicy.GetFailureTtl(failureKind);
        }

        if (probeName.Equals(CapabilityProbeCatalog.Wmi, StringComparison.OrdinalIgnoreCase) ||
            probeName.Equals(CapabilityProbeCatalog.ScheduledTask, StringComparison.OrdinalIgnoreCase))
        {
            return probeName.Equals(CapabilityProbeCatalog.ScheduledTask, StringComparison.OrdinalIgnoreCase)
                ? ScheduledTaskSuccessTtl
                : ManagementSuccessTtl;
        }

        if (probeName.Equals(CapabilityProbeCatalog.PsExec, StringComparison.OrdinalIgnoreCase))
            return PsExecSuccessTtl;

        return MissingProbeTtl;
    }

    public static TimeSpan GetMissingTtl() => MissingProbeTtl;
}
