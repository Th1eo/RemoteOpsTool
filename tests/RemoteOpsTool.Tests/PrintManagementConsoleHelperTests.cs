using System.Text;
using System.Xml.Linq;
using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Tests;

public sealed class PrintManagementConsoleHelperTests : IDisposable
{
    private const int PrintManagementStreamIndex = 5;
    private const int StoredBinaryCount = 6;

    private const string PrintManagementConfiguration = """
        <?xml version="1.0" encoding="utf-8"?>
        <pmc-configuration xmlns="http://schemas.microsoft.com/2003/print/pmc/config/1.0">
            <print-servers-standalone><server xmlns="" uncname="" servername=""/></print-servers-standalone>
            <miscellaneous-settings>
                <printers-folder-show-extended-view value="0"/>
            </miscellaneous-settings>
        </pmc-configuration>
        """;

    private const string ConsoleFileTemplate = """
        <?xml version="1.0"?>
        <MMC_ConsoleFile ConsoleVersion="3.0" ProgramMode="UserSDI">
          <ConsoleFileID>{11111111-2222-3333-4444-555555555555}</ConsoleFileID>
          <ScopeTree>
            <Nodes>
              <Node ID="2" CLSID="{7C606A3F-8AA8-4E36-92D6-2B6AFEC0B732}">
                <ComponentDatas>
                  <ComponentData>
                    <GUID Name="Snapin">{D06342BD-9057-4673-B43A-0E9BBBE99F11}</GUID>
                    <Stream BinaryRefIndex="__INDEX__"/>
                  </ComponentData>
                </ComponentDatas>
              </Node>
            </Nodes>
          </ScopeTree>
          <BinaryStorage>
        __BINARIES__
          </BinaryStorage>
        </MMC_ConsoleFile>
        """;

    private readonly string _consoleDirectory = Path.Combine(
        Path.GetTempPath(),
        "RemoteOpsTool.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_consoleDirectory))
            Directory.Delete(_consoleDirectory, recursive: true);
    }

    [Fact]
    public void BuildSeededConsoleXml_AddsTargetServerAndKeepsLocalEntry()
    {
        var seeded = PrintManagementConsoleHelper.BuildSeededConsoleXml(BuildConsoleFile(), @"  \\WORKSTATION01  ");

        var configuration = ReadStoredConfiguration(seeded);
        var server = Assert.Single(
            configuration.Descendants(),
            e => e.Name.LocalName == "server" && (string?)e.Attribute("servername") == "WORKSTATION01");

        Assert.Equal(@"\\WORKSTATION01", (string?)server.Attribute("uncname"));
        Assert.Equal(string.Empty, server.Name.NamespaceName);
        Assert.Contains(string.Empty, ReadServerNames(configuration));
    }

