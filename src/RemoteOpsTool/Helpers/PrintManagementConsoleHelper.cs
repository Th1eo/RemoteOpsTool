using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RemoteOpsTool.Helpers;

/// <summary>
/// Opens the management machine's own Print Management console
/// (printmanagement.msc) already connected to the target print server.
///
/// Print Management persists its server list inside the console file: the snap-in
/// stores a UTF-8 "pmc-configuration" document in a base64 stream under its
/// ComponentData. Seeding that stream with a "print-servers-standalone" entry is
/// how this helper pre-selects the target host; the console has no supported
/// "/server:" command-line switch.
///
/// The console runs on the operator's desktop through a network-only logon
/// (the runas /netonly equivalent), so no process is created on the target host
/// and the window never appears on the target user's desktop.
/// </summary>
public static class PrintManagementConsoleHelper
{
    internal const string PrintManagementSnapinClsid = "{D06342BD-9057-4673-B43A-0E9BBBE99F11}";
    internal const string ConsoleFileName = "printmanagement.msc";
    internal const string SeededConsoleFilePrefix = "printmanagement-";
    internal const string PrintServersElementName = "print-servers-standalone";

    private const int Base64LineLength = 76;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static PrintManagementConsoleLaunchResult OpenRemote(
        string targetHost,
        string username,
        string password)
    {
        if (HostHelper.IsLocalHost(targetHost))
            return PrintManagementConsoleLaunchResult.Failed("目标主机为本机，请直接在本机打开打印管理控制台。");

        if (string.IsNullOrWhiteSpace(username))
            return PrintManagementConsoleLaunchResult.Failed("未提供用于远程连接的用户名。");

        var plan = BuildLaunchPlan(targetHost);
        var result = ProcessHelper.StartWithNetworkCredentials(
            plan.FileName,
            plan.Arguments,
            username,
            password,
            Environment.SystemDirectory);

        if (!result.Success)
            return PrintManagementConsoleLaunchResult.Failed(result.StdErr);

        return new PrintManagementConsoleLaunchResult(true, plan.SeededConsole, plan.ConsolePath, plan.Note);
    }

    internal static PrintManagementConsoleLaunchPlan BuildLaunchPlan(
        string targetHost,
        string? consoleDirectory = null,
        string? sourceConsolePath = null)
    {
        var normalizedHost = HostHelper.NormalizeHost(targetHost);
        var mmcPath = Path.Combine(Environment.SystemDirectory, "mmc.exe");
        var stockConsolePath = Path.Combine(Environment.SystemDirectory, ConsoleFileName);

        var resolvedSource = sourceConsolePath ?? ResolveSourceConsolePath();
        if (resolvedSource is null)
        {
            return new PrintManagementConsoleLaunchPlan(
                mmcPath,
                [stockConsolePath],
                normalizedHost,
                stockConsolePath,
                false,
                BuildFallbackNote(normalizedHost, "本机未安装 printmanagement.msc"));
        }

        try
        {
            var seededConsolePath = CreateSeededConsoleFile(resolvedSource, normalizedHost, consoleDirectory);
            return new PrintManagementConsoleLaunchPlan(
                mmcPath,
                [seededConsolePath],
                normalizedHost,
                seededConsolePath,
                true,
                $"已在本机控制台中预置目标打印服务器（控制台: {seededConsolePath}）。");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new PrintManagementConsoleLaunchPlan(
                mmcPath,
                [stockConsolePath],
                normalizedHost,
                stockConsolePath,
                false,
                BuildFallbackNote(normalizedHost, ex.Message));
        }
    }

    /// <summary>
    /// Builds a console file whose Print Management snap-in already lists
    /// <paramref name="targetHost"/> as a print server.
    /// </summary>
    internal static string BuildSeededConsoleXml(string sourceConsoleXml, string targetHost)
    {
        var normalizedHost = HostHelper.NormalizeHost(targetHost);
        if (string.IsNullOrWhiteSpace(normalizedHost) || normalizedHost == ".")
            throw new InvalidOperationException("目标主机不能为空。");

        var console = XDocument.Parse(sourceConsoleXml, LoadOptions.PreserveWhitespace);
        var root = console.Root ?? throw new InvalidOperationException("打印管理控制台文件为空。");

        var storage = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "BinaryStorage")
            ?? throw new InvalidOperationException("打印管理控制台缺少二进制存储节点。");
        var storedBinaries = storage.Elements().ToList();

        var configurationStream = root.Descendants()
            .Where(e => e.Name.LocalName == "ComponentData")
            .Select(componentData => new
            {
                Snapin = componentData.Elements().FirstOrDefault(e => e.Name.LocalName == "GUID")?.Value,
                Stream = componentData.Elements().FirstOrDefault(e => e.Name.LocalName == "Stream")
            })
            .FirstOrDefault(x => string.Equals(x.Snapin, PrintManagementSnapinClsid, StringComparison.OrdinalIgnoreCase))
            ?.Stream;

        if (configurationStream is null ||
            !int.TryParse(
                configurationStream.Attribute("BinaryRefIndex")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var binaryIndex) ||
            binaryIndex < 0 ||
            binaryIndex >= storedBinaries.Count)
        {
            throw new InvalidOperationException("打印管理控制台缺少打印机管理 snapin 的配置流。");
        }

