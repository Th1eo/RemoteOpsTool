using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly ILogService _logService;

    [ObservableProperty]
    private LogLevelItem _selectedFilterLevel;

    public ObservableCollection<LogEntry> Entries => _logService.Entries;
    public ILogService LogService => _logService;
    public bool IsExecuting => _logService.IsExecuting;

    public List<LogLevelItem> LogLevels { get; } =
    [
        new LogLevelItem(LogLevel.Info, "信息"),
        new LogLevelItem(LogLevel.Warn, "警告"),
        new LogLevelItem(LogLevel.Error, "错误")
    ];

    public event Action<LogEntry>? EntryAdded;
    public event Action? LogCleared;
    public event Action? LogRebuilt;

    public LogViewModel(ILogService logService)
    {
        _logService = logService;
        _selectedFilterLevel = LogLevels[0];
        _logService.EntryAppended += e =>
        {
            EntryAdded?.Invoke(e);
        };
        _logService.LogCleared += () => LogCleared?.Invoke();
        _logService.LogRebuilt += () => LogRebuilt?.Invoke();
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logService.Clear();
    }

    partial void OnSelectedFilterLevelChanged(LogLevelItem value)
    {
        _logService.FilterLevel = value.Level;
    }
}

public record LogLevelItem(LogLevel Level, string DisplayName)
{
    public override string ToString() => DisplayName;
}
