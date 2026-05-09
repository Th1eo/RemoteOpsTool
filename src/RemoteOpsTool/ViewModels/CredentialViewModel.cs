using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class CredentialViewModel : ObservableObject
{
    private readonly ICredentialService _credentialService;
    private readonly ILogService _logService;

    [ObservableProperty]
    private string _newDomain = string.Empty;

    [ObservableProperty]
    private string _newUserName = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _newDescription = string.Empty;

    [ObservableProperty]
    private bool _isPasswordVisible;

    public ObservableCollection<CredentialInfo> Credentials => _credentialService.Credentials;

    public CredentialViewModel(ICredentialService credentialService, ILogService logService)
    {
        _credentialService = credentialService;
        _logService = logService;
    }

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    [RelayCommand]
    private async Task AddCredentialAsync()
    {
        // Validation
        if (string.IsNullOrWhiteSpace(NewUserName))
        {
            _logService.Warn("用户名不能为空。");
            return;
        }
        if (NewUserName.Trim().Length > 128)
        {
            _logService.Warn("用户名过长（最多128字符）。");
            return;
        }
        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            _logService.Warn("密码不能为空。");
            return;
        }
        if (NewPassword.Length < 4)
        {
            _logService.Warn("密码至少需要4个字符。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(NewDomain))
        {
            if (NewDomain.Trim().Length > 64)
            {
                _logService.Warn("域名过长（最多64字符）。");
                return;
            }
            if (NewDomain.Contains('\\') || NewDomain.Contains('@'))
            {
                _logService.Warn("域名不能包含 \\ 或 @ 字符。");
                return;
            }
        }

        var fullUser = string.IsNullOrWhiteSpace(NewDomain)
            ? NewUserName.Trim()
            : $"{NewDomain.Trim()}\\{NewUserName.Trim()}";

        // Check duplicate
        if (_credentialService.Credentials.Any(c =>
            c.UserName.Equals(fullUser, StringComparison.OrdinalIgnoreCase)))
        {
            _logService.Warn($"凭据 \"{fullUser}\" 已存在。");
            return;
        }

        var encrypted = Services.CredentialService.EncryptPassword(NewPassword);
        var info = new CredentialInfo
        {
            UserName = fullUser,
            EncryptedPassword = encrypted,
            Description = NewDescription.Trim()
        };

        _credentialService.Add(info);

        NewDomain = string.Empty;
        NewUserName = string.Empty;
        NewPassword = string.Empty;
        NewDescription = string.Empty;

        _logService.Info($"凭据已添加: {info.MaskedDisplay}");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = _credentialService.Credentials.Where(c => c.IsSelected).ToList();
        foreach (var cred in selected)
        {
            _credentialService.Remove(cred);
            _logService.Info($"Credential removed: {cred.MaskedDisplay}");
        }
        await _credentialService.SaveAsync();
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        _credentialService.ClearAll();
        await _credentialService.SaveAsync();
        _logService.Info("All credentials deleted.");
    }
}
