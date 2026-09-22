using System.Collections.Concurrent;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace RemoteOpsTool.Helpers;

public static class RemoteWmiHelper
{
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(20);

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
            Timeout = timeout ?? DefaultConnectionTimeout
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

    /// <summary>
    /// Executes a DCOM/WMI action through a bounded, credential-isolated
    /// connection pool. A scope is never shared concurrently; independent
    /// callers can use up to <see cref="RemoteWmiConnectionPool.MaxConcurrentScopes"/>
    /// scopes for the same host/credential/namespace.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        string host,
        string username,
        string password,
        Func<ManagementScope, T> action,
        CancellationToken ct = default,
        string wmiNamespace = @"root\cimv2",
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var effectiveTimeout = timeout ?? DefaultConnectionTimeout;
        var key = BuildConnectionPoolKey(host, username, password, wmiNamespace, effectiveTimeout);
        var pool = RemoteWmiConnectionPoolRegistry.GetOrCreate(
            key,
            () => CreateScope(host, username, password, wmiNamespace, effectiveTimeout));

        using var lease = await pool.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => action(lease.Scope), ct).ConfigureAwait(false);
            lease.MarkHealthy();
            return result;
        }
        catch (OperationCanceledException)
        {
            // Cancellation does not imply that the DCOM scope is broken.
            throw;
        }
        catch
        {
            lease.MarkFailed();
            throw;
        }
    }

    /// <summary>
    /// Releases all cached WMI scopes. Called during application shutdown.
    /// </summary>
    public static void ClearConnectionPools() => RemoteWmiConnectionPoolRegistry.Clear();

    /// <summary>
    /// Releases the cached pool for one host/credential/namespace combination.
    /// </summary>
    public static void ClearConnectionPool(
        string host,
        string username,
        string password,
        string wmiNamespace = @"root\cimv2",
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultConnectionTimeout;
        var key = BuildConnectionPoolKey(host, username, password, wmiNamespace, effectiveTimeout);
        RemoteWmiConnectionPoolRegistry.Clear(key);
    }

    internal static string BuildConnectionPoolKey(
        string host,
        string username,
        string password,
        string wmiNamespace,
        TimeSpan timeout)
    {
        var normalizedHost = HostHelper.NormalizeHost(host).ToLowerInvariant();
        var normalizedNamespace = (wmiNamespace ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedUser = (username ?? string.Empty).Trim().ToLowerInvariant();
        var material = $"{normalizedHost}\n{normalizedUser}\n{password}\n{normalizedNamespace}\n{(long)timeout.TotalMilliseconds}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"{normalizedHost}|{Convert.ToHexString(hash)}";
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

internal static class RemoteWmiConnectionPoolRegistry
{
    private static readonly ConcurrentDictionary<string, RemoteWmiConnectionPool> Pools =
        new(StringComparer.Ordinal);

    public static RemoteWmiConnectionPool GetOrCreate(
        string key,
        Func<ManagementScope> scopeFactory) =>
        Pools.GetOrAdd(key, _ => new RemoteWmiConnectionPool(scopeFactory));

    public static void Clear(string key)
    {
        if (Pools.TryRemove(key, out var pool))
            pool.Clear();
    }

    public static void Clear()
    {
        foreach (var key in Pools.Keys)
            Clear(key);
    }

    internal static bool Contains(string key) => Pools.ContainsKey(key);
}
