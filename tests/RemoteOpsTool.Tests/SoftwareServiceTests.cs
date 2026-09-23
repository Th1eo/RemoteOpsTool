using System.Text;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class SoftwareServiceTests
{
    [Fact]
    public void BuildSoftwareRegistryPsCommand_UsesEncodedCommandAndAllRegistryRoots()
    {
        var command = SoftwareService.BuildSoftwareRegistryPsCommand();

        Assert.StartsWith("powershell.exe ", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-EncodedCommand ", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Where-Object", command, StringComparison.Ordinal);
        Assert.DoesNotContain("$_.DisplayName", command, StringComparison.Ordinal);

        var encoded = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        foreach (var registryRoot in AppConstants.SoftwareRegistryKeys)
        {
            Assert.Contains(registryRoot, script, StringComparison.Ordinal);
        }

        Assert.Contains("__REMOTEOPS_SOFTWARE_CSV_BEGIN__", script, StringComparison.Ordinal);
        Assert.Contains("__REMOTEOPS_SOFTWARE_CSV_END__", script, StringComparison.Ordinal);
        Assert.Contains("RegistryRoot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSoftwareInventoryFromPsExec_ParsesRowsAndQualifiedRegistryKey()
    {
        const string csv =
            "\"DisplayName\",\"UninstallString\",\"QuietUninstallString\",\"Publisher\",\"InstallLocation\",\"PSChildName\",\"RegistryRoot\"\r\n" +
            "\"7-Zip\",\"C:\\Program Files\\7-Zip\\Uninstall.exe\",\"\",\"Igor Pavlov\",\"C:\\Program Files\\7-Zip\",\"7-Zip\",\"HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\"";
        var result = new CommandResult(
            0,
            "__REMOTEOPS_SOFTWARE_CSV_BEGIN__\r\n" + csv + "\r\n__REMOTEOPS_SOFTWARE_CSV_END__",
            string.Empty);

        var software = SoftwareService.ParseSoftwareInventoryFromPsExec(result, "TESTHOST");

        var item = Assert.Single(software);
        Assert.Equal("7-Zip", item.DisplayName);
        Assert.Equal("Igor Pavlov", item.Publisher);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip", item.RegistryKey);
    }

    [Fact]
    public void ParseSoftwareInventoryFromPsExec_ThrowsForNonZeroExit()
    {
        var result = new CommandResult(1, string.Empty, "Access is denied");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SoftwareService.ParseSoftwareInventoryFromPsExec(result, "TESTHOST"));

        Assert.Contains("获取软件清单失败", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Access is denied", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSoftwareInventoryFromPsExec_ThrowsWhenCompletionMarkersAreMissing()
    {
        var result = new CommandResult(0, "unexpected partial output", string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SoftwareService.ParseSoftwareInventoryFromPsExec(result, "TESTHOST"));

        Assert.Contains("未返回软件清单完成标记", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSoftwareInventoryFromPsExec_ThrowsWhenBeginMarkerHasNoEndMarker()
    {
        var result = new CommandResult(
            0,
            "__REMOTEOPS_SOFTWARE_CSV_BEGIN__\r\n" +
            "\"DisplayName\",\"UninstallString\",\"QuietUninstallString\",\"Publisher\",\"InstallLocation\",\"PSChildName\",\"RegistryRoot\"\r\n" +
            "\"7-Zip\",\"C:\\Program Files\\7-Zip\\Uninstall.exe\",\"\",\"Igor Pavlov\",\"C:\\Program Files\\7-Zip\",\"7-Zip\",\"HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\"",
            string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SoftwareService.ParseSoftwareInventoryFromPsExec(result, "TESTHOST"));

        Assert.Contains("未返回软件清单完成标记", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSoftwareInventoryFromPsExec_ReturnsEmptyListForExplicitEmptyInventory()
    {
        var result = new CommandResult(
            0,
            "__REMOTEOPS_SOFTWARE_CSV_BEGIN__\r\n__REMOTEOPS_SOFTWARE_CSV_END__",
            string.Empty);

        var software = SoftwareService.ParseSoftwareInventoryFromPsExec(result, "TESTHOST");

        Assert.Empty(software);
    }
}
