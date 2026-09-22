using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Tests;

public class RemoteExecutionOutputNormalizerTests
{
    private static readonly string[] PsExecBanner =
    [
        "Connecting to WORKSTATION01...",
        "",
        "",
        "Starting PSEXESVC service on WORKSTATION01...",
        "",
        "",
        "Copying authentication key to WORKSTATION01...",
        "",
        "",
        "Connecting with PsExec service on WORKSTATION01...",
        "",
        "",
        "Starting cmd on WORKSTATION01...",
        "",
        "",
        "",
    ];

    private static (List<string> Lines, List<string> Suppressed) NormalizeStream(
        IEnumerable<string> raw, bool filterPsExecProtocol = true)
    {
        var lines = new List<string>();
        var suppressed = new List<string>();
        var normalizer = new RemoteExecutionOutputNormalizer(
            lines.Add, suppressed.Add, filterPsExecProtocol);

        foreach (var line in raw)
            normalizer.Write(line);

        normalizer.Flush();
        return (lines, suppressed);
    }

    private static string Clixml(params string[] records)
    {
        var body = string.Concat(records);
        return $"#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">{body}</Objs>";
    }

    [Fact]
    public void Streaming_DropsPsExecHandshakeBannerAndLeadingBlankLines()
    {
        var (lines, suppressed) = NormalizeStream([.. PsExecBanner, "Windows IP Configuration", ""]);

        Assert.Equal(["Windows IP Configuration", ""], lines);
        Assert.Equal(PsExecBanner, suppressed);
    }

    [Fact]
    public void Streaming_KeepsBlankLinesThatAppearAfterRealOutput()
    {
        var (lines, _) = NormalizeStream(["", "Connecting to WORKSTATION01...", "", "first", "", "second"]);

        Assert.Equal(["first", "", "second"], lines);
    }

    [Fact]
    public void Streaming_DropsBannerOnlyFromTheLeadingPreamble()
    {
        // A command may legitimately print a line that looks like the banner.
        // Only the leading preamble is treated as PsExec protocol noise.
        var (lines, _) = NormalizeStream(["real output", "Connecting to WORKSTATION01..."]);

        Assert.Equal(["real output", "Connecting to WORKSTATION01..."], lines);
    }

