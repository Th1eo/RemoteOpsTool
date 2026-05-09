using System.Management;
using RemoteOpsTool.Helpers;
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
        _log.Debug($"获取打印机列表: host={host} method=WMI/DCOM user={username}");
        var wmiPrinters = await TryGetPrintersViaWmiAsync(host, username, password, ct);
        if (wmiPrinters.Count > 0)
        {
            _log.Debug($"WMI/DCOM 打印机列表完成: host={host} count={wmiPrinters.Count}");
            return wmiPrinters;
        }

        _log.Warn($"WMI/DCOM 打印机查询不可用，回退到 PsExec: {host}");

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
        _log.Debug($"添加打印机: host={host} printer={connectionName} method=WMI/DCOM user={username}");
        var wmiAdded = await TryAddPrinterConnectionViaWmiAsync(host, username, password, connectionName, ct);
        if (wmiAdded)
        {
            _log.Info($"已通过 WMI/DCOM 添加打印机: {connectionName}");
            return true;
        }

        var command = $"rundll32 printui.dll,PrintUIEntry /in /n \"{connectionName}\"";
        var result = await _psExec.ExecuteAsync(host, username, password, command,
            interactiveSession: true, sessionId: sessionId, ct: ct, wrapCmd: false);
        return result.Success;
    }

    public async Task<bool> RemovePrinterAsync(string host, string username, string password, string printerName,
        CancellationToken ct = default)
    {
        _log.Debug($"删除打印机: host={host} printer={printerName} method=WMI/DCOM user={username}");
        var wmiRemoved = await TryRemovePrinterViaWmiAsync(host, username, password, printerName, ct);
        if (wmiRemoved)
        {
            _log.Info($"已通过 WMI/DCOM 删除打印机: {printerName}");
            return true;
        }

        var command = $"rundll32 printui.dll,PrintUIEntry /dl /n \"{printerName}\"";
        var result = await _psExec.ExecuteAsync(host, username, password, command, ct: ct, wrapCmd: false);
        return result.Success;
    }

    public async Task<bool> SetDefaultPrinterAsync(string host, string username, string password, string printerName,
        int sessionId, CancellationToken ct = default)
    {
        _log.Debug($"设置默认打印机: host={host} printer={printerName} method=WMI/DCOM user={username}");
        var wmiDefault = await TrySetDefaultPrinterViaWmiAsync(host, username, password, printerName, ct);
        if (wmiDefault)
        {
            _log.Info($"已通过 WMI/DCOM 设置默认打印机: {printerName}");
            return true;
        }

        var command = $"rundll32 printui.dll,PrintUIEntry /y /n \"{printerName}\"";
        var result = await _psExec.ExecuteAsync(host, username, password, command,
            interactiveSession: true, sessionId: sessionId, ct: ct, wrapCmd: false);
        return result.Success;
    }

    private static async Task<List<PrinterInfo>> TryGetPrintersViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var printers = new List<PrinterInfo>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Name,DriverName,PortName,Shared,Default FROM Win32_Printer"));
                foreach (ManagementObject printer in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    printers.Add(new PrinterInfo
                    {
                        Name = RemoteWmiHelper.GetString(printer, "Name"),
                        DriverName = RemoteWmiHelper.GetString(printer, "DriverName"),
                        PortName = RemoteWmiHelper.GetString(printer, "PortName"),
                        Shared = RemoteWmiHelper.GetBool(printer, "Shared"),
                        Default = RemoteWmiHelper.GetBool(printer, "Default")
                    });
                }
            }
            catch
            {
                return [];
            }
            return printers;
        }, ct);
    }

    private static async Task<bool> TryAddPrinterConnectionViaWmiAsync(
        string host,
        string username,
        string password,
        string connectionName,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                using var printerClass = new ManagementClass(scope, new ManagementPath("Win32_Printer"), null);
                var result = printerClass.InvokeMethod("AddPrinterConnection", new object[] { connectionName });
                return result is null || Convert.ToUInt32(result) == 0;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    private static async Task<bool> TryRemovePrinterViaWmiAsync(
        string host,
        string username,
        string password,
        string printerName,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                var escapedName = RemoteWmiHelper.EscapeWqlString(printerName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_Printer WHERE Name='{escapedName}'"));
                var printer = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (printer == null) return false;

                printer.Delete();
                return true;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    private static async Task<bool> TrySetDefaultPrinterViaWmiAsync(
        string host,
        string username,
        string password,
        string printerName,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var scope = RemoteWmiHelper.CreateScope(host, username, password);
                scope.Connect();

                var escapedName = RemoteWmiHelper.EscapeWqlString(printerName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_Printer WHERE Name='{escapedName}'"));
                var printer = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (printer == null) return false;

                var result = printer.InvokeMethod("SetDefaultPrinter", null, null);
                return RemoteWmiHelper.IsSuccessReturn(result);
            }
            catch
            {
                return false;
            }
        }, ct);
    }
}
