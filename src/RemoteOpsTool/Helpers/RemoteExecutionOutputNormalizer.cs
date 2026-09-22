using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RemoteOpsTool.Helpers;

/// <summary>
/// 在远程执行输出进入日志区或进入失败分类之前做一次统一归一化，解决两类噪音：
///
/// 1. PsExec 控制台协议噪音：PsExec 会把握手横幅（Connecting to HOST...、
///    Starting PSEXESVC service on HOST... 等）以及夹杂在横幅之间的空行写到
///    与远端命令相同的 stderr 流里，这些内容不是命令输出。
/// 2. PowerShell CLIXML：只要重定向 PowerShell 的错误流，PowerShell 就会把错误、
///    警告、进度记录序列化成 CLIXML 文档写到 stderr。日志区应该显示可读的错误
///    文本，而不是 "#&lt; CLIXML" 和原始 XML。
///
/// 归一化对“流式回调”和“最终 CommandResult”使用同一套规则，保证
/// RemoteExecutionService 的输出去重（ReplayUnseenOutput）既不会重复也不会丢行。
/// </summary>
internal sealed class RemoteExecutionOutputNormalizer
{
    private const string ClixmlMarker = "#< CLIXML";
    private const string ClixmlClosingTag = "</Objs>";

    /// <summary>
    /// PsExec 握手横幅行。全部要求以省略号结尾，避免误删真实命令输出；
    /// 远端命令自身的失败行（如 "Could not start PSEXESVC service on HOST:"）
    /// 不匹配这些模式，因此传输失败分类仍然拿得到原始诊断。
    /// </summary>
    private static readonly Regex PsExecBannerRegex = new(
        "^(?:" +
        "Connecting to .+" +
        "|Starting PSEXESVC service on .+" +
        "|Copying authentication key to .+" +
        "|Connecting with PsExec service on .+" +
        "|Starting .+ on .+" +
        ")\\.\\.\\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClixmlEscapeRegex = new(
        "_x([0-9A-Fa-f]{4})_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Action<string> _emit;
    private readonly Action<string>? _onSuppressed;
    private readonly bool _filterPsExecProtocol;
    private readonly object _sync = new();
    private readonly StringBuilder _clixmlBuffer = new();
    private bool _capturingClixml;
    private bool _contentStarted;
    private int _suppressedCount;

    /// <summary>Number of protocol/blank lines suppressed during this execution.</summary>
    public int SuppressedCount => Volatile.Read(ref _suppressedCount);

    public RemoteExecutionOutputNormalizer(
        Action<string> emit,
        Action<string>? onSuppressed = null,
        bool filterPsExecProtocol = true)
    {
        _emit = emit;
        _onSuppressed = onSuppressed;
        _filterPsExecProtocol = filterPsExecProtocol;
    }

    /// <summary>写入一行原始输出（通常直接来自 ProcessHelper 的流式读取）。</summary>
    public void Write(string? line)
    {
        // ProcessHelper reads stdout and stderr on separate tasks. PsExec can emit
        // its banner on one stream while the command response arrives on the other,
        // so the stateful normalizer must serialize callbacks.
        lock (_sync)
        {
            WriteCore(line);
        }
    }

    private void WriteCore(string? line)
    {
        line ??= string.Empty;

        // CLIXML 一旦开始就吃掉后续所有行，直到 </Objs> 结束；期间的行不能再按
        // PsExec 协议噪音判断。
        if (_capturingClixml)
        {
            _clixmlBuffer.Append(line);
            TryCompleteClixml();
            return;
        }

        var markerIndex = line.IndexOf(ClixmlMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            if (markerIndex > 0)
                AcceptLine(line[..markerIndex]);

            _clixmlBuffer.Clear();
            _clixmlBuffer.Append(line[(markerIndex + ClixmlMarker.Length)..]);
            _capturingClixml = true;
            TryCompleteClixml();
            return;
        }

        AcceptLine(line);
    }

    /// <summary>进程结束时调用；只用于兜底输出未闭合的 CLIXML 内容。</summary>
    public void Flush()
    {
        lock (_sync)
        {
            if (!_capturingClixml)
                return;

            var pending = _clixmlBuffer.ToString();
            _capturingClixml = false;
            _clixmlBuffer.Clear();

            if (string.IsNullOrWhiteSpace(pending))
                return;

            // PowerShell 可能只输出标记，随后紧跟 PsExec 的退出信息，却没有真正
            // 的 CLIXML 文档。此时保留可读正文，但不要重新拼回 marker。
            if (!pending.Contains("<Objs", StringComparison.Ordinal))
            {
                AcceptLine(pending);
                return;
            }

            // 文档未正常闭合（例如远端进程被强杀）时保留原文，宁可输出原始内容
            // 也不要静默丢弃错误信息。
            AcceptLine(ClixmlMarker + pending);
        }
    }

    private void AcceptLine(string line)
    {
        if (_filterPsExecProtocol && !_contentStarted &&
            (string.IsNullOrWhiteSpace(line) || PsExecBannerRegex.IsMatch(line.Trim())))
        {
            // PsExec 的握手横幅和紧随其后的空行都出现在真实输出之前，
            // 只保留到调试日志（onSuppressed）里，避免刷屏。
            Interlocked.Increment(ref _suppressedCount);
            _onSuppressed?.Invoke(line);
            return;
        }

        if (!string.IsNullOrWhiteSpace(line))
            _contentStarted = true;

        _emit(line);
    }

    private void TryCompleteClixml()
    {
        if (_clixmlBuffer.Length == 0)
            return;

        var text = _clixmlBuffer.ToString();
        var closingIndex = text.IndexOf(ClixmlClosingTag, StringComparison.Ordinal);
        if (closingIndex < 0)
            return;

        var documentText = text[..(closingIndex + ClixmlClosingTag.Length)];
        var remainder = text[(closingIndex + ClixmlClosingTag.Length)..];

        _capturingClixml = false;
        _clixmlBuffer.Clear();

        if (TryDecodeClixml(documentText, out var decodedLines))
        {
            foreach (var decoded in decodedLines)
                AcceptLine(decoded);
        }
        else
        {
            // XML 非法（例如被截断）：回退为原始文本，保证不丢日志。
            AcceptLine(ClixmlMarker + documentText);
        }

        if (remainder.Length > 0)
            AcceptLine(remainder);
    }

    private static bool TryDecodeClixml(string documentText, out List<string> lines)
    {
        lines = [];
        try
        {
            var document = XDocument.Parse(documentText, LoadOptions.PreserveWhitespace);
            foreach (var element in document.Descendants())
            {
                if (!element.Name.LocalName.Equals("S", StringComparison.Ordinal))
                    continue;

                var stream = element.Attribute("S")?.Value;
                // 进度记录（Preparing modules for first use. 等）是 PowerShell 自身
                // 的渲染进度，对运维人员没有价值，直接丢弃。
                if (string.IsNullOrEmpty(stream) ||
                    stream.Equals("progress", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var text in SplitDecodedText(DecodeClixmlEscapes(element.Value)))
                {
                    // PowerShell terminates every record with CRLF, and the last
                    // record is often a lone space. Whitespace-only records carry
                    // no information and would show up as blank log lines.
                    if (!string.IsNullOrWhiteSpace(text))
                        lines.Add(text);
                }
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>还原 PowerShell 的 _xHHHH_ 控制字符转义（_x000D_ / _x000A_ 等）。</summary>
    internal static string DecodeClixmlEscapes(string text) =>
        ClixmlEscapeRegex.Replace(text, match =>
        {
            var codePoint = Convert.ToInt32(match.Groups[1].Value, 16);
            return char.ConvertFromUtf32(codePoint);
        });

    private static IEnumerable<string> SplitDecodedText(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // PowerShell 的每条错误记录都以 CRLF 结尾，去掉尾部换行、保留内部空行。
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        foreach (var line in normalized.Split('\n'))
            yield return line;
    }

    /// <summary>
    /// 对最终 CommandResult 应用同一套规则。用于：
    ///  * 流式通道结束后，让 RemoteExecutionService 的回放不会重新带回已过滤的噪音；
    ///  * 非流式通道（WMI/DCOM、无回调的 PsExec）也能得到可读的 CLIXML 文本。
    /// </summary>
    public static CommandResult NormalizeCompletedResult(CommandResult result, bool filterPsExecProtocol) =>
        new(result.ExitCode,
            NormalizeCompletedText(result.StdOut, filterPsExecProtocol),
            NormalizeCompletedText(result.StdErr, filterPsExecProtocol));

    public static string NormalizeCompletedText(string? text, bool filterPsExecProtocol)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var lines = new List<string>();
        var normalizer = new RemoteExecutionOutputNormalizer(
            lines.Add, onSuppressed: null, filterPsExecProtocol: filterPsExecProtocol);

        foreach (var line in SplitOutputLines(text))
            normalizer.Write(line);

        normalizer.Flush();
        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> SplitOutputLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
