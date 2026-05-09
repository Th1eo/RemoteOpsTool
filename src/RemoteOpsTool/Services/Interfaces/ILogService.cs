using System.Collections.ObjectModel;
using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface ILogService
{
    ObservableCollection<LogEntry> Entries { get; }
    LogLevel FilterLevel { get; set; }
    bool IsExecuting { get; set; }

    event Action<LogEntry> EntryAppended;
    event Action LogCleared;
    event Action LogRebuilt;

    bool FileLogEnabled { get; set; }

    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Debug(string message);
    void Log(LogLevel level, string message);
    void Clear();
}
