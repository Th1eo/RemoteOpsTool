using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;
using RemoteOpsTool.ViewModels.Dialogs;

namespace RemoteOpsTool.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IToolSetupService _toolSetup;
    private const int MaxConnectionFailures = 3;

    [ObservableProperty]
    private string _targetHost = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnectionLocked;

    [ObservableProperty]
    private int _connectionFailureCount;

    public bool IsRemoteEnabled => IsConnected && !IsConnectionLocked;

    public StatusBarViewModel StatusBar { get; }
    public LogViewModel Log { get; }
    public ConnectionViewModel Connection { get; }
    public FileDiskViewModel FileDisk { get; }
    public RemoteManagementViewModel RemoteManagement { get; }
    public InteractiveViewModel Interactive { get; }
    public NetworkViewModel Network { get; }
    public TerminalViewModel Terminal { get; }

    public MainViewModel(
        ISettingsService settings,
        ICredentialService credentialService,
        ILogService logService,
        IPsExecService psExecService,
        IRemoteExecutionService executionService,
        INetworkService networkService,
        ICapabilityService capabilityService,
        IDameWareService dameWareService,
        IFileDiskService fileDiskService,
        IDeviceService deviceService,
        IServiceManagerService serviceManagerService,
        IPrinterService printerService,
        ISoftwareService softwareService,
        IEnvVarService envVarService,
        ISystemInfoService systemInfoService,
        IToolSetupService toolSetup,
        ICacheService cacheService)
    {
        _settings = settings;
        _toolSetup = toolSetup;

        StatusBar = new StatusBarViewModel();
        Log = new LogViewModel(logService);
        Connection = new ConnectionViewModel(this, credentialService, logService, psExecService, dameWareService, networkService, capabilityService);
        FileDisk = new FileDiskViewModel(this, settings, logService, fileDiskService);
        RemoteManagement = new RemoteManagementViewModel(this, logService, psExecService, executionService,
            deviceService, serviceManagerService, printerService, softwareService, envVarService, systemInfoService, cacheService);
        Interactive = new InteractiveViewModel(this, logService, psExecService, executionService);
        Network = new NetworkViewModel(this, logService, networkService, psExecService, executionService);
        Terminal = new TerminalViewModel(this, logService, psExecService, executionService, networkService);
    }

    /// <summary>Fired when reconnect is requested from the UI.</summary>
    public event Action? ReconnectRequested;

    public void MarkConnectionSuccess()
    {
        IsConnected = true;
        IsConnectionLocked = false;
        ConnectionFailureCount = 0;
    }

    public void MarkConnectionFailure()
    {
        IsConnected = false;
        ConnectionFailureCount++;
        if (ConnectionFailureCount >= MaxConnectionFailures)
        {
            IsConnectionLocked = true;
            Log.LogService.Warn($"连续 {MaxConnectionFailures} 次连接失败，已锁定远程操作。请检查网络或凭据后点击重新连接按钮。");
        }
    }

    public void UnlockConnection()
    {
        IsConnectionLocked = false;
        ConnectionFailureCount = 0;
        Log.LogService.Info("连接已解锁。");
    }

    public void ResetFailureCount()
    {
        IsConnectionLocked = false;
        ConnectionFailureCount = 0;
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRemoteEnabled));
        OnPropertyChanged(nameof(ConnectButtonContent));
        StatusBar.IsConnected = value;
    }

    partial void OnIsConnectionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRemoteEnabled));
        StatusBar.IsConnectionLocked = value;
    }

    public string ConnectButtonContent => IsConnected ? "断开连接" : "重新连接";

    [RelayCommand]
    private void ToggleConnect()
    {
        if (IsConnected)
            Disconnect();
        else
            Reconnect();
    }

    [RelayCommand]
    private void Disconnect()
    {
        IsConnected = false;
        IsConnectionLocked = false;
        var (count, errors) = NetworkShareCredentialHelper.DisconnectAll();
        if (count > 0)
            Log.LogService.Info($"已断开 {count} 个由本工具建立的 SMB 连接。");
        foreach (var error in errors)
            Log.LogService.Warn($"SMB 断开失败: {error}");
        Log.LogService.Info($"已断开与 {TargetHost} 的连接。");
    }

    [RelayCommand]
    private void Reconnect()
    {
        ResetFailureCount();
        ReconnectRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = new SettingsViewModel(_settings, Log.LogService, _toolSetup);
        var window = new Views.Dialogs.SettingsDialog { DataContext = vm };
        window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);
    }

    public string GetTargetHost() => TargetHost?.Trim() ?? string.Empty;
}
