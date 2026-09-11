using System.Text;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services;

namespace RemoteOpsTool.Tests;

public class CleanupExecutionSimulationTests
{
    [Fact]
    public async Task CleanupScript_ExactDirectory_RetainsDirectoryAndRemovesContents()
    {
        var root = CreateIsolatedRoot();
        var target = Path.Combine(root, "Folder with spaces");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        await File.WriteAllTextAsync(Path.Combine(target, "one.tmp"), "test");
        await File.WriteAllTextAsync(Path.Combine(target, "nested", "two.tmp"), "test");

        try
        {
            var result = await RunCleanupScriptAsync(target, deleteDirectory: false);

            Assert.True(result.Success, result.StdErr);
            Assert.True(Directory.Exists(target));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task CleanupScript_DeleteDirectory_RemovesDirectoryItself()
    {
        var root = CreateIsolatedRoot();
        var target = Path.Combine(root, "delete-me");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "one.tmp"), "test");

        try
        {
            var result = await RunCleanupScriptAsync(target, deleteDirectory: true);

            Assert.True(result.Success, result.StdErr);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task CleanupScript_Wildcard_TreatsBracketsLiterally()
    {
        var root = CreateIsolatedRoot();
        var targetDirectory = Path.Combine(root, "literal[1]");
        Directory.CreateDirectory(targetDirectory);
        var matched = Path.Combine(targetDirectory, "matched.tmp");
        var retained = Path.Combine(targetDirectory, "retained.log");
        await File.WriteAllTextAsync(matched, "test");
        await File.WriteAllTextAsync(retained, "test");

        try
        {
            var result = await RunCleanupScriptAsync(Path.Combine(targetDirectory, "*.tmp"), deleteDirectory: false);

            Assert.True(result.Success, result.StdErr);
            Assert.False(File.Exists(matched));
            Assert.True(File.Exists(retained));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    private static async Task<CommandResult> RunCleanupScriptAsync(string path, bool deleteDirectory)
    {
        var script = FileDiskService.BuildCleanupScript(path, deleteDirectory);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await ProcessHelper.RunAsync("powershell.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded });
    }

    private static string CreateIsolatedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteOpsTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteIsolatedRoot(string root)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RemoteOpsTool.Tests"));
        var resolved = Path.GetFullPath(root);
        Assert.StartsWith(expectedParent + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
