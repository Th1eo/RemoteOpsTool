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
    private readonly ICacheService _cache;

    [ObservableProperty] private SoftwareRow? _selectedSoftware;
    [ObservableProperty] private bool _deepCleanup;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _lastRefreshText = "尚未刷新";
    [ObservableProperty] private bool _canUninstall = true;
    [ObservableProperty] private bool _canDeleteRegistry;

    public ObservableCollection<SoftwareRow> Software { get; } = [];
    public ObservableCollection<SoftwareRow> FilteredSoftware { get; } = [];

    public SoftwareManagerViewModel(MainViewModel main, ISoftwareService softwareService, ILogService logService, IPsExecService psExecService, ICacheService cache)
    {
        _main = main; _softwareService = softwareService; _logService = logService; _psExecService = psExecService; _cache = cache;
        _ = LoadSoftwareAsync();
    }

    private async Task LoadSoftwareAsync(string? selectName = null, bool force = false)
    {
        IsLoading = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) { IsLoading = false; return; }
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            var cacheKey = $"software_{(DeepCleanup ? "deep" : "normal")}";
            if (!force)
                await _cache.PopulateFromCacheAsync<List<SoftwareInfo>>(host, cacheKey, list => PopulateSoftware(list, null));

            if (force || !await _cache.HasValidCacheAsync(host, cacheKey))
            {
                var list = await _softwareService.GetInstalledSoftwareAsync(host, cred.UserName, password ?? string.Empty, DeepCleanup);
                await _cache.SaveAndPopulateAsync(host, cacheKey, list, l => PopulateSoftware(l, selectName));
            }

            LastRefreshText = _cache.GetCacheAge(host, cacheKey) is string age ? $"缓存于 {age}" : "尚未刷新";
        }
        finally { IsLoading = false; }
    }

    private void PopulateSoftware(List<SoftwareInfo> list, string? selectName)
    {
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

    [RelayCommand] private async Task RefreshAsync() => await LoadSoftwareAsync(force: true);

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
            if (string.IsNullOrWhiteSpace(item.Software.UninstallString) &&
                string.IsNullOrWhiteSpace(item.Software.QuietUninstallString)) continue;
            await _softwareService.UninstallSilentlyAsync(host, cred.UserName, password ?? string.Empty, item.Software);
        }
        _cache.Invalidate(host, $"software_{(DeepCleanup ? "deep" : "normal")}");
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
        _cache.Invalidate(host, $"software_{(DeepCleanup ? "deep" : "normal")}");
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
            var regPath = ToRegExePath(key);
            if (string.IsNullOrWhiteSpace(regPath)) continue;
            var cmd = $"reg delete \"{regPath}\" /f";
            await _psExecService.ExecuteAsync(host, cred.UserName, password ?? string.Empty, cmd);
        }

        // Refresh after deletion
        if (DeepCleanup)
        {
            _cache.Invalidate(host, "software_deep");
            await LoadSoftwareAsync();
        }
    }

    private static string ToRegExePath(string key)
    {
        var normalized = key.Trim().Replace('/', '\\');
        if (normalized.StartsWith(@"HKLM:\", StringComparison.OrdinalIgnoreCase))
            return @"HKLM\" + normalized[@"HKLM:\".Length..];
        if (normalized.StartsWith(@"HKCU:\", StringComparison.OrdinalIgnoreCase))
            return @"HKCU\" + normalized[@"HKCU:\".Length..];
        if (normalized.StartsWith(@"HKCR:\", StringComparison.OrdinalIgnoreCase))
            return @"HKCR\" + normalized[@"HKCR:\".Length..];

        return normalized.StartsWith(@"HKEY_", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : string.Empty;
    }
}

public partial class SoftwareRow : ObservableObject
{
    public SoftwareInfo Software { get; set; } = null!;
    [ObservableProperty] private bool _isChecked;
}