    [Fact]
    public void BuildSeededConsoleXml_DoesNotDuplicateServerThatIsAlreadyConfigured()
    {
        var once = PrintManagementConsoleHelper.BuildSeededConsoleXml(BuildConsoleFile(), "WORKSTATION01");
        var twice = PrintManagementConsoleHelper.BuildSeededConsoleXml(once, "workstation01");

        Assert.Equal(1, ReadServerNames(ReadStoredConfiguration(twice)).Count(name => string.Equals(name, "WORKSTATION01", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BuildSeededConsoleXml_ReadsTheStreamReferencedByTheSnapinComponentData()
    {
        var consoleXml = BuildConsoleFile(PrintManagementConfiguration, streamIndex: 2, storedBinaryCount: 4);

        var seeded = PrintManagementConsoleHelper.BuildSeededConsoleXml(consoleXml, "WORKSTATION01");

        var configuration = ReadStoredConfiguration(seeded, streamIndex: 2);
        Assert.Contains("WORKSTATION01", ReadServerNames(configuration));
    }

    [Fact]
    public void BuildSeededConsoleXml_ThrowsWhenTheSnapinConfigurationIsMissing()
    {
        var consoleXml = BuildConsoleFile()
            .Replace("{D06342BD-9057-4673-B43A-0E9BBBE99F11}", "{00000000-0000-0000-0000-000000000000}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => PrintManagementConsoleHelper.BuildSeededConsoleXml(consoleXml, "WORKSTATION01"));

        Assert.Contains("snapin", exception.Message);
    }

    [Fact]
    public void BuildSeededConsoleXml_ThrowsWhenTheReferencedStreamIsOutOfRange()
    {
        var consoleXml = BuildConsoleFile(streamIndex: 9, storedBinaryCount: StoredBinaryCount);

        var exception = Assert.Throws<InvalidOperationException>(
            () => PrintManagementConsoleHelper.BuildSeededConsoleXml(consoleXml, "WORKSTATION01"));

        Assert.Contains("snapin", exception.Message);
    }

    [Fact]
    public void BuildSeededConsoleXml_AssignsADistinctConsoleFileIdPerHost()
    {
        var source = BuildConsoleFile();

        var first = PrintManagementConsoleHelper.BuildSeededConsoleXml(source, "WORKSTATION01");
        var second = PrintManagementConsoleHelper.BuildSeededConsoleXml(source, "NW0805");

        Assert.NotEqual(ReadConsoleFileId(source), ReadConsoleFileId(first));
        Assert.NotEqual(ReadConsoleFileId(first), ReadConsoleFileId(second));
    }

    [Fact]
    public void BuildLaunchPlan_SeedsACachedConsoleFileForTheTargetHost()
    {
        var sourcePath = WriteSourceConsoleFile();

        var plan = PrintManagementConsoleHelper.BuildLaunchPlan("WORKSTATION01", _consoleDirectory, sourcePath);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "mmc.exe"), plan.FileName);
        Assert.True(plan.SeededConsole);
        Assert.Equal("WORKSTATION01", plan.TargetHost);
        Assert.Equal([plan.ConsolePath], plan.Arguments);
        Assert.Equal(_consoleDirectory, Path.GetDirectoryName(plan.ConsolePath));
        Assert.Equal("printmanagement-WORKSTATION01.msc", Path.GetFileName(plan.ConsolePath));
        Assert.True(File.Exists(plan.ConsolePath));
        Assert.StartsWith("<?xml", File.ReadAllText(plan.ConsolePath));

        var configuration = ReadStoredConfiguration(File.ReadAllText(plan.ConsolePath));
        Assert.Contains("WORKSTATION01", ReadServerNames(configuration));
    }

    [Fact]
    public void BuildLaunchPlan_FallsBackToTheStockConsoleWhenTheSourceConsoleIsMissing()
    {
        var missingSource = Path.Combine(_consoleDirectory, "does-not-exist.msc");

        var plan = PrintManagementConsoleHelper.BuildLaunchPlan("WORKSTATION01", _consoleDirectory, missingSource);

        Assert.False(plan.SeededConsole);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "printmanagement.msc"), plan.ConsolePath);
        Assert.Equal([plan.ConsolePath], plan.Arguments);
        Assert.Contains("添加/删除服务器", plan.Note);
    }

    [Fact]
    public void ResolveSourceConsolePath_PrefersTheLocalizedConsole()
    {
        var resolved = PrintManagementConsoleHelper.ResolveSourceConsolePath();

        Assert.NotNull(resolved);
        Assert.EndsWith("printmanagement.msc", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void CreateConsoleFileId_IsStablePerHostAndIgnoresCase()
    {
        Assert.Equal(
            PrintManagementConsoleHelper.CreateConsoleFileId("WORKSTATION01"),
            PrintManagementConsoleHelper.CreateConsoleFileId("workstation01"));
        Assert.NotEqual(
            PrintManagementConsoleHelper.CreateConsoleFileId("WORKSTATION01"),
            PrintManagementConsoleHelper.CreateConsoleFileId("NW0805"));
    }

    [Fact]
    public void SanitizeHostForFileName_ReplacesCharactersThatAreInvalidInFileNames()
    {
        Assert.Equal("fe80__1", PrintManagementConsoleHelper.SanitizeHostForFileName("fe80::1"));
    }

    [Fact]
    public void OpenRemote_RejectsTheLocalMachineInsteadOfOpeningTheConsoleOnIt()
    {
        var result = PrintManagementConsoleHelper.OpenRemote(
            Environment.MachineName, "CONTOSO\\operator", "password");

        Assert.False(result.Success);
        Assert.Contains("本机", result.Message);
    }

    [Fact]
    public void OpenRemote_RejectsAnEmptyCredentialWithoutLaunchingAnything()
    {
        var result = PrintManagementConsoleHelper.OpenRemote("WORKSTATION01", " ", "password");

        Assert.False(result.Success);
        Assert.Contains("用户名", result.Message);
    }

    private string WriteSourceConsoleFile()
    {
        Directory.CreateDirectory(_consoleDirectory);
        var sourcePath = Path.Combine(_consoleDirectory, "printmanagement.msc");
        File.WriteAllText(sourcePath, BuildConsoleFile());
        return sourcePath;
    }

    private static string BuildConsoleFile(
        string? configuration = null,
        int streamIndex = PrintManagementStreamIndex,
        int storedBinaryCount = StoredBinaryCount)
    {
        configuration ??= PrintManagementConfiguration;
        var binaries = new StringBuilder();
        for (var index = 0; index < storedBinaryCount; index++)
        {
            var content = index == streamIndex
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(configuration))
                : Convert.ToBase64String(Encoding.UTF8.GetBytes($"binary-{index}"));
            binaries.AppendLine($"    <Binary Name=\"Binary{index}\">{content}</Binary>");
        }

        return ConsoleFileTemplate
            .Replace("__INDEX__", streamIndex.ToString(), StringComparison.Ordinal)
            .Replace("__BINARIES__", binaries.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    private static XDocument ReadStoredConfiguration(string consoleXml, int streamIndex = PrintManagementStreamIndex)
    {
        var console = XDocument.Parse(consoleXml);
        var storage = console.Root!
            .Descendants()
            .First(e => e.Name.LocalName == "BinaryStorage");
        var stored = storage.Elements().ElementAt(streamIndex).Value;
        var base64 = new string(stored.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
    }

    private static IEnumerable<string> ReadServerNames(XDocument configuration) =>
        configuration.Descendants()
            .Where(e => e.Name.LocalName == "server")
            .Select(e => (string?)e.Attribute("servername") ?? string.Empty);

    private static string ReadConsoleFileId(string consoleXml) =>
        XDocument.Parse(consoleXml)
            .Descendants()
            .First(e => e.Name.LocalName == "ConsoleFileID")
            .Value;
}
