using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class NetworkPortsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly INetworkService _networkService;
    private readonly ILogService _logService;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    public NetworkConnectionRow? RightClickedRow { get; set; }

    private List<NetworkConnectionRow> _connections = [];

    public IEnumerable<NetworkConnectionRow> FilteredConnections =>
        string.IsNullOrWhiteSpace(SearchText)
            ? _connections
            : _connections.Where(r =>
                (r.ProcessName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.LocalAddress.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.RemoteAddress.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.Protocol.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.ProcessId.ToString().Contains(SearchText));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredConnections));

    public NetworkPortsViewModel(MainViewModel main, INetworkService networkService, ILogService logService)
    {
        _main = main;
        _networkService = networkService;
        _logService = logService;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            var conns = await _networkService.GetActiveConnectionsAsync(host, cred.UserName, password ?? string.Empty);

            _connections = await Task.Run(() =>
                conns.Select(c => new NetworkConnectionRow(c)).ToList());
        }
        catch { }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(FilteredConnections));
        }
    }

    private List<NetworkConnectionRow> CheckedRows =>
        _connections.Where(r => r.IsChecked).ToList();

    [RelayCommand]
    private async Task KillProcessAsync() => await KillProcessesAsync(false);

    [RelayCommand]
    private async Task KillProcessTreeAsync() => await KillProcessesAsync(true);

    private async Task KillProcessesAsync(bool killTree)
    {
        var items = RightClickedRow != null
            ? new List<NetworkConnectionRow> { RightClickedRow }
            : CheckedRows;

        if (items.Count == 0) return;
        RightClickedRow = null;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        var killedPids = new HashSet<int>();
        foreach (var item in items)
        {
            if (item.Info.ProcessId <= 0 || killedPids.Contains(item.Info.ProcessId)) continue;
            killedPids.Add(item.Info.ProcessId);
            await _networkService.KillProcessAsync(host, cred.UserName, password ?? string.Empty,
                item.Info.ProcessId, killTree);
        }
        await LoadAsync();
    }
}

public partial class NetworkConnectionRow : ObservableObject
{
    public NetworkConnectionInfo Info { get; }
    [ObservableProperty] private bool _isChecked;

    public string Protocol => Info.Protocol;
    public string LocalAddress => Info.LocalAddress;
    public string RemoteAddress => Info.RemoteAddress;
    public string State => Info.State;
    public int ProcessId => Info.ProcessId;
    public string ProcessName => Info.ProcessName;

    public NetworkConnectionRow(NetworkConnectionInfo info) { Info = info; }
}
