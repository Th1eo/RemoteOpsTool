using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels;

public partial class CredentialViewModel : ObservableObject, IDisposable
{
    private readonly ICredentialService _credentialService;
    private readonly ILogService _logService;
    private readonly ICapabilityService? _capabilityService;
    private readonly Func<string> _getTargetHost;
    private CredentialInfo? _observedCredential;
    private bool _disposed;

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

    [ObservableProperty]
    private bool _isValidating;

    [ObservableProperty]
    private CredentialInfo? _listSelectedCredential;

    public ObservableCollection<CredentialInfo> Credentials => _credentialService.Credentials;

    public string CurrentHost => _getTargetHost().Trim();

    public string CurrentCredentialText =>
        _credentialService.SelectedCredential?.DisplayText ?? "未选择当前凭据";

    public CredentialHealth CurrentCredentialHealth
    {
        get => _credentialService.SelectedCredential?.GetHealthForHost(CurrentHost)
            ?? CredentialHealth.Unverified;
    }

    public string CurrentCredentialHealthText
    {
        get
        {
            var credential = _credentialService.SelectedCredential;
            if (credential is null)
                return "请选择要使用的运维身份";

            if (credential.Health != CredentialHealth.SecretUnreadable &&
                !credential.IsValidatedForHost(CurrentHost))
            {
                return "未在当前目标主机验证";
            }

            return credential.HealthText;
        }
    }

    public string ValidateButtonText => IsValidating ? "验证中..." : "验证";

    public string CurrentCredentialDetailText
    {
        get
        {
            var credential = _credentialService.SelectedCredential;
            if (credential is null)
                return "单一当前凭据可避免不同账号混用导致的 SMB 会话冲突。";

            if (credential.Health == CredentialHealth.SecretUnreadable)
                return string.IsNullOrWhiteSpace(credential.LastError)
                    ? credential.HealthDetail
                    : credential.LastErrorText;

            if (!credential.IsValidatedForHost(CurrentHost))
                return "建议先验证，以区分密码错误、本机 RunAs 失败和目标机授权不足。";

            return string.IsNullOrWhiteSpace(credential.LastError)
                ? credential.HealthDetail
                : credential.LastErrorText;
        }
    }

    public string CurrentCredentialLastValidatedText =>
        _credentialService.SelectedCredential?.LastValidatedText ?? "从未验证";

    public CredentialViewModel(
        ICredentialService credentialService,
        ILogService logService,
        ICapabilityService? capabilityService = null,
        Func<string>? getTargetHost = null)
    {
        _credentialService = credentialService;
        _logService = logService;
        _capabilityService = capabilityService;
        _getTargetHost = getTargetHost ?? (() => string.Empty);

        ListSelectedCredential = _credentialService.SelectedCredential;
        _credentialService.SelectedCredentialChanged += OnServiceSelectedCredentialChanged;
        ObserveSelectedCredential();
    }

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    [RelayCommand]
    private async Task AddCredentialAsync()
    {
        if (!TryBuildNewCredential(out var fullUser, out var encryptedPassword))
            return;

        if (_credentialService.Credentials.Any(c =>
            c.UserName.Equals(fullUser, StringComparison.OrdinalIgnoreCase)))
        {
            _logService.Warn($"凭据 \"{fullUser}\" 已存在。请选中后使用“更新密码”。");
            return;
        }

        var info = new CredentialInfo
        {
            UserName = fullUser,
            EncryptedPassword = encryptedPassword,
            Description = NewDescription.Trim(),
            Health = CredentialHealth.Unverified,
        };

        _credentialService.Add(info);
        ListSelectedCredential = info;
        ResetNewCredentialFields();
        await _credentialService.SaveAsync();

        _logService.Info($"凭据已添加并设为当前凭据: {info.MaskedDisplay}");
    }

    [RelayCommand]
    private void SetCurrentCredential()
    {
        var credential = ListSelectedCredential;
        if (credential is null)
        {
            _logService.Warn("请先选择一条凭据。");
            return;
        }

        _credentialService.SetSelectedCredential(credential);
        _logService.Info($"当前运维凭据已切换为: {credential.MaskedDisplay}");
    }

