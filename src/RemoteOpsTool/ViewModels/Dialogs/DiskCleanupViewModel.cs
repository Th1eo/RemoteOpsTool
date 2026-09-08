using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class DiskCleanupViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _customDirectories = string.Empty;

    public ObservableCollection<CleanupItem> Items { get; } = [];

    public DiskCleanupViewModel(MainViewModel main, ILogService logService, IPsExecService psExecService, ISettingsService settingsService)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
        _settingsService = settingsService;

        Items.Add(new CleanupItem { Path = @"C:\Windows\Temp", Description = "Windows Temp", IsChecked = true });
        Items.Add(new CleanupItem { Path = @"C:\Windows\Prefetch", Description = "Prefetch", IsChecked = true });
        Items.Add(new CleanupItem { Path = @"C:\Windows\SoftwareDistribution\Download", Description = "Windows Update Cache", IsChecked = true });


    }

    [RelayCommand]
    private async Task CleanupAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;

        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var dirs = Items.Where(i => i.IsChecked).Select(i => i.Path).ToList();

        if (!string.IsNullOrWhiteSpace(CustomDirectories))
        {
            var custom = CustomDirectories
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => d.Length > 0);
            dirs.AddRange(custom);
        }

        if (dirs.Count == 0) return;

        // Cleanup targets are one-shot input. Clear the editor and remove any
        // legacy persisted value before starting the remote operation so reopening
        // the dialog never restores the previous paths.
        CustomDirectories = string.Empty;
        _settingsService.Settings.CustomCleanupDirectories = string.Empty;
        await _settingsService.SaveAsync();
        var fileDiskService = new Services.FileDiskService(_psExecService,
            _settingsService, _logService);

        await fileDiskService.CleanupDisksAsync(host, cred.UserName, password ?? string.Empty, dirs);
    }

    public partial class CleanupItem : ObservableObject
    {
        [ObservableProperty] private string _path = string.Empty;
        [ObservableProperty] private string _description = string.Empty;
        [ObservableProperty] private bool _isChecked;
    }
}
