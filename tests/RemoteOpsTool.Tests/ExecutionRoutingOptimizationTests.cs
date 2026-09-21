using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class ExecutionRoutingOptimizationTests
{
    private static RemoteCapabilityInfo Probe(string name, bool success, string detail = "") => new()
    {
        Name = name,
        Success = success,
        Detail = string.IsNullOrEmpty(detail) ? (success ? "ok" : "failed") : detail,
    };

    private static RemoteCommand Command(string text = "whoami") => new()
    {
        TargetHost = "REMOTE01",
        Username = @"DOMAIN\admin",
        Password = "secret",
        Command = text,
        Shell = CommandShell.Direct,
        WrapCmd = false,
    };

    [Fact]
    public void CapabilityPolicy_ClassifiesFailuresAndAppliesOperationSpecificTtl()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var permission = CapabilityPolicy.ClassifyResult(
            new CommandResult(1, string.Empty, "Access is denied"));
        var permissionCapability = CapabilityPolicy.CreateFailure(
            RemoteOperationKind.Command,
            RemoteTransportKind.PsExec,
            permission,
            "access denied",
            now);
        Assert.Equal(CapabilityFailureKind.PermissionDenied, permission);
        Assert.Equal(CapabilityHealth.PermissionDenied, permissionCapability.Health);
        Assert.Equal(TimeSpan.FromMinutes(10), permissionCapability.ExpiresAt - now);

        var disabled = CapabilityPolicy.ClassifyProbeFailure("The service is disabled (1058)");
        var disabledCapability = CapabilityPolicy.CreateFailure(
            RemoteOperationKind.Command,
            RemoteTransportKind.WmiDcom,
            disabled,
            "disabled",
            now);
        Assert.Equal(CapabilityFailureKind.Disabled, disabled);
        Assert.Equal(CapabilityHealth.HardUnavailable, disabledCapability.Health);
        Assert.Equal(TimeSpan.FromMinutes(30), disabledCapability.ExpiresAt - now);

        var timeout = CapabilityPolicy.ClassifyProbeFailure("operation timed out");
        var timeoutCapability = CapabilityPolicy.CreateFailure(
            RemoteOperationKind.Inventory,
            RemoteTransportKind.WmiDcom,
            timeout,
            "timeout",
            now);
        Assert.Equal(CapabilityFailureKind.Timeout, timeout);
        Assert.Equal(CapabilityHealth.TransientFailure, timeoutCapability.Health);
        Assert.Equal(TimeSpan.FromSeconds(30), timeoutCapability.ExpiresAt - now);
    }

    [Fact]
    public void CapabilitySnapshot_OperationHealthIsIsolatedByOperationAndTransport()
    {
        var snapshot = CreateSnapshot();

        snapshot.RecordTransportFailure(
            RemoteOperationKind.Command,
            RemoteTransportKind.PsExec,
            new CommandResult(1, string.Empty, "Access is denied"));

        Assert.Equal(
            CapabilityHealth.PermissionDenied,
            snapshot.GetOperationCapability(RemoteOperationKind.Command, RemoteTransportKind.PsExec).Health);
        Assert.True(snapshot.IsTransportCoolingDown(RemoteOperationKind.Command, RemoteTransportKind.PsExec));
        Assert.False(snapshot.IsTransportCoolingDown(RemoteOperationKind.InteractiveLaunch, RemoteTransportKind.PsExec));
        Assert.False(snapshot.IsTransportCoolingDown(RemoteOperationKind.Command, RemoteTransportKind.WmiDcom));
        Assert.Equal(
            CapabilityHealth.Unknown,
            snapshot.GetOperationCapability(RemoteOperationKind.InteractiveLaunch, RemoteTransportKind.PsExec).Health);
    }

    [Fact]
    public void CapabilitySnapshot_ProbeInitializationKeepsPsExecCommandAndInteractiveHealthSeparate()
    {
        var snapshot = CreateSnapshot();
        snapshot.InitializeProbeResults(
        [
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", false, "Access is denied"),
            Probe("计划任务 RPC", true),
        ]);

        Assert.Equal(
            CapabilityHealth.Available,
            snapshot.GetOperationCapability(RemoteOperationKind.Inventory, RemoteTransportKind.WmiDcom).Health);
        Assert.Equal(
            CapabilityHealth.Available,
            snapshot.GetOperationCapability(RemoteOperationKind.RegistryRead, RemoteTransportKind.WmiDcom).Health);
        Assert.Equal(
            CapabilityHealth.Available,
            snapshot.GetOperationCapability(RemoteOperationKind.RegistryWrite, RemoteTransportKind.WmiDcom).Health);
        Assert.Equal(
            CapabilityHealth.Unknown,
            snapshot.GetOperationCapability(RemoteOperationKind.Command, RemoteTransportKind.WmiDcom).Health);
        Assert.Equal(
            CapabilityHealth.PermissionDenied,
            snapshot.GetOperationCapability(RemoteOperationKind.Command, RemoteTransportKind.PsExec).Health);
        Assert.Equal(
            CapabilityHealth.Unknown,
            snapshot.GetOperationCapability(RemoteOperationKind.InteractiveLaunch, RemoteTransportKind.PsExec).Health);
        Assert.Equal(
            CapabilityHealth.Available,
            snapshot.GetOperationCapability(RemoteOperationKind.InteractiveLaunch, RemoteTransportKind.ScheduledTask).Health);
    }

    [Fact]
    public async Task CapabilityService_FingerprintSeparatesAccountsThatShareAPassword()
    {
        var probe = new FakeTransportProbeService { ProbeResults = [Probe("WMI/DCOM", true)] };
        var service = new CapabilityService(probe, new TestLogService());

        var first = await service.ProbeAsync("REMOTE01", "userA", "same-password");
        var second = await service.ProbeAsync("REMOTE01", "userB", "same-password");

        Assert.NotEqual(first.CredentialFingerprint, second.CredentialFingerprint);
    }

    [Fact]
    public void RouteLearningPolicy_PrefersReliableLowLatencyRouteAndRejectsUnprovenRoute()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            new RouteLearningRecord
            {
                Host = "REMOTE01",
                CredentialFingerprint = "A1",
                Operation = RemoteOperationKind.Command,
                Transport = RemoteTransportKind.PsExec,
                SuccessCount = 9,
                FailureCount = 1,
                AverageDurationMs = 40,
                LastSuccessAt = now,
                LastFailureAt = now.AddMinutes(-5),
                LastFailureKind = CapabilityFailureKind.Timeout,
            },
            new RouteLearningRecord
            {
                Host = "REMOTE01",
                CredentialFingerprint = "A1",
                Operation = RemoteOperationKind.Command,
                Transport = RemoteTransportKind.WmiDcom,
                SuccessCount = 5,
                FailureCount = 1,
                AverageDurationMs = 1_200,
                LastSuccessAt = now,
                LastFailureAt = now,
                LastFailureKind = CapabilityFailureKind.Timeout,
            },
        };

        Assert.True(RouteLearningPolicy.TrySelectPreferred(
            records,
            RemoteOperationKind.Command,
            out var selected));
        Assert.Equal(RemoteTransportKind.PsExec, selected);

        var unproven = new[]
        {
            new RouteLearningRecord
            {
                Host = "REMOTE01",
                CredentialFingerprint = "A1",
                Operation = RemoteOperationKind.Command,
                Transport = RemoteTransportKind.PsExec,
                FailureCount = 3,
                AverageDurationMs = 100,
                LastFailureAt = now,
                LastFailureKind = CapabilityFailureKind.Timeout,
            },
        };
        Assert.False(RouteLearningPolicy.TrySelectPreferred(
            unproven,
            RemoteOperationKind.Command,
            out _));
    }

    [Fact]
    public void RouteLearningStore_PersistsStatisticsAndDoesNotPersistSensitiveInput()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "RemoteOpsTool.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "route-learning.json");
        var credentialFingerprint = "A1B2C3";
        var occurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        try
        {
            using (var store = new RouteLearningStore(new TestLogService(), path))
            {
                store.Record(new CapabilityOutcome(
                    "\\\\REMOTE01",
                    credentialFingerprint,
                    RemoteOperationKind.Command,
                    RemoteTransportKind.PsExec,
                    TransportSucceeded: true,
                    100,
                    CapabilityFailureKind.None,
                    occurredAt));
                store.Record(new CapabilityOutcome(
                    "REMOTE01",
                    credentialFingerprint,
                    RemoteOperationKind.Command,
                    RemoteTransportKind.PsExec,
                    TransportSucceeded: false,
                    300,
                    CapabilityFailureKind.PermissionDenied,
                    occurredAt.AddSeconds(1)));

                var record = Assert.Single(store.GetRecords("REMOTE01", credentialFingerprint));
                Assert.Equal(1, record.SuccessCount);
                Assert.Equal(1, record.FailureCount);
                Assert.Equal(200, record.AverageDurationMs);
                Assert.Equal(CapabilityFailureKind.PermissionDenied, record.LastFailureKind);
                Assert.Empty(store.GetRecords("REMOTE01", "different-fingerprint"));

                store.Flush();
            }

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("secret-password", json);
            Assert.DoesNotContain("cmd.exe /c whoami /all", json);

            using (var reloaded = new RouteLearningStore(new TestLogService(), path))
            {
                var record = Assert.Single(reloaded.GetRecords("REMOTE01", credentialFingerprint));
                Assert.Equal(2, record.TotalAttempts);
                Assert.Equal(1, record.SuccessCount);
                Assert.Equal(1, record.FailureCount);
                Assert.Equal(200, record.AverageDurationMs);
                Assert.Equal(CapabilityFailureKind.PermissionDenied, record.LastFailureKind);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteExecutionService_UsesLearnedPreferredTransportBeforeDefaultOrder()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResults = [Probe("WMI/DCOM", true), Probe("PsExec 临时执行", true)],
        };
        var capabilities = new CapabilityService(probe, new TestLogService());
        var snapshot = await capabilities.ProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var routeLearning = new RecordingRouteLearningStore(
        [
            new RouteLearningRecord
            {
                Host = "REMOTE01",
                CredentialFingerprint = snapshot.CredentialFingerprint,
                Operation = RemoteOperationKind.Command,
                Transport = RemoteTransportKind.WmiDcom,
                SuccessCount = 10,
                AverageDurationMs = 20,
                LastSuccessAt = DateTimeOffset.UtcNow,
            },
        ]);
        var executor = new FakeRemoteCommandExecutor();
        var service = new RemoteExecutionService(
            capabilities,
            executor,
            new TestLogService(),
            routeLearning);

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command("learned"));

        Assert.True(result.Success);
        Assert.Equal(new[] { RemoteTransportKind.WmiDcom }, executor.CallOrder);
        var outcome = Assert.Single(routeLearning.Outcomes);
        Assert.True(outcome.TransportSucceeded);
        Assert.Equal(RemoteTransportKind.WmiDcom, outcome.Transport);
    }

    [Fact]
    public async Task RemoteExecutionService_RecordsTransportFailuresButStillFallsBackSafely()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResults = [Probe("WMI/DCOM", true), Probe("PsExec 临时执行", true)],
        };
        var executor = new FakeRemoteCommandExecutor();
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "PsExec transport unavailable")));
        executor.WmiHandler = (_, _) => Task.FromResult(
            TransportResult.Ok(RemoteTransportKind.WmiDcom, new CommandResult(0, "ok", string.Empty)));
        var routeLearning = new RecordingRouteLearningStore();
        var service = new RemoteExecutionService(
            new CapabilityService(probe, new TestLogService()),
            executor,
            new TestLogService(),
            routeLearning);

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command("fallback"));

        Assert.True(result.Success);
        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom },
            executor.CallOrder);
        Assert.Equal(2, routeLearning.Outcomes.Count);
        Assert.False(routeLearning.Outcomes[0].TransportSucceeded);
        Assert.True(routeLearning.Outcomes[1].TransportSucceeded);
    }

    [Fact]
    public async Task RemoteExecutionService_DoesNotReplayCommandFailureAndRecordsSuccessfulTransport()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResults = [Probe("WMI/DCOM", true), Probe("PsExec 临时执行", true)],
        };
        var executor = new FakeRemoteCommandExecutor();
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.CommandFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(9, string.Empty, "remote command failed")));
        var routeLearning = new RecordingRouteLearningStore();
        var service = new RemoteExecutionService(
            new CapabilityService(probe, new TestLogService()),
            executor,
            new TestLogService(),
            routeLearning);

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command("run-once"));

        Assert.False(result.Success);
        Assert.False(result.IsTransportFailure);
        Assert.Equal(9, result.ExitCode);
        Assert.Equal(new[] { RemoteTransportKind.PsExec }, executor.CallOrder);
        var outcome = Assert.Single(routeLearning.Outcomes);
        Assert.True(outcome.TransportSucceeded);
        Assert.Equal(CapabilityFailureKind.None, outcome.FailureKind);
    }

    private static CapabilitySnapshot CreateSnapshot() => new()
    {
        Host = "REMOTE01",
        UsernameKey = @"DOMAIN\admin",
        CredentialFingerprint = "A1B2C3",
    };
}

internal sealed class RecordingRouteLearningStore : IRouteLearningStore
{
    private readonly List<RouteLearningRecord> _records;

    public RecordingRouteLearningStore(params RouteLearningRecord[] records)
    {
        _records = [.. records];
    }

    public List<CapabilityOutcome> Outcomes { get; } = [];

    public IReadOnlyList<RouteLearningRecord> GetRecords(
        string host,
        string credentialFingerprint) =>
        _records
            .Where(record =>
                record.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                record.CredentialFingerprint.Equals(
                    credentialFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            .Select(record => record with { })
            .ToArray();

    public void Record(CapabilityOutcome outcome) => Outcomes.Add(outcome);

    public void Flush()
    {
    }
}
