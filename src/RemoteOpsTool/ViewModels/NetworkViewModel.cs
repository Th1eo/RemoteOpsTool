using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class NetworkViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly INetworkService _networkService;
    private readonly IPsExecService _psExecService;

    public NetworkViewModel(MainViewModel main, ILogService logService,
        INetworkService networkService, IPsExecService psExecService)
    {
        _main = main;
        _logService = logService;
        _networkService = networkService;
        _psExecService = psExecService;
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        await _networkService.FlushDnsAsync(host, cred.UserName, password ?? string.Empty);
    }

    [RelayCommand]
    private async Task RefreshIpAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        await _networkService.RefreshIpAsync(host, cred.UserName, password ?? string.Empty);
    }

    [RelayCommand]
    private void OpenPortManager()
    {
        var vm = new Dialogs.ProcessListViewModel(_main, _networkService, _logService);
        var window = new Views.Dialogs.NetworkPortsWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }
}
