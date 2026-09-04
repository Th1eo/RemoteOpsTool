using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class DeviceManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IDeviceService _deviceService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExec;
    private readonly ICacheService _cache;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private DeviceRow? _selectedDevice;

    [ObservableProperty]
    private string _lastRefreshText = "尚未刷新";

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public IEnumerable<DeviceRow> FilteredDevices => string.IsNullOrWhiteSpace(SearchText) 
        ? Devices 
        : Devices.Where(d => (d.FriendlyName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) 
                          || d.InstanceId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                          || d.Class.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredDevices));

    public DeviceManagerViewModel(MainViewModel main, IDeviceService deviceService, ILogService logService, IPsExecService psExec, ICacheService cache)
    {
        _main = main;
        _deviceService = deviceService;
        _logService = logService;
        _psExec = psExec;
        _cache = cache;
        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync(string? selectId = null, bool force = false)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            if (!force)
                await _cache.PopulateFromCacheAsync<List<DeviceInfo>>(host, CacheKeys.Devices, list => PopulateDevices(list, null));

            if (force || !await _cache.HasValidCacheAsync(host, CacheKeys.Devices))
            {
                var list = await _deviceService.GetDevicesAsync(host, cred.UserName, password ?? string.Empty);
                await _cache.SaveAndPopulateAsync(host, CacheKeys.Devices, list, l => PopulateDevices(l, selectId));
            }

            LastRefreshText = _cache.GetCacheAge(host, CacheKeys.Devices) is string age ? $"缓存于 {age}" : "尚未刷新";
        }
        finally { IsLoading = false; }
    }

    private void PopulateDevices(List<DeviceInfo> list, string? selectId)
    {
        Devices.Clear();
        DeviceRow? toSelect = null;
        foreach (var d in list)
        {
            var row = new DeviceRow(d);
            Devices.Add(row);
            if (selectId != null && d.InstanceId == selectId) toSelect = row;
        }
        OnPropertyChanged(nameof(FilteredDevices));
        if (toSelect != null)
            SelectedDevice = toSelect;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadDevicesAsync(force: true);

    private async Task BatchOperationAsync(Func<IDeviceService, string, string, string, string, Task> action)
    {
        var items = Devices.Where(d => d.IsChecked).ToList();
        if (items.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        foreach (var item in items)
            await action(_deviceService, host, cred.UserName, password ?? string.Empty, item.InstanceId);
        _cache.Invalidate(host, CacheKeys.Devices);
        await LoadDevicesAsync(items[0].InstanceId);
    }

    [RelayCommand]
    private async Task DisableSelectedAsync()
    {
        await BatchOperationAsync((svc, host, user, pass, id) => svc.DisableDeviceAsync(host, user, pass, id));
    }

    [RelayCommand]
    private async Task EnableSelectedAsync()
    {
        await BatchOperationAsync((svc, host, user, pass, id) => svc.EnableDeviceAsync(host, user, pass, id));
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        await BatchOperationAsync((svc, host, user, pass, id) => svc.UninstallDeviceAsync(host, user, pass, id));
    }

    [RelayCommand]
    private async Task UpdateDriverAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Driver files (*.inf)|*.inf|All files (*.*)|*.*",
            Title = "选择驱动程序文件"
        };
        if (WindowHelper.RunWithStateGuard(
            System.Windows.Application.Current.MainWindow,
            () => dialog.ShowDialog()) != true) return;

        var localPath = dialog.FileName;
        var fileName = Path.GetFileName(localPath);
        var remotePath = $@"\\{host}\ADMIN$\Temp\{fileName}";

        try
        {
            File.Copy(localPath, remotePath, true);
        }
        catch (Exception ex)
        {
            _logService.Error($"复制驱动文件失败: {ex.Message}");
            return;
        }

        var localTempPath = $@"C:\Windows\Temp\{fileName}";
        var result = await _psExec.ExecuteAsync(host, cred.UserName, password ?? string.Empty,
            $"pnputil /add-driver \"{localTempPath}\" /install");
        _logService.Info($"pnputil: {result.StdOut}");

        _cache.Invalidate(host, CacheKeys.Devices);
        await LoadDevicesAsync();
    }
}

public partial class DeviceRow : ObservableObject
{
    public DeviceInfo Device { get; }
    [ObservableProperty] private bool _isChecked;
    public string Status => Device.Status;
    public string Class => Device.Class;
    public string FriendlyName => Device.FriendlyName;
    public string InstanceId => Device.InstanceId;
    public string DriverVersion => Device.DriverVersion;
    public DeviceRow(DeviceInfo d) { Device = d; }
}
