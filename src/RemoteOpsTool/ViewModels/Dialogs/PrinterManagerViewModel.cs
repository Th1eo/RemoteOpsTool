using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class PrinterManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IPrinterService _printerService;
    private readonly IPsExecService _psExecService;
    private readonly ILogService _logService;

    [ObservableProperty] private PrinterRow? _selectedPrinter;
    [ObservableProperty] private string _newPrinterConnection = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _canSetDefault = true;

    public ObservableCollection<PrinterRow> Printers { get; } = [];
    public ObservableCollection<PrinterRow> FilteredPrinters { get; } = [];

    public PrinterManagerViewModel(MainViewModel main, IPrinterService printerService,
        IPsExecService psExecService, ILogService logService)
    {
        _main = main; _printerService = printerService;
        _psExecService = psExecService; _logService = logService;
        _ = LoadPrintersAsync();
    }

    private async Task LoadPrintersAsync(string? selectName = null)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var list = await _printerService.GetPrintersAsync(host, cred.UserName, password ?? string.Empty);
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
        finally { IsLoading = false; }
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

    [RelayCommand] private async Task RefreshAsync() => await LoadPrintersAsync();

    [RelayCommand]
    private async Task AddPrinterAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPrinterConnection)) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var sessionId = await _psExecService.GetActiveSessionIdAsync(host, cred.UserName, password ?? string.Empty);
        await _printerService.AddPrinterAsync(host, cred.UserName, password ?? string.Empty, NewPrinterConnection, sessionId);
        await LoadPrintersAsync(NewPrinterConnection);
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var checkedItems = Printers.Where(p => p.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        foreach (var item in checkedItems)
            await _printerService.RemovePrinterAsync(host, cred.UserName, password ?? string.Empty, item.Printer.Name);
        await LoadPrintersAsync();
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        var checkedItems = Printers.Where(p => p.IsChecked).ToList();
        if (checkedItems.Count != 1) return;
        var name = checkedItems[0].Printer.Name;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var sessionId = await _psExecService.GetActiveSessionIdAsync(host, cred.UserName, password ?? string.Empty);
        await _printerService.SetDefaultPrinterAsync(host, cred.UserName, password ?? string.Empty, name, sessionId);
        await LoadPrintersAsync(name);
    }

    [RelayCommand]
    private void OpenPrinterProperties()
    {
        var checkedItems = Printers.Where(p => p.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        var printer = checkedItems[0];
        var printerName = printer.Printer.Name;
        var host = _main.GetTargetHost();
        var isLocal = string.IsNullOrEmpty(host) || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || host is "localhost" or "127.0.0.1" or ".";

        var targetName = isLocal ? printerName : $"\\\\{host}\\{printerName}";

        if (isLocal)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /p /n \"{targetName}\"",
                UseShellExecute = true
            });
        }
        else
        {
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var (runAsUser, runAsDomain) = RemoteOpsTool.Helpers.ProcessHelper.SplitUserDomain(cred.UserName);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /p /n \"{targetName}\"",
                UseShellExecute = false,
                UserName = runAsUser,
                Domain = runAsDomain,
                LoadUserProfile = true
            };
            if (!string.IsNullOrEmpty(password))
                psi.Password = ToSecureString(password);
            System.Diagnostics.Process.Start(psi);
        }
    }

    private static System.Security.SecureString ToSecureString(string pwd)
    {
        var ss = new System.Security.SecureString();
        foreach (var c in pwd) ss.AppendChar(c);
        return ss;
    }
}

public partial class PrinterRow : ObservableObject
{
    public PrinterInfo Printer { get; set; } = null!;
    [ObservableProperty] private bool _isChecked;
}
