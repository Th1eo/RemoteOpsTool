using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class RemoteExecutionServiceTests
{
    private static RemoteCapabilityInfo Probe(string name, bool success) => new()
    {
        Name = name,
        Success = success,
        Detail = success ? "ok" : "failed",
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

    private static (RemoteExecutionService Service, FakeTransportProbeService Network, FakeRemoteCommandExecutor Executor)
        CreateService(params RemoteCapabilityInfo[] probes)
    {
        var probe = new FakeTransportProbeService { ProbeResults = [.. probes] };
        var executor = new FakeRemoteCommandExecutor();
        var service = new RemoteExecutionService(
            new CapabilityService(probe, new TestLogService()),
            executor,
            new TestLogService());
        return (service, probe, executor);
    }

    [Fact]
    public async Task OneSession_ProbesOnce_AndReusesTheSamePlanForEveryStep()
    {
        var (service, probe, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");

        var first = await session.ExecuteAsync(RemoteOperationKind.Command, Command("step1"));
        var second = await session.ExecuteAsync(RemoteOperationKind.Command, Command("step2"));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Equal(2, executor.PsExecCommands.Count);
        Assert.Empty(executor.WmiCommands);
        Assert.Equal(new[] { "step1", "step2" }, executor.PsExecCommands.Select(command => command.Command));
    }

    [Fact]
    public async Task LongCredentialedCommand_UsesWmiFirstAndKeepsPsExecAsFallback()
    {
        var (service, _, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true));
        executor.WmiHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.WmiDcom,
                new CommandResult(-1, string.Empty, "WMI transport unavailable")));
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.Ok(RemoteTransportKind.PsExec, new CommandResult(0, "ok", string.Empty)));
        var payload = new string('x', PsExecService.MaxSafeRunAsCommandLength + 1);

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var warmup = await session.ExecuteAsync(
            RemoteOperationKind.Command,
            Command("learn-ps-exec-route"));
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command(payload));

        Assert.True(warmup.Success);
        Assert.True(result.Success);
        Assert.Single(executor.WmiCommands);
        Assert.Equal(2, executor.PsExecCommands.Count);
        Assert.Equal(payload, executor.WmiCommands[0].Command);
        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom, RemoteTransportKind.PsExec },
            executor.CallOrder);
    }

    [Fact]
    public async Task LongCommandWithWmiCoolingDown_UsesPsExecAsReadyFallback()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResults = [Probe("WMI/DCOM", true), Probe("PsExec 临时执行", true)],
        };
        var capabilities = new CapabilityService(probe, new TestLogService());
        var executor = new FakeRemoteCommandExecutor();
        var service = new RemoteExecutionService(capabilities, executor, new TestLogService());
        var snapshot = await capabilities.ProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");
        snapshot.RecordTransportFailure(RemoteTransportKind.WmiDcom);
        var payload = new string('x', PsExecService.MaxSafeRunAsCommandLength + 1);

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command(payload));

        Assert.True(result.Success);
        Assert.Equal(new[] { RemoteTransportKind.PsExec }, executor.CallOrder);
        Assert.Empty(executor.WmiCommands);
        Assert.Equal(payload, Assert.Single(executor.PsExecCommands).Command);
    }

    [Fact]
    public async Task CoolingDownPreferredTransport_IsMovedBehindReadyFallback()
    {
        var probe = new FakeTransportProbeService
        {
            ProbeResults = [Probe("WMI/DCOM", true), Probe("PsExec 临时执行", true)],
        };
        var capabilities = new CapabilityService(probe, new TestLogService());
        var executor = new FakeRemoteCommandExecutor();
        var service = new RemoteExecutionService(capabilities, executor, new TestLogService());
        var snapshot = await capabilities.ProbeAsync("REMOTE01", @"DOMAIN\admin", "secret");
        snapshot.RecordTransportSuccess(RemoteOperationKind.Command, RemoteTransportKind.PsExec);
        snapshot.RecordTransportFailure(RemoteTransportKind.PsExec);

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command());

        Assert.True(result.Success);
        Assert.Equal(new[] { RemoteTransportKind.WmiDcom }, executor.CallOrder);
    }

    [Fact]
    public async Task IndependentTopLevelOperations_ReuseCachedCapability()
    {
        var (service, probe, executor) = CreateService(Probe("WMI/DCOM", true));

        var firstSession = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        await firstSession.ExecuteAsync(RemoteOperationKind.Command, Command());
        var secondSession = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        await secondSession.ExecuteAsync(RemoteOperationKind.Command, Command());

        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Equal(2, executor.WmiCommands.Count);
    }

    [Fact]
    public async Task RemoteNonZeroExit_DoesNotFallbackOrReplayTheCommand()
    {
        var (service, _, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true));
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.CommandFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(7, string.Empty, "remote command failed")));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command());

        Assert.False(result.Success);
        Assert.False(result.IsTransportFailure);
        Assert.Equal(7, result.ExitCode);
        Assert.Single(executor.PsExecCommands);
        Assert.Empty(executor.WmiCommands);
    }

    [Fact]
    public async Task TransportFailure_FallsBackOnce_AndTheSessionPinsTheSuccessfulFallback()
    {
        var (service, _, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true));
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "PsExec transport unavailable")));
        executor.WmiHandler = (_, _) => Task.FromResult(
            TransportResult.Ok(RemoteTransportKind.WmiDcom, new CommandResult(0, "ok", string.Empty)));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var first = await session.ExecuteAsync(RemoteOperationKind.Command, Command("step1"));
        var second = await session.ExecuteAsync(RemoteOperationKind.Command, Command("step2"));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Single(executor.PsExecCommands);
        Assert.Equal(2, executor.WmiCommands.Count);
        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom, RemoteTransportKind.WmiDcom },
            executor.CallOrder);
    }

    [Fact]
    public async Task LearnedFallbackRouteIsReusedByTheNextSession()
    {
        var (service, probe, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true));
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "PsExec transport unavailable")));
        executor.WmiHandler = (_, _) => Task.FromResult(
            TransportResult.Ok(RemoteTransportKind.WmiDcom, new CommandResult(0, "ok", string.Empty)));

        var firstSession = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        await firstSession.ExecuteAsync(RemoteOperationKind.Command, Command("first"));

        var secondSession = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        await secondSession.ExecuteAsync(RemoteOperationKind.Command, Command("second"));

        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Equal(3, executor.CallOrder.Count);
        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom, RemoteTransportKind.WmiDcom },
            executor.CallOrder);
    }

    [Fact]
    public async Task AllTransportFailures_AreAttemptedAtMostOncePerStep()
    {
        var (service, _, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true));
        executor.WmiHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.WmiDcom,
                new CommandResult(-1, string.Empty, "WMI unavailable")));
        executor.PsExecHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "PsExec unavailable")));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command());

        Assert.True(result.IsTransportFailure);
        Assert.Single(executor.WmiCommands);
        Assert.Single(executor.PsExecCommands);
        Assert.Equal(
            new[] { RemoteTransportKind.PsExec, RemoteTransportKind.WmiDcom },
            executor.CallOrder);
    }

    [Fact]
    public async Task InteractiveLaunch_UsesFixedPsExecWmiScheduledTaskFallbackOrder()
    {
        var (service, _, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true),
            Probe("计划任务 RPC", true));
        executor.InteractivePsExecHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(-1, string.Empty, "PsExec transport unavailable")));
        executor.InteractiveWmiHandler = (_, _) => Task.FromResult(
            TransportResult.TransportFailure(
                RemoteTransportKind.WmiDcom,
                new CommandResult(-1, string.Empty, "WMI transport unavailable")));
        executor.InteractiveScheduledTaskHandler = (_, _) => Task.FromResult(
            TransportResult.Ok(RemoteTransportKind.ScheduledTask, new CommandResult(0, "started", string.Empty)));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(
            RemoteOperationKind.InteractiveLaunch,
            Command("cmd.exe"));

        Assert.True(result.Success);
        Assert.Equal(
            new[]
            {
                RemoteTransportKind.PsExec,
                RemoteTransportKind.WmiDcom,
                RemoteTransportKind.ScheduledTask,
            },
            executor.CallOrder);
    }

    [Fact]
    public async Task InteractiveNonZeroExit_DoesNotFallbackOrLaunchTwice()
    {
        var (service, _, executor) = CreateService(
            Probe("WMI/DCOM", true),
            Probe("PsExec 临时执行", true),
            Probe("计划任务 RPC", true));
        executor.InteractivePsExecHandler = (_, _) => Task.FromResult(
            TransportResult.CommandFailure(
                RemoteTransportKind.PsExec,
                new CommandResult(5, string.Empty, "interactive launch denied")));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(
            RemoteOperationKind.InteractiveLaunch,
            Command("cmd.exe"));

        Assert.False(result.Success);
        Assert.False(result.IsTransportFailure);
        Assert.Single(executor.InteractivePsExecCommands);
        Assert.Empty(executor.InteractiveWmiCommands);
        Assert.Empty(executor.InteractiveScheduledTaskCommands);
    }

    [Fact]
    public async Task NoApplicableTransport_ReturnsFailureWithoutExecutingAnything()
    {
        var (service, _, executor) = CreateService(Probe("计划任务 RPC", true));

        var session = await service.CreateSessionAsync("REMOTE01", @"DOMAIN\admin", "secret");
        var result = await session.ExecuteAsync(RemoteOperationKind.Command, Command());

        Assert.True(result.IsTransportFailure);
        Assert.Empty(executor.CallOrder);
    }
}
