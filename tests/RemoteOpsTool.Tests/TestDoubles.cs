using System.Collections.ObjectModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Tests;

internal sealed class TestSettingsService : ISettingsService
{
    public AppSettings Settings { get; } = new();
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
}

internal sealed class TestLogService : ILogService
{
    public ObservableCollection<LogEntry> Entries { get; } = [];
    public LogLevel FilterLevel { get; set; }
    public bool IsExecuting { get; set; }
    public bool FileLogEnabled { get; set; }
    public event Action<LogEntry>? EntryAppended { add { } remove { } }
    public event Action? LogCleared { add { } remove { } }
    public event Action? LogRebuilt { add { } remove { } }
    public event Action<bool>? ExecutingChanged { add { } remove { } }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Debug(string message) { }
    public void Log(LogLevel level, string message) { }
    public void Clear() { }
}
