using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ICredentialService _credentialService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly IDameWareService _dameWareService;
    private readonly INetworkService _networkService;
    private CancellationTokenSource? _pingCts;

    [ObservableProperty]
    private bool _isContinuousPingActive;

    public string ContinuousPingButtonText => IsContinuousPingActive ? "停止 Ping" : "持续 Ping";
    public string ContinuousPingButtonStyleKey => IsContinuousPingActive ? "BtnRed" : "BtnBlue";
    public System.Windows.Media.Color ContinuousPingActiveColor =>
        IsContinuousPingActive
            ? System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C)
            : System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB);
    public System.Windows.Media.Color ContinuousPingBorderColor =>
        IsContinuousPingActive
            ? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)
            : System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);

    public ICredentialService CredentialService => _credentialService;

    public ConnectionViewModel(
        MainViewModel main,
        ICredentialService credentialService,
        ILogService logService,
        IPsExecService psExecService,
        IDameWareService dameWareService,
        INetworkService networkService)
    {
        _main = main;
        _credentialService = credentialService;
        _logService = logService;
        _psExecService = psExecService;
        _dameWareService = dameWareService;
        _networkService = networkService;
    }

    [RelayCommand]
    private void ContinuousPing()
    {
        if (IsContinuousPingActive)
        {
            _pingCts?.Cancel();
            IsContinuousPingActive = false;
            _logService.Info("持续 Ping 已停止。");
            return;
        }

        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host))
        {
            _logService.Warn("请输入目标主机名。");
            return;
        }

        _pingCts = new CancellationTokenSource();
        IsContinuousPingActive = true;
        var token = _pingCts.Token;
        var dispatcher = System.Windows.Application.Current.Dispatcher;

        _main.StatusBar.IsPinging = true;
        _main.StatusBar.PingStatus = "Pinging...";
        _logService.Info($"开始持续 Ping {host}...");

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _networkService.PingAsync(host, token);

                        _ = dispatcher.InvokeAsync(() =>
                        {
                            if (result.Success)
                            {
                                _main.StatusBar.PingLatencyMs = result.RoundtripTime;
                                _main.StatusBar.PingStatus = $"{result.RoundtripTime}ms";
                            }
                            else
                            {
                                _main.StatusBar.PingLatencyMs = 0;
                                _main.StatusBar.PingStatus = "超时";
                            }
                        });
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        _ = dispatcher.InvokeAsync(() =>
                        {
                            _main.StatusBar.PingLatencyMs = 0;
                            _main.StatusBar.PingStatus = "失败";
                        });
                    }

                    try { await Task.Delay(1000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                _ = dispatcher.InvokeAsync(() =>
                {
                    _main.StatusBar.IsPinging = false;
                    _main.StatusBar.PingStatus = string.Empty;
                });
                _pingCts?.Dispose();
                _pingCts = null;
                IsContinuousPingActive = false;
            }
        });
    }

    partial void OnIsContinuousPingActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ContinuousPingButtonText));
        OnPropertyChanged(nameof(ContinuousPingActiveColor));
        OnPropertyChanged(nameof(ContinuousPingBorderColor));
    }

    [RelayCommand]
    private async Task PingAsync()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host))
        {
            _logService.Warn("请输入目标主机名。");
            return;
        }
        _logService.Info($"正在 Ping {host}...");
        var result = await _networkService.PingAsync(host);
        _logService.Info(result.Output);
    }

    [RelayCommand]
    private async Task ProbeCapabilitiesAsync()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host))
        {
            _logService.Warn("请输入目标主机名。");
            return;
        }

        var cred = _credentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
        {
            _logService.Warn("请先选择当前运维凭据。");
            return;
        }

        var password = _credentialService.DecryptPassword(cred);
        _logService.Info($"开始能力探测: {host}");
        var results = await _networkService.ProbeCapabilitiesAsync(host, cred.UserName, password ?? string.Empty);

        _logService.Info("能力探测矩阵：");
        foreach (var item in results)
        {
            var status = item.Success ? "OK" : "FAIL";
            _logService.Info($"[{status}] {item.Name}: {item.Detail}");
        }
    }

    [RelayCommand]
    private async Task DameWareConnectAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _credentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
        {
            _logService.Warn("Please select a credential first.");
            return;
        }
        var password = _credentialService.DecryptPassword(cred);
        await _dameWareService.ConnectAsync(host, cred.UserName, password ?? string.Empty);
    }

    [RelayCommand]
    private void OpenCredentialDialog()
    {
        var vm = new CredentialViewModel(_credentialService, _logService);
        var window = new Views.Dialogs.CredentialDialog { DataContext = vm };
        window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);
    }

    [RelayCommand]
    private void ClearCredentials()
    {
        _credentialService.ClearAll();
        _ = _credentialService.SaveAsync();
        _logService.Info("All credentials cleared.");
    }
}
