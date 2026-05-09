using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class DeviceManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IDeviceService _deviceService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExec;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private DeviceRow? _selectedDevice;

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public IEnumerable<DeviceRow> FilteredDevices => string.IsNullOrWhiteSpace(SearchText) 
        ? Devices 
        : Devices.Where(d => (d.FriendlyName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) 
                          || d.InstanceId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                          || d.Class.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredDevices));

    public DeviceManagerViewModel(MainViewModel main, IDeviceService deviceService, ILogService logService, IPsExecService psExec)
    {
        _main = main;
        _deviceService = deviceService;
        _logService = logService;
        _psExec = psExec;
        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync(string? selectId = null)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var list = await _deviceService.GetDevicesAsync(host, cred.UserName, password ?? string.Empty);
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
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadDevicesAsync();

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
