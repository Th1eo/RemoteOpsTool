using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly IToolSetupService _toolSetup;
    private bool _isInitializing;

    [ObservableProperty]
    private string _psToolsPath = string.Empty;

    [ObservableProperty]
    private string _dameWarePath = string.Empty;

    [ObservableProperty]
    private string _domainPublicPath = string.Empty;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private bool _preferPsExec64;

    [ObservableProperty]
    private int _psExecConnectTimeoutSeconds;

    [ObservableProperty]
    private string _psExecRemoteWorkingDirectory = string.Empty;

    [ObservableProperty]
    private string _psExecServiceNamePrefix = string.Empty;

    [ObservableProperty]
    private string _psToolsStatus = string.Empty;

    [ObservableProperty]
    private bool _psToolsIsHealthy;

    [ObservableProperty]
    private string _psToolsStatusColor = "#C64545";

    public SettingsViewModel(ISettingsService settingsService, ILogService logService, IToolSetupService toolSetup)
    {
        _settingsService = settingsService;
        _logService = logService;
        _toolSetup = toolSetup;

        _isInitializing = true;
        var s = settingsService.Settings;
        PsToolsPath = s.PsToolsPath;
        DameWarePath = s.DameWarePath;
        DomainPublicPath = s.DomainPublicPath;
        DebugMode = s.DebugMode;
        PreferPsExec64 = s.PreferPsExec64;
        PsExecConnectTimeoutSeconds = s.PsExecConnectTimeoutSeconds;
        PsExecRemoteWorkingDirectory = s.PsExecRemoteWorkingDirectory;
        PsExecServiceNamePrefix = s.PsExecServiceNamePrefix;
        _isInitializing = false;

        RefreshPsToolsStatus();
    }

    private void RefreshPsToolsStatus()
    {
        var ok = _toolSetup.IsPsToolsAvailable(PsToolsPath);
        PsToolsIsHealthy = ok;
        PsToolsStatus = ok ? "PsExec 状态正常 √" : "PsExec 未找到 ×";
        PsToolsStatusColor = ok ? "#5DB872" : "#C64545";
    }

    partial void OnPsToolsPathChanged(string value)
    {
        _settingsService.Settings.PsToolsPath = value;
        RefreshPsToolsStatus();
    }

    partial void OnDebugModeChanged(bool value)
    {
        if (_isInitializing) return;

        _settingsService.Settings.DebugMode = value;
        _logService.FileLogEnabled = value;
        if (value)
            _logService.Info($"调试日志已启用，后续命令与操作细节将写入 {AppConstants.LogFilePath}。");
        else
            _logService.Info("调试日志已关闭。");
    }

    partial void OnPreferPsExec64Changed(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Settings.PreferPsExec64 = value;
    }

    partial void OnPsExecConnectTimeoutSecondsChanged(int value)
    {
        if (_isInitializing) return;
        _settingsService.Settings.PsExecConnectTimeoutSeconds = Math.Clamp(value, 3, 60);
    }

    partial void OnPsExecRemoteWorkingDirectoryChanged(string value)
    {
        if (_isInitializing) return;
        _settingsService.Settings.PsExecRemoteWorkingDirectory = value;
    }

    partial void OnPsExecServiceNamePrefixChanged(string value)
    {
        if (_isInitializing) return;
        _settingsService.Settings.PsExecServiceNamePrefix = value;
    }

    [RelayCommand]
    private void BrowsePsTools()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 PsTools 目录" };
        if (dialog.ShowDialog() == true)
        {
            PsToolsPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseDameWare()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择 DameWare 可执行文件"
        };
        if (WindowHelper.RunWithStateGuard(
            System.Windows.Application.Current.MainWindow,
            () => dialog.ShowDialog()) == true)
            DameWarePath = dialog.FileName;
    }

    [RelayCommand]
    private async Task ConfigurePsToolsAsync()
    {
        var (ok, msg) = await _toolSetup.ConfigurePsToolsAsync(PsToolsPath);
        _logService.Info(msg);
        RefreshPsToolsStatus();
    }

    [RelayCommand]
    private async Task UpdatePsToolsAsync()
    {
        var (ok, msg) = await _toolSetup.UpdatePsToolsAsync(PsToolsPath);
        _logService.Info(msg);
        RefreshPsToolsStatus();
    }

    [RelayCommand]
    private async Task UninstallPsToolsAsync()
    {
        var (ok, msg) = await _toolSetup.UninstallPsToolsAsync(PsToolsPath);
        _logService.Info(msg);
        RefreshPsToolsStatus();
    }

    [RelayCommand]
    private void OpenPsToolsDownload()
    {
        _toolSetup.OpenDownloadPage();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var confirmDialog = new ConfirmationDialog(
            "保存设置",
            "确认保存当前设置？",
            "确认后将把高级设置写入本机配置文件，后续远程操作会使用这些设置。",
            "保存");

        if (confirmDialog.ShowDialog() != true)
        {
            _logService.Info("已取消保存设置。");
            return;
        }

        var s = _settingsService.Settings;
        s.PsToolsPath = PsToolsPath;
        s.DameWarePath = DameWarePath;
        s.DomainPublicPath = DomainPublicPath;
        s.DebugMode = DebugMode;
        s.PreferPsExec64 = PreferPsExec64;
        s.PsExecConnectTimeoutSeconds = Math.Clamp(PsExecConnectTimeoutSeconds, 3, 60);
        s.PsExecRemoteWorkingDirectory = PsExecRemoteWorkingDirectory;
        s.PsExecServiceNamePrefix = PsExecServiceNamePrefix;
        await _settingsService.SaveAsync();

        _logService.Info("设置已保存。");
    }
}
