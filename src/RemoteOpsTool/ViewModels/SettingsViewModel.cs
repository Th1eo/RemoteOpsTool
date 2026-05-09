using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly IToolSetupService _toolSetup;

    [ObservableProperty]
    private string _psToolsPath = string.Empty;

    [ObservableProperty]
    private string _dameWarePath = string.Empty;

    [ObservableProperty]
    private string _domainPublicPath = string.Empty;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private string _psToolsStatus = string.Empty;

    [ObservableProperty]
    private bool _psToolsIsHealthy;

    [ObservableProperty]
    private string _psToolsStatusColor = "#FF6B6B";

    public SettingsViewModel(ISettingsService settingsService, ILogService logService, IToolSetupService toolSetup)
    {
        _settingsService = settingsService;
        _logService = logService;
        _toolSetup = toolSetup;

        var s = settingsService.Settings;
        PsToolsPath = s.PsToolsPath;
        DameWarePath = s.DameWarePath;
        DomainPublicPath = s.DomainPublicPath;
        DebugMode = s.DebugMode;

        RefreshPsToolsStatus();
    }

    private void RefreshPsToolsStatus()
    {
        var ok = _toolSetup.IsPsToolsAvailable(PsToolsPath);
        PsToolsIsHealthy = ok;
        PsToolsStatus = ok ? "PsExec 状态正常 √" : "PsExec 未找到 ×";
        PsToolsStatusColor = ok ? "#4ECB71" : "#FF6B6B";
    }

    partial void OnPsToolsPathChanged(string value)
    {
        _settingsService.Settings.PsToolsPath = value;
        RefreshPsToolsStatus();
    }

    [RelayCommand]
    private void BrowsePsTools()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 PsTools 目录" };
        if (WindowHelper.RunWithStateGuard(
            System.Windows.Application.Current.MainWindow,
            () => dialog.ShowDialog()) == System.Windows.Forms.DialogResult.OK)
        {
            PsToolsPath = dialog.SelectedPath;
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
        var s = _settingsService.Settings;
        s.PsToolsPath = PsToolsPath;
        s.DameWarePath = DameWarePath;
        s.DomainPublicPath = DomainPublicPath;
        s.DebugMode = DebugMode;
        await _settingsService.SaveAsync();

        // Sync file logging immediately
        var logSvc = App.GetService<ILogService>();
        logSvc.FileLogEnabled = DebugMode;

        _logService.Info("设置已保存。");
    }
}
