using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class ComputerManagementHelperTests
{
    [Fact]
    public void BuildLaunchPlan_OpensLocalMmcConnectedToRemoteComputer()
    {
        var plan = ComputerManagementHelper.BuildLaunchPlan(@"  \\WORKSTATION01  ");

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "mmc.exe"), plan.FileName);
        Assert.Equal("WORKSTATION01", plan.TargetHost);
        Assert.Equal(["compmgmt.msc", @"/computer=\\WORKSTATION01"], plan.Arguments);
    }

    [Fact]
    public void ResolveNetworkCredentialIdentity_PreservesDomainQualifiedUser()
    {
        var identity = ProcessHelper.ResolveNetworkCredentialIdentity(@"CONTOSO\operator");

        Assert.Equal("operator", identity.User);
        Assert.Equal("CONTOSO", identity.Domain);
    }

    [Fact]
    public void ResolveNetworkCredentialIdentity_UsesNullDomainForUpn()
    {
        var identity = ProcessHelper.ResolveNetworkCredentialIdentity("operator@contoso.example");

        Assert.Equal("operator@contoso.example", identity.User);
        Assert.Null(identity.Domain);
    }
}
