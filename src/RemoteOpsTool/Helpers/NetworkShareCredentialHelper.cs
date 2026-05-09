using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteOpsTool.Helpers;

public static class NetworkShareCredentialHelper
{
    private const int ResourceTypeDisk = 0x00000001;
    private const int NoError = 0;
    private const int ErrorSessionCredentialConflict = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public int dwScope;
        public int dwType = ResourceTypeDisk;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        NetResource lpNetResource,
        string? lpPassword,
        string? lpUserName,
        int dwFlags);

    public static (bool Success, string Message) EnsureConnection(
        string uncPath,
        string username,
        string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            return (true, "No alternate credential specified.");

        var resource = new NetResource { lpRemoteName = uncPath };
        var result = WNetAddConnection2(resource, password, username, 0);
        if (result == NoError)
            return (true, "Connected.");

        if (result == ErrorSessionCredentialConflict)
            return (true, "An existing SMB session is already connected.");

        return (false, new Win32Exception(result).Message);
    }
}
