using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteOpsTool.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _hostName = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnectionLocked;

    [ObservableProperty]
    private bool _isPinging;

    [ObservableProperty]
    private long _pingLatencyMs;

    [ObservableProperty]
    private string _pingStatus = string.Empty;

    public string ConnectionDotColor =>
        IsConnectionLocked ? "#FFA500" :
        IsConnected ? "#4ECB71" : "#FF6B6B";

    public string ConnectionStatusText =>
        IsConnectionLocked ? "已锁定" :
        IsConnected ? "已连接" : "未连接";

    public string ConnectionStatusColor =>
        IsConnectionLocked ? "#FFA500" :
        IsConnected ? "#4ECB71" : "#FF6B6B";

    public string ConnectionStatusIcon =>
        IsConnectionLocked ? "⚠" :
        IsConnected ? "●" : "✕";

    [ObservableProperty]
    private string _cDriveInfo = "--";

    [ObservableProperty]
    private string _dDriveInfo = "--";

    [ObservableProperty]
    private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    public void UpdateTime()
    {
        CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionDotColor));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ConnectionStatusColor));
        OnPropertyChanged(nameof(ConnectionStatusIcon));
    }

    partial void OnIsConnectionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionDotColor));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ConnectionStatusColor));
        OnPropertyChanged(nameof(ConnectionStatusIcon));
    }
}
