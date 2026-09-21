using System.Management;

namespace RemoteOpsTool.Helpers;

public static class RemoteWmiHelper
{
    public static ManagementScope CreateScope(
        string host,
        string username,
        string password,
        string wmiNamespace = @"root\cimv2",
        TimeSpan? timeout = null)
    {
        var computer = HostHelper.IsLocalHost(host) ? "." : host.Trim('\\', ' ');
        var scopePath = $@"\\{computer}\{wmiNamespace}";

        var options = new ConnectionOptions
        {
            Authentication = AuthenticationLevel.PacketPrivacy,
            Impersonation = ImpersonationLevel.Impersonate,
            EnablePrivileges = true,
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };

        if (!HostHelper.IsLocalHost(host) && !string.IsNullOrWhiteSpace(username))
        {
            var (user, domain) = ProcessHelper.SplitUserDomain(username);
            options.Username = user;
            options.Password = password;

            if (!string.IsNullOrWhiteSpace(domain))
                options.Authority = $"ntlmdomain:{domain}";
            else if (username.Contains('@'))
                options.Username = username;
        }

        return new ManagementScope(scopePath, options);
    }

    public static string EscapeWqlString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    public static string GetString(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName]?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool GetBool(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName] is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    public static ulong GetUInt64(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName] switch
            {
                ulong value => value,
                long value => value >= 0 ? (ulong)value : 0,
                string value when ulong.TryParse(value, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    public static uint GetUInt32(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName] switch
            {
                uint value => value,
                int value => value >= 0 ? (uint)value : 0,
                string value when uint.TryParse(value, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsSuccessReturn(ManagementBaseObject? result)
    {
        if (result == null) return false;
        return GetUInt32(result, "ReturnValue") == 0;
    }
}
