using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class TransportFailureClassifierTests
{
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(6, false)]
    public void IsPsExecLauncherFailure_OnlyTreatsMinusOneAsLauncherFailure(int exitCode, bool expected)
    {
        var result = new CommandResult(exitCode, string.Empty, string.Empty);

        Assert.Equal(expected, TransportFailureClassifier.IsPsExecLauncherFailure(result));
    }

    [Fact]
    public void IsPsExecTransportFailure_ReturnsTrueForLauncherFailure()
    {
        Assert.True(TransportFailureClassifier.IsPsExecTransportFailure(
            new CommandResult(-1, string.Empty, string.Empty)));
    }

    [Fact]
    public void IsPsExecTransportFailure_ReturnsTrueForServiceStartDenied()
    {
        var result = new CommandResult(6, string.Empty,
            "Couldn't install PSEXESVC service:\r\nThe handle is invalid.");

        Assert.True(TransportFailureClassifier.IsPsExecTransportFailure(result));
    }

    [Fact]
    public void IsPsExecTransportFailure_ReturnsFalseForCommandExitCode()
    {
        var result = new CommandResult(6, "The target program printed Access is denied.", string.Empty);

        Assert.False(TransportFailureClassifier.IsPsExecTransportFailure(result));
    }

    [Theory]
    [InlineData("Couldn't install PSEXESVC service:\r\nThe handle is invalid.")]
    [InlineData("Error establishing communication with PsExec service: The handle is invalid.")]
    [InlineData("无法安装 RemoteOpsTool_REMOTE01_1 服务: 拒绝访问。")]
    public void IsPsExecServiceStartDenied_DetectsPsExecServiceFailures(string output)
    {
        Assert.True(TransportFailureClassifier.IsPsExecServiceStartDenied(
            new CommandResult(6, string.Empty, output)));
    }

    [Theory]
    [InlineData("The command started a service and then printed Access is denied.")]
    [InlineData("Could not start service BackupAgent: Access is denied.")]
    [InlineData("Access is denied.")]
    [InlineData("The handle is invalid.")]
    public void IsPsExecServiceStartDenied_DoesNotClassifyUnrelatedOutput(string output)
    {
        Assert.False(TransportFailureClassifier.IsPsExecServiceStartDenied(
            new CommandResult(1, string.Empty, output)));
    }

    [Fact]
    public void IsWmiTransportFailure_ReturnsFalseForSuccessfulCommand()
    {
        Assert.False(TransportFailureClassifier.IsWmiTransportFailure(
            new CommandResult(0, "ok", string.Empty)));
    }

    [Fact]
    public void IsWmiTransportFailure_DetectsExplicitMarker()
    {
        var result = new CommandResult(2, string.Empty,
            "[RemoteOpsTool.WmiTransportFailure] WMI Win32_Process.Create 被拒绝访问");

        Assert.True(TransportFailureClassifier.IsWmiTransportFailure(result));
    }

    [Theory]
    [InlineData("WMI/DCOM 无法连接")]
    [InlineData("WMI/DCOM 命令执行失败: 无法连接")]
    [InlineData("WMI/DCOM 命令引导进程超时")]
    [InlineData("WMI/DCOM 命令执行超过 30 秒")]
    public void IsWmiTransportFailure_DetectsCompatibilityPhrases(string output)
    {
        Assert.True(TransportFailureClassifier.IsWmiTransportFailure(
            new CommandResult(-1, string.Empty, output)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(21)]
    public void IsWmiTransportFailure_DetectsLegacyCreateFailures(int exitCode)
    {
        var result = new CommandResult(exitCode, string.Empty,
            "WMI Win32_Process.Create 被拒绝访问");

        Assert.True(TransportFailureClassifier.IsWmiTransportFailure(result));
    }

    [Theory]
    [InlineData("远程命令输出：Win32_Process.Create 被拒绝访问")]
    [InlineData("远程命令输出：StdRegProv 写入失败")]
    [InlineData("The command printed Access is denied while inspecting WMI.")]
    public void IsWmiTransportFailure_DoesNotClassifyCommandOutput(string output)
    {
        Assert.False(TransportFailureClassifier.IsWmiTransportFailure(
            new CommandResult(1, output, string.Empty)));
    }

    [Fact]
    public void CreateWmiTransportFailure_UsesMarkerAndMinusOneExitCode()
    {
        var result = TransportFailureClassifier.CreateWmiTransportFailure("连接失败");

        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith(
            TransportFailureClassifier.WmiTransportFailureMarker,
            result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("连接失败", result.StdErr);
    }

    [Fact]
    public void NormalizeDetachedLaunchResult_ConvertsStartedPidFailureToSuccess()
    {
        var result = new CommandResult(-1, "The application started with process id 4321.", string.Empty);

        var normalized = TransportFailureClassifier.NormalizeDetachedLaunchResult(result);

        Assert.Equal(0, normalized.ExitCode);
        Assert.Equal(result.StdOut, normalized.StdOut);
    }

    [Fact]
    public void NormalizeDetachedLaunchResult_LeavesUnrelatedFailureUntouched()
    {
        var result = new CommandResult(3, "No process was started.", "boom");

        var normalized = TransportFailureClassifier.NormalizeDetachedLaunchResult(result);

        Assert.Equal(result, normalized);
    }

    [Fact]
    public void SummarizePsExecFailure_TruncatesLongOutput()
    {
        var longOutput = new string('x', 500);

        var summary = TransportFailureClassifier.SummarizePsExecFailure(
            new CommandResult(6, string.Empty, longOutput));

        Assert.EndsWith("...", summary);
        Assert.Equal(183, summary.Length);
    }

    [Fact]
    public void SummarizePsExecFailure_FallsBackToExitCodeWhenEmpty()
    {
        Assert.Equal("exit=7", TransportFailureClassifier.SummarizePsExecFailure(
            new CommandResult(7, string.Empty, string.Empty)));
    }

    [Fact]
    public void SummarizeCommandFailure_TruncatesLongOutput()
    {
        var longOutput = new string('y', 500);

        var summary = TransportFailureClassifier.SummarizeCommandFailure(
            new CommandResult(1, longOutput, string.Empty));

        Assert.EndsWith("...", summary);
        Assert.Equal(243, summary.Length);
    }

    [Fact]
    public void CombineTransportFailures_ReportsFirstChannelMissingDetail()
    {
        var second = new CommandResult(6, string.Empty, "Couldn't install PSEXESVC service.");

        var result = TransportFailureClassifier.CombineTransportFailures(
            "远程命令执行", "WMI/DCOM", null, "PsExec", second);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("WMI/DCOM: 未返回错误详情", result.StdErr);
        Assert.Contains("PsExec: Couldn't install PSEXESVC service.", result.StdErr);
    }

    [Fact]
    public void CombineTransportFailures_DoesNotContainInputSecretsThatWereNeverPresent()
    {
        var first = new CommandResult(-1, string.Empty, "WMI/DCOM 无法连接");
        var second = new CommandResult(6, string.Empty, "PsExec failed");

        var result = TransportFailureClassifier.CombineTransportFailures(
            "远程命令执行", "WMI/DCOM", first, "PsExec", second);

        Assert.DoesNotContain("secret", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }
}
