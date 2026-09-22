using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class ServiceCommandHelperTests
{
    [Theory]
    [InlineData("2 AUTO_START (DELAYED)", "自动(延迟启动)")]
    [InlineData("2 AUTO_START", "自动")]
    [InlineData("3 DEMAND_START", "手动")]
    [InlineData("4 DISABLED", "禁用")]
    [InlineData("5", "自动(延迟启动)")]
    [InlineData("", "手动")]
    public void ParseScStartType_MapsScTokensToUiValues(string raw, string expected)
    {
        Assert.Equal(expected, ServiceCommandHelper.ParseScStartType(raw));
    }

    [Theory]
    [InlineData("AUTO", false, "自动")]
    [InlineData("AUTO", true, "自动(延迟启动)")]
    [InlineData("MANUAL", false, "手动")]
    [InlineData("DISABLED", false, "禁用")]
    [InlineData("", false, "手动")]
    public void StartTypeFromWmi_RespectsDelayedFlag(string startMode, bool delayed, string expected)
    {
        Assert.Equal(expected, ServiceCommandHelper.StartTypeFromWmi(startMode, delayed));
    }

    [Theory]
    [InlineData("自动", "auto")]
    [InlineData("自动(延迟启动)", "delayed-auto")]
    [InlineData("手动", "demand")]
    [InlineData("禁用", "disabled")]
    public void ToScStartOption_MapsUiValuesToScOptions(string ui, string expected)
    {
        Assert.Equal(expected, ServiceCommandHelper.ToScStartOption(ui));
    }

    [Fact]
    public void IsServiceNotInstalled_DetectsExitCodeAndLocalizedText()
    {
        Assert.True(ServiceCommandHelper.IsServiceNotInstalled(1060, null));
        Assert.True(ServiceCommandHelper.IsServiceNotInstalled(1, "ERROR_SERVICE_DOES_NOT_EXIST"));
        Assert.True(ServiceCommandHelper.IsServiceNotInstalled(1, "指定的服务不存在。"));
        Assert.True(ServiceCommandHelper.IsServiceNotInstalled(1, "The specified service does not exist as an installed service."));
        Assert.False(ServiceCommandHelper.IsServiceNotInstalled(0, "The operation completed successfully."));
    }

    [Fact]
    public void IsPasswordCommandLineSafe_RejectsUnescapableCharacters()
    {
        Assert.False(ServiceCommandHelper.IsPasswordCommandLineSafe("pa\"ss", out var quoteReason));
        Assert.False(string.IsNullOrEmpty(quoteReason));

        Assert.False(ServiceCommandHelper.IsPasswordCommandLineSafe("pa%ss", out var percentReason));
        Assert.False(string.IsNullOrEmpty(percentReason));

        Assert.True(ServiceCommandHelper.IsPasswordCommandLineSafe("P@ssw0rd!", out var okReason));
        Assert.Equal(string.Empty, okReason);
    }

    [Fact]
    public void ValidateLogOnInput_RejectsMissingOrMismatchedCredentials()
    {
        Assert.Null(ServiceCommandHelper.ValidateLogOnInput(true, string.Empty, null, null));
        Assert.False(string.IsNullOrEmpty(ServiceCommandHelper.ValidateLogOnInput(false, "  ", "pw", "pw")));
        Assert.False(string.IsNullOrEmpty(ServiceCommandHelper.ValidateLogOnInput(false, "svc", "pw1", "pw2")));
        Assert.False(string.IsNullOrEmpty(ServiceCommandHelper.ValidateLogOnInput(false, "svc", string.Empty, string.Empty)));
        Assert.Null(ServiceCommandHelper.ValidateLogOnInput(false, "svc", "pw", "pw"));
    }

    [Theory]
    [InlineData("StartService", 10u, WmiServiceMethodOutcome.AlreadyInTargetState)]
    [InlineData("StopService", 5u, WmiServiceMethodOutcome.AlreadyInTargetState)]
    [InlineData("StartService", 0u, WmiServiceMethodOutcome.Succeeded)]
    [InlineData("StartService", 2u, WmiServiceMethodOutcome.AccessDenied)]
    [InlineData("StopService", 11u, WmiServiceMethodOutcome.Failed)]
    [InlineData("StartService", 5u, WmiServiceMethodOutcome.Failed)]
    [InlineData("Change", 1060u, WmiServiceMethodOutcome.NotFound)]
    public void MapWmiMethodReturnCode_ClassifiesResults(string method, uint code, WmiServiceMethodOutcome expected)
    {
        Assert.Equal(expected, ServiceCommandHelper.MapWmiMethodReturnCode(method, code));
    }

    [Fact]
    public void BuildConfigCommands_NoChanges_ProducesNothing()
    {
        var snapshot = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            BinaryPath = @"C:\Windows\System32\spoolsv.exe",
            StartParams = string.Empty,
            UseLocalSystem = true,
            AllowDesktopInteract = false,
            FirstFailure = "不操作",
            SecondFailure = "不操作",
            SubsequentFailure = "不操作",
            ResetFailDays = "1",
            RestartMinutes = "1",
        };

        var commands = ServiceCommandHelper.BuildConfigCommands(snapshot, snapshot, newPassword: null);

        Assert.Empty(commands);
    }

    [Fact]
    public void BuildConfigCommands_AccountUnchanged_DoesNotEmitObjOrType()
    {
        var current = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            UseLocalSystem = true,
            AllowDesktopInteract = false,
        };

        // 仅修改失败恢复策略，账号与交互标志都不应产生命令。
        var desired = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            UseLocalSystem = true,
            AllowDesktopInteract = false,
            FirstFailure = "重新启动服务",
        };

        var commands = ServiceCommandHelper.BuildConfigCommands(current, desired, newPassword: null);

        var single = Assert.Single(commands);
        Assert.Equal(ServiceMutationKind.FailureActions, single.Kind);
        Assert.DoesNotContain("obj=", single.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("type=", single.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigCommands_InteractiveFlagOnlyEmittedWhenChanged()
    {
        var current = new ServiceConfigSnapshot { ServiceName = "Svc", UseLocalSystem = true, AllowDesktopInteract = false };
        var desired = new ServiceConfigSnapshot { ServiceName = "Svc", UseLocalSystem = true, AllowDesktopInteract = true };

        var commands = ServiceCommandHelper.BuildConfigCommands(current, desired, newPassword: null);

        var single = Assert.Single(commands);
        Assert.Contains("type= interact", single.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigCommands_AccountChange_MasksPasswordInDisplayButKeepsRealCommand()
    {
        var current = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            UseLocalSystem = true,
            AllowDesktopInteract = false,
        };
        var desired = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            UseLocalSystem = false,
            LogOnAccount = @"DOMAIN\svc",
            AllowDesktopInteract = false,
        };

        var commands = ServiceCommandHelper.BuildConfigCommands(current, desired, newPassword: "S3cret!");

        var account = Assert.Single(commands, c => c.Kind == ServiceMutationKind.Account);
        Assert.Contains("obj=", account.Command, StringComparison.Ordinal);
        Assert.Contains("password= \"S3cret!\"", account.Command, StringComparison.Ordinal);

        // 真实命令里带密码，但日志必须只写脱敏后的文本。
        Assert.DoesNotContain("S3cret!", account.DisplayCommand, StringComparison.Ordinal);
        Assert.Contains("********", account.DisplayCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigCommands_PasswordOnlyResetsPassword()
    {
        var current = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            UseLocalSystem = false,
            LogOnAccount = @"DOMAIN\svc",
            AllowDesktopInteract = false,
        };
        var desired = current;

        var commands = ServiceCommandHelper.BuildConfigCommands(current, desired, newPassword: "NewPw!");

        var account = Assert.Single(commands);
        Assert.Equal(ServiceMutationKind.Account, account.Kind);
        Assert.Contains("password=", account.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("NewPw!", account.DisplayCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWmiChangePlan_AccountChange_IncludesStartNameAndPassword()
    {
        var current = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            UseLocalSystem = true,
        };
        var desired = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            UseLocalSystem = false,
            LogOnAccount = @"DOMAIN\svc",
        };

        var plan = ServiceCommandHelper.BuildWmiChangePlan(current, desired, "S3cret!");

        Assert.False(plan.RequiresScForStartType);
        Assert.Equal(@"DOMAIN\svc", plan.Parameters["StartName"]);
        Assert.Equal("S3cret!", plan.Parameters["StartPassword"]);
    }

    [Fact]
    public void BuildWmiChangePlan_AutomaticStartType_UsesWmiStartMode()
    {
        var current = new ServiceConfigSnapshot { ServiceName = "Spooler", StartType = "手动" };
        var desired = new ServiceConfigSnapshot { ServiceName = "Spooler", StartType = "自动" };

        var plan = ServiceCommandHelper.BuildWmiChangePlan(current, desired, newPassword: null);

        Assert.False(plan.RequiresScForStartType);
        Assert.Equal("Automatic", plan.Parameters["StartMode"]);
    }

    [Fact]
    public void BuildWmiChangePlan_DelayedStartType_LeavesStartModeToSc()
    {
        var current = new ServiceConfigSnapshot { ServiceName = "Spooler", StartType = "自动" };
        var desired = new ServiceConfigSnapshot { ServiceName = "Spooler", StartType = "自动(延迟启动)" };

        var plan = ServiceCommandHelper.BuildWmiChangePlan(current, desired, newPassword: null);

        Assert.True(plan.RequiresScForStartType);
        Assert.DoesNotContain("StartMode", plan.Parameters.Keys);
    }

    [Fact]
    public void BuildWmiChangePlan_NoChanges_HasNoParameters()
    {
        var snapshot = new ServiceConfigSnapshot
        {
            ServiceName = "Spooler",
            StartType = "自动",
            UseLocalSystem = true,
        };

        var plan = ServiceCommandHelper.BuildWmiChangePlan(snapshot, snapshot, newPassword: null);

        Assert.False(plan.RequiresScForStartType);
        Assert.False(plan.HasParameters);
    }

    [Fact]
    public void ValidateLogOnInput_PasswordOptionalWhenAccountUnchanged()
    {
        Assert.Null(ServiceCommandHelper.ValidateLogOnInput(
            useLocalSystem: false,
            logOnAccount: @"DOMAIN\svc",
            password: string.Empty,
            confirmPassword: string.Empty,
            passwordRequired: false));

        Assert.False(string.IsNullOrEmpty(ServiceCommandHelper.ValidateLogOnInput(
            useLocalSystem: false,
            logOnAccount: @"DOMAIN\svc",
            password: string.Empty,
            confirmPassword: string.Empty,
            passwordRequired: true)));
    }
    [Fact]
    public void ComposePathName_QuotesPathsWithSpaces()
    {
        Assert.Equal("\"C:\\Program Files\\app.exe\" -x",
            ServiceCommandHelper.ComposePathName(@"C:\Program Files\app.exe", "-x"));
        Assert.Equal(@"C:\Windows\app.exe", ServiceCommandHelper.ComposePathName(@"C:\Windows\app.exe", ""));
    }

    [Fact]
    public void CacheKeys_ServicesForCredential_SeparatesUsersAndNormalizesCase()
    {
        var key = CacheKeys.ServicesForCredential(@"DOMAIN\Alice");
        Assert.Equal(CacheKeys.ServicesForCredential(@"domain\alice"), key);
        Assert.NotEqual(CacheKeys.Services, key);
        Assert.NotEqual(CacheKeys.ServicesForCredential(@"DOMAIN\bob"), key);
        Assert.Equal(CacheKeys.Services, CacheKeys.ServicesForCredential(null));
    }
    [Fact]
    public void CacheKeys_ServiceProperties_SeparatesUsersAndNormalizesServiceName()
    {
        var key = CacheKeys.ServiceProperties("Spooler", @"DOMAIN\Alice");

        Assert.Equal(CacheKeys.ServiceProperties("spooler", @"domain\alice"), key);
        Assert.Equal(CacheKeys.ServiceProperties("SPOOLER", @"DOMAIN\ALICE"), key);
        Assert.NotEqual(CacheKeys.ServiceProperties("Spooler", @"DOMAIN\Bob"), key);
        Assert.NotEqual(CacheKeys.ServiceProperties("Winmgmt", @"DOMAIN\Alice"), key);
        Assert.StartsWith(CacheKeys.ServicePropertiesPrefix + "_", key);
    }
}