        var configurationXml = Encoding.UTF8.GetString(
            Convert.FromBase64String(RemoveWhitespace(storedBinaries[binaryIndex].Value)));
        var configuration = XDocument.Parse(configurationXml);

        var serverList = configuration.Descendants()
                              .FirstOrDefault(e => e.Name.LocalName == PrintServersElementName)
                          ?? throw new InvalidOperationException("打印管理配置缺少服务器列表节点。");

        var alreadySeeded = serverList.Elements()
            .Any(e => e.Name.LocalName == "server" &&
                      string.Equals((string?)e.Attribute("servername"), normalizedHost, StringComparison.OrdinalIgnoreCase));

        if (!alreadySeeded)
        {
            // The snap-in stores its servers as namespace-less elements so the
            // element is created without a namespace even though the document
            // declares a default one.
            serverList.Add(new XElement(
                XName.Get("server", string.Empty),
                new XAttribute("uncname", $@"\\{normalizedHost}"),
                new XAttribute("servername", normalizedHost)));
        }

        storedBinaries[binaryIndex].Value = WrapBase64(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(configuration, indent: true))));

        var consoleFileId = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ConsoleFileID");
        if (consoleFileId is not null)
            consoleFileId.Value = CreateConsoleFileId(normalizedHost);

        return Serialize(console, indent: false);
    }

    internal static string CreateSeededConsoleFile(
        string sourceConsolePath,
        string targetHost,
        string? consoleDirectory = null)
    {
        var consoleXml = BuildSeededConsoleXml(File.ReadAllText(sourceConsolePath), targetHost);
        var directory = string.IsNullOrWhiteSpace(consoleDirectory)
            ? GetDefaultConsoleDirectory()
            : Path.GetFullPath(consoleDirectory);
        Directory.CreateDirectory(directory);

        var hostSegment = SanitizeHostForFileName(HostHelper.NormalizeHost(targetHost));
        var path = Path.Combine(directory, $"{SeededConsoleFilePrefix}{hostSegment}.msc");
        try
        {
            File.WriteAllText(path, consoleXml, Utf8NoBom);
        }
        catch (IOException)
        {
            // The console for this host may already be open in MMC and therefore
            // hold the file; use a unique name instead of failing the launch.
            path = Path.Combine(
                directory,
                $"{SeededConsoleFilePrefix}{hostSegment}-{DateTime.Now:yyyyMMddHHmmssfff}.msc");
            File.WriteAllText(path, consoleXml, Utf8NoBom);
        }

        return path;
    }

    internal static string GetDefaultConsoleDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteOpsTool",
            "Consoles");

    internal static string? ResolveSourceConsolePath()
    {
        foreach (var candidate in EnumerateSourceConsolePaths())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal static string CreateConsoleFileId(string targetHost)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(targetHost.ToLowerInvariant()));
        var identifier = new Guid(hash.AsSpan(0, 16).ToArray());
        return "{" + identifier.ToString().ToUpperInvariant() + "}";
    }

    internal static string SanitizeHostForFileName(string targetHost)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(targetHost.Length);
        foreach (var character in targetHost)
            builder.Append(invalid.Contains(character) ? '_' : character);

        return builder.Length == 0 ? "console" : builder.ToString();
    }

    private static IEnumerable<string> EnumerateSourceConsolePaths()
    {
        var systemDirectory = Environment.SystemDirectory;
        var culture = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrWhiteSpace(culture.Name))
            yield return Path.Combine(systemDirectory, culture.Name, ConsoleFileName);
        if (!string.IsNullOrWhiteSpace(culture.TwoLetterISOLanguageName))
            yield return Path.Combine(systemDirectory, culture.TwoLetterISOLanguageName, ConsoleFileName);
        yield return Path.Combine(systemDirectory, ConsoleFileName);
    }

    private static string Serialize(XDocument document, bool indent)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = indent,
            IndentChars = "\t",
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\r\n",
            Encoding = Utf8NoBom
        };

        var stringWriter = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            document.Save(writer);
        }

        return stringWriter.ToString();
    }

    private static string RemoveWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    private static string WrapBase64(string base64)
    {
        var builder = new StringBuilder(base64.Length + (base64.Length / Base64LineLength * 2));
        for (var offset = 0; offset < base64.Length; offset += Base64LineLength)
        {
            if (offset > 0)
                builder.Append("\r\n");
            builder.Append(base64, offset, Math.Min(Base64LineLength, base64.Length - offset));
        }

        return builder.ToString();
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or XmlException
            or FormatException
            or NotSupportedException;

    private static string BuildFallbackNote(string normalizedHost, string reason) =>
        $"已回退到原始打印管理控制台（{reason}）；请在“打印管理 → 添加/删除服务器”中手动添加 {normalizedHost}。";

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Utf8NoBom;
    }
}

internal sealed record PrintManagementConsoleLaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string TargetHost,
    string ConsolePath,
    bool SeededConsole,
    string Note);

public sealed record PrintManagementConsoleLaunchResult(
    bool Success,
    bool SeededConsole,
    string ConsolePath,
    string Message)
{
    public static PrintManagementConsoleLaunchResult Failed(string message) =>
        new(false, false, string.Empty, message);
}
