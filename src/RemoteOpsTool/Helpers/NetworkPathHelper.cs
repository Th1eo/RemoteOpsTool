namespace RemoteOpsTool.Helpers;

public static class NetworkPathHelper
{
    public static string BuildAdminShare(string host, string driveLetter)
    {
        var drive = driveLetter.Trim().TrimEnd(':').ToUpperInvariant();
        if (HostHelper.IsLocalHost(host))
            return $"{drive}:\\";

        var remoteHost = host.Trim().Trim('\\');
        return $"\\\\{remoteHost}\\{drive}$";
    }

    public static string BuildPublicDesktop(string host)
    {
        if (HostHelper.IsLocalHost(host))
        {
            var localDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            return string.IsNullOrWhiteSpace(localDesktop)
                ? @"C:\Users\Public\Desktop"
                : localDesktop;
        }

        var remoteHost = host.Trim().Trim('\\');
        return $"\\\\{remoteHost}\\c$\\Users\\Public\\Desktop";
    }
}
