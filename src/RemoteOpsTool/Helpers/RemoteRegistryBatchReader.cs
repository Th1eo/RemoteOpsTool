using System.Management;

namespace RemoteOpsTool.Helpers;

/// <summary>
/// 以受控并发批量读取 StdRegProv 值。每个并行 lane 复用一组
/// ManagementScope/ManagementClass，降低 N 个值产生的连接和串行往返开销。
/// WMI 没有真正的批量 GetValue 方法，因此这里通过有界并行摊薄 RTT。
/// </summary>
public static class RemoteRegistryBatchReader
{
    private const int DefaultMaxConcurrency = 4;

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

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var results = new string[names.Count];
            var laneCount = Math.Min(DefaultMaxConcurrency, names.Count);
            var laneSize = (names.Count + laneCount - 1) / laneCount;

            Parallel.For(
                0,
                laneCount,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = laneCount },
                lane =>
                {
                    var start = lane * laneSize;
                    var end = Math.Min(start + laneSize, names.Count);
                    if (start >= end)
                        return;

                    // A lane owns its own DCOM connection and ManagementClass. If one
                    // lane cannot connect, leave only that lane's values empty instead
                    // of failing the whole registry folder.
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var scope = RemoteWmiHelper.CreateScope(host, username, password, @"root\default");
                        scope.Connect();
                        using var registry = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);

                        for (var i = start; i < end; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var type = i < types.Count ? types[i] : 1u;
                            results[i] = ReadValue(registry, hive, subKey, names[i], type);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Keep the lane's slots as empty strings. Callers already
                        // tolerate empty values and can still show the remaining rows.
                    }
                });

            return results;
        }, ct);
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