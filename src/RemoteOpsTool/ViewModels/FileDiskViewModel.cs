using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class FileDiskViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private string _selectedDrive = "D:";

    public ObservableCollection<string> DriveLetters { get; } =
    [
        "D:", "E:", "F:", "G:", "H:", "I:", "J:", "K:", "L:", "M:",
        "N:", "O:", "P:", "Q:", "R:", "S:", "T:", "U:", "V:", "W:", "X:", "Y:", "Z:"
    ];

    public FileDiskViewModel(
        MainViewModel main,
        ISettingsService settings,
        ILogService logService,
        IPsExecService psExecService,
        IFileDiskService fileDiskService)
    {
        _main = main;
        _settings = settings;
        _logService = logService;
        _psExecService = psExecService;
        _fileDiskService = fileDiskService;
    }

    private readonly ISettingsService _settings;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly IFileDiskService _fileDiskService;

    [RelayCommand]
    private void OpenCRoot()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host)) return;
        _fileDiskService.OpenCRoot(host);
    }

    [RelayCommand]
    private void OpenPublicDesktop()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host)) return;
        _fileDiskService.OpenPublicDesktop(host);
    }

    [RelayCommand]
    private void OpenDomainPublic()
    {
        _fileDiskService.OpenDomainPublic();
    }

    [RelayCommand]
    private void OpenSelectedDrive()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host)) return;
        _fileDiskService.OpenDrive(host, SelectedDrive);
    }

    [RelayCommand]
    private void GetDiskInfo()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;

        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var vm = new Dialogs.DiskInfoViewModel(_fileDiskService, host, cred.UserName, password ?? string.Empty);
        var window = new Views.Dialogs.DiskInfoWindow { DataContext = vm };
        window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);
    }

    [RelayCommand]
    private void OpenDiskCleanup()
    {
        var vm = new Dialogs.DiskCleanupViewModel(_main, _logService, _psExecService, _settings);
        var window = new Views.Dialogs.DiskCleanupWindow { DataContext = vm };
        window.ShowDialogSafe(System.Windows.Application.Current.MainWindow);
    }
}
