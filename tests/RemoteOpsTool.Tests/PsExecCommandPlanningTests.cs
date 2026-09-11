using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Tests;

public class PsExecCommandPlanningTests
{
    [Fact]
    public void BuildArguments_RemoteCmd_UsesCmdAndPreservesMetacharacters()
    {
        var service = CreateService();

        var args = service.BuildArguments("REMOTE01", "DOMAIN\\admin", "secret",
            "echo %TEMP% & whoami", false, 0, shell: CommandShell.Cmd);

        Assert.Equal(@"\\REMOTE01", args[0]);
        Assert.Contains("-h", args);
        Assert.Contains("-r", args);
        Assert.DoesNotContain("-i", args);
        Assert.DoesNotContain("-d", args);
        Assert.Equal(new[] { "cmd", "/c", "echo %TEMP% & whoami" }, args.TakeLast(3));
    }

    [Fact]
    public void BuildArguments_RemotePowerShell_UsesEncodedCommand()
    {
        var service = CreateService();

        var args = service.BuildArguments("REMOTE01", "DOMAIN\\admin", "secret",
            "Get-ChildItem 'C:\\Program Files'", false, 0, shell: CommandShell.PowerShell);

        var powerShellIndex = args.ToList().FindIndex(value =>
            value.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(powerShellIndex >= 0);
        Assert.Equal("-NoLogo", args[powerShellIndex + 1]);
        Assert.Equal("-NoProfile", args[powerShellIndex + 2]);
        Assert.Equal("-NonInteractive", args[powerShellIndex + 3]);
        Assert.Equal("-EncodedCommand", args[powerShellIndex + 4]);
        Assert.False(string.IsNullOrWhiteSpace(args[powerShellIndex + 5]));
    }

    [Fact]
    public void BuildArguments_RemoteInteractive_AddsSessionAndDetach()
    {
        var service = CreateService();

        var args = service.BuildArguments("REMOTE01", "DOMAIN\\admin", "secret",
            "control.exe /name Microsoft.ProgramsAndFeatures", true, 7,
            wrapCmd: false, shell: CommandShell.Direct);

        var interactiveIndex = args.ToList().IndexOf("-i");
        Assert.True(interactiveIndex >= 0);
        Assert.Equal("7", args[interactiveIndex + 1]);
        Assert.Contains("-d", args);
        Assert.Equal(new[] { "control.exe", "/name", "Microsoft.ProgramsAndFeatures" }, args.TakeLast(3));
    }

    [Theory]
    [InlineData("appwiz.cpl", "control.exe", "/name Microsoft.ProgramsAndFeatures")]
    [InlineData("compmgmt.msc", "mmc.exe", "compmgmt.msc")]
    [InlineData("sysdm.cpl", "control.exe", "sysdm.cpl")]
    public void ManagementEntryPoint_IsNormalized(string command, string expectedFile, string expectedArguments)
    {
        var plan = PsExecService.TryBuildManagementCommand(
            RemoteOpsTool.Helpers.ProcessHelper.SplitCommandLine(command));

        Assert.NotNull(plan);
        Assert.Equal(expectedFile, plan.Value.FileName);
        Assert.Equal(expectedArguments, plan.Value.Arguments);
    }

    [Theory]
    [InlineData(CommandShell.Cmd, "whoami /all")]
    [InlineData(CommandShell.PowerShell, "Get-Process")]
    public void ConsoleCommands_AreNotMistakenForGuiManagementEntries(CommandShell shell, string command)
    {
        var plan = PsExecService.TryBuildManagementCommand(
            RemoteOpsTool.Helpers.ProcessHelper.SplitCommandLine(command));

        Assert.Null(plan);
        var local = PsExecService.BuildLocalCommand(command, shell);
        Assert.False(local.FileName.Equals("control.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(local.FileName.Equals("mmc.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PsExecServiceStartDenied_IsDetectedFromCustomServiceError()
    {
        var result = new CommandResult(2250, string.Empty,
            "Could not start RemoteOpsTool_REMOTE01 service on REMOTE01:\r\nAccess is denied.");

        Assert.True(PsExecService.IsPsExecServiceStartDenied(result));
    }

    [Fact]
    public void PsExecServiceStartDenied_IsDetectedFromLocalizedServiceError()
    {
        var result = new CommandResult(2250, string.Empty,
            "无法启动 RemoteOpsTool_REMOTE01 服务：\r\n拒绝访问。");

        Assert.True(PsExecService.IsPsExecServiceStartDenied(result));
    }

    [Fact]
    public void PsExecServiceStartDenied_DoesNotMistakeCommandAccessDeniedForServiceFailure()
    {
        var result = new CommandResult(5, string.Empty,
            "Access is denied while opening the requested file.");

        Assert.False(PsExecService.IsPsExecServiceStartDenied(result));
    }
    [Fact]
    public void PsExecRecovery_ChangesCredentialTransportBeforeDefaultServiceName()
    {
        var initial = new[]
        {
            @"\\REMOTE01", "-u", @"DOMAIN\admin", "-p", "secret",
            "-accepteula", "-r", "RemoteOpsTool_REMOTE01_1", "cmd", "/c", "whoami"
        };

        var attempts = PsExecService.BuildPsExecRecoveryAttempts(initial, @"DOMAIN\admin", "secret");

        Assert.Equal(3, attempts.Count);
        Assert.Contains("-r", attempts[0].Arguments);
        Assert.DoesNotContain("-u", attempts[1].Arguments);
        Assert.Contains("-r", attempts[1].Arguments);
        Assert.DoesNotContain("-u", attempts[2].Arguments);
        Assert.DoesNotContain("-r", attempts[2].Arguments);
    }

    [Fact]
    public void WmiBootstrap_UsesRegistryPayloadAndReportsOutput()
    {
        var script = PsExecService.BuildWmiBootstrapScript("job123");

        Assert.Contains(@"HKLM:\SOFTWARE\RemoteOpsTool\WmiJobs\job123", script);
        Assert.Contains("$job.Command", script);
        Assert.Contains("$job.Shell", script);
        Assert.Contains("RedirectStandardOutput", script);
        Assert.Contains("ChildProcessId", script);
        Assert.DoesNotContain("whoami", script);
    }

    [Fact]
    public void InteractiveTask_UsesHighestInteractiveTokenWithoutPassword()
    {
        var script = PsExecService.BuildInteractiveTaskScript(
            "control.exe", "/name Microsoft.ProgramsAndFeatures", @"DOMAIN\admin", "task123");

        Assert.Contains("RemoteOpsTool_Interactive_task123", script);
        Assert.Contains("Principal.LogonType = 3", script);
        Assert.Contains("Principal.RunLevel = 1", script);
        Assert.Contains("RegisterTaskDefinition", script);
        Assert.Contains("DeleteTask", script);
        Assert.DoesNotContain(@"DOMAIN\admin", script);
    }

    private static PsExecService CreateService()
    {
        var settings = new TestSettingsService();
        settings.Settings.OmitPsExecExplicitCredentialsWhenRunAs = false;
        return new PsExecService(settings, new TestLogService());
    }
}
