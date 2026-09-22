using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>One persisted statistical route record for host + credential fingerprint + operation + command shape + transport.</summary>
public sealed record RouteLearningRecord
{
    public string Host { get; init; } = string.Empty;
    public string CredentialFingerprint { get; init; } = string.Empty;
    public RemoteOperationKind Operation { get; init; }
    public RemoteCommandShape CommandShape { get; init; } = RemoteCommandShape.ShortCommand;
    public RemoteTransportKind Transport { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public int CommandNonZeroCount { get; init; }
    public double AverageDurationMs { get; init; }
    public double EwmaDurationMs { get; init; }
    public double AverageFirstOutputMs { get; init; }
    public long TotalOutputBytes { get; init; }
    public List<long> RecentDurationMs { get; init; } = [];
    public DateTimeOffset LastSuccessAt { get; init; }
    public DateTimeOffset LastFailureAt { get; init; }
    public CapabilityFailureKind LastFailureKind { get; init; }
    public CapabilityFailureKind LastCommandFailureKind { get; init; }

    [JsonIgnore]
    public int TotalAttempts => SuccessCount + FailureCount;

    [JsonIgnore]
    public double SuccessRate => TotalAttempts == 0 ? 0 : (double)SuccessCount / TotalAttempts;

    [JsonIgnore]
    public double TransportFailureRate => TotalAttempts == 0 ? 0 : (double)FailureCount / TotalAttempts;

    [JsonIgnore]
    public double CommandNonZeroRate => SuccessCount == 0 ? 0 : (double)CommandNonZeroCount / SuccessCount;

    [JsonIgnore]
    public double P50DurationMs => GetPercentile(0.50);

    [JsonIgnore]
    public double P95DurationMs => GetPercentile(0.95);

    [JsonIgnore]
    public DateTimeOffset LastAttemptAt =>
        LastSuccessAt >= LastFailureAt ? LastSuccessAt : LastFailureAt;

    private double GetPercentile(double percentile)
    {
        if (RecentDurationMs.Count == 0)
            return AverageDurationMs;

        var samples = RecentDurationMs.OrderBy(value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * samples.Length) - 1;
        return samples[Math.Clamp(index, 0, samples.Length - 1)];
    }
}

/// <summary>Transport attempt outcome recorded without command text or credentials.</summary>
public sealed record CapabilityOutcome(
    string Host,
    string CredentialFingerprint,
    RemoteOperationKind Operation,
    RemoteTransportKind Transport,
    bool TransportSucceeded,
    long DurationMs,
    CapabilityFailureKind FailureKind,
    DateTimeOffset OccurredAt,
    RemoteCommandShape CommandShape = RemoteCommandShape.ShortCommand,
    long OutputBytes = 0,
    double? FirstOutputMs = null,
    bool CommandSucceeded = true);

/// <summary>Host-level route learning store.</summary>
public interface IRouteLearningStore
{
    IReadOnlyList<RouteLearningRecord> GetRecords(
        string host,
        string credentialFingerprint);

    void Record(CapabilityOutcome outcome);

    void Flush();
}

/// <summary>
/// Persists only route statistics. The credential is represented by the same
/// SHA-256 fingerprint used by CapabilityService; passwords and command text are
/// never written to this file.
/// </summary>
public sealed class RouteLearningStore : IRouteLearningStore, IDisposable
{
    private const int MaxRecords = 4096;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly string _filePath;
    private readonly ILogService _log;
    private readonly Timer _flushTimer;
    private Dictionary<string, RouteLearningRecord>? _records;
    private bool _dirty;
    private long _version;
    private int _disposed;

    public RouteLearningStore(ILogService log)
        : this(log, AppConstants.RouteLearningFilePath)
    {
    }