    [Fact]
    public void Streaming_DecodesClixmlErrorRecordsIntoReadableText()
    {
        var clixml = Clixml(
            "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparing modules for first use.</AV></MS></Obj>",
            "<S S=\"Error\">Get-Item : Cannot find drive. A drive with the name 'Z' does not exist._x000D__x000A_</S>",
            "<S S=\"Error\">At line:1 char:1_x000D__x000A_</S>");

        var (lines, _) = NormalizeStream(clixml.Split("\r\n"));

        Assert.Equal(
            [
                "Get-Item : Cannot find drive. A drive with the name 'Z' does not exist.",
                "At line:1 char:1",
            ],
            lines);
        Assert.DoesNotContain(lines, line => line.Contains("CLIXML", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("progress", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Streaming_ClixmlWithOnlyProgressRecordsEmitsNothing()
    {
        var clixml = Clixml(
            "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparing modules for first use.</AV></MS></Obj>");

        var (lines, _) = NormalizeStream(clixml.Split("\r\n"));

        Assert.Empty(lines);
    }

    [Fact]
    public void Streaming_DecodesControlCharacterEscapes()
    {
        var clixml = Clixml("<S S=\"Warning\">line1_x000D__x000A_line2_x000D__x000A_</S>");

        var (lines, _) = NormalizeStream(clixml.Split("\r\n"));

        Assert.Equal(["line1", "line2"], lines);
    }

    [Fact]
    public void Streaming_MalformedClixmlFallsBackToRawText()
    {
        var raw = new[] { "#< CLIXML", "<Objs><S S=\"Error\">truncated" };

        var (lines, _) = NormalizeStream(raw);

        Assert.Equal(["#< CLIXML<Objs><S S=\"Error\">truncated"], lines);
    }

    [Fact]
    public void Streaming_EmitsClixmlContentThatFollowsTheClosingTag()
    {
        var clixml = Clixml("<S S=\"Error\">boom_x000D__x000A_</S>") + "tail";

        var (lines, _) = NormalizeStream(clixml.Split("\r\n"));

        Assert.Equal(["boom", "tail"], lines);
    }

    [Fact]
    public void Streaming_DecodesTheExactGetNetAdapterFailureShapeWithoutBannerNoise()
    {
        string[] raw =
        [
            "Connecting to client01...",
            "",
            "",
            "Starting PSEXESVC service on client01...",
            "",
            "",
            "Copying authentication key to client01...",
            "",
            "",
            "Connecting with PsExec service on client01...",
            "",
            "",
            "Starting powershell.exe on client01...",
            "",
            "",
            "#< CLIXML",
            "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparing modules for first use.</AV></MS></Obj>" +
            "<S S=\"Error\">Get-NetAdapter : 拒绝访问。_x000D__x000A_</S>" +
            "<S S=\"Error\">所在位置 行:1 字符: 1_x000D__x000A_</S></Objs>",
            "powershell.exe exited on client01 with error code 0.",
        ];

        var (lines, _) = NormalizeStream(raw);

        Assert.Equal(
            [
                "Get-NetAdapter : 拒绝访问。",
                "所在位置 行:1 字符: 1",
                "powershell.exe exited on client01 with error code 0.",
            ],
            lines);
    }

    [Fact]
    public void NormalizeCompletedResult_KeepsTransportFailureDiagnosticsClassifiable()
    {
        var result = new CommandResult(
            1,
            string.Empty,
            "Connecting to WORKSTATION01...\r\n" +
            "Starting PSEXESVC service on WORKSTATION01...\r\n" +
            "Copying authentication key to WORKSTATION01...\r\n" +
            "Connecting with PsExec service on WORKSTATION01...\r\n" +
            "Error communicating with PsExec service on WORKSTATION01:\r\n" +
            "The handle is invalid.\r\n");

        var normalized = RemoteExecutionOutputNormalizer.NormalizeCompletedResult(
            result, filterPsExecProtocol: true);

        Assert.DoesNotContain("Connecting to WORKSTATION01...", normalized.StdErr, StringComparison.Ordinal);
        Assert.Contains("Error communicating with PsExec service on WORKSTATION01:", normalized.StdErr, StringComparison.Ordinal);
        Assert.Contains("The handle is invalid.", normalized.StdErr, StringComparison.Ordinal);
        Assert.True(TransportFailureClassifier.IsPsExecServiceStartDenied(normalized));
        Assert.True(TransportFailureClassifier.IsPsExecTransportFailure(normalized));
    }

    [Fact]
    public void NormalizeCompletedResult_KeepsServiceStartDeniedDiagnostics()
    {
        var result = new CommandResult(
            1,
            string.Empty,
            "Connecting to WORKSTATION01...\r\n" +
            "Could not start PSEXESVC service on WORKSTATION01:\r\n" +
            "Access is denied.\r\n");

        var normalized = RemoteExecutionOutputNormalizer.NormalizeCompletedResult(
            result, filterPsExecProtocol: true);

        Assert.True(TransportFailureClassifier.IsPsExecServiceStartDenied(normalized));
    }

    [Fact]
    public void NormalizeCompletedResult_PreservesDetachedLaunchProcessIdLine()
    {
        var result = new CommandResult(
            1,
            string.Empty,
            "Connecting to WORKSTATION01...\r\n" +
            "Starting control.exe on WORKSTATION01...\r\n" +
            "cmd started on WORKSTATION01 with process ID 4242.\r\n");

        var normalized = RemoteExecutionOutputNormalizer.NormalizeCompletedResult(
            result, filterPsExecProtocol: true);

        Assert.Contains("process ID 4242", normalized.StdErr, StringComparison.Ordinal);
        Assert.Equal(0, TransportFailureClassifier.NormalizeDetachedLaunchResult(normalized).ExitCode);
    }

    [Fact]
    public async Task RealPowerShellErrorStream_IsStreamedAsReadableText()
    {
        // End-to-end guard for the reported defect: a redirected PowerShell error
        // stream arrives as CLIXML on stderr, and the log area must show the error
        // text instead of "#< CLIXML" plus raw XML.
        var missing = $"RemoteOpsTool-normalizer-{Guid.NewGuid():N}";
        var script = $"Get-Item -LiteralPath 'C:\\{missing}' -ErrorAction Stop";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var lines = new List<string>();
        var normalizer = new RemoteExecutionOutputNormalizer(lines.Add);

        var result = await ProcessHelper.RunWithOutputAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            normalizer.Write);
        normalizer.Flush();

        Assert.False(result.Success);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, line => line.Contains(missing, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, line => line.Contains("CLIXML", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("<Objs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealMergedBannerAndClixmlStream_ProducesOnlyReadableCommandOutput()
    {
        // Mirrors the reported log: PsExec handshake banner and blank lines share
        // the stderr stream with the remote PowerShell command, whose redirected
        // error stream is serialized as CLIXML.
        var missing = $"RemoteOpsTool-merged-{Guid.NewGuid():N}";
        var script = $"Get-Item -LiteralPath 'C:\\{missing}' -ErrorAction Stop";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var commandLine =
            "echo Connecting to lab01... 1>&2 & " +
            "echo. 1>&2 & " +
            "echo Starting PSEXESVC service on lab01... 1>&2 & " +
            "echo. 1>&2 & " +
            "echo Starting powershell.exe on lab01... 1>&2 & " +
            $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}";

        var lines = new List<string>();
        var normalizer = new RemoteExecutionOutputNormalizer(lines.Add);

        var result = await ProcessHelper.RunWithOutputAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/c", commandLine],
            normalizer.Write);
        normalizer.Flush();

        Assert.False(result.Success);
        Assert.Contains(lines, line => line.Contains(missing, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, line => line.Contains("CLIXML", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("<Objs", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("Connecting to", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, line => line.StartsWith("Starting ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void NormalizeCompletedText_IsIdempotent()
    {
        var result = new CommandResult(
            1,
            string.Empty,
            "Connecting to WORKSTATION01...\r\n" +
            "Starting PSEXESVC service on WORKSTATION01...\r\n" +
            "first\r\n\r\nsecond\r\n");

        var once = RemoteExecutionOutputNormalizer.NormalizeCompletedResult(
            result, filterPsExecProtocol: true);
        var twice = RemoteExecutionOutputNormalizer.NormalizeCompletedResult(
            once, filterPsExecProtocol: false);

        Assert.Equal(once.StdErr, twice.StdErr);
    }

    [Fact]
    public async Task Streaming_IsSafeWhenStdoutAndStderrCallbacksArriveConcurrently()
    {
        var lines = new List<string>();
        var normalizer = new RemoteExecutionOutputNormalizer(lines.Add);
        var first = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                normalizer.Write($"stdout-{i}");
        });
        var second = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                normalizer.Write($"stderr-{i}");
        });

        await Task.WhenAll(first, second);
        normalizer.Flush();

        Assert.Equal(1000, lines.Count);
        Assert.Equal(1000, lines.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NormalizeCompletedText_OutputMatchesStreamedLines()
    {
        var raw = new[]
        {
            "Connecting to WORKSTATION01...",
            "",
            "Starting cmd on WORKSTATION01...",
            "",
            "Windows IP Configuration",
            "",
            "   Host Name . . . . . . . . : WORKSTATION01",
            "",
        };

        var (streamed, _) = NormalizeStream(raw);
        var completed = RemoteExecutionOutputNormalizer.NormalizeCompletedText(
            string.Join(Environment.NewLine, raw), filterPsExecProtocol: true);

        var replayed = completed
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        Assert.Equal(
            streamed.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray(),
            replayed);
    }
}
