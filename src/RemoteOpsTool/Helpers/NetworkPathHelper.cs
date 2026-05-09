namespace RemoteOpsTool.Helpers;

public static class NetworkPathHelper
{
    public static string BuildAdminShare(string host, string driveLetter)
    {
        return $"\\\\{host}\\{driveLetter.ToUpperInvariant().TrimEnd(':')}$";
    }

    public static string BuildPublicDesktop(string host)
    {
        return $"\\\\{host}\\c$\\Users\\Public\\Desktop";
    }

}
