using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class SystemInfoViewModel : ObservableObject
{
    private readonly ISystemInfoService? _service;
    private readonly string? _host;
    private readonly string? _username;
    private readonly string? _password;

    [ObservableProperty]
    private string _systemInfoText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SystemInfoData? _infoData;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _formattedText = string.Empty;

    [ObservableProperty]
    private string _queryMethodInfo = string.Empty;

    public SystemInfoViewModel(ISystemInfoService service, string host, string username, string password)
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
            var data = await _service.GetStructuredSystemInfoAsync(_host, _username ?? "", _password ?? "");
            InfoData = data;
            HasData = data.HasData;
            HasError = data.HasError;
            ErrorMessage = data.ErrorMessage;
            QueryMethodInfo = string.IsNullOrEmpty(data.QueryMethod) ? "" : $"查询方式: {data.QueryMethod}";
            if (HasData)
            {
                FormattedText = BuildFormattedText(data);
            }

            SystemInfoText = !string.IsNullOrWhiteSpace(data.RawOutput)
                ? data.RawOutput
                : !string.IsNullOrWhiteSpace(FormattedText)
                    ? FormattedText
                    : data.ErrorMessage ?? string.Empty;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"查询异常: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private static string BuildFormattedText(SystemInfoData data)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"Host Name: {data.HostName}",
            $"OS: {data.OsCaption} ({data.OsVersion} {data.OsArchitecture})",
            $"Build: {data.BuildNumber}",
            $"Install Date: {data.InstallDate}",
            $"Boot Time: {data.LastBootUpTime}",
            $"Manufacturer: {data.Manufacturer}",
            $"Model: {data.Model}",
            $"System Type: {data.SystemType}",
            $"Processor: {data.ProcessorName}",
            $"Cores: {data.ProcessorCores} ({data.ProcessorLogicalProcessors} Logical)",
            $"Max Clock: {data.ProcessorMaxClockSpeed}",
            $"Memory: {data.TotalPhysicalMemoryGB} (Free: {data.FreePhysicalMemoryGB}, Used: {data.UsedPhysicalMemoryGB})",
            $"Domain: {data.Domain}",
            $"Current User: {data.CurrentUser}",
            $"Owner: {data.RegisteredOwner}",
            $"Organization: {data.RegisteredOrganization}",
            $"System Drive: {data.SystemDrive}",
            $"System Dir: {data.SystemDirectory}",
            $"Windows Dir: {data.WindowsDirectory}",
            $"Time Zone: {data.TimeZone}",
            $"BIOS: {data.BiosManufacturer} {data.BiosVersion} (S/N: {data.BiosSerialNumber})",
            $"BaseBoard: {data.BaseBoardManufacturer} {data.BaseBoardProduct} (S/N: {data.BaseBoardSerialNumber})",
            $"Graphics: {data.GraphicsCards}",
            $"GPU Drivers: {data.GpuDriverVersions}",
            $"Network Adapters: {data.NetworkAdapters}",
            $"IP Addresses: {data.IpAddresses}",
            $"MAC Addresses: {data.MacAddresses}",
            $"Logical Disks: {data.LogicalDisks}",
            $"Recent HotFixes: {data.RecentHotFixes}"
        });
    }
}
