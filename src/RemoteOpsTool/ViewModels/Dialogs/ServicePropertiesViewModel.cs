using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class ServicePropertiesViewModel : ObservableObject
{
    private readonly string _host, _username, _password, _serviceName;
    private readonly ISettingsService _settings;
    private readonly IPsExecService _psExec;
    private readonly ILogService _log;
    private readonly bool _isLocal;

    private string Rmt(string cmd) => _isLocal ? cmd : $"\\\\{_host} {cmd}";

    public ServicePropertiesViewModel(string host, string username, string password,
        string serviceName, string displayName, ISettingsService settings, IPsExecService psExec, ILogService log)
    {
        _host = host; _username = username; _password = password;
        _serviceName = serviceName; _displayName = displayName;
        _settings = settings; _psExec = psExec; _log = log;
        _isLocal = string.IsNullOrEmpty(host)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || host is "localhost" or "127.0.0.1" or "::1" or ".";
        _ = LoadAsync();
    }

    [ObservableProperty] private string _displayName = "";
    public string ServiceName => _serviceName;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _binaryPath = "";
    [ObservableProperty] private string _startParams = "";
    [ObservableProperty] private string _selectedStartType = "";
    [ObservableProperty] private string _serviceStatus = "";
    [ObservableProperty] private string _statusColor = "#AAA";
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

        // Query config via sc
        if (_isLocal)
        {
            var scResult = await ProcessHelper.RunAsync("sc.exe", $"qc \"{_serviceName}\"");
            if (scResult.Success) ParseScOutput(scResult.StdOut);
            else { _log.Error($"sc qc failed: {scResult.StdErr}"); return; }
        }
        else
        {
            var scResult = await _psExec.ExecuteAsync(_host, _username, _password,
                $"sc qc \"{_serviceName}\"", silent: true);
            if (scResult.Success) ParseScOutput(scResult.StdOut);
            else { _log.Error($"sc qc failed: {scResult.StdErr}"); return; }
        }

        // Query status
        if (_isLocal)
        {
            var queryResult = await ProcessHelper.RunAsync("sc.exe", $"query \"{_serviceName}\"");
            if (queryResult.Success) ParseScStatus(queryResult.StdOut);
        }
        else
        {
            var queryResult = await _psExec.ExecuteAsync(_host, _username, _password,
                $"sc query \"{_serviceName}\"", silent: true);
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
            var descResult = await _psExec.ExecuteAsync(_host, _username, _password,
                $"sc qdescription \"{_serviceName}\"", silent: true);
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
            var depResult = await _psExec.ExecuteAsync(_host, _username, _password,
                $"sc enumdepend \"{_serviceName}\"", silent: true);
            if (depResult.Success)
                foreach (var l in depResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                { var tl = l.Trim(); if (!string.IsNullOrWhiteSpace(tl) && !tl.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) DependentServices.Add(tl); }
        }
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
        { ServiceStatus = "运行中"; StatusColor = "#4ECB71"; CanStart = false; CanStop = true; CanPause = true; CanResume = false; }
        else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        { ServiceStatus = "已停止"; StatusColor = "#FF6B6B"; CanStart = true; CanStop = false; CanPause = false; CanResume = false; }
        else if (output.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
        { ServiceStatus = "已暂停"; StatusColor = "#FFD700"; CanStart = false; CanStop = true; CanPause = false; CanResume = true; }
    }

    private static string Aft(string t) => t[(t.IndexOf(':') + 1)..].Trim();

    private async Task RunScCommandAsync(string cmd)
    {
        if (_isLocal)
            await ProcessHelper.RunAsync("sc.exe", $"{cmd} \"{_serviceName}\"");
        else
            await _psExec.ExecuteAsync(_host, _username, _password, $"sc {cmd} \"{_serviceName}\"", silent: true);
    }

    [RelayCommand] private async Task StartServiceAsync() { await RunScCommandAsync("start"); _log.Info($"正在启动服务: {_serviceName}"); await Task.Delay(1500); await LoadAsync(); }
    [RelayCommand] private async Task StopServiceAsync() { await RunScCommandAsync("stop"); _log.Info($"正在停止服务: {_serviceName}"); await Task.Delay(1500); await LoadAsync(); }
    [RelayCommand] private async Task PauseServiceAsync() { await RunScCommandAsync("pause"); await Task.Delay(1500); await LoadAsync(); }
    [RelayCommand] private async Task ResumeServiceAsync() { await RunScCommandAsync("continue"); await Task.Delay(1500); await LoadAsync(); }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var st = SelectedStartType switch { "自动" => "auto", "自动(延迟启动)" => "delayed-auto", "手动" => "demand", "禁用" => "disabled", _ => "" };
        if (!string.IsNullOrEmpty(st))
        {
            var binPath = !string.IsNullOrWhiteSpace(StartParams)
                ? $"binPath= \"{BinaryPath} {StartParams}\""
                : "";
            if (_isLocal)
                await ProcessHelper.RunAsync("sc.exe", $"config \"{_serviceName}\" start= {st} {binPath}");
            else
                await _psExec.ExecuteAsync(_host, _username, _password,
                    $"sc config \"{_serviceName}\" start= {st} {binPath}", silent: true);
        }

        if (UseThisAccount && !string.IsNullOrWhiteSpace(LogOnAccount))
        {
            var pwd = GetPasswordFromDialog();
            var a = $"config \"{_serviceName}\" obj= \"{LogOnAccount}\" password= \"{pwd}\"";
            if (AllowDesktopInteract) a += " type= interact type= own";
            if (_isLocal)
                await ProcessHelper.RunAsync("sc.exe", a);
            else
                await _psExec.ExecuteAsync(_host, _username, _password, $"sc {a}", silent: true);
        }
        else if (UseLocalSystem)
        {
            var a = $"config \"{_serviceName}\" obj= \"LocalSystem\"";
            if (AllowDesktopInteract) a += " type= interact type= own";
            if (_isLocal)
                await ProcessHelper.RunAsync("sc.exe", a);
            else
                await _psExec.ExecuteAsync(_host, _username, _password, $"sc {a}", silent: true);
        }

        var fa1 = FailNum(FirstFailure); var fa2 = FailNum(SecondFailure); var fa3 = FailNum(SubsequentFailure);
        var rd = int.TryParse(ResetFailDays, out var rdays) ? rdays : 1;
        var rm = int.TryParse(RestartMinutes, out var rmins) ? rmins : 1;
        var failureCmd = $"failure \"{_serviceName}\" actions= {fa1}/{fa2}/{fa3} reset= {rdays * 86400} reboot= {rmins * 60000}";
        if (_isLocal)
            await ProcessHelper.RunAsync("sc.exe", failureCmd);
        else
            await _psExec.ExecuteAsync(_host, _username, _password, $"sc {failureCmd}", silent: true);

        _log.Info($"服务 {_serviceName} 属性已应用");
        await LoadAsync();
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

            var psResult = await _psExec.ExecuteAsync(_host, _username, _password,
                "cmd /c \"net user\"", silent: true);
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
    private void Ok() { _ = ApplyAsync(); var dlg = System.Windows.Application.Current.Windows.OfType<ServicePropertiesDialog>().FirstOrDefault(); if (dlg != null) { dlg.DialogResult = true; dlg.Close(); } }

    private static int FailNum(string a) => a switch { "重新启动服务" => 1, "运行一个程序" => 2, "重新启动计算机" => 3, _ => 0 };
}
