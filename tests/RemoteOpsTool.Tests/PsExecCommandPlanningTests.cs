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
    public void BuildArguments_DefaultRunAsModeDoesNotPutPasswordOnPsExecCommandLine()
    {
        var settings = new TestSettingsService();
        var service = new PsExecService(settings, new TestLogService());

        var args = service.BuildArguments("REMOTE01", @"DOMAIN\admin", "secret",
            "whoami", false, 0);

        Assert.DoesNotContain("-u", args);
        Assert.DoesNotContain("-p", args);
        Assert.DoesNotContain("secret", args);
        Assert.Contains("-h", args);
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
    [InlineData("compmgmt.msc /computer=\\\\REMOTE01", "mmc.exe", "compmgmt.msc /computer=\\\\REMOTE01")]
    [InlineData("sysdm.cpl", "control.exe", "sysdm.cpl")]
    public void ManagementEntryPoint_IsNormalized(string command, string expectedFile, string expectedArguments)
    {
        var plan = PsExecService.TryBuildManagementCommand(
            RemoteOpsTool.Helpers.ProcessHelper.SplitCommandLine(command));

        Assert.NotNull(plan);
        Assert.Equal(expectedFile, plan.Value.FileName);
        Assert.Equal(expectedArguments, plan.Value.Arguments);
    }

    [Fact]
    public void AlternateCredentialArgumentQuoting_RoundTripsWindowsArguments()
    {
        var expected = new[]
        {
            "tool.exe",
            "",
            @"C:\Program Files\Remote Ops\",
            @"C:\Temp\A ""quoted"" file.txt",
            "%TEMP%",
            "a&b|c<d>e^f",
            "中文 参数"
        };

        var commandLine = RemoteOpsTool.Helpers.ProcessHelper.CombineArgumentsForWindows(expected);
        var actual = RemoteOpsTool.Helpers.ProcessHelper.SplitCommandLine(commandLine);

        Assert.Equal(expected, actual);
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
    public void LongCredentialedCommand_PrefersWmiToAvoidPsExecPasswordOnCommandLine()
    {
        Assert.True(PsExecService.ShouldPreferWmiTransport(
            @"DOMAIN\admin", "secret", new string('x', 701)));
    }

    [Fact]
    public void RemoteCommandRouting_PrefersPsExecForShortCredentialedCommands()
    {
        Assert.False(PsExecService.ShouldPreferWmiForRemoteCommand(
            preferWmiForRemoteCommands: true,
            username: @"DOMAIN\admin",
            password: "secret",
            command: "whoami"));
    }

    [Fact]
    public void RemoteCommandRouting_PrefersWmiForLongCredentialedCommands()
    {
        Assert.True(PsExecService.ShouldPreferWmiForRemoteCommand(
            preferWmiForRemoteCommands: true,
            username: @"DOMAIN\admin",
            password: "secret",
            command: new string('x', 701)));
    }

    [Fact]
    public void RemoteCommandRouting_DisabledPreferenceKeepsShortCommandsOnPsExec()
    {
        Assert.False(PsExecService.ShouldPreferWmiForRemoteCommand(
            preferWmiForRemoteCommands: false,
            username: @"DOMAIN\admin",
            password: "secret",
            command: new string('x', 701)));
    }

    [Fact]
    public void RemoteCommandRouting_NeverUsesWmiForInteractiveCommands()
    {
        Assert.False(PsExecService.ShouldPreferWmiForRemoteCommand(
            preferWmiForRemoteCommands: true,
            username: @"DOMAIN\admin",
            password: "secret",
            command: new string('x', 2000),
            interactiveSession: true));
    }

    [Fact]
    public void ShortCommand_DoesNotPreferWmi()
    {
        Assert.False(PsExecService.ShouldPreferWmiTransport(
            @"DOMAIN\admin", "secret", "whoami"));
    }

    [Fact]
    public void InteractiveCommand_DoesNotPreferWmi()
    {
        Assert.False(PsExecService.ShouldPreferWmiTransport(
            @"DOMAIN\admin", "secret", new string('x', 2000), interactiveSession: true));
    }

    [Fact]
    public void WmiCommandFailure_DoesNotFallback()
    {
        var result = new CommandResult(1, string.Empty, "目标命令自身失败");

        Assert.False(PsExecService.IsWmiTransportFailure(result));
    }

    [Fact]
    public void WmiTransportFailure_IsDetected()
    {
        var result = new CommandResult(-1, string.Empty,
            "WMI/DCOM 命令执行失败: 无法连接");

        Assert.True(PsExecService.IsWmiTransportFailure(result));
    }

    [Fact]
    public void WmiWin32ProcessCreateFailure_IsDetected()
    {
        var result = new CommandResult(2, string.Empty,
            "WMI Win32_Process.Create 被拒绝访问");

        Assert.True(PsExecService.IsWmiTransportFailure(result));
    }

    [Theory]
    [InlineData("远程命令输出：Win32_Process.Create 被拒绝访问")]
    [InlineData("远程命令输出：StdRegProv 写入失败")]
    [InlineData("The command printed Access is denied while inspecting WMI.")]
    public void WmiCommandOutput_IsNotMistakenForTransportFailure(string output)
    {
        var result = new CommandResult(1, output, string.Empty);

        Assert.False(PsExecService.IsWmiTransportFailure(result));
    }

    [Fact]
    public void WmiMarkedTransportFailure_IsDetectedEvenWhenReturnCodeIsNonNegative()
    {
        var result = new CommandResult(2, string.Empty,
            "[RemoteOpsTool.WmiTransportFailure] WMI Win32_Process.Create 被拒绝访问");

        Assert.True(PsExecService.IsWmiTransportFailure(result));
    }

    [Fact]
    public void PsExecServiceStartDenied_DetectsInvalidHandle()
    {
        var result = new CommandResult(6, string.Empty,
            "Couldn't install PSEXESVC service:\r\nThe handle is invalid.");

        Assert.True(PsExecService.IsPsExecServiceStartDenied(result));
    }

    [Fact]
    public void PsExecServiceStartDenied_DetectsPsexecServiceCommunicationFailure()
    {
        var result = new CommandResult(1, string.Empty,
            "Error establishing communication with PsExec service: The handle is invalid.");

        Assert.True(PsExecService.IsPsExecServiceStartDenied(result));
    }

    [Fact]
    public void SelectLocalSessionId_DoesNotAssumeSessionOne()
    {
        Assert.Equal(4, PsExecService.SelectLocalSessionId([4, 4, 0], 0, null));
        Assert.Equal(7, PsExecService.SelectLocalSessionId([4, 7], 2, 7));
        Assert.Equal(0, PsExecService.SelectLocalSessionId([4, 7], 2, 1));
        Assert.Equal(0, PsExecService.SelectLocalSessionId([], 0, null));
    }

    [Fact]
    public void SelectLocalSessionId_PrefersCurrentSessionWhenItIsInteractive()
    {
        Assert.Equal(3, PsExecService.SelectLocalSessionId([2, 3, 4], 3, null));
    }

    [Fact]
    public void SelectLocalSessionId_DiscardsSessionZero()
    {
        Assert.Equal(2, PsExecService.SelectLocalSessionId([0, 2], 0, null));
    }

    [Fact]
    public void PsExecServiceStartDenied_DoesNotClassifyGenericServiceCommandOutput()
    {
        var result = new CommandResult(1, string.Empty,
            "The command started a service and then printed Access is denied.");

        Assert.False(PsExecService.IsPsExecServiceStartDenied(result));
    }

    [Fact]
    public void PsExecServiceStartDenied_DoesNotClassifyUnrelatedServiceOutput()
    {
        var result = new CommandResult(1, string.Empty,
            "Could not start service BackupAgent: Access is denied.");

        Assert.False(PsExecService.IsPsExecServiceStartDenied(result));
    }

    [Fact]
    public void PsExecRecovery_ChangesCredentialTransportWithoutRepeatingServiceInstall()
    {
        var initial = new[]
        {
            @"\\REMOTE01", "-u", @"DOMAIN\admin", "-p", "secret",
            "-accepteula", "-r", "RemoteOpsTool_REMOTE01_1", "cmd", "/c", "whoami"
        };

        var attempts = PsExecService.BuildPsExecRecoveryAttempts(initial, @"DOMAIN\admin", "secret");

        Assert.Equal(2, attempts.Count);
        Assert.Contains("-r", attempts[0].Arguments);
        Assert.DoesNotContain("-u", attempts[1].Arguments);
        Assert.Contains("-r", attempts[1].Arguments);
    }
    [Fact]
    public void PsExecRecovery_WithoutCredentials_UsesSingleAttempt()
    {
        var initial = new[]
        {
            @"\\REMOTE01", "-accepteula", "-r", "RemoteOpsTool_REMOTE01_1",
            "cmd", "/c", "whoami"
        };

        var attempts = PsExecService.BuildPsExecRecoveryAttempts(initial, string.Empty, string.Empty);

        Assert.Single(attempts);
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
            "control.exe", "/name Microsoft.ProgramsAndFeatures", @"DOMAIN\admin", "task123", 7);

        Assert.Contains("RemoteOpsTool_Interactive_task123", script);
        Assert.Contains("Principal.LogonType = 3", script);
        Assert.Contains("Principal.RunLevel = 1", script);
        Assert.Contains("$sessionId = 7", script);
        Assert.Contains("RunEx($null, 4, $sessionId, $null)", script);
        Assert.DoesNotContain("RunEx($null, 4, $sessionId, $userName)", script);
        Assert.Contains("任务状态", script);
        Assert.Contains("$requestedAt = Get-Date", script);
        Assert.Contains("$deadline = $requestedAt.AddSeconds(10)", script);
        Assert.Contains("$instanceState", script);
        Assert.Contains("$lastRunTime", script);
        Assert.Contains("Running / Queued", script);
        Assert.Contains("RegisterTaskDefinition", script);
        Assert.Contains("DeleteTask", script);
        Assert.DoesNotContain(@"DOMAIN\admin", script);
        Assert.DoesNotContain("Principal.UserId", script);
    }

    [Fact]
    public void InteractiveTask_SessionZero_UsesRunWithoutSessionParameter()
    {
        var script = PsExecService.BuildInteractiveTaskScript(
            "notepad.exe", "", @"DOMAIN\admin", "task0", 0);

        Assert.Contains("$sessionId = 0", script);
        Assert.Contains("$registered.Run($null)", script);
        Assert.Contains("Principal.UserId = $userName", script);
    }

    [Fact]
    public void BuildArguments_InteractiveWithoutCredentials_OmitsLocalSystemFlag()
    {
        var service = CreateService();

        var args = service.BuildArguments("REMOTE01", "", "",
            "control.exe /name Microsoft.ProgramsAndFeatures", true, 7,
            wrapCmd: false, shell: CommandShell.Direct);

        Assert.DoesNotContain("-s", args);
        Assert.Contains("-i", args);
        Assert.Contains("-d", args);
        var interactiveIndex = args.ToList().IndexOf("-i");
        Assert.Equal("7", args[interactiveIndex + 1]);
    }

    [Fact]
    public void BuildArguments_BackgroundWithoutCredentials_KeepsLocalSystemFlag()
    {
        var service = CreateService();

        var args = service.BuildArguments("REMOTE01", "", "", "whoami", false, 0);

        Assert.Contains("-s", args);
        Assert.DoesNotContain("-i", args);
        Assert.DoesNotContain("-d", args);
    }

    [Fact]
    public void BuildArguments_InteractiveWithCredentials_OmitsLocalSystemFlag()
    {
        var service = CreateService();

        var args = service.BuildArguments("REMOTE01", "DOMAIN\\admin", "secret",
            "control.exe", true, 7, wrapCmd: false, shell: CommandShell.Direct);

        Assert.DoesNotContain("-s", args);
        Assert.Contains("-h", args);
        Assert.Contains("-i", args);
        Assert.Contains("-d", args);
    }

    [Fact]
    public void InteractiveTask_Base64RoundTripsSpecialCharacters()
    {
        var script = PsExecService.BuildInteractiveTaskScript(
            @"C:\Program Files\工具\app.exe",
            "参数 \"中文\" & %TEMP%",
            @"DOMAIN\运维.admin",
            "task@中文 id",
            7);

        Assert.Contains("Convert]::FromBase64String", script);
        Assert.Contains("RemoteOpsTool_Interactive_task中文id", script);
        Assert.DoesNotContain(@"C:\Program Files\工具\app.exe", script);
        Assert.DoesNotContain(@"DOMAIN\运维.admin", script);

        var decoded = DecodeEmbeddedUnicodeBase64(script);
        Assert.Equal(@"C:\Program Files\工具\app.exe", decoded[0]);
        Assert.Equal("参数 \"中文\" & %TEMP%", decoded[1]);
        Assert.Equal(@"DOMAIN\运维.admin", decoded[2]);
    }

    [Fact]
    public void InteractiveTask_StripsNonAlphanumericTaskId()
    {
        var script = PsExecService.BuildInteractiveTaskScript(
            "notepad.exe", "", @"DOMAIN\admin", "task-123_abc", 7);

        Assert.Contains("RemoteOpsTool_Interactive_task123abc", script);
        Assert.DoesNotContain("task-123_abc", script);
    }

    [Fact]
    public void InteractiveTask_RejectsTaskIdWithNoAlphanumericCharacters()
    {
        Assert.Throws<ArgumentException>(() =>
            PsExecService.BuildInteractiveTaskScript("notepad.exe", "", @"DOMAIN\admin", "！@#", 7));
    }

    [Fact]
    public void CombineTransportFailures_PreservesBothChannelReasons()
    {
        var wmi = new CommandResult(-1, string.Empty, "WMI/DCOM 无法连接");
        var psexec = new CommandResult(6, "Connecting to local system...", "Couldn't install PSEXESVC service: The handle is invalid.");

        var result = PsExecService.CombineTransportFailures(
            "远程命令执行", "WMI/DCOM", wmi, "PsExec", psexec);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("WMI/DCOM: WMI/DCOM 无法连接", result.StdErr);
        Assert.Contains("PsExec: Couldn't install PSEXESVC service", result.StdErr);
        Assert.Contains("The handle is invalid", result.StdErr);
    }

    [Fact]
    public void CombineTransportFailures_DoesNotExposePasswordFromCommandOutput()
    {
        var first = new CommandResult(-1, string.Empty, "WMI/DCOM 无法连接");
        var second = new CommandResult(6, string.Empty, "PsExec failed");

        var result = PsExecService.CombineTransportFailures(
            "远程命令执行", "WMI/DCOM", first, "PsExec", second);

        Assert.DoesNotContain("secret", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("alice", @"CONTOSO\alice", @"CONTOSO\alice")]
    [InlineData("other.user", @"CONTOSO\alice", "other.user")]
    [InlineData(@"CONTOSO\other.user", @"CONTOSO\alice", @"CONTOSO\other.user")]
    [InlineData("other.user@contoso.com", @"CONTOSO\alice", "other.user@contoso.com")]
    [InlineData(null, @"CONTOSO\alice", @"CONTOSO\alice")]
    [InlineData("", @"CONTOSO\alice", @"CONTOSO\alice")]
    [InlineData(null, "", "")]
    public void ResolveInteractiveTaskUser_QualifiesDesktopAccount(string? desktopUser, string connectionUser, string expected)
    {
        Assert.Equal(expected, PsExecService.ResolveInteractiveTaskUser(desktopUser, connectionUser));
    }

    [Theory]
    [InlineData("Alice", @"CONTOSO\alice", "WORKSTATION01", @"CONTOSO\alice")]
    [InlineData("other.user", @"CONTOSO\alice", "WORKSTATION01", @"CONTOSO\alice")]
    [InlineData("other.user", @"CONTOSO\alice", @"\\WORKSTATION01", @"CONTOSO\alice")]
    [InlineData("other.user", @"CONTOSO\alice", "workstation01.corp.contoso.com", @"CONTOSO\alice")]
    [InlineData(@"CONTOSO\other.user", @"CONTOSO\alice", "WORKSTATION01", @"CONTOSO\other.user")]
    [InlineData("other.user@contoso.com", @"CONTOSO\alice", "WORKSTATION01", "other.user@contoso.com")]
    [InlineData(null, @"CONTOSO\alice", "WORKSTATION01", @"CONTOSO\alice")]
    [InlineData("", @"CONTOSO\alice", "WORKSTATION01", @"CONTOSO\alice")]
    [InlineData("other.user", @"CONTOSO\alice", "10.1.2.3", @"CONTOSO\alice")]
    [InlineData("other.user", "", "WORKSTATION01", @"WORKSTATION01\other.user")]
    [InlineData("other.user", "", "10.0.0.1", "other.user")]
    public void ResolveSchtasksInteractiveRunAsUser_QualifiesForRemoteSchTasks(
        string? desktopUser, string connectionUser, string targetHost, string expected)
    {
        Assert.Equal(expected, PsExecService.ResolveSchtasksInteractiveRunAsUser(desktopUser, connectionUser, targetHost));
    }

    [Theory]
    [InlineData("echo hello", "/d", "/s", "/c", "echo hello")]
    [InlineData("\"C:\\Program Files\\Tool\\tool.exe\" \"C:\\Temp\\A B.txt\"", "/d", "/s", "/c", "\"C:\\Program Files\\Tool\\tool.exe\" \"C:\\Temp\\A B.txt\"")]
    [InlineData("echo \"quoted\" & echo %TEMP%", "/d", "/s", "/c", "echo \"quoted\" & echo %TEMP%")]
    public void BuildLocalCommand_CmdArgumentsRoundTripWithoutNestedQuoteCorruption(
        string command, string expected0, string expected1, string expected2, string expectedCommand)
    {
        var local = PsExecService.BuildLocalCommand(command, CommandShell.Cmd);

        Assert.Equal("cmd.exe", local.FileName, ignoreCase: true);
        Assert.Equal(new[] { expected0, expected1, expected2, expectedCommand },
            RemoteOpsTool.Helpers.ProcessHelper.SplitCommandLine(local.Arguments));
    }

    [Fact]
    public void LocalElevationBootstrap_DoesNotUseCmdRedirection()
    {
        var bootstrap = PsExecService.BuildLocalElevationBootstrapScript();
        var childBootstrap = PsExecService.BuildLocalElevatedChildScript();

        Assert.DoesNotContain("$env:ComSpec", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/d /s /c", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REMOTEOPSTOOL_ELEVATE_CHILD_BOOTSTRAP", bootstrap);
        Assert.Contains("RedirectStandardOutput", childBootstrap);
        Assert.Contains("REMOTEOPSTOOL_ELEVATE_ARGS", childBootstrap);
        Assert.DoesNotContain("/d /s /c", childBootstrap, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] DecodeEmbeddedUnicodeBase64(string script)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            script, @"FromBase64String\('([^']+)'\)");
        return matches
            .Select(m => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(m.Groups[1].Value)))
            .ToArray();
    }

    private static PsExecService CreateService()
    {
        var settings = new TestSettingsService();
        settings.Settings.OmitPsExecExplicitCredentialsWhenRunAs = false;
        return new PsExecService(settings, new TestLogService());
    }
}
