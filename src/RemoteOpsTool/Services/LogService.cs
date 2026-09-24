using System.Collections.ObjectModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class LogService : ILogService, IDisposable
{
    private readonly object _entriesLock = new();
    private readonly ObservableCollection<LogEntry> _allEntries = [];
    private static readonly object _fileLock = new();
    private static string? _logFilePath;
    private static readonly StringBuilder _fileBuffer = new(65536);
    private static System.Threading.Timer? _flushTimer;
    private const int MaxLogEntries = 20000;
    private const int FlushIntervalMs = 1000;
    private const int MaxBufferSize = 32768;

    public ObservableCollection<LogEntry> Entries { get; } = [];
    private LogLevel _filterLevel = LogLevel.Info;

    public event Action<LogEntry>? EntryAppended;
    public event Action? LogCleared;
    public event Action? LogRebuilt;
    public event Action<bool>? ExecutingChanged;

    public LogLevel FilterLevel
    {
        get => _filterLevel;
        set
        {
            if (_filterLevel == value) return;
            _filterLevel = value;
            RefreshFilter();
        }
    }

    private bool _isExecuting;
    public bool IsExecuting
    {
        get => _isExecuting;
        set
        {
            if (_isExecuting == value) return;
            _isExecuting = value;
            ExecutingChanged?.Invoke(value);
        }
    }
    public bool FileLogEnabled { get; set; }

    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warn(string message) => Log(LogLevel.Warn, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Debug(string message)
    {
        if (!FileLogEnabled) return;
        BufferWriteToFile(new LogEntry(DateTime.Now, LogLevel.Info, $"[DEBUG] {message}"));
    }

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_entriesLock)
        {
            _allEntries.Add(entry);
            if (_allEntries.Count > MaxLogEntries)
                _allEntries.RemoveAt(0);
        }

        if (level >= FilterLevel)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Entries.Add(entry);
                EntryAppended?.Invoke(entry);
                if (Entries.Count > 10000)
                    Entries.RemoveAt(0);
            });
        }

        if (FileLogEnabled)
            BufferWriteToFile(entry);
    }

    private static void BufferWriteToFile(LogEntry entry)
    {
        if (_logFilePath == null)
            _logFilePath = ResolveLogFilePath();

        var line = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}{Environment.NewLine}";
        lock (_fileLock)
        {
            _fileBuffer.Append(line);
            if (_fileBuffer.Length > MaxBufferSize)
                FlushBufferInternal();
            else if (_flushTimer == null)
                _flushTimer = new System.Threading.Timer(_ => FlushBufferInternal(), null, FlushIntervalMs, FlushIntervalMs);
        }
    }

    private static void FlushBufferInternal()
    {
        lock (_fileLock)
        {
            if (_fileBuffer.Length == 0) return;
            try
            {
                File.AppendAllText(_logFilePath!, _fileBuffer.ToString(), Encoding.UTF8);
                _fileBuffer.Clear();
            }
            catch { }
        }
    }

    /// <summary>
    /// 解析日志文件路径：优先 %LOCALAPPDATA%，并把目录 ACL 收紧到当前用户与
    /// SYSTEM，避免日志（可能包含命令细节）被同机其他用户读取。
    /// </summary>
    internal static string ResolveLogFilePath()
    {
        foreach (var directory in CandidateLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                RestrictToCurrentUser(directory);
                return Path.Combine(directory, AppConstants.LogFileName);
            }
            catch
            {
                // 换下一个候选目录；日志功能不能影响主流程。
            }
        }

        return Path.Combine(Path.GetTempPath(), AppConstants.LogFileName);
    }

    private static IEnumerable<string> CandidateLogDirectories()
    {
        yield return AppConstants.LogFolder;
        yield return Path.Combine(Path.GetTempPath(), AppConstants.CompanyFolder, "logs");
    }

    private static void RestrictToCurrentUser(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRule(rule);

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        info.SetAccessControl(security);
    }
    public static void FlushAndDispose()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        FlushBufferInternal();
    }

    public void Dispose()
    {
        FlushAndDispose();
    }

    public void Clear()
    {
        lock (_entriesLock)
            _allEntries.Clear();

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Clear();
            LogCleared?.Invoke();
        });
    }

    private void RefreshFilter()
    {
        List<LogEntry> filtered;
        lock (_entriesLock)
            filtered = _allEntries.Where(e => e.Level >= _filterLevel).ToList();

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Clear();
            foreach (var e in filtered)
                Entries.Add(e);
            LogRebuilt?.Invoke();
        });
    }
}
