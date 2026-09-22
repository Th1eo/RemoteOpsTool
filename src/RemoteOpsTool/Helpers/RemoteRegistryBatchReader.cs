using System.Management;

namespace RemoteOpsTool.Helpers;

/// <summary>
/// Reads StdRegProv values through a bounded set of pooled WMI scopes.
/// WMI has no true multi-get API, so each lane reuses one scope and one
/// StdRegProv ManagementClass while requests are spread across lanes.
/// </summary>
public static class RemoteRegistryBatchReader
{
    private const int DefaultMaxConcurrency = 4;

    public readonly record struct RegistryValueRequest(
        uint Hive,
        string SubKey,
        string ValueName,
        uint Type);

    public static Task<string[]> ReadValuesAsync(
        string host,
        string username,
        string password,
        uint hive,
        string subKey,
        IReadOnlyList<string> names,
        IReadOnlyList<uint> types,
        CancellationToken ct = default)
    {
        if (names.Count == 0)
            return Task.FromResult(Array.Empty<string>());

        var requests = new RegistryValueRequest[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            requests[i] = new RegistryValueRequest(
                hive,
                subKey,
                names[i],
                i < types.Count ? types[i] : 1u);
        }

        return ReadValuesAsync(host, username, password, requests, ct);
    }

    public static async Task<string[]> ReadValuesAsync(
        string host,
        string username,
        string password,
        IReadOnlyList<RegistryValueRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];

        var results = new string[requests.Count];
        var laneCount = Math.Min(DefaultMaxConcurrency, requests.Count);
        var laneSize = (requests.Count + laneCount - 1) / laneCount;
        var lanes = new List<Task>(laneCount);

        for (var lane = 0; lane < laneCount; lane++)
        {
            var start = lane * laneSize;
            var end = Math.Min(start + laneSize, requests.Count);
            if (start < end)
                lanes.Add(ReadLaneAsync(host, username, password, requests, results, start, end, ct));
        }

        await Task.WhenAll(lanes).ConfigureAwait(false);
        return results;
    }

    private static async Task ReadLaneAsync(
        string host,
        string username,
        string password,
        IReadOnlyList<RegistryValueRequest> requests,
        string[] results,
        int start,
        int end,
        CancellationToken ct)
    {
        try
        {
            await RemoteWmiHelper.ExecuteAsync(
                host,
                username,
                password,
                scope =>
                {
                    using var registry = new ManagementClass(
                        scope,
                        new ManagementPath("StdRegProv"),
                        null);

                    for (var i = start; i < end; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var request = requests[i];
                        results[i] = ReadValue(
                            registry,
                            request.Hive,
                            request.SubKey,
                            request.ValueName,
                            request.Type);
                    }

                    return true;
                },
                ct,
                @"root\default").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep this lane's slots as empty strings. Callers tolerate missing
            // values and can still display rows returned by healthy lanes.
        }
    }

    internal static string GetValueMethod(uint type) => type switch
    {
        2 => "GetExpandedStringValue",
        3 => "GetBinaryValue",
        4 => "GetDWORDValue",
        7 => "GetMultiStringValue",
        11 => "GetQWORDValue",
        _ => "GetStringValue"
    };

    private static string ReadValue(
        ManagementClass registry,
        uint hive,
        string subKey,
        string name,
        uint type)
    {
        try
        {
            var method = GetValueMethod(type);
            using var inParams = registry.GetMethodParameters(method);
            inParams["hDefKey"] = hive;
            inParams["sSubKeyName"] = subKey;
            inParams["sValueName"] = name;

            using var outParams = registry.InvokeMethod(method, inParams, null);
            if (RemoteWmiHelper.GetUInt32(outParams, "ReturnValue") != 0)
                return string.Empty;

            return type switch
            {
                3 => outParams["uValue"] is byte[] bytes
                    ? BitConverter.ToString(bytes).Replace("-", " ")
                    : string.Empty,
                7 => outParams["sValue"] is string[] items
                    ? string.Join("; ", items)
                    : string.Empty,
                4 or 11 => outParams["uValue"]?.ToString() ?? string.Empty,
                _ => outParams["sValue"]?.ToString() ?? string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}
