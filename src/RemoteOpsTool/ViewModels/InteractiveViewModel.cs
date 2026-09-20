using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.ViewModels;

public partial class InteractiveViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly IRemoteExecutionService _execution;

    public InteractiveViewModel(
        MainViewModel main,
        ILogService logService,
        IPsExecService psExecService,
        IRemoteExecutionService execution)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
        _execution = execution;
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
            // One fresh capability probe per launch; the top-level interactive
            // operation reuses that single plan and falls back only on a transport
            // failure (never after a remote non-zero exit).
            var session = await _execution.CreateSessionAsync(host, cred.UserName, password);
            var result = await session.ExecuteAsync(
                RemoteOperationKind.InteractiveLaunch,
                new RemoteCommand
                {
                    TargetHost = host,
                    Username = cred.UserName,
                    Password = password,
                    Command = command,
                    Shell = CommandShell.Direct,
                    WrapCmd = false,
                });
            if (!result.Success)
                _logService.Error($"启动{description}失败: {result.StdErr}".Trim());
        }
    }

    [RelayCommand] private Task LaunchCmdAsync() => LaunchInteractiveAsync("cmd.exe", "命令行");
    [RelayCommand] private Task LaunchPowerShellAsync() => LaunchInteractiveAsync("powershell.exe", "PowerShell 程序");
    [RelayCommand] private Task LaunchComputerMgmtAsync() => LaunchInteractiveAsync("compmgmt.msc", "计算机管理");
    [RelayCommand] private Task LaunchPrinterMgmtAsync() => LaunchInteractiveAsync("printmanagement.msc", "打印机管理");
    [RelayCommand] private Task LaunchRegEditAsync() => LaunchInteractiveAsync("regedit.exe", "注册表编辑器");
    [RelayCommand] private Task LaunchEnvVarAsync() => LaunchInteractiveAsync("systempropertiesadvanced.exe", "环境变量");
}
