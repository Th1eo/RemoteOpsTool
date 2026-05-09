using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly INetworkService _networkService;

    [ObservableProperty] private string _commandText = string.Empty;
    [ObservableProperty] private bool _isInteractiveMode;
    [ObservableProperty] private string _selectedSession = "";
    [ObservableProperty] private bool _hasSessions;

    public ObservableCollection<string> AvailableSessions { get; } = [];

    public TerminalViewModel(MainViewModel main, ILogService logService,
        IPsExecService psExecService, INetworkService networkService)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
        _networkService = networkService;
    }

    partial void OnIsInteractiveModeChanged(bool value)
    {
        if (value)
            _ = RefreshSessionsAsync();
    }

    public async Task RefreshSessionsAsync()
    {
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            var sessions = await _networkService.GetUserSessionsAsync(host, cred.UserName, password ?? string.Empty);

            AvailableSessions.Clear();
            foreach (var s in sessions.Where(s => !string.IsNullOrWhiteSpace(s.Username)))
            {
                var display = $"{s.Username} (会话{s.SessionId}, {s.State})";
                if (!AvailableSessions.Contains(display))
                    AvailableSessions.Add(display);
            }
            HasSessions = AvailableSessions.Count > 0;
            if (HasSessions && string.IsNullOrEmpty(SelectedSession))
                SelectedSession = AvailableSessions.First(s => s.Contains("Active") || s.Contains("运行中")) ?? AvailableSessions[0];
        }
        catch { }
    }

    [RelayCommand]
    private async Task ExecuteCommandAsync()
    {
        var command = CommandText.Trim();
        if (string.IsNullOrEmpty(command)) return;

        CommandText = string.Empty;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
        {
            _logService.Warn("请先选择凭据。");
            return;
        }

        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        if (IsInteractiveMode && !string.IsNullOrEmpty(SelectedSession))
        {
            var sessionId = ParseSessionId(SelectedSession);
            if (sessionId < 0)
            {
                _logService.Warn("无法解析会话ID，使用默认会话。");
                sessionId = 1;
            }

            _logService.Info($"交互执行到会话 {sessionId}: {command}");

            if (HostHelper.IsLocalHost(host))
                _psExecService.ExecuteInteractiveLocal(command);
            else
                await _psExecService.ExecuteInteractiveRemoteAsync(host, cred.UserName, password ?? string.Empty, command);
        }
        else
        {
            _logService.Info($"远程执行: {command}");
            await _psExecService.ExecuteWithOutputAsync(host, cred.UserName, password ?? string.Empty,
                command, line => _logService.Info(line));
        }
    }

    private static int ParseSessionId(string sessionDisplay)
    {
        // Format: "username (会话3, Active)"
        var start = sessionDisplay.IndexOf("会话", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return -1;
        var numStart = start + 2;
        var numEnd = sessionDisplay.IndexOfAny([')', ','], numStart);
        if (numEnd < 0) numEnd = sessionDisplay.Length;
        if (int.TryParse(sessionDisplay[numStart..numEnd], out var id))
            return id;
        return -1;
    }

    [RelayCommand]
    private async Task LoadScriptAsync()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host))
        {
            _logService.Warn("请先输入目标主机名。");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "脚本文件 (*.bat;*.ps1;*.cmd)|*.bat;*.ps1;*.cmd|所有文件 (*.*)|*.*",
            Title = "选择脚本文件"
        };

        if (WindowHelper.RunWithStateGuard(
            System.Windows.Application.Current.MainWindow,
            () => dialog.ShowDialog()) != true) return;

        try
        {
            var localPath = dialog.FileName;
            var fileName = Path.GetFileName(localPath);
            var remotePath = $@"\\{host}\admin$\Temp\{fileName}";
            await Task.Run(() => File.Copy(localPath, remotePath, true));
            _logService.Info($"脚本已上传到目标主机: C:\\Temp\\{fileName}");

            if (fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                CommandText = $"powershell -ExecutionPolicy Bypass -File \"C:\\Temp\\{fileName}\"";
            else
                CommandText = $"\"C:\\Temp\\{fileName}\"";

            _logService.Info($"执行命令已生成，点击「执行」或按 Enter 发送到目标主机。");
        }
        catch (Exception ex)
        {
            _logService.Error($"脚本上传失败: {ex.Message}");
        }
    }
}
