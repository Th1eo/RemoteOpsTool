using RemoteOpsTool;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class TaskSchedulerServiceTests
{
    [Fact]
    public void BuildCreateArguments_BackgroundWithoutCredentials_DoesNotAddUserPasswordOrInteractive()
    {
        var actual = TaskSchedulerService.BuildCreateArguments(
            "server", "", "", "Task1", "cmd /c dir", interactive: false, runAsUser: null);

        Assert.Equal(
            new[]
            {
                "/Create", "/S", "server", "/TN", "Task1", "/TR", "cmd /c dir",
                "/SC", "ONCE", "/ST", "00:00",
                "/RU", "SYSTEM", "/RL", "HIGHEST", "/F",
            },
            actual.ToArray());
    }

    [Fact]
    public void BuildCreateArguments_Interactive_UsesRunAsUserAndInteractiveToken()
    {
        var actual = TaskSchedulerService.BuildCreateArguments(
            "server", "", "", "Task1", "notepad.exe",
            interactive: true, runAsUser: @"DOMAIN\alice");

        Assert.Equal(
            new[]
            {
                "/Create", "/S", "server", "/TN", "Task1", "/TR", "notepad.exe",
                "/SC", "ONCE", "/ST", "00:00",
                "/RU", @"DOMAIN\alice", "/IT", "/RL", "HIGHEST", "/F",
            },
            actual.ToArray());
    }

    [Fact]
    public void BuildCreateArguments_WithCredentials_AddsUserAndPassword()
    {
        var actual = TaskSchedulerService.BuildCreateArguments(
            "server", @"DOMAIN\admin", "secret", "Task1", "cmd /c dir",
            interactive: false, runAsUser: null);

        Assert.Contains("/U", actual);
        Assert.Contains(@"DOMAIN\admin", actual);
        Assert.Contains("/P", actual);
        Assert.Contains("secret", actual);
        Assert.DoesNotContain("/IT", actual);
    }

    [Fact]
    public void SanitizeTaskName_KeepsOnlyAlphanumericCharacters()
    {
        Assert.Equal("RemoteOpsTool123", TaskSchedulerService.SanitizeTaskName("Remote-Ops Tool_123"));
    }

    [Fact]
    public void SanitizeTaskName_ThrowsWhenNoAlphanumericCharacterRemains()
    {
        Assert.Throws<ArgumentException>(() => TaskSchedulerService.SanitizeTaskName("---"));
    }

    [Theory]
    [InlineData("Last Task Result: 0", 0)]
    [InlineData("Last Task Result: 1", 1)]
    [InlineData("Last Task Result: 0x0", 0)]
    [InlineData("Last Task Result: 0x41303", 0x41303)]
    [InlineData("上次任务结果: 0x80070005", unchecked((int)0x80070005))]
    public void ParseLastTaskResult_ParsesEnglishChineseHexAndDecimal(
        string output, int expected)
    {
        Assert.Equal(expected, TaskSchedulerService.ParseLastTaskResult(output));
    }

    [Fact]
    public void ParseLastTaskResult_ReturnsNullWhenLabelMissing()
    {
        Assert.Null(TaskSchedulerService.ParseLastTaskResult("some unrelated output"));
    }

    [Theory]
    [InlineData("Status: Ready", "Ready")]
    [InlineData("Status: Disabled", "Disabled")]
    [InlineData("状态: 已启用", "已启用")]
    [InlineData("狀態: 已停用", "已停用")]
    public void ParseTaskState_ParsesEnglishAndChineseLabels(string output, string expected)
    {
        Assert.Equal(expected, TaskSchedulerService.ParseTaskState(output));
    }

    [Fact]
    public void ParseTaskState_ReturnsNullWhenLabelMissing()
    {
        Assert.Null(TaskSchedulerService.ParseTaskState("no status here"));
    }

    [Fact]
    public void IsSchtasksTransportFailure_RecognizesTransportErrors()
    {
        Assert.True(TaskSchedulerService.IsSchtasksTransportFailure(new CommandResult(-1, "", "")));
        Assert.True(TaskSchedulerService.IsSchtasksTransportFailure(new CommandResult(1, "", "ERROR: Access is denied.")));
        Assert.True(TaskSchedulerService.IsSchtasksTransportFailure(new CommandResult(1, "", "RPC server is unavailable")));
        Assert.True(TaskSchedulerService.IsSchtasksTransportFailure(new CommandResult(1, "", "找不到网络路径")));
    }

    [Fact]
    public void IsSchtasksTransportFailure_DoesNotTreatNormalCommandExitAsTransportFailure()
    {
        Assert.False(TaskSchedulerService.IsSchtasksTransportFailure(new CommandResult(0, "ok", "")));
        Assert.False(TaskSchedulerService.IsSchtasksTransportFailure(new CommandResult(2, "", "command returned error")));
    }

    [Fact]
    public void BuildRunArguments_BuildsRunCommand()
    {
        Assert.Equal(
            new[] { "/Run", "/S", "server", "/TN", "Task1" },
            TaskSchedulerService.BuildRunArguments("server", "", "", "Task1").ToArray());
    }

    [Fact]
    public void BuildQueryArguments_BuildsVerboseListQuery()
    {
        Assert.Equal(
            new[] { "/Query", "/S", "server", "/TN", "Task1", "/V", "/FO", "LIST" },
            TaskSchedulerService.BuildQueryArguments("server", "", "", "Task1").ToArray());
    }

    [Fact]
    public void BuildDeleteArguments_BuildsForceDelete()
    {
        Assert.Equal(
            new[] { "/Delete", "/S", "server", "/TN", "Task1", "/F" },
            TaskSchedulerService.BuildDeleteArguments("server", "", "", "Task1").ToArray());
    }

    [Fact]
    public void BuildTaskName_HasStablePrefixAndGuidLength()
    {
        var taskName = TaskSchedulerService.BuildTaskName();

        Assert.StartsWith("RemoteOpsTool_", taskName);
        Assert.Equal(46, taskName.Length);
    }
}
