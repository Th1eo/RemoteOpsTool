using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class ServiceManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IServiceManagerService _serviceManagerService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExec;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ServiceRow? _selectedService;

    public ObservableCollection<ServiceRow> ServiceEntries { get; } = [];

    /// <summary>Row that was right-clicked for context menu operations</summary>
    public ServiceRow? RightClickedRow { get; set; }

    public IEnumerable<ServiceRow> FilteredEntries => string.IsNullOrWhiteSpace(SearchText) 
        ? ServiceEntries 
        : ServiceEntries.Where(s => (s.DisplayName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) 
                                 || s.ServiceName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                 || s.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredEntries));

    public ServiceManagerViewModel(MainViewModel main, IServiceManagerService serviceManagerService,
        ILogService logService, IPsExecService psExec)
    {
        _main = main;
        _serviceManagerService = serviceManagerService;
        _logService = logService;
        _psExec = psExec;
        _ = LoadServicesAsync();
    }

    private async Task LoadServicesAsync(string? selectName = null)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var list = await _serviceManagerService.GetServicesAsync(host, cred.UserName, password ?? string.Empty);
            ServiceEntries.Clear();
            ServiceRow? toSelect = null;
            foreach (var s in list)
            {
                var row = new ServiceRow(s);
                ServiceEntries.Add(row);
                if (selectName != null && s.ServiceName == selectName) toSelect = row;
            }
            if (toSelect != null) SelectedService = toSelect;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand] private async Task RefreshAsync() => await LoadServicesAsync();

    private async Task BatchOperationAsync(Func<IServiceManagerService, string, string, string, string, Task> action)
    {
        var items = ServiceEntries.Where(s => s.IsChecked).ToList();
        if (items.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        foreach (var item in items)
            await action(_serviceManagerService, host, cred.UserName, password ?? string.Empty, item.ServiceName);
        await LoadServicesAsync(items[0].ServiceName);
    }

    [RelayCommand]
    private async Task ShowPropertiesAsync()
    {
        ServiceRow? row = RightClickedRow ?? ServiceEntries.FirstOrDefault(s => s.IsChecked);
        if (row == null) return;
        RightClickedRow = null;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        var settings = App.GetService<ISettingsService>();
        var vm = new ServicePropertiesViewModel(host, cred.UserName, password ?? "",
            row.ServiceName, row.DisplayName, settings, _psExec, _logService);
        var window = new Views.Dialogs.ServicePropertiesDialog
        {
            DataContext = vm,
            Owner = System.Windows.Application.Current.MainWindow
        };

        window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);
    }

    [RelayCommand]
    private async Task StartSelectedAsync()
        => await BatchOperationAsync((svc, host, user, pass, name) => svc.StartServiceAsync(host, user, pass, name));

    [RelayCommand]
    private async Task StopSelectedAsync()
        => await BatchOperationAsync((svc, host, user, pass, name) => svc.StopServiceAsync(host, user, pass, name));

    [RelayCommand]
    private async Task RestartSelectedAsync()
        => await BatchOperationAsync((svc, host, user, pass, name) => svc.RestartServiceAsync(host, user, pass, name));
}

public partial class ServiceRow : ObservableObject
{
    public ServiceInfo Service { get; }
    [ObservableProperty] private bool _isChecked;
    public string ServiceName => Service.ServiceName;
    public string DisplayName => Service.DisplayName;
    public string Status => Service.Status;
    public string StartType => Service.StartType;
    public string StatusDisplay => Status switch
    {
        "1" or "Stopped" => "已停止",
        "2" or "StartPending" => "启动中",
        "3" or "StopPending" => "停止中",
        "4" or "Running" => "运行中",
        "5" or "ContinuePending" => "继续挂起",
        "6" or "PausePending" => "暂停挂起",
        "7" or "Paused" => "已暂停",
        _ => Status
    };
    public ServiceRow(ServiceInfo s) { Service = s; }
}
