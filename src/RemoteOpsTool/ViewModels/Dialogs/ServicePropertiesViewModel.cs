using System.Collections.ObjectModel;
using System.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class ServicePropertiesViewModel : ObservableObject
{
    private readonly string _host, _username, _password, _serviceName;
    private readonly ISettingsService _settings;
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _log;
    private readonly bool _isLocal;

    private string Rmt(string cmd) => _isLocal ? cmd : $"\\\\{_host} {cmd}";

    public ServicePropertiesViewModel(string host, string username, string password,
        string serviceName, string displayName, ISettingsService settings, IPsExecService psExec,
        IRemoteExecutionService execution, ILogService log)
    {
        _host = host; _username = username; _password = password;
        _serviceName = serviceName; _displayName = displayName;
        _settings = settings; _psExec = psExec; _execution = execution; _log = log;
        _isLocal = HostHelper.IsLocalHost(host);
        _ = LoadAsync();
    }

    [ObservableProperty] private string _displayName = "";
    public string ServiceName => _serviceName;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _binaryPath = "";
    [ObservableProperty] private string _startParams = "";
    [ObservableProperty] private string _selectedStartType = "";
    [ObservableProperty] private string _serviceStatus = "";
    [ObservableProperty] private string _statusColor = "#6C6A64";
    [ObservableProperty] private bool _canStart, _canStop, _canPause, _canResume;
    public List<string> StartTypes { get; } = ["自动", "自动(延迟启动)", "手动", "禁用"];

    [ObservableProperty] private bool _useLocalSystem = true;
    [ObservableProperty] private bool _useThisAccount;
    [ObservableProperty] private string _logOnAccount = "";
    [ObservableProperty] private bool _allowDesktopInteract;

    public List<string> FailureActions { get; } = ["不操作", "重新启动服务", "运行一个程序", "重新启动计算机"];
    [ObservableProperty] private string _firstFailure = "不操作";
    [ObservableProperty] private string _secondFailure = "不操作";
    [ObservableProperty] private string _subsequentFailure = "不操作";
    [ObservableProperty] private string _resetFailDays = "1";
    [ObservableProperty] private string _restartMinutes = "1";

    public ObservableCollection<string> Dependencies { get; } = [];
    public ObservableCollection<string> DependentServices { get; } = [];

    private async Task LoadAsync()
    {
        Dependencies.Clear(); DependentServices.Clear();

        IRemoteExecutionSession? remoteSession = null;
        if (!_isLocal)
            remoteSession = await _execution.CreateSessionAsync(_host, _username, _password);

        if (!_isLocal && await TryLoadViaWmiAsync())
        {
            ExtractStartParams();
            await LoadFailureActionsAsync(remoteSession);
            return;
        }

        // Query config via sc
        if (_isLocal)
        {
            var scResult = await ProcessHelper.RunAsync("sc.exe", $"qc \"{_serviceName}\"");
            if (scResult.Success) ParseScOutput(scResult.StdOut);
            else { _log.Error($"sc qc failed: {scResult.StdErr}"); return; }
        }
        else
        {
            var scResult = await ExecuteRemoteCommandAsync(remoteSession, $"sc qc \"{_serviceName}\"",
                RemoteOperationKind.Inventory, silent: true);
            if (scResult.Success) ParseScOutput(scResult.StdOut);
            else { _log.Error($"sc qc failed: {scResult.StdOut}{Environment.NewLine}{scResult.StdErr}".Trim()); return; }
        }

        // Query status
        if (_isLocal)
        {
            var queryResult = await ProcessHelper.RunAsync("sc.exe", $"query \"{_serviceName}\"");
            if (queryResult.Success) ParseScStatus(queryResult.StdOut);
        }
        else
        {
            var queryResult = await ExecuteRemoteCommandAsync(remoteSession, $"sc query \"{_serviceName}\"",
                RemoteOperationKind.Inventory, silent: true);
            if (queryResult.Success) ParseScStatus(queryResult.StdOut);
        }

        // Description
        if (_isLocal)
        {
            var descResult = await ProcessHelper.RunAsync("sc.exe", $"qdescription \"{_serviceName}\"");
            if (descResult.Success)
                foreach (var l in descResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    if (!l.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) Description = l.Trim();
        }
        else
        {
            var descResult = await ExecuteRemoteCommandAsync(remoteSession, $"sc qdescription \"{_serviceName}\"",
                RemoteOperationKind.Inventory, silent: true);
            if (descResult.Success)
                foreach (var l in descResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    if (!l.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) Description = l.Trim();
        }

        // Dependent services
        if (_isLocal)
        {
            var depResult = await ProcessHelper.RunAsync("sc.exe", $"enumdepend \"{_serviceName}\"");
            if (depResult.Success)
                foreach (var l in depResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                { var tl = l.Trim(); if (!string.IsNullOrWhiteSpace(tl) && !tl.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) DependentServices.Add(tl); }
        }
        else
        {
            var depResult = await ExecuteRemoteCommandAsync(remoteSession, $"sc enumdepend \"{_serviceName}\"",
                RemoteOperationKind.Inventory, silent: true);
            if (depResult.Success)
                foreach (var l in depResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                { var tl = l.Trim(); if (!string.IsNullOrWhiteSpace(tl) && !tl.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) DependentServices.Add(tl); }
        }

        ExtractStartParams();
        await LoadFailureActionsAsync(remoteSession);
    }
    private async Task<bool> TryLoadViaWmiAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                var scope = RemoteWmiHelper.CreateScope(_host, _username, _password);
                scope.Connect();

                var escapedName = RemoteWmiHelper.EscapeWqlString(_serviceName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null)
                {
                    _log.Warn($"WMI 未找到服务: {_serviceName}");
                    return false;
                }

                DisplayName = RemoteWmiHelper.GetString(service, "DisplayName");
                BinaryPath = RemoteWmiHelper.GetString(service, "PathName");
                Description = RemoteWmiHelper.GetString(service, "Description");

                var startName = RemoteWmiHelper.GetString(service, "StartName");
                AllowDesktopInteract = RemoteWmiHelper.GetBool(service, "DesktopInteract");
                if (startName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                    startName.Equals("LocalSystemAccount", StringComparison.OrdinalIgnoreCase))
                {
                    UseLocalSystem = true;
                    UseThisAccount = false;
                }
                else if (!string.IsNullOrWhiteSpace(startName))
                {
                    UseThisAccount = true;
                    UseLocalSystem = false;
                    LogOnAccount = startName;
                }

                SelectedStartType = RemoteWmiHelper.GetString(service, "StartMode") switch
                {
                    "Auto" => "自动",
                    "Manual" => "手动",
                    "Disabled" => "禁用",
                    _ => "手动"
                };

                ParseScStatus($"STATE              : {RemoteWmiHelper.GetString(service, "State")}");
                LoadDependenciesViaWmi(scope);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 服务属性查询失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }

    private void LoadDependenciesViaWmi(ManagementScope scope)
    {
        try
        {
            using var depSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"ASSOCIATORS OF {{Win32_Service.Name='{RemoteWmiHelper.EscapeWqlString(_serviceName)}'}} WHERE AssocClass=Win32_DependentService Role=Dependent"));
            foreach (ManagementObject item in depSearcher.Get())
            {
                var name = RemoteWmiHelper.GetString(item, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                    Dependencies.Add(name);
            }
        }
        catch { }

        try
        {
            using var dependentSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"ASSOCIATORS OF {{Win32_Service.Name='{RemoteWmiHelper.EscapeWqlString(_serviceName)}'}} WHERE AssocClass=Win32_DependentService Role=Antecedent"));
            foreach (ManagementObject item in dependentSearcher.Get())
            {
                var name = RemoteWmiHelper.GetString(item, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                    DependentServices.Add(name);
            }
        }
        catch { }
    }

    private void ParseScOutput(string output)
    {
        foreach (var t in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (t.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("DISPLAY_NAME", StringComparison.OrdinalIgnoreCase))
                DisplayName = Aft(t);
            else if (t.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                BinaryPath = Aft(t);
            else if (t.StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase))
                SelectedStartType = Aft(t) switch
                { var s when s.StartsWith("2") => "自动", var s when s.StartsWith("5") => "自动(延迟启动)", var s when s.StartsWith("3") => "手动", var s when s.StartsWith("4") => "禁用", _ => "手动" };
            else if (t.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                var typeStr = Aft(t).Trim();
                var firstToken = typeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (int.TryParse(firstToken, System.Globalization.NumberStyles.HexNumber, null, out var typeVal))
                    AllowDesktopInteract = (typeVal & 0x100) != 0;
            }
            else if (t.StartsWith("SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase))
            { var acct = Aft(t); if (acct.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) { UseLocalSystem = true; UseThisAccount = false; } else { UseThisAccount = true; UseLocalSystem = false; LogOnAccount = acct; } }
            else if (t.StartsWith("DEPENDENCIES", StringComparison.OrdinalIgnoreCase))
                foreach (var d in Aft(t).Split(':', StringSplitOptions.RemoveEmptyEntries))
                    if (!string.IsNullOrWhiteSpace(d.Trim())) Dependencies.Add(d.Trim());
        }
    }

    private void ParseScStatus(string output)
    {
        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        { ServiceStatus = "运行中"; StatusColor = "#5DB872"; CanStart = false; CanStop = true; CanPause = true; CanResume = false; }
        else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        { ServiceStatus = "已停止"; StatusColor = "#C64545"; CanStart = true; CanStop = false; CanPause = false; CanResume = false; }
        else if (output.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
        { ServiceStatus = "已暂停"; StatusColor = "#8F73D8"; CanStart = false; CanStop = true; CanPause = false; CanResume = true; }
    }

    private static string Aft(string t) => t[(t.IndexOf(':') + 1)..].Trim();

    private void ExtractStartParams()
    {
        if (string.IsNullOrWhiteSpace(BinaryPath)) return;
        var path = BinaryPath.Trim();
        if (path.Length == 0) return;

        if (path.StartsWith('"'))
        {
            var closeQuote = path.IndexOf('"', 1);
            if (closeQuote < 0) return;
            BinaryPath = path[1..closeQuote];
            StartParams = path[(closeQuote + 1)..].Trim();
        }
        else
        {
            var spaceIdx = path.IndexOf(' ');
            if (spaceIdx < 0) return;
            BinaryPath = path[..spaceIdx];
            StartParams = path[(spaceIdx + 1)..].Trim();
        }
    }

    private async Task LoadFailureActionsAsync(IRemoteExecutionSession? remoteSession = null)
    {
        try
        {
            string output;
            if (_isLocal)
            {
                var result = await ProcessHelper.RunAsync("sc.exe", $"qfailure \"{_serviceName}\"");
                if (!result.Success) return;
                output = result.StdOut;
            }
            else
            {
                var result = await ExecuteRemoteCommandAsync(remoteSession, $"sc qfailure \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true);
                if (!result.Success) return;
                output = result.StdOut;
            }
            ParseFailureOutput(output);
        }
        catch { }
    }

    private void ParseFailureOutput(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var actions = new List<(string action, int delay)>();
        int? resetSecs = null;
        bool inFailureActions = false;

        foreach (var rawLine in lines)
        {
            var t = rawLine.Trim();
            if (t.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)) continue;

            if (t.StartsWith("RESET_PERIOD", StringComparison.OrdinalIgnoreCase))
            {
                inFailureActions = false;
                var val = Aft(t).Trim();
                var spaceIdx = val.IndexOf(' ');
                if (spaceIdx > 0 && int.TryParse(val[..spaceIdx], out var secs))
                    resetSecs = secs;
            }
            else if (t.StartsWith("FAILURE_ACTIONS", StringComparison.OrdinalIgnoreCase))
            {
                inFailureActions = true;
                var actionPart = Aft(t).Trim();
                if (!string.IsNullOrWhiteSpace(actionPart) && actionPart.Contains("--"))
                    ParseActionItem(actionPart, actions);
            }
            else if (t.StartsWith("REBOOT_MESSAGE", StringComparison.OrdinalIgnoreCase)) inFailureActions = false;
            else if (t.StartsWith("COMMAND_LINE", StringComparison.OrdinalIgnoreCase)) inFailureActions = false;
            else if (inFailureActions && t.Contains("--") && t.Contains("Delay"))
            {
                ParseActionItem(t, actions);
            }
        }

        if (actions.Count > 0) FirstFailure = ActionToChinese(actions[0].action);
        if (actions.Count > 1) SecondFailure = ActionToChinese(actions[1].action);
        if (actions.Count > 2) SubsequentFailure = ActionToChinese(actions[2].action);

        if (resetSecs.HasValue)
            ResetFailDays = Math.Max(1, resetSecs.Value / 86400).ToString();

        var restartAction = actions.FirstOrDefault(a =>
            a.action.Equals("RESTART", StringComparison.OrdinalIgnoreCase));
        if (restartAction.action != null)
            RestartMinutes = Math.Max(1, restartAction.delay / 60000).ToString();
    }

    private static void ParseActionItem(string line, List<(string action, int delay)> actions)
    {
        var parts = line.Split("--", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return;
        var action = parts[0].Trim();
        var delayMatch = System.Text.RegularExpressions.Regex.Match(parts[1], @"\d+");
        var delay = delayMatch.Success ? int.Parse(delayMatch.Value) : 0;
        actions.Add((action, delay));
    }

    private static string ActionToChinese(string action) => action switch
    {
        "RESTART" => "重新启动服务",
        "RUN_COMMAND" => "运行一个程序",
        "REBOOT" => "重新启动计算机",
        _ => "不操作"
    };

    private async Task<CommandResult> RunScCommandAsync(
        string cmd,
        bool appendServiceName = true,
        IRemoteExecutionSession? remoteSession = null,
        CancellationToken ct = default)
    {
        var arguments = appendServiceName ? $"{cmd} \"{_serviceName}\"" : cmd;
        return _isLocal
            ? await _psExec.ExecuteLocalElevatedAsync(
                _host, _username, _password, $"sc.exe {arguments}", ct, CommandShell.Direct)
            : await ExecuteRemoteCommandAsync(
                remoteSession, $"sc {arguments}", RemoteOperationKind.Command, silent: true, ct: ct);
    }


    private async Task<CommandResult> ExecuteRemoteCommandAsync(
        IRemoteExecutionSession? remoteSession,
        string command,
        RemoteOperationKind operation,
        bool silent = false,
        CancellationToken ct = default)
    {
        remoteSession ??= await _execution.CreateSessionAsync(_host, _username, _password, ct);
        var transportResult = await remoteSession.ExecuteAsync(
            operation,
            new RemoteCommand
            {
                TargetHost = _host,
                Username = _username,
                Password = _password,
                Command = command,
                Shell = CommandShell.Cmd,
                Silent = silent,
            },
            ct: ct);
        return transportResult.Result;
    }

    private async Task<bool> TryRunScCommandAsync(
        string cmd,
        bool appendServiceName = true,
        IRemoteExecutionSession? remoteSession = null)
    {
        var result = await RunScCommandAsync(cmd, appendServiceName, remoteSession: remoteSession);
        if (result.Success) return true;

        var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        _log.Error($"服务操作失败: {_serviceName} command={cmd} exit={result.ExitCode} {error.Trim()}");
        return false;
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        if (!await TryRunScCommandAsync("start")) return;
        _log.Info($"正在启动服务: {_serviceName}");
        await Task.Delay(1500);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        if (!await TryRunScCommandAsync("stop")) return;
        _log.Info($"正在停止服务: {_serviceName}");
        await Task.Delay(1500);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PauseServiceAsync()
    {
        if (!await TryRunScCommandAsync("pause")) return;
        await Task.Delay(1500);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ResumeServiceAsync()
    {
        if (!await TryRunScCommandAsync("continue")) return;
        await Task.Delay(1500);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        await ApplyChangesAsync();
    }

    private async Task<bool> ApplyChangesAsync()
    {
        var st = SelectedStartType switch { "自动" => "auto", "自动(延迟启动)" => "delayed-auto", "手动" => "demand", "禁用" => "disabled", _ => "" };
        IRemoteExecutionSession? mutationSession = _isLocal
            ? null
            : await _execution.CreateSessionAsync(_host, _username, _password);
        if (!string.IsNullOrEmpty(st))
        {
            var binPath = !string.IsNullOrWhiteSpace(StartParams)
                ? $"binPath= \"{BinaryPath} {StartParams}\""
                : "";
            if (!await TryRunScCommandAsync($"config \"{_serviceName}\" start= {st} {binPath}", appendServiceName: false, remoteSession: mutationSession))
                return false;
        }

        if (UseThisAccount && !string.IsNullOrWhiteSpace(LogOnAccount))
        {
            var pwd = GetPasswordFromDialog();
            var a = $"config \"{_serviceName}\" obj= \"{LogOnAccount}\" password= \"{pwd}\"";
            if (AllowDesktopInteract) a += " type= interact type= own";
            if (!await TryRunScCommandAsync(a, appendServiceName: false, remoteSession: mutationSession))
                return false;
        }
        else if (UseLocalSystem)
        {
            var a = $"config \"{_serviceName}\" obj= \"LocalSystem\"";
            if (AllowDesktopInteract) a += " type= interact type= own";
            if (!await TryRunScCommandAsync(a, appendServiceName: false, remoteSession: mutationSession))
                return false;
        }

        var fa1 = FailNum(FirstFailure); var fa2 = FailNum(SecondFailure); var fa3 = FailNum(SubsequentFailure);
        var rd = int.TryParse(ResetFailDays, out var rdays) ? rdays : 1;
        var rm = int.TryParse(RestartMinutes, out var rmins) ? rmins : 1;
        var failureCmd = $"failure \"{_serviceName}\" actions= {fa1}/{fa2}/{fa3} reset= {rdays * 86400} reboot= {rmins * 60000}";
        if (!await TryRunScCommandAsync(failureCmd, appendServiceName: false, remoteSession: mutationSession))
            return false;

        _log.Info($"服务 {_serviceName} 属性已应用");
        await LoadAsync();
        return true;
    }

    private string GetPasswordFromDialog()
    {
        var dlg = System.Windows.Application.Current.Windows.OfType<ServicePropertiesDialog>().FirstOrDefault();
        return (dlg?.FindName("LogOnPassword") as System.Windows.Controls.PasswordBox)?.Password ?? "";
    }

    [RelayCommand]
    private async Task BrowseAccountAsync()
    {
        var accounts = await QueryLocalAccountsAsync();
        if (accounts.Length == 0) return;

        var dlg = new AccountPickerDialog(_host, accounts);
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedAccount))
        {
            LogOnAccount = $".\\{dlg.SelectedAccount}";
            UseThisAccount = true;
            UseLocalSystem = false;
        }
    }

    private async Task<string[]> QueryLocalAccountsAsync()
    {
        try
        {
            if (_isLocal)
            {
                var result = await ProcessHelper.RunAsync("cmd.exe", "/c net user");
                if (!result.Success) return [];
                return ParseNetUserOutput(result.StdOut);
            }

            var psResult = await ExecuteRemoteCommandAsync(
                null, "cmd /c \"net user\"", RemoteOperationKind.Inventory, silent: true);
            if (psResult.Success)
                return ParseNetUserOutput(psResult.StdOut);
        }
        catch { }
        return [];
    }

    private static string[] ParseNetUserOutput(string output)
    {
        var accounts = new List<string>();
        var inUsers = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("---")) { inUsers = true; continue; }
            if (!inUsers) continue;
            if (t.StartsWith("命令成功") || t.StartsWith("The command")) break;

            var parts = t.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
                if (part.Length > 1 && !string.IsNullOrWhiteSpace(part))
                    accounts.Add(part);
        }
        return accounts.ToArray();
    }

    [RelayCommand]
    private async Task OkAsync()
    {
        if (!await ApplyChangesAsync()) return;

        var dlg = System.Windows.Application.Current.Windows.OfType<ServicePropertiesDialog>().FirstOrDefault();
        if (dlg != null)
        {
            dlg.DialogResult = true;
            dlg.Close();
        }
    }

    private static int FailNum(string a) => a switch { "重新启动服务" => 1, "运行一个程序" => 2, "重新启动计算机" => 3, _ => 0 };
}
