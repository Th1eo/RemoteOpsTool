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
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _logService;
    private readonly ICacheService _cache;

    [ObservableProperty] private PrinterRow? _selectedPrinter;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _canSetDefault = true;
    [ObservableProperty] private string _lastRefreshText = "尚未刷新";
    public PrinterRow? RightClickedRow { get; set; }

    public ObservableCollection<PrinterRow> Printers { get; } = [];
    public ObservableCollection<PrinterRow> FilteredPrinters { get; } = [];

    public PrinterManagerViewModel(MainViewModel main, IPrinterService printerService,
        IPsExecService psExecService, IRemoteExecutionService execution,
        ILogService logService, ICacheService cache)
    {
        _main = main; _printerService = printerService;
        _psExecService = psExecService; _execution = execution;
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

            var targetName = isLocal ? printerName : $"\\\\{host}\\{printerName}";

            if (isLocal)
            {
                _logService.Debug($"打印机属性: 本机模式 rundll32 printui.dll,PrintUIEntry /p /n \"{targetName}\"");
                var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
                if (cred == null)
                {
                    _logService.Warn("打开打印机属性: 未选择凭据，无法启动");
                    return;
                }

                var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
                var result = await _psExecService.ExecuteInteractiveLocalAsync(
                    $"rundll32.exe printui.dll,PrintUIEntry /p /n \"{targetName}\"",
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
                var password = _main.Connection.CredentialService.DecryptPassword(cred);
                _logService.Debug($"打印机属性: 远程 目标={host} 用户={cred.UserName} printer={targetName}");

                var result = await _execution.ExecuteOnceAsync(
                    host,
                    cred.UserName,
                    password ?? string.Empty,
                    $"rundll32.exe printui.dll,PrintUIEntry /p /n \"{targetName}\"",
                    RemoteOperationKind.InteractiveLaunch,
                    CommandShell.Direct,
                    wrapCmd: false,
                    interactiveSession: true);
                if (!result.Success)
                {
                    _logService.Error($"打开远程打印机属性失败: {result.StdErr}");
                    return;
                }
                _logService.Info($"已打开打印机属性: host={host} printer={printerName}");
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
                var password = _main.Connection.CredentialService.DecryptPassword(cred);
                _logService.Debug($"网络打印机安装向导: 目标={host} 用户={cred.UserName}");

                var result = await _execution.ExecuteOnceAsync(
                    host,
                    cred.UserName,
                    password ?? string.Empty,
                    $"rundll32.exe printui.dll,PrintUIEntry /ip /c\\\\{host}",
                    RemoteOperationKind.InteractiveLaunch,
                    CommandShell.Direct,
                    wrapCmd: false,
                    interactiveSession: true);
                if (!result.Success)
                {
                    _logService.Error($"启动网络打印机安装向导失败: {result.StdErr}");
                    return;
                }
                _logService.Info($"已启动网络打印机安装向导: host={host}");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"启动添加打印机向导失败: {ex.Message}");
        }
    }


    private void InvalidatePrinterCache(string host) => _cache.Invalidate(host, CacheKeys.Printers);
}

public partial class PrinterRow : ObservableObject
{
    public PrinterInfo Printer { get; set; } = null!;
    [ObservableProperty] private bool _isChecked;
}
