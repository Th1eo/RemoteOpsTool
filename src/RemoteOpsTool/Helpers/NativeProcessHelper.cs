using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteOpsTool.Helpers;

/// <summary>
/// Uses Windows native APIs (same ones Task Manager uses) for fast process info retrieval.
/// No external processes, no WMI overhead.
/// </summary>
public static class NativeProcessHelper
{
    #region Win32 P/Invoke

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer,
        int sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0,
        WTSApplicationName = 1,
        WTSWorkingDirectory = 2,
        WTSOEMId = 3,
        WTSSessionId = 4,
        WTSUserName = 5,
        WTSWinStationName = 6,
        WTSDomainName = 7,
        WTSConnectState = 8,
        WTSClientBuildNumber = 9,
        WTSClientName = 10,
        WTSClientDirectory = 11,
        WTSClientProductId = 12,
        WTSClientHardwareId = 13,
        WTSClientAddress = 14,
        WTSClientDisplay = 15,
        WTSClientProtocolType = 16,
        WTSIdleTime = 17,
        WTSLogonTime = 18,
        WTSIncomingBytes = 19,
        WTSOutgoingBytes = 20,
        WTSIncomingFrames = 21,
        WTSOutgoingFrames = 22,
        WTSClientInfo = 23,
        WTSSessionInfo = 24,
    }

    private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    #endregion

    /// <summary>Gets the username for a session via WTS API (same API Task Manager uses).</summary>
    public static string GetSessionUserName(int sessionId)
    {
        try
        {
            if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId,
                WTS_INFO_CLASS.WTSUserName, out var buffer, out _))
            {
                var username = Marshal.PtrToStringUni(buffer) ?? "";
                WTSFreeMemory(buffer);
                return username;
            }
        }
        catch { }
        return "";
    }

    /// <summary>Gets the domain\username for a session.</summary>
    public static string GetSessionDomainUser(int sessionId)
    {
        try
        {
            var user = GetSessionUserName(sessionId);
            if (string.IsNullOrEmpty(user)) return "";

            if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId,
                WTS_INFO_CLASS.WTSDomainName, out var buffer, out _))
            {
                var domain = Marshal.PtrToStringUni(buffer) ?? "";
                WTSFreeMemory(buffer);
                if (!string.IsNullOrEmpty(domain) && !domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    return $"{domain}\\{user}";
            }
            return user;
        }
        catch { }
        return "";
    }

    /// <summary>Maps session IDs to usernames for all active sessions.</summary>
    public static Dictionary<int, string> BuildSessionUserMap()
    {
        var map = new Dictionary<int, string>();
        try
        {
            var procs = Process.GetProcesses();
            var seenSessions = new HashSet<int>();
            foreach (var p in procs)
            {
                try
                {
                    if (seenSessions.Add(p.SessionId))
                    {
                        var user = GetSessionDomainUser(p.SessionId);
                        if (!string.IsNullOrEmpty(user))
                            map[p.SessionId] = user;
                    }
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Determines process status using IsHungAppWindow (same as Task Manager).
    /// For background processes (no window), returns "Running".
    /// </summary>
    public static string GetProcessStatus(Process p)
    {
        try
        {
            if (p.HasExited) return "Exited";
            var hWnd = p.MainWindowHandle;
            if (hWnd != IntPtr.Zero)
                return IsHungAppWindow(hWnd) ? "Not Responding" : "Running";
            return "Running";
        }
        catch { return ""; }
    }

    /// <summary>Enumerates visible window titles and maps them to process IDs.</summary>
    public static Dictionary<int, string> BuildWindowTitleMap()
    {
        var map = new Dictionary<int, string>();
        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid == 0) return true;

                    var length = GetWindowTextLength(hWnd);
                    if (length == 0) return true;

                    var sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    var title = sb.ToString().Trim();
                    if (string.IsNullOrEmpty(title)) return true;

                    if (!map.TryGetValue(pid, out var existing) || existing.Length < title.Length)
                        map[pid] = title;
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return map;
    }

    /// <summary>Gets the session name (console, rdp-tcp#0, etc.) for a session via WTS API.</summary>
    public static string GetSessionName(int sessionId)
    {
        try
        {
            if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId,
                WTS_INFO_CLASS.WTSWinStationName, out var buffer, out _))
            {
                var name = Marshal.PtrToStringUni(buffer) ?? "";
                WTSFreeMemory(buffer);
                return name;
            }
        }
        catch { }
        return "";
    }

    /// <summary>Builds SessionID to SessionName mapping using WTS API (no external process).</summary>
    public static Dictionary<int, string> BuildSessionNameMap()
    {
        var map = new Dictionary<int, string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!map.ContainsKey(p.SessionId))
                    {
                        var name = GetSessionName(p.SessionId);
                        if (!string.IsNullOrEmpty(name))
                            map[p.SessionId] = name;
                    }
                }
                catch { }
            }
        }
        catch { }
        return map;
    }
}