    internal RouteLearningStore(ILogService log, string filePath)
    {
        _log = log;
        _filePath = filePath;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public IReadOnlyList<RouteLearningRecord> GetRecords(
        string host,
        string credentialFingerprint)
    {
        var normalizedHost = HostHelper.NormalizeHost(host);
        lock (_gate)
        {
            EnsureLoaded();
            return _records!.Values
                .Where(record =>
                    record.Host.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                    record.CredentialFingerprint.Equals(
                        credentialFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                .Select(record => record with { })
                .ToArray();
        }
    }

    public void Record(CapabilityOutcome outcome)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (string.IsNullOrWhiteSpace(outcome.Host) ||
            string.IsNullOrWhiteSpace(outcome.CredentialFingerprint))
        {
            return;
        }

        var normalizedHost = HostHelper.NormalizeHost(outcome.Host);
        var key = BuildKey(
            normalizedHost,
            outcome.CredentialFingerprint,
            outcome.Operation,
            outcome.CommandShape,
            outcome.Transport);

        lock (_gate)
        {
            EnsureLoaded();
            var existing = _records!.TryGetValue(key, out var record)
                ? record
                : new RouteLearningRecord
                {
                    Host = normalizedHost,
                    CredentialFingerprint = outcome.CredentialFingerprint,
                    Operation = outcome.Operation,
                    CommandShape = outcome.CommandShape,
                    Transport = outcome.Transport,
                };

            var attempts = existing.TotalAttempts;
            var duration = Math.Max(0, outcome.DurationMs);
            var average = attempts == 0
                ? duration
                : ((existing.AverageDurationMs * attempts) + duration) / (attempts + 1);
            var ewma = attempts == 0 || existing.EwmaDurationMs <= 0
                ? average
                : (existing.EwmaDurationMs * 0.75) + (duration * 0.25);
            var firstOutput = Math.Max(0, outcome.FirstOutputMs ?? duration);
            var firstOutputAverage = attempts == 0
                ? firstOutput
                : ((existing.AverageFirstOutputMs * attempts) + firstOutput) / (attempts + 1);
            var recentDurations = existing.RecentDurationMs
                .TakeLast(63)
                .Append(duration)
                .ToList();

            existing = outcome.TransportSucceeded
                ? existing with
                {
                    SuccessCount = existing.SuccessCount + 1,
                    CommandNonZeroCount = existing.CommandNonZeroCount +
                        (outcome.CommandSucceeded ? 0 : 1),
                    AverageDurationMs = average,
                    EwmaDurationMs = ewma,
                    AverageFirstOutputMs = firstOutputAverage,
                    TotalOutputBytes = existing.TotalOutputBytes + Math.Max(0, outcome.OutputBytes),
                    RecentDurationMs = recentDurations,
                    LastSuccessAt = outcome.OccurredAt,
                    LastCommandFailureKind = outcome.CommandSucceeded
                        ? existing.LastCommandFailureKind
                        : outcome.FailureKind,
                }
                : existing with
                {
                    FailureCount = existing.FailureCount + 1,
                    AverageDurationMs = average,
                    EwmaDurationMs = ewma,
                    AverageFirstOutputMs = firstOutputAverage,
                    TotalOutputBytes = existing.TotalOutputBytes + Math.Max(0, outcome.OutputBytes),
                    RecentDurationMs = recentDurations,
                    LastFailureAt = outcome.OccurredAt,
                    LastFailureKind = outcome.FailureKind,
                };

            _records[key] = existing;
            PruneIfNeeded();
            _dirty = true;
            _version++;
            ScheduleFlush(FlushDelay);
        }
    }

    public void Flush()
    {
        // A timer callback and application shutdown can both request a flush.
        // Serialize writers so they never contend for the same temporary file.
        lock (_writeGate)
        {
            string? json = null;
            long version;
            lock (_gate)
            {
                if (!_dirty)
                    return;

                EnsureLoaded();
                version = _version;
                try
                {
                    var file = new RouteLearningFile
                    {
                        Version = 1,
                        Records = _records!.Values
                            .OrderBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(record => (int)record.Operation)
                            .ThenBy(record => (int)record.CommandShape)
                            .ThenBy(record => (int)record.Transport)
                            .ToList(),
                    };
                    json = JsonSerializer.Serialize(file, JsonOptions);
                }
                catch (Exception ex)
                {
                    _log.Warn($"序列化路由学习缓存失败: {ex.Message}");
                    ScheduleFlush(RetryDelay);
                    return;
                }
            }

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json!, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, _filePath, overwrite: true);

                lock (_gate)
                {
                    // If a new outcome arrived while the file was being written,
                    // keep the dirty flag set so that outcome is not lost.
                    if (_version == version)
                    {
                        _dirty = false;
                    }
                    else
                    {
                        ScheduleFlush(FlushDelay);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"保存路由学习缓存失败: {ex.Message}");
                lock (_gate)
                {
                    ScheduleFlush(RetryDelay);
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _flushTimer.Dispose();
        Flush();
    }

    private void ScheduleFlush(TimeSpan delay)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown may dispose the timer while a final flush is in progress.
        }
    }

    private void EnsureLoaded()
    {
        if (_records is not null)
            return;

        _records = new Dictionary<string, RouteLearningRecord>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_filePath))
            return;

        try
        {
            var file = JsonSerializer.Deserialize<RouteLearningFile>(
                File.ReadAllText(_filePath, Encoding.UTF8),
                JsonOptions);
            if (file?.Records is null)
                return;

            foreach (var record in file.Records)
            {
                if (string.IsNullOrWhiteSpace(record.Host) ||
                    string.IsNullOrWhiteSpace(record.CredentialFingerprint))
                {
                    continue;
                }

                var normalized = record with { Host = HostHelper.NormalizeHost(record.Host) };
                _records[BuildKey(
                    normalized.Host,
                    normalized.CredentialFingerprint,
                    normalized.Operation,
                    normalized.CommandShape,
                    normalized.Transport)] = normalized;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取路由学习缓存失败，将从空缓存开始: {ex.Message}");
        }
    }

    private void PruneIfNeeded()
    {
        while (_records!.Count > MaxRecords)
        {
            var oldest = _records
                .OrderBy(pair => pair.Value.LastAttemptAt)
                .First();
            _records.Remove(oldest.Key);
        }
    }

    private static string BuildKey(
        string host,
        string credentialFingerprint,
        RemoteOperationKind operation,
        RemoteCommandShape commandShape,
        RemoteTransportKind transport) =>
        $"{host}|{credentialFingerprint}|{operation}|{commandShape}|{transport}";

    private sealed class RouteLearningFile
    {
        public int Version { get; set; }
        public List<RouteLearningRecord> Records { get; set; } = [];
    }
}

internal sealed class NullRouteLearningStore : IRouteLearningStore
{
    public static NullRouteLearningStore Instance { get; } = new();

