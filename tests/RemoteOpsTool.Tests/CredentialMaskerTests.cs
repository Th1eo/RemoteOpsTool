using RemoteOpsTool.Helpers;

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
}
