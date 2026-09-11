using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteOpsTool.Helpers;

public static class NetworkShareCredentialHelper
{
    private const int ResourceTypeDisk = 0x00000001;
    private const int NoError = 0;
    private const int ErrorSessionCredentialConflict = 1219;
    private static readonly HashSet<string> ConnectedShares = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SyncRoot = new();

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

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string lpName,
        int dwFlags,
        bool fForce);

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
        {
            lock (SyncRoot) ConnectedShares.Add(uncPath);
            return (true, "Connected.");
        }

        if (result == ErrorSessionCredentialConflict)
        {
            // 1219 means that Windows already has an SMB session for this
            // server. It does not prove that the existing session uses the
            // requested account; treating it as success made Explorer/MMC
            // silently run with the wrong identity.
            return (false, "已有到该服务器的 SMB 会话，可能使用了其他凭据。请先断开现有会话后重试。");
        }

        return (false, new Win32Exception(result).Message);
    }

    public static (int Count, List<string> Errors) DisconnectAll()
    {
        var disconnected = 0;
        var errors = new List<string>();
        List<string> shares;
        lock (SyncRoot) shares = ConnectedShares.ToList();

        foreach (var share in shares)
        {
            var result = WNetCancelConnection2(share, 0, true);
            if (result == NoError)
            {
                disconnected++;
                lock (SyncRoot) ConnectedShares.Remove(share);
            }
            else
            {
                errors.Add($"{share}: {new Win32Exception(result).Message}");
            }
        }

        return (disconnected, errors);
    }
}
