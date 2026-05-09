namespace RemoteOpsTool.Services.Interfaces;

public interface IDameWareService
{
    Task ConnectAsync(string targetHost, string username, string password);
}
