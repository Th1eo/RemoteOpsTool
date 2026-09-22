using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class DiskInfoViewModel : ObservableObject
{
    private readonly IFileDiskService? _service;
    private readonly string? _host;
    private readonly string? _username;
    private readonly string? _password;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<DiskInfo> Disks { get; } = [];

    public DiskInfoViewModel(IFileDiskService service, string host, string username, string password)
    {
        _service = service;
        _host = host;
        _username = username;
        _password = password;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_service == null || string.IsNullOrEmpty(_host)) return;
        IsLoading = true;
        try
        {
            var disks = await _service.GetDiskInfoAsync(
                _host, _username ?? "", _password ?? "", forceRefresh: true);
            foreach (var d in disks) Disks.Add(d);
        }
        catch { }
        finally { IsLoading = false; }
    }

}
