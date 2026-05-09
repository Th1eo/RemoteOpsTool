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
                FormattedText = await _service.GetSystemInfoAsync(_host, _username ?? "", _password ?? "");
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
}
