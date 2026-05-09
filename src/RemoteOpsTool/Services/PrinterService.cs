using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class PrinterService : IPrinterService
{
    private readonly IPsExecService _psExec;
    private readonly ILogService _log;

    public PrinterService(IPsExecService psExec, ILogService log)
    {
        _psExec = psExec;
        _log = log;
    }

    public async Task<List<PrinterInfo>> GetPrintersAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        var cmd = "powershell \"Get-CimInstance Win32_Printer | Select-Object Name,DriverName,PortName,Shared,Default | ConvertTo-Json\"";
        var result = await _psExec.ExecuteAsync(host, username, password, cmd, ct: ct);
        if (!result.Success) return [];

        var printers = new List<PrinterInfo>();
        try
        {
            var json = result.StdOut;
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start < 0 || end < 0) return printers;
            json = json[start..(end + 1)];
            var items = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(json);
            if (items == null) return printers;
            foreach (var item in items)
            {
                printers.Add(new PrinterInfo
                {
                    Name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    DriverName = item.TryGetProperty("DriverName", out var d) ? d.GetString() ?? "" : "",
                    PortName = item.TryGetProperty("PortName", out var p) ? p.GetString() ?? "" : "",
                    Shared = item.TryGetProperty("Shared", out var s) && s.GetBoolean(),
                    Default = item.TryGetProperty("Default", out var def) && def.GetBoolean()
                });
            }
        }
        catch { return printers; }
        return printers;
    }

    public async Task<bool> AddPrinterAsync(string host, string username, string password, string connectionName,
        int sessionId, CancellationToken ct = default)
    {
        var command = $"rundll32 printui.dll,PrintUIEntry /in /n \"{connectionName}\"";
        var result = await _psExec.ExecuteAsync(host, username, password, command,
            interactiveSession: true, sessionId: sessionId, ct: ct, wrapCmd: false);
        return result.Success;
    }

    public async Task<bool> RemovePrinterAsync(string host, string username, string password, string printerName,
        CancellationToken ct = default)
    {
        var command = $"rundll32 printui.dll,PrintUIEntry /dl /n \"{printerName}\"";
        var result = await _psExec.ExecuteAsync(host, username, password, command, ct: ct, wrapCmd: false);
        return result.Success;
    }

    public async Task<bool> SetDefaultPrinterAsync(string host, string username, string password, string printerName,
        int sessionId, CancellationToken ct = default)
    {
        var command = $"rundll32 printui.dll,PrintUIEntry /y /n \"{printerName}\"";
        var result = await _psExec.ExecuteAsync(host, username, password, command,
            interactiveSession: true, sessionId: sessionId, ct: ct, wrapCmd: false);
        return result.Success;
    }
}
