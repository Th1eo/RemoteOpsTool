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
    private System.Threading.Timer? _saveDebounceTimer;
    private const int SaveDebounceMs = 500;

    public ObservableCollection<CredentialInfo> Credentials { get; } = [];

    public CredentialService(ILogService log)
    {
        _log = log;
    }

    private void DebouncedSave()
    {
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
        try
        {
            var filePath = AppConstants.CredentialsFilePath;
            if (!File.Exists(filePath))
            {
                _log.Info("No credentials file found, starting with empty credentials.");
                return;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var items = JsonSerializer.Deserialize<List<CredentialInfo>>(json) ?? [];

            Credentials.Clear();
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.EncryptedPassword))
                {
                    try
                    {
                        var encryptedBytes = Convert.FromBase64String(item.EncryptedPassword);
                        ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                    }
                    catch
                    {
                        _log.Warn($"Credential for {item.UserName} cannot be decrypted on this machine.");
                    }
                }

                item.PropertyChanged += OnCredentialPropertyChanged;
                Credentials.Add(item);
            }

            _log.Info($"Loaded {Credentials.Count} credentials.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load credentials: {ex.Message}");
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var dir = AppConstants.AppDataFolder;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Credentials.ToList(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(AppConstants.CredentialsFilePath, json);
            _log.Info("Credentials saved.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save credentials: {ex.Message}");
        }
    }

    public void Add(CredentialInfo credential)
    {
        credential.PropertyChanged += OnCredentialPropertyChanged;
        credential.IsSelected = true;
        Credentials.Add(credential);
        _ = SaveAsync();
    }

    private void OnCredentialPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CredentialInfo.IsSelected))
            DebouncedSave();
    }

    public void Remove(CredentialInfo credential)
    {
        credential.PropertyChanged -= OnCredentialPropertyChanged;
        Credentials.Remove(credential);
    }

    public void ClearAll()
    {
        Credentials.Clear();
    }

    public string? DecryptPassword(CredentialInfo credential)
    {
        if (string.IsNullOrEmpty(credential.EncryptedPassword))
            return null;

        try
        {
            var encryptedBytes = Convert.FromBase64String(credential.EncryptedPassword);
            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch
        {
            _log.Error($"Failed to decrypt password for {credential.UserName}.");
            return null;
        }
    }

    public List<CredentialInfo> GetSelectedCredentials()
    {
        return Credentials.Where(c => c.IsSelected).ToList();
    }

    public static string EncryptPassword(string plainPassword)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainPassword);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }
}
