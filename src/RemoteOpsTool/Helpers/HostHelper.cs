namespace RemoteOpsTool.Helpers;

public static class HostHelper
{
    private static readonly string LocalMachineName = Environment.MachineName;

    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;
        var h = host.Trim('\\', ' ').Trim();
        return h.Equals(LocalMachineName, StringComparison.OrdinalIgnoreCase)
            || h is "localhost" or "127.0.0.1" or "::1" or ".";
    }

    /// <summary>
    /// Returns a stable host value for caches and diagnostics without changing
    /// the host value used to address a remote computer.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        if (IsLocalHost(host))
            return LocalMachineName;

        return host.Trim('\\', ' ').Trim();
    }
}
