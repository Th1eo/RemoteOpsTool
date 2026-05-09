using System.Text.Json;
using RemoteOpsTool.Constants;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class SettingsService : ISettingsService
{
    private readonly ILogService _log;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService(ILogService log)
    {
        _log = log;
    }

    public async Task LoadAsync()
    {
        try
        {
            var filePath = AppConstants.SettingsFilePath;
            if (!File.Exists(filePath))
            {
                _log.Info("No settings file found, using defaults.");
                await SaveAsync();
                return;
            }

            var json = await File.ReadAllTextAsync(filePath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            _log.Info("Settings loaded.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load settings: {ex.Message}");
            Settings = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var dir = AppConstants.AppDataFolder;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(AppConstants.SettingsFilePath, json);
            _log.Info("Settings saved.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save settings: {ex.Message}");
        }
    }
}
