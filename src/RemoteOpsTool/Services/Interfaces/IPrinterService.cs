using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IPrinterService
{
    Task<List<PrinterInfo>> GetPrintersAsync(string host, string username, string password, CancellationToken ct = default);
    Task<bool> AddPrinterAsync(string host, string username, string password, string connectionName, int? sessionId = null, CancellationToken ct = default);
    Task<bool> RemovePrinterAsync(string host, string username, string password, string printerName, CancellationToken ct = default);
    Task<bool> SetPrinterSharedAsync(string host, string username, string password, string printerName, bool shared, CancellationToken ct = default);
    Task<bool> ClearDefaultPrinterAsync(string host, string username, string password, int? sessionId = null, CancellationToken ct = default);
    Task<bool> SetDefaultPrinterAsync(string host, string username, string password, string printerName, int? sessionId = null, CancellationToken ct = default);
}
