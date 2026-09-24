using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class NetworkViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly INetworkService _networkService;

    public NetworkViewModel(MainViewModel main, INetworkService networkService)
    {
        _main = main;
        _networkService = networkService;
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
}
