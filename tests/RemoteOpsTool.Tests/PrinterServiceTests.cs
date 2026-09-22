using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class PrinterServiceTests
{
    [Fact]
    public void BuildClearDefaultPrinterCommand_UsesSessionComWithoutDynamicCompilation()
    {
        var command = PrinterService.BuildClearDefaultPrinterCommand();

        Assert.Contains("WScript.Network", command);
        Assert.Contains("SetDefaultPrinter('')", command);
        Assert.DoesNotContain("Add-Type", command);
        Assert.DoesNotContain("DllImport", command);
        Assert.True(command.Length <= 700);
    }
}