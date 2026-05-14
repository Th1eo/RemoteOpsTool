namespace RemoteOpsTool.Services.Interfaces;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string host, string dataKey) where T : class;
    Task SetAsync<T>(string host, string dataKey, T data) where T : class;
    void Invalidate(string host, string? dataKey = null);
    Task<bool> HasValidCacheAsync(string host, string dataKey);
    string? GetCacheAge(string host, string dataKey);
    Task PopulateFromCacheAsync<T>(string host, string dataKey, Action<T> onData) where T : class;
    Task SaveAndPopulateAsync<T>(string host, string dataKey, T data, Action<T> onData) where T : class;
}
