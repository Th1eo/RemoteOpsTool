using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

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

        _logService.Info($"正在启动{description}: host={host}");
        var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
        if (HostHelper.IsLocalHost(host))
        {
            await _psExecService.ExecuteInteractiveLocalAsync(
                command, cred.UserName, password, shell: CommandShell.Direct);
        }
        else
        {
            await _psExecService.ExecuteInteractiveRemoteAsync(
                host, cred.UserName, password, command, wrapCmd: false, shell: CommandShell.Direct);
        }
    }

    [RelayCommand] private Task LaunchCmdAsync() => LaunchInteractiveAsync("cmd.exe", "命令行");
    [RelayCommand] private Task LaunchPowerShellAsync() => LaunchInteractiveAsync("powershell.exe", "PowerShell 程序");
    [RelayCommand] private Task LaunchComputerMgmtAsync() => LaunchInteractiveAsync("compmgmt.msc", "计算机管理");
    [RelayCommand] private Task LaunchPrinterMgmtAsync() => LaunchInteractiveAsync("printmanagement.msc", "打印机管理");
    [RelayCommand] private Task LaunchRegEditAsync() => LaunchInteractiveAsync("regedit.exe", "注册表编辑器");
    [RelayCommand] private Task LaunchEnvVarAsync() => LaunchInteractiveAsync("systempropertiesadvanced.exe", "环境变量");
}