    private NullRouteLearningStore()
    {
    }

    public IReadOnlyList<RouteLearningRecord> GetRecords(
        string host,
        string credentialFingerprint) => [];

    public void Record(CapabilityOutcome outcome)
    {
    }

    public void Flush()
    {
    }
}

/// <summary>Pure scoring policy used to choose a persisted first-choice route.</summary>
internal static class RouteLearningPolicy
{
    public static bool TrySelectPreferred(
        IReadOnlyList<RouteLearningRecord> records,
        RemoteOperationKind operation,
        out RemoteTransportKind transport) =>
        TrySelectPreferred(records, operation, commandShape: null, out transport);

    public static bool TrySelectPreferred(
        IReadOnlyList<RouteLearningRecord> records,
        RemoteOperationKind operation,
        RemoteCommandShape commandShape,
        out RemoteTransportKind transport) =>
        TrySelectPreferred(records, operation, (RemoteCommandShape?)commandShape, out transport);

    private static bool TrySelectPreferred(
        IReadOnlyList<RouteLearningRecord> records,
        RemoteOperationKind operation,
        RemoteCommandShape? commandShape,
        out RemoteTransportKind transport)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = records
            .Where(record =>
                record.Operation == operation &&
                (commandShape is null || record.CommandShape == commandShape) &&
                record.TotalAttempts > 0)
            .Select(record => new { Record = record, Score = CalculateScore(record, now) })
            .Where(candidate => double.IsFinite(candidate.Score) && candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Record.SuccessRate)
            .ThenBy(candidate => candidate.Record.EwmaDurationMs > 0
                ? candidate.Record.EwmaDurationMs
                : candidate.Record.AverageDurationMs)
            .ThenBy(candidate => DefaultOrder(operation, candidate.Record.Transport))
            .FirstOrDefault();

        if (candidates is null)
        {
            transport = default;
            return false;
        }

        transport = candidates.Record.Transport;
        return true;
    }

    private static double CalculateScore(RouteLearningRecord record, DateTimeOffset now)
    {
        if (record.SuccessCount == 0)
            return double.NegativeInfinity;

        // Ignore stale route statistics. A fresh probe or real operation will
        // create a new record; old records must not pin a route forever.
        if (record.LastAttemptAt < now - TimeSpan.FromDays(30))
            return double.NegativeInfinity;

        var reliability = record.SuccessRate * 1000;
        var latency = record.EwmaDurationMs > 0 ? record.EwmaDurationMs : record.AverageDurationMs;
        var tailPenalty = Math.Min(Math.Max(0, record.P95DurationMs - latency), 10_000) / 400.0;
        var latencyPenalty = Math.Min(latency, 30_000) / 100.0 + tailPenalty;
        var recentFailurePenalty =
            record.LastFailureAt > record.LastSuccessAt ? 150 : 0;
        var hardFailurePenalty =
            record.LastFailureKind is CapabilityFailureKind.PermissionDenied or
            CapabilityFailureKind.Disabled or
            CapabilityFailureKind.NotFound or
            CapabilityFailureKind.RemoteCommand
                ? 300
                : 0;
        var commandNonZeroPenalty = Math.Min(record.CommandNonZeroRate, 1) * 50;
        var volumeBonus = Math.Min(record.SuccessCount, 10) * 5;

        return reliability - latencyPenalty - recentFailurePenalty - hardFailurePenalty -
            commandNonZeroPenalty + volumeBonus;
    }

    private static int DefaultOrder(RemoteOperationKind operation, RemoteTransportKind transport)
    {
        var order = CapabilityMatrix.GetPreferredOrder(operation);
        var index = order.ToList().IndexOf(transport);
        return index < 0 ? int.MaxValue : index;
    }
}
