using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RemoteOpsTool.Helpers;

public static class HostHelper
{
    private static readonly string LocalMachineName = Environment.MachineName;
    private static readonly Lazy<HashSet<string>> LocalHostAliases = new(
        BuildLocalHostAliases, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HashSet<string>> LocalHostAddresses = new(
        BuildLocalHostAddresses, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        var normalized = NormalizeHostName(host);
        if (LocalHostAliases.Value.Contains(normalized))
            return true;

        if (IPAddress.TryParse(normalized, out var address))
        {
            if (IPAddress.IsLoopback(address))
                return true;

            var canonical = address.MapToIPv6().ToString();
            return LocalHostAddresses.Value.Contains(canonical);
        }

        return false;
    }

    /// <summary>
    /// Returns a stable host value for caches and diagnostics without changing
    /// the host value used to address a remote computer.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        if (IsLocalHost(host))
            return LocalMachineName;

        return NormalizeHostName(host);
    }

    private static string NormalizeHostName(string host)
    {
        var normalized = host.Trim('\\', ' ', '\t', '\r', '\n').TrimEnd('.');
        return string.IsNullOrEmpty(normalized) && host.Trim().Equals(".", StringComparison.Ordinal)
            ? "."
            : normalized;
    }

    private static HashSet<string> BuildLocalHostAliases()
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeHostName(LocalMachineName),
            "localhost",
            "127.0.0.1",
            "::1",
            "."
        };

        AddAlias(aliases, Environment.GetEnvironmentVariable("COMPUTERNAME"));
        try
        {
            var dnsHostName = Dns.GetHostName();
            AddAlias(aliases, dnsHostName);
            var entry = Dns.GetHostEntry(dnsHostName);
            AddAlias(aliases, entry.HostName);
            foreach (var alias in entry.Aliases)
                AddAlias(aliases, alias);
        }
        catch (SocketException)
        {
            // Name service may be unavailable during startup. The stable
            // machine-name and loopback aliases above are still sufficient.
        }
        catch (InvalidOperationException)
        {
            // Defensive: DNS providers can throw this while the network stack
            // is still initializing.
        }

        return aliases;
    }

    private static HashSet<string> BuildLocalHostAddresses()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                addresses.Add(address.MapToIPv6().ToString());
        }
        catch (SocketException)
        {
            // IP matching is an optimization for local FQDN/IP input and must
            // never make host classification fail.
        }
        catch (InvalidOperationException)
        {
        }

        return addresses;
    }

    private static void AddAlias(ISet<string> aliases, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            aliases.Add(NormalizeHostName(value));
    }
}
