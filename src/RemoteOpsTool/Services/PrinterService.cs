using System.Management;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class PrinterService : IPrinterService
{
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _log;

    public PrinterService(IPsExecService psExec, IRemoteExecutionService execution, ILogService log)
    {
        _psExec = psExec;
        _execution = execution;
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
        var result = await _execution.ExecuteOnceAsync(
            host, username, password, cmd, RemoteOperationKind.Inventory, ct: ct);
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
        int? sessionId = null, CancellationToken ct = default)
    {
        _log.Debug($"添加打印机: host={host} printer={connectionName} method=WMI/DCOM user={username}");
        var wmiAdded = await TryAddPrinterConnectionViaWmiAsync(host, username, password, connectionName, ct);
        if (wmiAdded)
        {
            _log.Info($"已通过 WMI/DCOM 添加打印机: {connectionName}");
            return true;
        }

        var command = $"rundll32 printui.dll,PrintUIEntry /in /n \"{connectionName}\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteInteractiveLocalAsync(
                command, username, password, ct, CommandShell.Direct, sessionId)
            : await _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.InteractiveLaunch,
                CommandShell.Direct, wrapCmd: false, interactiveSession: true,
                ct: ct);
        if (result.Success)
            _log.Info($"已通过 PsExec 添加打印机: {connectionName}");
        else
            _log.Warn($"打印机添加失败: {connectionName} - {result.StdErr}");
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
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteLocalElevatedAsync(
                host, username, password, command, ct, CommandShell.Direct)
            : await _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.Command,
                CommandShell.Direct, wrapCmd: false, ct: ct);
        if (result.Success)
            _log.Info($"已通过 PsExec 删除打印机: {printerName}");
        else
            _log.Warn($"打印机删除失败: {printerName} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> SetPrinterSharedAsync(string host, string username, string password, string printerName, bool shared,
        CancellationToken ct = default)
    {
        var action = shared ? "共享" : "取消共享";
        _log.Debug($"{action}打印机: host={host} printer={printerName} method=WMI/DCOM user={username}");
        var wmiShared = await TrySetPrinterSharedViaWmiAsync(host, username, password, printerName, shared, ct);
        if (wmiShared)
        {
            _log.Info($"已通过 WMI/DCOM {action}打印机: {printerName}");
            return true;
        }

        var escapedPrinterName = EscapePowerShellSingleQuoted(printerName);
        var sharedLiteral = shared ? "$true" : "$false";
        var shareNameArg = shared ? $" -ShareName '{escapedPrinterName}'" : string.Empty;
        var command = $"powershell \"Set-Printer -Name '{escapedPrinterName}' -Shared {sharedLiteral}{shareNameArg}\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteLocalElevatedAsync(
                host, username, password, command, ct, CommandShell.Direct)
            : await _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.Command, ct: ct);
        if (result.Success)
            _log.Info($"已通过 PsExec {action}打印机: {printerName}");
        else
            _log.Warn($"打印机{action}失败: {printerName} - {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> ClearDefaultPrinterAsync(string host, string username, string password,
        int? sessionId = null, CancellationToken ct = default)
    {
        _log.Debug($"释放默认打印机: host={host} method=SetDefaultPrinter user={username}");
        var command = "powershell \"Add-Type -Name NativePrinter -Namespace RemoteOps -MemberDefinition '[DllImport(\\\"winspool.drv\\\", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool SetDefaultPrinter(string name);'; if (-not [RemoteOps.NativePrinter]::SetDefaultPrinter('')) { exit 1 }\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteInteractiveLocalAsync(
                command, username, password, ct, CommandShell.Direct, sessionId)
            : await _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.InteractiveLaunch,
                CommandShell.Direct, wrapCmd: false, interactiveSession: true,
                ct: ct);
        if (result.Success)
            _log.Info("已请求 Windows 重新选择默认打印机。");
        else
            _log.Warn($"释放默认打印机失败: {result.StdErr}");
        return result.Success;
    }

    public async Task<bool> SetDefaultPrinterAsync(string host, string username, string password, string printerName,
        int? sessionId = null, CancellationToken ct = default)
    {
        _log.Debug($"设置默认打印机: host={host} printer={printerName} method=WMI/DCOM user={username}");
        var wmiDefault = await TrySetDefaultPrinterViaWmiAsync(host, username, password, printerName, ct);
        if (wmiDefault)
        {
            _log.Info($"已通过 WMI/DCOM 设置默认打印机: {printerName}");
            return true;
        }

        var command = $"rundll32 printui.dll,PrintUIEntry /y /n \"{printerName}\"";
        var result = HostHelper.IsLocalHost(host)
            ? await _psExec.ExecuteInteractiveLocalAsync(
                command, username, password, ct, CommandShell.Direct, sessionId)
            : await _execution.ExecuteOnceAsync(
                host, username, password, command, RemoteOperationKind.InteractiveLaunch,
                CommandShell.Direct, wrapCmd: false, interactiveSession: true,
                ct: ct);
        if (result.Success)
            _log.Info($"已通过 PsExec 设置默认打印机: {printerName}");
        else
            _log.Warn($"默认打印机设置失败: {printerName} - {result.StdErr}");
        return result.Success;
    }

    private async Task<List<PrinterInfo>> TryGetPrintersViaWmiAsync(
        string host,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            var printers = new List<PrinterInfo>();
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();

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

                return printers;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 打印机列表查询失败: {host} - {ex.Message}");
            return [];
        }
    }

    private async Task<bool> TryAddPrinterConnectionViaWmiAsync(
        string host,
        string username,
        string password,
        string connectionName,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();

                using var printerClass = new ManagementClass(scope, new ManagementPath("Win32_Printer"), null);
                var result = printerClass.InvokeMethod("AddPrinterConnection", new object[] { connectionName });
                return result is null || Convert.ToUInt32(result) == 0;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 添加打印机失败: {host} printer={connectionName} - {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryRemovePrinterViaWmiAsync(
        string host,
        string username,
        string password,
        string printerName,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();

                var escapedName = RemoteWmiHelper.EscapeWqlString(printerName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name FROM Win32_Printer WHERE Name='{escapedName}'"));
                using var results = searcher.Get();
                using var printer = results.OfType<ManagementObject>().FirstOrDefault();
                if (printer == null) return false;

                printer.Delete();
                return true;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 删除打印机失败: {host} printer={printerName} - {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySetPrinterSharedViaWmiAsync(
        string host,
        string username,
        string password,
        string printerName,
        bool shared,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();

                var escapedName = RemoteWmiHelper.EscapeWqlString(printerName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name,ShareName FROM Win32_Printer WHERE Name='{escapedName}'"));
                using var results = searcher.Get();
                using var printer = results.OfType<ManagementObject>().FirstOrDefault();
                if (printer == null) return false;

                if (shared)
                    printer["ShareName"] = printerName;
                printer["Shared"] = shared;
                printer.Put();
                return true;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 设置打印机共享状态失败: {host} printer={printerName} shared={shared} - {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySetDefaultPrinterViaWmiAsync(
        string host,
        string username,
        string password,
        string printerName,
        CancellationToken ct)
    {
        try
        {
            return await RemoteWmiHelper.ExecuteAsync(host, username, password, scope =>
            {
                ct.ThrowIfCancellationRequested();

                var escapedName = RemoteWmiHelper.EscapeWqlString(printerName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name FROM Win32_Printer WHERE Name='{escapedName}'"));
                using var results = searcher.Get();
                using var printer = results.OfType<ManagementObject>().FirstOrDefault();
                if (printer == null) return false;

                var result = printer.InvokeMethod("SetDefaultPrinter", null, null);
                return RemoteWmiHelper.IsSuccessReturn(result);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 设置默认打印机失败: {host} printer={printerName} - {ex.Message}");
            return false;
        }
    }

    private static string EscapePowerShellSingleQuoted(string value) => value.Replace("'", "''");
}
