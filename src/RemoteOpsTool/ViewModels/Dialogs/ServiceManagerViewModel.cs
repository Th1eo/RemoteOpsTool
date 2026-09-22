using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class ServiceManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IServiceManagerService _serviceManagerService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ICacheService _cache;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ServiceRow? _selectedService;

    public ObservableCollection<ServiceRow> ServiceEntries { get; } = [];

    [ObservableProperty]
    private string _lastRefreshText = "尚未刷新";

    public ServiceRow? RightClickedRow { get; set; }

    public IEnumerable<ServiceRow> FilteredEntries => string.IsNullOrWhiteSpace(SearchText) 
        ? ServiceEntries 
        : ServiceEntries.Where(s => (s.DisplayName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) 
                                 || s.ServiceName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                 || s.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredEntries));

    public ServiceManagerViewModel(MainViewModel main, IServiceManagerService serviceManagerService,
        ILogService logService, IPsExecService psExec, IRemoteExecutionService execution, ICacheService cache)
    {
        _main = main;
        _serviceManagerService = serviceManagerService;
        _logService = logService;
        _psExec = psExec;
        _execution = execution;
        _cache = cache;
        _ = LoadServicesAsync();
    }

    /// <summary>服务缓存按凭据隔离，避免不同账户看到互相矛盾的服务集合。</summary>
    private static string CacheKey(string? username) => CacheKeys.ServicesForCredential(username);

    private async Task LoadServicesAsync(string? selectName = null, bool force = false)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var cacheKey = CacheKey(cred.UserName);

            if (!force)
                await _cache.PopulateFromCacheAsync<List<ServiceInfo>>(host, cacheKey, list => PopulateServiceEntries(list, null));

            if (force || !await _cache.HasValidCacheAsync(host, cacheKey))
            {
                var list = await _serviceManagerService.GetServicesAsync(host, cred.UserName, password ?? string.Empty);
                await _cache.SaveAndPopulateAsync(host, cacheKey, list, l => PopulateServiceEntries(l, selectName));
            }

            LastRefreshText = _cache.GetCacheAge(host, cacheKey) is string age ? $"缓存于 {age}" : "尚未刷新";
        }
        finally { IsLoading = false; }
    }

    private void PopulateServiceEntries(List<ServiceInfo> list, string? selectName)
    {
        ServiceEntries.Clear();
        ServiceRow? toSelect = null;
        foreach (var s in list)
        {
            var row = new ServiceRow(s);
            ServiceEntries.Add(row);
            if (selectName != null && s.ServiceName == selectName) toSelect = row;
        }
        OnPropertyChanged(nameof(FilteredEntries));
        if (toSelect != null) SelectedService = toSelect;
    }

    [RelayCommand] private async Task RefreshAsync() => await LoadServicesAsync(force: true);

    private async Task BatchOperationAsync(Func<IServiceManagerService, string, string, string, string, Task> action)
    {
        var rightClickedRow = RightClickedRow;
        RightClickedRow = null;
        var items = rightClickedRow != null
            ? [rightClickedRow]
            : ServiceEntries.Where(s => s.IsChecked).ToList();
        if (items.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        foreach (var item in items)
            await action(_serviceManagerService, host, cred.UserName, password ?? string.Empty, item.ServiceName);
        _cache.Invalidate(host, CacheKey(cred.UserName));
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

        var vm = new ServicePropertiesViewModel(host, cred.UserName, password ?? "",
            row.ServiceName, row.DisplayName, _psExec, _execution, _logService);
        var window = new Views.Dialogs.ServicePropertiesDialog
        {
            DataContext = vm,
            Owner = System.Windows.Application.Current.MainWindow
        };

        window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);

        _cache.Invalidate(host, CacheKey(cred.UserName));
        await LoadServicesAsync(row.ServiceName, force: true);
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
