using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface ISettingsService
{
    AppSettings Settings { get; }

    Task LoadAsync();
    Task SaveAsync();
}
