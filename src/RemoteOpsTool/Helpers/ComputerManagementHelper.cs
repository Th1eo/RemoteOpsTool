namespace RemoteOpsTool.Helpers;

public static class ComputerManagementHelper
{
    public static CommandResult OpenRemote(string targetHost, string username, string password)
    {
        var plan = BuildLaunchPlan(targetHost);
        return ProcessHelper.StartWithNetworkCredentials(
            plan.FileName,
            plan.Arguments,
            username,
            password,
            Environment.SystemDirectory);
    }

    internal static ComputerManagementLaunchPlan BuildLaunchPlan(string targetHost)
    {
        var normalizedHost = HostHelper.NormalizeHost(targetHost);
        var mmcPath = Path.Combine(Environment.SystemDirectory, "mmc.exe");
        return new ComputerManagementLaunchPlan(
            mmcPath,
            ["compmgmt.msc", $"/computer=\\\\{normalizedHost}"],
            normalizedHost);
    }
}

internal sealed record ComputerManagementLaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string TargetHost);
