using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class CredentialService : ICredentialService
{
    private readonly ILogService _log;
    private readonly string _credentialsFilePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private System.Threading.Timer? _saveDebounceTimer;
    private CredentialInfo? _selectedCredential;
    private bool _suppressSelectionSync;
    private bool _suppressPersistence;
    private const int SaveDebounceMs = 500;

    public ObservableCollection<CredentialInfo> Credentials { get; } = [];

    public CredentialInfo? SelectedCredential => _selectedCredential;

    public event EventHandler? SelectedCredentialChanged;
    public event Action<string>? SaveFailed;

    public CredentialService(ILogService log, string? credentialsFilePath = null)
    {
        _log = log;
        _credentialsFilePath = credentialsFilePath ?? AppConstants.CredentialsFilePath;
    }

    private void DebouncedSave()
    {
        if (_suppressPersistence)
            return;

        if (_saveDebounceTimer == null)
        {
            _saveDebounceTimer = new System.Threading.Timer(_ =>
            {
                _ = SaveAsync();
            }, null, SaveDebounceMs, Timeout.Infinite);
        }
        else
        {
            _saveDebounceTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
    }

    public async Task LoadAsync()
    {
        _suppressPersistence = true;
        try
        {
            foreach (var credential in Credentials)
                credential.PropertyChanged -= OnCredentialPropertyChanged;

            Credentials.Clear();
            SetSelectedCredentialCore(null);

            if (!File.Exists(_credentialsFilePath))
            {
                _log.Info("No credentials file found, starting with empty credentials.");
                return;
            }

            var json = await File.ReadAllTextAsync(_credentialsFilePath);
            var items = JsonSerializer.Deserialize<List<CredentialInfo>>(json) ?? [];

            CredentialInfo? selected = null;
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.EncryptedPassword) && !CanDecrypt(item))
                {
                    item.Health = CredentialHealth.SecretUnreadable;
                    item.LastError = "当前 Windows 用户无法解密该凭据密码。";
                    _log.Warn($"Credential for {item.UserName} cannot be decrypted on this machine.");
                }

                item.PropertyChanged += OnCredentialPropertyChanged;
                Credentials.Add(item);

                if (item.IsSelected && selected is null)
                    selected = item;
            }

            SetSelectedCredentialCore(selected);
            _log.Info($"Loaded {Credentials.Count} credentials.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load credentials: {ex.Message}");
        }
        finally
        {
            _suppressPersistence = false;
        }
    }

    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        string? tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(_credentialsFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Credentials.ToList(), new JsonSerializerOptions { WriteIndented = true });
            tempPath = $"{_credentialsFilePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _credentialsFilePath, overwrite: true);
            tempPath = null;
            _log.Info("Credentials saved.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save credentials: {ex.Message}");
            SaveFailed?.Invoke(ex.Message);
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }

            _saveGate.Release();
        }
    }

    public void Add(CredentialInfo credential)
    {
        if (!Credentials.Contains(credential))
        {
            credential.PropertyChanged += OnCredentialPropertyChanged;
            Credentials.Add(credential);
        }

        SetSelectedCredential(credential);
    }

    private void OnCredentialPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CredentialInfo credential)
            return;

        if (e.PropertyName == nameof(CredentialInfo.IsSelected) && !_suppressSelectionSync)
        {
            if (credential.IsSelected)
                SetSelectedCredential(credential);
            else if (ReferenceEquals(_selectedCredential, credential))
                SetSelectedCredential(null);
            return;
        }

        DebouncedSave();
    }

    public void Remove(CredentialInfo credential)
    {
        credential.PropertyChanged -= OnCredentialPropertyChanged;
        var wasSelected = ReferenceEquals(_selectedCredential, credential);
        Credentials.Remove(credential);

        if (wasSelected)
            SetSelectedCredential(Credentials.FirstOrDefault());
        else
            DebouncedSave();
    }

    public void ClearAll()
    {
        foreach (var credential in Credentials)
            credential.PropertyChanged -= OnCredentialPropertyChanged;

        Credentials.Clear();
        SetSelectedCredentialCore(null);
        DebouncedSave();
    }

    public void SetSelectedCredential(CredentialInfo? credential)
    {
        if (credential is not null && !Credentials.Contains(credential))
            return;

        SetSelectedCredentialCore(credential);
        DebouncedSave();
    }

    private void SetSelectedCredentialCore(CredentialInfo? credential)
    {
        var changed = !ReferenceEquals(_selectedCredential, credential);

        _suppressSelectionSync = true;
        try
        {
            foreach (var item in Credentials)
                item.IsSelected = ReferenceEquals(item, credential);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        _selectedCredential = credential;
        if (changed)
            SelectedCredentialChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? DecryptPassword(CredentialInfo credential)
    {
        return TryDecryptPassword(credential, out var password) ? password : null;
    }

    public bool TryDecryptPassword(CredentialInfo credential, out string password)
    {
        password = string.Empty;
        if (string.IsNullOrEmpty(credential.EncryptedPassword))
        {
            MarkSecretUnreadable(credential, "该凭据没有保存密码。");
            return false;
        }

        if (!TryUnprotect(
                credential.EncryptedPassword,
                out var decryptedBytes,
                out var usedLegacyEntropy,
                out var error))
        {
            MarkSecretUnreadable(credential, $"密码解密失败：{error}");
            return false;
        }

        try
        {
            password = Encoding.UTF8.GetString(decryptedBytes);
        }
        finally
        {
            // 明文缓冲区尽快清零，缩短其在托管堆中的驻留时间。
            CryptographicOperations.ZeroMemory(decryptedBytes);
        }

        if (usedLegacyEntropy)
        {
            // 早期版本使用 entropy = null 保存；成功解密后顺手迁移到带 entropy 的格式。
            try
            {
                credential.EncryptedPassword = EncryptPassword(password);
                _log.Info($"凭据密码已迁移到带 entropy 的 DPAPI 格式: {credential.UserName}");
            }
            catch (CryptographicException ex)
            {
                // 迁移失败不应阻断本次凭据使用；下次解密时会再次尝试。
                _log.Warn($"凭据密码迁移到带 entropy 的 DPAPI 格式失败: {credential.UserName}: {ex.Message}");
            }
        }

        if (credential.Health == CredentialHealth.SecretUnreadable)
        {
            credential.Health = CredentialHealth.Unverified;
            credential.LastError = string.Empty;
        }

        var now = DateTimeOffset.Now;
        if (credential.LastUsedAt is null || now - credential.LastUsedAt.Value > TimeSpan.FromMinutes(5))
            credential.LastUsedAt = now;

        DebouncedSave();
        return true;
    }

    /// <summary>
    /// DPAPI optional entropy。它不是密钥，但能让其它 DPAPI 调用方（以及随手
    /// 使用 Unprotect(null) 的代码）无法直接解开保存的密码。
    /// </summary>
    internal static byte[] ProtectionEntropy { get; } =
        SHA256.HashData(Encoding.UTF8.GetBytes("RemoteOpsTool.Credentials.v1"));

    private static bool TryUnprotect(
        string encryptedBase64,
        out byte[] decryptedBytes,
        out bool usedLegacyEntropy,
        out string error)
    {
        decryptedBytes = [];
        usedLegacyEntropy = false;
        error = string.Empty;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException)
        {
            error = "凭据密文不是有效的 Base64 数据。";
            return false;
        }

        try
        {
            try
            {
                decryptedBytes = ProtectedData.Unprotect(
                    encryptedBytes, ProtectionEntropy, DataProtectionScope.CurrentUser);
                return true;
            }
            catch (CryptographicException)
            {
                // 兼容旧格式：早期版本使用 entropy = null。
                decryptedBytes = ProtectedData.Unprotect(
                    encryptedBytes, null, DataProtectionScope.CurrentUser);
                usedLegacyEntropy = true;
                return true;
            }
        }
        catch (CryptographicException ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    private static bool CanDecrypt(CredentialInfo credential)
    {
        if (string.IsNullOrEmpty(credential.EncryptedPassword))
            return false;

        if (!TryUnprotect(credential.EncryptedPassword, out var decryptedBytes, out _, out _))
            return false;

        CryptographicOperations.ZeroMemory(decryptedBytes);
        return true;
    }

    private void MarkSecretUnreadable(CredentialInfo credential, string reason)
    {
        credential.Health = CredentialHealth.SecretUnreadable;
        credential.LastError = reason;
        _log.Error($"Failed to decrypt password for {credential.UserName}: {reason}");
        DebouncedSave();
    }

    public List<CredentialInfo> GetSelectedCredentials()
    {
        return SelectedCredential is null ? [] : [SelectedCredential];
    }

    public static string EncryptPassword(string plainPassword)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainPassword);
        try
        {
            var encryptedBytes = ProtectedData.Protect(
                plainBytes, ProtectionEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }
}
