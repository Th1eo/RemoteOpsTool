using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
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
    private readonly ICapabilityService _capabilityService;
    private CredentialInfo? _observedCredential;
    private CancellationTokenSource? _pingCts;

    [ObservableProperty]
    private bool _isContinuousPingActive;

    public string ContinuousPingButtonText => IsContinuousPingActive ? "停止 Ping" : "持续 Ping";
    public string ContinuousPingButtonStyleKey => IsContinuousPingActive ? "BtnRed" : "BtnBlue";
    public System.Windows.Media.Color ContinuousPingActiveColor =>
        IsContinuousPingActive
            ? System.Windows.Media.Color.FromRgb(0xB3, 0x19, 0x2F)
            : System.Windows.Media.Color.FromRgb(0x6F, 0xA5, 0x1D);
    public System.Windows.Media.Color ContinuousPingBorderColor =>
        IsContinuousPingActive
            ? System.Windows.Media.Color.FromRgb(0xB3, 0x19, 0x2F)
            : System.Windows.Media.Color.FromRgb(0x6F, 0xA5, 0x1D);

    public ICredentialService CredentialService => _credentialService;

    public string CurrentCredentialText =>
        _credentialService.SelectedCredential?.DisplayText ?? "未选择当前凭据";

    public CredentialHealth CurrentCredentialHealth
    {
        get
        {
            return _credentialService.SelectedCredential?.GetHealthForHost(_main.GetTargetHost())
                ?? CredentialHealth.Unverified;
        }
    }

    public string CurrentCredentialHealthText
    {
        get
        {
            var credential = _credentialService.SelectedCredential;
            if (credential is null)
                return "请选择当前运维凭据";

            if (credential.Health != CredentialHealth.SecretUnreadable &&
                !credential.IsValidatedForHost(_main.GetTargetHost()))
            {
                return "未在当前目标主机验证";
            }

            return credential.HealthText;
        }
    }

    public string CurrentCredentialDetailText
    {
        get
        {
            var credential = _credentialService.SelectedCredential;
            if (credential is null)
                return "请先在“凭据管理”中选择一个运维身份。";

            if (credential.Health == CredentialHealth.SecretUnreadable)
                return string.IsNullOrWhiteSpace(credential.LastError)
                    ? credential.HealthDetail
                    : credential.LastErrorText;

            if (!credential.IsValidatedForHost(_main.GetTargetHost()))
                return "点击“能力探测”或“验证”检查该身份在本目标机上的可用性。";

            return string.IsNullOrWhiteSpace(credential.LastError)
                ? credential.HealthDetail
                : credential.LastErrorText;
        }
    }

    public ConnectionViewModel(
        MainViewModel main,
        ICredentialService credentialService,
        ILogService logService,
        IPsExecService psExecService,
        IDameWareService dameWareService,
        INetworkService networkService,
        ICapabilityService capabilityService)
    {
        _main = main;
        _credentialService = credentialService;
        _logService = logService;
        _psExecService = psExecService;
        _dameWareService = dameWareService;
        _networkService = networkService;
        _capabilityService = capabilityService;

        _main.PropertyChanged += OnMainPropertyChanged;
        _credentialService.SelectedCredentialChanged += OnSelectedCredentialChanged;
        ObserveSelectedCredential();
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
                                _main.MarkConnectionSuccess();
                            }
                            else
                            {
                                _main.StatusBar.PingLatencyMs = 0;
                                _main.StatusBar.PingStatus = "超时";
                                _main.MarkConnectionFailure();
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
                            _main.MarkConnectionFailure();
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
        _main.StatusBar.IsPinging = true;
        _main.StatusBar.PingStatus = "Pinging...";
        var result = await _networkService.PingAsync(host);
        _main.StatusBar.IsPinging = false;
        _logService.Info(result.Output);
        if (result.Success)
        {
            _main.StatusBar.PingLatencyMs = result.RoundtripTime;
            _main.StatusBar.PingStatus = $"{result.RoundtripTime}ms";
            _main.MarkConnectionSuccess();
        }
        else
        {
            _main.StatusBar.PingLatencyMs = 0;
            _main.StatusBar.PingStatus = "超时";
            _main.MarkConnectionFailure();
        }
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

        var cred = _credentialService.SelectedCredential;
        if (cred == null)
        {
            _logService.Warn("请先选择当前运维凭据。");
            return;
        }

        if (!_credentialService.TryDecryptPassword(cred, out var password))
        {
            _logService.Error($"凭据 {cred.MaskedDisplay} 的密码不可读，请先重新录入密码。");
            return;
        }

        _logService.Info($"开始能力探测: {host}");
        var snapshot = await _capabilityService.RefreshAsync(
            host, cred.UserName, password);

        _logService.Info("能力探测矩阵：");
        foreach (var item in snapshot.RawResults)
        {
            var status = item.Success ? "OK" : "FAIL";
            _logService.Info($"[{status}] {item.Name}: {item.Detail}");
        }

        var assessment = CredentialHealthClassifier.Classify(snapshot.RawResults);
        cred.Health = assessment.Health;
        cred.LastValidatedAt = DateTimeOffset.Now;
        cred.LastValidatedHost = host;
        cred.LastError = assessment.Error;
        await _credentialService.SaveAsync();

        _logService.Info($"凭据健康状态: {cred.MaskedDisplay} -> {cred.HealthText}");
    }

    [RelayCommand]
    private async Task DameWareConnectAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _credentialService.SelectedCredential;
        if (cred == null)
        {
            _logService.Warn("请先选择当前运维凭据。");
            return;
        }

        if (!_credentialService.TryDecryptPassword(cred, out var password))
        {
            _logService.Error($"凭据 {cred.MaskedDisplay} 的密码不可读，请先重新录入密码。");
            return;
        }

        await _dameWareService.ConnectAsync(host, cred.UserName, password);
    }

    [RelayCommand]
    private void OpenCredentialDialog()
    {
        var vm = new CredentialViewModel(
            _credentialService,
            _logService,
            _capabilityService,
            _main.GetTargetHost);
        var window = new Views.Dialogs.CredentialDialog { DataContext = vm };
        try
        {
            window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [RelayCommand]
    private async Task ClearCredentialsAsync()
    {
        if (_credentialService.Credentials.Count == 0)
            return;

        var owner = System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current?.MainWindow;
        var dialog = new Views.Dialogs.ConfirmationDialog(
            "清空凭据",
            "确认删除全部凭据？",
            $"将删除本机保存的 {_credentialService.Credentials.Count} 条运维凭据，且无法撤销。",
            "全部删除");

        var confirmed = owner is not null
            ? dialog.ShowDialogSafe(owner) == true
            : dialog.ShowDialog() == true;
        if (!confirmed)
            return;

        _credentialService.ClearAll();
        await _credentialService.SaveAsync();
        _logService.Info("已删除全部凭据。");
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TargetHost))
            NotifyCurrentCredentialStateChanged();
    }

    private void OnSelectedCredentialChanged(object? sender, EventArgs e)
    {
        ObserveSelectedCredential();
        NotifyCurrentCredentialStateChanged();
    }

    private void ObserveSelectedCredential()
    {
        if (_observedCredential is not null)
            _observedCredential.PropertyChanged -= OnObservedCredentialPropertyChanged;

        _observedCredential = _credentialService.SelectedCredential;

        if (_observedCredential is not null)
            _observedCredential.PropertyChanged += OnObservedCredentialPropertyChanged;
    }

    private void OnObservedCredentialPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        NotifyCurrentCredentialStateChanged();

    private void NotifyCurrentCredentialStateChanged()
    {
        OnPropertyChanged(nameof(CurrentCredentialText));
        OnPropertyChanged(nameof(CurrentCredentialHealth));
        OnPropertyChanged(nameof(CurrentCredentialHealthText));
        OnPropertyChanged(nameof(CurrentCredentialDetailText));
    }
}
