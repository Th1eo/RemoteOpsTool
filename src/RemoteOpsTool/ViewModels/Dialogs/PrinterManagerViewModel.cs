using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class PrinterManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IPrinterService _printerService;
    private readonly IPsExecService _psExecService;
    private readonly ILogService _logService;
    private readonly ICacheService _cache;

    // 添加向导在管理端本机运行，需要后台守卫判断安装结果；采用退避轮询，避免持续打印日志。
    private static readonly TimeSpan AddWizardGuardTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AddWizardGuardInitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AddWizardGuardMaxDelay = TimeSpan.FromSeconds(60);

    [ObservableProperty] private PrinterRow? _selectedPrinter;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _canSetDefault = true;
    [ObservableProperty] private string _lastRefreshText = "尚未刷新";
    public PrinterRow? RightClickedRow { get; set; }

    public ObservableCollection<PrinterRow> Printers { get; } = [];
    public ObservableCollection<PrinterRow> FilteredPrinters { get; } = [];

    public PrinterManagerViewModel(MainViewModel main, IPrinterService printerService,
        IPsExecService psExecService, ILogService logService, ICacheService cache)
    {
        _main = main; _printerService = printerService;
        _psExecService = psExecService;
        _logService = logService; _cache = cache;
        _ = LoadPrintersAsync();
    }

    private async Task LoadPrintersAsync(string? selectName = null, bool force = false)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            if (!force)
                await _cache.PopulateFromCacheAsync<List<PrinterInfo>>(host, CacheKeys.Printers, list => PopulatePrinters(list, null));

            if (force || !await _cache.HasValidCacheAsync(host, CacheKeys.Printers))
            {
                var list = await _printerService.GetPrintersAsync(host, cred.UserName, password ?? string.Empty);
                await _cache.SaveAndPopulateAsync(host, CacheKeys.Printers, list, l => PopulatePrinters(l, selectName));
            }

            LastRefreshText = _cache.GetCacheAge(host, CacheKeys.Printers) is string age ? $"缓存于 {age}" : "尚未刷新";
        }
        finally { IsLoading = false; }
    }

    private void PopulatePrinters(List<PrinterInfo> list, string? selectName)
    {
        foreach (var r in Printers) r.PropertyChanged -= OnPrinterRowPropertyChanged;
        Printers.Clear();
        PrinterRow? toSelect = null;
        foreach (var p in list)
        {
            var row = new PrinterRow { Printer = p };
            row.PropertyChanged += OnPrinterRowPropertyChanged;
            Printers.Add(row);
            if (selectName != null && p.Name == selectName) toSelect = row;
        }
        ApplyFilter();
        if (toSelect != null) SelectedPrinter = toSelect;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredPrinters.Clear();
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? Printers
            : Printers.Where(p => p.Printer.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        foreach (var p in filtered) FilteredPrinters.Add(p);
    }

    private void OnPrinterRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrinterRow.IsChecked))
            CanSetDefault = Printers.Count(p => p.IsChecked) == 1;
    }

    [RelayCommand] private async Task RefreshAsync() => await LoadPrintersAsync(force: true);

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var rightClickedRow = RightClickedRow;
        RightClickedRow = null;
        var checkedItems = rightClickedRow != null
            ? [rightClickedRow]
            : Printers.Where(p => p.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        foreach (var item in checkedItems)
            await _printerService.RemovePrinterAsync(host, cred.UserName, password ?? string.Empty, item.Printer.Name);
        InvalidatePrinterCache(host);
        await LoadPrintersAsync();
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        var rightClickedRow = RightClickedRow;
        RightClickedRow = null;
        if (rightClickedRow != null)
        {
            await SetDefaultPrinterAsync(rightClickedRow);
            return;
        }

        var checkedItems = Printers.Where(p => p.IsChecked).ToList();
        if (checkedItems.Count != 1) return;
        await SetDefaultPrinterAsync(checkedItems[0]);
    }

    public async Task SharePrinterAsync(PrinterRow row)
    {
        var name = row.Printer.Name;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var updated = await _printerService.SetPrinterSharedAsync(
            host, cred.UserName, password ?? string.Empty, name, !row.Printer.Shared);
        if (!updated) return;

        InvalidatePrinterCache(host);
        await LoadPrintersAsync(name);
    }

    public async Task SetDefaultPrinterAsync(PrinterRow row)
    {
        if (row.Printer.Default)
        {
            await ClearDefaultPrinterAsync(row);
            return;
        }

        var name = row.Printer.Name;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var setDefault = await _printerService.SetDefaultPrinterAsync(host, cred.UserName, password ?? string.Empty, name);
        if (!setDefault) return;

        InvalidatePrinterCache(host);
        await LoadPrintersAsync(name);
    }

    private async Task ClearDefaultPrinterAsync(PrinterRow row)
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var cleared = await _printerService.ClearDefaultPrinterAsync(host, cred.UserName, password ?? string.Empty);
        if (!cleared) return;

        InvalidatePrinterCache(host);
        await LoadPrintersAsync(row.Printer.Name);
    }

    [RelayCommand]
    private async Task OpenPrinterProperties()
    {
        _logService.Info("正在打开打印机属性...");
        try
        {
            var rightClickedRow = RightClickedRow;
            RightClickedRow = null;
            var printer = rightClickedRow ?? Printers.FirstOrDefault(p => p.IsChecked);
            if (printer == null)
            {
                _logService.Warn("打开打印机属性: 未选中任何打印机");
                return;
            }
            var printerName = printer.Printer.Name;
            var host = _main.GetTargetHost();
            InvalidatePrinterCache(host);
            _logService.Debug($"打印机属性: printer={printerName} host={host}");

            var isLocal = HostHelper.IsLocalHost(host);

            if (isLocal)
            {
                _logService.Debug($"打印机属性: 本机模式 rundll32 printui.dll,PrintUIEntry /p /n \"{printerName}\"");
                var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
                if (cred == null)
                {
                    _logService.Warn("打开打印机属性: 未选择凭据，无法启动");
                    return;
                }

                var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
                var result = await _psExecService.ExecuteInteractiveLocalAsync(
                    $"rundll32.exe printui.dll,PrintUIEntry /p /n \"{printerName}\"",
                    cred.UserName,
                    password,
                    shell: CommandShell.Direct);
                if (!result.Success)
                {
                    _logService.Error($"打开本机打印机属性失败: {result.StdErr}");
                    return;
                }
                _logService.Info($"已打开打印机属性(本机): {printerName}");
            }
            else
            {
                var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
                if (cred == null)
                {
                    _logService.Warn("打开打印机属性: 未选择凭据，无法启动");
                    return;
                }
                var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
                _logService.Debug($"打印机属性: 在管理端本机打开目标主机原生窗口 target={host} user={cred.UserName} printer={printerName}");

                var result = PrinterManagementHelper.OpenRemoteProperties(
                    host,
                    cred.UserName,
                    password,
                    printerName);
                if (!result.Success)
                {
                    _logService.Error($"在管理端打开目标打印机属性失败: {result.StdErr}");
                    return;
                }

                _logService.Info($"已在本机打开目标打印机属性: host={host} printer={printerName}");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"打开打印机属性失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenAddPrinterWizard()
    {
        _logService.Info("正在打开添加打印机向导...");
        try
        {
            var host = _main.GetTargetHost();
            InvalidatePrinterCache(host);
            _logService.Debug($"添加打印机向导: host={host}");

            var isLocal = HostHelper.IsLocalHost(host);

            if (isLocal)
            {
                _logService.Debug("添加打印机向导: 本机模式启动 printui.exe /il");
                var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
                if (cred == null)
                {
                    _logService.Warn("添加打印机向导: 未选择凭据，无法启动");
                    return;
                }

                var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
                var result = await _psExecService.ExecuteInteractiveLocalAsync(
                    "printui.exe /il",
                    cred.UserName,
                    password,
                    shell: CommandShell.Direct);
                if (!result.Success)
                {
                    _logService.Error($"启动本机添加打印机向导失败: {result.StdErr}");
                    return;
                }
                _logService.Info("已启动添加打印机向导(本机)。");
            }
            else
            {
                var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
                if (cred == null)
                {
                    _logService.Warn("添加打印机向导: 未选择凭据，无法启动");
                    return;
                }
                var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
                _logService.Debug($"添加打印机向导: 在管理端本机打开目标打印后台处理程序 target={host} user={cred.UserName}");

                // 先对目标主机和管理端本机各取一次打印机快照，向导结束后由后台守卫
                // 判定安装究竟落在目标主机，还是被向导的某个分支静默装到了管理端本机。
                var snapshot = new AddPrinterWizardSnapshot(
                    await GetPrinterNamesAsync(host, cred.UserName, password),
                    await GetPrinterNamesAsync(Environment.MachineName, cred.UserName, password));

                var result = PrinterManagementHelper.OpenRemoteAddWizard(host, cred.UserName, password);
                if (!result.Success)
                {
                    _logService.Error($"在管理端启动目标打印机添加向导失败: {result.StdErr}");
                    return;
                }

                _logService.Info($"已在管理端本机启动添加打印机向导（连接目标打印后台处理程序）: host={host}");
                _ = GuardAddPrinterWizardOutcomeAsync(host, cred.UserName, password, snapshot);
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"启动添加打印机向导失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 采集某台主机的打印机名称集合；查询失败时返回空集合。
    /// </summary>
    private async Task<IReadOnlyList<string>> GetPrinterNamesAsync(string host, string username, string password)
    {
        try
        {
            var printers = await _printerService.GetPrintersAsync(host, username, password);
            return printers
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
        }
        catch (Exception ex)
        {
            _logService.Debug($"添加打印机向导快照查询失败: host={host} - {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 添加向导在管理端本机启动并由目标主机的打印后台处理程序执行安装，但向导中的
    /// 部分分支（例如“添加本地打印机”）会忽略 /c 把队列装到管理端本机。这里在后台
    /// 按退避间隔比对两台机器的打印机集合：目标主机新增即为正常；只有管理端新增则
    /// 明确告警，提示用户删除误装的队列。
    /// </summary>
    private async Task GuardAddPrinterWizardOutcomeAsync(
        string host,
        string username,
        string password,
        AddPrinterWizardSnapshot snapshot)
    {
        try
        {
            var deadline = DateTime.UtcNow + AddWizardGuardTimeout;
            var delay = AddWizardGuardInitialDelay;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(delay);
                if (DateTime.UtcNow >= deadline)
                    break;

                var targetAfter = await GetPrinterNamesAsync(host, username, password);
                var localAfter = await GetPrinterNamesAsync(Environment.MachineName, username, password);

                var outcome = PrinterManagementHelper.EvaluateAddWizardOutcome(
                    snapshot.TargetPrinters,
                    targetAfter,
                    snapshot.LocalPrinters,
                    localAfter,
                    out var addedTarget,
                    out var addedLocal);

                switch (outcome)
                {
                    case PrinterAddWizardOutcome.TargetUpdated:
                        _logService.Info(
                            $"添加打印机向导已在目标主机完成安装: host={host} printer={string.Join(", ", addedTarget)}");
                        InvalidatePrinterCache(host);
                        await RefreshPrintersOnUiThreadAsync(addedTarget[0]);
                        return;

                    case PrinterAddWizardOutcome.InstalledOnLocalMachine:
                        _logService.Warn(
                            $"检测到打印机被安装到管理端本机而不是目标主机 {host}: {string.Join(", ", addedLocal)}。" +
                            "该向导的部分分支会忽略 /c 参数；请先删除管理端误装的打印机，再改用“添加网络/共享打印机”或在目标主机上直接安装。");
                        return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, AddWizardGuardMaxDelay.TotalSeconds));
            }

            _logService.Debug(
                $"添加打印机向导在 {AddWizardGuardTimeout.TotalMinutes:0} 分钟内未检测到打印机变化（可能已取消或仍在等待输入）: host={host}");
        }
        catch (Exception ex)
        {
            _logService.Debug($"添加打印机向导结果校验失败: host={host} - {ex.Message}");
        }
    }

    private async Task RefreshPrintersOnUiThreadAsync(string? selectName)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            await LoadPrintersAsync(selectName, force: true);
            return;
        }

        await dispatcher.InvokeAsync(() => LoadPrintersAsync(selectName, force: true)).Task.Unwrap();
    }

    private void InvalidatePrinterCache(string host) => _cache.Invalidate(host, CacheKeys.Printers);
}

public partial class PrinterRow : ObservableObject
{
    public PrinterInfo Printer { get; set; } = null!;
    [ObservableProperty] private bool _isChecked;
}

internal sealed record AddPrinterWizardSnapshot(
    IReadOnlyList<string> TargetPrinters,
    IReadOnlyList<string> LocalPrinters);