    [RelayCommand(CanExecute = nameof(CanValidateSelectedCredential))]
    private async Task ValidateSelectedCredentialAsync()
    {
        var credential = ListSelectedCredential;
        if (credential is null)
        {
            _logService.Warn("请先选择一条凭据。");
            return;
        }

        var host = CurrentHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            _logService.Warn("请先在主界面填写目标主机，再验证凭据。");
            return;
        }

        if (_capabilityService is null)
        {
            _logService.Warn("当前窗口未提供能力探测服务，无法验证凭据。");
            return;
        }

        if (!_credentialService.TryDecryptPassword(credential, out var password))
        {
            _logService.Error($"凭据 {credential.MaskedDisplay} 的密码不可读，请重新录入密码。");
            return;
        }

        IsValidating = true;
        try
        {
            _logService.Info($"开始验证凭据 {credential.MaskedDisplay} 对 {host} 的可用性...");
            var snapshot = await _capabilityService.RefreshAsync(host, credential.UserName, password);
            var assessment = CredentialHealthClassifier.Classify(snapshot.RawResults);

            credential.Health = assessment.Health;
            credential.LastValidatedAt = DateTimeOffset.Now;
            credential.LastValidatedHost = host;
            credential.LastError = assessment.Error;
            await _credentialService.SaveAsync();

            var status = string.IsNullOrWhiteSpace(assessment.Error)
                ? credential.HealthText
                : $"{credential.HealthText}（{assessment.Error}）";
            _logService.Info($"凭据验证完成: {credential.MaskedDisplay} -> {status}");

            if (credential.Health != CredentialHealth.Healthy)
                _logService.Warn($"凭据 {credential.MaskedDisplay} 在 {host} 上不可用: {credential.HealthDetail}");
        }
        catch (Exception ex)
        {
            credential.Health = CredentialHealth.TransportUnavailable;
            credential.LastValidatedAt = DateTimeOffset.Now;
            credential.LastValidatedHost = host;
            credential.LastError = $"验证过程失败：{ex.Message}";
            await _credentialService.SaveAsync();
            _logService.Error($"凭据验证失败: {ex.Message}");
        }
        finally
        {
            IsValidating = false;
        }
    }

    [RelayCommand]
    private async Task UpdateSelectedPasswordAsync()
    {
        var credential = ListSelectedCredential;
        if (credential is null)
        {
            _logService.Warn("请先选择要更新密码的凭据。");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            _logService.Warn("请先在上方密码框中输入新密码。");
            return;
        }

        if (NewPassword.Length < 4)
        {
            _logService.Warn("密码至少需要4个字符。");
            return;
        }

        credential.EncryptedPassword = Services.CredentialService.EncryptPassword(NewPassword);
        credential.Health = CredentialHealth.Unverified;
        credential.LastValidatedAt = null;
        credential.LastValidatedHost = string.Empty;
        credential.LastError = string.Empty;

        if (!string.IsNullOrWhiteSpace(NewDescription))
            credential.Description = NewDescription.Trim();

        NewPassword = string.Empty;
        await _credentialService.SaveAsync();
        _logService.Info($"已更新凭据密码，需重新验证: {credential.MaskedDisplay}");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var credential = ListSelectedCredential;
        if (credential is null)
        {
            _logService.Warn("请先选择要删除的凭据。");
            return;
        }

        if (!Confirm(
                "删除凭据",
                "确认删除当前凭据？",
                $"将从本机凭据库中删除 {credential.MaskedDisplay}。此操作不会影响域账号本身。",
                "删除"))
        {
            return;
        }

        _credentialService.Remove(credential);
        await _credentialService.SaveAsync();

        EnsureListSelectionConsistent();
        _logService.Info($"已删除凭据: {credential.MaskedDisplay}");
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        if (Credentials.Count == 0)
            return;

        if (!Confirm(
                "清空凭据",
                "确认删除全部凭据？",
                $"将删除本机保存的 {Credentials.Count} 条运维凭据，且无法撤销。",
                "全部删除"))
        {
            return;
        }

        _credentialService.ClearAll();
        await _credentialService.SaveAsync();

        EnsureListSelectionConsistent();
        _logService.Info("已删除全部凭据。");
    }

    /// <summary>
    /// 保证列表选中项始终指向仍存在于凭据库中的对象：
    /// 删除/清空后若原选中项已被移除，则回退到当前身份（或空）。
    /// </summary>
    internal void EnsureListSelectionConsistent()
    {
        var selected = ListSelectedCredential;
        if (selected is not null && _credentialService.Credentials.Contains(selected))
            return;

        ListSelectedCredential = _credentialService.SelectedCredential;
    }

    private bool CanValidateSelectedCredential() =>
        ListSelectedCredential is not null && !IsValidating;

    partial void OnListSelectedCredentialChanged(CredentialInfo? value) =>
        ValidateSelectedCredentialCommand.NotifyCanExecuteChanged();

    private bool TryBuildNewCredential(out string fullUser, out string encryptedPassword)
    {
        fullUser = string.Empty;
        encryptedPassword = string.Empty;

        if (string.IsNullOrWhiteSpace(NewUserName))
        {
            _logService.Warn("用户名不能为空。");
            return false;
        }
        if (NewUserName.Trim().Length > 128)
        {
            _logService.Warn("用户名过长（最多128字符）。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            _logService.Warn("密码不能为空。");
            return false;
        }
        if (NewPassword.Length < 4)
        {
            _logService.Warn("密码至少需要4个字符。");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(NewDomain))
        {
            if (NewDomain.Trim().Length > 64)
            {
                _logService.Warn("域名过长（最多64字符）。");
                return false;
            }
            if (NewDomain.Contains('\\') || NewDomain.Contains('@'))
            {
                _logService.Warn("域名不能包含 \\ 或 @ 字符。");
                return false;
            }
        }

        fullUser = string.IsNullOrWhiteSpace(NewDomain)
            ? NewUserName.Trim()
            : $"{NewDomain.Trim()}\\{NewUserName.Trim()}";
        encryptedPassword = Services.CredentialService.EncryptPassword(NewPassword);
        return true;
    }

    private void ResetNewCredentialFields()
    {
        NewDomain = string.Empty;
        NewUserName = string.Empty;
        NewPassword = string.Empty;
        NewDescription = string.Empty;
    }

    private void OnServiceSelectedCredentialChanged(object? sender, EventArgs e)
    {
        ObserveSelectedCredential();
        NotifyCurrentCredentialChanged();
    }

    private void ObserveSelectedCredential()
    {
        if (_observedCredential is not null)
            _observedCredential.PropertyChanged -= OnObservedCredentialPropertyChanged;

        _observedCredential = _credentialService.SelectedCredential;

        if (_observedCredential is not null)
            _observedCredential.PropertyChanged += OnObservedCredentialPropertyChanged;
    }

    private void OnObservedCredentialPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyCurrentCredentialChanged();
    }

    private void NotifyCurrentCredentialChanged()
    {
        OnPropertyChanged(nameof(CurrentCredentialText));
        OnPropertyChanged(nameof(CurrentCredentialHealth));
        OnPropertyChanged(nameof(CurrentCredentialHealthText));
        OnPropertyChanged(nameof(CurrentCredentialDetailText));
        OnPropertyChanged(nameof(CurrentCredentialLastValidatedText));
    }

    private static bool Confirm(string title, string heading, string message, string confirmText)
    {
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;

        var dialog = new ConfirmationDialog(title, heading, message, confirmText);
        return owner is not null
            ? dialog.ShowDialogSafe(owner) == true
            : dialog.ShowDialog() == true;
    }

    partial void OnIsValidatingChanged(bool value)
    {
        OnPropertyChanged(nameof(ValidateButtonText));
        ValidateSelectedCredentialCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _credentialService.SelectedCredentialChanged -= OnServiceSelectedCredentialChanged;

        if (_observedCredential is not null)
            _observedCredential.PropertyChanged -= OnObservedCredentialPropertyChanged;

        _observedCredential = null;
        GC.SuppressFinalize(this);
    }
}
