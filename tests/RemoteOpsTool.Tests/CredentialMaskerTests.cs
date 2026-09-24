using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class CredentialMaskerTests
{
    [Theory]
    [InlineData("sc config Spooler obj= \"DOMAIN\\svc\" password= \"S3cret!\"", "S3cret!")]
    [InlineData("sc config Spooler obj= \"DOMAIN\\svc\" password= S3cret!", "S3cret!")]
    [InlineData("tool --password=S3cret! run", "S3cret!")]
    public void MaskPasswordInCommand_MasksPasswordAssignments(string command, string password)
    {
        var masked = CredentialMasker.MaskPasswordInCommand(command, password: null);

        Assert.DoesNotContain(password, masked, StringComparison.Ordinal);
        Assert.Contains("********", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskPasswordInCommand_StillReplacesKnownPsExecPassword()
    {
        var masked = CredentialMasker.MaskPasswordInCommand(
            @"PsExec.exe \\host -u DOMAIN\svc -p S3cret! whoami",
            "S3cret!");

        Assert.DoesNotContain("S3cret!", masked, StringComparison.Ordinal);
        Assert.Contains("-p ********", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskPasswordInCommand_DoesNotMaskSimilarParameterNames()
    {
        const string command = "tool StartPasswordMetadata=visible";

        var masked = CredentialMasker.MaskPasswordInCommand(command, password: null);

        Assert.Equal(command, masked);
    }

    [Fact]
    public void MaskPasswordArguments_MasksStandaloneSwitchBeforeQuoting()
    {
        const string password = "P@ss\"word!";
        var arguments = new[] { @"\\HOST01", "-u", @"CONTOSO\opsuser", "-p", password, "whoami" };

        var masked = CredentialMasker.MaskPasswordArguments(arguments);
        var joined = string.Join(" ", masked);

        Assert.DoesNotContain(password, joined, StringComparison.Ordinal);
        Assert.Contains("********", joined, StringComparison.Ordinal);
        Assert.Equal("-p", masked[3]);
        Assert.Equal("********", masked[4]);
    }

    [Fact]
    public void MaskPasswordArguments_MasksPasswordContainingBackslashAndSpace()
    {
        const string password = @"P@ss\ word";
        var arguments = new[] { "PsExec.exe", @"\\HOST01", "-p", password, "whoami" };

        var joined = string.Join(" ", CredentialMasker.MaskPasswordArguments(arguments));

        Assert.DoesNotContain(password, joined, StringComparison.Ordinal);
        Assert.Contains("-p ********", joined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-p:S3cret!")]
    [InlineData("-p=S3cret!")]
    [InlineData("/p:S3cret!")]
    public void MaskPasswordArguments_MasksInlinePasswordSwitch(string passwordSwitch)
    {
        var masked = CredentialMasker.MaskPasswordArguments(["tool", passwordSwitch, "run"]);

        Assert.EndsWith("********", masked[1], StringComparison.Ordinal);
        Assert.DoesNotContain("S3cret!", string.Join(" ", masked), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PsExec.exe \\\\host -p S3cret! whoami")]
    [InlineData("PsExec.exe \\\\host -p:S3cret! whoami")]
    [InlineData("PsExec.exe \\\\host -p=S3cret! whoami")]
    [InlineData("PsExec.exe \\\\host /p S3cret! whoami")]
    public void MaskPasswordInCommand_MasksUnknownPsExecPasswordSwitch(string command)
    {
        var masked = CredentialMasker.MaskPasswordInCommand(command, password: null);

        Assert.DoesNotContain("S3cret!", masked, StringComparison.Ordinal);
        Assert.Contains("********", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskSchedulerFormatArguments_MasksPasswordSwitch()
    {
        const string password = @"P@ss\ word";
        var arguments = new[]
        {
            "schtasks.exe", "/Create", "/TN", "task", "/TR", "whoami",
            "/U", @"CONTOSO\opsuser", "/P", password, "/F"
        };

        var formatted = TaskSchedulerService.FormatArguments(arguments);

        Assert.DoesNotContain(password, formatted, StringComparison.Ordinal);
        Assert.Contains("/P ********", formatted, StringComparison.Ordinal);
    }
}
