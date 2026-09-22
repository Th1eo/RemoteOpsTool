using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class CleanupScriptTests
{
    [Fact]
    public void CleanupScript_UsesLiteralPathAndVerifiesExactFileDeletion()
    {
        var script = FileDiskService.BuildCleanupScript(@"C:\Temp\file name.log", false);

        Assert.Contains("$targetPath = 'C:\\Temp\\file name.log'", script);
        Assert.Contains("Remove-Item -LiteralPath $targetPath -Force", script);
        Assert.Contains("if (Test-Path -LiteralPath $targetPath) { throw '文件删除后仍然存在' }", script);
    }

    [Fact]
    public void CleanupScript_EscapesSingleQuotesInPaths()
    {
        var script = FileDiskService.BuildCleanupScript(@"C:\Temp\O'Brien.log", false);

        Assert.Contains("$targetPath = 'C:\\Temp\\O''Brien.log'", script);
    }

    [Fact]
    public void CleanupScript_SupportsOrdinaryWildcardsAndTreatsBracketsLiterally()
    {
        var script = FileDiskService.BuildCleanupScript(@"C:\Temp\[cache]\*.tmp", false);

        Assert.Contains("[System.Management.Automation.WildcardPattern]::Escape($targetPath)", script);
        Assert.Contains(".Replace('`*', '*').Replace('`?', '?')", script);
        Assert.Contains("Get-Item -Path $wildcardPath", script);
    }

    [Fact]
    public void CleanupScript_RemovesWindowsCopyAsPathOuterQuotes()
    {
        var path = @"C:\Temp\file name.log";
        var script = FileDiskService.BuildCleanupScript($"\"{path}\"", false);

        Assert.Contains($"$targetPath = '{path}'", script);
    }

    [Fact]
    public void CleanupScript_DeleteDirectoryMode_RemovesAndVerifiesDirectory()
    {
        var script = FileDiskService.BuildCleanupScript(@"C:\Temp\Old Folder", true);

        Assert.Contains("$deleteDirectory = $true", script);
        Assert.Contains("Remove-Item -LiteralPath $target.FullName -Force -Recurse", script);
        Assert.Contains("if (Test-Path -LiteralPath $targetPath) { throw '目录删除后仍然存在' }", script);
        Assert.Contains("禁止删除磁盘根目录或共享根目录", script);
    }
}
