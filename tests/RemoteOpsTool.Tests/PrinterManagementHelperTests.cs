using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public class PrinterManagementHelperTests
{
    [Fact]
    public void BuildRemotePropertiesLaunchPlan_UsesLocalRundll32AndRemoteComputerArgument()
    {
        var plan = PrinterManagementHelper.BuildRemotePropertiesLaunchPlan(@"  \\WORKSTATION01  ", "Microsoft Print to PDF");

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), plan.FileName);
        Assert.Equal("WORKSTATION01", plan.TargetHost);
        Assert.Equal(
            ["printui.dll,PrintUIEntry", "/p", "/n", "Microsoft Print to PDF", @"/c\\WORKSTATION01"],
            plan.Arguments);
    }

    [Fact]
    public void OpenRemoteProperties_RejectsEmptyPrinterName()
    {
        var result = PrinterManagementHelper.OpenRemoteProperties(
            "WORKSTATION01", "CONTOSO\\operator", "password", " ");

        Assert.False(result.Success);
        Assert.Contains("打印机名称", result.StdErr);
    }

    [Fact]
    public void BuildRemoteAddWizardLaunchPlan_UsesLocalRundll32AndPointsAtRemoteComputer()
    {
        var plan = PrinterManagementHelper.BuildRemoteAddWizardLaunchPlan(@"  \\WORKSTATION01  ");

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), plan.FileName);
        Assert.Equal("WORKSTATION01", plan.TargetHost);
        Assert.Equal(["printui.dll,PrintUIEntry", "/il", @"/c\\WORKSTATION01"], plan.Arguments);
    }

    [Fact]
    public void OpenRemoteAddWizard_RejectsLocalHostInsteadOfOpeningWizardOnTarget()
    {
        var result = PrinterManagementHelper.OpenRemoteAddWizard(
            Environment.MachineName, "CONTOSO\\operator", "password");

        Assert.False(result.Success);
        Assert.Contains("本机", result.StdErr);
    }

    [Fact]
    public void EvaluateAddWizardOutcome_ReportsTargetUpdatedWhenTargetGainsPrinter()
    {
        var outcome = PrinterManagementHelper.EvaluateAddWizardOutcome(
            ["Microsoft Print to PDF"],
            ["Microsoft Print to PDF", "RemoteOps Test Printer"],
            ["Fax"],
            ["Fax"],
            out var addedTarget,
            out var addedLocal);

        Assert.Equal(PrinterAddWizardOutcome.TargetUpdated, outcome);
        Assert.Equal(["RemoteOps Test Printer"], addedTarget);
        Assert.Empty(addedLocal);
    }

    [Fact]
    public void EvaluateAddWizardOutcome_DetectsPrinterInstalledOnManagementMachine()
    {
        var outcome = PrinterManagementHelper.EvaluateAddWizardOutcome(
            ["Microsoft Print to PDF"],
            ["Microsoft Print to PDF"],
            ["Fax"],
            ["Fax", "HP LaserJet M404"],
            out var addedTarget,
            out var addedLocal);

        Assert.Equal(PrinterAddWizardOutcome.InstalledOnLocalMachine, outcome);
        Assert.Empty(addedTarget);
        Assert.Equal(["HP LaserJet M404"], addedLocal);
    }

    [Fact]
    public void EvaluateAddWizardOutcome_ReturnsNoChangeWhenNothingWasInstalled()
    {
        var outcome = PrinterManagementHelper.EvaluateAddWizardOutcome(
            ["Microsoft Print to PDF"],
            ["Microsoft Print to PDF"],
            ["Fax"],
            ["  fax  "],
            out var addedTarget,
            out var addedLocal);

        Assert.Equal(PrinterAddWizardOutcome.NoChange, outcome);
        Assert.Empty(addedTarget);
        Assert.Empty(addedLocal);
    }

    [Fact]
    public void EvaluateAddWizardOutcome_IsCaseInsensitiveAndTrimsNames()
    {
        var outcome = PrinterManagementHelper.EvaluateAddWizardOutcome(
            [],
            ["  remoteops test printer  "],
            [],
            [],
            out var addedTarget,
            out var addedLocal);

        Assert.Equal(PrinterAddWizardOutcome.TargetUpdated, outcome);
        Assert.Equal(["remoteops test printer"], addedTarget);
        Assert.Empty(addedLocal);
    }
}