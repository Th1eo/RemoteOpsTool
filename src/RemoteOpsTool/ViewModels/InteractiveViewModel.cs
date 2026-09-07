using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Models;

namespace RemoteOpsTool.ViewModels;

public partial class InteractiveViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;

    public InteractiveViewModel(MainViewModel main, ILogService logService, IPsExecService psExecService)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
    }

    private async Task LaunchInteractiveAsync(string command, string description)
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
        {
            _logService.Warn("请先选择凭据。");
            return;
        }

        if (HostHelper.IsLocalHost(host))
        {
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            await _psExecService.ExecuteInteractiveLocalAsync(command, cred.UserName, password ?? string.Empty, shell: CommandShell.Direct);
        }
        else
        {
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            await _psExecService.ExecuteInteractiveRemoteAsync(host, cred.UserName, password ?? string.Empty, command, wrapCmd: false, shell: CommandShell.Direct);
        }
    }

    [RelayCommand] private void LaunchCmd() => _ = LaunchInteractiveAsync("cmd.exe", "命令行");
    [RelayCommand] private void LaunchPowerShell() => _ = LaunchInteractiveAsync("powershell.exe", "PowerShell程序");
    [RelayCommand] private void LaunchComputerMgmt() => _ = LaunchInteractiveAsync("compmgmt.msc", "计算机管理");
    [RelayCommand] private void LaunchPrinterMgmt() => _ = LaunchInteractiveAsync("printmanagement.msc", "打印机管理");
    [RelayCommand] private void LaunchRegEdit() => _ = LaunchInteractiveAsync("regedit.exe", "注册表");
    [RelayCommand] private void LaunchEnvVar() => _ = LaunchInteractiveAsync("systempropertiesadvanced.exe", "环境变量");
}
