using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels;

public partial class RemoteManagementViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly IDeviceService _deviceService;
    private readonly IServiceManagerService _serviceManagerService;
    private readonly IPrinterService _printerService;
    private readonly ISoftwareService _softwareService;
    private readonly IEnvVarService _envVarService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly ICacheService _cacheService;

    public RemoteManagementViewModel(
        MainViewModel main,
        ILogService logService,
        IPsExecService psExecService,
        IDeviceService deviceService,
        IServiceManagerService serviceManagerService,
        IPrinterService printerService,
        ISoftwareService softwareService,
        IEnvVarService envVarService,
        ISystemInfoService systemInfoService,
        ICacheService cacheService)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
        _deviceService = deviceService;
        _serviceManagerService = serviceManagerService;
        _printerService = printerService;
        _softwareService = softwareService;
        _envVarService = envVarService;
        _systemInfoService = systemInfoService;
        _cacheService = cacheService;
    }

    [RelayCommand]
    private void OpenDeviceManager()
    {
        var vm = new Dialogs.DeviceManagerViewModel(_main, _deviceService, _logService, _psExecService, _cacheService);
        var window = new Views.Dialogs.DeviceManagerWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenServiceManager()
    {
        var vm = new Dialogs.ServiceManagerViewModel(_main, _serviceManagerService, _logService, _psExecService, _cacheService);
        var window = new Views.Dialogs.ServiceManagerWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenPrinterManager()
    {
        var vm = new Dialogs.PrinterManagerViewModel(_main, _printerService, _psExecService, _logService, _cacheService);
        var window = new Views.Dialogs.PrinterManagerWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenSoftwareManager()
    {
        var vm = new Dialogs.SoftwareManagerViewModel(_main, _softwareService, _logService, _psExecService, _cacheService);
        var window = new Views.Dialogs.SoftwareManagerWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenEnvVarEditor()
    {
        var vm = new Dialogs.EnvVarViewModel(_main, _envVarService, _logService, _psExecService, _cacheService);
        var window = new Views.Dialogs.EnvVarWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenSystemInfo()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;

        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var vm = new Dialogs.SystemInfoViewModel(_systemInfoService, host, cred.UserName, password ?? string.Empty);
        var window = new Views.Dialogs.SystemInfoWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private Task OpenRegistryAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) { _logService.Warn("请先选择凭据。"); return Task.CompletedTask; }
        if (string.IsNullOrEmpty(host)) return Task.CompletedTask;

        var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? "";
        var vm = new Dialogs.RemoteRegistryViewModel(host, cred.UserName, password, _psExecService, _logService, _cacheService);
        var window = new Views.Dialogs.RemoteRegistryWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
        _ = vm.InitializeAsync();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenUsersGroupsAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) { _logService.Warn("请先选择凭据。"); return; }
        if (string.IsNullOrEmpty(host)) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        if (HostHelper.IsLocalHost(host))
        {
            var result = await _psExecService.ExecuteInteractiveLocalAsync(
                "lusrmgr.msc",
                cred.UserName,
                password ?? string.Empty,
                shell: RemoteOpsTool.Models.CommandShell.Direct);
            if (!result.Success)
                _logService.Error($"打开本机用户和组失败: {result.StdErr}");
            return;
        }

        if (!string.IsNullOrEmpty(password))
            await ProcessHelper.RunAsync("cmdkey", $"/add:{host} /user:{cred.UserName} /pass:\"{password}\"");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "compmgmt.msc",
            Arguments = $"/computer=\\\\{host}",
            UseShellExecute = true
        });
        _logService.Info($"已打开计算机管理 → {host}，凭据已自动注入。");
    }
}
