using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
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

            var cacheKey = CacheKeys.Software(DeepCleanup);
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
        InvalidateSoftwareCaches(host);
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
        InvalidateSoftwareCaches(host);
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
            // Use the same WMI StdRegProv provider that produced the list. This keeps
            // registry-view and HKCU identity consistent with enumeration. PsExec is only a
            // fallback for targets where the provider cannot delete the key.
            var deletedViaWmi = await _softwareService.DeleteRegistryKeyAsync(
                host, cred.UserName, password ?? string.Empty, key);
            if (deletedViaWmi)
            {
                _logService.Info($"注册表键清理完成: {regPath}");
                continue;
            }

            // PsExec may start a 32-bit reg.exe. Explicitly select the 64-bit view so HKLM/HKCR
            // paths refer to the same keys that were displayed in the WMI list.
            var cmd = BuildRegistryDeleteCommand(regPath);
            var deleteResult = await _psExecService.ExecuteAsync(
                host, cred.UserName, password ?? string.Empty, cmd, silent: true);

            // Cleanup is intentionally idempotent: the key may already have been removed by
            // an uninstaller or may have disappeared since the cached list was loaded.
            var missingRegistryKey = IsMissingRegistryKey(deleteResult);
            if (deleteResult.Success || missingRegistryKey)
            {
                _logService.Info(missingRegistryKey
                    ? $"注册表键已不存在，跳过清理: {regPath}"
                    : $"注册表键清理完成: {regPath}");
            }
            else
            {
                _logService.Warn($"注册表键清理失败: {regPath} (exit code: {deleteResult.ExitCode})\n" +
                    (string.IsNullOrWhiteSpace(deleteResult.StdErr) ? deleteResult.StdOut : deleteResult.StdErr));
            }
        }

        // Refresh after deletion
        if (DeepCleanup)
        {
            InvalidateSoftwareCaches(host);
            await LoadSoftwareAsync();
        }
    }

    private void InvalidateSoftwareCaches(string host)
    {
        _cache.InvalidateByPrefix(host, CacheKeys.SoftwarePrefix);
    }

    private static string BuildRegistryDeleteCommand(string regPath)
    {
        // WMI StdRegProv enumerates the 64-bit view on 64-bit targets. reg.exe otherwise
        // follows the bitness of the PsExec-launched process and can report a valid key as
        // missing. /reg:64 is harmless for the supported x64 target environment and also
        // prevents WOW6432Node paths from being redirected a second time.
        return $"reg delete \"{regPath}\" /f /reg:64";
    }

    private static bool IsMissingRegistryKey(CommandResult result)
    {
        var output = $"{result.StdOut}\n{result.StdErr}";
        return result.ExitCode == 1 &&
            (output.Contains("unable to find the specified registry key or value", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("找不到指定的注册表项或值", StringComparison.OrdinalIgnoreCase));
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
