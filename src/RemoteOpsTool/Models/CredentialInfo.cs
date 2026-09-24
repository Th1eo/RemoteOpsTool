using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace RemoteOpsTool.Models;

public partial class CredentialInfo : ObservableObject
{
    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _encryptedPassword = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private CredentialHealth _health = CredentialHealth.Unverified;

    [ObservableProperty]
    private DateTimeOffset? _lastValidatedAt;

    [ObservableProperty]
    private string _lastValidatedHost = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _lastUsedAt;

    [ObservableProperty]
    private string _lastError = string.Empty;

    [JsonIgnore]
    public string DisplayText => string.IsNullOrWhiteSpace(Description)
        ? UserName
        : $"{UserName} [{Description}]";

    [JsonIgnore]
    public string MaskedDisplay
    {
        get
        {
            var lastBackslash = UserName.LastIndexOf('\\');
            if (lastBackslash >= 0 && lastBackslash < UserName.Length - 1)
                return $"{UserName[..(lastBackslash + 1)]}{UserName[(lastBackslash + 1)..Math.Min(lastBackslash + 4, UserName.Length)]}***";
            return UserName.Length > 3 ? $"{UserName[..3]}***" : UserName;
        }
    }

    [JsonIgnore]
    public string HealthText => Health switch
    {
        CredentialHealth.Healthy => "可用",
        CredentialHealth.NeedsReauth => "需重新认证",
        CredentialHealth.LocalLogonBlocked => "本机登录失败",
        CredentialHealth.SecretUnreadable => "密码不可读",
        CredentialHealth.AuthorizationDenied => "目标授权失败",
        CredentialHealth.SessionConflict => "SMB 会话冲突",
        CredentialHealth.TransportUnavailable => "远程通道不可用",
        _ => "未验证",
    };

    [JsonIgnore]
    public string HealthDetail => Health switch
    {
        CredentialHealth.Healthy => "至少一个远程命令通道验证成功。",
        CredentialHealth.NeedsReauth => "用户名、域名或密码可能已变更。",
        CredentialHealth.LocalLogonBlocked => "Windows 无法在操作端以该账号启动进程，请检查本地登录权限、账号状态或密码。",
        CredentialHealth.SecretUnreadable => "当前 Windows 用户无法解密保存的密码，请重新录入密码。",
        CredentialHealth.AuthorizationDenied => "账号已通过本机启动，但目标机拒绝 WMI、SCM 或 RPC 授权。",
        CredentialHealth.SessionConflict => "当前 Windows 会话已使用其他账号连接目标机，需先断开 SMB 会话。",
        CredentialHealth.TransportUnavailable => "目标机可达，但当前没有可用的远程命令通道。",
        _ => "尚未在当前目标主机验证该凭据。",
    };

    [JsonIgnore]
    public string LastValidatedText => LastValidatedAt is null
        ? "从未验证"
        : string.IsNullOrWhiteSpace(LastValidatedHost)
            ? $"验证于 {LastValidatedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}"
            : $"{LastValidatedHost} · {LastValidatedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";

    [JsonIgnore]
    public string LastUsedText => LastUsedAt is null
        ? "从未使用"
        : $"最近使用 {LastUsedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";

    [JsonIgnore]
    public string LastErrorText => string.IsNullOrWhiteSpace(LastError) ? "无错误详情" : LastError;

    public bool IsValidatedForHost(string? host)
    {
        var normalizedHost = host?.Trim();
        return LastValidatedAt is not null &&
               !string.IsNullOrWhiteSpace(LastValidatedHost) &&
               !string.IsNullOrWhiteSpace(normalizedHost) &&
               LastValidatedHost.Trim().Equals(normalizedHost, StringComparison.OrdinalIgnoreCase);
    }

    public CredentialHealth GetHealthForHost(string? host) =>
        Health == CredentialHealth.SecretUnreadable || IsValidatedForHost(host)
            ? Health
            : CredentialHealth.Unverified;

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(MaskedDisplay));
    }

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(DisplayText));

    partial void OnHealthChanged(CredentialHealth value)
    {
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthDetail));
    }

    partial void OnLastValidatedAtChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(LastValidatedText));

    partial void OnLastValidatedHostChanged(string value) =>
        OnPropertyChanged(nameof(LastValidatedText));

    partial void OnLastUsedAtChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(LastUsedText));

    partial void OnLastErrorChanged(string value) => OnPropertyChanged(nameof(LastErrorText));
}
