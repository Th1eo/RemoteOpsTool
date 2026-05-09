using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class SoftwareManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ISoftwareService _softwareService;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;

    [ObservableProperty] private SoftwareRow? _selectedSoftware;
    [ObservableProperty] private bool _deepCleanup;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _canUninstall = true;
    [ObservableProperty] private bool _canDeleteRegistry;

    public ObservableCollection<SoftwareRow> Software { get; } = [];
    public ObservableCollection<SoftwareRow> FilteredSoftware { get; } = [];

    public SoftwareManagerViewModel(MainViewModel main, ISoftwareService softwareService, ILogService logService, IPsExecService psExecService)
    {
        _main = main; _softwareService = softwareService; _logService = logService; _psExecService = psExecService;
        _ = LoadSoftwareAsync();
    }

    private async Task LoadSoftwareAsync(string? selectName = null)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var list = await _softwareService.GetInstalledSoftwareAsync(host, cred.UserName, password ?? string.Empty);

            if (DeepCleanup)
            {
                var deepKeys = new[] {
                    @"HKLM:\Software\Classes\Installer\Products",
                    @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products"
                };
                foreach (var regKey in deepKeys)
                {
                    var psCmd = "powershell \"Get-ChildItem '" + regKey + @"' -ErrorAction SilentlyContinue | ForEach-Object { $item = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue; [PSCustomObject]@{ PSChildName = $_.PSChildName; DisplayName = if($item.DisplayName){$item.DisplayName}else{$_.PSChildName}; UninstallString = if($item.UninstallString){$item.UninstallString}else{''}; Publisher = if($item.Publisher){$item.Publisher}else{''}; InstallLocation = if($item.InstallLocation){$item.InstallLocation}else{''}; ProductName = if($item.ProductName){$item.ProductName}else{''} } } | ConvertTo-Csv -NoTypeInformation""";
                    var result = await _psExecService.ExecuteAsync(host, cred.UserName, password ?? string.Empty, psCmd);
                    if (!result.Success) continue;
                    var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("\"PSChildName\"")) continue;
                        var parts = line.Split(',');
                        if (parts.Length < 6) continue;
                        var childName = parts[0].Trim('"');
                        var displayName = parts[1].Trim('"');
                        var uninstall = parts[2].Trim('"');
                        var productName = parts[5].Trim('"');
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = childName;
                        if (!string.IsNullOrWhiteSpace(productName) && displayName == childName)
                            displayName = productName;
                        var key = $"{regKey}\\{childName}";
                        if (list.All(s => s.RegistryKey != key))
                            list.Add(new SoftwareInfo
                            {
                                DisplayName = displayName,
                                UninstallString = uninstall,
                                Publisher = parts[3].Trim('"'),
                                InstallLocation = parts[4].Trim('"'),
                                RegistryKey = key
                            });
                    }
                }
            }

            foreach (var r in Software) r.PropertyChanged -= OnSoftwareRowPropertyChanged;
            Software.Clear();
            SoftwareRow? toSelect = null;
            foreach (var s in list)
            {
                var row = new SoftwareRow { Software = s };
                row.PropertyChanged += OnSoftwareRowPropertyChanged;
                Software.Add(row);
                if (selectName != null && s.DisplayName == selectName) toSelect = row;
            }
            ApplyFilter();
            if (toSelect != null) SelectedSoftware = toSelect;
        }
        finally { IsLoading = false; }
    }

    partial void OnDeepCleanupChanged(bool value)
    {
        CanUninstall = !value;
        CanDeleteRegistry = value && Software.Any(s => s.IsChecked);
        _ = LoadSoftwareAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredSoftware.Clear();
        var search = SearchText?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(search)
            ? Software
            : Software.Where(s =>
                (s.Software.DisplayName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (s.Software.RegistryKey?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (s.Software.UninstallString?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (s.Software.Publisher?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        foreach (var s in filtered) FilteredSoftware.Add(s);
    }

    private void OnSoftwareRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareRow.IsChecked))
        {
            var count = Software.Count(s => s.IsChecked);
            CanDeleteRegistry = DeepCleanup && count > 0;
        }
    }

    [RelayCommand] private async Task RefreshAsync() => await LoadSoftwareAsync();

    [RelayCommand]
    private async Task UninstallSilentAsync()
    {
        var checkedItems = Software.Where(s => s.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        foreach (var item in checkedItems)
        {
            if (string.IsNullOrWhiteSpace(item.Software.UninstallString)) continue;
            await _softwareService.UninstallSilentlyAsync(host, cred.UserName, password ?? string.Empty, item.Software.UninstallString);
        }
        await LoadSoftwareAsync();
    }

    [RelayCommand]
    private async Task UninstallInteractiveAsync()
    {
        var checkedItems = Software.Where(s => s.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var sessionId = 1;
        foreach (var item in checkedItems)
        {
            if (string.IsNullOrWhiteSpace(item.Software.UninstallString)) continue;
            await _softwareService.UninstallInteractiveAsync(host, cred.UserName, password ?? string.Empty,
                item.Software.UninstallString, sessionId);
        }
        await LoadSoftwareAsync();
    }

    [RelayCommand]
    private void CopyRegistryKey()
    {
        var checkedItems = Software.Where(s => s.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        var text = checkedItems[0].Software.RegistryKey;
        if (!string.IsNullOrWhiteSpace(text))
            System.Windows.Clipboard.SetText(text);
    }

    [RelayCommand]
    private void CopyUninstallString()
    {
        var checkedItems = Software.Where(s => s.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        System.Windows.Clipboard.SetText(checkedItems[0].Software.UninstallString);
    }

    [RelayCommand]
    private async Task DeleteRegistryKeysAsync()
    {
        var checkedItems = Software.Where(s => s.IsChecked).ToList();
        if (checkedItems.Count == 0) return;
        if (!DeepCleanup) return;

        var result = System.Windows.MessageBox.Show(
            $"确定要删除目标主机上 {checkedItems.Count} 个注册表键吗？\n\n此操作不可逆，请谨慎操作！",
            "危险操作确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        foreach (var item in checkedItems)
        {
            var key = item.Software.RegistryKey;
            if (string.IsNullOrWhiteSpace(key)) continue;
            // Convert PSDrive path to reg.exe format
            var regPath = key.Replace(@"HKLM:\", @"HKLM\");
            var cmd = $"reg delete \"{regPath}\" /f";
            await _psExecService.ExecuteAsync(host, cred.UserName, password ?? string.Empty, cmd);
        }

        // Refresh after deletion
        if (DeepCleanup)
            await LoadSoftwareAsync();
    }
}

public partial class SoftwareRow : ObservableObject
{
    public SoftwareInfo Software { get; set; } = null!;
    [ObservableProperty] private bool _isChecked;
}
