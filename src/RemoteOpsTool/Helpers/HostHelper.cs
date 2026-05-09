namespace RemoteOpsTool.Helpers;

public static class HostHelper
{
    private static readonly string LocalMachineName = Environment.MachineName;

    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrEmpty(host))
            return true;
        var h = host.Trim('\\', ' ').Trim();
        return h.Equals(LocalMachineName, StringComparison.OrdinalIgnoreCase)
            || h is "localhost" or "127.0.0.1" or "::1" or ".";
    }
}
